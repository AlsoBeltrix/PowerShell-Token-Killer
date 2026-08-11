using System.Text.Json;

namespace PtkMcpServer.Audit;

/// <summary>
/// The oldest spool segment the exporter still needs — everything strictly
/// older has been delivered. Journal retention consults this so ordinary
/// age-based cleanup never destroys records that were never exported
/// (audit-restoration R3d).
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
    private const int MaximumFileBytes = 8 * 1024;

    /// <summary>
    /// The segment file name at which delivery stands, or null when there is
    /// no usable cursor. Callers treat null as "retain nothing extra".
    /// </summary>
    internal static string? ReadOldestRequiredSegment(string auditRootDirectory)
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
            if (!document.RootElement.TryGetProperty("segment", out var segment) ||
                segment.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var name = segment.GetString();
            return AuditSpoolSegmentIdentity.TryParse(name, out _) ? name : null;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a closed segment is still needed by the exporter: the cursor's
    /// own segment and anything ordered after it. Comparison is by the
    /// segment's canonical (boot, index) identity, falling back to an ordinal
    /// name comparison only when the floor cannot be parsed.
    /// </summary>
    internal static bool IsRequired(string segmentFileName, string? floorFileName)
    {
        if (floorFileName is null) return false;
        if (string.Equals(segmentFileName, floorFileName, StringComparison.Ordinal))
            return true;
        if (!AuditSpoolSegmentIdentity.TryParse(segmentFileName, out var segment) ||
            !AuditSpoolSegmentIdentity.TryParse(floorFileName, out var floor))
        {
            return false;
        }

        // Within one supervisor boot the index orders the chain. Across boots
        // there is no ordering, so only the cursor's own boot is protected;
        // an older boot's segments are already delivered or already lost.
        return segment.SupervisorBootId == floor.SupervisorBootId &&
               segment.Index >= floor.Index;
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
