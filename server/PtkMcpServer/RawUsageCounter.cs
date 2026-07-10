namespace PtkMcpServer;

/// <summary>
/// Raw-usage visibility (shell-dialect plan D2): counts raw=true calls at the
/// ptk_invoke user-call boundary ONLY, so the owner can see escape-hatch
/// pressure in ptk_state and the server log. Internal probes that pass
/// raw:true straight to RunspaceHost (ptk_state's own probes, the cwd probe
/// before every background job) measure implementation plumbing, not the
/// model's habit, and must never inflate this — which is why the increment
/// lives in InvokeTool and nothing below it.
/// </summary>
public sealed class RawUsageCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    /// <summary>Returns the new total.</summary>
    public int Increment() => Interlocked.Increment(ref _count);
}
