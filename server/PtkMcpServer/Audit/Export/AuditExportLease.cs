namespace PtkMcpServer.Audit.Export;

/// <summary>
/// Cross-process single-exporter lease (cr4-4). Every supervisor on a shared
/// audit root runs an export service, but exactly one may drain: the durable
/// cursor and gap ledger are single-writer artifacts, and concurrent
/// tmp+rename writers would silently regress each other's position and
/// evidence. Acquisition is non-blocking — a standby exporter reports itself
/// and retries on its next pump tick; it never gates execution and never
/// waits.
/// </summary>
internal sealed class AuditExportLease : IDisposable
{
    internal const string FileName = "export-lease.lock";

    private FileStream? _stream;

    internal bool IsHeld => _stream is not null;

    /// <summary>Idempotent, non-blocking. True when this process holds (or
    /// already held) the lease.</summary>
    internal bool TryAcquire(string auditRootDirectory)
    {
        if (_stream is not null) return true;
        try
        {
            _stream = new FileStream(
                Path.Combine(auditRootDirectory, FileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
