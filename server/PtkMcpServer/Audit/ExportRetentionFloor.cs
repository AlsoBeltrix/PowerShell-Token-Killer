using System.Text.Json;

namespace PtkMcpServer.Audit;

/// <summary>
/// What the exporter still needs from the spool, PER SUPERVISOR BOOT.
/// Journal retention consults this so ordinary age-based cleanup never
/// destroys records that were never exported (audit-restoration R3d).
///
/// The cr4-4 reopen retired the single-segment floor: any cross-boot
/// ordering keyed on the remaining files mutates when delivered segments are
/// deleted, and a mutated order both hid undelivered segments from the
/// exporter and exposed them to retention. Per-boot floors are
/// order-independent: a boot with no recorded delivery keeps everything, a
/// boot with a recorded position keeps its position's segment and later
/// ones, and a boot whose lifecycle terminal was delivered needs nothing.
///
/// Read directly from the export cursor rather than wired through the
/// construction chain: both components are audit subsystem parts in one
/// assembly and one process, and a missing, unreadable, or stale cursor
/// simply yields "no floor", which is exactly the pre-R3d behaviour. The
/// journal must never fail because the exporter's bookkeeping is unavailable.
/// </summary>
internal static class ExportRetentionFloor
{
    private const string CursorFileName = "export-cursor.json";
    private const int MaximumFileBytes = 64 * 1024;

    /// <summary>One boot's floor: delivery stands at (or within) the segment
    /// with this index; earlier segments are fully delivered.</summary>
    internal sealed record BootFloor(int SegmentIndex, bool Terminal);

    /// <summary>
    /// The per-boot floors, or null when there is no usable cursor — callers
    /// treat null as "retain nothing extra" (export has never run here).
    /// </summary>
    internal static IReadOnlyDictionary<Guid, BootFloor>? ReadFloors(string auditRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(auditRootDirectory)) return null;
        try
        {
            var path = Path.Combine(auditRootDirectory, CursorFileName);
            if (!File.Exists(path)) return null;
            var bytes = SecureAuditStorage.ReadProtectedFile(
                path,
                MaximumFileBytes,
                requireProtectedParent: false,
                verifyWithoutMutation: true);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var floors = new Dictionary<Guid, BootFloor>();
            if (root.TryGetProperty("boots", out var boots) &&
                boots.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in boots.EnumerateObject())
                {
                    if (!Guid.TryParseExact(entry.Name, "D", out var bootId)) continue;
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    var terminal =
                        entry.Value.TryGetProperty("terminal", out var terminalElement) &&
                        terminalElement.ValueKind == JsonValueKind.True;
                    int segmentIndex = 0;
                    if (entry.Value.TryGetProperty("segment", out var segmentElement) &&
                        segmentElement.ValueKind == JsonValueKind.String &&
                        AuditSpoolSegmentIdentity.TryParse(segmentElement.GetString(), out var identity) &&
                        identity.SupervisorBootId == bootId)
                    {
                        segmentIndex = identity.Index;
                    }
                    floors[bootId] = new BootFloor(segmentIndex, terminal);
                }
            }
            else if (root.TryGetProperty("segment", out var legacySegment) &&
                     legacySegment.ValueKind == JsonValueKind.String &&
                     AuditSpoolSegmentIdentity.TryParse(legacySegment.GetString(), out var legacy))
            {
                // Version-1 cursor: its one position becomes that boot's floor.
                floors[legacy.SupervisorBootId] = new BootFloor(legacy.Index, Terminal: false);
            }

            return floors;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a closed segment is still needed by the exporter. With no
    /// floors at all (export never ran), nothing extra is retained. With
    /// floors: a boot the exporter has never recorded is wholly retained
    /// (undelivered), a boot with a position retains its floor segment and
    /// everything after it, and a boot whose lifecycle terminal was delivered
    /// retains nothing (its chain is complete — nothing appends after
    /// server.stopped).
    /// </summary>
    internal static bool IsRequired(
        string segmentFileName,
        IReadOnlyDictionary<Guid, BootFloor>? floors)
    {
        if (floors is null) return false;
        if (!AuditSpoolSegmentIdentity.TryParse(segmentFileName, out var segment))
            return false;
        if (!floors.TryGetValue(segment.SupervisorBootId, out var floor))
            return true;
        if (floor.Terminal) return false;
        return segment.Index >= floor.SegmentIndex;
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
