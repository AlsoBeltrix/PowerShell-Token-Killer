using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Ingest;
using PtkSiemReceiver.Security;

namespace PtkSiemReceiver.Storage;

internal sealed record CustodyHealthSnapshot(
    bool Healthy,
    string FailureCode,
    string CheckedUtc,
    long CustodySequence,
    string? CustodyHash,
    long WitnessSequence,
    string? WitnessHash,
    bool RestorePending,
    bool AnchorConfigured);

internal sealed class CustodyHealthState
{
    private readonly object _sync = new();
    private CustodyHealthSnapshot _snapshot = new(
        true, "disabled", DateTimeOffset.MinValue.ToString("O"),
        0, null, 0, null, false, false);

    internal CustodyHealthSnapshot Snapshot
    {
        get
        {
            lock (_sync) return _snapshot;
        }
    }

    internal bool CanMutate => Snapshot.Healthy && !Snapshot.RestorePending;

    internal void Set(CustodyHealthSnapshot snapshot)
    {
        lock (_sync) _snapshot = snapshot;
    }
}

internal sealed record CustodyWitnessRecord(
    int Version,
    long WitnessSequence,
    string? PreviousWitnessHash,
    string RecordHash,
    string Kind,
    string ObservedUtc,
    long CustodySequence,
    string? CustodyHash,
    long? PriorCustodySequence,
    string? PriorCustodyHash,
    string? OperatorActor,
    string? OperatorEndpoint);

internal sealed record PendingCustodyRestore(
    string DetectedUtc,
    long PriorCustodySequence,
    string? PriorCustodyHash,
    long RestoredCustodySequence,
    string? RestoredCustodyHash);

/// <summary>
/// Independent append-only custody witness. Each record is one immutable,
/// owner-only file outside the SQLite data root. The witness's own hash chain
/// detects internal mutation; its latest custody head detects database tail
/// truncation and whole-store replacement. An optional second append-only
/// file-drop directory can live on off-box/write-once storage and pins the
/// local witness prefix. Without that anchor, the current witness hash is
/// exposed for manual out-of-band attestation.
/// </summary>
internal sealed class CustodyWitness : IDisposable
{
    private const int MaximumRecordBytes = 64 * 1024;
    private static readonly HashSet<string> RecordProperties = new(StringComparer.Ordinal)
    {
        "v", "witness_sequence", "previous_witness_hash", "record_hash",
        "kind", "observed_utc", "custody_sequence", "custody_hash",
        "prior_custody_sequence", "prior_custody_hash",
        "operator_actor", "operator_endpoint",
    };

    private readonly CustodyWitnessOptions _options;
    private readonly SqliteIngestStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ProtectedDirectoryLease _witnessLease;
    private readonly ProtectedDirectoryLease? _anchorLease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<CustodyWitnessRecord> _records;
    private PendingCustodyRestore? _pendingRestore;
    private bool _disposed;

    private CustodyWitness(
        CustodyWitnessOptions options,
        SqliteIngestStore store,
        TimeProvider timeProvider,
        ProtectedDirectoryLease witnessLease,
        ProtectedDirectoryLease? anchorLease,
        List<CustodyWitnessRecord> records,
        CustodyHealthState healthState)
    {
        _options = options;
        _store = store;
        _timeProvider = timeProvider;
        _witnessLease = witnessLease;
        _anchorLease = anchorLease;
        _records = records;
        HealthState = healthState;
    }

    internal CustodyHealthState HealthState { get; }

    internal static CustodyWitness Open(
        CustodyWitnessOptions options,
        SqliteIngestStore store,
        TimeProvider timeProvider,
        ProtectedPathTestHooks? protectedPathTestHooks = null)
    {
        ProtectedDirectoryLease? witnessLease = null;
        ProtectedDirectoryLease? anchorLease = null;
        try
        {
            witnessLease = SiemProtectedPath.RetainExternalDirectory(
                options.DirectoryPath, protectedPathTestHooks);
            if (options.AnchorDirectoryPath is not null)
            {
                anchorLease = SiemProtectedPath.RetainExternalDirectory(
                    options.AnchorDirectoryPath, protectedPathTestHooks);
            }

            var records = ReadRecords(options.DirectoryPath);
            var health = new CustodyHealthState();
            var witness = new CustodyWitness(
                options, store, timeProvider, witnessLease, anchorLease, records, health);
            witness.InitializeAsync().GetAwaiter().GetResult();
            return witness;
        }
        catch (SiemReceiverStartupException)
        {
            anchorLease?.Dispose();
            witnessLease?.Dispose();
            throw;
        }
        catch (ProtectedPathException exception)
        {
            anchorLease?.Dispose();
            witnessLease?.Dispose();
            throw new SiemReceiverStartupException("custody_witness_protection", exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            anchorLease?.Dispose();
            witnessLease?.Dispose();
            throw new SiemReceiverStartupException("custody_witness", exception);
        }
    }

    internal async Task<CustodyHealthSnapshot> CheckpointAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_pendingRestore is not null)
                return HealthState.Snapshot;

            VerifyWitnessStillAppendOnly();
            VerifyAndFillAnchor();
            var verification = await _store.VerifyCustodyAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!verification.Healthy)
            {
                return SetUnhealthy(
                    verification.FailureCode,
                    new CustodyHead(verification.HeadSequence, verification.HeadHash),
                    restorePending: false);
            }

            var head = new CustodyHead(verification.HeadSequence, verification.HeadHash);
            if (!await LatestWitnessMatchesStoreAsync(head, cancellationToken)
                    .ConfigureAwait(false))
            {
                SetPendingRestore(head, "custody_store_divergence");
                return HealthState.Snapshot;
            }

            if (force || !SameCustodyHead(_records[^1], head))
                AppendCheckpoint(head);
            return SetHealthy(head);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var head = await _store.ReadCustodyHeadAsync(CancellationToken.None)
                .ConfigureAwait(false);
            return SetUnhealthy("custody_witness_check", head, restorePending: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<CustodyRestoreApplyResult> AuthorizeRestoreAsync(
        IngestReceiptContext operatorReceipt,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var pending = _pendingRestore ??
                throw new InvalidOperationException("No custody restore is pending.");
            var currentHead = await _store.ReadCustodyHeadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (currentHead.Sequence != pending.RestoredCustodySequence ||
                !string.Equals(
                    currentHead.Hash,
                    pending.RestoredCustodyHash,
                    StringComparison.Ordinal))
            {
                SetPendingRestore(currentHead, "custody_restore_changed");
                throw new InvalidOperationException(
                    "The restored custody head changed before authorization.");
            }

            var restoreRecord = _records[^1] is { Kind: "restore" } existingRestore &&
                                SameCustodyHead(existingRestore, currentHead) &&
                                existingRestore.PriorCustodySequence == pending.PriorCustodySequence &&
                                string.Equals(
                                    existingRestore.PriorCustodyHash,
                                    pending.PriorCustodyHash,
                                    StringComparison.Ordinal)
                ? existingRestore
                : AppendRecord(
                    kind: "restore",
                    currentHead,
                    priorCustodySequence: pending.PriorCustodySequence,
                    priorCustodyHash: pending.PriorCustodyHash,
                    operatorActor: operatorReceipt.ClientCertificateThumbprint,
                    operatorEndpoint: operatorReceipt.RemoteEndpoint,
                    observedUtc: operatorReceipt.ReceivedUtc);
            var request = new CustodyRestoreRequest(
                restoreRecord.RecordHash,
                pending.DetectedUtc,
                pending.PriorCustodySequence,
                pending.PriorCustodyHash,
                pending.RestoredCustodySequence,
                pending.RestoredCustodyHash);
            var applied = await _store.ApplyCustodyRestoreAsync(
                    request, operatorReceipt, cancellationToken)
                .ConfigureAwait(false);
            AppendCheckpoint(applied.Head);
            _pendingRestore = null;
            SetHealthy(applied.Head);
            return applied;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InitializeAsync()
    {
        VerifyAndFillAnchor();
        var head = await _store.ReadCustodyHeadAsync(CancellationToken.None)
            .ConfigureAwait(false);
        if (_records.Count == 0)
        {
            AppendCheckpoint(head);
            SetHealthy(head);
            return;
        }

        var latest = _records[^1];
        if (latest.Kind == "restore")
        {
            var exists = await _store.HasCustodyRestoreAsync(
                    latest.RecordHash, CancellationToken.None)
                .ConfigureAwait(false);
            if (!exists && SameCustodyHead(latest, head))
            {
                var receipt = new IngestReceiptContext(
                    DateTimeOffset.ParseExact(
                        latest.ObservedUtc,
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal),
                    latest.OperatorActor!,
                    latest.OperatorEndpoint!);
                var applied = await _store.ApplyCustodyRestoreAsync(
                    new CustodyRestoreRequest(
                        latest.RecordHash,
                        latest.ObservedUtc,
                        latest.PriorCustodySequence!.Value,
                        latest.PriorCustodyHash,
                        latest.CustodySequence,
                        latest.CustodyHash),
                    receipt,
                    CancellationToken.None).ConfigureAwait(false);
                head = applied.Head;
            }
        }

        if (!await LatestWitnessMatchesStoreAsync(head, CancellationToken.None)
                .ConfigureAwait(false))
        {
            SetPendingRestore(head, "custody_store_divergence");
            return;
        }

        if (!SameCustodyHead(_records[^1], head))
            AppendCheckpoint(head);
        SetHealthy(head);
    }

    private async Task<bool> LatestWitnessMatchesStoreAsync(
        CustodyHead currentHead,
        CancellationToken cancellationToken)
    {
        var latest = _records[^1];
        if (latest.CustodySequence > currentHead.Sequence) return false;
        var hashAtWitness = await _store.ReadCustodyHashAsync(
                latest.CustodySequence, cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(
            hashAtWitness, latest.CustodyHash, StringComparison.Ordinal);
    }

    private void SetPendingRestore(CustodyHead restoredHead, string failureCode)
    {
        var prior = _records[^1];
        _pendingRestore = new PendingCustodyRestore(
            FormatUtc(_timeProvider.GetUtcNow()),
            prior.CustodySequence,
            prior.CustodyHash,
            restoredHead.Sequence,
            restoredHead.Hash);
        _ = SetUnhealthy(failureCode, restoredHead, restorePending: true);
    }

    private CustodyHealthSnapshot SetHealthy(CustodyHead head)
    {
        var latest = _records[^1];
        var snapshot = new CustodyHealthSnapshot(
            true,
            "healthy",
            FormatUtc(_timeProvider.GetUtcNow()),
            head.Sequence,
            head.Hash,
            latest.WitnessSequence,
            latest.RecordHash,
            false,
            _anchorLease is not null);
        HealthState.Set(snapshot);
        return snapshot;
    }

    private CustodyHealthSnapshot SetUnhealthy(
        string failureCode,
        CustodyHead head,
        bool restorePending)
    {
        var latest = _records.Count == 0 ? null : _records[^1];
        var snapshot = new CustodyHealthSnapshot(
            false,
            failureCode,
            FormatUtc(_timeProvider.GetUtcNow()),
            head.Sequence,
            head.Hash,
            latest?.WitnessSequence ?? 0,
            latest?.RecordHash,
            restorePending,
            _anchorLease is not null);
        HealthState.Set(snapshot);
        return snapshot;
    }

    private void AppendCheckpoint(CustodyHead head) =>
        _ = AppendRecord(
            "checkpoint",
            head,
            priorCustodySequence: null,
            priorCustodyHash: null,
            operatorActor: null,
            operatorEndpoint: null,
            observedUtc: _timeProvider.GetUtcNow());

    private CustodyWitnessRecord AppendRecord(
        string kind,
        CustodyHead head,
        long? priorCustodySequence,
        string? priorCustodyHash,
        string? operatorActor,
        string? operatorEndpoint,
        DateTimeOffset observedUtc)
    {
        var sequence = _records.Count == 0 ? 1 : _records[^1].WitnessSequence + 1;
        var previousHash = _records.Count == 0 ? null : _records[^1].RecordHash;
        var utc = FormatUtc(observedUtc);
        var hash = CustodyWitnessHash.Compute(
            sequence,
            previousHash,
            kind,
            utc,
            head.Sequence,
            head.Hash,
            priorCustodySequence,
            priorCustodyHash,
            operatorActor,
            operatorEndpoint);
        var record = new CustodyWitnessRecord(
            1, sequence, previousHash, hash, kind, utc,
            head.Sequence, head.Hash, priorCustodySequence, priorCustodyHash,
            operatorActor, operatorEndpoint);
        var bytes = Serialize(record);
        // Anchor first. A crash between the two creates an anchor-ahead state
        // that startup can copy back into the local witness. Local-first would
        // make a missing anchor tail indistinguishable from off-box deletion.
        if (_options.AnchorDirectoryPath is not null)
            WriteRecord(_options.AnchorDirectoryPath, record, bytes);
        WriteRecord(_options.DirectoryPath, record, bytes);
        _records.Add(record);
        return record;
    }

    private void VerifyWitnessStillAppendOnly()
    {
        var onDisk = ReadRecords(_options.DirectoryPath);
        if (onDisk.Count != _records.Count ||
            !onDisk.Select(record => record.RecordHash)
                .SequenceEqual(_records.Select(record => record.RecordHash), StringComparer.Ordinal))
        {
            throw new InvalidDataException("The custody witness changed after startup.");
        }
        SiemProtectedPath.VerifyRetainedDirectory(_witnessLease);
    }

    private void VerifyAndFillAnchor()
    {
        if (_options.AnchorDirectoryPath is null) return;
        var anchored = ReadRecords(_options.AnchorDirectoryPath);
        var sharedCount = Math.Min(anchored.Count, _records.Count);
        for (var index = 0; index < sharedCount; index++)
        {
            if (!string.Equals(
                    anchored[index].RecordHash,
                    _records[index].RecordHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The custody anchor diverges from the local witness.");
            }
        }
        if (anchored.Count < _records.Count)
            throw new InvalidDataException("The custody anchor regressed behind the local witness.");
        for (var index = _records.Count; index < anchored.Count; index++)
        {
            var record = anchored[index];
            WriteRecord(_options.DirectoryPath, record, Serialize(record));
            _records.Add(record);
        }
        SiemProtectedPath.VerifyRetainedDirectory(_anchorLease!);
    }

    private static List<CustodyWitnessRecord> ReadRecords(string directoryPath)
    {
        var records = new List<CustodyWitnessRecord>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            if (!TryParseFileName(name, out var sequence, out var namedHash))
                throw new InvalidDataException("The custody witness contains an unknown entry.");
            var bytes = SiemProtectedPath.ReadExternalFile(entry, MaximumRecordBytes);
            var record = Parse(bytes);
            if (record.WitnessSequence != sequence ||
                !string.Equals(record.RecordHash, namedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The custody witness filename does not match its record.");
            }
            records.Add(record);
        }
        ValidateChain(records);
        return records;
    }

    private static void ValidateChain(IReadOnlyList<CustodyWitnessRecord> records)
    {
        CustodyWitnessRecord? previous = null;
        foreach (var record in records)
        {
            if (record.Version != 1 ||
                record.WitnessSequence != (previous?.WitnessSequence ?? 0) + 1 ||
                !string.Equals(
                    record.PreviousWitnessHash,
                    previous?.RecordHash,
                    StringComparison.Ordinal) ||
                !IsLowerHex(record.RecordHash) ||
                !string.Equals(
                    record.RecordHash,
                    CustodyWitnessHash.Compute(
                        record.WitnessSequence,
                        record.PreviousWitnessHash,
                        record.Kind,
                        record.ObservedUtc,
                        record.CustodySequence,
                        record.CustodyHash,
                        record.PriorCustodySequence,
                        record.PriorCustodyHash,
                        record.OperatorActor,
                        record.OperatorEndpoint),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The custody witness chain is invalid.");
            }

            var observed = DateTimeOffset.ParseExact(
                record.ObservedUtc,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal);
            if (record.CustodySequence < 0 ||
                !string.Equals(FormatUtc(observed), record.ObservedUtc, StringComparison.Ordinal) ||
                (record.CustodySequence == 0) != (record.CustodyHash is null) ||
                (record.CustodyHash is not null && !IsLowerHex(record.CustodyHash)) ||
                (record.PriorCustodyHash is not null && !IsLowerHex(record.PriorCustodyHash)))
            {
                throw new InvalidDataException("The custody witness head is invalid.");
            }

            if (record.Kind == "checkpoint")
            {
                if (record.PriorCustodySequence is not null ||
                    record.PriorCustodyHash is not null ||
                    record.OperatorActor is not null ||
                    record.OperatorEndpoint is not null ||
                    (previous is not null &&
                     (record.CustodySequence < previous.CustodySequence ||
                      (record.CustodySequence == previous.CustodySequence &&
                       !string.Equals(record.CustodyHash, previous.CustodyHash, StringComparison.Ordinal)))))
                {
                    throw new InvalidDataException("The custody checkpoint regressed.");
                }
            }
            else if (record.Kind == "restore")
            {
                if (previous is null ||
                    record.PriorCustodySequence != previous.CustodySequence ||
                    !string.Equals(record.PriorCustodyHash, previous.CustodyHash, StringComparison.Ordinal) ||
                    !IsLowerHex(record.OperatorActor) ||
                    string.IsNullOrWhiteSpace(record.OperatorEndpoint) ||
                    (record.CustodySequence == previous.CustodySequence &&
                     string.Equals(record.CustodyHash, previous.CustodyHash, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException("The custody restore record is invalid.");
                }
            }
            else
            {
                throw new InvalidDataException("The custody witness kind is invalid.");
            }
            previous = record;
        }
    }

    private static byte[] Serialize(CustodyWitnessRecord record) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = record.Version,
            witness_sequence = record.WitnessSequence,
            previous_witness_hash = record.PreviousWitnessHash,
            record_hash = record.RecordHash,
            kind = record.Kind,
            observed_utc = record.ObservedUtc,
            custody_sequence = record.CustodySequence,
            custody_hash = record.CustodyHash,
            prior_custody_sequence = record.PriorCustodySequence,
            prior_custody_hash = record.PriorCustodyHash,
            operator_actor = record.OperatorActor,
            operator_endpoint = record.OperatorEndpoint,
        });

    private static CustodyWitnessRecord Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var properties = root.EnumerateObject().ToArray();
        if (root.ValueKind != JsonValueKind.Object ||
            properties.Length != RecordProperties.Count ||
            !properties.Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal).SetEquals(RecordProperties))
        {
            throw new InvalidDataException("The custody witness record shape is invalid.");
        }
        return new CustodyWitnessRecord(
            root.GetProperty("v").GetInt32(),
            root.GetProperty("witness_sequence").GetInt64(),
            NullableString(root, "previous_witness_hash"),
            RequiredString(root, "record_hash"),
            RequiredString(root, "kind"),
            RequiredString(root, "observed_utc"),
            root.GetProperty("custody_sequence").GetInt64(),
            NullableString(root, "custody_hash"),
            NullableInt64(root, "prior_custody_sequence"),
            NullableString(root, "prior_custody_hash"),
            NullableString(root, "operator_actor"),
            NullableString(root, "operator_endpoint"));
    }

    private static void WriteRecord(
        string directoryPath,
        CustodyWitnessRecord record,
        byte[] bytes)
    {
        var path = Path.Combine(directoryPath, FileName(record));
        if (File.Exists(path))
        {
            var existing = SiemProtectedPath.ReadExternalFile(path, MaximumRecordBytes);
            if (!CryptographicOperations.FixedTimeEquals(existing, bytes))
                throw new InvalidDataException("A custody witness record already exists with different bytes.");
            return;
        }
        _ = SiemProtectedPath.WriteNewProtectedFile(path, bytes);
    }

    private static string FileName(CustodyWitnessRecord record) =>
        $"{record.WitnessSequence:D20}-{record.RecordHash}.json";

    private static bool TryParseFileName(
        string name,
        out long sequence,
        out string hash)
    {
        sequence = 0;
        hash = string.Empty;
        if (name.Length != 20 + 1 + 64 + 5 ||
            name[20] != '-' ||
            !name.EndsWith(".json", StringComparison.Ordinal) ||
            !long.TryParse(name.AsSpan(0, 20), NumberStyles.None, CultureInfo.InvariantCulture, out sequence))
        {
            return false;
        }
        hash = name.Substring(21, 64);
        return IsLowerHex(hash);
    }

    private static string RequiredString(JsonElement root, string property) =>
        root.GetProperty(property).GetString() ?? throw new InvalidDataException();

    private static string? NullableString(JsonElement root, string property)
    {
        var value = root.GetProperty(property);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static long? NullableInt64(JsonElement root, string property)
    {
        var value = root.GetProperty(property);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
    }

    private static bool SameCustodyHead(CustodyWitnessRecord record, CustodyHead head) =>
        record.CustodySequence == head.Sequence &&
        string.Equals(record.CustodyHash, head.Hash, StringComparison.Ordinal);

    private static bool IsLowerHex(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _anchorLease?.Dispose();
        _witnessLease.Dispose();
        _gate.Dispose();
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

internal sealed class CustodyWitnessService(
    CustodyWitness witness,
    SiemReceiverOptions options,
    ILogger<CustodyWitnessService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(
            options.CustodyWitness!.CheckpointIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken).ConfigureAwait(false);
                var health = await witness.CheckpointAsync(
                    force: true, stoppingToken).ConfigureAwait(false);
                if (!health.Healthy)
                {
                    logger.LogError(
                        "Custody health check failed: {FailureCode}; ingest is paused.",
                        health.FailureCode);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                logger.LogError(exception, "Custody witness checkpoint failed; ingest is paused.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await witness.CheckpointAsync(force: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            logger.LogError(exception, "The clean-shutdown custody checkpoint failed.");
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

internal static class CustodyWitnessHash
{
    private static readonly byte[] Magic = "PTK-SIEM-WITNESS1"u8.ToArray();

    internal static string Compute(
        long witnessSequence,
        string? previousWitnessHash,
        string kind,
        string observedUtc,
        long custodySequence,
        string? custodyHash,
        long? priorCustodySequence,
        string? priorCustodyHash,
        string? operatorActor,
        string? operatorEndpoint)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Magic);
        AppendInt64(hash, witnessSequence);
        AppendText(hash, previousWitnessHash);
        AppendText(hash, kind);
        AppendText(hash, observedUtc);
        AppendInt64(hash, custodySequence);
        AppendText(hash, custodyHash);
        AppendNullableInt64(hash, priorCustodySequence);
        AppendText(hash, priorCustodyHash);
        AppendText(hash, operatorActor);
        AppendText(hash, operatorEndpoint);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendNullableInt64(IncrementalHash hash, long? value)
    {
        hash.AppendData([value.HasValue ? (byte)1 : (byte)0]);
        if (value.HasValue) AppendInt64(hash, value.Value);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendText(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            hash.AppendData([0]);
            return;
        }
        hash.AppendData([1]);
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
