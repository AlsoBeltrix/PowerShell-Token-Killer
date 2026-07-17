using System.Collections.Concurrent;
using PtkMcpGuardian.Ownership;

namespace PtkMcpGuardian.Tests;

public sealed class PublicJobIdAllocatorTests
{
    [Fact]
    public void First_identifier_is_one_and_each_allocation_advances_once()
    {
        IPublicJobIdAllocator allocator = new MonotonicPublicJobIdAllocator();

        Assert.Equal(1, allocator.Allocate().Value);
        Assert.Equal(2, allocator.Allocate().Value);
        Assert.Equal(3, allocator.Allocate().Value);
    }

    [Fact]
    public void Negative_initial_high_watermark_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MonotonicPublicJobIdAllocator(-1));
    }

    [Fact]
    public void Exhaustion_never_wraps_or_recovers()
    {
        IPublicJobIdAllocator allocator = new MonotonicPublicJobIdAllocator(long.MaxValue - 1);

        Assert.Equal(long.MaxValue, allocator.Allocate().Value);
        var first = Assert.Throws<PublicJobIdExhaustedException>(() => allocator.Allocate());
        var second = Assert.Throws<PublicJobIdExhaustedException>(() => allocator.Allocate());

        Assert.Equal(PublicJobIdExhaustedException.StableDetailCode, first.DetailCode);
        Assert.Equal(first.DetailCode, second.DetailCode);
    }

    [Fact]
    public void Concurrent_allocations_are_unique_and_gap_free()
    {
        const int allocationCount = 8_192;
        IPublicJobIdAllocator allocator = new MonotonicPublicJobIdAllocator();
        var allocated = new ConcurrentBag<long>();

        Parallel.For(
            0,
            allocationCount,
            _ => allocated.Add(allocator.Allocate().Value));

        var ordered = allocated.Order().ToArray();
        Assert.Equal(allocationCount, ordered.Length);
        Assert.Equal(
            Enumerable.Range(1, allocationCount).Select(value => (long)value),
            ordered);
    }

    [Fact]
    public void Concurrent_exhaustion_publishes_maximum_once_then_stays_terminal()
    {
        const int contenderCount = 256;
        IPublicJobIdAllocator allocator = new MonotonicPublicJobIdAllocator(long.MaxValue - 1);
        var allocated = new ConcurrentBag<long>();
        var failures = new ConcurrentBag<string>();

        Parallel.For(
            0,
            contenderCount,
            _ =>
            {
                try
                {
                    allocated.Add(allocator.Allocate().Value);
                }
                catch (PublicJobIdExhaustedException exception)
                {
                    failures.Add(exception.DetailCode);
                }
            });

        Assert.Equal([long.MaxValue], allocated.Order());
        Assert.Equal(contenderCount - 1, failures.Count);
        Assert.All(failures, detailCode =>
            Assert.Equal(PublicJobIdExhaustedException.StableDetailCode, detailCode));
        var terminal = Assert.Throws<PublicJobIdExhaustedException>(() => allocator.Allocate());
        Assert.Equal(PublicJobIdExhaustedException.StableDetailCode, terminal.DetailCode);
    }
}
