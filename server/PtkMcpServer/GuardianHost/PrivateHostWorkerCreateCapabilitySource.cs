using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

internal readonly record struct ConsumedWorkerCreateCapability(
    WorkerGeneration WorkerGeneration,
    CapabilityToken Token);

/// <summary>
/// A guardian-granted, host-generation-bound permission to perform exactly one
/// worker creation attempt. Consumption is local and one-shot; the guardian's
/// generation high-water mark is already advanced when the grant is issued.
/// </summary>
internal sealed class PrivateHostWorkerCreateCapability
{
    private readonly Func<long> _unixTimeMilliseconds;
    private int _consumed;

    internal PrivateHostWorkerCreateCapability(
        WorkerGeneration workerGeneration,
        CapabilityToken token,
        long deadlineUnixTimeMilliseconds,
        Func<long> unixTimeMilliseconds)
    {
        WorkerGeneration = workerGeneration ??
            throw new ArgumentNullException(nameof(workerGeneration));
        Token = token ?? throw new ArgumentNullException(nameof(token));
        if (deadlineUnixTimeMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(deadlineUnixTimeMilliseconds));
        _unixTimeMilliseconds = unixTimeMilliseconds ??
            throw new ArgumentNullException(nameof(unixTimeMilliseconds));
        DeadlineUnixTimeMilliseconds = deadlineUnixTimeMilliseconds;
    }

    internal WorkerGeneration WorkerGeneration { get; }
    internal CapabilityToken Token { get; }
    internal long DeadlineUnixTimeMilliseconds { get; }

    internal ConsumedWorkerCreateCapability Consume()
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new InvalidOperationException(
                "Worker create capability has already been consumed.");
        if (DeadlineUnixTimeMilliseconds <= _unixTimeMilliseconds())
            throw new TimeoutException("Worker create capability has expired.");
        return new ConsumedWorkerCreateCapability(WorkerGeneration, Token);
    }
}

/// <summary>
/// Requests the next nonreusing worker generation from the guardian and
/// validates the exact retained control response before exposing a one-shot
/// launch capability to host runtime composition.
/// </summary>
internal sealed class PrivateHostWorkerCreateCapabilitySource
{
    private readonly PrivateHostServerIdentity _host;
    private readonly IPrivateHostControlEventSink _control;
    private readonly Func<long> _unixTimeMilliseconds;

    internal PrivateHostWorkerCreateCapabilitySource(
        PrivateHostServerIdentity host,
        IPrivateHostControlEventSink control,
        Func<long>? unixTimeMilliseconds = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _unixTimeMilliseconds = unixTimeMilliseconds ??
            (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal async ValueTask<PrivateHostWorkerCreateCapability> RequestAsync(
        RecoveryBinding binding,
        WorkerGenerationHighWatermark generationHighWatermark,
        long startupDeadlineUnixTimeMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(generationHighWatermark);
        cancellationToken.ThrowIfCancellationRequested();
        if (startupDeadlineUnixTimeMilliseconds <= _unixTimeMilliseconds())
            throw new TimeoutException("Worker startup deadline has expired.");

        var request = await _control.ExchangeControlAsync(
                sequence => new WorkerCreateCapabilityRequestedEvent(
                    _host.GuardianBootId,
                    _host.HostBootId,
                    _host.HostGeneration,
                    sequence,
                    binding.Alias,
                    binding.TransitionVersion,
                    binding.BindingDigest,
                    startupDeadlineUnixTimeMilliseconds),
                cancellationToken)
            .ConfigureAwait(false);

        if (request is not WorkerCreateCapabilityGrantRequest grant ||
            grant.SessionAlias != binding.Alias ||
            grant.SessionTransitionVersion != binding.TransitionVersion ||
            grant.DeadlineUnixTimeMilliseconds != startupDeadlineUnixTimeMilliseconds ||
            grant.WorkerGeneration.Value <= generationHighWatermark.Value)
        {
            throw new InvalidDataException(
                "Guardian returned an invalid worker create capability.");
        }
        if (startupDeadlineUnixTimeMilliseconds <= _unixTimeMilliseconds())
            throw new TimeoutException("Worker create capability arrived after its deadline.");

        return new PrivateHostWorkerCreateCapability(
            grant.WorkerGeneration,
            grant.Token,
            startupDeadlineUnixTimeMilliseconds,
            _unixTimeMilliseconds);
    }
}
