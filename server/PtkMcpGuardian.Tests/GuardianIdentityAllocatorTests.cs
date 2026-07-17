using System.Collections.Concurrent;
using System.Reflection;
using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianIdentityAllocatorTests
{
    [Fact]
    public void Host_and_private_request_sequences_start_at_one_and_resume_after_seed()
    {
        IHostGenerationAllocator hosts = new MonotonicHostGenerationAllocator();
        IPrivateRequestIdAllocator requests = new MonotonicPrivateRequestIdAllocator(40);

        Assert.Equal(1, hosts.Allocate().Value);
        Assert.Equal(2, hosts.Allocate().Value);
        Assert.Equal(41, requests.Allocate().Value);
        Assert.Equal(42, requests.Allocate().Value);
    }

    [Fact]
    public void Negative_scalar_high_watermarks_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MonotonicHostGenerationAllocator(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MonotonicPrivateRequestIdAllocator(-1));
    }

    [Fact]
    public void Host_and_private_request_allocations_are_concurrently_unique_and_monotonic()
    {
        const int allocationCount = 8_192;
        IHostGenerationAllocator hosts = new MonotonicHostGenerationAllocator();
        IPrivateRequestIdAllocator requests = new MonotonicPrivateRequestIdAllocator();
        var hostValues = new ConcurrentBag<long>();
        var requestValues = new ConcurrentBag<long>();

        Parallel.For(0, allocationCount, _ =>
        {
            hostValues.Add(hosts.Allocate().Value);
            requestValues.Add(requests.Allocate().Value);
        });

        AssertGapFree(hostValues, allocationCount);
        AssertGapFree(requestValues, allocationCount);
    }

    [Theory]
    [InlineData(true, "HostGeneration",
        "host_generation_exhausted")]
    [InlineData(false, "PrivateRequestId",
        "private_request_id_exhausted")]
    public void Scalar_exhaustion_publishes_maximum_once_and_is_permanent(
        bool hostSequence,
        string expectedKindName,
        string expectedDetailCode)
    {
        var sequence = hostSequence
            ? (IScalarSequence)new HostSequence(long.MaxValue - 1)
            : new RequestSequence(long.MaxValue - 1);
        var expectedKind = Enum.Parse<GuardianIdentityExhaustionKind>(expectedKindName);

        Assert.Equal(long.MaxValue, sequence.Allocate());
        var first = Assert.Throws<GuardianIdentityExhaustedException>(() => sequence.Allocate());
        var second = Assert.Throws<GuardianIdentityExhaustedException>(() => sequence.Allocate());

        Assert.Equal(expectedKind, first.DetailKind);
        Assert.Equal(expectedDetailCode, first.DetailCode);
        Assert.Equal(first.DetailKind, second.DetailKind);
        Assert.Equal(first.DetailCode, second.DetailCode);
    }

    [Theory]
    [InlineData(true, "HostGeneration", "host_generation_exhausted")]
    [InlineData(false, "PrivateRequestId", "private_request_id_exhausted")]
    public void Scalar_maximum_is_published_once_under_contention_then_exhaustion_is_permanent(
        bool hostSequence,
        string expectedKindName,
        string expectedDetailCode)
    {
        const int contenderCount = 256;
        var sequence = hostSequence
            ? (IScalarSequence)new HostSequence(long.MaxValue - 1)
            : new RequestSequence(long.MaxValue - 1);
        var expectedKind = Enum.Parse<GuardianIdentityExhaustionKind>(expectedKindName);
        var allocated = new ConcurrentBag<long>();
        var failures = new ConcurrentBag<(GuardianIdentityExhaustionKind Kind, string Code)>();

        Parallel.For(0, contenderCount, _ =>
        {
            try
            {
                allocated.Add(sequence.Allocate());
            }
            catch (GuardianIdentityExhaustedException exception)
            {
                failures.Add((exception.DetailKind, exception.DetailCode));
            }
        });

        Assert.Equal([long.MaxValue], allocated.Order());
        Assert.Equal(contenderCount - 1, failures.Count);
        Assert.All(failures, failure =>
        {
            Assert.Equal(expectedKind, failure.Kind);
            Assert.Equal(expectedDetailCode, failure.Code);
        });
        for (var retry = 0; retry < 2; retry++)
        {
            var terminal = Assert.Throws<GuardianIdentityExhaustedException>(() =>
                sequence.Allocate());
            Assert.Equal(expectedKind, terminal.DetailKind);
            Assert.Equal(expectedDetailCode, terminal.DetailCode);
        }
    }

    [Fact]
    public void Worker_sequences_are_alias_local_resume_after_seed_and_preserve_gaps()
    {
        var alpha = new CanonicalAlias("alpha");
        var beta = new CanonicalAlias("beta");
        IWorkerGenerationAllocator allocator = new PerAliasWorkerGenerationAllocator(
        [
            new WorkerGenerationHighWatermarkEntry(
                alpha,
                new WorkerGenerationHighWatermark(7)),
        ]);

        Assert.Equal(8, allocator.Allocate(alpha).Value);
        Assert.Equal(1, allocator.Allocate(beta).Value);

        var abandoned = allocator.Allocate(alpha);
        Assert.Equal(9, abandoned.Value);
        Assert.Equal(10, allocator.Allocate(alpha).Value);
        Assert.Equal(2, allocator.Allocate(beta).Value);
    }

    [Fact]
    public void Worker_allocations_are_concurrently_unique_per_alias()
    {
        const int allocationCount = 4_096;
        var alpha = new CanonicalAlias("alpha");
        var beta = new CanonicalAlias("beta");
        IWorkerGenerationAllocator allocator = new PerAliasWorkerGenerationAllocator();
        var alphaValues = new ConcurrentBag<long>();
        var betaValues = new ConcurrentBag<long>();

        Parallel.Invoke(
            () => Parallel.For(0, allocationCount, _ =>
                alphaValues.Add(allocator.Allocate(alpha).Value)),
            () => Parallel.For(0, allocationCount, _ =>
                betaValues.Add(allocator.Allocate(beta).Value)));

        AssertGapFree(alphaValues, allocationCount);
        AssertGapFree(betaValues, allocationCount);
    }

    [Fact]
    public void Exhausted_worker_alias_does_not_poison_another_alias()
    {
        var exhausted = new CanonicalAlias("secret");
        var healthy = new CanonicalAlias("healthy");
        IWorkerGenerationAllocator allocator = new PerAliasWorkerGenerationAllocator(
        [
            new WorkerGenerationHighWatermarkEntry(
                exhausted,
                new WorkerGenerationHighWatermark(long.MaxValue)),
            new WorkerGenerationHighWatermarkEntry(
                healthy,
                new WorkerGenerationHighWatermark(12)),
        ]);

        var first = Assert.Throws<GuardianIdentityExhaustedException>(() =>
            allocator.Allocate(exhausted));
        Assert.Equal(GuardianIdentityExhaustionKind.WorkerGeneration, first.DetailKind);
        Assert.Equal("worker_generation_exhausted", first.DetailCode);
        Assert.DoesNotContain(exhausted.Value, first.Message, StringComparison.Ordinal);
        Assert.Equal(13, allocator.Allocate(healthy).Value);
        var second = Assert.Throws<GuardianIdentityExhaustedException>(() =>
            allocator.Allocate(exhausted));
        Assert.Equal(first.DetailCode, second.DetailCode);
        Assert.Equal(14, allocator.Allocate(healthy).Value);
    }

    [Fact]
    public void Worker_maximum_is_published_once_under_same_alias_contention_without_poisoning_another_alias()
    {
        const int contenderCount = 256;
        var contested = new CanonicalAlias("contested");
        var healthy = new CanonicalAlias("healthy");
        IWorkerGenerationAllocator allocator = new PerAliasWorkerGenerationAllocator(
        [
            new WorkerGenerationHighWatermarkEntry(
                contested,
                new WorkerGenerationHighWatermark(long.MaxValue - 1)),
            new WorkerGenerationHighWatermarkEntry(
                healthy,
                new WorkerGenerationHighWatermark(20)),
        ]);
        var allocated = new ConcurrentBag<long>();
        var failures = new ConcurrentBag<(GuardianIdentityExhaustionKind Kind, string Code)>();

        Parallel.For(0, contenderCount, _ =>
        {
            try
            {
                allocated.Add(allocator.Allocate(contested).Value);
            }
            catch (GuardianIdentityExhaustedException exception)
            {
                failures.Add((exception.DetailKind, exception.DetailCode));
            }
        });

        Assert.Equal([long.MaxValue], allocated.Order());
        Assert.Equal(contenderCount - 1, failures.Count);
        Assert.All(failures, failure =>
        {
            Assert.Equal(GuardianIdentityExhaustionKind.WorkerGeneration, failure.Kind);
            Assert.Equal("worker_generation_exhausted", failure.Code);
        });
        Assert.Equal(21, allocator.Allocate(healthy).Value);
        var terminal = Assert.Throws<GuardianIdentityExhaustedException>(() =>
            allocator.Allocate(contested));
        Assert.Equal(GuardianIdentityExhaustionKind.WorkerGeneration, terminal.DetailKind);
        Assert.Equal("worker_generation_exhausted", terminal.DetailCode);
        Assert.Equal(22, allocator.Allocate(healthy).Value);
    }

    [Fact]
    public void Duplicate_null_and_overbound_worker_seeds_are_rejected()
    {
        var duplicate = new WorkerGenerationHighWatermarkEntry(
            new CanonicalAlias("same"),
            new WorkerGenerationHighWatermark(0));
        Assert.Throws<ArgumentException>(() =>
            new PerAliasWorkerGenerationAllocator([duplicate, duplicate]));
        Assert.Throws<ArgumentNullException>(() =>
            new PerAliasWorkerGenerationAllocator(null!));
        Assert.Throws<ArgumentException>(() =>
            new PerAliasWorkerGenerationAllocator(
                Enumerable.Range(0, ContractLimits.MaximumAliases + 1)
                    .Select(Seed)));
    }

    [Fact]
    public void Alias_capacity_failure_is_bounded_and_existing_aliases_keep_advancing()
    {
        var seeds = Enumerable.Range(0, ContractLimits.MaximumAliases)
            .Select(Seed)
            .ToArray();
        IWorkerGenerationAllocator allocator = new PerAliasWorkerGenerationAllocator(seeds);

        var failure = Assert.Throws<GuardianIdentityExhaustedException>(() =>
            allocator.Allocate(new CanonicalAlias("overflow")));

        Assert.Equal(GuardianIdentityExhaustionKind.WorkerAliasCapacity, failure.DetailKind);
        Assert.Equal("worker_alias_capacity_exhausted", failure.DetailCode);
        Assert.DoesNotContain("overflow", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, allocator.Allocate(seeds[0].Alias).Value);
    }

    [Fact]
    public void Last_alias_slot_is_admitted_once_under_distinct_alias_contention()
    {
        const int contenderCount = 64;
        var seeds = Enumerable.Range(0, ContractLimits.MaximumAliases - 1)
            .Select(Seed)
            .ToArray();
        IWorkerGenerationAllocator allocator = new PerAliasWorkerGenerationAllocator(seeds);
        var candidates = Enumerable.Range(0, contenderCount)
            .Select(index => new CanonicalAlias($"candidate-{index:D3}"))
            .ToArray();
        var admitted = new ConcurrentBag<(CanonicalAlias Alias, long Generation)>();
        var rejected = new ConcurrentBag<
            (CanonicalAlias Alias, GuardianIdentityExhaustionKind Kind, string Code)>();

        Parallel.For(0, candidates.Length, index =>
        {
            var alias = candidates[index];
            try
            {
                admitted.Add((alias, allocator.Allocate(alias).Value));
            }
            catch (GuardianIdentityExhaustedException exception)
            {
                rejected.Add((alias, exception.DetailKind, exception.DetailCode));
            }
        });

        var winner = Assert.Single(admitted);
        Assert.Equal(1, winner.Generation);
        Assert.Equal(contenderCount - 1, rejected.Count);
        Assert.All(rejected, failure =>
        {
            Assert.Equal(GuardianIdentityExhaustionKind.WorkerAliasCapacity, failure.Kind);
            Assert.Equal("worker_alias_capacity_exhausted", failure.Code);
        });
        Assert.Equal(2, allocator.Allocate(winner.Alias).Value);
        Assert.Equal(1, allocator.Allocate(seeds[0].Alias).Value);

        var rejectedAlias = rejected.First().Alias;
        var terminal = Assert.Throws<GuardianIdentityExhaustedException>(() =>
            allocator.Allocate(rejectedAlias));
        Assert.Equal(GuardianIdentityExhaustionKind.WorkerAliasCapacity, terminal.DetailKind);
        Assert.Equal("worker_alias_capacity_exhausted", terminal.DetailCode);
    }

    [Fact]
    public void Allocator_shape_exposes_only_irreversible_typed_allocation()
    {
        var contracts = new Dictionary<Type, (Type ReturnType, Type[] Parameters)>
        {
            [typeof(MonotonicHostGenerationAllocator)] =
                (typeof(HostGeneration), Type.EmptyTypes),
            [typeof(MonotonicPrivateRequestIdAllocator)] =
                (typeof(PrivateRequestId), Type.EmptyTypes),
            [typeof(PerAliasWorkerGenerationAllocator)] =
                (typeof(WorkerGeneration), [typeof(CanonicalAlias)]),
        };

        foreach (var (type, contract) in contracts)
        {
            Assert.False(type.IsPublic);
            var methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .ToArray();
            var allocate = Assert.Single(methods);
            Assert.Equal("Allocate", allocate.Name);
            Assert.Equal(contract.ReturnType, allocate.ReturnType);
            Assert.Equal(
                contract.Parameters,
                allocate.GetParameters().Select(parameter => parameter.ParameterType));
        }

        AssertInterfaceShape<IHostGenerationAllocator>(typeof(HostGeneration), Type.EmptyTypes);
        AssertInterfaceShape<IPrivateRequestIdAllocator>(typeof(PrivateRequestId), Type.EmptyTypes);
        AssertInterfaceShape<IWorkerGenerationAllocator>(
            typeof(WorkerGeneration),
            [typeof(CanonicalAlias)]);
    }

    private static WorkerGenerationHighWatermarkEntry Seed(int index) => new(
        new CanonicalAlias($"alias-{index:D3}"),
        new WorkerGenerationHighWatermark(0));

    private static void AssertGapFree(IEnumerable<long> values, int count)
    {
        var ordered = values.Order().ToArray();
        Assert.Equal(count, ordered.Length);
        Assert.Equal(
            Enumerable.Range(1, count).Select(value => (long)value),
            ordered);
    }

    private static void AssertInterfaceShape<T>(Type returnType, Type[] parameters)
    {
        var method = Assert.Single(typeof(T).GetMethods());
        Assert.Equal("Allocate", method.Name);
        Assert.Equal(returnType, method.ReturnType);
        Assert.Equal(
            parameters,
            method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private interface IScalarSequence
    {
        long Allocate();
    }

    private sealed class HostSequence(long seed) : IScalarSequence
    {
        private readonly IHostGenerationAllocator _allocator =
            new MonotonicHostGenerationAllocator(seed);

        public long Allocate() => _allocator.Allocate().Value;
    }

    private sealed class RequestSequence(long seed) : IScalarSequence
    {
        private readonly IPrivateRequestIdAllocator _allocator =
            new MonotonicPrivateRequestIdAllocator(seed);

        public long Allocate() => _allocator.Allocate().Value;
    }
}
