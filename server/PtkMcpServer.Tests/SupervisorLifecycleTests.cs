using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tests;

public sealed class SupervisorLifecycleTests
{
    [Fact]
    public async Task Stop_refuses_new_calls_cancels_and_drains_all_active_calls_then_disposes_session()
    {
        var session = new RecordingSession();
        using var lifecycle = new SupervisorLifecycle(session);
        Assert.True(lifecycle.TryBeginCall(
            CancellationToken.None,
            out var firstLease,
            out var firstCancellation));
        Assert.True(lifecycle.TryBeginCall(
            CancellationToken.None,
            out var secondLease,
            out var secondCancellation));

        var firstCancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstRegistration = firstCancellation.Register(
            () => firstCancelled.TrySetResult(true));
        using var secondRegistration = secondCancellation.Register(
            () => secondCancelled.TrySetResult(true));

        Task? stop = null;
        try
        {
            stop = lifecycle.StopAsync(CancellationToken.None);
            await Task.WhenAll(firstCancelled.Task, secondCancelled.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(lifecycle.TryBeginCall(
                CancellationToken.None,
                out var refusedLease,
                out var refusedCancellation));
            Assert.Null(refusedLease);
            Assert.True(refusedCancellation.IsCancellationRequested);
            Assert.Empty(session.Events);

            firstLease!.Dispose();
            await Task.Delay(50);
            Assert.False(stop.IsCompleted, "shutdown overtook an active call");
            Assert.Empty(session.Events);

            secondLease!.Dispose();
            await session.ShutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted, "session disposal overtook session shutdown");
            Assert.Equal(["shutdown"], session.Events);

            session.ReleaseShutdown.TrySetResult(true);
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["shutdown", "dispose"], session.Events);
        }
        finally
        {
            firstLease?.Dispose();
            secondLease?.Dispose();
            session.ReleaseShutdown.TrySetResult(true);
            if (stop is not null)
            {
                try { await stop.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* preserve the primary assertion */ }
            }
        }
    }

    private sealed class RecordingSession : ISessionLifetime
    {
        private readonly List<string> _events = [];

        internal IReadOnlyList<string> Events => _events;
        internal TaskCompletionSource<bool> ShutdownEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseShutdown { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ShutdownAsync()
        {
            _events.Add("shutdown");
            ShutdownEntered.TrySetResult(true);
            await ReleaseShutdown.Task;
        }

        public void Dispose() => _events.Add("dispose");
    }
}
