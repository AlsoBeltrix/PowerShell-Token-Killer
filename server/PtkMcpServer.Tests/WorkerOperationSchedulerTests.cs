using System.Collections.Concurrent;
using System.Text;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerOperationSchedulerTests
{
    private static readonly Guid SessionId =
        Guid.Parse("ff8286b1-f7df-41ae-991d-480187bef484");
    private static readonly Guid ArtifactId =
        Guid.Parse("86fe4df2-77d6-4744-a421-0cc62ab64c67");
    private const long Incarnation = 4;
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly WorkerProtocolLimits Limits =
        WorkerOperationProtocol.CreateLimits(
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1));

    [Fact]
    public async Task Invoke_dispatches_off_admission_and_writes_exactly_one_result()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var frames = new ConcurrentQueue<WorkerEnvelope>();
        var scheduler = Scheduler(
            new DelegateExecutor(async (request, cancellationToken) =>
            {
                Assert.IsType<WorkerInvokeRequest>(request);
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return new WorkerInvokeExecutionResult(
                    WorkerResultStatus.Completed,
                    "done");
            }),
            frames);

        scheduler.Admit(Invoke(2));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(frames);
        release.TrySetResult();
        await WaitUntilAsync(() => frames.Count == 1);

        var terminal = Assert.Single(frames);
        Assert.Equal(WorkerMessageKind.Result, terminal.Kind);
        Assert.Equal(
            new WorkerResult(2, WorkerResultStatus.Completed, "done", null),
            WorkerOperationProtocol.ParseResult(
                terminal,
                SessionId,
                Incarnation));
        Assert.Equal(0, scheduler.OutstandingCount);
    }

    [Fact]
    public async Task State_query_can_report_busy_while_an_invoke_is_active()
    {
        var invokeEntered = NewSignal();
        var releaseInvoke = NewSignal();
        var frames = new ConcurrentQueue<WorkerEnvelope>();
        var scheduler = Scheduler(
            new DelegateExecutor(async (request, cancellationToken) =>
            {
                if (request is WorkerInvokeRequest)
                {
                    invokeEntered.TrySetResult();
                    await releaseInvoke.Task.WaitAsync(cancellationToken);
                    return new WorkerInvokeExecutionResult(
                        WorkerResultStatus.Completed,
                        "invoke complete");
                }
                return new WorkerStateExecutionResult(
                    Available: false,
                    "runspace: busy",
                    "runspace_busy");
            }),
            frames);

        scheduler.Admit(Invoke(2));
        await invokeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.Admit(WorkerOperationProtocol.CreateStateQueryEnvelope(
            SessionId,
            Incarnation,
            3,
            listAvailable: false));
        await WaitUntilAsync(() => frames.Count == 1);

        var snapshot = WorkerOperationProtocol.ParseStateSnapshot(
            Assert.Single(frames),
            SessionId,
            Incarnation);
        Assert.False(snapshot.Available);
        Assert.Equal("runspace_busy", snapshot.DetailCode);

        releaseInvoke.TrySetResult();
        await WaitUntilAsync(() => frames.Count == 2);
        Assert.Equal(
            1,
            frames.Count(frame => frame.Kind == WorkerMessageKind.Result));
    }

    [Fact]
    public async Task Cancel_targets_one_request_and_duplicate_or_unknown_cancel_is_benign()
    {
        var firstEntered = NewSignal();
        var secondEntered = NewSignal();
        var releaseSecond = NewSignal();
        var frames = new ConcurrentQueue<WorkerEnvelope>();
        var scheduler = Scheduler(
            new DelegateExecutor(async (request, cancellationToken) =>
            {
                if (request.RequestId == 2)
                {
                    firstEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                secondEntered.TrySetResult();
                await releaseSecond.Task.WaitAsync(cancellationToken);
                return new WorkerInvokeExecutionResult(
                    WorkerResultStatus.Completed,
                    "second");
            }),
            frames);

        scheduler.Admit(Invoke(2));
        scheduler.Admit(Invoke(3));
        await Task.WhenAll(firstEntered.Task, secondEntered.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = WorkerOperationProtocol.CreateCancelEnvelope(
            SessionId,
            Incarnation,
            2);
        scheduler.Admit(cancel);
        scheduler.Admit(cancel);
        scheduler.Admit(WorkerOperationProtocol.CreateCancelEnvelope(
            SessionId,
            Incarnation,
            999));
        await WaitUntilAsync(() => frames.Count == 1);

        var canceled = WorkerOperationProtocol.ParseResult(
            Assert.Single(frames),
            SessionId,
            Incarnation);
        Assert.Equal(WorkerResultStatus.Canceled, canceled.Status);
        Assert.Equal("request_canceled", canceled.DetailCode);

        releaseSecond.TrySetResult();
        await WaitUntilAsync(() => frames.Count == 2);
        Assert.Contains(
            frames,
            frame => frame.RequestId == 3 &&
                WorkerOperationProtocol.ParseResult(
                    frame,
                    SessionId,
                    Incarnation).Status == WorkerResultStatus.Completed);
    }

    [Fact]
    public async Task Expired_request_never_executes_and_duplicate_id_is_rejected()
    {
        var calls = 0;
        var clockReads = 0;
        var frames = new ConcurrentQueue<WorkerEnvelope>();
        var scheduler = Scheduler(
            new DelegateExecutor((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<WorkerExecutionResult>(
                    new WorkerInvokeExecutionResult(
                        WorkerResultStatus.Completed,
                        "should not run"));
            }),
            frames,
            utcNow: () => Interlocked.Increment(ref clockReads) == 1
                ? Now
                : Now.AddSeconds(2));

        scheduler.Admit(WorkerOperationProtocol.CreateInvokeEnvelope(
            SessionId,
            Incarnation,
            2,
            "Get-Date",
            false,
            WorkerInvokeRoute.Auto,
            timeoutSeconds: 1,
            artifact: null,
            Limits));
        await WaitUntilAsync(() => frames.Count == 1);
        Assert.Equal(0, calls);
        Assert.Equal(
            WorkerResultStatus.TimedOut,
            WorkerOperationProtocol.ParseResult(
                Assert.Single(frames),
                SessionId,
                Incarnation).Status);

        var replay = Assert.Throws<WorkerProtocolException>(
            () => scheduler.Admit(Invoke(2)));
        Assert.Equal("operation_request_replay", replay.DetailCode);
    }

    [Fact]
    public async Task Artifact_chunks_seal_then_one_result_in_order()
    {
        var bytes = Encoding.UTF8.GetBytes(new string(
            'x',
            Limits.MaximumArtifactChunkBytes + 17));
        var frames = new ConcurrentQueue<WorkerEnvelope>();
        var scheduler = Scheduler(
            new DelegateExecutor((_, _) =>
                Task.FromResult<WorkerExecutionResult>(
                    new WorkerInvokeExecutionResult(
                        WorkerResultStatus.Completed,
                        "done",
                        Artifact: new WorkerArtifactPayload(ArtifactId, bytes)))),
            frames);
        scheduler.Admit(WorkerOperationProtocol.CreateInvokeEnvelope(
            SessionId,
            Incarnation,
            2,
            "'artifact'",
            false,
            WorkerInvokeRoute.Pwsh,
            0,
            new WorkerArtifactRequest(ArtifactId, bytes.Length),
            Limits));
        await WaitUntilAsync(() => frames.Count == 4);

        var ordered = frames.ToArray();
        Assert.Equal(
            [
                WorkerMessageKind.ArtifactChunk,
                WorkerMessageKind.ArtifactChunk,
                WorkerMessageKind.ArtifactSeal,
                WorkerMessageKind.Result,
            ],
            ordered.Select(frame => frame.Kind));
        using var receiver = new WorkerArtifactReceiver(
            2,
            new WorkerArtifactRequest(ArtifactId, bytes.Length));
        receiver.Accept(WorkerOperationProtocol.ParseArtifactChunk(
            ordered[0],
            SessionId,
            Incarnation,
            Limits));
        receiver.Accept(WorkerOperationProtocol.ParseArtifactChunk(
            ordered[1],
            SessionId,
            Incarnation,
            Limits));
        receiver.Accept(WorkerOperationProtocol.ParseArtifactSeal(
            ordered[2],
            SessionId,
            Incarnation));
        Assert.True(receiver.IsSealed);
        Assert.Equal(bytes.Length, receiver.Length);
        Assert.Equal(
            WorkerResultStatus.Completed,
            WorkerOperationProtocol.ParseResult(
                ordered[3],
                SessionId,
                Incarnation).Status);
    }

    [Fact]
    public async Task Drain_cancels_and_observes_all_work_then_refuses_admission()
    {
        var entered = NewSignal();
        var frames = new ConcurrentQueue<WorkerEnvelope>();
        var scheduler = Scheduler(
            new DelegateExecutor(async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }),
            frames);
        scheduler.Admit(Invoke(2));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, scheduler.OutstandingCount);
        Assert.Equal(
            WorkerResultStatus.Canceled,
            WorkerOperationProtocol.ParseResult(
                Assert.Single(frames),
                SessionId,
                Incarnation).Status);
        Assert.Throws<InvalidOperationException>(() => scheduler.Admit(Invoke(3)));
    }

    [Fact]
    public async Task Outstanding_request_bound_refuses_before_execution_capacity_can_grow()
    {
        var frames = new ConcurrentQueue<WorkerEnvelope>();
        var scheduler = Scheduler(
            new DelegateExecutor(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }),
            frames);

        for (var requestId = 2L;
             requestId < 2L + WorkerOperationScheduler.MaximumOutstandingRequests;
             requestId++)
        {
            scheduler.Admit(Invoke(requestId));
        }

        var exception = Assert.Throws<WorkerProtocolException>(
            () => scheduler.Admit(Invoke(
                2L + WorkerOperationScheduler.MaximumOutstandingRequests)));
        Assert.Equal("operation_capacity_exceeded", exception.DetailCode);

        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(
            WorkerOperationScheduler.MaximumOutstandingRequests,
            frames.Count);
        Assert.All(
            frames,
            frame => Assert.Equal(
                WorkerResultStatus.Canceled,
                WorkerOperationProtocol.ParseResult(
                    frame,
                    SessionId,
                    Incarnation).Status));
    }

    [Fact]
    public async Task Writer_failure_latches_fatal_and_cancels_other_work()
    {
        var secondEntered = NewSignal();
        var secondCanceled = NewSignal();
        var scheduler = new WorkerOperationScheduler(
            SessionId,
            Incarnation,
            Limits,
            initialRequestIdHighWater: 1,
            new DelegateExecutor(async (request, cancellationToken) =>
            {
                if (request.RequestId == 2)
                    return new WorkerInvokeExecutionResult(
                        WorkerResultStatus.Completed,
                        "first");
                secondEntered.TrySetResult();
                using var registration = cancellationToken.Register(
                    () => secondCanceled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }),
            (_, _) => Task.FromException(new IOException("injected write failure")),
            utcNow: () => Now);

        scheduler.Admit(Invoke(2));
        scheduler.Admit(Invoke(3));
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<IOException>(async () =>
            await scheduler.Fatal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("injected write failure", failure.Message);
        await secondCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<WorkerProtocolException>(() => scheduler.Admit(Invoke(4)));
    }

    private static WorkerOperationScheduler Scheduler(
        IWorkerOperationExecutor executor,
        ConcurrentQueue<WorkerEnvelope> frames,
        Func<DateTimeOffset>? utcNow = null,
        Func<DateTimeOffset, CancellationToken, Task>? waitUntilDeadline = null)
        => new(
            SessionId,
            Incarnation,
            Limits,
            initialRequestIdHighWater: 1,
            executor,
            (frame, _) =>
            {
                frames.Enqueue(frame);
                return Task.CompletedTask;
            },
            utcNow ?? (() => Now),
            waitUntilDeadline);

    private static WorkerEnvelope Invoke(long requestId) =>
        WorkerOperationProtocol.CreateInvokeEnvelope(
            SessionId,
            Incarnation,
            requestId,
            "'ok'",
            false,
            WorkerInvokeRoute.Pwsh,
            0,
            null,
            Limits);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DelegateExecutor(
        Func<WorkerOperationRequest, CancellationToken, Task<WorkerExecutionResult>> execute) :
        IWorkerOperationExecutor
    {
        public Task<WorkerExecutionResult> ExecuteAsync(
            WorkerOperationRequest request,
            CancellationToken cancellationToken) =>
            execute(request, cancellationToken);
    }
}
