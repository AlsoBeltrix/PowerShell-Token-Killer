using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

/// <summary>
/// Binds one logical worker generation to the host/guardian containment
/// exchange. The Unix launcher owns broker ordering; this adapter only
/// translates its exact process identities and awaits the matched wire
/// acknowledgement before returning.
/// </summary>
internal sealed class PrivateHostUnixWorkerContainmentRegistry :
    IUnixWorkerContainmentRegistry
{
    private readonly PrivateHostServerIdentity _host;
    private readonly CanonicalAlias _alias;
    private readonly SessionTransitionVersion _transitionVersion;
    private readonly GuardianHostWorkerIdentity _worker;
    private readonly IPrivateHostControlEventSink _control;

    internal PrivateHostUnixWorkerContainmentRegistry(
        PrivateHostServerIdentity host,
        CanonicalAlias alias,
        SessionTransitionVersion transitionVersion,
        GuardianHostWorkerIdentity worker,
        IPrivateHostControlEventSink control)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _alias = alias ?? throw new ArgumentNullException(nameof(alias));
        _transitionVersion = transitionVersion ??
            throw new ArgumentNullException(nameof(transitionVersion));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _control = control ?? throw new ArgumentNullException(nameof(control));
    }

    public ValueTask RegisterPendingAsync(
        UnixWorkerContainmentIdentity identity,
        CancellationToken cancellationToken) =>
        ExchangeAsync(
            identity,
            static (owner, sequence, containment) =>
                new WorkerContainmentPendingEvent(
                    owner._host.GuardianBootId,
                    owner._host.HostBootId,
                    owner._host.HostGeneration,
                    sequence,
                    owner._alias,
                    owner._transitionVersion,
                    owner._worker,
                    containment),
            typeof(WorkerContainmentPendingAckRequest),
            cancellationToken);

    public ValueTask RegisterArmedAsync(
        UnixWorkerContainmentIdentity identity,
        CancellationToken cancellationToken) =>
        ExchangeAsync(
            identity,
            static (owner, sequence, containment) =>
                new WorkerContainmentArmedEvent(
                    owner._host.GuardianBootId,
                    owner._host.HostBootId,
                    owner._host.HostGeneration,
                    sequence,
                    owner._alias,
                    owner._transitionVersion,
                    owner._worker,
                    containment),
            typeof(WorkerContainmentArmedAckRequest),
            cancellationToken);

    public ValueTask RemoveAsync(
        UnixWorkerContainmentIdentity identity,
        CancellationToken cancellationToken) =>
        ExchangeAsync(
            identity,
            static (owner, sequence, containment) =>
                new WorkerContainmentRemoveRequestedEvent(
                    owner._host.GuardianBootId,
                    owner._host.HostBootId,
                    owner._host.HostGeneration,
                    sequence,
                    owner._alias,
                    owner._transitionVersion,
                    owner._worker,
                    containment),
            typeof(WorkerContainmentRemoveAckRequest),
            cancellationToken);

    private async ValueTask ExchangeAsync(
        UnixWorkerContainmentIdentity identity,
        Func<
            PrivateHostUnixWorkerContainmentRegistry,
            HostEventSequence,
            GuardianHostContainmentIdentity,
            GuardianHostContainmentEvent> createEvent,
        Type expectedRequestType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var containment = Convert(identity);
        var request = await _control.ExchangeControlAsync(
                sequence => createEvent(this, sequence, containment),
                cancellationToken)
            .ConfigureAwait(false);
        if (request.GetType() != expectedRequestType)
        {
            throw new InvalidDataException(
                "Guardian returned the wrong Unix containment acknowledgement.");
        }
    }

    private static GuardianHostContainmentIdentity Convert(
        UnixWorkerContainmentIdentity identity)
    {
        if (identity.BrokerProcessId <= 0 ||
            identity.WorkerProcessId <= 0 ||
            identity.BrokerProcessId == identity.WorkerProcessId ||
            identity.WorkerProcessGroup != identity.WorkerProcessId ||
            !identity.BrokerIdentity.IsValid ||
            !identity.WorkerIdentity.IsValid)
        {
            throw new ArgumentException(
                "Unix worker containment identity is invalid.",
                nameof(identity));
        }

        return new GuardianHostContainmentIdentity(
            checked((uint)identity.BrokerProcessId),
            identity.BrokerIdentity.High,
            identity.BrokerIdentity.Low,
            checked((uint)identity.WorkerProcessId),
            identity.WorkerIdentity.High,
            identity.WorkerIdentity.Low);
    }
}
