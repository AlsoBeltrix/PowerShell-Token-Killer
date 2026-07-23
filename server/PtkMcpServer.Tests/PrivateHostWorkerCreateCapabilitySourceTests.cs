using PtkMcpServer.GuardianHost;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class PrivateHostWorkerCreateCapabilitySourceTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration HostGeneration = new(7);
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(3);
    private static readonly Sha256Digest BindingDigest = new(new string('a', 64));
    private static readonly CapabilityToken Token = new(
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

    [Fact]
    public async Task Exact_grant_advances_past_manifest_high_water_and_is_consumed_once()
    {
        WorkerCreateCapabilityRequestedEvent? source = null;
        var control = new RecordingControlSink(createEvent =>
        {
            source = Assert.IsType<WorkerCreateCapabilityRequestedEvent>(
                createEvent(new HostEventSequence(11)));
            return new WorkerCreateCapabilityGrantRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(19),
                deadlineUnixTimeMilliseconds: 500,
                Alias,
                Transition,
                new WorkerGeneration(8),
                source.EventSequence,
                Token);
        });
        var authority = new PrivateHostWorkerCreateCapabilitySource(
            Identity(),
            control,
            unixTimeMilliseconds: () => 100);

        var capability = await authority.RequestAsync(
            Binding(),
            new WorkerGenerationHighWatermark(7),
            startupDeadlineUnixTimeMilliseconds: 500,
            TestContext.Current.CancellationToken);

        Assert.NotNull(source);
        Assert.Equal(BindingDigest, source.BindingDigest);
        Assert.Equal(500, source.StartupDeadlineUnixTimeMilliseconds);
        var consumed = capability.Consume();
        Assert.Equal(8, consumed.WorkerGeneration.Value);
        Assert.Same(Token, consumed.Token);
        Assert.Throws<InvalidOperationException>(() => capability.Consume());
        Assert.Equal(1, control.ExchangeCount);
    }

    [Fact]
    public async Task Stale_generation_and_wrong_control_type_are_rejected()
    {
        var stale = new PrivateHostWorkerCreateCapabilitySource(
            Identity(),
            new RecordingControlSink(createEvent =>
            {
                var source = createEvent(new HostEventSequence(1));
                return new WorkerCreateCapabilityGrantRequest(
                    Guardian,
                    Host,
                    HostGeneration,
                    new PrivateRequestId(2),
                    500,
                    Alias,
                    Transition,
                    new WorkerGeneration(7),
                    source.EventSequence,
                    Token);
            }),
            () => 100);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await stale.RequestAsync(
                Binding(),
                new WorkerGenerationHighWatermark(7),
                500,
                TestContext.Current.CancellationToken));

        var wrongType = new PrivateHostWorkerCreateCapabilitySource(
            Identity(),
            new RecordingControlSink(createEvent =>
            {
                var source = createEvent(new HostEventSequence(1));
                return new WorkerContainmentPendingAckRequest(
                    Guardian,
                    Host,
                    HostGeneration,
                    new PrivateRequestId(2),
                    500,
                    Alias,
                    Transition,
                    new GuardianHostWorkerIdentity(
                        new WorkerBootId(
                            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")),
                        new WorkerGeneration(8)),
                    source.EventSequence);
            }),
            () => 100);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await wrongType.RequestAsync(
                Binding(),
                new WorkerGenerationHighWatermark(7),
                500,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Expired_deadline_never_emits_an_event_and_late_grant_cannot_be_consumed()
    {
        var control = new RecordingControlSink(_ =>
            throw new InvalidOperationException("Expired request reached the wire."));
        var expired = new PrivateHostWorkerCreateCapabilitySource(
            Identity(),
            control,
            () => 500);
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await expired.RequestAsync(
                Binding(),
                new WorkerGenerationHighWatermark(7),
                500,
                TestContext.Current.CancellationToken));
        Assert.Equal(0, control.ExchangeCount);

        var now = 100L;
        var late = new PrivateHostWorkerCreateCapabilitySource(
            Identity(),
            new RecordingControlSink(createEvent =>
            {
                var source = createEvent(new HostEventSequence(1));
                now = 500;
                return new WorkerCreateCapabilityGrantRequest(
                    Guardian,
                    Host,
                    HostGeneration,
                    new PrivateRequestId(2),
                    500,
                    Alias,
                    Transition,
                    new WorkerGeneration(8),
                    source.EventSequence,
                    Token);
            }),
            () => now);
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await late.RequestAsync(
                Binding(),
                new WorkerGenerationHighWatermark(7),
                500,
                TestContext.Current.CancellationToken));
    }

    private static PrivateHostServerIdentity Identity() => new(
        Guardian,
        Host,
        HostGeneration,
        hostPid: 42);

    private static RecoveryBinding Binding() => new(
        Alias,
        RecoveryBindingKind.Default,
        templateName: null,
        templateDigest: null,
        bootstrapDigest: null,
        allowColdBackground: true,
        DesiredSessionState.Ready,
        Transition,
        BindingDigest);

    private sealed class RecordingControlSink(
        Func<Func<HostEventSequence, GuardianHostEvent>, GuardianHostRequest> exchange)
        : IPrivateHostControlEventSink
    {
        private int _exchangeCount;

        internal int ExchangeCount => Volatile.Read(ref _exchangeCount);

        public Task<GuardianHostRequest> ExchangeControlAsync(
            Func<HostEventSequence, GuardianHostEvent> createEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _exchangeCount);
            return Task.FromResult(exchange(createEvent));
        }
    }
}
