using PtkMcpServer.Sessions;
using PtkMcpServer.Tools;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerSupervisorTests
{
    [Fact]
    public async Task Public_invoke_reports_proved_not_started_failure()
    {
        var text = await InvokePublicAsync(
            WorkerInvocationDisposition.NotStarted,
            "worker_transport_unavailable",
            "sample-online");

        Assert.Equal(
            "[ptk invoke] status=not_started session=sample-online " +
            "detail=worker_transport_unavailable; the command was not started " +
            "and PTK did not retry it; correct the stated cause before retrying.",
            text);
    }

    [Fact]
    public async Task Public_invoke_forbids_resubmission_when_outcome_is_unknown()
    {
        var text = await InvokePublicAsync(
            WorkerInvocationDisposition.OutcomeUnknown,
            "worker_transport_failure",
            "sample-onprem");

        Assert.Equal(
            "[ptk invoke] status=outcome_unknown session=sample-onprem " +
            "detail=worker_transport_failure; do not resubmit automatically; " +
            "PTK did not retry the command.",
            text);
    }

    /// <summary>
    /// GitHub #35 F6: "outcome may be unknown" rode every failure, including a
    /// script that ran and threw and a parse error that executed nothing. Both
    /// outcomes are fully known, and telling a caller to distrust a certain
    /// result teaches distrust where trust is deserved. Only a genuinely
    /// uncertain outcome carries the rider now.
    /// </summary>
    [Theory]
    [InlineData("execution_failed", false)]
    [InlineData("outcome_unknown", true)]
    public void Only_a_genuinely_uncertain_failure_says_the_outcome_may_be_unknown(
        string detailCode,
        bool expectUncertaintyRider)
    {
        var text = WorkerSupervisor.FormatInvocationForTests(
            new NamedSessionInvokeResult(
                new WorkerResult(
                    RequestId: 1,
                    WorkerResultStatus.Failed,
                    "boom",
                    detailCode),
                OutputRecovery: null));

        Assert.Contains($"detail={detailCode}", text, StringComparison.Ordinal);
        Assert.Contains("the command was not retried", text, StringComparison.Ordinal);
        Assert.Equal(
            expectUncertaintyRider,
            text.Contains("outcome may be unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void Faulted_session_snapshot_says_that_explicit_reset_is_required()
    {
        var text = WorkerSupervisor.FormatList(
            [
                new NamedSessionSnapshot(
                    "sample-online",
                    Guid.NewGuid(),
                    NamedSessionState.Faulted,
                    WorkerProcessId: null,
                    Active: false,
                    WarmStateLost: true,
                    LastFailure: "replacement_start_failed",
                    ResetRequired: true),
            ]);

        Assert.Contains("state=faulted", text, StringComparison.Ordinal);
        Assert.Contains(
            "last_failure=replacement_start_failed",
            text,
            StringComparison.Ordinal);
        Assert.Contains("reset_required=true", text, StringComparison.Ordinal);
    }

    private static async Task<string> InvokePublicAsync(
        WorkerInvocationDisposition disposition,
        string detailCode,
        string session)
    {
        using var supervisor = new WorkerSupervisor(
            new NamedSessionSupervisor(
                () => new FailingWorkerFactory(disposition, detailCode),
                startupTimeout: TimeSpan.FromSeconds(5),
                containmentGrace: TimeSpan.FromSeconds(1)));
        _ = await supervisor.NamedSessions.OpenAsync(session);

        return await InvokeTool.Invoke(
            supervisor,
            "'never returns'",
            CancellationToken.None,
            raw: false,
            route: "pwsh",
            timeoutSeconds: 30,
            session: session,
            outputStore: null);
    }

    private sealed class FailingWorkerFactory(
        WorkerInvocationDisposition disposition,
        string detailCode) : ISessionWorkerFactory
    {
        public Task<ISessionWorker> StartAsync(
            Guid sessionId,
            long incarnation,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<ISessionWorker>(
                new FailingWorker(
                    sessionId,
                    incarnation,
                    disposition,
                    detailCode));
    }

    private sealed class FailingWorker(
        Guid sessionId,
        long incarnation,
        WorkerInvocationDisposition disposition,
        string detailCode) : ISessionWorker
    {
        private readonly TaskCompletionSource _fatal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 47001;
        public Guid SessionId => sessionId;
        public long Incarnation => incarnation;
        public bool IsTransportUsable => true;
        public Task Fatal => _fatal.Task;
        public Task ContainmentEmpty => Task.CompletedTask;

        public Task<SessionWorkerInvocation> InvokeAsync(
            string script,
            bool raw,
            WorkerInvokeRoute route,
            int timeoutSeconds,
            IWorkerArtifactCapture? artifactCapture,
            CancellationToken cancellationToken) =>
            Task.FromException<SessionWorkerInvocation>(
                new WorkerInvocationException(
                    disposition,
                    detailCode,
                    new IOException("injected")));

        public Task<WorkerStateSnapshot> StateAsync(
            bool listAvailable,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new WorkerStateSnapshot(
                    RequestId: 1,
                    Available: false,
                    Text: string.Empty,
                    DetailCode: "state_unavailable"));

        public Task<WorkerContainmentResult> StopAsync(
            WorkerContainmentReason reason,
            CancellationToken cancellationToken) =>
            Task.FromResult(WorkerContainmentResult.Confirmed());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
