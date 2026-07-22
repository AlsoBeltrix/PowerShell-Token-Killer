using System.Collections.Concurrent;
using System.Reflection;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Ownership;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianJobCapabilityRegistryTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration Generation = new(7);
    private static readonly GuardianHostIdentity HostIdentity = new(
        Guardian,
        Host,
        Generation);
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(3);
    private static readonly GuardianHostWorkerIdentity Worker = new(
        new WorkerBootId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")),
        new WorkerGeneration(11));
    private static readonly GuardianHostOperationIdentity OperationIdentity = new(
        new PlanId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
        new OperationId(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")));

    [Fact]
    public void Reservation_activates_one_capability_bound_to_the_exact_owner()
    {
        using var registry = Registry();
        var registration = Reserve(registry);

        Assert.Equal(1, registration.PublicJobId.Value);
        Assert.Equal(1, registry.TrackedCount);
        Assert.Equal(1, registry.PendingCount);
        Assert.Equal(0, registry.ActiveCount);
        Assert.True(registry.TryActivate(
            registration,
            new InvokeBackgroundResult(registration.PublicJobId, Token(1)),
            out var capability,
            out var failure));

        Assert.Null(failure);
        Assert.Equal(registration.PublicJobId, capability!.PublicJobId);
        Assert.Equal(registration.RegistrationId, capability.RegistrationId);
        Assert.Equal(Token(1), capability.JobCapability);
        Assert.True(capability.MatchesOwner(
            HostIdentity,
            Alias,
            Transition,
            Worker));
        Assert.False(capability.MatchesOwner(
            new GuardianHostIdentity(
                new GuardianBootId(
                    Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")),
                Host,
                Generation),
            Alias,
            Transition,
            Worker));
        Assert.False(capability.MatchesOwner(
            new GuardianHostIdentity(
                Guardian,
                new HostBootId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
                Generation),
            Alias,
            Transition,
            Worker));
        Assert.False(capability.MatchesOwner(
            new GuardianHostIdentity(
                Guardian,
                Host,
                new HostGeneration(Generation.Value + 1)),
            Alias,
            Transition,
            Worker));
        Assert.False(capability.MatchesOwner(
            HostIdentity,
            new CanonicalAlias("other"),
            Transition,
            Worker));
        Assert.False(capability.MatchesOwner(
            HostIdentity,
            Alias,
            new SessionTransitionVersion(Transition.Value + 1),
            Worker));
        Assert.False(capability.MatchesOwner(
            HostIdentity,
            Alias,
            Transition,
            new GuardianHostWorkerIdentity(
                Worker.BootId,
                new WorkerGeneration(Worker.Generation.Value + 1))));
        Assert.Equal(0, registry.PendingCount);
        Assert.Equal(1, registry.ActiveCount);
        Assert.True(registry.TryGetActive(registration.PublicJobId, out var found));
        Assert.Same(capability, found);
    }

    [Fact]
    public void Activation_requires_the_reserved_response_id_and_is_one_shot()
    {
        using var registry = Registry();
        var registration = Reserve(registry);

        Assert.False(registry.TryActivate(
            registration,
            new InvokeBackgroundResult(new PublicJobId(99), Token(2)),
            out var mismatched,
            out var mismatchFailure));
        Assert.Null(mismatched);
        Assert.Equal("public_job_response_mismatch", mismatchFailure);
        Assert.Equal(1, registry.PendingCount);

        Assert.True(registry.TryActivate(
            registration,
            new InvokeBackgroundResult(registration.PublicJobId, Token(3)),
            out var activated,
            out var activationFailure));
        Assert.Null(activationFailure);
        Assert.False(registry.TryActivate(
            registration,
            new InvokeBackgroundResult(registration.PublicJobId, Token(4)),
            out var duplicate,
            out var duplicateFailure));
        Assert.Null(duplicate);
        Assert.Equal("public_job_already_active", duplicateFailure);
        Assert.True(registry.TryGetActive(registration.PublicJobId, out var retained));
        Assert.Same(activated, retained);
        Assert.Equal(Token(3), retained!.JobCapability);
    }

    [Fact]
    public void Forged_registration_cannot_activate_or_cancel_the_reserved_owner()
    {
        using var registry = Registry();
        var registration = Reserve(registry);
        var forged = registration with
        {
            RegistrationId = registration.RegistrationId + 1,
        };

        Assert.False(registry.TryActivate(
            forged,
            new InvokeBackgroundResult(registration.PublicJobId, Token(5)),
            out var capability,
            out var failure));
        Assert.Null(capability);
        Assert.Equal("public_job_registration_unknown", failure);
        Assert.False(registry.TryCancel(forged));
        Assert.Equal(1, registry.PendingCount);
        Assert.True(registry.TryActivate(
            registration,
            new InvokeBackgroundResult(registration.PublicJobId, Token(6)),
            out _,
            out _));
    }

    [Fact]
    public void Canceled_reservation_frees_capacity_without_reusing_its_id()
    {
        using var registry = new GuardianJobCapabilityRegistry(
            new MonotonicPublicJobIdAllocator(initialHighWatermark: 40),
            maximumTrackedJobs: 1);
        var canceled = Reserve(registry);

        Assert.Equal(41, canceled.PublicJobId.Value);
        Assert.True(registry.TryCancel(canceled));
        Assert.False(registry.TryCancel(canceled));
        var replacement = Reserve(registry);

        Assert.Equal(42, replacement.PublicJobId.Value);
        Assert.Equal(1, registry.TrackedCount);
    }

    [Fact]
    public void Capacity_refusal_does_not_consume_an_identifier()
    {
        var allocator = new CountingAllocator(initialHighWatermark: 80);
        using var registry = new GuardianJobCapabilityRegistry(
            allocator,
            maximumTrackedJobs: 1);
        var first = Reserve(registry);

        Assert.False(registry.TryReserve(
            HostIdentity,
            Alias,
            Transition,
            Worker,
            out var refused,
            out var failure));
        Assert.Null(refused);
        Assert.Equal("public_job_capacity", failure);
        Assert.Equal(1, allocator.AllocationCount);
        Assert.True(registry.TryCancel(first));

        var next = Reserve(registry);
        Assert.Equal(82, next.PublicJobId.Value);
        Assert.Equal(2, allocator.AllocationCount);
    }

    [Fact]
    public void Active_capability_requires_exact_removal_and_cannot_be_canceled()
    {
        using var registry = Registry();
        var registration = Reserve(registry);
        Assert.True(registry.TryActivate(
            registration,
            new InvokeBackgroundResult(registration.PublicJobId, Token(7)),
            out var capability,
            out _));

        Assert.False(registry.TryCancel(registration));
        Assert.False(registry.TryRemove(capability! with
        {
            JobCapability = Token(8),
        }));
        Assert.Equal(1, registry.ActiveCount);
        Assert.True(registry.TryRemove(capability));
        Assert.False(registry.TryGetActive(registration.PublicJobId, out _));
        Assert.Equal(0, registry.TrackedCount);

        var replacement = Reserve(registry);
        Assert.Equal(2, replacement.PublicJobId.Value);
    }

    [Fact]
    public void Lost_generation_becomes_a_session_scoped_containment_tombstone()
    {
        using var registry = Registry();
        var activeRegistration = Reserve(registry);
        Assert.True(registry.TryActivate(
            activeRegistration,
            new InvokeBackgroundResult(activeRegistration.PublicJobId, Token(11)),
            out _,
            out _));
        var pendingRegistration = Reserve(registry);

        Assert.Equal(0, registry.MarkGenerationLost(
            new GuardianHostIdentity(
                Guardian,
                Host,
                new HostGeneration(Generation.Value + 1)),
            "host_generation_lost"));
        Assert.Equal(1, registry.MarkGenerationLost(
            HostIdentity,
            "host_generation_lost"));
        Assert.Equal(0, registry.ActiveCount);
        Assert.Equal(1, registry.PendingCount);
        Assert.Equal(1, registry.TombstoneCount);
        Assert.False(registry.TryGetActive(activeRegistration.PublicJobId, out _));
        Assert.False(registry.TryGetTombstone(
            activeRegistration.PublicJobId,
            new CanonicalAlias("other"),
            out _));
        Assert.False(registry.TryGetTombstone(
            pendingRegistration.PublicJobId,
            Alias,
            out _));
        Assert.True(registry.TryGetTombstone(
            activeRegistration.PublicJobId,
            Alias,
            out var pending));
        Assert.Equal(GuardianJobContainmentStatus.Pending, pending!.ContainmentStatus);
        Assert.Equal("host_generation_lost", pending.LossReason);

        Assert.Equal(1, registry.MarkGenerationContainmentUnconfirmed(HostIdentity));
        Assert.True(registry.TryGetTombstone(
            activeRegistration.PublicJobId,
            Alias,
            out var unconfirmed));
        Assert.Equal(
            GuardianJobContainmentStatus.Unconfirmed,
            unconfirmed!.ContainmentStatus);

        Assert.Equal(1, registry.ConfirmGenerationContainment(HostIdentity));
        var confirmed = Assert.Single(registry.SnapshotTombstones(Alias));
        Assert.Equal(activeRegistration.PublicJobId, confirmed.PublicJobId);
        Assert.Equal(GuardianJobContainmentStatus.Confirmed, confirmed.ContainmentStatus);
        Assert.Equal(0, registry.MarkGenerationContainmentUnconfirmed(HostIdentity));
        Assert.Equal(
            GuardianJobContainmentStatus.Confirmed,
            Assert.Single(registry.SnapshotTombstones(Alias)).ContainmentStatus);
    }

    [Fact]
    public void Lifecycle_terminal_is_exactly_correlated_and_survives_fast_exit()
    {
        using var registry = Registry();
        Assert.True(registry.TryReserve(
            HostIdentity,
            Alias,
            Transition,
            Worker,
            OperationIdentity,
            out var registration,
            out var failure), failure);
        var forgedIdentity = new GuardianHostOperationIdentity(
            OperationIdentity.PlanId,
            new OperationId(Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff")));

        Assert.False(registry.TryObserveLifecycleEvent(
            Terminal(registration!.PublicJobId, forgedIdentity),
            out _));
        var terminal = Terminal(registration.PublicJobId, OperationIdentity);
        Assert.True(registry.TryObserveLifecycleEvent(
            terminal,
            out var terminalAvailable));
        Assert.True(terminalAvailable);
        Assert.False(registry.TryCancel(registration));
        Assert.True(registry.TryActivate(
            registration,
            new InvokeBackgroundResult(registration.PublicJobId, Token(12)),
            out _,
            out _));
        Assert.True(registry.TryTakeLifecycleTerminal(
            registration.PublicJobId,
            out var retained));
        Assert.Same(terminal, retained);
        Assert.False(registry.TryTakeLifecycleTerminal(
            registration.PublicJobId,
            out _));
        Assert.False(registry.TryObserveLifecycleEvent(terminal, out _));
        Assert.Equal(1, registry.MarkGenerationLost(
            HostIdentity,
            "host_generation_lost"));
        Assert.True(registry.TryGetTombstone(
            registration.PublicJobId,
            Alias,
            out var tombstone));
        Assert.Equal(GuardianHostJobState.Completed, tombstone!.TerminalState);
        Assert.Equal(0, tombstone.ExitCode);
    }

    [Fact]
    public void Concurrent_reservations_are_unique_and_respect_the_bound()
    {
        const int capacity = 64;
        using var registry = new GuardianJobCapabilityRegistry(
            new MonotonicPublicJobIdAllocator(),
            capacity);
        var accepted = new ConcurrentBag<GuardianJobRegistration>();
        var failures = new ConcurrentBag<string?>();

        Parallel.For(
            0,
            512,
            _ =>
            {
                if (registry.TryReserve(
                        HostIdentity,
                        Alias,
                        Transition,
                        Worker,
                        out var registration,
                        out var failure))
                {
                    accepted.Add(registration!);
                }
                else
                {
                    failures.Add(failure);
                }
            });

        Assert.Equal(capacity, accepted.Count);
        Assert.Equal(capacity, registry.TrackedCount);
        Assert.Equal(
            Enumerable.Range(1, capacity).Select(value => (long)value),
            accepted.Select(value => value.PublicJobId.Value).Order());
        Assert.All(failures, failure => Assert.Equal("public_job_capacity", failure));
    }

    [Fact]
    public void Disposal_clears_capabilities_and_rejects_new_mutation()
    {
        var registry = Registry();
        var registration = Reserve(registry);
        Assert.True(registry.TryActivate(
            registration,
            new InvokeBackgroundResult(registration.PublicJobId, Token(9)),
            out var capability,
            out _));

        registry.Dispose();
        registry.Dispose();

        Assert.Equal(0, registry.TrackedCount);
        Assert.False(registry.TryGetActive(registration.PublicJobId, out _));
        Assert.False(registry.TryCancel(registration));
        Assert.False(registry.TryRemove(capability!));
        Assert.Throws<ObjectDisposedException>(() => registry.TryReserve(
            HostIdentity,
            Alias,
            Transition,
            Worker,
            out _,
            out _));
        Assert.Throws<ObjectDisposedException>(() => registry.TryActivate(
            registration,
            new InvokeBackgroundResult(registration.PublicJobId, Token(10)),
            out _,
            out _));
    }

    [Fact]
    public void Invalid_capacity_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GuardianJobCapabilityRegistry(
                new MonotonicPublicJobIdAllocator(),
                maximumTrackedJobs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GuardianJobCapabilityRegistry(
                new MonotonicPublicJobIdAllocator(),
                GuardianJobCapabilityRegistry.DefaultMaximumTrackedJobs + 1));
    }

    [Fact]
    public void Retained_entries_never_keep_script_bearing_operations()
    {
        var entry = typeof(GuardianJobCapabilityRegistry).GetNestedType(
            "Entry",
            BindingFlags.NonPublic);

        Assert.NotNull(entry);
        Assert.DoesNotContain(
            entry!.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(OperationRequest) ||
                typeof(GuardianHostOperation).IsAssignableFrom(field.FieldType));
    }

    private static GuardianJobCapabilityRegistry Registry() => new(
        new MonotonicPublicJobIdAllocator());

    private static GuardianJobRegistration Reserve(
        GuardianJobCapabilityRegistry registry)
    {
        Assert.True(registry.TryReserve(
            HostIdentity,
            Alias,
            Transition,
            Worker,
            out var registration,
            out var failure));
        Assert.Null(failure);
        return registration!;
    }

    private static JobLifecycleEvent Terminal(
        PublicJobId publicJobId,
        GuardianHostOperationIdentity operationIdentity) => new(
        Guardian,
        Host,
        Generation,
        new HostEventSequence(1),
        requestId: null,
        Alias,
        Transition,
        Worker,
        operationIdentity,
        publicJobId,
        GuardianHostJobState.Completed,
        exitCode: 0,
        GuardianHostOutputState.Sealed,
        outputBytes: 12,
        outputDigest: null);

    private static CapabilityToken Token(byte marker)
    {
        Span<byte> bytes = stackalloc byte[ContractLimits.CapabilityTokenBytes];
        bytes.Fill(marker);
        return new CapabilityToken(Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));
    }

    private sealed class CountingAllocator(long initialHighWatermark) :
        IPublicJobIdAllocator
    {
        private readonly MonotonicPublicJobIdAllocator _inner =
            new(initialHighWatermark);
        private int _allocationCount;

        internal int AllocationCount => Volatile.Read(ref _allocationCount);

        public PublicJobId Allocate()
        {
            Interlocked.Increment(ref _allocationCount);
            return _inner.Allocate();
        }
    }
}
