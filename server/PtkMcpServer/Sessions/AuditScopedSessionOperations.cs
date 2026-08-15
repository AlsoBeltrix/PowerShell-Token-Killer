using PtkMcpServer.Audit;

namespace PtkMcpServer.Sessions;

/// <summary>
/// Request-scoped tool facade that carries the admitted audit capability to
/// the connection-owned worker supervisor without exposing it as MCP input.
/// </summary>
internal sealed class AuditScopedSessionOperations(
    WorkerSupervisor supervisor,
    AuditCallContextAccessor auditContext) : ISessionOperations
{
    public Task<ToolOutcome> InvokeAsync(
        string script,
        CancellationToken cancellationToken,
        bool raw,
        string route,
        int timeoutSeconds,
        string session,
        OutputStore? outputStore) =>
        supervisor.InvokeAsync(
            script,
            cancellationToken,
            raw,
            route,
            timeoutSeconds,
            session,
            outputStore,
            auditContext);

    public Task<ToolOutcome> StateAsync(
        bool listAvailable,
        string session,
        CancellationToken cancellationToken) =>
        ((ISessionOperations)supervisor).StateAsync(
            listAvailable,
            session,
            cancellationToken);

    public Task<ToolOutcome> ResetAsync(
        string session,
        CancellationToken cancellationToken) =>
        ((ISessionOperations)supervisor).ResetAsync(
            session,
            cancellationToken);

    public Task<ToolOutcome> SessionAsync(
        string action,
        string? name,
        CancellationToken cancellationToken) =>
        ((ISessionOperations)supervisor).SessionAsync(
            action,
            name,
            cancellationToken);
}
