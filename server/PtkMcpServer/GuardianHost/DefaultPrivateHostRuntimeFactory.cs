using PtkMcpServer.Sessions;

namespace PtkMcpServer.GuardianHost;

/// <summary>
/// Production composition for one private default-session host. The protocol
/// server validates bootstrap and initialize pins; this composition deliberately
/// retains none of them and shares one serialized outbound authority for runtime
/// delivery and output events.
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

        var sessionFactory = new DefaultPrivateHostSessionFactory();
        var outputTransfer = new EventPrivateHostOutputTransfer(identity, eventSink);
        return new DefaultPrivateHostRuntime(
            identity,
            eventSink,
            sessionFactory,
            outputTransfer);
    }
}
