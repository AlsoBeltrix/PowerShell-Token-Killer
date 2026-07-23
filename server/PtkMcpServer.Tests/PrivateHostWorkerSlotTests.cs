using PtkMcpServer.GuardianHost;
using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class PrivateHostWorkerSlotTests
{
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(3);
    private static readonly WorkerBootId Boot = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly CapabilityToken Token = new(
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

    [Fact]
    public async Task Exact_grant_is_burned_before_one_identity_bound_launch()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(100);
        var capability = new PrivateHostWorkerCreateCapability(
            new WorkerGeneration(8),
            Token,
            deadlineUnixTimeMilliseconds: 30_100,
            () => now.ToUnixTimeMilliseconds());
        var capabilities = new RecordingCapabilitySource(capability);
        var process = new FakeProcessClient(Boot.Value, generation: 8);
        var launch = new RecordingLaunchAuthority(process);
        var factory = new PrivateHostWorkerSlotFactory(
            capabilities,
            launch,
            workerBootId: () => Boot,
            utcNow: () => now);

        await using var slot = await factory.CreateAsync(
            Binding(),
            new WorkerGenerationHighWatermark(7),
            onEvent: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(Boot, slot.Identity.BootId);
        Assert.Equal(8, slot.Identity.Generation.Value);
        Assert.Same(process, slot.Process);
        Assert.Equal(1, capabilities.RequestCount);
        Assert.Equal(30_100, capabilities.DeadlineUnixTimeMilliseconds);
        Assert.Equal(1, launch.LaunchCount);
        Assert.Equal(Binding(), launch.Binding);
        Assert.Equal(slot.Identity, launch.Identity);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(30_100),
            launch.DeadlineUtc);
        Assert.Throws<InvalidOperationException>(() => capability.Consume());
    }

    [Fact]
    public async Task Mismatched_launched_identity_is_contained_and_never_becomes_a_slot()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(100);
        var capability = new PrivateHostWorkerCreateCapability(
            new WorkerGeneration(8),
            Token,
            deadlineUnixTimeMilliseconds: 30_100,
            () => now.ToUnixTimeMilliseconds());
        var mismatched = new FakeProcessClient(Boot.Value, generation: 9);
        var factory = new PrivateHostWorkerSlotFactory(
            new RecordingCapabilitySource(capability),
            new RecordingLaunchAuthority(mismatched),
            workerBootId: () => Boot,
            utcNow: () => now);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await factory.CreateAsync(
                Binding(),
                new WorkerGenerationHighWatermark(7),
                onEvent: null,
                TestContext.Current.CancellationToken));

        Assert.True(mismatched.Disposed);
        Assert.Throws<InvalidOperationException>(() => capability.Consume());
    }

    [Fact]
    public async Task Cancellation_after_grant_but_before_consumption_starts_no_launch()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(100);
        var capability = new PrivateHostWorkerCreateCapability(
            new WorkerGeneration(8),
            Token,
            deadlineUnixTimeMilliseconds: 30_100,
            () => now.ToUnixTimeMilliseconds());
        using var cancellation = new CancellationTokenSource();
        var capabilities = new RecordingCapabilitySource(
            capability,
            afterRequest: cancellation.Cancel);
        var launch = new RecordingLaunchAuthority(
            new FakeProcessClient(Boot.Value, generation: 8));
        var factory = new PrivateHostWorkerSlotFactory(
            capabilities,
            launch,
            workerBootId: () => Boot,
            utcNow: () => now);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await factory.CreateAsync(
                Binding(),
                new WorkerGenerationHighWatermark(7),
                onEvent: null,
                cancellation.Token));

        Assert.Equal(0, launch.LaunchCount);
        var consumed = capability.Consume();
        Assert.Equal(8, consumed.WorkerGeneration.Value);
    }

    private static RecoveryBinding Binding() => new(
        Alias,
        RecoveryBindingKind.Default,
        templateName: null,
        templateDigest: null,
        bootstrapDigest: null,
        allowColdBackground: true,
        DesiredSessionState.Ready,
        Transition,
        new Sha256Digest(new string('b', 64)));

    private sealed class RecordingCapabilitySource(
        PrivateHostWorkerCreateCapability capability,
        Action? afterRequest = null) : IPrivateHostWorkerCreateCapabilitySource
    {
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);
        internal long DeadlineUnixTimeMilliseconds { get; private set; }

        public ValueTask<PrivateHostWorkerCreateCapability> RequestAsync(
            RecoveryBinding binding,
            WorkerGenerationHighWatermark generationHighWatermark,
            long startupDeadlineUnixTimeMilliseconds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Binding(), binding);
            Assert.Equal(7, generationHighWatermark.Value);
            Interlocked.Increment(ref _requestCount);
            DeadlineUnixTimeMilliseconds = startupDeadlineUnixTimeMilliseconds;
            afterRequest?.Invoke();
            return ValueTask.FromResult(capability);
        }
    }

    private sealed class RecordingLaunchAuthority(IWorkerProcessClient process) :
        IPrivateHostWorkerLaunchAuthority
    {
        private int _launchCount;

        internal int LaunchCount => Volatile.Read(ref _launchCount);
        internal RecoveryBinding? Binding { get; private set; }
        internal GuardianHostWorkerIdentity? Identity { get; private set; }
        internal DateTimeOffset DeadlineUtc { get; private set; }

        public Task<IWorkerProcessClient> LaunchAsync(
            RecoveryBinding binding,
            GuardianHostWorkerIdentity workerIdentity,
            DateTimeOffset deadlineUtc,
            Func<WorkerEnvelope, CancellationToken, ValueTask>? onEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _launchCount);
            Binding = binding;
            Identity = workerIdentity;
            DeadlineUtc = deadlineUtc;
            Assert.Null(onEvent);
            return Task.FromResult(process);
        }
    }

    private sealed class FakeProcessClient(Guid bootId, long generation) :
        IWorkerProcessClient
    {
        private int _disposed;

        public int ProcessId => 42;
        public Guid WorkerBootId { get; } = bootId;
        public long Generation { get; } = generation;
        public Task Fatal { get; } = Task.Delay(Timeout.InfiniteTimeSpan);
        public Task<WorkerDiagnosticReport> Diagnostics { get; } =
            Task.FromResult(new WorkerDiagnosticReport(
                new WorkerDiagnosticSummary(0, 0, false, new string('0', 64)),
                new WorkerDiagnosticSummary(0, 0, false, new string('0', 64))));
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;

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

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }
}
