using System.Text;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Sessions;

internal sealed record SessionWorkerInvokeResult(
    string Text,
    InvokeDisposition Disposition,
    bool TimedOut);

internal sealed record SessionWorkerStateResult(
    string Text,
    bool RunspaceDetailsAvailable);

/// <summary>
/// Owns one warm PowerShell session and all session-lifetime execution state.
/// Request-scoped audit capabilities and supervisor-owned output storage are
/// borrowed per operation instead of becoming runtime-lifetime dependencies.
/// </summary>
public sealed class SessionRuntime : ISessionLifetime, IDisposable
{
    private readonly RunspaceHost _host;
    private readonly RawUsageCounter _rawUsage;
    private readonly SemaphoreSlim _availableModuleCacheGate = new(1, 1);
    private string? _availableModuleCache;
    private int _disposed;

    internal SessionRuntime(
        RunspaceHost host,
        RawUsageCounter rawUsage)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(rawUsage);

        _host = host;
        _rawUsage = rawUsage;
    }

    Task ISessionLifetime.ShutdownAsync() => ShutdownAsync();

    internal Task ShutdownAsync() => _host.ShutdownAsync();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _host.Dispose();
        }
        finally
        {
            _availableModuleCacheGate.Dispose();
        }
    }

    internal async Task<string> InvokeAsync(
        string script,
        CancellationToken cancellationToken,
        bool raw = false,
        string route = "auto",
        int timeoutSeconds = 0,
        AuditCallContext? audit = null,
        OutputStore? outputStore = null)
    {
        var host = _host;
        var rawUsage = _rawUsage;
        if (audit is not null && !audit.BeginValidation())
            return AuditCallContext.NotStartedMessage;

        // Deprecated-flag visibility is counted at the user-call boundary
        // only. The flag is intentionally not forwarded into planning or
        // execution; the log and ptk_state count show remaining compatibility
        // usage until the next breaking schema revision removes it.
        if (raw)
        {
            Console.Error.WriteLine($"ptk: raw=true call #{rawUsage.Increment()} this session");
        }

        route = NormalizeRoute(route);

        return (await InvokeForegroundCoreAsync(
            host,
            script,
            cancellationToken,
            route,
            timeoutSeconds,
            audit,
            outputStore).ConfigureAwait(false)).Text;
    }

    internal Task<SessionWorkerInvokeResult> InvokeWorkerAsync(
        string script,
        CancellationToken cancellationToken,
        bool raw,
        string route,
        int timeoutSeconds,
        DateTimeOffset deadlineUtc,
        IForegroundOutputCapture? outputCapture = null)
    {
        if (raw)
        {
            Console.Error.WriteLine(
                $"ptk: raw=true call #{_rawUsage.Increment()} this session");
        }
        route = NormalizeRoute(route);
        return InvokeForegroundCoreAsync(
            _host,
            script,
            cancellationToken,
            route,
            timeoutSeconds,
            audit: null,
            outputStore: null,
            outputCapture,
            deadlineUtc);
    }

    private static async Task<SessionWorkerInvokeResult> InvokeForegroundCoreAsync(
        RunspaceHost host,
        string script,
        CancellationToken cancellationToken,
        string route,
        int timeoutSeconds,
        AuditCallContext? audit,
        OutputStore? outputStore,
        IForegroundOutputCapture? workerOutputCapture = null,
        DateTimeOffset? workerDeadlineUtc = null)
    {
        if (outputStore is not null && workerOutputCapture is not null)
            throw new ArgumentException("Only one output-capture owner may be supplied.");
        using var ownedOutputCapture = outputStore is null
            ? null
            : new ForegroundOutputCapture(outputStore);
        var outputCapture = workerOutputCapture ?? ownedOutputCapture;
        var result = audit is null
            ? outputCapture is null
                ? await host.InvokeAsync(
                    script,
                    cancellationToken: cancellationToken,
                    route: route,
                    timeoutSeconds: timeoutSeconds,
                    deadline: workerDeadlineUtc).ConfigureAwait(false)
                : await host.InvokeWithOutputCaptureAsync(
                    script,
                    outputCapture,
                    cancellationToken: cancellationToken,
                    route: route,
                    timeoutSeconds: timeoutSeconds,
                    deadline: workerDeadlineUtc).ConfigureAwait(false)
            : await host.InvokeAsync(
                script,
                audit,
                cancellationToken: cancellationToken,
                route: route,
                timeoutSeconds: timeoutSeconds,
                deadline: audit.Metadata.Request.DeadlineUtc,
                outputCapture: outputCapture).ConfigureAwait(false);

        var sb = new StringBuilder();
        var output = result.Output.TrimEnd();
        sb.Append(output.Length > 0 ? output : "(no output)");

        if (result.UserExecutionStarted &&
            result.Routing is
            {
                FallbackReason: { } fallbackReason,
                OriginalScriptDispatched: true,
            } routing)
        {
            sb.AppendLine();
            sb.Append(
                $"[route] requested={routing.RequestedRoute.ToMachineCode()} " +
                $"effective={routing.EffectivePath.ToMachineCode()} " +
                $"fallback={fallbackReason.ToMachineCode()}; " +
                "the original script was dispatched once and PTK did not retry it.");
        }

        if (result.ExitCode is int exitCode)
        {
            sb.AppendLine();
            sb.Append($"[exit] {exitCode}");
        }

        // Neutral by design: native tools write progress and diagnostics to
        // stderr while succeeding, so this section is not a failure signal -
        // [errors] below is (issue #5).
        if (result.Stderr is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("[stderr]");
            foreach (var line in result.Stderr) sb.AppendLine(line);
        }

        if (result.Errors.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[errors]");
            foreach (var error in result.Errors) sb.AppendLine(error);
        }

        if (result.Warnings.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[warnings]");
            foreach (var warning in result.Warnings) sb.AppendLine(warning);
        }

        if (workerOutputCapture is null &&
            result.OutputRecovery is { Advertise: true } recovery)
        {
            sb.AppendLine();
            sb.Append(recovery.Handle is { } handle
                ? $"recovery=available: ptk_output handle={handle}"
                : recovery.DetailCode == "rtk_capture_unsupported"
                    ? "recovery=unavailable: rtk capture unsupported"
                    : "recovery=unavailable: output capture unavailable; command was not rerun");
        }

        var response = sb.ToString().TrimEnd();
        if (audit?.AuthorizationPersistenceFailed == true && !result.UserExecutionStarted)
            response = AuditCallContext.NotStartedMessage;
        audit?.RecordInvokeResult(result, response);
        return new SessionWorkerInvokeResult(
            response,
            result.Disposition,
            result.TimedOut);
    }

    private static string NormalizeRoute(string? route) =>
        route?.ToLowerInvariant() switch
        {
            "pwsh" => "pwsh",
            "rtk" => "rtk",
            _ => "auto",
        };

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    internal async Task<string> StateAsync(
        bool listAvailable = false,
        CancellationToken cancellationToken = default,
        AuditCallContext? audit = null) =>
        (await StateCoreAsync(
            listAvailable,
            cancellationToken,
            audit).ConfigureAwait(false)).Text;

    internal Task<SessionWorkerStateResult> StateWorkerAsync(
        bool listAvailable,
        CancellationToken cancellationToken) =>
        StateCoreAsync(listAvailable, cancellationToken, audit: null);

    private async Task<SessionWorkerStateResult> StateCoreAsync(
        bool listAvailable = false,
        CancellationToken cancellationToken = default,
        AuditCallContext? audit = null)
    {
        var host = _host;
        var rawUsage = _rawUsage;
        if (audit is not null && !audit.AuthorizeControl("state.probe_requested"))
        {
            return new SessionWorkerStateResult(
                AuditCallContext.NotStartedMessage,
                RunspaceDetailsAvailable: false);
        }
        var runspaceLossRecorded = false;

        // No assignments in this script: probing the session must not add
        // variables to it (the report would perturb its own drift numbers).
        var script = string.Join('\n',
            "\"engine: $($PSVersionTable.PSVersion)\"",
            "\"cwd: $((Microsoft.PowerShell.Management\\Get-Location).Path)\"",
            $"\"variables: $(@(Microsoft.PowerShell.Utility\\Get-Variable).Count) (baseline {host.BaselineVariableCount})\"",
            "$(if (@(Microsoft.PowerShell.Core\\Get-Module).Count -eq 0) { 'modules loaded: (none)' } else { 'modules loaded:' })",
            "Microsoft.PowerShell.Core\\Get-Module | Microsoft.PowerShell.Utility\\Sort-Object Name | " +
            "Microsoft.PowerShell.Core\\ForEach-Object { '  ' + $_.Name + ' ' + $_.Version }");
        // Zero-wait acquire: the health check must never queue behind the
        // workload it exists to diagnose (issue #6). Null = busy; the failed
        // acquire IS the busy signal — no snapshot-then-queue race window.
        var result = await host.TryInvokeStateProbeIfIdleAsync(
            script,
            cancellationToken: cancellationToken);
        if (result?.WarmStateLost == true && audit is not null)
        {
            audit.RecordControlOutcome(
                "runspace.recycled",
                "completed",
                detailCode: "state_probe_timed_out",
                warmStateLost: true);
            runspaceLossRecorded = true;
        }

        var probeState = result is not null && (!result.Success || result.Errors.Length > 0)
            ? "partial"
            : "completed";
        string? probeDetailCode = result is null
            ? "runspace_busy"
            : probeState == "partial" ? "probe_errors" : null;

        SessionWorkerStateResult Finish(string response)
        {
            audit?.CommitReadOutcome(
                "state.probe_completed",
                probeState,
                response,
                detailCode: probeDetailCode);
            return new SessionWorkerStateResult(
                response,
                RunspaceDetailsAvailable: result is not null);
        }

        var sb = new StringBuilder();
        // Raw count is compatibility telemetry for user-level raw=true calls
        // only. Internal state probes never touch the compatibility flag.
        sb.AppendLine(
            $"ptk {PtkVersion.Value}: pid {Environment.ProcessId}, up {FormatUptime(DateTimeOffset.UtcNow - host.StartedUtc)}, " +
            $"shaping {(host.ModuleLoaded ? "on" : "off")}, raw calls this session: {rawUsage.Count}");
        if (audit is not null)
            sb.AppendLine(audit.HealthStatusLine());
        else
            sb.AppendLine("audit: disabled");
        var busyLineEmitted = false;
        if (result is null)
        {
            busyLineEmitted = true;
            sb.AppendLine(FormatBusyLine(host));
            sb.AppendLine("runspace-dependent details (engine, cwd, variables, loaded modules) unavailable while busy.");
        }
        else
        {
            if (result.Output.TrimEnd().Length > 0) sb.AppendLine(result.Output.TrimEnd());
            // A probe can still fail (provider/module faults, cancellation, or
            // non-terminating errors): surface that instead of silently
            // reporting partial state as the truth.
            if (!result.Success || result.Errors.Length > 0)
            {
                sb.AppendLine("[state probe errors]");
                foreach (var error in result.Errors) sb.AppendLine(error);
            }
        }

        var drift = host.GetEnvironmentDrift();
        sb.AppendLine("[env drift since server start]");
        if (drift.IsEmpty)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            if (drift.Added.Length > 0) sb.AppendLine("added: " + string.Join(", ", drift.Added));
            if (drift.Modified.Length > 0) sb.AppendLine("modified: " + string.Join(", ", drift.Modified));
            if (drift.Removed.Length > 0) sb.AppendLine("removed: " + string.Join(", ", drift.Removed));
            if (drift.PathEntriesAdded.Length > 0) sb.AppendLine("PATH entries added: " + string.Join("; ", drift.PathEntriesAdded));
            if (drift.PathEntriesRemoved.Length > 0) sb.AppendLine("PATH entries removed: " + string.Join("; ", drift.PathEntriesRemoved));
        }

        if (listAvailable)
        {
            // A populated cache renders without touching the gate: a caller
            // merely reading the cache must not make a concurrent call claim
            // an enumeration is running (codex finding i56-15). The cache is
            // written once per session; a stale null read just falls through
            // to the gate path.
            if (_availableModuleCache is string cachedFast)
            {
                sb.AppendLine("modules available:");
                sb.AppendLine(cachedFast.Length > 0 ? cachedFast : "  (none)");
                return Finish(sb.ToString().TrimEnd());
            }
            // Zero-wait like every other status probe (codex finding i56-7):
            // a second state call must not block for minutes behind another
            // caller's slow first enumeration - that would withhold even the
            // host-level facts this tool promises to always deliver.
            if (!_availableModuleCacheGate.Wait(0))
            {
                sb.AppendLine("modules available: enumeration already in progress in another state call (not cached)");
                probeDetailCode ??= "module_enumeration_in_progress";
                return Finish(sb.ToString().TrimEnd());
            }
            try
            {
                if (_availableModuleCache is null)
                {
                    // Independently zero-wait: a long call can win the runspace
                    // between the first probe and this one, and queueing here
                    // would reintroduce the blocked health check (issue #6).
                    var available = await host.TryInvokeStateProbeIfIdleAsync(
                        "Microsoft.PowerShell.Core\\Get-Module -ListAvailable | " +
                        "Microsoft.PowerShell.Utility\\Sort-Object Name -Unique | " +
                        "Microsoft.PowerShell.Core\\ForEach-Object { '  {0} {1}' -f $_.Name, $_.Version }",
                        cancellationToken: cancellationToken);
                    if (available?.WarmStateLost == true && audit is not null && !runspaceLossRecorded)
                    {
                        audit.RecordControlOutcome(
                            "runspace.recycled",
                            "completed",
                            detailCode: "module_probe_timed_out",
                            warmStateLost: true);
                        runspaceLossRecorded = true;
                    }
                    // Cache only a clean probe: a failed/canceled enumeration must
                    // not masquerade as "(none)", and Success=true still carries
                    // non-terminating errors can accompany fake/partial data,
                    // so both must be clear before caching.
                    if (available is null)
                    {
                        probeDetailCode ??= "module_probe_busy";
                        sb.AppendLine("modules available: unavailable while the runspace is busy (not cached)");
                        // A long call can win the gate BETWEEN the main probe
                        // and this one; the promised busy snapshot must not
                        // vanish just because the main leg was idle (i56-8).
                        if (!busyLineEmitted) sb.AppendLine(FormatBusyLine(host));
                    }
                    else if (available.Success && available.Errors.Length == 0)
                    {
                        _availableModuleCache = available.Output.TrimEnd();
                    }
                    else
                    {
                        probeState = "partial";
                        probeDetailCode = "module_probe_errors";
                        sb.AppendLine("modules available: probe reported errors (not cached)");
                        foreach (var error in available.Errors) sb.AppendLine("  " + error);
                    }
                }
                if (_availableModuleCache is not null)
                {
                    sb.AppendLine("modules available:");
                    sb.AppendLine(_availableModuleCache.Length > 0 ? _availableModuleCache : "  (none)");
                }
            }
            finally
            {
                _availableModuleCacheGate.Release();
            }
        }

        return Finish(sb.ToString().TrimEnd());
    }

    /// <summary>Test hook for explicit cache lifecycle assertions.</summary>
    internal void ClearAvailableCacheForTests() => _availableModuleCache = null;

    // Queue-wait and execution age are independently observable (issue #6):
    // this line carries the active call's age and the waiter count; the
    // queue-expiry failure on ptk_invoke carries the wait it spent.
    private static string FormatBusyLine(RunspaceHost host)
    {
        var (_, age, waiters, recovering) = host.GetGateStatus();
        if (recovering) return $"runspace: busy (rebuilding after a recycle, {waiters} waiting)";
        return age is not null
            ? $"runspace: busy (active call running {age.Value.TotalSeconds:0}s, {waiters} waiting)"
            : $"runspace: busy ({waiters} waiting)";
    }

    private static string FormatUptime(TimeSpan up) =>
        up.TotalHours >= 1 ? $"{(int)up.TotalHours}h{up.Minutes:00}m"
        : up.TotalMinutes >= 1 ? $"{(int)up.TotalMinutes}m{up.Seconds:00}s"
        : $"{Math.Max(0, (int)up.TotalSeconds)}s";

    internal async Task<string> ResetAsync(
        CancellationToken cancellationToken = default,
        AuditCallContext? audit = null)
    {
        if (audit is not null && !audit.AuthorizeControl("reset.requested"))
            return AuditCallContext.NotStartedMessage;

        try
        {
            await _host.ResetAsync(cancellationToken).ConfigureAwait(false);
            audit?.RecordControlOutcome(
                "runspace.recycled",
                "completed",
                warmStateLost: true);
        }
        catch
        {
            audit?.RecordControlOutcome(
                "reset.outcome_unknown",
                "outcome_unknown",
                detailCode: "runspace_outcome_unknown",
                terminationCertainty: "unknown");
            throw;
        }

        return "Runspace recycled; all warm state cleared and environment restored.";
    }

}
