using Microsoft.Extensions.Hosting;
using PtkMcpServer.Sessions;

namespace PtkMcpServer;

/// <summary>
/// Connection-level request admission and ordered shutdown. Audit availability
/// has no role in admission.
/// </summary>
internal sealed class SupervisorLifecycle : IHostedService, IDisposable
{
    private readonly object _gate = new();
    private readonly ISessionLifetime _sessions;
    private readonly CancellationTokenSource _shutdown = new();

    private TaskCompletionSource<bool>? _activeCallsDrained;
    private Task? _stopTask;
    private int _activeCalls;
    private bool _stopping;
    private bool _disposed;

    internal SupervisorLifecycle(ISessionLifetime sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    internal bool TryBeginCall(
        CancellationToken requestCancellation,
        out SupervisorCallLease? lease,
        out CancellationToken callCancellation)
    {
        lock (_gate)
        {
            if (_disposed || _stopping)
            {
                lease = null;
                callCancellation = new CancellationToken(canceled: true);
                return false;
            }

            if (_activeCalls == 0)
            {
                _activeCallsDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _activeCalls = checked(_activeCalls + 1);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                requestCancellation,
                _shutdown.Token);
            lease = new SupervisorCallLease(ReleaseActiveCall, linked);
            callCancellation = linked.Token;
            return true;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stopTask is not null)
                return _stopTask;

            _stopping = true;
            _shutdown.Cancel();
            var activeCalls = _activeCalls == 0
                ? Task.CompletedTask
                : _activeCallsDrained!.Task;
            _stopTask = StopCoreAsync(activeCalls);
            return _stopTask;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }

        StopAsync(CancellationToken.None).GetAwaiter().GetResult();

        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _shutdown.Dispose();
    }

    private async Task StopCoreAsync(Task activeCalls)
    {
        await activeCalls.ConfigureAwait(false);
        try
        {
            await _sessions.ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            _sessions.Dispose();
        }
    }

    private void ReleaseActiveCall()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_gate)
        {
            if (_activeCalls < 1)
                return;
            _activeCalls--;
            if (_activeCalls == 0)
            {
                drained = _activeCallsDrained;
                _activeCallsDrained = null;
            }
        }
        drained?.TrySetResult(true);
    }
}

internal sealed class SupervisorCallLease(
    Action release,
    CancellationTokenSource linkedCancellation) : IDisposable
{
    private Action? _release = release;
    private CancellationTokenSource? _linkedCancellation = linkedCancellation;

    public void Dispose()
    {
        Interlocked.Exchange(ref _linkedCancellation, null)?.Dispose();
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
