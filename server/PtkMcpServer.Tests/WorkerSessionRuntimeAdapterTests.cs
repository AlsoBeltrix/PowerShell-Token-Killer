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
    public async Task Ordinary_adapter_rejects_invoke_before_parsing_script_work()
    {
        var fixture = new RuntimeFixture();
        try
        {
            var executor = (IWorkerOperationExecutor)fixture.Runtime;
            var invoke = new WorkerOperationRequest(
                RequestId: 1,
                Generation: 7,
                Deadline,
                WorkerSessionOperationCodec.InvokeOperation,
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

    private static WorkerInvokePreparePayload Prepare(string script) => new(
        Guid.NewGuid(),
        Generation: 7,
        Deadline,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))
            .ToLowerInvariant(),
        new WorkerInvokeArguments(script, Raw: false, WorkerInvokeRoute.Auto));

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
