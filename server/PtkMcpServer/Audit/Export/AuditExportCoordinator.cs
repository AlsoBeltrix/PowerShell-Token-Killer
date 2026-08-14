using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace PtkMcpServer.Audit.Export;

internal sealed record AuditDestinationStatus(
    Guid DestinationId,
    string Kind,
    string OperatorLabel,
    string EndpointSummary,
    string Adapter,
    string CredentialReference,
    long ConfigurationRevision,
    DateTimeOffset ActivatedUtc,
    bool Enabled,
    AuditExportHealthSnapshot Delivery,
    AuditBackfillStatus? Backfill = null);

internal sealed record AuditBackfillStatus(
    Guid BackfillId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset CreatedUtc,
    string Actor,
    AuditBackfillState State,
    DateTimeOffset? CompletedUtc,
    string? Failure,
    AuditExportHealthSnapshot Delivery);

/// <summary>
/// Keeps one exporter runtime per enabled destination. Each runtime owns its
/// cursor, gap ledger and lease; configuration revision changes replace only
/// that destination. The local journal remains the single admission source.
/// </summary>
internal sealed class AuditExportCoordinator : IHostedService, IAsyncDisposable
{
    private readonly AuditOptions _options;
    private readonly AuditDestinationRegistry _registry;
    private readonly AuditBackfillRegistry _backfillRegistry;
    private readonly ScriptEvidenceStoreProvider _evidence;
    private readonly Func<AuditJournal?> _liveJournalSource;
    private readonly Uri? _alertWebhook;
    private readonly AuditExportHealth _primaryHealth;
    private readonly Func<AuditExportSettings, IAuditDestination> _destinationFactory;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly Dictionary<Guid, ExportRuntime> _runtimes = [];
    private readonly Dictionary<Guid, ExportRuntime> _backfillRuntimes = [];
    private readonly Dictionary<Guid, AuditExportHealth> _health = [];
    private readonly Dictionary<Guid, AuditExportHealth> _backfillHealth = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _pump;
    private int _disposed;

    internal AuditExportCoordinator(
        AuditOptions options,
        AuditDestinationRegistry registry,
        AuditBackfillRegistry backfillRegistry,
        ScriptEvidenceStoreProvider evidence,
        Func<AuditJournal?> liveJournalSource,
        AuditExportHealth primaryHealth,
        Uri? alertWebhook = null,
        Func<AuditExportSettings, IAuditDestination>? destinationFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(backfillRegistry);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(liveJournalSource);
        ArgumentNullException.ThrowIfNull(primaryHealth);
        _options = options;
        _registry = registry;
        _backfillRegistry = backfillRegistry;
        _evidence = evidence;
        _liveJournalSource = liveJournalSource;
        _primaryHealth = primaryHealth;
        _alertWebhook = alertWebhook;
        _destinationFactory = destinationFactory ??
            (settings => new HttpAuditDestination(settings));
    }

    internal IReadOnlyList<AuditDestinationStatus> Statuses()
    {
        var snapshot = _registry.Snapshot();
        lock (_health)
        {
            return snapshot.Destinations
                .OrderBy(destination => destination.OperatorLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(destination => destination.DestinationId)
                .Select(destination =>
                {
                    var health = _health.TryGetValue(destination.DestinationId, out var holder)
                        ? holder.Snapshot()
                        : DisabledSnapshot(destination);
                    var backfill = _backfillRegistry.ForDestination(destination.DestinationId);
                    AuditBackfillStatus? backfillStatus = null;
                    if (backfill is not null)
                    {
                        var backfillDelivery = _backfillHealth.TryGetValue(
                            destination.DestinationId,
                            out var backfillHolder)
                            ? backfillHolder.Snapshot()
                            : DisabledSnapshot(destination);
                        backfillStatus = new AuditBackfillStatus(
                            backfill.BackfillId,
                            backfill.FromUtc,
                            backfill.ToUtc,
                            backfill.CreatedUtc,
                            backfill.Actor,
                            backfill.State,
                            backfill.CompletedUtc,
                            backfill.Failure,
                            backfillDelivery);
                    }
                    return new AuditDestinationStatus(
                        destination.DestinationId,
                        AuditExportSettings.KindText(destination.Kind),
                        destination.OperatorLabel,
                        destination.RedactedEndpoint,
                        destination.Adapter,
                        destination.CredentialReference,
                        destination.ConfigurationRevision,
                        destination.ActivatedUtc,
                        destination.Enabled,
                        health with { Enabled = destination.Enabled },
                        backfillStatus);
                })
                .ToArray();
        }
    }

    internal async Task<bool> HasPendingObligationsAsync(
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_registry.TryRefresh(out _))
                return true;
            if (_runtimes.TryGetValue(destinationId, out var runtime))
                return runtime.Service.HasPendingObligations();

            var destination = _registry.Snapshot().Destinations
                .FirstOrDefault(item => item.DestinationId == destinationId);
            if (destination is null) return false;

            var health = GetOrCreateHealth(destination);
            var temporary = CreateRuntime(destination, health);
            try
            {
                return temporary.Service.HasPendingObligations();
            }
            finally
            {
                await temporary.Service.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await SynchronizeNowAsync(cancellationToken).ConfigureAwait(false);
        _pump = Task.Run(() => PumpAsync(_stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Individual exporters retain their durable cursors.
            }
        }
        await StopAllAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task SynchronizeNowAsync(CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_registry.TryRefresh(out var refreshFailure))
                throw new InvalidDataException(
                    $"Destination configuration refresh failed: {refreshFailure}.");
            var configured = _registry.Snapshot().Destinations;
            var desired = configured
                .Where(destination => destination.Enabled)
                .ToDictionary(destination => destination.DestinationId);

            foreach (var existing in _runtimes.ToArray())
            {
                if (desired.TryGetValue(existing.Key, out var definition) &&
                    definition.ConfigurationRevision == existing.Value.ConfigurationRevision)
                {
                    continue;
                }
                await StopRuntimeAsync(existing.Value, cancellationToken).ConfigureAwait(false);
                _runtimes.Remove(existing.Key);
            }

            foreach (var definition in configured)
            {
                var health = GetOrCreateHealth(definition);
                health.SetDestination(
                    definition.DestinationId,
                    definition.OperatorLabel,
                    definition.RedactedEndpoint,
                    definition.Enabled);
                if (!definition.Enabled || _runtimes.ContainsKey(definition.DestinationId))
                    continue;

                var runtime = CreateRuntime(definition, health);
                _runtimes.Add(definition.DestinationId, runtime);
                await runtime.Service.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            await SynchronizeBackfillsAsync(configured, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                await SynchronizeNowAsync(cancellationToken).ConfigureAwait(false);
                CompleteFinishedBackfills();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                _primaryHealth.RecordFailure("export.coordinator_sync_failed");
            }
        }
    }

    private ExportRuntime CreateRuntime(
        AuditDestinationDefinition definition,
        AuditExportHealth health)
    {
        var settings = definition.ToExportSettings(_alertWebhook);
        var service = new AuditExportService(
            _options,
            _destinationFactory(settings),
            new AuditExportCursorStore(
                _options.RootDirectory,
                AuditExportCursorStore.DestinationFileName(definition.DestinationId)),
            health,
            liveJournalSource: _liveJournalSource,
            evidence: _evidence,
            gapStore: new AuditExportGapStore(
                _options.RootDirectory,
                AuditExportGapStore.DestinationFileName(definition.DestinationId)),
            lease: new AuditExportLease(
                AuditExportLease.DestinationFileName(definition.DestinationId)),
            recordFilter: record => IsRequiredBy(record, definition),
            holdAllPermanentRefusals: true);
        return new ExportRuntime(definition.ConfigurationRevision, service);
    }

    private async Task SynchronizeBackfillsAsync(
        IReadOnlyList<AuditDestinationDefinition> destinations,
        CancellationToken cancellationToken)
    {
        var definitions = destinations.ToDictionary(item => item.DestinationId);
        var active = _backfillRegistry.ReadAll()
            .Where(item => item.State == AuditBackfillState.Active)
            .ToDictionary(item => item.DestinationId);

        foreach (var existing in _backfillRuntimes.ToArray())
        {
            if (active.TryGetValue(existing.Key, out var backfill) &&
                existing.Value.ConfigurationRevision == BackfillRevision(backfill))
            {
                continue;
            }
            await StopRuntimeAsync(existing.Value, cancellationToken).ConfigureAwait(false);
            _backfillRuntimes.Remove(existing.Key);
        }

        foreach (var backfill in active.Values)
        {
            if (_backfillRuntimes.ContainsKey(backfill.DestinationId)) continue;
            if (!definitions.TryGetValue(backfill.DestinationId, out var destination))
            {
                _backfillRegistry.TryFail(backfill.DestinationId, "destination_not_found");
                continue;
            }
            var health = GetOrCreateBackfillHealth(destination);
            health.SetDestination(
                destination.DestinationId,
                destination.OperatorLabel,
                destination.RedactedEndpoint,
                enabled: true);
            var runtime = CreateBackfillRuntime(destination, backfill, health);
            _backfillRuntimes.Add(backfill.DestinationId, runtime);
            await runtime.Service.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private ExportRuntime CreateBackfillRuntime(
        AuditDestinationDefinition destination,
        AuditBackfillDefinition backfill,
        AuditExportHealth health)
    {
        var suffix = backfill.BackfillId.ToString("N");
        var service = new AuditExportService(
            _options,
            _destinationFactory(destination.ToExportSettings(_alertWebhook)),
            new AuditExportCursorStore(
                _options.RootDirectory,
                $"export-backfill-cursor-{suffix}.json"),
            health,
            liveJournalSource: _liveJournalSource,
            evidence: _evidence,
            gapStore: new AuditExportGapStore(
                _options.RootDirectory,
                $"export-backfill-gaps-{suffix}.json"),
            lease: new AuditExportLease($"export-backfill-{suffix}.lock"),
            recordFilter: record => IsInRange(record, backfill.FromUtc, backfill.ToUtc),
            holdAllPermanentRefusals: true);
        return new ExportRuntime(BackfillRevision(backfill), service);
    }

    private AuditExportHealth GetOrCreateBackfillHealth(
        AuditDestinationDefinition destination)
    {
        lock (_health)
        {
            if (_backfillHealth.TryGetValue(destination.DestinationId, out var existing))
                return existing;
            var created = new AuditExportHealth();
            _backfillHealth.Add(destination.DestinationId, created);
            return created;
        }
    }

    private void CompleteFinishedBackfills()
    {
        lock (_health)
        {
            foreach (var item in _backfillHealth)
            {
                var health = item.Value.Snapshot();
                if (health.LastScanUtc is null || health.ConsecutiveFailures > 0 ||
                    health.PendingBytes > 0 || health.PendingEventRecords > 0 ||
                    health.PendingEvidenceRecords > 0)
                {
                    continue;
                }
                _backfillRegistry.TryComplete(item.Key, DateTimeOffset.UtcNow);
            }
        }
    }

    private AuditExportHealth GetOrCreateHealth(AuditDestinationDefinition definition)
    {
        lock (_health)
        {
            if (_health.TryGetValue(definition.DestinationId, out var existing))
                return existing;
            var created = _health.Count == 0 ? _primaryHealth : new AuditExportHealth();
            _health.Add(definition.DestinationId, created);
            return created;
        }
    }

    internal static bool IsRequiredBy(
        string record,
        AuditDestinationDefinition destination)
    {
        try
        {
            using var document = JsonDocument.Parse(record);
            var root = document.RootElement;
            var version = root.TryGetProperty("schema_version", out var schema)
                ? schema.GetString()
                : null;
            if (!string.Equals(
                    version,
                    AuditEventSerializer.DestinationObligationSchemaVersion,
                    StringComparison.Ordinal))
            {
                return destination.IncludeLegacyRecords;
            }
            if (!root.TryGetProperty("required_destination_ids", out var required) ||
                required.ValueKind != JsonValueKind.Array)
            {
                // A v6 record without obligations is malformed. Never route it
                // to an arbitrary prospective destination.
                return false;
            }
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(item.GetString(), out var destinationId) &&
                    destinationId == destination.DestinationId)
                {
                    return true;
                }
            }
            return false;
        }
        catch (JsonException)
        {
            // Existing legacy exporter behavior remains conservative: only the
            // migrated destination may receive records without a v6 obligation.
            return destination.IncludeLegacyRecords;
        }
    }

    internal static bool IsInRange(
        string record,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        try
        {
            using var document = JsonDocument.Parse(record);
            return document.RootElement.TryGetProperty("occurred_utc", out var occurred) &&
                occurred.ValueKind == JsonValueKind.String &&
                occurred.TryGetDateTimeOffset(out var occurredUtc) &&
                occurredUtc >= fromUtc && occurredUtc < toUtc;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task StopAllAsync(CancellationToken cancellationToken)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var runtime in _runtimes.Values)
                await StopRuntimeAsync(runtime, cancellationToken).ConfigureAwait(false);
            _runtimes.Clear();
            foreach (var runtime in _backfillRuntimes.Values)
                await StopRuntimeAsync(runtime, cancellationToken).ConfigureAwait(false);
            _backfillRuntimes.Clear();
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private static async Task StopRuntimeAsync(
        ExportRuntime runtime,
        CancellationToken cancellationToken)
    {
        await runtime.Service.StopAsync(cancellationToken).ConfigureAwait(false);
        await runtime.Service.DisposeAsync().ConfigureAwait(false);
    }

    private static AuditExportHealthSnapshot DisabledSnapshot(
        AuditDestinationDefinition destination) =>
        new(
            Configured: true,
            Destination: destination.Adapter,
            DeliveredRecords: 0,
            PendingBytes: 0,
            ConsecutiveFailures: 0,
            LastFailureDetail: null,
            LastDeliveryUtc: null,
            DestinationId: destination.DestinationId,
            OperatorLabel: destination.OperatorLabel,
            EndpointSummary: destination.RedactedEndpoint,
            Enabled: destination.Enabled);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await StopAllAsync(CancellationToken.None).ConfigureAwait(false);
        _syncGate.Dispose();
        _stopping.Dispose();
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record ExportRuntime(
        long ConfigurationRevision,
        AuditExportService Service);

    private static long BackfillRevision(AuditBackfillDefinition backfill) =>
        BitConverter.ToInt64(backfill.BackfillId.ToByteArray(), 0);
}
