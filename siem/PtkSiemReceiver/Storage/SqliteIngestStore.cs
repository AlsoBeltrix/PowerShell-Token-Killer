using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PtkSiemReceiver.Ingest;
using PtkSiemReceiver.Security;

namespace PtkSiemReceiver.Storage;

internal enum SqliteIngestWriteKind
{
    Event,
    Quarantine,
}

internal interface ISqliteIngestFaultInjector
{
    void BeforeCommit(SqliteIngestWriteKind writeKind);

    void AfterStartupProtectionForTests(string databasePath)
    {
    }

    void AfterConnectionOpenForTests(string databasePath)
    {
    }

    void BeforeConnectionOpenForTests(string databasePath)
    {
    }
}

internal sealed record SqliteWriterPolicy(string JournalMode, int Synchronous);

internal enum GapDispositionOutcome
{
    NotFound,
    IllegalState,
    Dispositioned,
    Resumed,
}

internal enum AlertTransitionOutcome
{
    NotFound,
    IllegalTransition,
    Ok,
}

internal sealed record CreatedAlert(
    long AlertId,
    string RuleName,
    string SubjectKind,
    string SubjectId,
    string CreatedUtc,
    string Detail);

internal sealed class SqliteIngestStore : IIngestCommitter, IDisposable
{
    private const int CurrentSchemaVersion = 3;
    private const int BusyTimeoutSeconds = 5;
    private readonly SqliteConnection _writer;
    private readonly ProtectedDirectoryLease _parentLease;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly ISqliteIngestFaultInjector? _faultInjector;
    private readonly string? _alertRuleConfigHash;
    private int _disposed;

    private SqliteIngestStore(
        SqliteConnection writer,
        SqliteWriterPolicy writerPolicy,
        ISqliteIngestFaultInjector? faultInjector,
        ProtectedDirectoryLease parentLease,
        string? alertRuleConfigHash)
    {
        _writer = writer;
        _parentLease = parentLease;
        WriterPolicy = writerPolicy;
        _faultInjector = faultInjector;
        _alertRuleConfigHash = alertRuleConfigHash;
    }

    internal SqliteWriterPolicy WriterPolicy { get; }

    internal static SqliteIngestStore Open(
        string databasePath,
        ISqliteIngestFaultInjector? faultInjector = null,
        ProtectedPathTestHooks? protectedPathTestHooks = null,
        IReadOnlySet<ProtectedPathIdentity>? protectedExternalIdentities = null,
        string? alertRuleConfigHash = null)
    {
        SqliteConnection? connection = null;
        ProtectedDirectoryLease? parentLease = null;
        try
        {
            var fullPath = SiemProtectedPath.NormalizeAbsolute(databasePath);
            var parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent))
                throw new SiemReceiverStartupException("storage_parent");
            parentLease = SiemProtectedPath.RetainExternalDirectory(
                parent,
                protectedPathTestHooks);

            var walPath = fullPath + "-wal";
            var sharedMemoryPath = fullPath + "-shm";
            var databaseIdentity = SiemProtectedPath.InspectSqliteFileOrMissing(
                fullPath,
                protectedPathTestHooks);
            var walIdentity = SiemProtectedPath.InspectSqliteFileOrMissing(
                walPath,
                protectedPathTestHooks);
            var sharedMemoryIdentity =
                SiemProtectedPath.InspectSqliteFileOrMissing(
                    sharedMemoryPath,
                    protectedPathTestHooks);
            if (protectedExternalIdentities is not null &&
                new[] { databaseIdentity, walIdentity, sharedMemoryIdentity }
                    .Any(identity => identity is { } existing &&
                                     protectedExternalIdentities.Contains(existing)))
            {
                throw new SiemReceiverStartupException("protected_path_collision");
            }
            if (databaseIdentity is null &&
                (walIdentity is not null || sharedMemoryIdentity is not null))
            {
                throw new SiemReceiverStartupException("storage_orphan_sidecar");
            }

            databaseIdentity ??= SiemProtectedPath.CreateProtectedFile(fullPath);
            walIdentity ??= SiemProtectedPath.CreateProtectedFile(walPath);
            sharedMemoryIdentity ??=
                SiemProtectedPath.CreateProtectedFile(sharedMemoryPath);

            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = BusyTimeoutSeconds,
            }.ToString());
            faultInjector?.BeforeConnectionOpenForTests(fullPath);
            connection.Open();

            faultInjector?.AfterConnectionOpenForTests(fullPath);
            SiemProtectedPath.VerifySqliteFileIsOpen(databaseIdentity.Value);
            _ = SiemProtectedPath.VerifySqliteFile(
                fullPath,
                databaseIdentity.Value,
                protectedPathTestHooks);
            var policy = ConfigureAndAssertWriterPolicy(connection);
            MaterializeWalSidecars(connection);
            _ = SiemProtectedPath.VerifySqliteFile(
                walPath,
                walIdentity.Value,
                protectedPathTestHooks);
            _ = SiemProtectedPath.VerifySqliteFile(
                sharedMemoryPath,
                sharedMemoryIdentity.Value,
                protectedPathTestHooks);
            SiemProtectedPath.VerifySqliteFileIsOpen(databaseIdentity.Value);
            SiemProtectedPath.VerifySqliteFileIsOpen(walIdentity.Value);
            SiemProtectedPath.VerifySqliteFileIsOpen(sharedMemoryIdentity.Value);
            ApplyMigrations(connection);
            faultInjector?.AfterStartupProtectionForTests(fullPath);

            SiemProtectedPath.VerifyRetainedDirectory(parentLease, protectedPathTestHooks);
            _ = SiemProtectedPath.VerifySqliteFile(
                fullPath,
                databaseIdentity.Value,
                protectedPathTestHooks);
            _ = SiemProtectedPath.VerifySqliteFile(
                walPath,
                walIdentity.Value,
                protectedPathTestHooks);
            _ = SiemProtectedPath.VerifySqliteFile(
                sharedMemoryPath,
                sharedMemoryIdentity.Value,
                protectedPathTestHooks);
            SiemProtectedPath.VerifySqliteFileIsOpen(walIdentity.Value);
            SiemProtectedPath.VerifySqliteFileIsOpen(sharedMemoryIdentity.Value);
            var store = new SqliteIngestStore(
                connection,
                policy,
                faultInjector,
                parentLease,
                alertRuleConfigHash);
            connection = null;
            parentLease = null;
            return store;
        }
        catch (SiemReceiverStartupException)
        {
            connection?.Dispose();
            parentLease?.Dispose();
            throw;
        }
        catch (ProtectedPathException exception)
        {
            connection?.Dispose();
            parentLease?.Dispose();
            throw new SiemReceiverStartupException(
                exception.FailureKind == ProtectedPathFailureKind.InvalidPath
                    ? "storage_path"
                    : "storage_protection",
                exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            connection?.Dispose();
            parentLease?.Dispose();
            throw new SiemReceiverStartupException("storage");
        }
    }

    public async Task<IngestCommitResult> CommitAsync(
        ValidatedOtlpRecord record,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateReceipt(receipt);
        ThrowIfDisposed();

        await _writerGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            using var transaction = _writer.BeginTransaction(deferred: false);

            var duplicate = ReadEvent(record.EventId, transaction);
            if (duplicate is not null)
            {
                // A record's identity is its exact JSONL body (which embeds
                // the event hash) — never the transport envelope: the same
                // honest event replayed through the other wire encoding, or
                // regrouped into a different batch, carries different
                // request/envelope bytes but the identical body, and must be
                // idempotent, not quarantined (cr4-5). A same-id DIFFERENT
                // body remains the forgery signal.
                if (string.Equals(duplicate.EventHash, record.EventHash, StringComparison.Ordinal) &&
                    duplicate.ExactJsonBody.AsSpan().SequenceEqual(record.ExactJsonBody))
                {
                    transaction.Rollback();
                    return IngestCommitResult.Accepted();
                }

                var chain = ReadChain(record.SupervisorBootId, transaction);
                var mismatchAttemptId = AppendQuarantine(
                    RejectedFrom(record, "duplicate_mismatch"),
                    receipt,
                    chain,
                    transaction);
                EnqueueAlertWork(
                    "quarantine",
                    mismatchAttemptId.ToString(CultureInfo.InvariantCulture),
                    receipt,
                    transaction);
                cancellationToken.ThrowIfCancellationRequested();
                _faultInjector?.BeforeCommit(SqliteIngestWriteKind.Quarantine);
                transaction.Commit();
                return IngestCommitResult.Permanent("duplicate_mismatch");
            }

            var currentHead = ReadChain(record.SupervisorBootId, transaction);
            var chainFailure = ValidateChainPosition(record, currentHead);
            if (chainFailure is not null)
            {
                // The S6 post-gap carve-out: while a gap for this boot awaits
                // or has an operator disposition, an otherwise-valid record
                // beyond the gap is committed flagged post-gap — evidence
                // keeps arriving, the frozen chain head is never silently
                // re-anchored, and only an operator disposition resumes.
                var activeGap = chainFailure == "chain_gap"
                    ? ReadActiveGap(record.SupervisorBootId, transaction)
                    : null;
                if (activeGap is not null)
                {
                    var postGapHead = ReadPostGapHead(
                        record.SupervisorBootId,
                        currentHead?.Sequence ?? 0,
                        transaction);
                    var postGapFailure = ValidatePostGapPosition(record, postGapHead);
                    if (postGapFailure is null)
                    {
                        InsertEvent(record, receipt, transaction, postGap: true);
                        AppendCustody(
                            record.RawRequestBytes,
                            receipt,
                            "accepted:post-gap",
                            "event",
                            FormatGuid(record.EventId),
                            transaction);
                        EnqueueAlertWork(
                            "event", FormatGuid(record.EventId), receipt, transaction);
                        if (activeGap.State == "dispositioned")
                        {
                            // The operator already authorized resumption; the
                            // first record beyond the gap anchors AND resumes.
                            ResumeGap(activeGap, record, receipt, transaction);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        _faultInjector?.BeforeCommit(SqliteIngestWriteKind.Event);
                        transaction.Commit();
                        return IngestCommitResult.Accepted();
                    }

                    // A record that does not even chain onto the post-gap
                    // sub-chain is an ordinary rejection; one active gap per
                    // boot, never a second while the first is undecided.
                    chainFailure = postGapFailure;
                }

                var attemptId = AppendQuarantine(
                    RejectedFrom(record, chainFailure),
                    receipt,
                    currentHead,
                    transaction);
                EnqueueAlertWork(
                    "quarantine",
                    attemptId.ToString(CultureInfo.InvariantCulture),
                    receipt,
                    transaction);
                if (chainFailure == "chain_gap" &&
                    ReadActiveGap(record.SupervisorBootId, transaction) is null)
                {
                    var gapId = OpenGap(record, currentHead, receipt, transaction);
                    EnqueueAlertWork(
                        "gap",
                        gapId.ToString(CultureInfo.InvariantCulture),
                        receipt,
                        transaction);
                }

                cancellationToken.ThrowIfCancellationRequested();
                _faultInjector?.BeforeCommit(SqliteIngestWriteKind.Quarantine);
                transaction.Commit();
                return IngestCommitResult.Permanent(chainFailure);
            }

            InsertEvent(record, receipt, transaction);
            AdvanceChain(record, currentHead, transaction);
            AppendCustody(
                record.RawRequestBytes,
                receipt,
                "accepted",
                "event",
                FormatGuid(record.EventId),
                transaction);
            EnqueueAlertWork("event", FormatGuid(record.EventId), receipt, transaction);

            cancellationToken.ThrowIfCancellationRequested();
            _faultInjector?.BeforeCommit(SqliteIngestWriteKind.Event);
            transaction.Commit();
            return IngestCommitResult.Accepted();
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task<IngestCommitResult> QuarantineAsync(
        RejectedOtlpAttempt attempt,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ValidateReceipt(receipt);
        ThrowIfDisposed();

        await _writerGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            using var transaction = _writer.BeginTransaction(deferred: false);
            ChainHead? currentHead = null;
            if (Guid.TryParseExact(attempt.ClaimedSupervisorBootId, "D", out var bootId))
                currentHead = ReadChain(bootId, transaction);

            var attemptId = AppendQuarantine(attempt, receipt, currentHead, transaction);
            EnqueueAlertWork(
                "quarantine",
                attemptId.ToString(CultureInfo.InvariantCulture),
                receipt,
                transaction);
            cancellationToken.ThrowIfCancellationRequested();
            _faultInjector?.BeforeCommit(SqliteIngestWriteKind.Quarantine);
            transaction.Commit();
            return IngestCommitResult.Permanent(attempt.FailureCode);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    /// <summary>
    /// Enforces the configured retention bounds (rbc-11: the options were
    /// parsed but never applied, so an unattended receiver grew without
    /// bound). Events and quarantine attempts age out; the custody ledger
    /// NEVER does — it is the append-only witness that proves what was
    /// received, and deleting it would destroy the evidence retention exists
    /// to protect. Chain heads are likewise preserved so a later record still
    /// validates against its predecessor's hash.
    /// </summary>
    internal async Task<SiemRetentionOutcome> EnforceRetentionAsync(
        int? maximumAgeDays,
        long? maximumTotalBytes,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (maximumAgeDays is null && maximumTotalBytes is null)
            return new SiemRetentionOutcome(0, 0, 0);

        long eventsRemoved = 0;
        long quarantineRemoved = 0;

        if (maximumAgeDays is int ageDays)
        {
            var cutoff = FormatUtc(utcNow.AddDays(-ageDays));
            await _writerGate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                using var transaction = _writer.BeginTransaction(deferred: false);
                eventsRemoved += DeleteAgedEvents(cutoff, transaction);
                quarantineRemoved += DeleteAgedQuarantine(cutoff, transaction);
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            finally
            {
                _writerGate.Release();
            }
        }

        if (maximumTotalBytes is long maximumBytes)
        {
            // Each pruning chunk takes and RELEASES the writer gate, so live
            // ingest interleaves with retention instead of stalling behind a
            // whole sweep (cr3-3). Compaction happens once, after pruning.
            var pruned = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                await _writerGate.WaitAsync(cancellationToken);
                long removed;
                try
                {
                    ThrowIfDisposed();
                    // Live bytes exclude already-freed pages, so an age purge
                    // that already made room does not trigger further deletes
                    // (cr3-4).
                    if (LiveDatabaseBytes() <= maximumBytes) break;
                    using var transaction = _writer.BeginTransaction(deferred: false);
                    removed = DeleteOldestEvents(RetentionSweepBatch, transaction);
                    removed += DeleteOldestQuarantine(RetentionSweepBatch, transaction);
                    if (removed == 0)
                    {
                        transaction.Rollback();
                        break;
                    }
                    transaction.Commit();
                }
                finally
                {
                    _writerGate.Release();
                }
                eventsRemoved += removed;
                pruned = true;
            }

            if (pruned)
            {
                await _writerGate.WaitAsync(cancellationToken);
                try
                {
                    ThrowIfDisposed();
                    Vacuum();
                }
                finally
                {
                    _writerGate.Release();
                }
            }
        }

        await _writerGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return new SiemRetentionOutcome(eventsRemoved, quarantineRemoved, LiveDatabaseBytes());
        }
        finally
        {
            _writerGate.Release();
        }
    }

    private const int RetentionSweepBatch = 512;

    private long DeleteAgedEvents(string cutoffUtc, SqliteTransaction transaction)
    {
        // A chain head is never deleted: the next record from that supervisor
        // boot must still validate against it.
        using var command = CreateCommand(_writer, transaction, """
            DELETE FROM events
            WHERE received_utc < $cutoff
              AND event_id NOT IN (SELECT head_event_id FROM chains)
              AND NOT (post_gap = 1 AND supervisor_boot_id IN (
                  SELECT supervisor_boot_id FROM gaps WHERE state != 'resumed'));
            """);
        command.Parameters.AddWithValue("$cutoff", cutoffUtc);
        return command.ExecuteNonQuery();
    }

    private long DeleteAgedQuarantine(string cutoffUtc, SqliteTransaction transaction)
    {
        using var command = CreateCommand(_writer, transaction, """
            DELETE FROM quarantine WHERE received_utc < $cutoff;
            """);
        command.Parameters.AddWithValue("$cutoff", cutoffUtc);
        return command.ExecuteNonQuery();
    }

    private long DeleteOldestEvents(int batch, SqliteTransaction transaction)
    {
        // An undecided gap's post-gap sub-chain is pending evidence: deleting
        // any of it would break the continuity the resume depends on.
        using var command = CreateCommand(_writer, transaction, """
            DELETE FROM events
            WHERE event_id IN (
                SELECT event_id FROM events
                WHERE event_id NOT IN (SELECT head_event_id FROM chains)
                  AND NOT (post_gap = 1 AND supervisor_boot_id IN (
                      SELECT supervisor_boot_id FROM gaps WHERE state != 'resumed'))
                ORDER BY received_utc ASC
                LIMIT $batch);
            """);
        command.Parameters.AddWithValue("$batch", batch);
        return command.ExecuteNonQuery();
    }

    private long DeleteOldestQuarantine(int batch, SqliteTransaction transaction)
    {
        using var command = CreateCommand(_writer, transaction, """
            DELETE FROM quarantine
            WHERE attempt_id IN (
                SELECT attempt_id FROM quarantine
                ORDER BY received_utc ASC
                LIMIT $batch);
            """);
        command.Parameters.AddWithValue("$batch", batch);
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Bytes the data actually occupies: total pages minus pages already on
    /// the freelist. Measuring raw page_count counts space a pending compaction
    /// will reclaim, which made a combined age+size sweep delete fresh records
    /// the age purge had already made room for (cr3-4).
    /// </summary>
    private long LiveDatabaseBytes()
    {
        var pageCount = Convert.ToInt64(
            ExecuteScalar(_writer, null, "PRAGMA page_count;") ?? 0L,
            System.Globalization.CultureInfo.InvariantCulture);
        var freeCount = Convert.ToInt64(
            ExecuteScalar(_writer, null, "PRAGMA freelist_count;") ?? 0L,
            System.Globalization.CultureInfo.InvariantCulture);
        var pageSize = Convert.ToInt64(
            ExecuteScalar(_writer, null, "PRAGMA page_size;") ?? 0L,
            System.Globalization.CultureInfo.InvariantCulture);
        return Math.Max(0, pageCount - freeCount) * pageSize;
    }

    private void Vacuum()
    {
        // Deleted pages must actually leave the file, or a size-bounded sweep
        // never converges.
        using var command = CreateCommand(_writer, null, "VACUUM;");
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _writer.Dispose();
        _parentLease.Dispose();
        _writerGate.Dispose();
    }

    private static SqliteWriterPolicy ConfigureAndAssertWriterPolicy(SqliteConnection connection)
    {
        var journalMode = Convert.ToString(
            ExecuteScalar(connection, null, "PRAGMA journal_mode=WAL;"),
            CultureInfo.InvariantCulture)?.ToLowerInvariant();
        ExecuteNonQuery(connection, null, "PRAGMA synchronous=FULL;");
        ExecuteNonQuery(connection, null, "PRAGMA foreign_keys=ON;");
        ExecuteNonQuery(
            connection,
            null,
            $"PRAGMA busy_timeout={BusyTimeoutSeconds * 1000};");
        var synchronous = Convert.ToInt32(
            ExecuteScalar(connection, null, "PRAGMA synchronous;"),
            CultureInfo.InvariantCulture);
        var foreignKeys = Convert.ToInt32(
            ExecuteScalar(connection, null, "PRAGMA foreign_keys;"),
            CultureInfo.InvariantCulture);

        if (!string.Equals(journalMode, "wal", StringComparison.Ordinal) ||
            synchronous != 2 ||
            foreignKeys != 1)
        {
            throw new SiemReceiverStartupException("storage_policy");
        }

        return new SqliteWriterPolicy(journalMode!, synchronous);
    }

    private static void MaterializeWalSidecars(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_schema LIMIT 1;";
        _ = command.ExecuteScalar();
        transaction.Rollback();
    }

    private static void ApplyMigrations(SqliteConnection connection)
    {
        var version = Convert.ToInt32(
            ExecuteScalar(connection, null, "PRAGMA user_version;"),
            CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
            throw new SiemReceiverStartupException("storage_schema_newer");

        if (version == 0)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            ExecuteNonQuery(connection, transaction, SchemaVersionOneSql);

            using (var meta = CreateCommand(connection, transaction, """
                INSERT INTO meta(key, value) VALUES
                    ('schema_version', $schema_version),
                    ('receiver_id', $receiver_id);
                """))
            {
                meta.Parameters.AddWithValue("$schema_version", "1");
                meta.Parameters.AddWithValue("$receiver_id", Guid.NewGuid().ToString("D"));
                meta.ExecuteNonQuery();
            }

            ExecuteNonQuery(connection, transaction, "PRAGMA user_version=1;");
            transaction.Commit();
            version = 1;
        }

        if (version < 2)
        {
            // Schema v2 (mini-SIEM S6, audit-restoration R5c): the
            // gap-disposition state machine. Gapped boots keep storing
            // evidence (post-gap flagged, never silently re-anchored) and
            // resumption is an operator decision, recorded.
            using var transaction = connection.BeginTransaction(deferred: false);
            ExecuteNonQuery(connection, transaction, SchemaVersionTwoSql);
            ExecuteNonQuery(
                connection,
                transaction,
                "UPDATE meta SET value = '2' WHERE key = 'schema_version';");
            ExecuteNonQuery(connection, transaction, "PRAGMA user_version=2;");
            transaction.Commit();
        }

        if (version < 3)
        {
            // Schema v3 (mini-SIEM S6, audit-restoration R5c): the alert
            // pipeline — a durable work-item queue written in the ingest
            // transaction, a persisted evaluation cursor, and
            // custody-chained alerts.
            using var transaction = connection.BeginTransaction(deferred: false);
            ExecuteNonQuery(connection, transaction, SchemaVersionThreeSql);
            ExecuteNonQuery(
                connection,
                transaction,
                "UPDATE meta SET value = '3' WHERE key = 'schema_version';");
            ExecuteNonQuery(connection, transaction, "PRAGMA user_version=3;");
            transaction.Commit();
        }

        var recordedVersion = Convert.ToString(
            ExecuteScalar(
                connection,
                null,
                "SELECT value FROM meta WHERE key = 'schema_version';"),
            CultureInfo.InvariantCulture);
        if (!string.Equals(
                recordedVersion,
                CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new SiemReceiverStartupException("storage_schema");
        }
    }

    private static EventIdentity? ReadEvent(Guid eventId, SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            transaction.Connection!,
            transaction,
            "SELECT event_hash, exact_json_body FROM events WHERE event_id = $event_id;");
        command.Parameters.AddWithValue("$event_id", FormatGuid(eventId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new EventIdentity(reader.GetString(0), reader.GetFieldValue<byte[]>(1))
            : null;
    }

    private static ChainHead? ReadChain(Guid supervisorBootId, SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            transaction.Connection!,
            transaction,
            """
            SELECT head_sequence, head_event_id, head_event_hash
            FROM chains
            WHERE supervisor_boot_id = $boot_id;
            """);
        command.Parameters.AddWithValue("$boot_id", FormatGuid(supervisorBootId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ChainHead(reader.GetInt64(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static string? ValidateChainPosition(
        ValidatedOtlpRecord record,
        ChainHead? currentHead)
    {
        if (currentHead is null)
            return record.Sequence == 1 ? null : "chain_gap";

        if (record.Sequence <= currentHead.Sequence) return "chain_position";
        if (record.Sequence > currentHead.Sequence + 1) return "chain_gap";
        return string.Equals(
            record.PreviousEventHash,
            currentHead.EventHash,
            StringComparison.Ordinal)
            ? null
            : "chain_break";
    }

    private static void InsertEvent(
        ValidatedOtlpRecord record,
        IngestReceiptContext receipt,
        SqliteTransaction transaction,
        bool postGap = false)
    {
        using var command = CreateCommand(transaction.Connection!, transaction, """
            INSERT INTO events(
                event_id, supervisor_boot_id, sequence, schema_version, event_type,
                occurred_utc, observed_utc, host_id, worker_boot_id,
                previous_event_hash, event_hash, session_name, session_generation,
                call_id, job_id, outcome_state, raw_request, exact_json_body,
                received_utc, post_gap)
            VALUES(
                $event_id, $boot_id, $sequence, $schema_version, $event_type,
                $occurred_utc, $observed_utc, $host_id, $worker_boot_id,
                $previous_event_hash, $event_hash, $session_name, $session_generation,
                $call_id, $job_id, $outcome_state, $raw_request, $exact_json_body,
                $received_utc, $post_gap);
            """);
        command.Parameters.AddWithValue("$post_gap", postGap ? 1 : 0);
        command.Parameters.AddWithValue("$event_id", FormatGuid(record.EventId));
        command.Parameters.AddWithValue("$boot_id", FormatGuid(record.SupervisorBootId));
        command.Parameters.AddWithValue("$sequence", record.Sequence);
        command.Parameters.AddWithValue("$schema_version", record.SchemaVersion);
        command.Parameters.AddWithValue("$event_type", record.EventType);
        command.Parameters.AddWithValue("$occurred_utc", FormatUtc(record.OccurredUtc));
        command.Parameters.AddWithValue("$observed_utc", FormatUtc(record.ObservedUtc));
        command.Parameters.AddWithValue("$host_id", FormatGuid(record.HostId));
        AddNullable(command, "$worker_boot_id", record.WorkerBootId is null ? null : FormatGuid(record.WorkerBootId.Value));
        AddNullable(command, "$previous_event_hash", record.PreviousEventHash);
        command.Parameters.AddWithValue("$event_hash", record.EventHash);
        AddNullable(command, "$session_name", record.SessionName);
        AddNullable(command, "$session_generation", record.SessionGeneration);
        AddNullable(command, "$call_id", record.CallId);
        AddNullable(command, "$job_id", record.JobId);
        AddNullable(command, "$outcome_state", record.OutcomeState);
        command.Parameters.AddWithValue("$raw_request", record.RawRequestBytes);
        command.Parameters.AddWithValue("$exact_json_body", record.ExactJsonBody);
        command.Parameters.AddWithValue("$received_utc", FormatUtc(receipt.ReceivedUtc));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The event insert did not affect exactly one row.");
    }

    private static void AdvanceChain(
        ValidatedOtlpRecord record,
        ChainHead? currentHead,
        SqliteTransaction transaction)
    {
        using var command = currentHead is null
            ? CreateCommand(transaction.Connection!, transaction, """
                INSERT INTO chains(
                    supervisor_boot_id, head_sequence, head_event_id, head_event_hash)
                VALUES($boot_id, $sequence, $event_id, $event_hash);
                """)
            : CreateCommand(transaction.Connection!, transaction, """
                UPDATE chains
                SET head_sequence = $sequence,
                    head_event_id = $event_id,
                    head_event_hash = $event_hash
                WHERE supervisor_boot_id = $boot_id
                  AND head_sequence = $expected_sequence
                  AND head_event_id = $expected_event_id
                  AND head_event_hash = $expected_event_hash;
                """);
        command.Parameters.AddWithValue("$boot_id", FormatGuid(record.SupervisorBootId));
        command.Parameters.AddWithValue("$sequence", record.Sequence);
        command.Parameters.AddWithValue("$event_id", FormatGuid(record.EventId));
        command.Parameters.AddWithValue("$event_hash", record.EventHash);
        if (currentHead is not null)
        {
            command.Parameters.AddWithValue("$expected_sequence", currentHead.Sequence);
            command.Parameters.AddWithValue("$expected_event_id", currentHead.EventId);
            command.Parameters.AddWithValue("$expected_event_hash", currentHead.EventHash);
        }

        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The chain head changed during the serialized ingest transaction.");
    }

    // ---- Alert pipeline (mini-SIEM S6 / R5c) ----

    /// <summary>
    /// Evaluates the next unprocessed work item against the frozen rules.
    /// The alert inserts, their custody entries, and the cursor advance
    /// commit in ONE transaction, so a committed work item yields its alert
    /// exactly once — a crash before this commit replays the item at
    /// startup, a crash after it never re-evaluates. Returns null when the
    /// queue is drained.
    /// </summary>
    internal async Task<IReadOnlyList<CreatedAlert>?> EvaluateNextAlertWorkItemAsync(
        IReadOnlyList<Configuration.AlertRule> rules,
        string evaluationConfigHash,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _writerGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            using var transaction = _writer.BeginTransaction(deferred: false);
            var cursor = Convert.ToInt64(
                ExecuteScalar(
                    transaction.Connection!,
                    transaction,
                    "SELECT value FROM meta WHERE key = 'alert_cursor';"),
                CultureInfo.InvariantCulture);

            long itemId;
            string kind;
            string subjectId;
            string enqueuedUtc;
            string enqueueHash;
            using (var command = CreateCommand(transaction.Connection!, transaction, """
                SELECT item_id, kind, subject_id, enqueued_utc, rule_config_hash
                FROM alert_queue WHERE item_id > $cursor
                ORDER BY item_id ASC LIMIT 1;
                """))
            {
                command.Parameters.AddWithValue("$cursor", cursor);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    transaction.Rollback();
                    return null;
                }

                itemId = reader.GetInt64(0);
                kind = reader.GetString(1);
                subjectId = reader.GetString(2);
                enqueuedUtc = reader.GetString(3);
                enqueueHash = reader.GetString(4);
            }

            // The receiver itself is the actor of an evaluation-created
            // alert; its custody credential identity is the all-zero
            // sentinel, never a client credential.
            var receipt = new IngestReceiptContext(
                utcNow.ToUniversalTime(), new string('0', 64), "receiver");
            var created = new List<CreatedAlert>();
            var createdUtc = FormatUtc(utcNow);
            foreach (var rule in rules)
            {
                var detail = EvaluateRule(
                    rule, kind, subjectId, enqueuedUtc, transaction);
                if (detail is null) continue;

                long alertId;
                using (var command = CreateCommand(transaction.Connection!, transaction, """
                    INSERT OR IGNORE INTO alerts(
                        rule_name, work_item_id, subject_kind, subject_id,
                        created_utc, state, enqueue_config_hash,
                        evaluation_config_hash, detail, updated_utc)
                    VALUES(
                        $rule_name, $work_item_id, $subject_kind, $subject_id,
                        $created_utc, 'open', $enqueue_hash,
                        $evaluation_hash, $detail, $created_utc);
                    """))
                {
                    command.Parameters.AddWithValue("$rule_name", rule.Name);
                    command.Parameters.AddWithValue("$work_item_id", itemId);
                    command.Parameters.AddWithValue("$subject_kind", kind);
                    command.Parameters.AddWithValue("$subject_id", subjectId);
                    command.Parameters.AddWithValue("$created_utc", createdUtc);
                    command.Parameters.AddWithValue("$enqueue_hash", enqueueHash);
                    command.Parameters.AddWithValue("$evaluation_hash", evaluationConfigHash);
                    command.Parameters.AddWithValue("$detail", detail);
                    if (command.ExecuteNonQuery() != 1) continue;
                }

                alertId = Convert.ToInt64(
                    ExecuteScalar(transaction.Connection!, transaction, "SELECT last_insert_rowid();"),
                    CultureInfo.InvariantCulture);
                AppendCustody(
                    AlertCustodyBytes(alertId, rule.Name, itemId, "created"),
                    receipt,
                    "alert:created",
                    "alert",
                    alertId.ToString(CultureInfo.InvariantCulture),
                    transaction);
                created.Add(new CreatedAlert(
                    alertId, rule.Name, kind, subjectId, createdUtc, detail));
            }

            ExecuteParameterized(
                transaction,
                "UPDATE meta SET value = $cursor WHERE key = 'alert_cursor';",
                ("$cursor", itemId.ToString(CultureInfo.InvariantCulture)));

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return created;
        }
        finally
        {
            _writerGate.Release();
        }
    }

    /// <summary>Returns the alert detail JSON when the rule matches this work
    /// item, null otherwise. A subject row already removed by retention
    /// simply no longer matches — the queue never fails on it.</summary>
    private static string? EvaluateRule(
        Configuration.AlertRule rule,
        string kind,
        string subjectId,
        string enqueuedUtc,
        SqliteTransaction transaction)
    {
        switch (rule.Type)
        {
            case "event_match":
            {
                if (kind != "event") return null;
                using var command = CreateCommand(transaction.Connection!, transaction,
                    "SELECT event_type FROM events WHERE event_id = $id;");
                command.Parameters.AddWithValue("$id", subjectId);
                var eventType = command.ExecuteScalar() as string;
                return string.Equals(eventType, rule.EventType, StringComparison.Ordinal)
                    ? $$"""{"event_id":"{{subjectId}}","event_type":"{{eventType}}"}"""
                    : null;
            }

            case "chain_break":
            {
                if (kind != "quarantine") return null;
                using var command = CreateCommand(transaction.Connection!, transaction, """
                    SELECT failure_code, claimed_supervisor_boot_id, claimed_sequence
                    FROM quarantine WHERE attempt_id = $id;
                    """);
                command.Parameters.AddWithValue("$id", subjectId);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;
                if (!string.Equals(reader.GetString(0), "chain_break", StringComparison.Ordinal))
                    return null;
                var boot = reader.IsDBNull(1) ? null : reader.GetString(1);
                var sequence = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                return $$"""{"attempt_id":{{subjectId}},"supervisor_boot_id":{{(boot is null ? "null" : $"\"{boot}\"")}},"claimed_sequence":{{sequence?.ToString(CultureInfo.InvariantCulture) ?? "null"}}}""";
            }

            case "gap_detected":
            {
                if (kind != "gap") return null;
                using var command = CreateCommand(transaction.Connection!, transaction, """
                    SELECT supervisor_boot_id, claimed_sequence FROM gaps
                    WHERE gap_id = $id;
                    """);
                command.Parameters.AddWithValue("$id", subjectId);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;
                return $$"""{"gap_id":{{subjectId}},"supervisor_boot_id":"{{reader.GetString(0)}}","claimed_sequence":{{reader.GetInt64(1)}}}""";
            }

            case "ingest_rate":
            {
                if (kind != "event") return null;
                if (!DateTimeOffset.TryParse(
                        enqueuedUtc,
                        CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var enqueued))
                {
                    return null;
                }

                var windowStart = FormatUtc(enqueued.AddSeconds(-rule.WindowSeconds!.Value));
                long count;
                using (var command = CreateCommand(transaction.Connection!, transaction, """
                    SELECT COUNT(*) FROM events
                    WHERE received_utc > $start AND received_utc <= $end;
                    """))
                {
                    command.Parameters.AddWithValue("$start", windowStart);
                    command.Parameters.AddWithValue("$end", enqueuedUtc);
                    count = Convert.ToInt64(
                        command.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                if (count <= rule.Threshold!.Value) return null;

                // One open rate alert per rule: the condition is a state,
                // not an event, and every further item inside the burst
                // would otherwise mint its own copy.
                using (var command = CreateCommand(transaction.Connection!, transaction, """
                    SELECT COUNT(*) FROM alerts
                    WHERE rule_name = $rule AND state = 'open';
                    """))
                {
                    command.Parameters.AddWithValue("$rule", rule.Name);
                    if (Convert.ToInt64(
                            command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                    {
                        return null;
                    }
                }

                return $$"""{"count":{{count}},"threshold":{{rule.Threshold.Value}},"window_seconds":{{rule.WindowSeconds.Value}}}""";
            }

            default:
                return null;
        }
    }

    /// <summary>The alert-lifecycle writer: <c>open → acknowledged →
    /// closed</c>, no other transitions, rows never deleted here. Each
    /// transition commits with its custody entry recording who (the
    /// operator credential's SHA-256), when, and the prior state.</summary>
    internal async Task<AlertTransitionOutcome> TransitionAlertAsync(
        long alertId,
        string targetState,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken)
    {
        if (targetState is not ("acknowledged" or "closed"))
            throw new ArgumentException("Unknown alert state.", nameof(targetState));
        ValidateReceipt(receipt);
        ThrowIfDisposed();

        await _writerGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            using var transaction = _writer.BeginTransaction(deferred: false);
            string? state = null;
            long workItemId = 0;
            string ruleName = string.Empty;
            using (var command = CreateCommand(transaction.Connection!, transaction,
                       "SELECT state, work_item_id, rule_name FROM alerts WHERE alert_id = $id;"))
            {
                command.Parameters.AddWithValue("$id", alertId);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    state = reader.GetString(0);
                    workItemId = reader.GetInt64(1);
                    ruleName = reader.GetString(2);
                }
            }

            if (state is null) return AlertTransitionOutcome.NotFound;
            var legal = (state, targetState) is ("open", "acknowledged") or ("acknowledged", "closed");
            if (!legal) return AlertTransitionOutcome.IllegalTransition;

            ExecuteParameterized(
                transaction,
                """
                UPDATE alerts SET state = $state, updated_utc = $utc, updated_by = $actor
                WHERE alert_id = $id;
                """,
                ("$state", targetState),
                ("$utc", FormatUtc(receipt.ReceivedUtc)),
                ("$actor", receipt.ClientCertificateThumbprint),
                ("$id", alertId.ToString(CultureInfo.InvariantCulture)));

            AppendCustody(
                AlertCustodyBytes(alertId, ruleName, workItemId, $"{state}->{targetState}"),
                receipt,
                $"alert:{targetState}",
                "alert",
                alertId.ToString(CultureInfo.InvariantCulture),
                transaction);

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return AlertTransitionOutcome.Ok;
        }
        finally
        {
            _writerGate.Release();
        }
    }

    private void ExecuteParameterized(
        SqliteTransaction transaction,
        string commandText,
        params (string Name, string Value)[] parameters)
    {
        using var command = CreateCommand(transaction.Connection!, transaction, commandText);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The write did not affect exactly one row.");
    }

    private static byte[] AlertCustodyBytes(
        long alertId,
        string ruleName,
        long workItemId,
        string transition) =>
        Encoding.UTF8.GetBytes($"alert:{alertId}:{ruleName}:item={workItemId}:{transition}");

    // ---- Gap-disposition state machine (mini-SIEM S6 / R5c) ----

    /// <summary>
    /// The operator's sole authority over resumption: an <c>open</c> gap
    /// becomes <c>dispositioned</c> (<c>resolved</c> or <c>accepted-loss</c>),
    /// and if post-gap records are already stored the chain resumes
    /// immediately — the head moves to the sub-chain tail and the gap is
    /// <c>resumed</c>. With no stored post-gap records the gap stays
    /// dispositioned and the next record beyond the gap anchors and resumes.
    /// Resolution never acknowledges the missing record: the quarantine row
    /// and the gap row remain the evidence.
    /// </summary>
    internal async Task<GapDispositionOutcome> DispositionGapAsync(
        long gapId,
        string disposition,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken)
    {
        if (disposition is not ("resolved" or "accepted-loss"))
            throw new ArgumentException("Unknown gap disposition.", nameof(disposition));
        ValidateReceipt(receipt);
        ThrowIfDisposed();

        await _writerGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            using var transaction = _writer.BeginTransaction(deferred: false);
            GapRow? gap = null;
            using (var command = CreateCommand(transaction.Connection!, transaction, """
                SELECT gap_id, supervisor_boot_id, state, claimed_sequence
                FROM gaps WHERE gap_id = $gap_id;
                """))
            {
                command.Parameters.AddWithValue("$gap_id", gapId);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    gap = new GapRow(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt64(3));
                }
            }

            if (gap is null) return GapDispositionOutcome.NotFound;
            if (gap.State != "open") return GapDispositionOutcome.IllegalState;

            var dispositionUtc = FormatUtc(receipt.ReceivedUtc);
            using (var command = CreateCommand(transaction.Connection!, transaction, """
                UPDATE gaps SET
                    state = 'dispositioned',
                    disposition = $disposition,
                    disposition_actor = $actor,
                    disposition_endpoint = $endpoint,
                    disposition_utc = $utc
                WHERE gap_id = $gap_id;
                """))
            {
                command.Parameters.AddWithValue("$disposition", disposition);
                command.Parameters.AddWithValue("$actor", receipt.ClientCertificateThumbprint);
                command.Parameters.AddWithValue("$endpoint", receipt.RemoteEndpoint);
                command.Parameters.AddWithValue("$utc", dispositionUtc);
                command.Parameters.AddWithValue("$gap_id", gap.GapId);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The gap disposition did not affect exactly one row.");
            }

            AppendCustody(
                GapCustodyBytes(gap, $"dispositioned:{disposition}"),
                receipt,
                $"gap:dispositioned:{disposition}",
                "gap",
                gap.GapId.ToString(CultureInfo.InvariantCulture),
                transaction);

            var resumed = false;
            if (Guid.TryParseExact(gap.SupervisorBootId, "D", out var bootId))
            {
                var currentHead = ReadChain(bootId, transaction);
                var tail = ReadPostGapHead(bootId, currentHead?.Sequence ?? 0, transaction);
                if (tail is not null)
                {
                    var first = ReadFirstPostGap(
                        bootId, currentHead?.Sequence ?? 0, transaction);
                    SetChainHead(gap.SupervisorBootId, tail, transaction);
                    MarkGapResumed(gap.GapId, dispositionUtc, first!.EventId, transaction);
                    AppendCustody(
                        GapCustodyBytes(gap, "resumed"),
                        receipt,
                        "gap:resumed",
                        "gap",
                        gap.GapId.ToString(CultureInfo.InvariantCulture),
                        transaction);
                    resumed = true;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return resumed ? GapDispositionOutcome.Resumed : GapDispositionOutcome.Dispositioned;
        }
        finally
        {
            _writerGate.Release();
        }
    }

    private static GapRow? ReadActiveGap(Guid supervisorBootId, SqliteTransaction transaction)
    {
        using var command = CreateCommand(transaction.Connection!, transaction, """
            SELECT gap_id, supervisor_boot_id, state, claimed_sequence
            FROM gaps
            WHERE supervisor_boot_id = $boot_id AND state != 'resumed';
            """);
        command.Parameters.AddWithValue("$boot_id", FormatGuid(supervisorBootId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new GapRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3))
            : null;
    }

    /// <summary>The stored post-gap sub-chain's newest record for this boot.
    /// Post-gap flags persist as historical evidence after a resume, so the
    /// walk only considers records above the frozen head — a resumed
    /// episode's records sit at or below it.</summary>
    private static ChainHead? ReadPostGapHead(
        Guid supervisorBootId,
        long headSequence,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(transaction.Connection!, transaction, """
            SELECT sequence, event_id, event_hash FROM events
            WHERE supervisor_boot_id = $boot_id AND post_gap = 1 AND sequence > $head
            ORDER BY sequence DESC LIMIT 1;
            """);
        command.Parameters.AddWithValue("$boot_id", FormatGuid(supervisorBootId));
        command.Parameters.AddWithValue("$head", headSequence);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ChainHead(reader.GetInt64(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static ChainHead? ReadFirstPostGap(
        Guid supervisorBootId,
        long headSequence,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(transaction.Connection!, transaction, """
            SELECT sequence, event_id, event_hash FROM events
            WHERE supervisor_boot_id = $boot_id AND post_gap = 1 AND sequence > $head
            ORDER BY sequence ASC LIMIT 1;
            """);
        command.Parameters.AddWithValue("$boot_id", FormatGuid(supervisorBootId));
        command.Parameters.AddWithValue("$head", headSequence);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ChainHead(reader.GetInt64(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    /// <summary>The first post-gap record anchors the sub-chain wherever the
    /// gap left it; every later one must chain contiguously onto the stored
    /// sub-chain exactly as the main chain would.</summary>
    private static string? ValidatePostGapPosition(
        ValidatedOtlpRecord record,
        ChainHead? postGapHead)
    {
        if (postGapHead is null) return null;
        if (record.Sequence <= postGapHead.Sequence) return "chain_position";
        if (record.Sequence > postGapHead.Sequence + 1) return "chain_gap";
        return string.Equals(
            record.PreviousEventHash,
            postGapHead.EventHash,
            StringComparison.Ordinal)
            ? null
            : "chain_break";
    }

    private static long OpenGap(
        ValidatedOtlpRecord record,
        ChainHead? currentHead,
        IngestReceiptContext receipt,
        SqliteTransaction transaction)
    {
        using (var command = CreateCommand(transaction.Connection!, transaction, """
            INSERT INTO gaps(
                supervisor_boot_id, observed_head_sequence, claimed_sequence,
                opened_utc, state)
            VALUES($boot_id, $head_sequence, $claimed_sequence, $opened_utc, 'open');
            """))
        {
            command.Parameters.AddWithValue("$boot_id", FormatGuid(record.SupervisorBootId));
            AddNullable(command, "$head_sequence", currentHead?.Sequence);
            command.Parameters.AddWithValue("$claimed_sequence", record.Sequence);
            command.Parameters.AddWithValue("$opened_utc", FormatUtc(receipt.ReceivedUtc));
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The gap insert did not affect exactly one row.");
        }

        var gapId = Convert.ToInt64(
            ExecuteScalar(transaction.Connection!, transaction, "SELECT last_insert_rowid();"),
            CultureInfo.InvariantCulture);
        var gap = new GapRow(
            gapId, FormatGuid(record.SupervisorBootId), "open", record.Sequence);
        AppendCustody(
            GapCustodyBytes(gap, "opened"),
            receipt,
            "gap:opened",
            "gap",
            gapId.ToString(CultureInfo.InvariantCulture),
            transaction);
        return gapId;
    }

    private static void ResumeGap(
        GapRow gap,
        ValidatedOtlpRecord record,
        IngestReceiptContext receipt,
        SqliteTransaction transaction)
    {
        SetChainHead(
            gap.SupervisorBootId,
            new ChainHead(record.Sequence, FormatGuid(record.EventId), record.EventHash),
            transaction);
        MarkGapResumed(
            gap.GapId,
            FormatUtc(receipt.ReceivedUtc),
            FormatGuid(record.EventId),
            transaction);
        AppendCustody(
            GapCustodyBytes(gap, "resumed"),
            receipt,
            "gap:resumed",
            "gap",
            gap.GapId.ToString(CultureInfo.InvariantCulture),
            transaction);
    }

    private static void SetChainHead(
        string supervisorBootId,
        ChainHead head,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(transaction.Connection!, transaction, """
            INSERT INTO chains(
                supervisor_boot_id, head_sequence, head_event_id, head_event_hash)
            VALUES($boot_id, $sequence, $event_id, $event_hash)
            ON CONFLICT(supervisor_boot_id) DO UPDATE SET
                head_sequence = $sequence,
                head_event_id = $event_id,
                head_event_hash = $event_hash;
            """);
        command.Parameters.AddWithValue("$boot_id", supervisorBootId);
        command.Parameters.AddWithValue("$sequence", head.Sequence);
        command.Parameters.AddWithValue("$event_id", head.EventId);
        command.Parameters.AddWithValue("$event_hash", head.EventHash);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The chain-head resume write did not affect exactly one row.");
    }

    private static void MarkGapResumed(
        long gapId,
        string resumedUtc,
        string resumeEventId,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(transaction.Connection!, transaction, """
            UPDATE gaps SET
                state = 'resumed',
                resumed_utc = $utc,
                resume_event_id = $event_id
            WHERE gap_id = $gap_id;
            """);
        command.Parameters.AddWithValue("$utc", resumedUtc);
        command.Parameters.AddWithValue("$event_id", resumeEventId);
        command.Parameters.AddWithValue("$gap_id", gapId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The gap resume did not affect exactly one row.");
    }

    private static byte[] GapCustodyBytes(GapRow gap, string transition) =>
        Encoding.UTF8.GetBytes(
            $"gap:{gap.GapId}:{gap.SupervisorBootId}:claimed={gap.ClaimedSequence}:{transition}");

    private sealed record GapRow(
        long GapId,
        string SupervisorBootId,
        string State,
        long ClaimedSequence);

    private static long AppendQuarantine(
        RejectedOtlpAttempt attempt,
        IngestReceiptContext receipt,
        ChainHead? currentHead,
        SqliteTransaction transaction)
    {
        using (var command = CreateCommand(transaction.Connection!, transaction, """
            INSERT INTO quarantine(
                failure_code, claimed_event_id, claimed_event_hash,
                claimed_previous_event_hash, claimed_supervisor_boot_id,
                claimed_sequence, observed_head_sequence, observed_head_event_hash,
                raw_request, exact_json_body, received_utc)
            VALUES(
                $failure_code, $claimed_event_id, $claimed_event_hash,
                $claimed_previous_event_hash, $claimed_supervisor_boot_id,
                $claimed_sequence, $observed_head_sequence, $observed_head_event_hash,
                $raw_request, $exact_json_body, $received_utc);
            """))
        {
            command.Parameters.AddWithValue("$failure_code", attempt.FailureCode);
            AddNullable(command, "$claimed_event_id", attempt.ClaimedEventId);
            AddNullable(command, "$claimed_event_hash", attempt.ClaimedEventHash);
            AddNullable(command, "$claimed_previous_event_hash", attempt.ClaimedPreviousEventHash);
            AddNullable(command, "$claimed_supervisor_boot_id", attempt.ClaimedSupervisorBootId);
            AddNullable(command, "$claimed_sequence", attempt.ClaimedSequence);
            AddNullable(command, "$observed_head_sequence", currentHead?.Sequence);
            AddNullable(command, "$observed_head_event_hash", currentHead?.EventHash);
            command.Parameters.AddWithValue("$raw_request", attempt.RawRequestBytes);
            AddNullable(command, "$exact_json_body", attempt.ExactJsonBody);
            command.Parameters.AddWithValue("$received_utc", FormatUtc(receipt.ReceivedUtc));
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The quarantine insert did not affect exactly one row.");
        }

        var attemptId = Convert.ToInt64(
            ExecuteScalar(transaction.Connection!, transaction, "SELECT last_insert_rowid();"),
            CultureInfo.InvariantCulture);
        AppendCustody(
            attempt.RawRequestBytes,
            receipt,
            $"quarantine:{attempt.FailureCode}",
            "quarantine",
            attemptId.ToString(CultureInfo.InvariantCulture),
            transaction);
        return attemptId;
    }

    /// <summary>Durably enqueues an alert-evaluation work item in the same
    /// transaction as the row it describes, stamped with the startup-frozen
    /// rule-configuration hash. No configured rules, no queue.</summary>
    private void EnqueueAlertWork(
        string kind,
        string subjectId,
        IngestReceiptContext receipt,
        SqliteTransaction transaction)
    {
        if (_alertRuleConfigHash is null) return;
        using var command = CreateCommand(transaction.Connection!, transaction, """
            INSERT INTO alert_queue(kind, subject_id, enqueued_utc, rule_config_hash)
            VALUES($kind, $subject_id, $enqueued_utc, $rule_config_hash);
            """);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$subject_id", subjectId);
        command.Parameters.AddWithValue("$enqueued_utc", FormatUtc(receipt.ReceivedUtc));
        command.Parameters.AddWithValue("$rule_config_hash", _alertRuleConfigHash);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The alert work-item insert did not affect exactly one row.");
    }

    private static void AppendCustody(
        byte[] rawRequestBytes,
        IngestReceiptContext receipt,
        string disposition,
        string subjectKind,
        string subjectId,
        SqliteTransaction transaction)
    {
        long previousSequence = 0;
        string? previousHash = null;
        using (var head = CreateCommand(transaction.Connection!, transaction, """
            SELECT receipt_sequence, receipt_hash
            FROM custody
            ORDER BY receipt_sequence DESC
            LIMIT 1;
            """))
        using (var reader = head.ExecuteReader())
        {
            if (reader.Read())
            {
                previousSequence = reader.GetInt64(0);
                previousHash = reader.GetString(1);
            }
        }

        var receiptSequence = checked(previousSequence + 1);
        var receivedUtc = FormatUtc(receipt.ReceivedUtc);
        var receiptHash = CustodyHash.Compute(
            receiptSequence,
            previousHash,
            rawRequestBytes,
            receivedUtc,
            receipt.ClientCertificateThumbprint,
            receipt.RemoteEndpoint,
            disposition,
            subjectKind,
            subjectId);

        using var command = CreateCommand(transaction.Connection!, transaction, """
            INSERT INTO custody(
                receipt_sequence, ledger_version, previous_receipt_hash, receipt_hash,
                received_utc, client_certificate_thumbprint, remote_endpoint,
                disposition, subject_kind, subject_id)
            VALUES(
                $receipt_sequence, 1, $previous_receipt_hash, $receipt_hash,
                $received_utc, $client_certificate_thumbprint, $remote_endpoint,
                $disposition, $subject_kind, $subject_id);
            """);
        command.Parameters.AddWithValue("$receipt_sequence", receiptSequence);
        AddNullable(command, "$previous_receipt_hash", previousHash);
        command.Parameters.AddWithValue("$receipt_hash", receiptHash);
        command.Parameters.AddWithValue("$received_utc", receivedUtc);
        command.Parameters.AddWithValue("$client_certificate_thumbprint", receipt.ClientCertificateThumbprint);
        command.Parameters.AddWithValue("$remote_endpoint", receipt.RemoteEndpoint);
        command.Parameters.AddWithValue("$disposition", disposition);
        command.Parameters.AddWithValue("$subject_kind", subjectKind);
        command.Parameters.AddWithValue("$subject_id", subjectId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The custody insert did not affect exactly one row.");
    }

    private static RejectedOtlpAttempt RejectedFrom(
        ValidatedOtlpRecord record,
        string failureCode) =>
        new(
            record.RawRequestBytes,
            record.ExactJsonBody,
            failureCode,
            FormatGuid(record.EventId),
            record.EventHash,
            record.PreviousEventHash,
            FormatGuid(record.SupervisorBootId),
            record.Sequence);

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        command.CommandTimeout = BusyTimeoutSeconds;
        return command;
    }

    private static object? ExecuteScalar(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        using var command = CreateCommand(connection, transaction, commandText);
        return command.ExecuteScalar();
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        using var command = CreateCommand(connection, transaction, commandText);
        command.ExecuteNonQuery();
    }

    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void ValidateReceipt(IngestReceiptContext receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ReceivedUtc.Offset != TimeSpan.Zero ||
            receipt.ClientCertificateThumbprint.Length != 64 ||
            receipt.ClientCertificateThumbprint.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)) ||
            string.IsNullOrWhiteSpace(receipt.RemoteEndpoint))
        {
            throw new ArgumentException("The ingest receipt metadata is invalid.", nameof(receipt));
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static string FormatGuid(Guid value) => value.ToString("D");

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record EventIdentity(string EventHash, byte[] ExactJsonBody);

    private sealed record ChainHead(long Sequence, string EventId, string EventHash);

    private const string SchemaVersionOneSql = """
        CREATE TABLE meta(
            key TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE events(
            event_id TEXT PRIMARY KEY NOT NULL,
            supervisor_boot_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK(sequence >= 1),
            schema_version TEXT NOT NULL,
            event_type TEXT NOT NULL,
            occurred_utc TEXT NOT NULL,
            observed_utc TEXT NOT NULL,
            host_id TEXT NOT NULL,
            worker_boot_id TEXT NULL,
            previous_event_hash TEXT NULL,
            event_hash TEXT NOT NULL,
            session_name TEXT NULL,
            session_generation INTEGER NULL,
            call_id TEXT NULL,
            job_id INTEGER NULL,
            outcome_state TEXT NULL,
            raw_request BLOB NOT NULL,
            exact_json_body BLOB NOT NULL,
            received_utc TEXT NOT NULL,
            UNIQUE(supervisor_boot_id, sequence)
        );

        CREATE INDEX ix_events_occurred_utc
            ON events(occurred_utc);
        CREATE INDEX ix_events_type_occurred
            ON events(event_type, occurred_utc);
        CREATE INDEX ix_events_session_occurred
            ON events(session_name, occurred_utc)
            WHERE session_name IS NOT NULL;

        CREATE TABLE chains(
            supervisor_boot_id TEXT PRIMARY KEY NOT NULL,
            head_sequence INTEGER NOT NULL CHECK(head_sequence >= 1),
            head_event_id TEXT NOT NULL,
            head_event_hash TEXT NOT NULL,
            FOREIGN KEY(head_event_id) REFERENCES events(event_id)
        ) WITHOUT ROWID;

        CREATE TABLE quarantine(
            attempt_id INTEGER PRIMARY KEY AUTOINCREMENT,
            failure_code TEXT NOT NULL,
            claimed_event_id TEXT NULL,
            claimed_event_hash TEXT NULL,
            claimed_previous_event_hash TEXT NULL,
            claimed_supervisor_boot_id TEXT NULL,
            claimed_sequence INTEGER NULL,
            observed_head_sequence INTEGER NULL,
            observed_head_event_hash TEXT NULL,
            raw_request BLOB NOT NULL,
            exact_json_body BLOB NULL,
            received_utc TEXT NOT NULL
        );

        CREATE INDEX ix_quarantine_received
            ON quarantine(received_utc);
        CREATE INDEX ix_quarantine_failure_received
            ON quarantine(failure_code, received_utc);

        CREATE TABLE custody(
            receipt_sequence INTEGER PRIMARY KEY,
            ledger_version INTEGER NOT NULL CHECK(ledger_version = 1),
            previous_receipt_hash TEXT NULL,
            receipt_hash TEXT NOT NULL UNIQUE,
            received_utc TEXT NOT NULL,
            client_certificate_thumbprint TEXT NOT NULL,
            remote_endpoint TEXT NOT NULL,
            disposition TEXT NOT NULL,
            subject_kind TEXT NOT NULL,
            subject_id TEXT NOT NULL
        );
        """;

    private const string SchemaVersionTwoSql = """
        ALTER TABLE events ADD COLUMN post_gap INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE gaps(
            gap_id INTEGER PRIMARY KEY AUTOINCREMENT,
            supervisor_boot_id TEXT NOT NULL,
            observed_head_sequence INTEGER NULL,
            claimed_sequence INTEGER NOT NULL,
            opened_utc TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('open','dispositioned','resumed')),
            disposition TEXT NULL CHECK(disposition IN ('resolved','accepted-loss')),
            disposition_actor TEXT NULL,
            disposition_endpoint TEXT NULL,
            disposition_utc TEXT NULL,
            resumed_utc TEXT NULL,
            resume_event_id TEXT NULL
        );

        CREATE UNIQUE INDEX ix_gaps_active_boot
            ON gaps(supervisor_boot_id)
            WHERE state != 'resumed';
        CREATE INDEX ix_gaps_opened
            ON gaps(opened_utc);
        """;

    private const string SchemaVersionThreeSql = """
        CREATE TABLE alert_queue(
            item_id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL CHECK(kind IN ('event','quarantine','gap')),
            subject_id TEXT NOT NULL,
            enqueued_utc TEXT NOT NULL,
            rule_config_hash TEXT NOT NULL
        );

        CREATE TABLE alerts(
            alert_id INTEGER PRIMARY KEY AUTOINCREMENT,
            rule_name TEXT NOT NULL,
            work_item_id INTEGER NOT NULL,
            subject_kind TEXT NOT NULL,
            subject_id TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('open','acknowledged','closed')),
            enqueue_config_hash TEXT NOT NULL,
            evaluation_config_hash TEXT NOT NULL,
            detail TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            updated_by TEXT NULL,
            UNIQUE(work_item_id, rule_name)
        );

        CREATE INDEX ix_alerts_state
            ON alerts(state, alert_id);

        INSERT INTO meta(key, value) VALUES('alert_cursor', '0');
        """;
}

internal static class CustodyHash
{
    private static readonly byte[] Magic = "PTK-SIEM-CUSTODY"u8.ToArray();

    /// <summary>
    /// Version 1 framing is the fixed magic, a big-endian ledger version and
    /// receipt sequence, then eight big-endian-length-prefixed fields in this
    /// exact order: previous hash, raw request, receipt UTC, certificate
    /// thumbprint, remote endpoint, disposition, subject kind, subject ID.
    /// </summary>
    internal static string Compute(
        long receiptSequence,
        string? previousReceiptHash,
        ReadOnlySpan<byte> rawRequestBytes,
        string receivedUtc,
        string certificateThumbprint,
        string remoteEndpoint,
        string disposition,
        string subjectKind,
        string subjectId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Magic);

        Span<byte> integer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt32BigEndian(integer[..sizeof(int)], 1);
        hash.AppendData(integer[..sizeof(int)]);
        BinaryPrimitives.WriteInt64BigEndian(integer, receiptSequence);
        hash.AppendData(integer);

        AppendText(hash, previousReceiptHash ?? string.Empty);
        AppendField(hash, rawRequestBytes);
        AppendText(hash, receivedUtc);
        AppendText(hash, certificateThumbprint);
        AppendText(hash, remoteEndpoint);
        AppendText(hash, disposition);
        AppendText(hash, subjectKind);
        AppendText(hash, subjectId);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendText(IncrementalHash hash, string value) =>
        AppendField(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendField(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
