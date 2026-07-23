using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerClientTests
{
    private static readonly Guid BootId =
        Guid.ParseExact("7a762236-3840-4ff4-85bb-b8a587af9b6e", "D");
    private static readonly Guid PlanId =
        Guid.ParseExact("fc15c1cf-f0aa-4d20-a987-a244846fa5a7", "D");
    private static readonly DateTimeOffset Deadline =
        DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());

    [Fact]
    public async Task Client_initializes_executes_state_and_shuts_down_one_generation()
    {
        var runtime = new ClientRuntime();
        await using var harness = new ClientHarness(runtime);
        await harness.Client.InitializeAsync(
            generation: 7,
            Deadline,
            TestContext.Current.CancellationToken);

        var response = await harness.Client.ExecuteAsync(
            WorkerSessionOperationCodec.StateOperation,
            new WorkerStateArguments(ListAvailable: true),
            Deadline,
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkerOperationStatus.Completed, response.Status);
        Assert.Equal(
            new WorkerStateResult("client-state"),
            WorkerSessionOperationCodec.ParseResult(
                WorkerSessionOperationCodec.StateOperation,
                response.Result!.Value));
        Assert.Equal(1, runtime.OrdinaryExecutionCount);
        await harness.Client.ShutdownAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            new WorkerServerExit(WorkerServerExitKind.Shutdown, "shutdown"),
            await harness.ServerRun.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, runtime.ShutdownCount);
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public async Task Client_prepares_hash_bound_plan_and_commits_exactly_once()
    {
        var runtime = new ClientRuntime();
        await using var harness = new ClientHarness(runtime);
        await harness.Client.InitializeAsync(
            generation: 11,
            Deadline,
            TestContext.Current.CancellationToken);
        var prepare = Prepare(generation: 11, script: "Get-Date");

        var descriptor = await harness.Client.PrepareAsync(
            prepare,
            TestContext.Current.CancellationToken);

        Assert.Equal(PlanId, descriptor.PlanId);
        Assert.Equal(BootId, descriptor.WorkerBootId);
        Assert.Equal(0, runtime.PreparedExecutionCount);

        var response = await harness.Client.CommitAsync(
            new WorkerCommitPayload(
                prepare.PlanId,
                prepare.ScriptDigest,
                prepare.Generation,
                prepare.DeadlineUtc),
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkerOperationStatus.Completed, response.Status);
        Assert.Equal(
            new WorkerInvokeResult("client-prepared"),
            WorkerSessionOperationCodec.ParseResult(
                WorkerSessionOperationCodec.InvokeOperation,
                response.Result!.Value));
        Assert.Equal(1, runtime.PreparedExecutionCount);
        await harness.Client.ShutdownAsync(TestContext.Current.CancellationToken);
        _ = await harness.ServerRun.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Client_cancel_targets_the_pending_ordinary_request_id()
    {
        var runtime = new ClientRuntime(blockOrdinary: true);
        await using var harness = new ClientHarness(runtime);
        await harness.Client.InitializeAsync(
            generation: 13,
            Deadline,
            TestContext.Current.CancellationToken);

        var operation = harness.Client.ExecuteAsync(
            WorkerSessionOperationCodec.StateOperation,
            new WorkerStateArguments(ListAvailable: false),
            Deadline,
            TestContext.Current.CancellationToken);
        await runtime.OrdinaryStarted.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        await harness.Client.CancelAsync(
            targetRequestId: 2,
            TestContext.Current.CancellationToken);
        var response = await operation.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkerOperationStatus.Canceled, response.Status);
        Assert.Equal("request_canceled", response.DetailCode);
        await harness.Client.ShutdownAsync(TestContext.Current.CancellationToken);
        _ = await harness.ServerRun.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Client_rejects_script_bearing_ordinary_request_before_wire_admission()
    {
        var runtime = new ClientRuntime();
        await using var harness = new ClientHarness(runtime);
        await harness.Client.InitializeAsync(
            generation: 17,
            Deadline,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            harness.Client.ExecuteAsync(
                WorkerSessionOperationCodec.InvokeOperation,
                new WorkerInvokeArguments("Get-Process", false, WorkerInvokeRoute.Auto),
                Deadline,
                TestContext.Current.CancellationToken));

        Assert.Equal("ordinary_invoke_forbidden", exception.DetailCode);
        Assert.Equal(0, runtime.OrdinaryExecutionCount);
        await harness.Client.ShutdownAsync(TestContext.Current.CancellationToken);
        _ = await harness.ServerRun.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Client_fails_the_generation_on_unknown_operational_event()
    {
        await using var worker = new ScriptedWorker();
        await worker.InitializeAsync(
            generation: 19,
            TestContext.Current.CancellationToken);

        await worker.Events.WriteAsync(
            new WorkerEnvelope(
                WorkerProtocol.Version,
                WorkerMessageKind.Event,
                BootId,
                RequestId: null,
                JsonSerializer.SerializeToElement(new
                {
                    @event = "unrecognized",
                    generation = 19,
                })),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
            await worker.Client.Fatal.WaitAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal("Worker event processing failed.", exception.Message);
    }

    [Fact]
    public async Task Client_rejects_valid_descriptor_that_does_not_correlate_to_prepare()
    {
        await using var worker = new ScriptedWorker();
        await worker.InitializeAsync(
            generation: 23,
            TestContext.Current.CancellationToken);
        var prepare = Prepare(generation: 23, script: "Get-Item");
        var prepareTask = worker.Client.PrepareAsync(
            prepare,
            TestContext.Current.CancellationToken);
        var request = await worker.Requests.ReadAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(WorkerMessageKind.Prepare, request!.Kind);
        var otherPlan = Guid.ParseExact(
            "12345678-9abc-4def-8123-456789abcdef",
            "D");
        var descriptor = new WorkerPreparedPlanDescriptor(
            otherPlan,
            BootId,
            prepare.ScriptDigest,
            prepare.Generation,
            prepare.DeadlineUtc,
            ExecutionDomain.PowerShell,
            RequestedExecutionRoute.Auto,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            OutputProvenance.PowerShellObjects,
            ImmutableArray<ExecutionPath>.Empty,
            FallbackReason: null,
            WorkingDirectoryDigest: null,
            RtkBinaryDigest: null,
            BashBinaryDigest: null,
            OutputShapingRtkBinaryDigest: null);
        await worker.Events.WriteAsync(
            WorkerPreparedOperationProtocol.CreatePreparedResponse(
                BootId,
                request.RequestId!.Value,
                generation: 23,
                descriptor),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(
            () => prepareTask);
        Assert.Equal("prepared_correlation_mismatch", exception.DetailCode);
        var fatal = await Assert.ThrowsAsync<IOException>(async () =>
            await worker.Client.Fatal.WaitAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal("Worker response validation failed.", fatal.Message);
    }

    private static WorkerInvokePreparePayload Prepare(long generation, string script) =>
        new(
            PlanId,
            generation,
            Deadline,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script))),
            new WorkerInvokeArguments(script, false, WorkerInvokeRoute.Auto));

    private sealed class ClientHarness : IAsyncDisposable
    {
        private readonly Pipe _requests = new();
        private readonly Pipe _events = new();
        private readonly Stream _clientRequests;
        private readonly Stream _clientEvents;
        private readonly Stream _serverRequests;
        private readonly Stream _serverEvents;

        internal ClientHarness(IWorkerSessionRuntime runtime)
        {
            _clientRequests = _requests.Writer.AsStream(leaveOpen: true);
            _serverRequests = _requests.Reader.AsStream(leaveOpen: true);
            _serverEvents = _events.Writer.AsStream(leaveOpen: true);
            _clientEvents = _events.Reader.AsStream(leaveOpen: true);
            var server = new WorkerServer(
                _serverRequests,
                _serverEvents,
                (_, _) => Task.FromResult<ISessionLifetime>(runtime),
                BootId);
            Client = new WorkerClient(
                _clientRequests,
                _clientEvents,
                BootId);
            ServerRun = server.RunAsync();
        }

        internal WorkerClient Client { get; }
        internal Task<WorkerServerExit> ServerRun { get; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _requests.Writer.CompleteAsync();
            await _requests.Reader.CompleteAsync();
            await _events.Writer.CompleteAsync();
            await _events.Reader.CompleteAsync();
            await _clientRequests.DisposeAsync();
            await _clientEvents.DisposeAsync();
            await _serverRequests.DisposeAsync();
            await _serverEvents.DisposeAsync();
        }
    }

    private sealed class ScriptedWorker : IAsyncDisposable
    {
        private readonly Pipe _requests = new();
        private readonly Pipe _events = new();
        private readonly Stream _clientRequests;
        private readonly Stream _clientEvents;
        private readonly Stream _workerRequests;
        private readonly Stream _workerEvents;

        internal ScriptedWorker()
        {
            _clientRequests = _requests.Writer.AsStream(leaveOpen: true);
            _workerRequests = _requests.Reader.AsStream(leaveOpen: true);
            _workerEvents = _events.Writer.AsStream(leaveOpen: true);
            _clientEvents = _events.Reader.AsStream(leaveOpen: true);
            Client = new WorkerClient(_clientRequests, _clientEvents, BootId);
            Requests = new WorkerProtocolReader(_workerRequests);
            Events = new WorkerProtocolWriter(_workerEvents);
        }

        internal WorkerClient Client { get; }
        internal WorkerProtocolReader Requests { get; }
        internal WorkerProtocolWriter Events { get; }

        internal async Task InitializeAsync(
            long generation,
            CancellationToken cancellationToken)
        {
            var initialization = Client.InitializeAsync(
                generation,
                Deadline,
                cancellationToken);
            await Events.WriteAsync(
                new WorkerEnvelope(
                    WorkerProtocol.Version,
                    WorkerMessageKind.Event,
                    BootId,
                    RequestId: null,
                    JsonSerializer.SerializeToElement(new { @event = "hello" })),
                cancellationToken);
            var request = await Requests.ReadAsync(cancellationToken);
            Assert.NotNull(request);
            Assert.Equal(WorkerMessageKind.Initialize, request.Kind);
            await Events.WriteAsync(
                new WorkerEnvelope(
                    WorkerProtocol.Version,
                    WorkerMessageKind.Response,
                    BootId,
                    request.RequestId,
                    JsonSerializer.SerializeToElement(new
                    {
                        status = "ready",
                        generation,
                    })),
                cancellationToken);
            await initialization;
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _requests.Writer.CompleteAsync();
            await _requests.Reader.CompleteAsync();
            await _events.Writer.CompleteAsync();
            await _events.Reader.CompleteAsync();
            await _clientRequests.DisposeAsync();
            await _clientEvents.DisposeAsync();
            await _workerRequests.DisposeAsync();
            await _workerEvents.DisposeAsync();
        }
    }

    private sealed class ClientRuntime(bool blockOrdinary = false) : IWorkerSessionRuntime
    {
        private int _ordinaryExecutionCount;
        private int _preparedExecutionCount;

        internal int OrdinaryExecutionCount => Volatile.Read(ref _ordinaryExecutionCount);
        internal int PreparedExecutionCount => Volatile.Read(ref _preparedExecutionCount);
        internal int ShutdownCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal TaskCompletionSource OrdinaryStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JsonElement> ExecuteAsync(
            WorkerOperationRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _ordinaryExecutionCount);
            OrdinaryStarted.TrySetResult();
            if (blockOrdinary)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return WorkerSessionOperationCodec.CreateResult(
                request.Operation,
                new WorkerStateResult("client-state"));
        }

        public async Task<WorkerPreparedRuntimeResult> InvokeAsync(
            WorkerInvokePreparePayload prepare,
            IInvocationAuthorizer authorizer,
            CancellationToken cancellationToken)
        {
            var plan = new ExecutionPlan(
                prepare.Arguments.Script,
                prepare.Arguments.Script,
                ExecutionDomain.PowerShell,
                ExecutionPath.PowerShellDirect,
                PreExecutionValidation.None,
                ResolutionContext.Warm,
                RequestedExecutionRoute.Auto,
                OutputProvenance.PowerShellObjects,
                ImmutableArray<ExecutionPath>.Empty,
                fallbackReason: null,
                rtkExecutableIdentity: null);
            if (!await authorizer.AuthorizePlanAsync(plan, cancellationToken) ||
                !await authorizer.AuthorizeDispatchAsync(
                    ExecutionDispatch.FromPlan(plan),
                    cancellationToken))
            {
                return new WorkerPreparedRuntimeResult(
                    "not-started",
                    UserExecutionStarted: false);
            }

            Interlocked.Increment(ref _preparedExecutionCount);
            return new WorkerPreparedRuntimeResult(
                "client-prepared",
                UserExecutionStarted: true);
        }

        public Task ShutdownAsync()
        {
            ShutdownCount++;
            return Task.CompletedTask;
        }

        public void Dispose() => DisposeCount++;
    }
}
