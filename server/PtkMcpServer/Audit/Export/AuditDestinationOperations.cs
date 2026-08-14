using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PtkMcpServer.Audit.Export;

internal sealed record AuditDestinationOperationResult(
    bool Succeeded,
    string? Failure,
    AuditDestinationDefinition? Destination = null);

internal sealed record AuditBackfillOperationResult(
    bool Succeeded,
    string? Failure,
    AuditBackfillDefinition? Backfill = null);

internal interface IAuditDestinationCredentialValidator
{
    Task<string?> ValidateAsync(
        AuditDestinationDraft draft,
        CancellationToken cancellationToken);
}

/// <summary>
/// Performs a non-ingesting endpoint probe. A 401/403 is a credential
/// refusal; any other HTTP response proves the protected endpoint was
/// reached without creating a forensic event.
/// </summary>
internal sealed class AuditDestinationCredentialValidator :
    IAuditDestinationCredentialValidator,
    IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    internal AuditDestinationCredentialValidator(HttpClient? client = null)
    {
        _client = client ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _ownsClient = client is null;
    }

    public async Task<string?> ValidateAsync(
        AuditDestinationDraft draft,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, draft.Endpoint);
        if (!string.IsNullOrEmpty(draft.Credential))
        {
            request.Headers.Authorization = draft.Kind == AuditDestinationKind.SplunkHec
                ? new AuthenticationHeaderValue("Splunk", draft.Credential)
                : new AuthenticationHeaderValue("Bearer", draft.Credential);
        }
        try
        {
            using var response = await _client
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "credential_refused"
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "validation_timeout";
        }
        catch (HttpRequestException)
        {
            return "endpoint_unreachable";
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}

internal sealed class AuditDestinationOperations
{
    private const int MaximumActorLength = 256;
    private const int MaximumReasonLength = 2048;
    private readonly AuditOptions _options;
    private readonly AuditDestinationRegistry _registry;
    private readonly AuditBackfillRegistry _backfills;
    private readonly AuditExportCoordinator _coordinator;
    private readonly IAuditDestinationCredentialValidator _validator;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AuditDestinationOperations(
        AuditOptions options,
        AuditDestinationRegistry registry,
        AuditBackfillRegistry backfills,
        AuditExportCoordinator coordinator,
        IAuditDestinationCredentialValidator validator)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(backfills);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(validator);
        _options = options;
        _registry = registry;
        _backfills = backfills;
        _coordinator = coordinator;
        _validator = validator;
    }

    internal async Task<AuditDestinationOperationResult> AddAsync(
        AuditDestinationDraft draft,
        bool confirmedSensitiveDuplication,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_registry.TryRefresh(out var refreshFailure))
                return new(false, refreshFailure);
            if (_registry.Snapshot().Destinations.Count > 0 &&
                !confirmedSensitiveDuplication)
            {
                return new(false, "sensitive_duplication_confirmation_required");
            }
            var validationFailure = await _validator
                .ValidateAsync(draft, cancellationToken)
                .ConfigureAwait(false);
            if (validationFailure is not null)
                return new(false, validationFailure);
            if (!_registry.TryAdd(
                    draft,
                    confirmedSensitiveDuplication,
                    DateTimeOffset.UtcNow,
                    out var created,
                    out var failure))
            {
                return new(false, failure);
            }
            await _coordinator.SynchronizeNowAsync(cancellationToken).ConfigureAwait(false);
            return new(true, null, created);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<AuditDestinationOperationResult> UpdateAsync(
        Guid destinationId,
        AuditDestinationDraft draft,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_registry.TryRefresh(out var refreshFailure))
                return new(false, refreshFailure);
            var prior = _registry.Snapshot().Destinations
                .FirstOrDefault(destination => destination.DestinationId == destinationId);
            if (prior is null) return new(false, "destination_not_found");
            var validationDraft = string.IsNullOrEmpty(draft.Credential)
                ? draft with { Credential = prior.Credential }
                : draft;
            var validationFailure = await _validator
                .ValidateAsync(validationDraft, cancellationToken)
                .ConfigureAwait(false);
            if (validationFailure is not null)
                return new(false, validationFailure);
            if (!_registry.TryUpdate(
                    destinationId,
                    draft,
                    DateTimeOffset.UtcNow,
                    out var updated,
                    out var failure))
            {
                return new(false, failure);
            }
            await _coordinator.SynchronizeNowAsync(cancellationToken).ConfigureAwait(false);
            return new(true, null, updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<AuditDestinationOperationResult> SetEnabledAsync(
        Guid destinationId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_registry.TryRefresh(out var refreshFailure))
                return new(false, refreshFailure);
            if (enabled)
            {
                var definition = _registry.Snapshot().Destinations
                    .FirstOrDefault(destination => destination.DestinationId == destinationId);
                if (definition is null) return new(false, "destination_not_found");
                var validationFailure = await _validator
                    .ValidateAsync(
                        new AuditDestinationDraft(
                            definition.Kind,
                            definition.OperatorLabel,
                            definition.Endpoint,
                            definition.Credential),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (validationFailure is not null)
                    return new(false, validationFailure);
            }

            var pending = !enabled && await _coordinator
                .HasPendingObligationsAsync(destinationId, cancellationToken)
                .ConfigureAwait(false);
            if (!_registry.TrySetEnabled(destinationId, enabled, pending, out var failure))
                return new(false, failure);
            await _coordinator.SynchronizeNowAsync(cancellationToken).ConfigureAwait(false);
            var changed = _registry.Snapshot().Destinations
                .FirstOrDefault(destination => destination.DestinationId == destinationId);
            return new(true, null, changed);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<AuditDestinationOperationResult> RemoveAsync(
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_registry.TryRefresh(out var refreshFailure))
                return new(false, refreshFailure);
            var pending = await _coordinator
                .HasPendingObligationsAsync(destinationId, cancellationToken)
                .ConfigureAwait(false);
            if (!_registry.TryRemove(destinationId, pending, out var failure))
                return new(false, failure);
            await _coordinator.SynchronizeNowAsync(cancellationToken).ConfigureAwait(false);
            return new(true, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<AuditDestinationOperationResult> AbandonAsync(
        Guid destinationId,
        string actor,
        string reason,
        bool remove,
        CancellationToken cancellationToken)
    {
        actor = actor?.Trim() ?? string.Empty;
        reason = reason?.Trim() ?? string.Empty;
        if (actor.Length is 0 or > MaximumActorLength)
            return new(false, "invalid_actor");
        if (reason.Length is 0 or > MaximumReasonLength)
            return new(false, "invalid_reason");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_registry.TryRefresh(out var refreshFailure))
                return new(false, refreshFailure);
            var destination = _registry.Snapshot().Destinations
                .FirstOrDefault(item => item.DestinationId == destinationId);
            if (destination is null) return new(false, "destination_not_found");
            _ = await _coordinator
                .HasPendingObligationsAsync(destinationId, cancellationToken)
                .ConfigureAwait(false);
            var status = _coordinator.Statuses()
                .First(item => item.DestinationId == destinationId);
            var cursor = new AuditExportCursorStore(
                    _options.RootDirectory,
                    AuditExportCursorStore.DestinationFileName(destinationId))
                .Read();
            if (!TryWriteAbandonment(
                    destination,
                    status.Delivery,
                    cursor,
                    actor,
                    reason,
                    remove,
                    out var writeFailure))
            {
                return new(false, writeFailure);
            }

            var changed = remove
                ? _registry.TryRemove(destinationId, hasPendingObligations: false, out var failure)
                : _registry.TrySetEnabled(
                    destinationId,
                    enabled: false,
                    hasPendingObligations: false,
                    out failure);
            if (!changed) return new(false, failure);
            await _coordinator.SynchronizeNowAsync(cancellationToken).ConfigureAwait(false);
            return new(true, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<AuditBackfillOperationResult> StartBackfillAsync(
        Guid destinationId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string actor,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_registry.TryRefresh(out var refreshFailure))
                return new(false, refreshFailure);
            if (!_registry.Snapshot().Destinations.Any(
                    destination => destination.DestinationId == destinationId))
            {
                return new(false, "destination_not_found");
            }
            if (!_backfills.TryStart(
                    destinationId,
                    fromUtc,
                    toUtc,
                    actor,
                    confirmed,
                    out var created,
                    out var failure))
            {
                return new(false, failure);
            }
            await _coordinator.SynchronizeNowAsync(cancellationToken).ConfigureAwait(false);
            return new(true, null, created);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryWriteAbandonment(
        AuditDestinationDefinition destination,
        AuditExportHealthSnapshot pending,
        AuditExportCursor cursor,
        string actor,
        string reason,
        bool remove,
        out string failure)
    {
        var recordId = Guid.NewGuid();
        var path = Path.Combine(
            _options.RootDirectory,
            $"export-abandonment-{recordId:N}.json");
        var document = new
        {
            schema_version = "ptk.export-abandonment/1",
            abandonment_id = recordId,
            destination_id = destination.DestinationId,
            operator_label = destination.OperatorLabel,
            configuration_revision = destination.ConfigurationRevision,
            recorded_utc = DateTimeOffset.UtcNow,
            actor,
            reason,
            action = remove ? "remove" : "disable",
            custody_consequence =
                "Undelivered PTK event and evidence obligations for this destination will no longer hold local journal retention.",
            undelivered = new
            {
                measurement_state = string.Equals(
                    pending.LastFailureDetail,
                    "export.pending_scan_failed",
                    StringComparison.Ordinal)
                    ? "unavailable"
                    : "complete",
                event_records = pending.PendingEventRecords,
                event_bytes = Math.Max(0, pending.PendingBytes - pending.PendingEvidenceBytes),
                evidence_records = pending.PendingEvidenceRecords,
                evidence_bytes = pending.PendingEvidenceBytes,
                oldest_pending_utc = pending.OldestPendingUtc,
                event_and_evidence_source_ranges = CaptureUndeliveredRanges(cursor),
            },
        };
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            using var stream = SecureAuditStorage.CreateExclusiveFile(path);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            SecureAuditStorage.ConfirmRetainedCreatedFileDurability(
                _options.RootDirectory,
                path,
                stream.SafeFileHandle);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure = "abandonment_unwritable";
            return false;
        }
    }

    private IReadOnlyList<UndeliveredJournalRange> CaptureUndeliveredRanges(
        AuditExportCursor cursor)
    {
        try
        {
            if (!Directory.Exists(_options.SpoolDirectory))
                return [];

            var segmentGroups = Directory.GetFiles(
                    _options.SpoolDirectory,
                    "ptk-audit-*.jsonl",
                    SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Select(file => AuditSpoolSegmentIdentity.TryParse(file.Name, out var identity)
                    ? new { File = file, Identity = identity }
                    : null)
                .Where(item => item is not null)
                .Select(item => item!)
                .GroupBy(item => item.Identity.SupervisorBootId)
                .OrderBy(group => group.Key)
                .ToArray();
            var ranges = new List<UndeliveredJournalRange>(segmentGroups.Length);
            foreach (var group in segmentGroups)
            {
                var ordered = group.OrderBy(item => item.Identity.Index).ToArray();
                var first = ordered[0];
                var position = cursor.For(group.Key);
                if (position?.SegmentFileName is not null &&
                    AuditSpoolSegmentIdentity.TryParse(
                        position.SegmentFileName,
                        out var positionIdentity))
                {
                    first = ordered.FirstOrDefault(
                        item => item.Identity.Index >= positionIdentity.Index) ?? first;
                }

                var firstOffset = position is not null && string.Equals(
                    first.File.Name,
                    position.SegmentFileName,
                    StringComparison.Ordinal)
                    ? position.ByteOffset
                    : 0;
                var last = ordered[^1];
                ranges.Add(new UndeliveredJournalRange(
                    group.Key.ToString("D"),
                    first.File.Name,
                    firstOffset,
                    last.File.Name,
                    last.File.Length,
                    position?.LastSequence ?? 0));
            }

            return ranges;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return [];
        }
    }

    private sealed record UndeliveredJournalRange(
        [property: System.Text.Json.Serialization.JsonPropertyName("supervisor_boot_id")]
        string SupervisorBootId,
        [property: System.Text.Json.Serialization.JsonPropertyName("first_undelivered_segment")]
        string FirstUndeliveredSegment,
        [property: System.Text.Json.Serialization.JsonPropertyName("first_undelivered_offset")]
        long FirstUndeliveredOffset,
        [property: System.Text.Json.Serialization.JsonPropertyName("observed_through_segment")]
        string ObservedThroughSegment,
        [property: System.Text.Json.Serialization.JsonPropertyName("observed_through_offset")]
        long ObservedThroughOffset,
        [property: System.Text.Json.Serialization.JsonPropertyName("acknowledged_through_sequence")]
        long AcknowledgedThroughSequence);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
