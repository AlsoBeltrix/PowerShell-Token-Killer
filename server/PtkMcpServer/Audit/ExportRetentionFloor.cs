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
    /// own segment and anything ordered after it in DELIVERY order.
    /// Comparison is by the segment's canonical (boot, index) identity within
    /// a boot; across boots, delivery order is boot groups ordered by their
    /// earliest segment's creation time (matching the exporter's traversal,
    /// cr4-4), supplied by the caller from its own directory listing — a
    /// different boot is NOT "already delivered or already lost" when
    /// supervisors share the root concurrently. Without a supplied group
    /// ordering only the cursor's own boot is protected (the pre-cr4-4
    /// behaviour).
    /// </summary>
    internal static bool IsRequired(
        string segmentFileName,
        string? floorFileName,
        Func<Guid, DateTime?>? bootGroupEarliestCreationUtc = null)
    {
        if (floorFileName is null) return false;
        if (string.Equals(segmentFileName, floorFileName, StringComparison.Ordinal))
            return true;
        if (!AuditSpoolSegmentIdentity.TryParse(segmentFileName, out var segment) ||
            !AuditSpoolSegmentIdentity.TryParse(floorFileName, out var floor))
        {
            return false;
        }

        if (segment.SupervisorBootId == floor.SupervisorBootId)
            return segment.Index >= floor.Index;
        if (bootGroupEarliestCreationUtc is null) return false;

        var candidateGroup = bootGroupEarliestCreationUtc(segment.SupervisorBootId);
        var floorGroup = bootGroupEarliestCreationUtc(floor.SupervisorBootId);
        // Unknown ordering is conservative: keep the bytes.
        if (candidateGroup is null || floorGroup is null) return true;
        // Only a boot group STRICTLY before the cursor's group has been
        // traversed by delivery; everything at or after it may still be
        // undelivered.
        return candidateGroup >= floorGroup;
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
