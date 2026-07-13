namespace PtkMcpServer;

/// <summary>
/// Compatibility-usage visibility: counts raw=true calls at the ptk_invoke
/// user boundary only. The deprecated flag is inert but remains observable in
/// ptk_state and the server log until the next breaking tool-schema revision
/// removes it. Internal probes never touch this counter.
/// </summary>
public sealed class RawUsageCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    /// <summary>Returns the new total.</summary>
    public int Increment() => Interlocked.Increment(ref _count);
}
