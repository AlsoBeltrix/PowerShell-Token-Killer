using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PtkMcpServer.GuardianHost;
using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class PrivateHostWorkerEventBridgeTests
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
    public async Task Exact_validator_facts_are_forwarded_in_order()
    {
        var events = new RecordingEventSink();
        var bridge = new PrivateHostWorkerEventBridge(Identity, events);
        await using var slot = Slot();
        var descriptor = BashDescriptor();
        var registration = bridge.Register(
            ForegroundRequest(),
            slot,
            descriptor);
        registration.MarkCommitAuthorized();

        await bridge.HandleAsync(
            ValidatorStarted(descriptor),
            TestContext.Current.CancellationToken);
        await bridge.HandleAsync(
            ValidatorCompleted(
                descriptor,
                processStarted: true,
                exitCode: 0,
                rootTerminationConfirmed: true),
            TestContext.Current.CancellationToken);
        registration.CompleteForeground();

        Assert.Equal(0, bridge.RegistrationCount);
        Assert.Collection(
            events.Events,
            item =>
            {
                var started =
                    Assert.IsType<PreparedValidatorLifecycleEvent>(item);
                Assert.Equal(GuardianHostValidatorPhase.Started, started.Phase);
                Assert.Null(started.ExitCode);
                Assert.Equal(Digest('b'), started.ValidatorBinaryDigest);
                Assert.Equal(Operation, started.OperationIdentity);
                Assert.Equal(new PrivateRequestId(8), started.RequestId);
            },
            item =>
            {
                var completed =
                    Assert.IsType<PreparedValidatorLifecycleEvent>(item);
                Assert.Equal(
                    GuardianHostValidatorPhase.Completed,
                    completed.Phase);
                Assert.Equal(0, completed.ExitCode);
                Assert.Equal(Digest('b'), completed.ValidatorBinaryDigest);
            });
    }

    [Fact]
    public async Task Validator_no_start_completion_preserves_null_exit_fact()
    {
        var events = new RecordingEventSink();
        var bridge = new PrivateHostWorkerEventBridge(Identity, events);
        await using var slot = Slot();
        var descriptor = BashDescriptor();
        var registration = bridge.Register(
            ForegroundRequest(),
            slot,
            descriptor);
        registration.MarkCommitAuthorized();

        await bridge.HandleAsync(
            ValidatorCompleted(
                descriptor,
                processStarted: false,
                exitCode: null,
                rootTerminationConfirmed: null),
            TestContext.Current.CancellationToken);
        registration.CompleteForeground();

        var completed = Assert.IsType<PreparedValidatorLifecycleEvent>(
            Assert.Single(events.Events));
        Assert.Equal(GuardianHostValidatorPhase.Completed, completed.Phase);
        Assert.Null(completed.ExitCode);
    }

    [Fact]
    public async Task Background_terminal_racing_start_response_is_retained_and_forwarded_once()
    {
        var events = new RecordingEventSink();
        var bridge = new PrivateHostWorkerEventBridge(Identity, events);
        await using var slot = Slot();
        var descriptor = BackgroundDescriptor();
        var registration = bridge.Register(
            BackgroundRequest(),
            slot,
            descriptor);
        registration.MarkCommitAuthorized();

        await bridge.HandleAsync(
            JobTerminal(descriptor),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, bridge.RegistrationCount);
        registration.CompleteBackgroundStart(started: true);
        Assert.Equal(0, bridge.RegistrationCount);
        var terminal = Assert.IsType<JobLifecycleEvent>(
            Assert.Single(events.Events));
        Assert.Equal(new PublicJobId(9001), terminal.PublicJobId);
        Assert.Equal(GuardianHostJobState.Completed, terminal.State);
        Assert.Equal(0, terminal.ExitCode);
        Assert.Equal(GuardianHostOutputState.Unavailable, terminal.OutputState);
        Assert.Equal(Operation, terminal.OperationIdentity);
        Assert.Null(terminal.RequestId);

        var replay = await Assert.ThrowsAsync<WorkerProtocolException>(
            async () => await bridge.HandleAsync(
                JobTerminal(descriptor),
                TestContext.Current.CancellationToken));
        Assert.Equal("invalid_worker_event_correlation", replay.DetailCode);
        Assert.Single(events.Events);
    }

    [Fact]
    public async Task Mismatched_descriptor_digest_never_reaches_guardian()
    {
        var events = new RecordingEventSink();
        var bridge = new PrivateHostWorkerEventBridge(Identity, events);
        await using var slot = Slot();
        var descriptor = BackgroundDescriptor();
        var registration = bridge.Register(
            BackgroundRequest(),
            slot,
            descriptor);
        registration.MarkCommitAuthorized();

        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(
            async () => await bridge.HandleAsync(
                JobTerminal(descriptor, descriptorDigest: Digest('f').Value),
                TestContext.Current.CancellationToken));

        Assert.Equal("invalid_worker_event_correlation", failure.DetailCode);
        Assert.Empty(events.Events);
        Assert.Equal(1, bridge.RegistrationCount);
        bridge.RetireWorker(Worker);
        Assert.Equal(0, bridge.RegistrationCount);
    }

    [Fact]
    public async Task Refused_background_start_cannot_later_emit_a_terminal()
    {
        var events = new RecordingEventSink();
        var bridge = new PrivateHostWorkerEventBridge(Identity, events);
        await using var slot = Slot();
        var descriptor = BackgroundDescriptor();
        var registration = bridge.Register(
            BackgroundRequest(),
            slot,
            descriptor);
        registration.MarkCommitAuthorized();
        registration.CompleteBackgroundStart(started: false);

        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(
            async () => await bridge.HandleAsync(
                JobTerminal(descriptor),
                TestContext.Current.CancellationToken));

        Assert.Equal("invalid_worker_event_correlation", failure.DetailCode);
        Assert.Empty(events.Events);
        Assert.Equal(0, bridge.RegistrationCount);
    }

    private static PrivateHostWorkerSlot Slot() => new(
        Binding(),
        Worker,
        new StubProcessClient());

    private static RecoveryBinding Binding() => new(
        Alias,
        RecoveryBindingKind.Default,
        templateName: null,
        templateDigest: null,
        bootstrapDigest: null,
        allowColdBackground: true,
        DesiredSessionState.Ready,
        Transition,
        Digest('a'));

    private static OperationRequest ForegroundRequest() => new(
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
            Call(),
            Dispatch(),
            Output(),
            "printf ok",
            raw: false,
            GuardianHostInvokeRoute.Rtk));

    private static OperationRequest BackgroundRequest() => new(
        Guardian,
        Host,
        HostGeneration,
        new PrivateRequestId(9),
        Deadline,
        Alias,
        Transition,
        Worker,
        Operation,
        new InvokeBackgroundOperation(
            Call(),
            Dispatch(),
            Output(),
            "Get-Date",
            raw: false,
            GuardianHostInvokeRoute.Pwsh,
            new PublicJobId(9001)));

    private static CallId Call() => new(
        Guid.Parse("11111111-1111-7111-8111-111111111111"));

    private static DispatchCapability Dispatch() => new(
        Token(0x22),
        Call(),
        Deadline);

    private static OutputCapability Output() => new(
        Token(0x33),
        1024,
        Deadline);

    private static WorkerPreparedPlanDescriptor BashDescriptor() => new(
        Operation.PlanId.Value,
        Worker.BootId.Value,
        Sha256Digest.Compute(Encoding.UTF8.GetBytes("printf ok")).Value,
        Worker.Generation.Value,
        DateTimeOffset.FromUnixTimeMilliseconds(Deadline),
        ExecutionDomain.Bash,
        RequestedExecutionRoute.Rtk,
        ExecutionPath.BashViaRtk,
        PreExecutionValidation.BashSyntax,
        ResolutionContext.Warm,
        OutputProvenance.DirectText,
        ImmutableArray<ExecutionPath>.Empty,
        FallbackReason: null,
        WorkingDirectoryDigest: Digest('c').Value,
        RtkBinaryDigest: Digest('d').Value,
        BashBinaryDigest: Digest('b').Value,
        OutputShapingRtkBinaryDigest: null);

    private static WorkerPreparedPlanDescriptor BackgroundDescriptor() => new(
        Operation.PlanId.Value,
        Worker.BootId.Value,
        Sha256Digest.Compute(Encoding.UTF8.GetBytes("Get-Date")).Value,
        Worker.Generation.Value,
        DateTimeOffset.FromUnixTimeMilliseconds(Deadline),
        ExecutionDomain.PowerShell,
        RequestedExecutionRoute.PowerShell,
        ExecutionPath.PowerShellDirect,
        PreExecutionValidation.None,
        ResolutionContext.Cold,
        OutputProvenance.PowerShellObjects,
        ImmutableArray<ExecutionPath>.Empty,
        FallbackReason: null,
        WorkingDirectoryDigest: null,
        RtkBinaryDigest: null,
        BashBinaryDigest: null,
        OutputShapingRtkBinaryDigest: null);

    private static WorkerEnvelope ValidatorStarted(
        WorkerPreparedPlanDescriptor descriptor) => Event(new
        {
            @event = "validator_started",
            generation = Worker.Generation.Value,
            planId = Operation.PlanId.Value.ToString("D"),
            descriptorDigest =
                WorkerPreparedOperationCodec.ComputePreparedDescriptorDigest(
                    descriptor),
            executionPath = "bash_via_rtk",
        });

    private static WorkerEnvelope ValidatorCompleted(
        WorkerPreparedPlanDescriptor descriptor,
        bool processStarted,
        int? exitCode,
        bool? rootTerminationConfirmed) => Event(new
        {
            @event = "validator_completed",
            generation = Worker.Generation.Value,
            planId = Operation.PlanId.Value.ToString("D"),
            descriptorDigest =
                WorkerPreparedOperationCodec.ComputePreparedDescriptorDigest(
                    descriptor),
            executionPath = "bash_via_rtk",
            detailCode = exitCode == 0
                ? "bash_syntax_valid"
                : "bash_validator_start_failed",
            processStarted,
            exitCode,
            rootTerminationConfirmed,
        });

    private static WorkerEnvelope JobTerminal(
        WorkerPreparedPlanDescriptor descriptor,
        string? descriptorDigest = null) => Event(new
        {
            @event = "job_terminal",
            generation = Worker.Generation.Value,
            planId = Operation.PlanId.Value.ToString("D"),
            descriptorDigest = descriptorDigest ??
                WorkerPreparedOperationCodec.ComputePreparedDescriptorDigest(
                    descriptor),
            publicJobId = 9001,
            state = "completed",
            exitCode = 0,
            outputState = "unavailable",
            outputBytes = 0,
            outputDigest = (string?)null,
        });

    private static WorkerEnvelope Event(object payload) => new(
        WorkerProtocol.Version,
        WorkerMessageKind.Event,
        Worker.BootId.Value,
        RequestId: null,
        JsonSerializer.SerializeToElement(payload));

    private static Sha256Digest Digest(char value) =>
        new(new string(value, 64));

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

    private sealed class RecordingEventSink : IPrivateHostEventSink
    {
        private long _sequence;

        internal List<GuardianHostEvent> Events { get; } = [];

        public ValueTask WriteEventAsync(
            Func<HostEventSequence, GuardianHostEvent> createEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(createEvent(new HostEventSequence(++_sequence)));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubProcessClient : IWorkerProcessClient
    {
        public int ProcessId => 42;
        public Guid WorkerBootId => Worker.BootId.Value;
        public long Generation => Worker.Generation.Value;
        public Task Fatal => Task.Delay(Timeout.InfiniteTimeSpan);
        public Task<WorkerDiagnosticReport> Diagnostics =>
            Task.FromResult(new WorkerDiagnosticReport(
                new WorkerDiagnosticSummary(0, 0, false, Digest('0').Value),
                new WorkerDiagnosticSummary(0, 0, false, Digest('0').Value)));

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
            Func<long, CancellationToken, ValueTask>? beforeWrite = null) =>
            throw new NotSupportedException();

        public Task<WorkerOperationResponse> CommitAsync(
            WorkerCommitPayload commit,
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null) =>
            throw new NotSupportedException();

        public Task<WorkerOperationResponse> AbortAsync(
            WorkerAbortPayload abort,
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null) =>
            throw new NotSupportedException();

        public Task ShutdownAsync(
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
