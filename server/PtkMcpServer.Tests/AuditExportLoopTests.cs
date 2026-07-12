using System.Threading.Channels;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditExportLoopTests
{
    private static readonly Guid BootId =
        Guid.Parse("12345678-1234-4abc-8def-0123456789ab");

    [Fact]
    public async Task Retry_after_waits_exactly_without_an_early_second_attempt()
    {
        var source = new ScriptedSource(
            Step(
                AuditExportCoordinatorStepKind.Retry,
                retryAfter: TimeSpan.FromMinutes(17)),
            Step(AuditExportCoordinatorStepKind.Complete));
        var delays = new ControlledDelay();
        var loop = new AuditExportLoop(source, delay: delays.DelayAsync);

        var completion = loop.Start();
        var retry = await delays.NextAsync();

        Assert.Equal(TimeSpan.FromMinutes(17), retry.Duration);
        Assert.Equal(1, source.CallCount);
        Assert.Equal(AuditExportLoopState.WaitingToRetry, loop.Snapshot.State);
        Assert.Equal(TimeSpan.FromMinutes(17), loop.Snapshot.ScheduledDelay);

        retry.Release();
        await completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, source.CallCount);
        Assert.Equal(AuditExportLoopState.Completed, loop.Snapshot.State);
        await loop.DisposeAsync();
        Assert.True(source.IsDisposed);
    }

    [Fact]
    public async Task Acknowledged_progress_resets_the_full_jitter_series()
    {
        var source = new ScriptedSource(
            Step(AuditExportCoordinatorStepKind.Retry),
            Step(AuditExportCoordinatorStepKind.Advanced),
            Step(AuditExportCoordinatorStepKind.Retry),
            Step(AuditExportCoordinatorStepKind.Complete));
        var delays = new ControlledDelay();
        var loop = new AuditExportLoop(
            source,
            new AuditExportRetrySchedule(() => 0.5d),
            delay: delays.DelayAsync);

        var completion = loop.Start();
        var firstRetry = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(500), firstRetry.Duration);
        firstRetry.Release();

        var secondRetry = await delays.NextAsync();
        Assert.Equal(3, source.CallCount);
        Assert.Equal(TimeSpan.FromMilliseconds(500), secondRetry.Duration);
        secondRetry.Release();

        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, source.CallCount);
        await loop.DisposeAsync();
    }

    [Fact]
    public async Task Idle_and_blocked_steps_poll_without_a_tight_loop()
    {
        var blockedStep = Step(
            AuditExportCoordinatorStepKind.Blocked,
            AuditExportFailureClass.Configuration);
        var source = new ScriptedSource(
            Step(AuditExportCoordinatorStepKind.Idle),
            blockedStep,
            blockedStep,
            Step(AuditExportCoordinatorStepKind.Complete));
        var delays = new ControlledDelay();
        var loop = new AuditExportLoop(
            source,
            idlePollInterval: TimeSpan.FromSeconds(3),
            delay: delays.DelayAsync);

        var completion = loop.Start();
        var idle = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(3), idle.Duration);
        Assert.Equal(1, source.CallCount);
        Assert.Equal(AuditExportLoopState.WaitingForWork, loop.Snapshot.State);
        idle.Release();

        var blocked = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(3), blocked.Duration);
        Assert.Equal(3, source.CallCount);
        Assert.Equal(
            AuditExportCoordinatorStepKind.Blocked,
            loop.Snapshot.LastStep?.Kind);
        blocked.Release();

        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, source.CallCount);
        await loop.DisposeAsync();
    }

    [Fact]
    public async Task Disposal_cancels_an_inflight_step_before_disposing_its_source()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource(async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The infinite delay completed.");
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }
        });
        var loop = new AuditExportLoop(source);

        var completion = loop.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await loop.DisposeAsync();

        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(source.IsDisposed);
        Assert.Equal(AuditExportLoopState.Disposed, loop.Snapshot.State);
    }

    private static AuditExportCoordinatorStep Step(
        AuditExportCoordinatorStepKind kind,
        AuditExportFailureClass? failureClass = null,
        TimeSpan? retryAfter = null) =>
        new(
            kind,
            BootId,
            IsCurrentBoot: true,
            EventId: kind is AuditExportCoordinatorStepKind.Advanced or
                AuditExportCoordinatorStepKind.Retry or
                AuditExportCoordinatorStepKind.Blocked
                ? Guid.CreateVersion7()
                : null,
            DetailCode: kind.ToString().ToLowerInvariant(),
            failureClass,
            retryAfter);

    private sealed class ScriptedSource : IAuditExportStepSource
    {
        private readonly object _gate = new();
        private readonly Queue<Func<CancellationToken, Task<AuditExportCoordinatorStep>>>
            _steps;
        private int _callCount;
        private int _disposed;

        internal ScriptedSource(params AuditExportCoordinatorStep[] steps)
            : this(steps.Select<
                AuditExportCoordinatorStep,
                Func<CancellationToken, Task<AuditExportCoordinatorStep>>>(
                    step => cancellationToken =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult(step);
                    }).ToArray())
        {
        }

        internal ScriptedSource(
            params Func<CancellationToken, Task<AuditExportCoordinatorStep>>[] steps)
        {
            _steps = new Queue<
                Func<CancellationToken, Task<AuditExportCoordinatorStep>>>(steps);
        }

        internal int CallCount => Volatile.Read(ref _callCount);

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public Task<AuditExportCoordinatorStep> ExportNextAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Func<CancellationToken, Task<AuditExportCoordinatorStep>> step;
            lock (_gate)
            {
                if (_steps.Count == 0)
                    throw new InvalidOperationException("No scripted export step remains.");
                step = _steps.Dequeue();
            }
            return step(cancellationToken);
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class ControlledDelay
    {
        private readonly Channel<PendingDelay> _pending =
            Channel.CreateUnbounded<PendingDelay>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

        internal Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            var pending = new PendingDelay(duration);
            if (!_pending.Writer.TryWrite(pending))
                throw new InvalidOperationException("The delay channel is closed.");
            return pending.Completion.WaitAsync(cancellationToken);
        }

        internal async Task<PendingDelay> NextAsync() =>
            await _pending.Reader.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class PendingDelay(TimeSpan duration)
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TimeSpan Duration { get; } = duration;

        internal Task Completion => _release.Task;

        internal void Release() => _release.TrySetResult();
    }
}
