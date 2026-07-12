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
        var health = AnchoredHealth();
        var loop = new AuditExportLoop(
            source,
            idlePollInterval: TimeSpan.FromSeconds(3),
            delay: delays.DelayAsync,
            healthObserver: health.ExportObserver);

        var completion = loop.Start();
        var idle = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(3), idle.Duration);
        Assert.Equal(1, source.CallCount);
        Assert.Equal(AuditExportLoopState.WaitingForWork, loop.Snapshot.State);
        Assert.Equal(AuditExporterState.Idle, health.Snapshot().Exporter.State);
        idle.Release();

        var blocked = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(3), blocked.Duration);
        Assert.Equal(3, source.CallCount);
        Assert.Equal(
            AuditExportCoordinatorStepKind.Blocked,
            loop.Snapshot.LastStep?.Kind);
        Assert.Equal(AuditExporterState.Stalled, health.Snapshot().Exporter.State);
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
        var health = AnchoredHealth();
        var loop = new AuditExportLoop(source, healthObserver: health.ExportObserver);

        var completion = loop.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AuditExporterState.Running, health.Snapshot().Exporter.State);

        await loop.DisposeAsync();

        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(source.IsDisposed);
        Assert.Equal(AuditExportLoopState.Disposed, loop.Snapshot.State);
        Assert.Equal(AuditExporterState.Stopped, health.Snapshot().Exporter.State);
    }

    [Fact]
    public async Task Health_observer_tracks_retry_ack_warning_and_stalled_block_without_degrading_journal()
    {
        var now = new DateTimeOffset(2026, 7, 12, 10, 30, 0, TimeSpan.Zero);
        var acknowledgedEventId = Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
        var blockedEventId = Guid.Parse("32345678-1234-4abc-8def-0123456789ab");
        var retry = new AuditExportCoordinatorStep(
            AuditExportCoordinatorStepKind.Retry,
            BootId,
            IsCurrentBoot: true,
            acknowledgedEventId,
            "transport.connection",
            RetryAfter: TimeSpan.FromMinutes(17));
        var acknowledged = new AuditExportCoordinatorStep(
            AuditExportCoordinatorStepKind.Advanced,
            BootId,
            IsCurrentBoot: true,
            acknowledgedEventId,
            "otlp.acknowledged_warning",
            HasHealthWarning: true);
        var blocked = new AuditExportCoordinatorStep(
            AuditExportCoordinatorStepKind.Blocked,
            BootId,
            IsCurrentBoot: false,
            blockedEventId,
            "retry.http.401",
            AuditExportFailureClass.Configuration);
        var source = new ScriptedSource(
            retry,
            acknowledged,
            blocked,
            blocked,
            Step(AuditExportCoordinatorStepKind.Complete));
        var delays = new ControlledDelay();
        var health = AnchoredHealth();
        var loop = new AuditExportLoop(
            source,
            idlePollInterval: TimeSpan.FromSeconds(3),
            timeProvider: new FixedTimeProvider(now),
            delay: delays.DelayAsync,
            healthObserver: health.ExportObserver);

        var completion = loop.Start();
        var retryDelay = await delays.NextAsync();

        var retrying = health.Snapshot();
        Assert.Equal(AuditHealthState.Healthy, retrying.State);
        Assert.Equal(AuditExporterState.Retrying, retrying.Exporter.State);
        Assert.Equal(TimeSpan.FromMinutes(17), retrying.Exporter.ScheduledDelay);
        Assert.Equal(now.AddMinutes(17), retrying.Exporter.NextActionUtc);
        Assert.Null(retrying.Exporter.LastAcknowledgment);
        retryDelay.Release();

        var blockedDelay = await delays.NextAsync();
        var stalled = health.Snapshot();
        Assert.Equal(AuditHealthState.Healthy, stalled.State);
        Assert.Equal(AuditExporterState.Stalled, stalled.Exporter.State);
        Assert.Equal(TimeSpan.FromSeconds(3), stalled.Exporter.ScheduledDelay);
        Assert.True(stalled.Exporter.HasHealthWarning);
        Assert.Equal(now, stalled.Exporter.LastProgress?.ObservedUtc);
        Assert.Equal(acknowledgedEventId, stalled.Exporter.LastProgress?.EventId);
        Assert.Equal(now, stalled.Exporter.LastAcknowledgment?.ObservedUtc);
        Assert.Equal(acknowledgedEventId, stalled.Exporter.LastAcknowledgment?.EventId);
        Assert.True(stalled.Exporter.LastAcknowledgment?.HasHealthWarning);
        Assert.Equal(BootId, stalled.Exporter.Blocked?.SupervisorBootId);
        Assert.False(stalled.Exporter.Blocked?.IsCurrentBoot);
        Assert.Equal(blockedEventId, stalled.Exporter.Blocked?.EventId);
        Assert.Equal("retry.http.401", stalled.Exporter.Blocked?.DetailCode);
        Assert.Equal("configuration", stalled.Exporter.Blocked?.FailureClass);
        var normalLine = AuditExporterHealthText.FormatNormal(stalled.Exporter);
        Assert.Contains("audit exporter: stalled, warning true", normalLine, StringComparison.Ordinal);
        Assert.Contains("audit exporter block: supervisor boot 12345678-1234-4abc-8def-0123456789ab", normalLine, StringComparison.Ordinal);
        Assert.Contains("detail retry.http.401, failure configuration", normalLine, StringComparison.Ordinal);
        Assert.Contains("audit exporter acknowledgment:", normalLine, StringComparison.Ordinal);

        blockedDelay.Release();
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AuditExporterState.Completed, health.Snapshot().Exporter.State);
        Assert.Equal(AuditHealthState.Healthy, health.Snapshot().State);
        await loop.DisposeAsync();
        Assert.Equal(AuditExporterState.Stopped, health.Snapshot().Exporter.State);
    }

    [Fact]
    public async Task Export_loop_fault_is_reported_without_marking_the_journal_unavailable()
    {
        var health = AnchoredHealth();
        var source = new ScriptedSource(_ =>
            Task.FromException<AuditExportCoordinatorStep>(new IOException("secret transport detail")));
        var loop = new AuditExportLoop(source, healthObserver: health.ExportObserver);

        await Assert.ThrowsAsync<IOException>(() => loop.Start());

        var snapshot = health.Snapshot();
        Assert.Equal(AuditHealthState.Healthy, snapshot.State);
        Assert.Equal(AuditExporterState.Faulted, snapshot.Exporter.State);
        Assert.Null(snapshot.Exporter.Blocked);
        Assert.DoesNotContain("secret", snapshot.Exporter.ToString(), StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<IOException>(async () => await loop.DisposeAsync());
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

    private static AuditHealth AnchoredHealth() => new(AuditOptions.Create(
        Path.Combine(Path.GetTempPath(), "ptk-export-health-" + Guid.NewGuid().ToString("N")),
        AuditProtectionMode.Anchored,
        new string('a', 64)));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

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
