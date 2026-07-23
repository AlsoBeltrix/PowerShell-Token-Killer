using PtkMcpServer.GuardianHost;
using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class PrivateHostUnixWorkerContainmentRegistryTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration HostGeneration = new(7);
    private static readonly CanonicalAlias Alias = new("named");
    private static readonly SessionTransitionVersion Transition = new(9);
    private static readonly GuardianHostWorkerIdentity Worker = new(
        new WorkerBootId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")),
        new WorkerGeneration(11));
    private static readonly UnixWorkerContainmentIdentity UnixIdentity = new(
        BrokerProcessId: 2001,
        BrokerIdentity: new UnixProcessIdentity(12, 13),
        WorkerProcessId: 2002,
        WorkerIdentity: new UnixProcessIdentity(14, 15),
        WorkerProcessGroup: 2002);

    [Fact]
    public async Task Pending_armed_and_remove_use_exact_logical_and_process_identity()
    {
        var control = new RecordingControlSink();
        var registry = NewRegistry(control);

        await registry.RegisterPendingAsync(UnixIdentity, CancellationToken.None);
        await registry.RegisterArmedAsync(UnixIdentity, CancellationToken.None);
        await registry.RemoveAsync(UnixIdentity, CancellationToken.None);

        Assert.Collection(
            control.Events,
            value => Assert.IsType<WorkerContainmentPendingEvent>(value),
            value => Assert.IsType<WorkerContainmentArmedEvent>(value),
            value => Assert.IsType<WorkerContainmentRemoveRequestedEvent>(value));
        Assert.Equal([1L, 2L, 3L], control.Events.Select(
            value => value.EventSequence.Value));
        Assert.All(control.Events, value =>
        {
            Assert.Equal(Guardian, value.GuardianBootId);
            Assert.Equal(Host, value.HostBootId);
            Assert.Equal(HostGeneration, value.HostGeneration);
            Assert.Equal(Alias, value.SessionAlias);
            Assert.Equal(Transition, value.SessionTransitionVersion);
            Assert.Equal(Worker.BootId, value.WorkerIdentity!.BootId);
            Assert.Equal(Worker.Generation, value.WorkerIdentity.Generation);
            var containment = Assert.IsAssignableFrom<GuardianHostContainmentEvent>(
                value).ContainmentIdentity;
            Assert.Equal((uint)UnixIdentity.BrokerProcessId, containment.BrokerPid);
            Assert.Equal(UnixIdentity.BrokerIdentity.High,
                containment.BrokerStartIdentityHigh);
            Assert.Equal(UnixIdentity.BrokerIdentity.Low,
                containment.BrokerStartIdentityLow);
            Assert.Equal((uint)UnixIdentity.WorkerProcessId, containment.WorkerPid);
            Assert.Equal(UnixIdentity.WorkerIdentity.High,
                containment.WorkerStartIdentityHigh);
            Assert.Equal(UnixIdentity.WorkerIdentity.Low,
                containment.WorkerStartIdentityLow);
            Assert.Equal((uint)UnixIdentity.WorkerProcessGroup,
                containment.ProcessGroupId);
        });
    }

    [Fact]
    public async Task Wrong_acknowledgement_type_and_invalid_process_identity_fail_closed()
    {
        var wrong = new RecordingControlSink
        {
            ReturnWrongAcknowledgement = true,
        };
        var registry = NewRegistry(wrong);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.RegisterPendingAsync(
                UnixIdentity,
                CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.RegisterPendingAsync(
                UnixIdentity with
                {
                    WorkerProcessGroup = UnixIdentity.WorkerProcessId + 1,
                },
                CancellationToken.None).AsTask());
        Assert.Single(wrong.Events);
    }

    private static PrivateHostUnixWorkerContainmentRegistry NewRegistry(
        IPrivateHostControlEventSink control) => new(
            new PrivateHostServerIdentity(
                Guardian,
                Host,
                HostGeneration,
                hostPid: 4242),
            Alias,
            Transition,
            Worker,
            control);

    private sealed class RecordingControlSink : IPrivateHostControlEventSink
    {
        private long _sequence;

        internal List<GuardianHostEvent> Events { get; } = [];
        internal bool ReturnWrongAcknowledgement { get; init; }

        public Task<GuardianHostRequest> ExchangeControlAsync(
            Func<HostEventSequence, GuardianHostEvent> createEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = createEvent(
                new HostEventSequence(Interlocked.Increment(ref _sequence)));
            Events.Add(source);
            var requestId = new PrivateRequestId(_sequence);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(1)
                .ToUnixTimeMilliseconds();
            GuardianHostRequest request = source switch
            {
                WorkerContainmentPendingEvent pending
                    when !ReturnWrongAcknowledgement =>
                    new WorkerContainmentPendingAckRequest(
                        Guardian,
                        Host,
                        HostGeneration,
                        requestId,
                        deadline,
                        Alias,
                        Transition,
                        Worker,
                        pending.EventSequence),
                WorkerContainmentArmedEvent armed =>
                    new WorkerContainmentArmedAckRequest(
                        Guardian,
                        Host,
                        HostGeneration,
                        requestId,
                        deadline,
                        Alias,
                        Transition,
                        Worker,
                        armed.EventSequence),
                WorkerContainmentRemoveRequestedEvent remove =>
                    new WorkerContainmentRemoveAckRequest(
                        Guardian,
                        Host,
                        HostGeneration,
                        requestId,
                        deadline,
                        Alias,
                        Transition,
                        Worker,
                        remove.EventSequence),
                GuardianHostContainmentEvent containment =>
                    new WorkerContainmentArmedAckRequest(
                        Guardian,
                        Host,
                        HostGeneration,
                        requestId,
                        deadline,
                        Alias,
                        Transition,
                        Worker,
                        containment.EventSequence),
                _ => throw new InvalidOperationException(),
            };
            return Task.FromResult(request);
        }
    }
}
