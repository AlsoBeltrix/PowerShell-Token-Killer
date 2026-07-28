using PtkMcpServer.Audit;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Sessions;

/// <summary>
/// Owns the current connection's session runtime. Later worker slices replace
/// the in-process runtime behind this boundary without changing public tools.
/// </summary>
internal sealed class WorkerSupervisor : ISessionOperations, ISessionLifetime
{
    private readonly ISessionOperations _operations;
    private readonly ISessionLifetime _lifetime;
    private readonly NamedSessionSupervisor? _namedSessions;
    private int _disposed;

    internal WorkerSupervisor(Func<SessionRuntime> createRuntime)
    {
        ArgumentNullException.ThrowIfNull(createRuntime);
        var runtime = createRuntime();
        _operations = runtime;
        _lifetime = runtime;
        var limits = WorkerOperationProtocol.CreateLimits(
            DefaultSessionRuntimeFactory.ReadCallTimeout(),
            DefaultSessionRuntimeFactory.ReadMaxCallTimeout());
        _namedSessions = new NamedSessionSupervisor(
            () => ProcessSessionWorkerFactory.CreateDefault(limits),
            startupTimeout: TimeSpan.FromSeconds(30),
            containmentGrace: TimeSpan.FromSeconds(10));
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

    internal WorkerSupervisor(
        ISessionOperations operations,
        ISessionLifetime lifetime,
        NamedSessionSupervisor namedSessions)
        : this(operations, lifetime)
    {
        _namedSessions = namedSessions ??
            throw new ArgumentNullException(nameof(namedSessions));
    }

    internal NamedSessionSupervisor NamedSessions =>
        _namedSessions ?? throw new InvalidOperationException(
            "This supervisor has no named-session registry.");

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

    public async Task ShutdownAsync()
    {
        try
        {
            if (_namedSessions is not null)
                await _namedSessions.ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            await _lifetime.ShutdownAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            if (_namedSessions is not null)
                _namedSessions.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            _lifetime.Dispose();
        }
    }
}
