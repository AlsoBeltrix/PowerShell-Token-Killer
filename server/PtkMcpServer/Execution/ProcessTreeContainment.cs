using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PtkMcpServer;

/// <summary>
/// Best-effort Unix descendant containment for launched command processes.
///
/// <see cref="Process.Kill(bool)"/> with <c>entireProcessTree: true</c>
/// delivers SIGKILL on Unix (a SIGTERM trap cannot defeat it), but its
/// descendant enumeration walks live parent links at kill time. A
/// descendant whose intermediate parent already exited was reparented to
/// PID 1 and is invisible to that walk, so it survives the kill (rbc-6).
/// The instant daemonization idiom <c>( cmd &amp; )</c> produces exactly
/// this shape.
///
/// Primary mechanism (exclusive process group): at first use the server
/// makes itself a process-group leader (<c>setpgid(0, 0)</c>; interactive
/// shells already start it as one). From then on the server's pgid is an
/// inherited kernel mark carried by every descendant of every launched
/// process. Kill-time escalation then sweeps: any process whose pgid is
/// the server's, that is not the server itself, and that has no live
/// parent chain back to the server is an escaped descendant — SIGKILL it.
/// This is deterministic: no polling race, and sub-poll-interval
/// intermediate deaths cannot hide an orphan.
///
/// Fallback mechanism (polled closure): when group leadership cannot be
/// acquired, a poller samples the process table and tracks the observed
/// descendant closure of each launched root; escalation SIGKILLs tracked
/// survivors. This misses descendants whose entire intermediate lineage
/// lived shorter than one poll interval.
///
/// Boundaries, by design:
///  - Windows instances are inert; Windows containment is the Job-Object
///    posture tracked by rbc-5.
///  - A descendant that calls setsid (or setpgid) sheds the group mark —
///    the same escape a Windows Job-Object breakaway grants. Closing it
///    requires OS facilities (cgroups, subreapers) that an unprivileged
///    portable parent does not have.
///  - Group leadership means an operator killpg aimed at the server's
///    original group no longer reaches it; service managers that signal
///    direct children or use cgroups are unaffected.
///  - Escalation is containment, not a retry: it never upgrades the audit
///    disposition of the invocation, and a root whose exit was not
///    observed stays unconfirmed unless the post-escalation recheck
///    observes it.
/// </summary>
internal sealed class ProcessTreeContainment : IDisposable
{
    private static readonly ConditionalWeakTable<Process, ProcessTreeContainment> Registry = [];
    private static readonly Lazy<bool> ExclusiveGroup = new(TryAcquireExclusiveGroup);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EscalationGrace = TimeSpan.FromMilliseconds(500);
    private const int Sigkill = 9;

    private readonly int _rootPid;
    private readonly bool _inert;
    private readonly Lock _gate = new();
    private readonly HashSet<int> _tracked = [];
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;

    private ProcessTreeContainment(int rootPid, bool inert)
    {
        _rootPid = rootPid;
        _inert = inert;
        if (!inert && !ExclusiveGroup.Value)
            _ = Task.Run(() => PollLoopAsync(_stop.Token));
    }

    /// <summary>
    /// Begins containment for a successfully started process. Returns an
    /// inert instance on Windows or when the pid cannot be read. Never
    /// throws.
    /// </summary>
    internal static ProcessTreeContainment Track(Process process)
    {
        ProcessTreeContainment tracker;
        try
        {
            tracker = OperatingSystem.IsWindows()
                ? new ProcessTreeContainment(0, inert: true)
                : new ProcessTreeContainment(process.Id, inert: false);
        }
        catch
        {
            tracker = new ProcessTreeContainment(0, inert: true);
        }

        try { Registry.AddOrUpdate(process, tracker); } catch { }
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
        !OperatingSystem.IsWindows() && ExclusiveGroup.Value;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stop.Cancel(); } catch { }
        _stop.Dispose();
    }

    private static bool TryAcquireExclusiveGroup()
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            if (getpgrp() != getpid()) _ = setpgid(0, 0);
            return getpgrp() == getpid();
        }
        catch
        {
            return false;
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var snapshot = ProcessTableSnapshot.TryTake();
            if (snapshot is not null) Update(snapshot);
            try { await Task.Delay(PollInterval, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Update(List<ProcessTableRow> snapshot)
    {
        var closure = LiveClosure(snapshot, _rootPid);
        var present = new Dictionary<int, int>(snapshot.Count);
        foreach (var row in snapshot) present[row.Pid] = row.Ppid;

        lock (_gate)
        {
            // Retain a tracked pid only while it stays present and is
            // either still inside the closure or reparented to PID 1;
            // pids that exit are dropped on the next poll, bounding
            // pid-reuse misfire risk to one poll interval.
            _tracked.RemoveWhere(pid =>
                !present.TryGetValue(pid, out var ppid) ||
                (!closure.Contains(pid) && ppid != 1));
            foreach (var pid in closure) _tracked.Add(pid);
        }
    }

    private async Task<bool> EscalateCoreAsync(Process process, bool stopped)
    {
        var snapshot = ProcessTableSnapshot.TryTake();
        if (snapshot is not null)
        {
            foreach (var pid in FindEscapees(snapshot))
            {
                _ = sys_kill(pid, Sigkill);
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

    private HashSet<int> FindEscapees(List<ProcessTableRow> snapshot)
    {
        if (ExclusiveGroup.Value)
        {
            // Deterministic sweep: group-marked processes with no live
            // parent chain back to the server are escaped descendants.
            var self = getpid();
            var group = getpgrp();
            var live = LiveClosure(snapshot, self);
            var escapees = new HashSet<int>();
            foreach (var row in snapshot)
            {
                if (row.Pgid == group && row.Pid != self && !live.Contains(row.Pid))
                    escapees.Add(row.Pid);
            }

            return escapees;
        }

        // Fallback: tracked survivors observed by the poller, folded with
        // the root's current live closure, intersected with live pids.
        var survivors = new HashSet<int>();
        lock (_gate)
        {
            foreach (var pid in _tracked) survivors.Add(pid);
        }

        var present = new HashSet<int>(snapshot.Count);
        foreach (var row in snapshot) present.Add(row.Pid);
        foreach (var pid in LiveClosure(snapshot, _rootPid)) survivors.Add(pid);
        survivors.IntersectWith(present);
        survivors.Remove(_rootPid);
        return survivors;
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

    [DllImport("libc", SetLastError = true)]
    private static extern int setpgid(int pid, int pgid);
}

internal readonly record struct ProcessTableRow(int Pid, int Ppid, int Pgid);

/// <summary>
/// Point-in-time (pid, ppid, pgid) view of the process table. Reads /proc
/// when it exists (Linux); otherwise shells out to <c>/bin/ps</c>
/// (macOS/BSD).
/// </summary>
internal static class ProcessTableSnapshot
{
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
        var text = ps.StandardOutput.ReadToEnd();
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
