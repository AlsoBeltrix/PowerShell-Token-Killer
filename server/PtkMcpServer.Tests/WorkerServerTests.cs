using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerServerTests
{
    private static readonly Guid SessionId = Guid.Parse("8de8da91-1522-4d93-a768-5a07fe55e6ee");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly WorkerProtocolLimits Limits =
        WorkerOperationProtocol.CreateLimits(
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1));

    [Fact]
    public async Task Cancellation_before_initialize_never_constructs_a_runtime()
    {
        using var input = new FeedableReadStream(ignoreCancellation: true);
        using var output = new CapturingWriteStream();
        using var cancellation = new CancellationTokenSource();
        var factoryCalls = 0;
        var server = Server(
            input,
            output,
            (_, _) =>
            {
                factoryCalls++;
                throw new InvalidOperationException("factory must not run");
            });

        var run = server.RunAsync(cancellation.Token);
        cancellation.Cancel();
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new WorkerServerExit(WorkerServerExitKind.Canceled, "canceled"), exit);
        Assert.Equal(0, factoryCalls);
        Assert.Empty(await output.FramesAsync());
    }

    [Fact]
    public async Task Initialize_then_shutdown_correlate_responses_and_await_owned_lifetime()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var shutdownEntered = NewSignal();
        var releaseShutdown = NewSignal();
        var lifetime = new RecordingLifetime(async () =>
        {
            shutdownEntered.TrySetResult();
            await releaseShutdown.Task;
        });
        WorkerInitializeRequest? captured = null;
        var initialize = Initialize(requestId: 41, generation: 7, deadline: Now.AddMinutes(1));
        var shutdown = Envelope(
            WorkerMessageKind.Shutdown,
            42,
            EmptyPayload(),
            incarnation: 7);
        input.Enqueue(Frame(initialize));

        var server = Server(
            input,
            output,
            (request, _) =>
            {
                captured = request;
                return Task.FromResult<IWorkerSession>(lifetime);
            });
        Task<WorkerServerExit>? run = null;
        try
        {
            run = server.RunAsync();
            await output.WaitForWritesAsync(1);
            input.Enqueue(Frame(shutdown));
            await shutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted, "worker emitted terminal shutdown before lifetime drain");
            var beforeRelease = await output.FramesAsync();
            Assert.Equal([WorkerMessageKind.Ready],
                beforeRelease.Select(frame => frame.Kind));

            releaseShutdown.TrySetResult();
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new WorkerServerExit(WorkerServerExitKind.Shutdown, "shutdown"), exit);
        }
        finally
        {
            releaseShutdown.TrySetResult();
            input.Complete();
            if (run is not null)
            {
                try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { /* preserve primary failure */ }
            }
        }

        Assert.Equal(
            new WorkerInitializeRequest(
                SessionId,
                7,
                41,
                Now.AddMinutes(1),
                Limits),
            captured);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
        var frames = await output.FramesAsync();
        Assert.Equal(2, frames.Count);
        AssertProtocolIdentity(frames);
        Assert.Equal([41L, 42L], frames.Select(frame => frame.RequestId));
        Assert.Equal(
            [WorkerMessageKind.Ready, WorkerMessageKind.Stopped],
            frames.Select(frame => frame.Kind));
    }

    [Fact]
    public async Task First_non_initialize_frame_is_protocol_fatal_without_runtime_construction()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        input.Enqueue(Frame(Envelope(WorkerMessageKind.Invoke, 1, EmptyPayload())));
        input.Complete();
        var factoryCalls = 0;
        var server = Server(
            input,
            output,
            (_, _) =>
            {
                factoryCalls++;
                throw new InvalidOperationException("factory must not run");
            });

        var exit = await server.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(WorkerServerExitKind.ProtocolError, "initialize_required"),
            exit);
        Assert.Equal(0, factoryCalls);
        Assert.Empty(await output.FramesAsync());
    }

    [Fact]
    public async Task Request_stream_failure_is_classified_as_transport_failure()
    {
        using var input = new ThrowingReadStream();
        using var output = new CapturingWriteStream();
        var factoryCalls = 0;
        var server = Server(
            input,
            output,
            (_, _) =>
            {
                factoryCalls++;
                throw new InvalidOperationException("factory must not run");
            });

        var exit = await server.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(
                WorkerServerExitKind.TransportFailure,
                "request_transport_failure"),
            exit);
        Assert.Equal(0, factoryCalls);
        Assert.Empty(await output.FramesAsync());
    }

    [Fact]
    public async Task Event_stream_failure_is_classified_as_transport_failure()
    {
        using var input = new FeedableReadStream();
        using var output = new ThrowingWriteStream();
        input.Enqueue(Frame(Initialize(1, 1, Now.AddMinutes(1))));
        var lifetime = new RecordingLifetime();
        var factoryCalls = 0;
        var server = Server(
            input,
            output,
            (_, _) =>
            {
                factoryCalls++;
                return Task.FromResult<IWorkerSession>(lifetime);
            });

        var exit = await server.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(
                WorkerServerExitKind.TransportFailure,
                "event_transport_failure"),
            exit);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Operation_response_transport_failure_keeps_transport_classification()
    {
        using var input = new FeedableReadStream();
        using var output = new FailingSecondWriteStream();
        var lifetime = new RecordingLifetime(
            execute: (_, _) => Task.FromResult<WorkerExecutionResult>(
                new WorkerInvokeExecutionResult(
                    WorkerResultStatus.Completed,
                    "done")));
        input.Enqueue(Frame(Initialize(1, 1, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));
        var run = server.RunAsync();
        await output.FirstFrameWritten.WaitAsync(TimeSpan.FromSeconds(10));

        input.Enqueue(Frame(WorkerOperationProtocol.CreateInvokeEnvelope(
            SessionId,
            incarnation: 1,
            requestId: 2,
            "'done'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 0,
            artifact: null,
            Limits)));
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(
                WorkerServerExitKind.TransportFailure,
                "event_transport_failure"),
            exit);
        Assert.Equal(2, output.WriteCalls);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Invalid_executor_terminal_is_a_worker_runtime_failure()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var lifetime = new RecordingLifetime(
            execute: (_, _) => Task.FromResult<WorkerExecutionResult>(
                new WorkerInvokeExecutionResult(
                    WorkerResultStatus.Completed,
                    "done",
                    DetailCode: "must_be_null")));
        input.Enqueue(Frame(Initialize(1, 1, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));
        var run = server.RunAsync();
        await output.WaitForWritesAsync(1);

        input.Enqueue(Frame(WorkerOperationProtocol.CreateInvokeEnvelope(
            SessionId,
            incarnation: 1,
            requestId: 2,
            "'done'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 0,
            artifact: null,
            Limits)));
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(
                WorkerServerExitKind.RuntimeFailure,
                "outbound_protocol_failure"),
            exit);
        Assert.Collection(
            await output.FramesAsync(),
            frame => Assert.Equal(WorkerMessageKind.Ready, frame.Kind));
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Duplicate_initialize_after_ready_is_protocol_fatal_and_drains_once()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var lifetime = new RecordingLifetime();
        input.Enqueue(Frame(Initialize(1, 1, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));
        var run = server.RunAsync();
        await output.WaitForWritesAsync(1);

        input.Enqueue(Frame(Initialize(2, 1, Now.AddMinutes(1))));
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(WorkerServerExitKind.ProtocolError, "unsupported_message"),
            exit);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Collection(
            await output.FramesAsync(),
            frame => Assert.Equal(WorkerMessageKind.Ready, frame.Kind));
    }

    [Fact]
    public async Task Unsolicited_terminal_after_ready_is_protocol_fatal()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var lifetime = new RecordingLifetime();
        input.Enqueue(Frame(Initialize(1, 1, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));
        var run = server.RunAsync();
        await output.WaitForWritesAsync(1);

        input.Enqueue(Frame(WorkerOperationProtocol.CreateResultEnvelope(
            SessionId,
            incarnation: 1,
            new WorkerResult(
                RequestId: 2,
                WorkerResultStatus.Completed,
                "unsolicited",
                DetailCode: null))));
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(
                WorkerServerExitKind.ProtocolError,
                "unsupported_message"),
            exit);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Collection(
            await output.FramesAsync(),
            frame => Assert.Equal(WorkerMessageKind.Ready, frame.Kind));
    }

    [Fact]
    public async Task Invoke_and_state_frames_dispatch_after_ready()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var lifetime = new RecordingLifetime(
            execute: (request, _) => Task.FromResult<WorkerExecutionResult>(
                request switch
                {
                    WorkerInvokeRequest => new WorkerInvokeExecutionResult(
                        WorkerResultStatus.Completed,
                        "invoke-result"),
                    WorkerStateQueryRequest => new WorkerStateExecutionResult(
                        Available: false,
                        "runspace: busy",
                        "runspace_busy"),
                    _ => throw new InvalidOperationException(),
                }));
        input.Enqueue(Frame(Initialize(1, 1, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));
        var run = server.RunAsync();
        await output.WaitForWritesAsync(1);
        input.Enqueue(Frame(WorkerOperationProtocol.CreateInvokeEnvelope(
            SessionId,
            1,
            2,
            "'invoke'",
            false,
            WorkerInvokeRoute.Pwsh,
            0,
            null,
            Limits)));
        input.Enqueue(Frame(WorkerOperationProtocol.CreateStateQueryEnvelope(
            SessionId,
            1,
            3,
            false)));
        await output.WaitForWritesAsync(2);
        input.Enqueue(Frame(Envelope(
            WorkerMessageKind.Shutdown,
            4,
            EmptyPayload(),
            incarnation: 1)));
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new WorkerServerExit(WorkerServerExitKind.Shutdown, "shutdown"), exit);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
        var frames = await output.FramesAsync();
        Assert.Equal(4, frames.Count);
        Assert.Equal(WorkerMessageKind.Ready, frames[0].Kind);
        Assert.Equal(WorkerMessageKind.Stopped, frames[^1].Kind);
        Assert.Contains(
            frames[1..^1],
            frame => frame.Kind == WorkerMessageKind.Result &&
                frame.RequestId == 2);
        Assert.Contains(
            frames[1..^1],
            frame => frame.Kind == WorkerMessageKind.StateSnapshot &&
                frame.RequestId == 3);
    }

    [Fact]
    public async Task Eof_before_initialize_never_constructs_a_runtime()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        input.Complete();
        var factoryCalls = 0;
        var server = Server(
            input,
            output,
            (_, _) =>
            {
                factoryCalls++;
                throw new InvalidOperationException("factory must not run");
            });

        var exit = await server.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new WorkerServerExit(WorkerServerExitKind.Eof, "eof_before_initialize"), exit);
        Assert.Equal(0, factoryCalls);
        Assert.Empty(await output.FramesAsync());
    }

    [Fact]
    public async Task Eof_after_ready_awaits_shutdown_then_disposes_without_terminal_response()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var shutdownEntered = NewSignal();
        var releaseShutdown = NewSignal();
        var lifetime = new RecordingLifetime(async () =>
        {
            shutdownEntered.TrySetResult();
            await releaseShutdown.Task;
        });
        input.Enqueue(Frame(Initialize(1, 3, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));
        Task<WorkerServerExit>? run = null;
        try
        {
            run = server.RunAsync();
            await output.WaitForWritesAsync(1);
            input.Complete();
            await shutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted, "worker EOF cleanup did not await lifetime shutdown");
            Assert.Single(await output.FramesAsync());

            releaseShutdown.TrySetResult();
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new WorkerServerExit(WorkerServerExitKind.Eof, "eof_after_ready"), exit);
        }
        finally
        {
            releaseShutdown.TrySetResult();
            input.Complete();
            if (run is not null)
            {
                try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { /* preserve primary failure */ }
            }
        }

        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Single(await output.FramesAsync());
    }

    [Fact]
    public async Task Eof_during_blocked_initialize_cancels_and_cleans_a_late_runtime()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var factoryEntered = NewSignal();
        var factoryCanceled = NewSignal();
        var releaseFactory = new TaskCompletionSource<IWorkerSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new RecordingLifetime();
        input.Enqueue(Frame(Initialize(1, 5, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            async (_, cancellationToken) =>
            {
                using var registration = cancellationToken.Register(
                    () => factoryCanceled.TrySetResult());
                factoryEntered.TrySetResult();
                return await releaseFactory.Task;
            });

        var run = server.RunAsync();
        try
        {
            await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            input.Complete();
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(
                new WorkerServerExit(WorkerServerExitKind.Eof, "eof_during_initialize"),
                exit);
            await factoryCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(await output.FramesAsync());

            releaseFactory.TrySetResult(lifetime);
            await lifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            input.Complete();
            releaseFactory.TrySetResult(lifetime);
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
            try { await lifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
        }
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Synchronously_blocking_factory_delegate_cannot_hide_eof()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        using var releaseFactory = new ManualResetEventSlim();
        using var cleanupCancellation = new CancellationTokenSource();
        var factoryEntered = NewSignal();
        var factoryCanceled = NewSignal();
        var lifetime = new RecordingLifetime();
        input.Enqueue(Frame(Initialize(1, 5, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, cancellationToken) =>
            {
                using var registration = cancellationToken.Register(
                    () => factoryCanceled.TrySetResult());
                factoryEntered.TrySetResult();
                releaseFactory.Wait();
                return Task.FromResult<IWorkerSession>(lifetime);
            });

        Task<Task<WorkerServerExit>>? start = null;
        Task<WorkerServerExit>? run = null;
        try
        {
            start = Task.Factory.StartNew(
                () => server.RunAsync(cleanupCancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            run = await start.WaitAsync(TimeSpan.FromSeconds(10));

            input.Complete();
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(
                new WorkerServerExit(WorkerServerExitKind.Eof, "eof_during_initialize"),
                exit);
            await factoryCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(await output.FramesAsync());

            releaseFactory.Set();
            await lifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            input.Complete();
            releaseFactory.Set();
            cleanupCancellation.Cancel();
            if (start is not null)
            {
                try { run ??= await start.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { /* preserve primary failure */ }
            }
            if (run is not null)
            {
                try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { /* preserve primary failure */ }
            }
            try { await lifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
        }

        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Active_initialize_deadline_cancels_and_cleans_a_late_runtime()
    {
        using var input = new FeedableReadStream();
        using var output = new BlockingFirstWriteStream();
        var deadline = Now.AddMinutes(1);
        var deadlineReached = 0;
        DateTimeOffset? observedDeadline = null;
        var deadlineWaiterEntered = NewSignal();
        var releaseDeadline = NewSignal();
        var factoryEntered = NewSignal();
        var factoryCanceled = NewSignal();
        var releaseFactory = new TaskCompletionSource<IWorkerSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new RecordingLifetime();
        input.Enqueue(Frame(Initialize(17, 5, deadline)));
        var server = Server(
            input,
            output,
            async (_, cancellationToken) =>
            {
                using var registration = cancellationToken.Register(
                    () => factoryCanceled.TrySetResult());
                factoryEntered.TrySetResult();
                return await releaseFactory.Task;
            },
            () => Volatile.Read(ref deadlineReached) == 0 ? Now : deadline,
            async (value, cancellationToken) =>
            {
                observedDeadline = value;
                deadlineWaiterEntered.TrySetResult();
                await releaseDeadline.Task.WaitAsync(cancellationToken);
            });

        var run = server.RunAsync();
        try
        {
            await Task.WhenAll(factoryEntered.Task, deadlineWaiterEntered.Task)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Volatile.Write(ref deadlineReached, 1);
            releaseDeadline.TrySetResult();
            await output.BlockedWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await factoryCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(deadline, observedDeadline);
            Assert.False(run.IsCompleted, "deadline response write was not held by the test gate");
            output.ReleaseBlockedWrite.TrySetResult();

            var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(
                new WorkerServerExit(
                    WorkerServerExitKind.InitializeFailed,
                    "initialize_deadline_expired"),
                exit);
            var frames = await output.FramesAsync();
            Assert.Single(frames);
            AssertProtocolIdentity(frames);
            Assert.Equal(17, frames[0].RequestId);
            Assert.Equal("failed", frames[0].Payload.GetProperty("status").GetString());
            Assert.DoesNotContain(
                frames,
                frame => frame.Kind == WorkerMessageKind.Ready);

            releaseFactory.TrySetResult(lifetime);
            await lifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            input.Complete();
            releaseDeadline.TrySetResult();
            output.ReleaseBlockedWrite.TrySetResult();
            releaseFactory.TrySetResult(lifetime);
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
            try { await lifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
        }

        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Factory_queued_past_deadline_never_invokes_runtime_construction()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var scheduler = new PausedTaskScheduler();
        var deadline = Now.AddMinutes(1);
        var deadlineReached = 0;
        var deadlineWaiterEntered = NewSignal();
        var releaseDeadline = NewSignal();
        var factoryCalls = 0;
        var lifetime = new RecordingLifetime();
        input.Enqueue(Frame(Initialize(23, 6, deadline)));
        var server = Server(
            input,
            output,
            (_, _) =>
            {
                Interlocked.Increment(ref factoryCalls);
                return Task.FromResult<IWorkerSession>(lifetime);
            },
            () => Volatile.Read(ref deadlineReached) == 0 ? Now : deadline,
            async (_, cancellationToken) =>
            {
                deadlineWaiterEntered.TrySetResult();
                await releaseDeadline.Task.WaitAsync(cancellationToken);
                scheduler.Release();
                await scheduler.ExecutionCompleted.Task.WaitAsync(cancellationToken);
            },
            scheduler);

        var run = server.RunAsync();
        try
        {
            await Task.WhenAll(scheduler.TaskQueued.Task, deadlineWaiterEntered.Task)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Volatile.Write(ref deadlineReached, 1);
            releaseDeadline.TrySetResult();

            var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(
                new WorkerServerExit(
                    WorkerServerExitKind.InitializeFailed,
                    "initialize_deadline_expired"),
                exit);
            Assert.Equal(0, Volatile.Read(ref factoryCalls));
            Assert.Equal(0, lifetime.ShutdownCount);
            Assert.Equal(0, lifetime.DisposeCount);
            var frames = await output.FramesAsync();
            Assert.Single(frames);
            Assert.DoesNotContain(frames, frame => frame.Kind == WorkerMessageKind.Ready);
        }
        finally
        {
            Volatile.Write(ref deadlineReached, 1);
            releaseDeadline.TrySetResult();
            scheduler.Release();
            input.Complete();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
        }
    }

    [Fact]
    public async Task Host_cancellation_after_ready_awaits_lifetime_drain()
    {
        using var input = new FeedableReadStream(ignoreCancellation: true);
        using var output = new CapturingWriteStream();
        using var cancellation = new CancellationTokenSource();
        var shutdownEntered = NewSignal();
        var releaseShutdown = NewSignal();
        var lifetime = new RecordingLifetime(async () =>
        {
            shutdownEntered.TrySetResult();
            await releaseShutdown.Task;
        });
        input.Enqueue(Frame(Initialize(1, 3, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));
        var run = server.RunAsync(cancellation.Token);
        try
        {
            await output.WaitForWritesAsync(1);
            cancellation.Cancel();
            await shutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted, "host cancellation did not await lifetime shutdown");
            Assert.Single(await output.FramesAsync());

            releaseShutdown.TrySetResult();
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new WorkerServerExit(WorkerServerExitKind.Canceled, "canceled"), exit);
        }
        finally
        {
            cancellation.Cancel();
            input.Complete();
            releaseShutdown.TrySetResult();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
        }

        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Buffered_eof_after_initialize_never_constructs_or_publishes_ready()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var lifetime = new RecordingLifetime();
        input.Enqueue(Frame(Initialize(1, 5, Now.AddMinutes(1))));
        input.Complete();
        var factoryCalls = 0;
        var server = Server(
            input,
            output,
            (_, _) =>
            {
                factoryCalls++;
                return Task.FromResult<IWorkerSession>(lifetime);
            });

        var exit = await server.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(WorkerServerExitKind.Eof, "eof_during_initialize"),
            exit);
        Assert.Empty(await output.FramesAsync());
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, lifetime.ShutdownCount);
        Assert.Equal(0, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Shutdown_before_ready_is_protocol_fatal_and_cleans_late_runtime()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var factoryEntered = NewSignal();
        var releaseFactory = new TaskCompletionSource<IWorkerSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new RecordingLifetime();
        input.Enqueue(Frame(Initialize(1, 5, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            async (_, _) =>
            {
                factoryEntered.TrySetResult();
                return await releaseFactory.Task;
            });

        var run = server.RunAsync();
        try
        {
            await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            input.Enqueue(Frame(Envelope(
                WorkerMessageKind.Shutdown,
                2,
                EmptyPayload(),
                incarnation: 5)));
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(
                new WorkerServerExit(WorkerServerExitKind.ProtocolError, "message_before_ready"),
                exit);
            Assert.Empty(await output.FramesAsync());

            releaseFactory.TrySetResult(lifetime);
            await lifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            input.Complete();
            releaseFactory.TrySetResult(lifetime);
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
            try { await lifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
        }

        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Throwing_factory_cancellation_callback_cannot_prevent_late_cleanup()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var factoryEntered = NewSignal();
        var cancellationCallback = NewSignal();
        var callbackCount = 0;
        var releaseFactory = new TaskCompletionSource<IWorkerSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new RecordingLifetime();
        input.Enqueue(Frame(Initialize(1, 5, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            async (_, cancellationToken) =>
            {
                using var registration = cancellationToken.Register(() =>
                {
                    Interlocked.Increment(ref callbackCount);
                    cancellationCallback.TrySetResult();
                    throw new IOException("injected cancellation callback failure");
                });
                factoryEntered.TrySetResult();
                return await releaseFactory.Task;
            });

        var run = server.RunAsync();
        try
        {
            await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            input.Complete();
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(
                new WorkerServerExit(WorkerServerExitKind.Eof, "eof_during_initialize"),
                exit);
            await cancellationCallback.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, Volatile.Read(ref callbackCount));

            releaseFactory.TrySetResult(lifetime);
            await lifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            input.Complete();
            releaseFactory.TrySetResult(lifetime);
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
            try { await lifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
        }

        Assert.Equal(1, Volatile.Read(ref callbackCount));
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Host_cancel_during_initialize_isolated_from_throwing_factory_callback()
    {
        using var input = new FeedableReadStream(ignoreCancellation: true);
        using var output = new CapturingWriteStream();
        using var cancellation = new CancellationTokenSource();
        var factoryEntered = NewSignal();
        var cancellationCallback = NewSignal();
        var callbackCount = 0;
        var releaseFactory = new TaskCompletionSource<IWorkerSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new RecordingLifetime();
        input.Enqueue(Frame(Initialize(1, 5, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            async (_, factoryCancellation) =>
            {
                using var registration = factoryCancellation.Register(() =>
                {
                    Interlocked.Increment(ref callbackCount);
                    cancellationCallback.TrySetResult();
                    throw new IOException("injected cancellation callback failure");
                });
                factoryEntered.TrySetResult();
                return await releaseFactory.Task;
            });

        var run = server.RunAsync(cancellation.Token);
        try
        {
            await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var cancellationFailure = Record.Exception(cancellation.Cancel);
            Assert.Null(cancellationFailure);
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new WorkerServerExit(WorkerServerExitKind.Canceled, "canceled"), exit);
            await cancellationCallback.Task.WaitAsync(TimeSpan.FromSeconds(10));

            releaseFactory.TrySetResult(lifetime);
            await lifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            cancellation.Cancel();
            input.Complete();
            releaseFactory.TrySetResult(lifetime);
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
            try { await lifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
        }

        Assert.Equal(1, Volatile.Read(ref callbackCount));
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Eof_cleanup_failure_is_runtime_failure_not_transport_failure()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var lifetime = new RecordingLifetime(
            () => Task.FromException(new IOException("secret cleanup failure")));
        input.Enqueue(Frame(Initialize(1, 3, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));
        var run = server.RunAsync();
        await output.WaitForWritesAsync(1);

        input.Complete();
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(WorkerServerExitKind.RuntimeFailure, "cleanup_failed"),
            exit);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Single(await output.FramesAsync());
        Assert.DoesNotContain(
            "secret cleanup failure",
            Encoding.UTF8.GetString(output.Snapshot()),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protocol_failure_preserves_its_detail_when_runtime_cleanup_fails()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var lifetime = new RecordingLifetime(
            () => Task.FromException(new IOException("secret cleanup failure")));
        input.Enqueue(Frame(Initialize(1, 3, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));
        var run = server.RunAsync();
        await output.WaitForWritesAsync(1);

        input.Enqueue(Frame(Initialize(2, 3, Now.AddMinutes(1))));
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(WorkerServerExitKind.ProtocolError, "unsupported_message"),
            exit);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Single(await output.FramesAsync());
    }

    [Fact]
    public async Task Shutdown_failure_never_emits_false_stopped_and_still_disposes()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var lifetime = new RecordingLifetime(
            () => Task.FromException(new IOException("injected shutdown failure")));
        input.Enqueue(Frame(Initialize(1, 2, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));

        var run = server.RunAsync();
        await output.WaitForWritesAsync(1);
        input.Enqueue(Frame(Envelope(
            WorkerMessageKind.Shutdown,
            2,
            EmptyPayload(),
            incarnation: 2)));
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(WorkerServerExitKind.RuntimeFailure, "shutdown_failed"),
            exit);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
        var frames = await output.FramesAsync();
        Assert.Equal(2, frames.Count);
        Assert.Equal(WorkerMessageKind.Ready, frames[0].Kind);
        var failure = WorkerOperationProtocol.ParseResult(
            frames[1],
            SessionId,
            expectedIncarnation: 2);
        Assert.Equal(WorkerResultStatus.Failed, failure.Status);
        Assert.Equal("shutdown_failed", failure.DetailCode);
        Assert.DoesNotContain(
            frames,
            frame => frame.Payload.TryGetProperty("status", out var status) &&
                status.GetString() == "stopped");
    }

    [Fact]
    public async Task Dispose_failure_never_emits_false_stopped()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var lifetime = new RecordingLifetime(
            dispose: () => throw new IOException("injected dispose failure"));
        input.Enqueue(Frame(Initialize(1, 2, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));

        var run = server.RunAsync();
        await output.WaitForWritesAsync(1);
        input.Enqueue(Frame(Envelope(
            WorkerMessageKind.Shutdown,
            2,
            EmptyPayload(),
            incarnation: 2)));
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(WorkerServerExitKind.RuntimeFailure, "shutdown_failed"),
            exit);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
        var frames = await output.FramesAsync();
        Assert.Equal("failed", frames[^1].Payload.GetProperty("status").GetString());
        Assert.DoesNotContain(
            frames,
            frame => frame.Payload.TryGetProperty("status", out var status) &&
                status.GetString() == "stopped");
    }

    [Fact]
    public async Task Shutdown_requires_request_id_and_empty_payload_after_ready()
    {
        var cases = new[]
        {
            (
                Envelope(
                    WorkerMessageKind.Shutdown,
                    requestId: null,
                    EmptyPayload(),
                    incarnation: 2),
                DetailCode: "request_id_required"),
            (
                Envelope(
                    WorkerMessageKind.Shutdown,
                    requestId: 2,
                    JsonSerializer.SerializeToElement(new { unexpected = true }),
                    incarnation: 2),
                DetailCode: "invalid_payload"),
        };

        foreach (var testCase in cases)
        {
            using var input = new FeedableReadStream();
            using var output = new CapturingWriteStream();
            var lifetime = new RecordingLifetime();
            input.Enqueue(Frame(Initialize(1, 2, Now.AddMinutes(1))));
            var server = Server(
                input,
                output,
                (_, _) => Task.FromResult<IWorkerSession>(lifetime));
            var run = server.RunAsync();
            await output.WaitForWritesAsync(1);

            input.Enqueue(Frame(testCase.Item1));
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(
                new WorkerServerExit(WorkerServerExitKind.ProtocolError, testCase.DetailCode),
                exit);
            Assert.Equal(1, lifetime.ShutdownCount);
            Assert.Equal(1, lifetime.DisposeCount);
            Assert.DoesNotContain(
                await output.FramesAsync(),
                frame => frame.Payload.TryGetProperty("status", out var status) &&
                    status.GetString() == "stopped");
        }
    }

    [Fact]
    public async Task Shutdown_request_id_cannot_reuse_an_operation_correlation()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var lifetime = new RecordingLifetime(
            execute: (_, _) => Task.FromResult<WorkerExecutionResult>(
                new WorkerInvokeExecutionResult(
                    WorkerResultStatus.Completed,
                    "done")));
        input.Enqueue(Frame(Initialize(1, 2, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));
        var run = server.RunAsync();
        await output.WaitForWritesAsync(1);

        input.Enqueue(Frame(WorkerOperationProtocol.CreateInvokeEnvelope(
            SessionId,
            incarnation: 2,
            requestId: 2,
            "'done'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 0,
            artifact: null,
            Limits)));
        input.Enqueue(Frame(WorkerOperationProtocol.CreateEmptyEnvelope(
            WorkerMessageKind.Shutdown,
            SessionId,
            incarnation: 2,
            requestId: 2)));

        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(
                WorkerServerExitKind.ProtocolError,
                "operation_request_replay"),
            exit);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Collection(
            await output.FramesAsync(),
            frame => Assert.Equal(WorkerMessageKind.Ready, frame.Kind),
            frame => Assert.Equal(WorkerMessageKind.Result, frame.Kind));
    }

    [Fact]
    public async Task Expired_initialize_is_correlated_and_never_enters_factory()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        input.Enqueue(Frame(Initialize(91, 11, Now)));
        input.Complete();
        var factoryCalls = 0;
        var server = Server(
            input,
            output,
            (_, _) =>
            {
                factoryCalls++;
                throw new InvalidOperationException("factory must not run");
            });

        var exit = await server.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(
                WorkerServerExitKind.InitializeFailed,
                "initialize_deadline_expired"),
            exit);
        Assert.Equal(0, factoryCalls);
        var frames = await output.FramesAsync();
        Assert.Single(frames);
        Assert.Equal(91, frames[0].RequestId);
        Assert.Equal("failed", frames[0].Payload.GetProperty("status").GetString());
        Assert.Equal(
            "initialize_deadline_expired",
            frames[0].Payload.GetProperty("detailCode").GetString());
    }

    [Fact]
    public async Task Runtime_returned_at_the_deadline_is_drained_without_ready()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        var deadline = Now.AddMinutes(1);
        var deadlineReached = 0;
        var shutdownEntered = NewSignal();
        var releaseShutdown = NewSignal();
        var lifetime = new RecordingLifetime(async () =>
        {
            shutdownEntered.TrySetResult();
            await releaseShutdown.Task;
        });
        input.Enqueue(Frame(Initialize(71, 9, deadline)));
        var server = Server(
            input,
            output,
            (_, _) =>
            {
                Volatile.Write(ref deadlineReached, 1);
                return Task.FromResult<IWorkerSession>(lifetime);
            },
            () => Volatile.Read(ref deadlineReached) == 0 ? Now : deadline);

        var run = server.RunAsync();
        WorkerServerExit exit;
        try
        {
            await shutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted, "deadline returned before draining a completed factory");
            releaseShutdown.TrySetResult();
            exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseShutdown.TrySetResult();
            input.Complete();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* preserve primary failure */ }
        }

        Assert.Equal(
            new WorkerServerExit(
                WorkerServerExitKind.InitializeFailed,
                "initialize_deadline_expired"),
            exit);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);
        var frames = await output.FramesAsync();
        Assert.Single(frames);
        Assert.Equal("failed", frames[0].Payload.GetProperty("status").GetString());
        Assert.DoesNotContain(frames, frame => frame.Kind == WorkerMessageKind.Ready);
    }

    [Fact]
    public async Task Initialize_requires_a_correlating_request_id()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        input.Enqueue(Frame(Envelope(
            WorkerMessageKind.Initialize,
            requestId: null,
            Initialize(1, 1, Now.AddMinutes(1)).Payload,
            incarnation: 1)));
        input.Complete();
        var factoryCalls = 0;
        var exit = await Server(
            input,
            output,
            (_, _) =>
            {
                factoryCalls++;
                throw new InvalidOperationException("factory must not run");
            }).RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(WorkerServerExitKind.ProtocolError, "request_id_required"),
            exit);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task Worker_server_instance_runs_only_once()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        input.Complete();
        var server = Server(
            input,
            output,
            (_, _) => throw new InvalidOperationException("factory must not run"));
        _ = await server.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.RunAsync());
    }

    [Fact]
    public async Task Factory_failure_is_generic_and_never_leaks_exception_text()
    {
        using var input = new FeedableReadStream();
        using var output = new CapturingWriteStream();
        input.Enqueue(Frame(Initialize(1, 1, Now.AddMinutes(1))));
        var server = Server(
            input,
            output,
            (_, _) => throw new InvalidOperationException("secret factory detail"));

        var exit = await server.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new WorkerServerExit(WorkerServerExitKind.InitializeFailed, "initialize_failed"),
            exit);
        var text = Encoding.UTF8.GetString(output.Snapshot());
        Assert.DoesNotContain("secret factory detail", text, StringComparison.Ordinal);
        var frames = await output.FramesAsync();
        Assert.Equal("initialize_failed", frames[0].Payload.GetProperty("detailCode").GetString());
    }

    [Fact]
    public async Task Misrouted_session_and_truncated_frames_are_stable_protocol_failures()
    {
        using var mismatchedInput = new FeedableReadStream();
        using var mismatchedOutput = new CapturingWriteStream();
        var lifetime = new RecordingLifetime();
        mismatchedInput.Enqueue(Frame(Initialize(1, 1, Now.AddMinutes(1))));
        var mismatchedServer = Server(
            mismatchedInput,
            mismatchedOutput,
            (_, _) => Task.FromResult<IWorkerSession>(lifetime));
        var mismatchRun = mismatchedServer.RunAsync();
        await mismatchedOutput.WaitForWritesAsync(1);
        mismatchedInput.Enqueue(Frame(
            WorkerOperationProtocol.CreateStateQueryEnvelope(
                Guid.Parse("2f609093-486a-46d9-904f-20e68ea34b72"),
                1,
                2,
                false)));
        var mismatch = await mismatchRun.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(
            new WorkerServerExit(
                WorkerServerExitKind.ProtocolError,
                "session_identity_mismatch"),
            mismatch);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(1, lifetime.DisposeCount);

        using var truncatedInput = new FeedableReadStream();
        using var truncatedOutput = new CapturingWriteStream();
        truncatedInput.Enqueue(Encoding.UTF8.GetBytes("{\"protocolVersion\":2"));
        truncatedInput.Complete();
        var truncated = await Server(
            truncatedInput,
            truncatedOutput,
            (_, _) => throw new InvalidOperationException("factory must not run"))
            .RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(
            new WorkerServerExit(WorkerServerExitKind.ProtocolError, "truncated_frame"),
            truncated);
    }

    [Fact]
    public async Task Initialize_payload_rejects_missing_unknown_and_nonpositive_fields()
    {
        var payloads = new[]
        {
            JsonSerializer.SerializeToElement(new
            {
                deadlineUnixTimeMilliseconds = Now.AddMinutes(1).ToUnixTimeMilliseconds(),
            }),
            JsonSerializer.SerializeToElement(new
            {
                deadlineUnixTimeMilliseconds = Now.AddMinutes(1).ToUnixTimeMilliseconds(),
                maximumScriptBytes = Limits.MaximumScriptBytes,
                maximumArtifactBytes = Limits.MaximumArtifactBytes,
                maximumArtifactChunkBytes = Limits.MaximumArtifactChunkBytes,
                defaultTimeoutSeconds = Limits.DefaultTimeoutSeconds,
                maximumTimeoutSeconds = Limits.MaximumTimeoutSeconds,
                extra = true,
            }),
            JsonSerializer.SerializeToElement(new
            {
                deadlineUnixTimeMilliseconds = Now.AddMinutes(1).ToUnixTimeMilliseconds(),
                maximumScriptBytes = 0,
                maximumArtifactBytes = Limits.MaximumArtifactBytes,
                maximumArtifactChunkBytes = Limits.MaximumArtifactChunkBytes,
                defaultTimeoutSeconds = Limits.DefaultTimeoutSeconds,
                maximumTimeoutSeconds = Limits.MaximumTimeoutSeconds,
            }),
        };

        foreach (var payload in payloads)
        {
            using var input = new FeedableReadStream();
            using var output = new CapturingWriteStream();
            input.Enqueue(Frame(Envelope(
                WorkerMessageKind.Initialize,
                1,
                payload,
                incarnation: 1)));
            input.Complete();
            var factoryCalls = 0;
            var exit = await Server(
                input,
                output,
                (_, _) =>
                {
                    factoryCalls++;
                    throw new InvalidOperationException("factory must not run");
                }).RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(WorkerServerExitKind.ProtocolError, exit.Kind);
            Assert.Equal(0, factoryCalls);
        }
    }

    [Fact]
    public void Worker_server_surface_has_no_supervisor_operation_audit_or_output_capability()
    {
        var surface = typeof(WorkerServer)
            .GetMembers(BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(MemberTypes)
            .SelectMany(Flatten)
            .ToHashSet();

        Assert.DoesNotContain(typeof(ISessionOperations), surface);
        Assert.DoesNotContain(typeof(AuditCallContextAccessor), surface);
        Assert.DoesNotContain(typeof(OutputStore), surface);

        static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
        {
            FieldInfo field => [field.FieldType],
            PropertyInfo property => [property.PropertyType],
            MethodInfo method =>
                [method.ReturnType, .. method.GetParameters().Select(parameter => parameter.ParameterType)],
            ConstructorInfo constructor =>
                constructor.GetParameters().Select(parameter => parameter.ParameterType),
            _ => [],
        };

        static IEnumerable<Type> Flatten(Type type)
        {
            yield return type;
            if (type.HasElementType && type.GetElementType() is { } element)
            {
                foreach (var nested in Flatten(element)) yield return nested;
            }
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var nested in Flatten(argument)) yield return nested;
            }
        }
    }

    private static WorkerServer Server(
        Stream input,
        Stream output,
        Func<WorkerInitializeRequest, CancellationToken, Task<IWorkerSession>> factory,
        Func<DateTimeOffset>? utcNow = null,
        Func<DateTimeOffset, CancellationToken, Task>? waitUntilDeadline = null,
        TaskScheduler? factoryScheduler = null)
        => new(
            input,
            output,
            factory,
            utcNow ?? (() => Now),
            waitUntilDeadline,
            factoryScheduler);

    private static WorkerEnvelope Initialize(
        long requestId,
        long generation,
        DateTimeOffset deadline)
        => WorkerOperationProtocol.CreateInitializeEnvelope(
            SessionId,
            generation,
            requestId,
            deadline,
            Limits);

    private static WorkerEnvelope Envelope(
        WorkerMessageKind kind,
        long? requestId,
        JsonElement payload,
        long incarnation = 1)
        => new(
            WorkerProtocol.Version,
            kind,
            SessionId,
            incarnation,
            requestId,
            payload);

    private static JsonElement EmptyPayload() =>
        JsonSerializer.SerializeToElement(new { });

    private static byte[] Frame(WorkerEnvelope envelope)
    {
        var encoded = WorkerProtocol.Encode(envelope);
        var frame = new byte[encoded.Length + 1];
        encoded.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';
        return frame;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertProtocolIdentity(IEnumerable<WorkerEnvelope> frames)
    {
        Assert.All(frames, frame =>
        {
            Assert.Equal(WorkerProtocol.Version, frame.ProtocolVersion);
            Assert.Equal(SessionId, frame.SessionId);
            Assert.True(frame.Incarnation > 0);
        });
    }

    private sealed class PausedTaskScheduler : TaskScheduler
    {
        private readonly object _gate = new();
        private Task? _scheduledTask;
        private int _released;
        private int _executionQueued;

        internal TaskCompletionSource TaskQueued { get; } = NewSignal();
        internal TaskCompletionSource ExecutionCompleted { get; } = NewSignal();

        internal void Release()
        {
            Volatile.Write(ref _released, 1);
            Task? scheduledTask;
            lock (_gate) scheduledTask = _scheduledTask;
            if (scheduledTask is not null) QueueExecution(scheduledTask);
        }

        protected override void QueueTask(Task task)
        {
            lock (_gate)
            {
                if (_scheduledTask is not null)
                    throw new InvalidOperationException("Only one factory task is expected.");
                _scheduledTask = task;
            }
            TaskQueued.TrySetResult();
            if (Volatile.Read(ref _released) != 0) QueueExecution(task);
        }

        protected override bool TryExecuteTaskInline(
            Task task,
            bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task>? GetScheduledTasks()
        {
            lock (_gate)
            {
                return _scheduledTask is null ? [] : [_scheduledTask];
            }
        }

        private void QueueExecution(Task task)
        {
            if (Interlocked.Exchange(ref _executionQueued, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _ = TryExecuteTask(task);
                }
                finally
                {
                    ExecutionCompleted.TrySetResult();
                }
            });
        }
    }

    private sealed class RecordingLifetime(
        Func<Task>? shutdown = null,
        Action? dispose = null,
        Func<WorkerOperationRequest, CancellationToken, Task<WorkerExecutionResult>>? execute = null) :
        IWorkerSession
    {
        private readonly Func<Task> _shutdown = shutdown ?? (() => Task.CompletedTask);
        private readonly Action _dispose = dispose ?? (() => { });

        internal int ShutdownCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal TaskCompletionSource Disposed { get; } = NewSignal();

        public async Task ShutdownAsync()
        {
            ShutdownCount++;
            await _shutdown();
        }

        public void Dispose()
        {
            DisposeCount++;
            Disposed.TrySetResult();
            _dispose();
        }

        public Task<WorkerExecutionResult> ExecuteAsync(
            WorkerOperationRequest request,
            CancellationToken cancellationToken) =>
            execute?.Invoke(request, cancellationToken) ??
            Task.FromException<WorkerExecutionResult>(
                new NotSupportedException("No operation executor was configured."));
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("injected request transport failure"));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("injected event transport failure"));

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class FailingSecondWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly TaskCompletionSource _firstFrameWritten = NewSignal();
        private int _writeCalls;

        internal Task FirstFrameWritten => _firstFrameWritten.Task;
        internal int WriteCalls => Volatile.Read(ref _writeCalls);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _writeCalls);
            if (call == 2)
                throw new IOException("injected operation response failure");
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await _inner.FlushAsync(cancellationToken);
            if (WriteCalls == 1) _firstFrameWritten.TrySetResult();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class FeedableReadStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly bool _ignoreCancellation;
        private byte[]? _current;
        private int _offset;

        internal FeedableReadStream(bool ignoreCancellation = false)
        {
            _ignoreCancellation = ignoreCancellation;
        }

        internal void Enqueue(byte[] bytes)
        {
            if (!_chunks.Writer.TryWrite(bytes))
                throw new InvalidOperationException("The input stream is already complete.");
        }

        internal void Complete() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset == _current.Length)
            {
                var effectiveCancellation = _ignoreCancellation
                    ? CancellationToken.None
                    : cancellationToken;
                if (!await _chunks.Reader.WaitToReadAsync(effectiveCancellation)) return 0;
                if (!_chunks.Reader.TryRead(out _current)) continue;
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            Complete();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingFirstWriteStream : Stream
    {
        private readonly CapturingWriteStream _inner = new();
        private int _writeCount;

        internal TaskCompletionSource BlockedWriteEntered { get; } = NewSignal();
        internal TaskCompletionSource ReleaseBlockedWrite { get; } = NewSignal();

        internal Task<List<WorkerEnvelope>> FramesAsync() => _inner.FramesAsync();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) == 1)
            {
                BlockedWriteEntered.TrySetResult();
                await ReleaseBlockedWrite.Task.WaitAsync(cancellationToken);
            }
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingWriteStream : Stream
    {
        private readonly object _gate = new();
        private readonly MemoryStream _bytes = new();
        private readonly SemaphoreSlim _writes = new(0);

        internal async Task WaitForWritesAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(
                    await _writes.WaitAsync(TimeSpan.FromSeconds(10)),
                    $"Timed out waiting for worker frame {i + 1} of {count}.");
            }
        }

        internal byte[] Snapshot()
        {
            lock (_gate) return _bytes.ToArray();
        }

        internal async Task<List<WorkerEnvelope>> FramesAsync()
        {
            using var stream = new MemoryStream(Snapshot(), writable: false);
            var reader = new WorkerProtocolReader(stream);
            var frames = new List<WorkerEnvelope>();
            while (await reader.ReadAsync() is { } frame) frames.Add(frame);
            return frames;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _bytes.Write(buffer.Span);
            _writes.Release();
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _bytes.Dispose();
                _writes.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush()
        {
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate) _bytes.Write(buffer, offset, count);
            _writes.Release();
        }
    }
}
