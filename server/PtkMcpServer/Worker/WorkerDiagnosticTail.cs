using System.Text;

namespace PtkMcpServer.Worker;

/// <summary>
/// Retains the last bounded slice of a worker's standard error so an abnormal
/// exit can name its own cause. The worker writes exactly one bounded ASCII
/// diagnostic line before it dies
/// (<see cref="WorkerProcessExit.MaximumDiagnosticBytes"/>); everything else on
/// that stream is unexpected, so this keeps a rolling tail rather than a
/// growing buffer and never lets the volume of worker output bound the
/// supervisor's memory.
/// </summary>
/// <remarks>
/// This runs only on the path where something has already gone wrong, so no
/// member throws: an unreadable or malformed tail reports absent, and the
/// caller falls back to the transport-level detail it would have reported
/// before this type existed.
/// </remarks>
internal sealed class WorkerDiagnosticTail
{
    private readonly object _gate = new();
    private readonly byte[] _tail =
        new byte[WorkerProcessExit.MaximumDiagnosticBytes];
    private int _length;

    /// <summary>
    /// Reads <paramref name="stream"/> to completion, retaining only its final
    /// bytes. Replaces a plain discard drain: the stream must still be
    /// consumed or the worker can block writing to a full pipe.
    /// </summary>
    internal async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                Append(buffer.AsSpan(0, read));
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // A failed read is itself a symptom of the death being diagnosed.
            // Keep whatever arrived before it and stop.
        }
    }

    /// <summary>
    /// The worker's final standard-error line, or <see langword="null"/> when
    /// nothing usable was retained. The worker writes its exit diagnostic last,
    /// so only the final line is reported — earlier lines may be anything the
    /// executed command left behind.
    /// </summary>
    /// <remarks>
    /// The reported line must be printable ASCII, checked on the raw bytes:
    /// decoding first would fold every non-ASCII byte to '?' and pass a guard
    /// meant to catch exactly that. Anything else means this is not the
    /// worker's own diagnostic, and surfacing it would put arbitrary executed
    /// output inside a supervisor-authored failure line.
    /// </remarks>
    internal string? Text
    {
        get
        {
            byte[] snapshot;
            lock (_gate)
            {
                if (_length == 0)
                    return null;
                snapshot = _tail[.._length];
            }

            var end = snapshot.Length;
            while (end > 0 && IsTrimmable(snapshot[end - 1]))
                end--;
            var start = end;
            while (start > 0 && snapshot[start - 1] != (byte)'\n')
                start--;
            while (start < end && IsTrimmable(snapshot[start]))
                start++;
            if (start >= end)
                return null;

            for (var index = start; index < end; index++)
            {
                if (snapshot[index] < 0x20 || snapshot[index] > 0x7e)
                    return null;
            }
            return Encoding.ASCII.GetString(snapshot, start, end - start);
        }
    }

    private static bool IsTrimmable(byte value) =>
        value is (byte)'\r' or (byte)'\n' or (byte)' ';

    private void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            // Only the final MaximumDiagnosticBytes can matter: a longer write
            // means something other than the worker's exit diagnostic is on
            // this stream, and the tail is still where the last word is.
            if (bytes.Length >= _tail.Length)
            {
                bytes[^_tail.Length..].CopyTo(_tail);
                _length = _tail.Length;
                return;
            }

            var keep = Math.Min(_length, _tail.Length - bytes.Length);
            if (keep > 0 && keep != _length)
                Array.Copy(_tail, _length - keep, _tail, 0, keep);
            bytes.CopyTo(_tail.AsSpan(keep));
            _length = keep + bytes.Length;
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}
