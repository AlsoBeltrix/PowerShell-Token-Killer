using System.Collections.Immutable;
using PtkMcpServer.GuardianHost;
using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class PrivateHostPreparedDispatchAuthorizerTests
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
    public async Task Exact_worker_plan_is_projected_and_authorized_once()
    {
        var control = new RecordingControlSink();
        var authorizer = new PrivateHostPreparedDispatchAuthorizer(
            Identity,
            control,
            unixTimeMilliseconds: () => Deadline - 1);
        await using var slot = Slot();
        var request = Request();

        await authorizer.AuthorizeAsync(
            request,
            slot,
            Descriptor(),
            TestContext.Current.CancellationToken);

        var source = Assert.IsType<PreparedDispatchAuthorizationRequestedEvent>(
            control.Source);
        Assert.Equal(request.RequestId, source.RequestId);
        Assert.Equal(Alias, source.SessionAlias);
        Assert.Equal(Transition, source.SessionTransitionVersion);
        Assert.Equal(Worker.BootId, source.WorkerIdentity?.BootId);
        Assert.Equal(Worker.Generation, source.WorkerIdentity?.Generation);
        Assert.Equal(Operation.PlanId, source.OperationIdentity?.PlanId);
        Assert.Equal(Operation.OperationId, source.OperationIdentity?.OperationId);

        var projected = source.Descriptor;
        Assert.Equal(Operation.PlanId, projected.PlanId);
        Assert.Equal(Worker.BootId, projected.WorkerIdentity.BootId);
        Assert.Equal(Worker.Generation, projected.WorkerIdentity.Generation);
        Assert.Equal(Deadline, projected.DeadlineUnixTimeMilliseconds);
        Assert.Equal(Digest('1'), projected.ScriptDigest);
        Assert.Equal(GuardianHostExecutionDomain.MixedDataflow, projected.Domain);
        Assert.Equal(GuardianHostRequestedExecutionRoute.Rtk, projected.RequestedRoute);
        Assert.Equal(GuardianHostEffectiveExecutionRoute.BashViaRtk, projected.EffectiveRoute);
        Assert.Equal(
            GuardianHostPreExecutionValidation.BashSyntax,
            projected.PreExecutionValidation);
        Assert.Equal(GuardianHostResolutionContext.Warm, projected.ResolutionContext);
        Assert.Equal(
            GuardianHostOutputProvenance.RtkFiltered,
            projected.OutputProvenance);
        Assert.Equal(
            [
                GuardianHostEffectiveExecutionRoute.PowerShellDirect,
                GuardianHostEffectiveExecutionRoute.NativeDirect,
            ],
            projected.PermittedFallbacks);
        Assert.Equal(
            GuardianHostExecutionFallbackReason.RtkTargetResolutionChanged,
            projected.FallbackReason);
        Assert.Equal(Digest('2'), projected.WorkingDirectoryDigest);
        Assert.Equal(Digest('3'), projected.RtkBinaryDigest);
        Assert.Equal(Digest('4'), projected.BashBinaryDigest);
        Assert.Equal(Digest('5'), projected.OutputShapingRtkBinaryDigest);
        Assert.Equal(1, control.ExchangeCount);
    }

    [Fact]
    public async Task Expired_or_mismatched_preparation_never_reaches_guardian()
    {
        var control = new RecordingControlSink();
        var authorizer = new PrivateHostPreparedDispatchAuthorizer(
            Identity,
            control,
            unixTimeMilliseconds: () => Deadline);
        await using var slot = Slot();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await authorizer.AuthorizeAsync(
                Request(),
                slot,
                Descriptor(),
                TestContext.Current.CancellationToken));
        Assert.Equal(0, control.ExchangeCount);

        authorizer = new PrivateHostPreparedDispatchAuthorizer(
            Identity,
            control,
            unixTimeMilliseconds: () => Deadline - 1);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await authorizer.AuthorizeAsync(
                Request(),
                slot,
                Descriptor() with { WorkerBootId = Guid.NewGuid() },
                TestContext.Current.CancellationToken));
        Assert.Equal(0, control.ExchangeCount);
    }

    [Fact]
    public async Task Foreground_cannot_authorize_a_cold_preparation()
    {
        var control = new RecordingControlSink();
        var authorizer = new PrivateHostPreparedDispatchAuthorizer(
            Identity,
            control,
            unixTimeMilliseconds: () => Deadline - 1);
        await using var slot = Slot();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await authorizer.AuthorizeAsync(
                Request(),
                slot,
                Descriptor() with
                {
                    ResolutionContext = ResolutionContext.Cold,
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(0, control.ExchangeCount);
    }

    [Fact]
    public async Task Inexact_guardian_control_is_rejected()
    {
        var control = new RecordingControlSink(
            static source =>
            {
                var prepared =
                    Assert.IsType<PreparedDispatchAuthorizationRequestedEvent>(
                        source);
                return ExactResponse(
                    prepared,
                    descriptorDigest: Digest('f'));
            });
        var authorizer = new PrivateHostPreparedDispatchAuthorizer(
            Identity,
            control,
            unixTimeMilliseconds: () => Deadline - 1);
        await using var slot = Slot();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await authorizer.AuthorizeAsync(
                Request(),
                slot,
                Descriptor(),
                TestContext.Current.CancellationToken));
        Assert.Equal(1, control.ExchangeCount);
    }

    [Fact]
    public void Projection_maps_every_worker_enum_value_explicitly()
    {
        var descriptor = Descriptor() with
        {
            Domain = null,
            PermittedFallbacks = ImmutableArray<ExecutionPath>.Empty,
            FallbackReason = null,
        };

        Assert.Null(
            PrivateHostPreparedPlanProjection.Project(descriptor).Domain);
        foreach (var domain in Enum.GetValues<ExecutionDomain>())
        {
            Assert.Equal(
                domain.ToString(),
                PrivateHostPreparedPlanProjection
                    .Project(descriptor with { Domain = domain })
                    .Domain?.ToString());
        }
        foreach (var route in Enum.GetValues<RequestedExecutionRoute>())
        {
            var expected = route == RequestedExecutionRoute.PowerShell
                ? "Pwsh"
                : route.ToString();
            Assert.Equal(
                expected,
                PrivateHostPreparedPlanProjection
                    .Project(descriptor with { RequestedRoute = route })
                    .RequestedRoute.ToString());
        }
        foreach (var route in Enum.GetValues<ExecutionPath>())
        {
            Assert.Equal(
                route.ToString(),
                PrivateHostPreparedPlanProjection
                    .Project(descriptor with { EffectiveRoute = route })
                    .EffectiveRoute.ToString());
        }
        foreach (var validation in Enum.GetValues<PreExecutionValidation>())
        {
            Assert.Equal(
                validation.ToString(),
                PrivateHostPreparedPlanProjection
                    .Project(descriptor with
                    {
                        PreExecutionValidation = validation,
                    })
                    .PreExecutionValidation.ToString());
        }
        foreach (var context in Enum.GetValues<ResolutionContext>())
        {
            Assert.Equal(
                context.ToString(),
                PrivateHostPreparedPlanProjection
                    .Project(descriptor with { ResolutionContext = context })
                    .ResolutionContext.ToString());
        }
        foreach (var provenance in Enum.GetValues<OutputProvenance>())
        {
            Assert.Equal(
                provenance.ToString(),
                PrivateHostPreparedPlanProjection
                    .Project(descriptor with { OutputProvenance = provenance })
                    .OutputProvenance.ToString());
        }
        foreach (var reason in Enum.GetValues<ExecutionFallbackReason>())
        {
            Assert.Equal(
                reason.ToString(),
                PrivateHostPreparedPlanProjection
                    .Project(descriptor with { FallbackReason = reason })
                    .FallbackReason?.ToString());
        }
    }

    private static PrivateHostWorkerSlot Slot() => new(
        Binding(),
        Worker,
        new FakeProcessClient());

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

    private static WorkerPreparedPlanDescriptor Descriptor() => new(
        Operation.PlanId.Value,
        Worker.BootId.Value,
        Digest('1').Value,
        Worker.Generation.Value,
        DateTimeOffset.FromUnixTimeMilliseconds(Deadline),
        ExecutionDomain.MixedDataflow,
        RequestedExecutionRoute.Rtk,
        ExecutionPath.BashViaRtk,
        PreExecutionValidation.BashSyntax,
        ResolutionContext.Warm,
        OutputProvenance.RtkFiltered,
        [
            ExecutionPath.PowerShellDirect,
            ExecutionPath.NativeDirect,
        ],
        ExecutionFallbackReason.RtkTargetResolutionChanged,
        Digest('2').Value,
        Digest('3').Value,
        Digest('4').Value,
        Digest('5').Value);

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

    private static PreparedDispatchAuthorizeRequest ExactResponse(
        PreparedDispatchAuthorizationRequestedEvent source,
        Sha256Digest? descriptorDigest = null) => new(
        Guardian,
        Host,
        HostGeneration,
        new PrivateRequestId(99),
        source.Descriptor.DeadlineUnixTimeMilliseconds,
        Alias,
        Transition,
        Worker,
        Operation,
        source.EventSequence,
        descriptorDigest ?? source.Descriptor.DescriptorDigest);

    private sealed class RecordingControlSink(
        Func<GuardianHostEvent, GuardianHostRequest>? response = null) :
        IPrivateHostControlEventSink
    {
        private readonly Func<GuardianHostEvent, GuardianHostRequest> _response =
            response ?? (static source => ExactResponse(
                Assert.IsType<PreparedDispatchAuthorizationRequestedEvent>(
                    source)));
        private int _exchangeCount;

        internal int ExchangeCount => Volatile.Read(ref _exchangeCount);
        internal GuardianHostEvent? Source { get; private set; }

        public Task<GuardianHostRequest> ExchangeControlAsync(
            Func<HostEventSequence, GuardianHostEvent> createEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _exchangeCount);
            Source = createEvent(new HostEventSequence(7));
            return Task.FromResult(_response(Source));
        }
    }

    private sealed class FakeProcessClient : IWorkerProcessClient
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

        public Task ContainForRecoveryAsync() => Task.CompletedTask;

        public Task ShutdownAsync(
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
