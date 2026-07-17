using PtkSharedContracts;

namespace PtkMcpGuardian.Ownership;

/// <summary>
/// Guardian-owned source of positive public job identifiers. Allocation is
/// irreversible: a caller may abandon an allocated identifier, but no later
/// caller can receive it again during this guardian lifetime.
/// </summary>
internal interface IPublicJobIdAllocator
{
    PublicJobId Allocate();
}

/// <summary>
/// Lock-free signed-64-bit high-water mark. Exhaustion is permanent and fails
/// closed before arithmetic can wrap into zero or the negative range.
/// </summary>
internal sealed class MonotonicPublicJobIdAllocator : IPublicJobIdAllocator
{
    private long _highWatermark;

    internal MonotonicPublicJobIdAllocator(long initialHighWatermark = 0)
    {
        if (initialHighWatermark < 0)
            throw new ArgumentOutOfRangeException(nameof(initialHighWatermark));

        _highWatermark = initialHighWatermark;
    }

    public PublicJobId Allocate()
    {
        while (true)
        {
            var current = Volatile.Read(ref _highWatermark);
            if (current == long.MaxValue)
                throw new PublicJobIdExhaustedException();

            var next = current + 1;
            if (Interlocked.CompareExchange(ref _highWatermark, next, current) == current)
                return new PublicJobId(next);
        }
    }
}

/// <summary>A stable internal terminal for this guardian boot.</summary>
internal sealed class PublicJobIdExhaustedException : InvalidOperationException
{
    internal const string StableDetailCode = "public_job_id_exhausted";

    internal PublicJobIdExhaustedException()
        : base("The guardian public job identifier sequence is exhausted.")
    {
    }

    internal string DetailCode => StableDetailCode;
}
