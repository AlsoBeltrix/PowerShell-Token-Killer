namespace PtkMcpServer.Sessions;

/// <summary>
/// Tool-facing operations for connection-owned named sessions. This is not
/// the worker wire contract: the supervisor resolves a session and serializes
/// only bounded protocol values to that session's worker.
/// </summary>
public interface ISessionOperations
{
    Task<string> InvokeAsync(
        string script,
        CancellationToken cancellationToken,
        bool raw,
        string route,
        int timeoutSeconds,
        string session,
        OutputStore? outputStore);

    Task<string> StateAsync(
        bool listAvailable,
        string session,
        CancellationToken cancellationToken);

    Task<string> ResetAsync(
        string session,
        CancellationToken cancellationToken);

    Task<string> SessionAsync(
        string action,
        string? name,
        CancellationToken cancellationToken);
}

/// <summary>
/// Ordered owned-session drain used by the supervisor lifecycle. This remains
/// separate from tool operations so request code cannot initiate shutdown.
/// </summary>
internal interface ISessionLifetime : IDisposable
{
    Task ShutdownAsync();
}
