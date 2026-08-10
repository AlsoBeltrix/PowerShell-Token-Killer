using System.Text.Json;
using System.Text.Json.Serialization;

namespace PtkMcpServer.Audit.Export;

/// <summary>
/// Durable record of segments local retention deleted before they were
/// delivered — permanently lost custody at the destination.
///
/// This is on disk, not in memory, because a gap is evidence: process-local
/// state would erase the only trace of the loss at the next restart (cr3-2
/// verification). Bounded to <see cref="MaximumRetainedSegments"/> names; the
/// count keeps growing after the names stop being retained.
/// </summary>
internal sealed class AuditExportGapStore
{
    internal const string FileName = "export-gaps.json";
    internal const int MaximumRetainedSegments = 64;
    private const int MaximumFileBytes = 32 * 1024;

    private readonly string _path;
    private readonly string _directory;
    private readonly object _gate = new();

    internal AuditExportGapStore(string auditRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditRootDirectory);
        _directory = auditRootDirectory;
        _path = Path.Combine(auditRootDirectory, FileName);
    }

    internal AuditExportGapRecord Read()
    {
        lock (_gate)
            return ReadLocked();
    }

    /// <summary>
    /// Records one lost segment. Returns the resulting durable record. A
    /// segment already recorded is not counted twice, so a repeating drain
    /// cannot inflate the number.
    /// </summary>
    internal AuditExportGapRecord Record(string segmentFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentFileName);
        lock (_gate)
        {
            var current = ReadLocked();
            if (current.Segments.Contains(segmentFileName, StringComparer.Ordinal))
                return current;

            var segments = current.Segments
                .Append(segmentFileName)
                .TakeLast(MaximumRetainedSegments)
                .ToArray();
            var updated = new AuditExportGapRecord(current.Count + 1, segments);
            TryWriteLocked(updated);
            return updated;
        }
    }

    private AuditExportGapRecord ReadLocked()
    {
        try
        {
            if (!File.Exists(_path)) return AuditExportGapRecord.Empty;
            var bytes = SecureAuditStorage.ReadProtectedFile(
                _path,
                MaximumFileBytes,
                requireProtectedParent: false,
                verifyWithoutMutation: true);
            var file = JsonSerializer.Deserialize<GapFile>(bytes);
            if (file is null || file.Count < 0) return AuditExportGapRecord.Empty;
            return new AuditExportGapRecord(file.Count, file.Segments ?? []);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return AuditExportGapRecord.Empty;
        }
    }

    private void TryWriteLocked(AuditExportGapRecord record)
    {
        var temporaryPath = Path.Combine(
            _directory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new GapFile
            {
                Count = record.Count,
                Segments = record.Segments.ToArray(),
            });
            using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Losing the durable note must not stop execution or delivery;
            // the in-memory health line still reports the gap for this
            // process.
            SecureAuditStorage.TryDelete(temporaryPath);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed class GapFile
    {
        [JsonPropertyName("count")] public long Count { get; set; }
        [JsonPropertyName("segments")] public string[]? Segments { get; set; }
    }
}

internal sealed record AuditExportGapRecord(long Count, IReadOnlyList<string> Segments)
{
    internal static AuditExportGapRecord Empty { get; } = new(0, []);
}
