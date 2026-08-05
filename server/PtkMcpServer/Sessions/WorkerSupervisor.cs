using System.Globalization;
using System.Text;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Sessions;

/// <summary>
/// Owns every worker and named session for one MCP connection. No submitted
/// script executes in this supervisor process.
/// </summary>
internal sealed class WorkerSupervisor : ISessionOperations, ISessionLifetime
{
    private readonly NamedSessionSupervisor _sessions;
    private int _disposed;

    internal WorkerSupervisor(NamedSessionSupervisor sessions)
    {
        _sessions = sessions ??
            throw new ArgumentNullException(nameof(sessions));
    }

    internal static WorkerSupervisor CreateDefault(
        TimeSpan callTimeout,
        TimeSpan maxCallTimeout)
    {
        var limits = WorkerOperationProtocol.CreateLimits(
            callTimeout,
            maxCallTimeout);
        return new WorkerSupervisor(
            new NamedSessionSupervisor(
                () => ProcessSessionWorkerFactory.CreateDefault(limits),
                startupTimeout: TimeSpan.FromSeconds(30),
                containmentGrace: TimeSpan.FromSeconds(10)));
    }

    internal NamedSessionSupervisor NamedSessions => _sessions;

    async Task<string> ISessionOperations.InvokeAsync(
        string script,
        CancellationToken cancellationToken,
        bool raw,
        string route,
        int timeoutSeconds,
        string session,
        OutputStore? outputStore)
    {
        try
        {
            var invocation = await _sessions.InvokeAsync(
                session,
                script,
                raw,
                ParseRoute(route),
                timeoutSeconds,
                outputStore,
                cancellationToken).ConfigureAwait(false);
            return FormatInvocation(invocation);
        }
        catch (NamedSessionException exception)
        {
            return Refused("invoke", session, exception);
        }
        catch (SessionWorkerStartException exception)
        {
            return Failed("invoke", session, exception.DetailCode);
        }
        catch (WorkerInvocationException exception)
        {
            return FormatInvocationFailure(session, exception);
        }
        catch (WorkerProcessException exception)
        {
            return Failed("invoke", session, exception.DetailCode);
        }
        catch (WorkerProtocolException exception)
        {
            return Failed("invoke", session, exception.DetailCode);
        }
    }

    async Task<string> ISessionOperations.StateAsync(
        bool listAvailable,
        string session,
        CancellationToken cancellationToken)
    {
        try
        {
            var workerState = await _sessions.StateAsync(
                session,
                listAvailable,
                cancellationToken).ConfigureAwait(false);
            var snapshot = _sessions.List().Single(item =>
                string.Equals(item.Name, session, StringComparison.Ordinal));
            var sb = new StringBuilder();
            sb.Append("ptk supervisor: pid=")
                .Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
                .Append(" sessions=")
                .Append(_sessions.List().Length.ToString(CultureInfo.InvariantCulture))
                .Append('/')
                .Append(NamedSessionSupervisor.MaximumSessions);
            sb.AppendLine();
            AppendSnapshot(sb, snapshot);
            sb.AppendLine();
            sb.AppendLine("audit: disabled");
            if (workerState.Available)
            {
                var text = workerState.Text.TrimEnd();
                sb.Append(text.Length == 0 ? "(no runspace state)" : text);
            }
            else
            {
                sb.Append("runspace: unavailable (detail=")
                    .Append(workerState.DetailCode ?? "state_unavailable")
                    .Append(')');
            }
            return sb.ToString().TrimEnd();
        }
        catch (NamedSessionException exception)
        {
            return Refused("state", session, exception);
        }
        catch (WorkerProcessException exception)
        {
            return Failed("state", session, exception.DetailCode);
        }
        catch (WorkerProtocolException exception)
        {
            return Failed("state", session, exception.DetailCode);
        }
    }

    async Task<string> ISessionOperations.ResetAsync(
        string session,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _sessions.ResetAsync(
                session,
                cancellationToken).ConfigureAwait(false);
            var sb = new StringBuilder("[ptk reset] completed");
            sb.AppendLine();
            AppendSnapshot(sb, snapshot);
            sb.AppendLine();
            sb.Append("warm state was discarded only for this session.");
            return sb.ToString();
        }
        catch (NamedSessionException exception)
        {
            return Refused("reset", session, exception);
        }
        catch (SessionWorkerStartException exception)
        {
            return Failed("reset", session, exception.DetailCode);
        }
    }

    async Task<string> ISessionOperations.SessionAsync(
        string action,
        string? name,
        CancellationToken cancellationToken)
    {
        action = action?.ToLowerInvariant() ?? string.Empty;
        try
        {
            switch (action)
            {
                case "list":
                    if (name is not null)
                    {
                        return "[ptk session] refused detail=unexpected_session_name; " +
                            "omit name when action=list.";
                    }
                    return FormatList(_sessions.List());
                case "open":
                    if (name is null)
                        return MissingName(action);
                    if (name == NamedSessionSupervisor.DefaultName)
                    {
                        return "[ptk session] refused session=default " +
                            "detail=default_session_exists; default is lazy and " +
                            "already belongs to this connection.";
                    }
                    return FormatTransition(
                        "opened",
                        await _sessions.OpenAsync(
                            name,
                            cancellationToken).ConfigureAwait(false));
                case "close":
                    if (name is null)
                        return MissingName(action);
                    await _sessions.CloseAsync(
                        name,
                        cancellationToken).ConfigureAwait(false);
                    return $"[ptk session] closed session={name}";
                default:
                    return "[ptk session] refused detail=invalid_action; " +
                        "use list | open | close.";
            }
        }
        catch (NamedSessionException exception)
        {
            return Refused("session", name, exception);
        }
        catch (SessionWorkerStartException exception)
        {
            return Failed("session", name, exception.DetailCode);
        }
    }

    public Task ShutdownAsync() => _sessions.ShutdownAsync();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _sessions.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static WorkerInvokeRoute ParseRoute(string? route) =>
        route?.ToLowerInvariant() switch
        {
            "pwsh" => WorkerInvokeRoute.Pwsh,
            "rtk" => WorkerInvokeRoute.Rtk,
            _ => WorkerInvokeRoute.Auto,
        };

    internal static string FormatInvocationForTests(NamedSessionInvokeResult invocation) =>
        FormatInvocation(invocation);

    private static string FormatInvocation(NamedSessionInvokeResult invocation)
    {
        var sb = new StringBuilder(invocation.Result.Text.TrimEnd());
        if (sb.Length == 0)
            sb.Append("(no output)");

        if (invocation.OutputRecovery is { Advertise: true } recovery)
        {
            sb.AppendLine();
            if (recovery.Handle is { } handle)
            {
                sb.Append("recovery=available: ptk_output handle=")
                    .Append(handle);
                // Say what the artifact actually holds. A lossy projection
                // stores the same reduced view already shown inline, so a bare
                // "recovery=available" promised a fuller copy that does not
                // exist — worst on nested objects, where the entire stored
                // capture was the collapsed table (GitHub #34 F2, #35 F5).
                // The handle is still offered: it is a stable snapshot, and
                // the caller decides whether reading it is worthwhile.
                if (recovery.DetailCode == "passive_projection_lossy")
                {
                    sb.Append(
                        " (same shaped view as above; the unshaped object was " +
                        "not retained)");
                }
            }
            else
            {
                sb.Append("recovery=unavailable: output capture unavailable")
                    .Append(recovery.DetailCode is null
                        ? string.Empty
                        : $" (detail={recovery.DetailCode})")
                    .Append("; command was not rerun");
            }
        }

        switch (invocation.Result.Status)
        {
            case WorkerResultStatus.Completed:
                break;
            case WorkerResultStatus.Refused:
                AppendTerminal(
                    sb,
                    "refused",
                    invocation.Result.DetailCode,
                    "the command was not started");
                break;
            case WorkerResultStatus.Canceled:
                AppendTerminal(
                    sb,
                    "canceled",
                    invocation.Result.DetailCode,
                    "the command was not retried");
                break;
            case WorkerResultStatus.TimedOut:
                AppendTerminal(
                    sb,
                    "timed_out",
                    invocation.Result.DetailCode,
                    "this session worker is being replaced; the command was not retried");
                break;
            case WorkerResultStatus.Failed:
                // Only say the outcome is uncertain when it actually is. The
                // rider used to ride every failure, including a script that
                // ran and threw and a parse error that executed nothing — both
                // fully known outcomes. Telling a caller to distrust a result
                // that is certain teaches distrust exactly where trust is
                // deserved (GitHub #35 F6).
                AppendTerminal(
                    sb,
                    "failed",
                    invocation.Result.DetailCode,
                    invocation.Result.DetailCode == "outcome_unknown"
                        ? "outcome may be unknown; the command was not retried"
                        : "the command was not retried");
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        return sb.ToString().TrimEnd();
    }

    internal static string FormatInvocationFailure(
        string session,
        WorkerInvocationException exception) =>
        exception.Disposition switch
        {
            WorkerInvocationDisposition.NotStarted =>
                $"[ptk invoke] status=not_started session={session} " +
                $"detail={exception.CauseDetailCode}; the command was not started " +
                "and PTK did not retry it; correct the stated cause before retrying." +
                WorkerExitFacts(exception),
            WorkerInvocationDisposition.OutcomeUnknown =>
                $"[ptk invoke] status=outcome_unknown session={session} " +
                $"detail={exception.CauseDetailCode}; do not resubmit automatically; " +
                "PTK did not retry the command." +
                WorkerExitFacts(exception),
            _ => throw new ArgumentOutOfRangeException(nameof(exception)),
        };

    /// <summary>
    /// What the worker said as it died, appended to the failure line. Absent
    /// facts are omitted rather than printed as placeholders, so a death that
    /// explains nothing reads exactly as it did before GitHub #13 — and one
    /// that explains itself names the cause instead of leaving the caller to
    /// guess between a worker defect, a transport fault, and its own command
    /// killing the process.
    /// </summary>
    private static string WorkerExitFacts(WorkerInvocationException exception)
    {
        if (exception.WorkerExit is not { } exit)
            return string.Empty;
        // Everything here is labelled untrusted because all of it is: the
        // caller's script runs inside the worker, so it both shares the
        // worker's standard error and chooses the exit code (i13-1, reopened).
        // The facts are still shown — they are usually the real cause, and
        // they are the evidence #13 exists to deliver — but the reader must
        // weigh them, not read them as PTK's own finding.
        var sb = new StringBuilder(" evidence(untrusted, may originate from the executed command):");
        if (exit.ExitCode is { } code)
        {
            sb.Append(" exit_code=")
                .Append(code.ToString(CultureInfo.InvariantCulture));
        }
        if (exit.Diagnostic is { } diagnostic)
            sb.Append(" worker_stderr_tail=\"").Append(diagnostic).Append('"');
        return sb.ToString();
    }

    private static void AppendTerminal(
        StringBuilder sb,
        string status,
        string? detailCode,
        string guidance)
    {
        sb.AppendLine();
        sb.Append("[ptk worker] status=")
            .Append(status)
            .Append(" detail=")
            .Append(detailCode ?? "unspecified")
            .Append("; ")
            .Append(guidance)
            .Append('.');
    }

    internal static string FormatList(NamedSessionSnapshot[] sessions)
    {
        var sb = new StringBuilder("[ptk sessions]");
        foreach (var session in sessions)
        {
            sb.AppendLine();
            AppendSnapshot(sb, session);
        }
        return sb.ToString();
    }

    private static string FormatTransition(
        string action,
        NamedSessionSnapshot snapshot)
    {
        var sb = new StringBuilder("[ptk session] ")
            .Append(action);
        sb.AppendLine();
        AppendSnapshot(sb, snapshot);
        return sb.ToString();
    }

    private static void AppendSnapshot(
        StringBuilder sb,
        NamedSessionSnapshot snapshot)
    {
        sb.Append("session=").Append(snapshot.Name)
            .Append(" state=")
            .Append(snapshot.State.ToString().ToLowerInvariant())
            .Append(" worker_pid=")
            .Append(snapshot.WorkerProcessId?.ToString(CultureInfo.InvariantCulture) ??
                "none")
            .Append(" active=")
            .Append(snapshot.Active ? "true" : "false")
            .Append(" warm_state_lost=")
            .Append(snapshot.WarmStateLost ? "true" : "false")
            .Append(" last_failure=")
            .Append(snapshot.LastFailure ?? "none")
            .Append(" reset_required=")
            .Append(snapshot.ResetRequired ? "true" : "false");
    }

    private static string MissingName(string action) =>
        $"[ptk session] refused action={action} detail=session_name_required; " +
        "name is required.";

    private static string Refused(
        string operation,
        string? session,
        NamedSessionException exception) =>
        $"[ptk {operation}] refused session={session ?? "none"} " +
        $"detail={exception.DetailCode}; {exception.Message} Nothing was executed.";

    private static string Failed(
        string operation,
        string? session,
        string detailCode) =>
        $"[ptk {operation}] failed session={session ?? "none"} " +
        $"detail={detailCode}; no operation was retried.";
}
