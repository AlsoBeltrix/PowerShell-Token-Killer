using PtkSharedContracts;

namespace PtkMcpGuardian.Lifecycle;

internal interface IHostGenerationAllocator
{
    HostGeneration Allocate();
}

internal interface IPrivateRequestIdAllocator
{
    PrivateRequestId Allocate();
}

internal interface IWorkerGenerationAllocator
{
    WorkerGeneration Allocate(CanonicalAlias alias);
}

/// <summary>
/// Guardian-lifetime host-generation high-water mark. A value is consumed
/// immediately before its process-creation attempt and can never be released.
/// </summary>
internal sealed class MonotonicHostGenerationAllocator : IHostGenerationAllocator
{
    private long _highWatermark;

    internal MonotonicHostGenerationAllocator(long initialHighWatermark = 0)
    {
        if (initialHighWatermark < 0)
            throw new ArgumentOutOfRangeException(nameof(initialHighWatermark));

        _highWatermark = initialHighWatermark;
    }

    public HostGeneration Allocate()
    {
        while (true)
        {
            var current = Volatile.Read(ref _highWatermark);
            if (current == long.MaxValue)
            {
                throw new GuardianIdentityExhaustedException(
                    GuardianIdentityExhaustionKind.HostGeneration);
            }

            var next = current + 1;
            if (Interlocked.CompareExchange(ref _highWatermark, next, current) == current)
                return new HostGeneration(next);
        }
    }
}

/// <summary>
/// Guardian-boot request-ID high-water mark shared by every host generation.
/// It is deliberately independent from any private connection instance.
/// </summary>
internal sealed class MonotonicPrivateRequestIdAllocator : IPrivateRequestIdAllocator
{
    private long _highWatermark;

    internal MonotonicPrivateRequestIdAllocator(long initialHighWatermark = 0)
    {
        if (initialHighWatermark < 0)
            throw new ArgumentOutOfRangeException(nameof(initialHighWatermark));

        _highWatermark = initialHighWatermark;
    }

    public PrivateRequestId Allocate()
    {
        while (true)
        {
            var current = Volatile.Read(ref _highWatermark);
            if (current == long.MaxValue)
            {
                throw new GuardianIdentityExhaustedException(
                    GuardianIdentityExhaustionKind.PrivateRequestId);
            }

            var next = current + 1;
            if (Interlocked.CompareExchange(ref _highWatermark, next, current) == current)
                return new PrivateRequestId(next);
        }
    }
}

/// <summary>
/// Per-alias worker-generation high-water marks. Aliases advance independently,
/// and allocation is the only operation that consumes a generation.
/// </summary>
internal sealed class PerAliasWorkerGenerationAllocator : IWorkerGenerationAllocator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _highWatermarks = new(StringComparer.Ordinal);

    internal PerAliasWorkerGenerationAllocator()
        : this(Array.Empty<WorkerGenerationHighWatermarkEntry>())
    {
    }

    internal PerAliasWorkerGenerationAllocator(
        IEnumerable<WorkerGenerationHighWatermarkEntry> initialHighWatermarks)
    {
        ArgumentNullException.ThrowIfNull(initialHighWatermarks);

        foreach (var entry in initialHighWatermarks)
        {
            if (entry is null || entry.Alias is null || entry.Generation is null)
            {
                throw new ArgumentException(
                    "Worker-generation high-water marks cannot contain null values.",
                    nameof(initialHighWatermarks));
            }
            if (_highWatermarks.Count == ContractLimits.MaximumAliases)
            {
                throw new ArgumentException(
                    "Worker-generation high-water marks exceed the alias bound.",
                    nameof(initialHighWatermarks));
            }
            if (!_highWatermarks.TryAdd(entry.Alias.Value, entry.Generation.Value))
            {
                throw new ArgumentException(
                    "Worker-generation high-water marks must contain unique aliases.",
                    nameof(initialHighWatermarks));
            }
        }
    }

    public WorkerGeneration Allocate(CanonicalAlias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);

        lock (_gate)
        {
            if (!_highWatermarks.TryGetValue(alias.Value, out var current))
            {
                if (_highWatermarks.Count == ContractLimits.MaximumAliases)
                {
                    throw new GuardianIdentityExhaustedException(
                        GuardianIdentityExhaustionKind.WorkerAliasCapacity);
                }

                current = 0;
                _highWatermarks.Add(alias.Value, current);
            }

            if (current == long.MaxValue)
            {
                throw new GuardianIdentityExhaustedException(
                    GuardianIdentityExhaustionKind.WorkerGeneration);
            }

            var next = current + 1;
            _highWatermarks[alias.Value] = next;
            return new WorkerGeneration(next);
        }
    }
}

internal enum GuardianIdentityExhaustionKind
{
    HostGeneration,
    PrivateRequestId,
    WorkerGeneration,
    WorkerAliasCapacity,
}

/// <summary>A bounded fail-closed terminal with no identity or alias data.</summary>
internal sealed class GuardianIdentityExhaustedException : InvalidOperationException
{
    internal GuardianIdentityExhaustedException(GuardianIdentityExhaustionKind detailKind)
        : base(MessageFor(detailKind))
    {
        if (!Enum.IsDefined(detailKind))
            throw new ArgumentOutOfRangeException(nameof(detailKind));

        DetailKind = detailKind;
    }

    internal GuardianIdentityExhaustionKind DetailKind { get; }

    internal string DetailCode => DetailKind switch
    {
        GuardianIdentityExhaustionKind.HostGeneration => "host_generation_exhausted",
        GuardianIdentityExhaustionKind.PrivateRequestId => "private_request_id_exhausted",
        GuardianIdentityExhaustionKind.WorkerGeneration => "worker_generation_exhausted",
        GuardianIdentityExhaustionKind.WorkerAliasCapacity => "worker_alias_capacity_exhausted",
        _ => throw new InvalidOperationException("Unknown guardian identity exhaustion kind."),
    };

    private static string MessageFor(GuardianIdentityExhaustionKind detailKind) => detailKind switch
    {
        GuardianIdentityExhaustionKind.HostGeneration =>
            "The guardian host-generation sequence is exhausted.",
        GuardianIdentityExhaustionKind.PrivateRequestId =>
            "The guardian private request-ID sequence is exhausted.",
        GuardianIdentityExhaustionKind.WorkerGeneration =>
            "A guardian worker-generation sequence is exhausted.",
        GuardianIdentityExhaustionKind.WorkerAliasCapacity =>
            "The guardian worker-alias capacity is exhausted.",
        _ => throw new ArgumentOutOfRangeException(nameof(detailKind)),
    };
}
