using System.Text.Json;
using System.Text.Json.Serialization;

namespace PtkMcpServer.Audit.Export;

internal enum AuditBackfillState
{
    Active,
    Completed,
    Failed,
}

internal sealed record AuditBackfillDefinition(
    Guid BackfillId,
    Guid DestinationId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset CreatedUtc,
    string Actor,
    AuditBackfillState State,
    DateTimeOffset? CompletedUtc = null,
    string? Failure = null);

/// <summary>One durable, explicitly ranged backfill state per destination.</summary>
internal sealed class AuditBackfillRegistry
{
    private const int CurrentVersion = 1;
    private const int MaximumFileBytes = 64 * 1024;
    private readonly object _gate = new();
    private readonly string _root;

    internal AuditBackfillRegistry(string auditRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditRootDirectory);
        _root = auditRootDirectory;
    }

    internal IReadOnlyList<AuditBackfillDefinition> ReadAll()
    {
        lock (_gate)
        {
            var result = new List<AuditBackfillDefinition>();
            try
            {
                foreach (var path in Directory.GetFiles(
                             _root,
                             "export-backfill-*.json",
                             SearchOption.TopDirectoryOnly))
                {
                    var item = Read(path);
                    if (item is not null) result.Add(item);
                }
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                return [];
            }
            return result
                .OrderBy(item => item.DestinationId)
                .ToArray();
        }
    }

    internal AuditBackfillDefinition? ForDestination(Guid destinationId)
    {
        lock (_gate) return Read(PathFor(destinationId));
    }

    internal bool TryStart(
        Guid destinationId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string actor,
        bool confirmed,
        out AuditBackfillDefinition? created,
        out string failure)
    {
        created = null;
        actor = actor?.Trim() ?? string.Empty;
        fromUtc = fromUtc.ToUniversalTime();
        toUtc = toUtc.ToUniversalTime();
        if (!confirmed)
        {
            failure = "backfill_confirmation_required";
            return false;
        }
        if (actor.Length is 0 or > 256)
        {
            failure = "invalid_actor";
            return false;
        }
        if (fromUtc >= toUtc)
        {
            failure = "invalid_backfill_range";
            return false;
        }

        lock (_gate)
        {
            var prior = Read(PathFor(destinationId));
            if (prior?.State == AuditBackfillState.Active)
            {
                failure = "backfill_already_active";
                return false;
            }
            created = new AuditBackfillDefinition(
                Guid.NewGuid(),
                destinationId,
                fromUtc,
                toUtc,
                DateTimeOffset.UtcNow,
                actor,
                AuditBackfillState.Active);
            if (!TryWrite(created))
            {
                created = null;
                failure = "backfill_state_unwritable";
                return false;
            }
            failure = string.Empty;
            return true;
        }
    }

    internal bool TryComplete(Guid destinationId, DateTimeOffset completedUtc)
    {
        lock (_gate)
        {
            var prior = Read(PathFor(destinationId));
            if (prior is null || prior.State != AuditBackfillState.Active) return false;
            return TryWrite(prior with
            {
                State = AuditBackfillState.Completed,
                CompletedUtc = completedUtc.ToUniversalTime(),
            });
        }
    }

    internal bool TryFail(Guid destinationId, string failure)
    {
        lock (_gate)
        {
            var prior = Read(PathFor(destinationId));
            if (prior is null || prior.State != AuditBackfillState.Active) return false;
            return TryWrite(prior with
            {
                State = AuditBackfillState.Failed,
                Failure = failure,
                CompletedUtc = DateTimeOffset.UtcNow,
            });
        }
    }

    private AuditBackfillDefinition? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = SecureAuditStorage.ReadProtectedFile(
                path,
                MaximumFileBytes,
                requireProtectedParent: true);
            var file = JsonSerializer.Deserialize<BackfillFile>(bytes);
            if (file is null || file.Version != CurrentVersion ||
                file.BackfillId == Guid.Empty || file.DestinationId == Guid.Empty ||
                file.FromUtc >= file.ToUtc || string.IsNullOrWhiteSpace(file.Actor) ||
                !Enum.TryParse<AuditBackfillState>(file.State, ignoreCase: true, out var state))
            {
                return null;
            }
            return new AuditBackfillDefinition(
                file.BackfillId,
                file.DestinationId,
                file.FromUtc.ToUniversalTime(),
                file.ToUtc.ToUniversalTime(),
                file.CreatedUtc.ToUniversalTime(),
                file.Actor,
                state,
                file.CompletedUtc?.ToUniversalTime(),
                file.Failure);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return null;
        }
    }

    private bool TryWrite(AuditBackfillDefinition definition)
    {
        var path = PathFor(definition.DestinationId);
        var temporary = Path.Combine(
            _root,
            $".export-backfill-{definition.DestinationId:N}-{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new BackfillFile
            {
                Version = CurrentVersion,
                BackfillId = definition.BackfillId,
                DestinationId = definition.DestinationId,
                FromUtc = definition.FromUtc,
                ToUtc = definition.ToUtc,
                CreatedUtc = definition.CreatedUtc,
                Actor = definition.Actor,
                State = definition.State.ToString().ToLowerInvariant(),
                CompletedUtc = definition.CompletedUtc,
                Failure = definition.Failure,
            });
            using (var stream = SecureAuditStorage.CreateExclusiveFile(temporary))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                SecureAuditStorage.ReplaceAtomically(temporary, path, _root);
            }
            else
            {
                SecureAuditStorage.PublishAtomically(temporary, path, _root);
            }
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (!IsFatal(cleanup)) { }
            return false;
        }
    }

    private string PathFor(Guid destinationId) =>
        Path.Combine(_root, $"export-backfill-{destinationId:N}.json");

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed class BackfillFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }
        [JsonPropertyName("backfill_id")]
        public Guid BackfillId { get; set; }
        [JsonPropertyName("destination_id")]
        public Guid DestinationId { get; set; }
        [JsonPropertyName("from_utc")]
        public DateTimeOffset FromUtc { get; set; }
        [JsonPropertyName("to_utc")]
        public DateTimeOffset ToUtc { get; set; }
        [JsonPropertyName("created_utc")]
        public DateTimeOffset CreatedUtc { get; set; }
        [JsonPropertyName("actor")]
        public string? Actor { get; set; }
        [JsonPropertyName("state")]
        public string? State { get; set; }
        [JsonPropertyName("completed_utc")]
        public DateTimeOffset? CompletedUtc { get; set; }
        [JsonPropertyName("failure")]
        public string? Failure { get; set; }
    }
}
