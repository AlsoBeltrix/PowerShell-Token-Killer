using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PtkMcpServer;

/// <summary>
/// Unix descendant containment for command processes launched inside one
/// broker-owned session worker process group.
///
/// <see cref="Process.Kill(bool)"/> with <c>entireProcessTree: true</c>
/// delivers SIGKILL on Unix (a SIGTERM trap cannot defeat it), but its
/// descendant enumeration walks live parent links at kill time. A
/// descendant whose intermediate parent already exited was reparented to
/// PID 1 and is invisible to that walk, so it survives the kill (rbc-6).
/// The instant daemonization idiom <c>( cmd &amp; )</c> produces exactly
/// this shape.
///
/// The broker makes the worker a process-group leader before startup. Every
/// command descendant inherits that kernel mark. Kill-time escalation sweeps
/// group-marked processes whose live parent chain no longer reaches the
/// worker, closing the instant-daemonization hole in Process.Kill(tree).
///
/// Boundaries, by design:
///  - Windows instances are inert; Windows containment is the Job-Object
///    posture tracked by rbc-5.
///  - A descendant that calls setsid (or setpgid) sheds the group mark —
///    the same escape a Windows Job-Object breakaway grants. Closing it
///    requires OS facilities (cgroups, subreapers) that an unprivileged
///    portable parent does not have.
///  - Escalation is containment, not a retry: it never upgrades the audit
///    disposition of the invocation, and a root whose exit was not
///    observed stays unconfirmed unless the post-escalation recheck
///    observes it.
/// </summary>
internal sealed class ProcessTreeContainment : IDisposable
{
    private static readonly ConditionalWeakTable<Process, ProcessTreeContainment> Registry = [];
    private static readonly TimeSpan EscalationGrace = TimeSpan.FromMilliseconds(500);

    private const int Sigkill = 9;
    private static int _workerOwnedGroupMode;

    private readonly bool _inert;
    private int _disposed;

    private ProcessTreeContainment(bool inert)
    {
        _inert = inert;
    }

    /// <summary>
    /// Verifies the broker-owned worker group before a command launch. Direct
    /// in-process unit hosts are inert; production has no in-process execution
    /// path and reaches this only after worker bootstrap selected the group.
    /// </summary>
    internal static void EnsureExclusiveGroup()
    {
        if (OperatingSystem.IsWindows() ||
            Volatile.Read(ref _workerOwnedGroupMode) == 0)
        {
            return;
        }

        if (getpid() <= 0 || getpgrp() != getpid())
        {
            throw new InvalidOperationException(
                "The Unix worker lost its broker-owned process group.");
        }
    }

    /// <summary>
    /// Selects the broker-owned worker process group before any command can
    /// launch. The broker has already made this worker the group leader, so
    /// worker mode validates that fact and never attempts a nested setpgid or
    /// setsid transition.
    /// </summary>
    internal static void EnterWorkerOwnedGroupMode()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "A broker-owned process group is a Unix worker boundary.");
        }
        var processId = getpid();
        if (processId <= 0 || getpgrp() != processId)
        {
            throw new InvalidOperationException(
                "The Unix worker is not its broker-owned process-group leader.");
        }
        if (Interlocked.CompareExchange(
                ref _workerOwnedGroupMode,
                1,
                0) != 0)
        {
            throw new InvalidOperationException(
                "The Unix worker containment mode was already selected.");
        }
    }

    /// <summary>
    /// Begins containment for a successfully started process. Direct
    /// in-process test hosts and Windows are inert.
    /// </summary>
    internal static ProcessTreeContainment Track(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var tracker = new ProcessTreeContainment(
            inert: OperatingSystem.IsWindows() ||
                Volatile.Read(ref _workerOwnedGroupMode) == 0);
        try
        {
            Registry.AddOrUpdate(process, tracker);
        }
        catch
        {
            tracker.Dispose();
            return new ProcessTreeContainment(inert: true);
        }
        return tracker;
    }

    /// <summary>
    /// Kill-time escalation. SIGKILLs escaped descendants (group-marked
    /// orphans in exclusive mode; tracked survivors in fallback mode),
    /// re-kills the root if its exit was not confirmed, and returns the
    /// (possibly upgraded) root-termination confirmation. Never throws;
    /// returns <paramref name="stopped"/> unchanged when no active
    /// tracker exists for <paramref name="process"/>.
    /// </summary>
    internal static async Task<bool> EscalateAsync(Process process, bool stopped)
    {
        ProcessTreeContainment? tracker;
        try
        {
            if (!Registry.TryGetValue(process, out tracker) ||
                tracker is null || tracker._inert)
            {
                return stopped;
            }
        }
        catch
        {
            return stopped;
        }

        try { return await tracker.EscalateCoreAsync(process, stopped); }
        catch { return stopped; }
    }

    /// <summary>
    /// True when the deterministic exclusive-group sweep is active for
    /// this server process. Exposed so guards can assert which mechanism
    /// they exercised.
    /// </summary>
    internal static bool UsingExclusiveGroup =>
        !OperatingSystem.IsWindows() &&
        Volatile.Read(ref _workerOwnedGroupMode) != 0;

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    private async Task<bool> EscalateCoreAsync(Process process, bool stopped)
    {
        var snapshot = ProcessTableSnapshot.TryTake();
        if (snapshot is not null)
        {
            var self = getpid();
            var group = getpgrp();
            var live = LiveClosure(snapshot, self);
            foreach (var row in snapshot)
            {
                if (row.Pgid == group &&
                    row.Pid != self &&
                    !live.Contains(row.Pid))
                {
                    _ = sys_kill(row.Pid, Sigkill);
                }
            }
        }

        if (!stopped)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                var wait = process.WaitForExitAsync();
                if (await Task.WhenAny(wait, Task.Delay(EscalationGrace)) == wait)
                {
                    await wait;
                }

                stopped = process.HasExited;
            }
            catch { }
        }

        return stopped;
    }

    private static HashSet<int> LiveClosure(
        List<ProcessTableRow> snapshot,
        int rootPid)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        foreach (var row in snapshot)
        {
            if (!childrenByParent.TryGetValue(row.Ppid, out var children))
            {
                children = [];
                childrenByParent[row.Ppid] = children;
            }

            children.Add(row.Pid);
        }

        var closure = new HashSet<int>();
        var frontier = new Queue<int>();
        frontier.Enqueue(rootPid);
        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();
            if (!childrenByParent.TryGetValue(parent, out var children)) continue;
            foreach (var child in children)
            {
                if (closure.Add(child)) frontier.Enqueue(child);
            }
        }

        return closure;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int sys_kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpid();

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgrp();

}

internal readonly record struct ProcessTableRow(int Pid, int Ppid, int Pgid);

/// <summary>
/// Point-in-time (pid, ppid, pgid) view of the process table. Reads /proc
/// when it exists (Linux); otherwise shells out to <c>/bin/ps</c>
/// (macOS/BSD).
/// </summary>
internal static class ProcessTableSnapshot
{
    private static readonly Lock SharedGate = new();
    private static List<ProcessTableRow>? _sharedSnapshot;
    private static long _sharedTimestamp;

    internal static List<ProcessTableRow>? TryTake()
    {
        try
        {
            return Directory.Exists("/proc") ? FromProc() : FromPs();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns one read-only-by-convention snapshot shared by concurrent
    /// containment observers for a short interval. Kill-time callers continue
    /// to use <see cref="TryTake"/> directly and always receive a fresh view.
    /// </summary>
    internal static List<ProcessTableRow>? TryTakeShared(TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));

        lock (SharedGate)
        {
            var now = Stopwatch.GetTimestamp();
            if (_sharedTimestamp != 0 &&
                Stopwatch.GetElapsedTime(_sharedTimestamp, now) <= maximumAge)
            {
                return _sharedSnapshot;
            }

            _sharedSnapshot = TryTake();
            _sharedTimestamp = now;
            return _sharedSnapshot;
        }
    }

    private static List<ProcessTableRow> FromProc()
    {
        var rows = new List<ProcessTableRow>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid)) continue;
            string stat;
            try { stat = File.ReadAllText(Path.Combine(dir, "stat")); }
            catch { continue; }

            // Format: pid (comm) state ppid pgrp ... — comm may contain
            // spaces and parentheses, so anchor on the last ')'.
            var close = stat.LastIndexOf(')');
            if (close < 0) continue;
            var fields = stat[(close + 1)..].Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) continue;
            if (int.TryParse(fields[1], out var ppid) &&
                int.TryParse(fields[2], out var pgid))
            {
                rows.Add(new ProcessTableRow(pid, ppid, pgid));
            }
        }

        return rows;
    }

    private static List<ProcessTableRow>? FromPs()
    {
        var startInfo = new ProcessStartInfo("/bin/ps", "-axo pid=,ppid=,pgid=")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var ps = Process.Start(startInfo);
        if (ps is null) return null;
        using var standardOutput = ps.StandardOutput;
        var text = standardOutput.ReadToEnd();
        ps.WaitForExit();
        if (ps.ExitCode != 0) return null;

        var rows = new List<ProcessTableRow>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) continue;
            if (int.TryParse(fields[0], out var pid) &&
                int.TryParse(fields[1], out var ppid) &&
                int.TryParse(fields[2], out var pgid))
            {
                rows.Add(new ProcessTableRow(pid, ppid, pgid));
            }
        }

        return rows;
    }
}
