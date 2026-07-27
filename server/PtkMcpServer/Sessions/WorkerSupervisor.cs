using PtkMcpServer.Audit;

namespace PtkMcpServer.Sessions;

/// <summary>
/// Owns the current connection's session runtime. Later worker slices replace
/// the in-process runtime behind this boundary without changing public tools.
/// </summary>
internal sealed class WorkerSupervisor : ISessionOperations, ISessionLifetime
{
    private readonly ISessionOperations _operations;
    private readonly ISessionLifetime _lifetime;
    private int _disposed;

    internal WorkerSupervisor(Func<SessionRuntime> createRuntime)
    {
        ArgumentNullException.ThrowIfNull(createRuntime);
        var runtime = createRuntime();
        _operations = runtime;
        _lifetime = runtime;
    }

    internal WorkerSupervisor(
        ISessionOperations operations,
        ISessionLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(lifetime);
        _operations = operations;
        _lifetime = lifetime;
    }

    Task<string> ISessionOperations.InvokeAsync(
        string script,
        CancellationToken cancellationToken,
        bool raw,
        string route,
        bool background,
        int timeoutSeconds,
        OutputStore? outputStore) =>
        _operations.InvokeAsync(
            script,
            cancellationToken,
            raw,
            route,
            background,
            timeoutSeconds,
            outputStore);

    Task<string> ISessionOperations.JobAsync(
        string action,
        CancellationToken cancellationToken,
        long id,
        long offset) =>
        _operations.JobAsync(action, cancellationToken, id, offset);

    Task<string> ISessionOperations.StateAsync(
        bool listAvailable,
        CancellationToken cancellationToken) =>
        _operations.StateAsync(listAvailable, cancellationToken);

    Task<string> ISessionOperations.ResetAsync(CancellationToken cancellationToken) =>
        _operations.ResetAsync(cancellationToken);

    public Task ShutdownAsync() => _lifetime.ShutdownAsync();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Dispose();
    }
}
