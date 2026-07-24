using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PtkMcpServer.GuardianHost;
using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class PrivateHostPreparedInvokeDispatcherTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration HostGeneration = new(3);
    private static readonly PrivateHostServerIdentity Identity = new(
        Guardian,
        Host,
        HostGeneration,
        hostPid: 42);
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(4);
    private static readonly GuardianHostWorkerIdentity Worker = new(
        new WorkerBootId(
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")),
        new WorkerGeneration(5));
    private static readonly GuardianHostOperationIdentity Operation = new(
        new PlanId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
        new OperationId(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")));
    private const long Deadline = 10_000;

    [Fact]
    public async Task Guardian_authorization_and_exact_delivery_precede_commit_byte()
    {
        var order = new List<string>();
        var process = new RecordingProcessClient(order);
        await using var slot = Slot(process);
        var events = new RecordingEventSink(order);
        var control = new RecordingControlSink(order);
        var dispatcher = Dispatcher(events, control);

        var response = await dispatcher.ExecuteForegroundAsync(
            Request(),
            slot,
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkerOperationStatus.Completed, response.Status);
        Assert.Equal(
        [
            "prepare_write",
            "commit_reserved",
            "authorize_event",
            "authorize_response",
            "delivery_write_started",
            "commit_write",
            "delivery_terminal_decoded",
        ], order);
        Assert.Equal(0, process.AbortCount);
        Assert.Collection(
            events.Events,
            write =>
            {
                Assert.Equal(
                    GuardianHostDeliveryState.WriteStarted,
                    write.DeliveryState);
                Assert.Equal(
                    new PrivateRequestId(71),
                    write.WorkerRequestId);
            },
            terminal =>
            {
                Assert.Equal(
                    GuardianHostDeliveryState.TerminalDecoded,
                    terminal.DeliveryState);
                Assert.Equal(
                    new PrivateRequestId(71),
                    terminal.WorkerRequestId);
            });
    }

    [Fact]
    public async Task Authorization_failure_aborts_without_writing_commit()
    {
        var order = new List<string>();
        var process = new RecordingProcessClient(order);
        await using var slot = Slot(process);
        var events = new RecordingEventSink(order);
        var control = new RecordingControlSink(
            order,
            exactDescriptor: false);
        var dispatcher = Dispatcher(events, control);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await dispatcher.ExecuteForegroundAsync(
                Request(),
                slot,
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain("commit_write", order);
        Assert.Equal(1, process.AbortCount);
        Assert.Equal(
        [
            "prepare_write",
            "commit_reserved",
            "authorize_event",
            "authorize_response",
            "abort_write",
            "delivery_not_dispatched",
        ], order);
        var refusal = Assert.Single(events.Events);
        Assert.Equal(
            GuardianHostDeliveryState.NotDispatched,
            refusal.DeliveryState);
        Assert.Null(refusal.WorkerRequestId);
    }

    [Fact]
    public async Task Delivery_failure_after_authorization_aborts_before_commit()
    {
        var order = new List<string>();
        var process = new RecordingProcessClient(order);
        await using var slot = Slot(process);
        var events = new RecordingEventSink(
            order,
            failWriteStarted: true);
        var dispatcher = Dispatcher(
            events,
            new RecordingControlSink(order));

        await Assert.ThrowsAsync<IOException>(async () =>
            await dispatcher.ExecuteForegroundAsync(
                Request(),
                slot,
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain("commit_write", order);
        Assert.Equal(1, process.AbortCount);
        Assert.Equal(
        [
            "prepare_write",
            "commit_reserved",
            "authorize_event",
            "authorize_response",
            "delivery_write_started",
            "abort_write",
            "delivery_not_dispatched",
        ], order);
    }

    [Fact]
    public async Task Commit_transport_failure_after_barrier_is_never_aborted()
    {
        var order = new List<string>();
        var process = new RecordingProcessClient(
            order,
            failAfterCommitWrite: true);
        await using var slot = Slot(process);
        var events = new RecordingEventSink(order);
        var dispatcher = Dispatcher(
            events,
            new RecordingControlSink(order));

        await Assert.ThrowsAsync<IOException>(async () =>
            await dispatcher.ExecuteForegroundAsync(
                Request(),
                slot,
                TestContext.Current.CancellationToken));

        Assert.Contains("commit_write", order);
        Assert.Equal(0, process.AbortCount);
        var delivery = Assert.Single(events.Events);
        Assert.Equal(
            GuardianHostDeliveryState.WriteStarted,
            delivery.DeliveryState);
        Assert.Equal(new PrivateRequestId(71), delivery.WorkerRequestId);
    }

    [Fact]
    public async Task Background_dispatch_retains_exact_public_job_until_terminal()
    {
        var order = new List<string>();
        var process = new RecordingProcessClient(order, background: true);
        await using var slot = Slot(process);
        var events = new RecordingEventSink(order);
        var workerEvents = new PrivateHostWorkerEventBridge(Identity, events);
        var dispatcher = Dispatcher(
            events,
            new RecordingControlSink(order),
            workerEvents);

        var result = await dispatcher.ExecuteBackgroundAsync(
            BackgroundRequest(),
            slot,
            TestContext.Current.CancellationToken);

        Assert.True(result.Started);
        Assert.Equal(9001, result.PublicJobId);
        Assert.Equal("Job 9001 started.", result.Text);
        Assert.Equal(1, workerEvents.RegistrationCount);
        Assert.Equal(
        [
            "prepare_write",
            "commit_reserved",
            "authorize_event",
            "authorize_response",
            "delivery_write_started",
            "commit_write",
            "delivery_terminal_decoded",
        ], order);
        workerEvents.RetireWorker(Worker);
        Assert.Equal(0, workerEvents.RegistrationCount);
    }

    [Fact]
    public async Task Background_terminal_is_correlated_during_commit_before_start_response()
    {
        var order = new List<string>();
        var events = new RecordingEventSink(
            order,
            acceptJobTerminal: true);
        var workerEvents = new PrivateHostWorkerEventBridge(Identity, events);
        var process = new RecordingProcessClient(
            order,
            background: true,
            afterCommitWrite: () => workerEvents.HandleAsync(
                    JobTerminal(Descriptor(background: true)),
                    TestContext.Current.CancellationToken)
                .AsTask());
        await using var slot = Slot(process);
        var dispatcher = Dispatcher(
            events,
            new RecordingControlSink(order),
            workerEvents);

        var result = await dispatcher.ExecuteBackgroundAsync(
            BackgroundRequest(),
            slot,
            TestContext.Current.CancellationToken);

        Assert.True(result.Started);
        Assert.Equal(0, workerEvents.RegistrationCount);
        var terminal = Assert.IsType<JobLifecycleEvent>(
            Assert.Single(events.WorkerEvents));
        Assert.Equal(new PublicJobId(9001), terminal.PublicJobId);
        Assert.Equal(
        [
            "prepare_write",
            "commit_reserved",
            "authorize_event",
            "authorize_response",
            "delivery_write_started",
            "commit_write",
            "job_terminal_event",
            "delivery_terminal_decoded",
        ], order);
    }

    private static PrivateHostPreparedInvokeDispatcher Dispatcher(
        IPrivateHostEventSink events,
        IPrivateHostControlEventSink control,
        PrivateHostWorkerEventBridge? workerEvents = null) => new(
        Identity,
        events,
        new PrivateHostPreparedDispatchAuthorizer(
            Identity,
            control,
            unixTimeMilliseconds: () => Deadline - 1),
        workerEvents ?? new PrivateHostWorkerEventBridge(Identity, events));

    private static PrivateHostWorkerSlot Slot(
        IWorkerProcessClient process) => new(
        Binding(),
        Worker,
        process);

    private static RecoveryBinding Binding() => new(
        Alias,
        RecoveryBindingKind.Default,
        templateName: null,
        templateDigest: null,
        bootstrapDigest: null,
        allowColdBackground: true,
        DesiredSessionState.Ready,
        Transition,
        Digest('b'));

    private static OperationRequest Request() => new(
        Guardian,
        Host,
        HostGeneration,
        new PrivateRequestId(8),
        Deadline,
        Alias,
        Transition,
        Worker,
        Operation,
        new InvokeForegroundOperation(
            new CallId(Guid.Parse("11111111-1111-7111-8111-111111111111")),
            new DispatchCapability(
                Token(0x22),
                new CallId(Guid.Parse(
                    "11111111-1111-7111-8111-111111111111")),
                Deadline),
            new OutputCapability(Token(0x33), 1024, Deadline),
            "Get-Date",
            raw: false,
            GuardianHostInvokeRoute.Auto));

    private static OperationRequest BackgroundRequest() => new(
        Guardian,
        Host,
        HostGeneration,
        new PrivateRequestId(8),
        Deadline,
        Alias,
        Transition,
        Worker,
        Operation,
        new InvokeBackgroundOperation(
            new CallId(Guid.Parse("11111111-1111-7111-8111-111111111111")),
            new DispatchCapability(
                Token(0x22),
                new CallId(Guid.Parse(
                    "11111111-1111-7111-8111-111111111111")),
                Deadline),
            new OutputCapability(Token(0x33), 1024, Deadline),
            "Get-Date",
            raw: false,
            GuardianHostInvokeRoute.Auto,
            new PublicJobId(9001)));

    private static WorkerPreparedPlanDescriptor Descriptor(
        bool background = false) => new(
        Operation.PlanId.Value,
        Worker.BootId.Value,
        ScriptDigest().Value,
        Worker.Generation.Value,
        DateTimeOffset.FromUnixTimeMilliseconds(Deadline),
        ExecutionDomain.PowerShell,
        RequestedExecutionRoute.Auto,
        ExecutionPath.PowerShellDirect,
        PreExecutionValidation.None,
        background ? ResolutionContext.Cold : ResolutionContext.Warm,
        OutputProvenance.PowerShellObjects,
        ImmutableArray<ExecutionPath>.Empty,
        FallbackReason: null,
        WorkingDirectoryDigest: null,
        RtkBinaryDigest: null,
        BashBinaryDigest: null,
        OutputShapingRtkBinaryDigest: null);

    private static WorkerEnvelope JobTerminal(
        WorkerPreparedPlanDescriptor descriptor) => new(
        WorkerProtocol.Version,
        WorkerMessageKind.Event,
        Worker.BootId.Value,
        RequestId: null,
        JsonSerializer.SerializeToElement(new
        {
            @event = "job_terminal",
            generation = Worker.Generation.Value,
            planId = Operation.PlanId.Value.ToString("D"),
            descriptorDigest =
                WorkerPreparedOperationCodec.ComputePreparedDescriptorDigest(
                    descriptor),
            publicJobId = 9001,
            state = "completed",
            exitCode = 0,
            outputState = "unavailable",
            outputBytes = 0,
            outputDigest = (string?)null,
        }));

    private static Sha256Digest Digest(char value) =>
        new(new string(value, 64));

    private static Sha256Digest ScriptDigest() =>
        Sha256Digest.Compute(Encoding.UTF8.GetBytes("Get-Date"));

    private static CapabilityToken Token(byte value)
    {
        var bytes = Enumerable.Repeat(
                value,
                ContractLimits.CapabilityTokenBytes)
            .ToArray();
        return new CapabilityToken(
            Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_'));
    }

    private sealed class RecordingProcessClient(
        List<string> order,
        bool failAfterCommitWrite = false,
        bool background = false,
        Func<Task>? afterCommitWrite = null) : IWorkerProcessClient
    {
        private int _abortCount;

        public int ProcessId => 42;
        public Guid WorkerBootId => Worker.BootId.Value;
        public long Generation => Worker.Generation.Value;
        public Task Fatal => Task.Delay(Timeout.InfiniteTimeSpan);
        public Task<WorkerDiagnosticReport> Diagnostics =>
            Task.FromResult(new WorkerDiagnosticReport(
                new WorkerDiagnosticSummary(0, 0, false, Digest('0').Value),
                new WorkerDiagnosticSummary(0, 0, false, Digest('0').Value)));
        internal int AbortCount => Volatile.Read(ref _abortCount);

        public Task<WorkerOperationResponse> ExecuteAsync(
            string operation,
            WorkerSessionOperationArguments arguments,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null) =>
            throw new NotSupportedException();

        public Task<WorkerPreparedPlanDescriptor> PrepareAsync(
            WorkerInvokePreparePayload prepare,
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order.Add("prepare_write");
            Assert.Equal(Operation.PlanId.Value, prepare.PlanId);
            Assert.Equal(
                ScriptDigest().Value,
                prepare.ScriptDigest);
            Assert.Equal(
                background
                    ? WorkerPreparedInvokeKind.Background
                    : WorkerPreparedInvokeKind.Foreground,
                prepare.Kind);
            Assert.Equal(background ? 9001 : null, prepare.PublicJobId);
            return Task.FromResult(Descriptor(background));
        }

        public async Task<WorkerOperationResponse> CommitAsync(
            WorkerCommitPayload commit,
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order.Add("commit_reserved");
            Assert.NotNull(beforeWrite);
            await beforeWrite(71, cancellationToken);
            order.Add("commit_write");
            if (afterCommitWrite is not null)
                await afterCommitWrite();
            if (failAfterCommitWrite)
                throw new IOException("ambiguous commit write");
            if (background)
            {
                return WorkerOperationResponse.Completed(
                    requestId: 71,
                    Worker.Generation.Value,
                    JsonSerializer.SerializeToElement(new
                    {
                        text = "Job 9001 started.",
                        publicJobId = 9001,
                        started = true,
                    }));
            }
            return WorkerOperationResponse.Completed(
                requestId: 71,
                Worker.Generation.Value,
                WorkerSessionOperationCodec.CreateResult(
                    WorkerSessionOperationCodec.InvokeOperation,
                    new WorkerInvokeResult("ok")));
        }

        public Task<WorkerOperationResponse> AbortAsync(
            WorkerAbortPayload abort,
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null)
        {
            Interlocked.Increment(ref _abortCount);
            order.Add("abort_write");
            Assert.Equal(Operation.PlanId.Value, abort.PlanId);
            return Task.FromResult(WorkerOperationResponse.Canceled(
                requestId: 72,
                Worker.Generation.Value,
                "operation_aborted"));
        }

        public Task ShutdownAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingControlSink(
        List<string> order,
        bool exactDescriptor = true) : IPrivateHostControlEventSink
    {
        public Task<GuardianHostRequest> ExchangeControlAsync(
            Func<HostEventSequence, GuardianHostEvent> createEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order.Add("authorize_event");
            var source =
                Assert.IsType<PreparedDispatchAuthorizationRequestedEvent>(
                    createEvent(new HostEventSequence(7)));
            order.Add("authorize_response");
            return Task.FromResult<GuardianHostRequest>(
                new PreparedDispatchAuthorizeRequest(
                    Guardian,
                    Host,
                    HostGeneration,
                    new PrivateRequestId(99),
                    Deadline,
                    Alias,
                    Transition,
                    Worker,
                    Operation,
                    source.EventSequence,
                    exactDescriptor
                        ? source.Descriptor.DescriptorDigest
                        : Digest('f')));
        }
    }

    private sealed class RecordingEventSink(
        List<string> order,
        bool failWriteStarted = false,
        bool acceptJobTerminal = false) : IPrivateHostEventSink
    {
        private long _sequence;

        internal List<OperationDeliveryEvent> Events { get; } = [];
        internal List<GuardianHostEvent> WorkerEvents { get; } = [];

        public ValueTask WriteEventAsync(
            Func<HostEventSequence, GuardianHostEvent> createEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var created = createEvent(new HostEventSequence(++_sequence));
            if (acceptJobTerminal && created is JobLifecycleEvent)
            {
                order.Add("job_terminal_event");
                WorkerEvents.Add(created);
                return ValueTask.CompletedTask;
            }
            var delivery = Assert.IsType<OperationDeliveryEvent>(created);
            order.Add($"delivery_{MachineCode(delivery.DeliveryState)}");
            if (failWriteStarted &&
                delivery.DeliveryState ==
                    GuardianHostDeliveryState.WriteStarted)
            {
                throw new IOException("delivery failed");
            }
            Events.Add(delivery);
            return ValueTask.CompletedTask;
        }

        private static string MachineCode(
            GuardianHostDeliveryState state) => state switch
        {
            GuardianHostDeliveryState.NotDispatched => "not_dispatched",
            GuardianHostDeliveryState.WriteStarted => "write_started",
            GuardianHostDeliveryState.TerminalDecoded => "terminal_decoded",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }
}
