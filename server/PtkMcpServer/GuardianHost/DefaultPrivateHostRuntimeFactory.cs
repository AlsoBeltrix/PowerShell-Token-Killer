namespace PtkMcpServer.GuardianHost;

/// <summary>
/// Production composition for one private default-session host. The protocol
/// server validates bootstrap and initialize pins; this composition retains no
/// pin authority and routes the declared session only through a contained
/// worker slot using the shared event/control channel.
/// </summary>
internal static class DefaultPrivateHostRuntimeFactory
{
    internal static IPrivateHostRuntime Create(
        PrivateHostServerIdentity identity,
        PrivateHostServerPins pins,
        IPrivateHostEventSink eventSink)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(eventSink);

        var control = eventSink as IPrivateHostControlEventSink ??
            throw new ArgumentException(
                "Production private-host composition requires one shared event/control channel.",
                nameof(eventSink));
        var workerEvents = new PrivateHostWorkerEventBridge(identity, eventSink);
        var capabilitySource = new PrivateHostWorkerCreateCapabilitySource(
            identity,
            control);
        var launch = new ProductionPrivateHostWorkerLaunchAuthority(
            identity,
            control);
        var slots = new PrivateHostWorkerSlotFactory(
            capabilitySource,
            launch);
        var authorizer = new PrivateHostPreparedDispatchAuthorizer(
            identity,
            control);
        var prepared = new PrivateHostPreparedInvokeDispatcher(
            identity,
            eventSink,
            authorizer,
            workerEvents);
        var outputTransfer = new EventPrivateHostOutputTransfer(identity, eventSink);
        return new WorkerPrivateHostRuntime(
            identity,
            eventSink,
            slots,
            prepared,
            workerEvents,
            outputTransfer);
    }
}
