using System.Text.Json;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Audit;

/// <summary>
/// Conservative segment retention floor across every enabled destination and
/// active bounded backfill. One missing or unreadable required cursor retains
/// all segments; acknowledgment by one destination never releases another.
/// </summary>
internal static class ExportRetentionFloor
{
    private const string LegacyCursorFileName = "export-cursor.json";
    private const int MaximumFileBytes = 256 * 1024;

    internal sealed record BootFloor(int SegmentIndex, bool Terminal);

    internal static IReadOnlyDictionary<Guid, BootFloor>? ReadFloors(
        string auditRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(auditRootDirectory)) return null;
        try
        {
            var cursorPaths = RequiredCursorPaths(auditRootDirectory, out var configured);
            if (!configured)
            {
                var legacy = Path.Combine(auditRootDirectory, LegacyCursorFileName);
                return File.Exists(legacy) ? ReadCursorFloors(legacy) : null;
            }
            if (cursorPaths.Count == 0) return null;

            var sources = new List<IReadOnlyDictionary<Guid, BootFloor>>();
            foreach (var path in cursorPaths)
            {
                if (!File.Exists(path)) return RetainEverything();
                var floors = ReadCursorFloors(path);
                if (floors is null) return RetainEverything();
                sources.Add(floors);
            }
            return Combine(sources);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Destination authority exists but cannot be proved readable:
            // fail retention closed, not delivery open.
            return File.Exists(Path.Combine(auditRootDirectory, AuditDestinationRegistry.FileName))
                ? RetainEverything()
                : null;
        }
    }

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

    private static IReadOnlyList<string> RequiredCursorPaths(
        string root,
        out bool configured)
    {
        var destinationPath = Path.Combine(root, AuditDestinationRegistry.FileName);
        configured = File.Exists(destinationPath);
        if (!configured) return [];

        var bytes = SecureAuditStorage.ReadProtectedFile(
            destinationPath,
            MaximumFileBytes,
            requireProtectedParent: true);
        using var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("destinations", out var destinations) ||
            destinations.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Destination authority has no destination set.");
        }

        var paths = new List<string>();
        foreach (var destination in destinations.EnumerateArray())
        {
            if (!destination.TryGetProperty("enabled", out var enabled) ||
                enabled.ValueKind != JsonValueKind.True)
            {
                continue;
            }
            if (!destination.TryGetProperty("destination_id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(idElement.GetString(), out var destinationId))
            {
                throw new InvalidDataException("Enabled destination identity is invalid.");
            }
            paths.Add(Path.Combine(
                root,
                AuditExportCursorStore.DestinationFileName(destinationId)));
        }

        foreach (var backfill in new AuditBackfillRegistry(root).ReadAll()
                     .Where(item => item.State == AuditBackfillState.Active))
        {
            paths.Add(Path.Combine(
                root,
                $"export-backfill-cursor-{backfill.BackfillId:N}.json"));
        }
        return paths.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyDictionary<Guid, BootFloor>? ReadCursorFloors(string path)
    {
        try
        {
            var bytes = SecureAuditStorage.ReadProtectedFile(
                path,
                MaximumFileBytes,
                requireProtectedParent: true);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var floors = new Dictionary<Guid, BootFloor>();
            if (root.TryGetProperty("boots", out var boots) &&
                boots.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in boots.EnumerateObject())
                {
                    if (!Guid.TryParseExact(entry.Name, "D", out var bootId) ||
                        entry.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var terminal = entry.Value.TryGetProperty("terminal", out var terminalElement) &&
                        terminalElement.ValueKind == JsonValueKind.True;
                    var segmentIndex = 0;
                    if (entry.Value.TryGetProperty("segment", out var segmentElement) &&
                        segmentElement.ValueKind == JsonValueKind.String &&
                        AuditSpoolSegmentIdentity.TryParse(
                            segmentElement.GetString(),
                            out var identity) &&
                        identity.SupervisorBootId == bootId)
                    {
                        segmentIndex = identity.Index;
                    }
                    floors[bootId] = new BootFloor(segmentIndex, terminal);
                }
            }
            if (root.TryGetProperty("segment", out var legacySegment) &&
                legacySegment.ValueKind == JsonValueKind.String &&
                AuditSpoolSegmentIdentity.TryParse(legacySegment.GetString(), out var legacy))
            {
                floors[legacy.SupervisorBootId] = new BootFloor(legacy.Index, Terminal: false);
            }
            return floors;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<Guid, BootFloor> Combine(
        IReadOnlyList<IReadOnlyDictionary<Guid, BootFloor>> sources)
    {
        if (sources.Count == 0) return RetainEverything();
        var boots = sources.SelectMany(source => source.Keys).Distinct().ToArray();
        var combined = new Dictionary<Guid, BootFloor>();
        foreach (var boot in boots)
        {
            if (sources.Any(source => !source.ContainsKey(boot)))
            {
                combined[boot] = new BootFloor(0, Terminal: false);
                continue;
            }
            var floors = sources.Select(source => source[boot]).ToArray();
            var terminal = floors.All(floor => floor.Terminal);
            combined[boot] = terminal
                ? new BootFloor(0, Terminal: true)
                : new BootFloor(
                    floors.Where(floor => !floor.Terminal)
                        .Select(floor => floor.SegmentIndex)
                        .DefaultIfEmpty(0)
                        .Min(),
                    Terminal: false);
        }
        return combined;
    }

    private static IReadOnlyDictionary<Guid, BootFloor> RetainEverything() =>
        new Dictionary<Guid, BootFloor>();

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
