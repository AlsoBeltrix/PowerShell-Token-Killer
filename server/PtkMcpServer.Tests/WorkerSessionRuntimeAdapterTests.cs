using System.Security.Cryptography;
using System.Text;
using PtkMcpServer.Sessions;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerSessionRuntimeAdapterTests
{
    private static readonly Guid BootId =
        Guid.ParseExact("87654321-4321-4321-8321-cba987654321", "D");
    private static readonly DateTimeOffset Deadline =
        DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());

    [Fact]
    public async Task Prepared_adapter_executes_once_only_after_controller_commit()
    {
        var fixture = new RuntimeFixture();
        var controller = new WorkerPreparedInvokeController(
            BootId,
            generation: 7,
            fixture.Runtime,
            new AcceptingObserver());
        var prepare = Prepare(
            "$script:workerPreparedCount = 1 + $script:workerPreparedCount; " +
            "$script:workerPreparedCount");
        try
        {
            var descriptor = await controller.PrepareAsync(
                prepare,
                TestContext.Current.CancellationToken);

            Assert.Equal(prepare.PlanId, descriptor.PlanId);
            var commit = new WorkerCommitPayload(
                prepare.PlanId,
                prepare.ScriptDigest,
                prepare.Generation,
                prepare.DeadlineUtc);
            var first = controller.Commit(commit);
            Assert.Same(first, controller.Commit(commit));

            var terminal = await first.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(WorkerPreparedInvokeTerminalKind.Completed, terminal.Kind);
            Assert.Contains("1", terminal.Text, StringComparison.Ordinal);
        }
        finally
        {
            await controller.CancelAndDrainAsync();
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Prepared_adapter_preserves_a_structured_execution_timeout_terminal()
    {
        var fixture = new RuntimeFixture();
        var controller = new WorkerPreparedInvokeController(
            BootId,
            generation: 7,
            fixture.Runtime,
            new AcceptingObserver());
        var prepare = Prepare(
            "Start-Sleep -Seconds 30",
            deadline: DateTimeOffset.FromUnixTimeMilliseconds(
                DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds()));
        try
        {
            _ = await controller.PrepareAsync(
                prepare,
                TestContext.Current.CancellationToken);

            var terminal = await controller.Commit(new WorkerCommitPayload(
                    prepare.PlanId,
                    prepare.ScriptDigest,
                    prepare.Generation,
                    prepare.DeadlineUtc))
                .WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(WorkerPreparedInvokeTerminalKind.Expired, terminal.Kind);
            Assert.Equal("prepared_execution_timed_out", terminal.DetailCode);
            Assert.Null(terminal.Text);
        }
        finally
        {
            await controller.CancelAndDrainAsync();
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Prepared_background_adapter_starts_the_guardian_reserved_id_and_reports_terminal()
    {
        var fixture = new RuntimeFixture();
        var controller = new WorkerPreparedInvokeController(
            BootId,
            generation: 7,
            fixture.Runtime,
            new AcceptingObserver());
        const long publicJobId = 9001;
        var prepare = Prepare(
            "Write-Output 'worker-background'",
            WorkerPreparedInvokeKind.Background,
            publicJobId);
        try
        {
            var descriptor = await controller.PrepareAsync(
                prepare,
                TestContext.Current.CancellationToken);

            Assert.Equal(ResolutionContext.Cold, descriptor.ResolutionContext);
            var terminal = await controller.Commit(new WorkerCommitPayload(
                    prepare.PlanId,
                    prepare.ScriptDigest,
                    prepare.Generation,
                    prepare.DeadlineUtc))
                .WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(WorkerPreparedInvokeTerminalKind.Completed, terminal.Kind);
            var background = Assert.IsType<WorkerPreparedBackgroundResult>(
                terminal.Background);
            Assert.True(background.Started);
            Assert.Equal(publicJobId, background.PublicJobId);
            var snapshot = await background.Terminal!
                .WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(publicJobId, snapshot.Id);
            Assert.False(snapshot.Running);
            Assert.Equal(0, snapshot.ExitCode);
        }
        finally
        {
            await controller.CancelAndDrainAsync();
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Ordinary_adapter_rejects_invoke_before_parsing_script_work()
    {
        var fixture = new RuntimeFixture();
        try
        {
            var executor = (IWorkerOperationExecutor)fixture.Runtime;
            foreach (var operation in new[]
            {
                WorkerSessionOperationCodec.InvokeOperation,
                WorkerPreparedOperationCodec.BackgroundInvokeOperation,
            })
            {
                var invoke = new WorkerOperationRequest(
                    RequestId: 1,
                    Generation: 7,
                    Deadline,
                    operation,
                    WorkerSessionOperationCodec.CreateArguments(
                        WorkerSessionOperationCodec.InvokeOperation,
                        new WorkerInvokeArguments(
                            "throw 'must not execute'",
                            Raw: false,
                            WorkerInvokeRoute.Auto)));

                var exception = await Assert.ThrowsAsync<WorkerProtocolException>(
                    () => executor.ExecuteAsync(
                        invoke,
                        TestContext.Current.CancellationToken));
                Assert.Equal("ordinary_invoke_forbidden", exception.DetailCode);
            }

            var state = await executor.ExecuteAsync(
                new WorkerOperationRequest(
                    RequestId: 2,
                    Generation: 7,
                    Deadline,
                    WorkerSessionOperationCodec.StateOperation,
                    WorkerSessionOperationCodec.CreateArguments(
                        WorkerSessionOperationCodec.StateOperation,
                        new WorkerStateArguments(ListAvailable: false))),
                TestContext.Current.CancellationToken);
            var parsed = Assert.IsType<WorkerStateResult>(
                WorkerSessionOperationCodec.ParseResult(
                    WorkerSessionOperationCodec.StateOperation,
                    state));
            Assert.Contains("ptk server:", parsed.Text, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WorkerInvokePreparePayload Prepare(
        string script,
        WorkerPreparedInvokeKind kind = WorkerPreparedInvokeKind.Foreground,
        long? publicJobId = null,
        DateTimeOffset? deadline = null) => new(
        Guid.NewGuid(),
        Generation: 7,
        deadline ?? Deadline,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))
            .ToLowerInvariant(),
        new WorkerInvokeArguments(
            script,
            Raw: false,
            kind == WorkerPreparedInvokeKind.Background
                ? WorkerInvokeRoute.Pwsh
                : WorkerInvokeRoute.Auto),
        kind,
        publicJobId);

    private sealed class RuntimeFixture
    {
        private readonly string _jobsRoot = Path.Combine(
            Path.GetTempPath(),
            "ptk-worker-runtime-" + Guid.NewGuid().ToString("N"));
        private readonly RunspaceHost _host;
        private readonly JobManager _jobs;
        private readonly SessionRuntime _session;

        internal RuntimeFixture()
        {
            _host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(30));
            _jobs = new JobManager(_jobsRoot);
            _session = new SessionRuntime(_host, _jobs, new RawUsageCounter());
            Runtime = Assert.IsAssignableFrom<IWorkerSessionRuntime>(_session);
        }

        internal IWorkerSessionRuntime Runtime { get; }

        internal async Task DisposeAsync()
        {
            try
            {
                await Runtime.ShutdownAsync();
            }
            finally
            {
                Runtime.Dispose();
                try { Directory.Delete(_jobsRoot, recursive: true); } catch { }
            }
        }
    }

    private sealed class AcceptingObserver : IWorkerPreparedInvokeObserver
    {
        public ValueTask<bool> RecordValidatorStartedAsync(
            WorkerPreparedPlanDescriptor descriptor,
            ExecutionDispatch dispatch,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> RecordValidatorCompletedAsync(
            WorkerPreparedPlanDescriptor descriptor,
            ExecutionDispatch dispatch,
            BashSyntaxValidationResult result,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }
}
