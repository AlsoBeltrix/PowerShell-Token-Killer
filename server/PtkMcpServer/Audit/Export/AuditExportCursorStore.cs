using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PtkMcpServer.Audit.Export;

/// <summary>How far delivery has durably progressed through the spool.</summary>
internal sealed record AuditExportCursor(string? SegmentFileName, long ByteOffset)
{
    internal static AuditExportCursor Start { get; } = new(null, 0);
}

/// <summary>
/// Owner-only durable record of the export position. Advanced only AFTER a
/// destination accepted the batch, so a crash re-delivers rather than skips
/// (at-least-once; the receiver is idempotent). A cursor that cannot be
/// persisted never blocks execution — it costs re-delivery, nothing more.
/// </summary>
internal sealed class AuditExportCursorStore
{
    internal const string FileName = "export-cursor.json";
    private const int MaximumFileBytes = 8 * 1024;

    private readonly string _path;
    private readonly string _directory;

    internal AuditExportCursorStore(string auditRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditRootDirectory);
        _directory = auditRootDirectory;
        _path = Path.Combine(auditRootDirectory, FileName);
    }

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
            if (file is null || file.ByteOffset < 0) return AuditExportCursor.Start;
            return new AuditExportCursor(file.SegmentFileName, file.ByteOffset);
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
                SegmentFileName = cursor.SegmentFileName,
                ByteOffset = cursor.ByteOffset,
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

    private sealed class CursorFile
    {
        [JsonPropertyName("segment")] public string? SegmentFileName { get; set; }
        [JsonPropertyName("offset")] public long ByteOffset { get; set; }
    }
}
