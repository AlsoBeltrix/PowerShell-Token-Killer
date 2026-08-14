using System.Text.Json;
using System.Text.Json.Serialization;

namespace PtkMcpServer.Audit.Export;

/// <summary>
/// One boot's durable delivery state: the last segment delivery touched, the
/// consumed byte offset within it, and the last delivered record's chain
/// position.
///
/// The chain position is what makes loss detectable: file bookkeeping cannot
/// distinguish "this segment was deleted after everything in it was
/// delivered" from "a tail was appended, rotated and deleted before the next
/// drain" (both cr3-2 verification rounds). Audit records carry a per-boot
/// contiguous <c>sequence</c>, so comparing the next record's sequence with
/// the last delivered one proves whether anything was lost, whatever
/// retention did to the files.
/// </summary>
internal sealed record AuditExportBootPosition(
    string? SegmentFileName,
    long ByteOffset,
    long LastSequence,
    bool LastWasLifecycleTerminal,
    DateTimeOffset TouchedUtc,
    // The predecessor boot this boot's records attest (boot lineage). Held
    // DURABLY so the attestation outlives the drain that read it: a claim
    // judged "pending" while the predecessor was merely blocked must still
    // raise its boundary when the predecessor's tail later vanishes
    // (cr4-4, second frontier round).
    string? PreviousBootId = null);

/// <summary>
/// How far delivery has durably progressed, PER SUPERVISOR BOOT. The cr4-4
/// reopen proved a single linear position cannot survive concurrent boots:
/// any total order keyed on the remaining files mutates when retention
/// deletes delivered segments, and a mutated order lets the one cursor both
/// skip and expose undelivered segments. Per-boot positions are
/// order-independent by construction.
/// </summary>
internal sealed record AuditExportCursor(
    IReadOnlyDictionary<string, AuditExportBootPosition> Boots,
    long UnrecordedGaps = 0,
    long UnrecordedMissingRecords = 0)
{
    internal static AuditExportCursor Start { get; } =
        new(new Dictionary<string, AuditExportBootPosition>(StringComparer.Ordinal));

    internal AuditExportBootPosition? For(Guid supervisorBootId) =>
        Boots.TryGetValue(supervisorBootId.ToString("D"), out var position)
            ? position
            : null;

    internal AuditExportCursor WithBoot(Guid supervisorBootId, AuditExportBootPosition position)
    {
        var boots = new Dictionary<string, AuditExportBootPosition>(Boots, StringComparer.Ordinal)
        {
            [supervisorBootId.ToString("D")] = position,
        };
        return this with { Boots = Bound(boots) };
    }

    /// <summary>Bounded map: terminal (finished) boots are evicted first,
    /// oldest-touched first; a non-terminal entry guards an undelivered
    /// floor and is only evicted under genuine overflow.</summary>
    private static Dictionary<string, AuditExportBootPosition> Bound(
        Dictionary<string, AuditExportBootPosition> boots)
    {
        const int maximumBoots = 64;
        while (boots.Count > maximumBoots)
        {
            var victim = boots
                .OrderByDescending(entry => entry.Value.LastWasLifecycleTerminal)
                .ThenBy(entry => entry.Value.TouchedUtc)
                .First();
            boots.Remove(victim.Key);
        }
        return boots;
    }
}

/// <summary>
/// Owner-only durable record of the export position. Advanced only AFTER a
/// destination accepted the batch, so a crash re-delivers rather than skips
/// (at-least-once; the receiver is idempotent). A cursor that cannot be
/// persisted never blocks execution — it costs re-delivery, nothing more.
/// A version-1 (single-position) cursor is migrated on read.
/// </summary>
internal sealed class AuditExportCursorStore
{
    internal const string FileName = "export-cursor.json";
    private const int MaximumFileBytes = 64 * 1024;

    private readonly string _path;
    private readonly string _directory;

    internal AuditExportCursorStore(
        string auditRootDirectory,
        string? fileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditRootDirectory);
        _directory = auditRootDirectory;
        _path = Path.Combine(auditRootDirectory, ValidateFileName(fileName ?? FileName));
    }

    internal static string DestinationFileName(Guid destinationId) =>
        $"export-cursor-{destinationId:N}.json";

    internal string CursorPath => _path;

    internal AuditExportCursor Read()
    {
        try
        {
            if (!File.Exists(_path)) return AuditExportCursor.Start;
            var bytes = SecureAuditStorage.ReadProtectedFile(
                _path,
                MaximumFileBytes,
                requireProtectedParent: false,
                verifyWithoutMutation: true);
            var file = JsonSerializer.Deserialize<CursorFile>(bytes);
            if (file is null) return AuditExportCursor.Start;

            var boots = new Dictionary<string, AuditExportBootPosition>(StringComparer.Ordinal);
            if (file.Boots is not null)
            {
                foreach (var (key, entry) in file.Boots)
                {
                    if (entry is null || entry.ByteOffset < 0) continue;
                    if (!Guid.TryParseExact(key, "D", out _)) continue;
                    boots[key] = new AuditExportBootPosition(
                        entry.SegmentFileName,
                        entry.ByteOffset,
                        entry.LastSequence,
                        entry.LastWasLifecycleTerminal,
                        entry.TouchedUtc ?? DateTimeOffset.UnixEpoch,
                        entry.PreviousBootId);
                }
            }
            else if (file.SegmentFileName is not null || file.LastSupervisorBootId is not null)
            {
                // Version-1 migration: one linear position becomes that
                // segment's boot's position; a chain memory recorded against
                // a different boot becomes a chain-only entry.
                if (file.ByteOffset < 0) return AuditExportCursor.Start;
                if (file.SegmentFileName is not null &&
                    AuditSpoolSegmentIdentity.TryParse(file.SegmentFileName, out var identity))
                {
                    var chainMatches = string.Equals(
                        file.LastSupervisorBootId,
                        identity.SupervisorBootId.ToString("D"),
                        StringComparison.Ordinal);
                    boots[identity.SupervisorBootId.ToString("D")] = new AuditExportBootPosition(
                        file.SegmentFileName,
                        file.ByteOffset,
                        chainMatches ? file.LastSequence : 0,
                        chainMatches && file.LastWasLifecycleTerminal,
                        DateTimeOffset.UnixEpoch);
                }
                if (file.LastSupervisorBootId is not null &&
                    Guid.TryParseExact(file.LastSupervisorBootId, "D", out _) &&
                    !boots.ContainsKey(file.LastSupervisorBootId))
                {
                    boots[file.LastSupervisorBootId] = new AuditExportBootPosition(
                        SegmentFileName: null,
                        ByteOffset: 0,
                        file.LastSequence,
                        file.LastWasLifecycleTerminal,
                        DateTimeOffset.UnixEpoch);
                }
            }

            return new AuditExportCursor(
                boots,
                Math.Max(0, file.UnrecordedGaps),
                Math.Max(0, file.UnrecordedMissingRecords));
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // An unreadable cursor re-delivers from the oldest retained
            // segment: duplicates are contractually fine, gaps are not.
            return AuditExportCursor.Start;
        }
    }

    internal bool TryWrite(AuditExportCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var temporaryPath = Path.Combine(
            _directory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new CursorFile
            {
                Version = 2,
                Boots = cursor.Boots.ToDictionary(
                    entry => entry.Key,
                entry => (BootFile?)new BootFile
                    {
                        SegmentFileName = entry.Value.SegmentFileName,
                        ByteOffset = entry.Value.ByteOffset,
                        LastSequence = entry.Value.LastSequence,
                        LastWasLifecycleTerminal = entry.Value.LastWasLifecycleTerminal,
                        TouchedUtc = entry.Value.TouchedUtc,
                        PreviousBootId = entry.Value.PreviousBootId,
                    },
                    StringComparer.Ordinal),
                UnrecordedGaps = cursor.UnrecordedGaps,
                UnrecordedMissingRecords = cursor.UnrecordedMissingRecords,
            });
            using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            // Atomic overwrite, not PublishAtomically: that helper refuses an
            // existing destination (single-publish semantics for identity
            // artifacts), while the cursor is rewritten after every batch.
            // The temporary file is created owner-only inside the already
            // protected audit root, and the rename carries those permissions.
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            SecureAuditStorage.TryDelete(temporaryPath);
            return false;
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static string ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName.Length > 128)
        {
            throw new ArgumentException("Export cursor file name is invalid.", nameof(fileName));
        }
        return fileName;
    }

    private sealed class CursorFile
    {
        [JsonPropertyName("version")] public int? Version { get; set; }
        [JsonPropertyName("boots")] public Dictionary<string, BootFile?>? Boots { get; set; }
        // Version-1 fields, read for migration only.
        [JsonPropertyName("segment")] public string? SegmentFileName { get; set; }
        [JsonPropertyName("offset")] public long ByteOffset { get; set; }
        [JsonPropertyName("boot")] public string? LastSupervisorBootId { get; set; }
        [JsonPropertyName("sequence")] public long LastSequence { get; set; }
        [JsonPropertyName("terminal")] public bool LastWasLifecycleTerminal { get; set; }
        // Gaps whose durable ledger write failed: parked here so the evidence
        // survives a restart even when only the ledger is unwritable.
        [JsonPropertyName("unrecorded_gaps")] public long UnrecordedGaps { get; set; }
        [JsonPropertyName("unrecorded_missing")] public long UnrecordedMissingRecords { get; set; }
    }

    private sealed class BootFile
    {
        [JsonPropertyName("segment")] public string? SegmentFileName { get; set; }
        [JsonPropertyName("offset")] public long ByteOffset { get; set; }
        [JsonPropertyName("sequence")] public long LastSequence { get; set; }
        [JsonPropertyName("terminal")] public bool LastWasLifecycleTerminal { get; set; }
        [JsonPropertyName("touched")] public DateTimeOffset? TouchedUtc { get; set; }
        [JsonPropertyName("previous")] public string? PreviousBootId { get; set; }
    }
}
