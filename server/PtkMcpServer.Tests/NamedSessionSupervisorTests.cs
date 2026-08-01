using System.Collections.Concurrent;
using PtkMcpServer.Sessions;
using PtkMcpServer.Tools;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class NamedSessionSupervisorTests
{
    private static readonly TimeSpan CheckpointTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Default_is_cold_and_nondefault_names_are_explicit_strict_and_bounded()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);

        var initial = Assert.Single(sessions.List());
        Assert.Equal(NamedSessionSupervisor.DefaultName, initial.Name);
        Assert.Equal(NamedSessionState.Cold, initial.State);
        Assert.Null(initial.WorkerProcessId);
        Assert.Equal(0, fleet.FactoryCount);
        Assert.Equal(0, fleet.StartCount);
        var coldState = await sessions.StateAsync(
            NamedSessionSupervisor.DefaultName,
            listAvailable: false);
        Assert.False(coldState.Available);
        Assert.Equal("session_cold", coldState.DetailCode);
        Assert.Equal(0, fleet.FactoryCount);
        var closeDefault = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.CloseAsync(NamedSessionSupervisor.DefaultName));
        Assert.Equal("default_session_required", closeDefault.DetailCode);

        foreach (var invalid in new[]
        {
            "", "UPPER", ".leading", "space name", new string('a', 65),
        })
        {
            var exception = await Assert.ThrowsAsync<NamedSessionException>(
                () => sessions.OpenAsync(invalid));
            Assert.Equal("invalid_session_name", exception.DetailCode);
        }

        var unknown = await Assert.ThrowsAsync<NamedSessionException>(() =>
            sessions.InvokeAsync(
                "missing",
                "'never'",
                raw: false,
                WorkerInvokeRoute.Pwsh,
                timeoutSeconds: 30,
                outputStore: null));
        Assert.Equal("session_not_found", unknown.DetailCode);
        Assert.Equal(0, fleet.StartCount);

        var defaultInvoke = await sessions.InvokeAsync(
            NamedSessionSupervisor.DefaultName,
            "'default'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            outputStore: null);
        Assert.Equal(WorkerResultStatus.Completed, defaultInvoke.Result.Status);
        Assert.Equal(
            NamedSessionState.Ready,
            sessions.List().Single(item => item.Name == "default").State);

        for (var index = 1; index < NamedSessionSupervisor.MaximumSessions; index++)
        {
            var opened = await sessions.OpenAsync($"slot-{index}");
            Assert.Equal(NamedSessionState.Ready, opened.State);
        }
        Assert.Equal(NamedSessionSupervisor.MaximumSessions, sessions.List().Length);

        var capacity = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.OpenAsync("one-too-many"));
        Assert.Equal("session_capacity_exceeded", capacity.DetailCode);
        Assert.Equal(
            NamedSessionSupervisor.MaximumSessions,
            fleet.StartCount);
        Assert.Equal(
            NamedSessionSupervisor.MaximumSessions,
            fleet.FactoryCount);
    }

    [Fact]
    public async Task Startup_obeys_its_deadline_and_does_not_retain_a_prelaunch_alias()
    {
        var fleet = new FakeFleet();
        fleet.EnqueueStart(async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested)
            {
                throw new SessionWorkerStartException(
                    "worker_start_timed_out",
                    processLaunched: false,
                    containment: null,
                    containmentEmpty: null,
                    exception);
            }
        });
        await using var sessions = new NamedSessionSupervisor(
            fleet.CreateFactory,
            startupTimeout: TimeSpan.FromMilliseconds(50),
            containmentGrace: TimeSpan.FromMilliseconds(250));

        var failure = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.OpenAsync("deadline"));

        Assert.Equal("worker_start_timed_out", failure.DetailCode);
        Assert.DoesNotContain(sessions.List(), item => item.Name == "deadline");
        Assert.Equal(1, fleet.StartCount);
    }

    [Fact]
    public async Task Default_factory_failure_leaves_it_cold_and_allows_a_fresh_retry()
    {
        var fleet = new FakeFleet();
        var providerCalls = 0;
        await using var sessions = new NamedSessionSupervisor(
            () =>
            {
                if (Interlocked.Increment(ref providerCalls) == 1)
                    throw new IOException("injected factory failure");
                return fleet.CreateFactory();
            },
            startupTimeout: TimeSpan.FromMilliseconds(250),
            containmentGrace: TimeSpan.FromMilliseconds(250));

        var first = await Assert.ThrowsAsync<NamedSessionException>(
            () => Invoke(sessions, NamedSessionSupervisor.DefaultName));

        Assert.Equal("worker_factory_failed", first.DetailCode);
        Assert.Equal(
            NamedSessionState.Cold,
            Assert.Single(sessions.List()).State);
        var retry = await Invoke(
            sessions,
            NamedSessionSupervisor.DefaultName);
        Assert.Equal(WorkerResultStatus.Completed, retry.Result.Status);
        Assert.Equal(2, providerCalls);
        Assert.Equal(1, fleet.StartCount);
    }

    [Fact]
    public async Task Launch_failure_with_an_unconfirmed_domain_is_not_reported_as_prelaunch()
    {
        var containment = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new ThrowingLauncher(
            new WorkerProcessException(
                "injected_launch_failure",
                containmentEmpty: containment.Task));
        var command = new WorkerLaunchCommand(
            Path.Combine(Path.GetTempPath(), "ptk-never-launched"),
            [],
            Path.GetTempPath(),
            []);
        var factory = new ProcessSessionWorkerFactory(
            launcher,
            command,
            WorkerOperationProtocol.CreateLimits(
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(2)));

        var failure = await Assert.ThrowsAsync<SessionWorkerStartException>(
            () => factory.StartAsync(
                Guid.NewGuid(),
                incarnation: 1,
                DateTimeOffset.UtcNow.AddSeconds(1),
                CancellationToken.None));

        Assert.True(failure.ProcessLaunched);
        Assert.Equal(
            WorkerContainmentOutcome.DescendantsUnknown,
            failure.Containment?.Outcome);
        Assert.Same(containment.Task, failure.ContainmentEmpty);
        containment.TrySetResult();
    }

    [Fact]
    public async Task Concurrent_open_shares_one_bounded_start_and_ready_open_is_idempotent()
    {
        var fleet = new FakeFleet();
        var startEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fleet.EnqueueStart(async (context, cancellationToken) =>
        {
            startEntered.TrySetResult();
            await releaseStart.Task.WaitAsync(cancellationToken);
            return fleet.CreateWorker(context);
        });
        await using var sessions = CreateSupervisor(fleet);

        var first = sessions.OpenAsync("exchange");
        await startEntered.Task.WaitAsync(CheckpointTimeout);
        var second = sessions.OpenAsync("exchange");
        Assert.Equal(1, fleet.StartCount);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        releaseStart.TrySetResult();
        var opened = await Task.WhenAll(first, second).WaitAsync(CheckpointTimeout);
        Assert.Equal(opened[0], opened[1]);
        Assert.Equal(1, fleet.StartCount);

        Assert.Equal(opened[0], await sessions.OpenAsync("exchange"));
        Assert.Equal(1, fleet.StartCount);
    }

    [Fact]
    public async Task Shutdown_waits_for_and_reaps_a_worker_that_returns_after_startup_was_canceled()
    {
        var fleet = new FakeFleet();
        var startEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fleet.EnqueueStart(async (context, _) =>
        {
            startEntered.TrySetResult();
            await releaseStart.Task;
            return fleet.CreateWorker(context);
        });
        await using var sessions = CreateSupervisor(fleet);

        var opening = sessions.OpenAsync("late-start");
        await startEntered.Task.WaitAsync(CheckpointTimeout);
        var shutdown = sessions.ShutdownAsync();
        var concurrentShutdown = sessions.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        Assert.False(concurrentShutdown.IsCompleted);

        releaseStart.TrySetResult();
        await Task.WhenAll(shutdown, concurrentShutdown)
            .WaitAsync(CheckpointTimeout);
        _ = await Assert.ThrowsAsync<NamedSessionException>(
            () => opening);

        Assert.Empty(sessions.List());
        var worker = Assert.Single(fleet.Workers);
        Assert.Equal(1, worker.StopCount);
        Assert.False(worker.IsTransportUsable);
    }

    [Fact]
    public async Task Prelaunch_failure_removes_a_new_alias_but_postlaunch_failure_reserves_it()
    {
        var fleet = new FakeFleet();
        fleet.EnqueueStart((_, _) => Task.FromException<ISessionWorker>(
            new SessionWorkerStartException(
                "prelaunch_failed",
                processLaunched: false,
                containment: null,
                containmentEmpty: null)));
        fleet.EnqueueStart((_, _) => Task.FromException<ISessionWorker>(
            new SessionWorkerStartException(
                "initialize_failed",
                processLaunched: true,
                WorkerContainmentResult.Confirmed(),
                Task.CompletedTask)));
        await using var sessions = CreateSupervisor(fleet);

        var prelaunch = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.OpenAsync("prelaunch"));
        Assert.Equal("prelaunch_failed", prelaunch.DetailCode);
        Assert.DoesNotContain(sessions.List(), item => item.Name == "prelaunch");

        var postlaunch = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.OpenAsync("postlaunch"));
        Assert.Equal("initialize_failed", postlaunch.DetailCode);
        var faulted = Assert.Single(
            sessions.List(),
            item => item.Name == "postlaunch");
        Assert.Equal(NamedSessionState.Faulted, faulted.State);

        var reopen = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.OpenAsync("postlaunch"));
        Assert.Equal("session_reset_required", reopen.DetailCode);

        var reset = await sessions.ResetAsync("postlaunch");
        Assert.Equal(NamedSessionState.Ready, reset.State);
        Assert.True(reset.WarmStateLost);
    }

    [Fact]
    public async Task Reset_and_close_refuse_while_foreground_work_is_active()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        await sessions.OpenAsync("busy");
        var worker = Assert.Single(fleet.Workers);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        worker.InvokeHandler = async (request, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Completed(request);
        };

        var invoke = sessions.InvokeAsync(
            "busy",
            "'work'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            outputStore: null);
        await entered.Task.WaitAsync(CheckpointTimeout);
        Assert.True(
            sessions.List().Single(item => item.Name == "busy").Active);

        var reset = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.ResetAsync("busy"));
        Assert.Equal("session_busy", reset.DetailCode);
        var close = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.CloseAsync("busy"));
        Assert.Equal("session_busy", close.DetailCode);

        release.TrySetResult();
        _ = await invoke.WaitAsync(CheckpointTimeout);
        await sessions.CloseAsync("busy");
        Assert.DoesNotContain(sessions.List(), item => item.Name == "busy");
    }

    [Fact]
    public async Task State_while_foreground_work_is_active_returns_busy_without_querying_the_worker()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        await sessions.OpenAsync("busy-state");
        var worker = Assert.Single(fleet.Workers);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stateCalls = 0;
        worker.InvokeHandler = async (request, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Completed(request);
        };
        worker.StateHandler = (_, _) =>
        {
            Interlocked.Increment(ref stateCalls);
            return Task.FromResult(
                new WorkerStateSnapshot(1, true, "unexpected", null));
        };

        var invoke = Invoke(sessions, "busy-state");
        await entered.Task.WaitAsync(CheckpointTimeout);
        var state = await sessions.StateAsync(
            "busy-state",
            listAvailable: false).WaitAsync(CheckpointTimeout);

        Assert.False(state.Available);
        Assert.Equal("session_busy", state.DetailCode);
        Assert.Equal(0, stateCalls);
        release.TrySetResult();
        _ = await invoke.WaitAsync(CheckpointTimeout);
    }

    [Fact]
    public async Task Unconfirmed_containment_reserves_alias_until_observer_then_reset()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        await sessions.OpenAsync("held");
        var worker = Assert.Single(fleet.Workers);
        var workerProcessId = worker.ProcessId;
        worker.ThrowOnProcessIdReadAfterStop = true;
        worker.StopResult = WorkerContainmentResult.Unknown("descendants_unknown");
        worker.SetContainmentPending();

        var close = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.CloseAsync("held"));
        Assert.Equal("descendants_unknown", close.DetailCode);
        Assert.Equal(
            NamedSessionState.Faulted,
            Assert.Single(sessions.List(), item => item.Name == "held").State);
        Assert.Null(
            Assert.Single(
                sessions.List(),
                item => item.Name == "held").WorkerProcessId);
        Assert.True(
            Assert.Single(
                sessions.List(),
                item => item.Name == "held").WarmStateLost);

        var resetBlocked = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.ResetAsync("held"));
        Assert.Equal("descendants_unknown", resetBlocked.DetailCode);
        var closeBlocked = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.CloseAsync("held"));
        Assert.Equal("descendants_unknown", closeBlocked.DetailCode);
        var reopenBlocked = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.OpenAsync("held"));
        Assert.Equal("session_reset_required", reopenBlocked.DetailCode);

        worker.ConfirmContainment();
        NamedSessionSnapshot? reset = null;
        var deadline = DateTimeOffset.UtcNow + CheckpointTimeout;
        while (reset is null && DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                reset = await sessions.ResetAsync("held");
            }
            catch (NamedSessionException exception)
                when (exception.DetailCode == "descendants_unknown")
            {
                await Task.Delay(10);
            }
        }
        Assert.NotNull(reset);

        var ready = Assert.Single(sessions.List(), item => item.Name == "held");
        Assert.Equal(NamedSessionState.Ready, ready.State);
        Assert.NotEqual(workerProcessId, ready.WorkerProcessId);
    }

    [Fact]
    public async Task Confirmed_containment_result_without_completed_proof_cannot_replace_a_worker()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        await sessions.OpenAsync("proof");
        var worker = Assert.Single(fleet.Workers);
        worker.SetContainmentPending();

        var blocked = await Assert.ThrowsAsync<NamedSessionException>(
            () => sessions.ResetAsync("proof"));

        Assert.Equal("descendants_unknown", blocked.DetailCode);
        Assert.Equal(1, fleet.StartCount);
        Assert.Equal(
            NamedSessionState.Faulted,
            sessions.List().Single(item => item.Name == "proof").State);

        worker.ConfirmContainment();
        var reset = await sessions.ResetAsync("proof");
        Assert.Equal(NamedSessionState.Ready, reset.State);
        Assert.Equal(2, fleet.StartCount);
    }

    [Fact]
    public async Task Unexpected_worker_loss_replaces_only_that_session_once()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        var first = await sessions.OpenAsync("first");
        var second = await sessions.OpenAsync("second");
        var firstWorker = fleet.Workers.Single(
            worker => worker.ProcessId == first.WorkerProcessId);
        var secondWorker = fleet.Workers.Single(
            worker => worker.ProcessId == second.WorkerProcessId);

        firstWorker.Fail(new IOException("injected worker crash"));

        await WaitUntilAsync(() =>
        {
            var snapshot = sessions.List().Single(item => item.Name == "first");
            return snapshot.State == NamedSessionState.Ready &&
                   snapshot.WorkerProcessId != firstWorker.ProcessId;
        });
        var replacement = sessions.List().Single(item => item.Name == "first");
        Assert.True(replacement.WarmStateLost);
        Assert.Equal("worker_lost", replacement.LastFailure);
        Assert.Equal(
            secondWorker.ProcessId,
            sessions.List().Single(item => item.Name == "second").WorkerProcessId);
        Assert.Equal(1, firstWorker.StopCount);
        Assert.Equal(0, secondWorker.StopCount);
        Assert.Equal(3, fleet.StartCount);
    }

    [Fact]
    public async Task Failed_automatic_replacement_faults_only_that_session_until_explicit_reset()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        var failed = await sessions.OpenAsync("failed");
        var sibling = await sessions.OpenAsync("sibling");
        var failedWorker = fleet.Workers.Single(
            worker => worker.ProcessId == failed.WorkerProcessId);
        var siblingWorker = fleet.Workers.Single(
            worker => worker.ProcessId == sibling.WorkerProcessId);
        fleet.EnqueueStart((_, _) =>
            Task.FromException<ISessionWorker>(
                new SessionWorkerStartException(
                    "replacement_start_failed",
                    processLaunched: false,
                    containment: null,
                    containmentEmpty: null)));

        failedWorker.Fail(new IOException("injected worker crash"));

        await WaitUntilAsync(() =>
            sessions.List().Single(item => item.Name == "failed").State ==
            NamedSessionState.Faulted);
        Assert.True(
            sessions.List().Single(item => item.Name == "failed").ResetRequired);
        Assert.Equal(3, fleet.StartCount);
        await Task.Delay(100);
        Assert.Equal(3, fleet.StartCount);
        Assert.Equal(
            siblingWorker.ProcessId,
            sessions.List().Single(item => item.Name == "sibling").WorkerProcessId);
        Assert.Equal(0, siblingWorker.StopCount);

        var reset = await sessions.ResetAsync("failed");

        Assert.Equal(NamedSessionState.Ready, reset.State);
        Assert.False(reset.ResetRequired);
        Assert.Equal(4, fleet.StartCount);
        Assert.Equal(
            siblingWorker.ProcessId,
            sessions.List().Single(item => item.Name == "sibling").WorkerProcessId);
    }

    [Fact]
    public async Task Worker_loss_cannot_mutate_another_supervisors_session()
    {
        var failedFleet = new FakeFleet();
        var otherFleet = new FakeFleet();
        await using var failedSupervisor = CreateSupervisor(failedFleet);
        await using var otherSupervisor = CreateSupervisor(otherFleet);
        var failed = await failedSupervisor.OpenAsync("exchange");
        var other = await otherSupervisor.OpenAsync("exchange");
        var failedWorker = failedFleet.Workers.Single(
            worker => worker.ProcessId == failed.WorkerProcessId);
        var otherWorker = otherFleet.Workers.Single(
            worker => worker.ProcessId == other.WorkerProcessId);

        failedWorker.Fail(new IOException("injected worker crash"));

        await WaitUntilAsync(() =>
            failedSupervisor.List().Single(item => item.Name == "exchange").State ==
                NamedSessionState.Ready &&
            failedSupervisor.List().Single(item => item.Name == "exchange")
                .WorkerProcessId != failedWorker.ProcessId);
        var unchanged = otherSupervisor.List()
            .Single(item => item.Name == "exchange");
        Assert.Equal(other, unchanged);
        Assert.Equal(0, otherWorker.StopCount);
        Assert.Equal(
            WorkerResultStatus.Completed,
            (await Invoke(otherSupervisor, "exchange")).Result.Status);
    }

    [Fact]
    public async Task Timeout_replaces_only_its_worker_and_late_old_failure_cannot_mutate_replacement()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        var first = await sessions.OpenAsync("first");
        var second = await sessions.OpenAsync("second");
        var firstWorker = fleet.Workers.Single(
            worker => worker.ProcessId == first.WorkerProcessId);
        var secondWorker = fleet.Workers.Single(
            worker => worker.ProcessId == second.WorkerProcessId);
        firstWorker.InvokeHandler = (request, _) =>
            Task.FromResult(new SessionWorkerInvocation(
                new WorkerResult(
                    request.RequestId,
                    WorkerResultStatus.TimedOut,
                    string.Empty,
                    "execution_timed_out"),
                ArtifactId: null,
                ArtifactContent: null));

        var result = await sessions.InvokeAsync(
            "first",
            "'timeout'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 1,
            outputStore: null);
        Assert.Equal(WorkerResultStatus.TimedOut, result.Result.Status);

        await WaitUntilAsync(() =>
            sessions.List().Single(item => item.Name == "first").State ==
            NamedSessionState.Ready &&
            sessions.List().Single(item => item.Name == "first").WorkerProcessId !=
            firstWorker.ProcessId);
        var replacement = sessions.List().Single(item => item.Name == "first");
        Assert.Equal("execution_timed_out", replacement.LastFailure);
        Assert.Equal(secondWorker.ProcessId, second.WorkerProcessId);
        Assert.Equal(
            secondWorker.ProcessId,
            sessions.List().Single(item => item.Name == "second").WorkerProcessId);

        firstWorker.Fail(new IOException("late old failure"));
        await Task.Delay(50);
        Assert.Equal(
            replacement.WorkerProcessId,
            sessions.List().Single(item => item.Name == "first").WorkerProcessId);
    }

    [Fact]
    public async Task Admitted_invoke_transport_failure_stops_admission_and_replaces_only_that_worker()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        var failed = await sessions.OpenAsync("failed");
        var sibling = await sessions.OpenAsync("sibling");
        var failedWorker = fleet.Workers.Single(
            worker => worker.ProcessId == failed.WorkerProcessId);
        var siblingWorker = fleet.Workers.Single(
            worker => worker.ProcessId == sibling.WorkerProcessId);
        failedWorker.InvokeHandler = (_, _) =>
        {
            failedWorker.MarkTransportUnusable();
            return Task.FromException<SessionWorkerInvocation>(
                new IOException("injected admitted transport failure"));
        };

        _ = await Assert.ThrowsAsync<IOException>(
            () => Invoke(sessions, "failed"));

        var afterFailure = sessions.List().Single(item => item.Name == "failed");
        Assert.False(
            afterFailure.State == NamedSessionState.Ready &&
            afterFailure.WorkerProcessId == failedWorker.ProcessId);
        await WaitUntilAsync(() =>
            sessions.List().Single(item => item.Name == "failed").State ==
                NamedSessionState.Ready &&
            sessions.List().Single(item => item.Name == "failed").WorkerProcessId !=
                failedWorker.ProcessId);
        Assert.Equal(
            "worker_transport_failed",
            sessions.List().Single(item => item.Name == "failed").LastFailure);
        Assert.Equal(1, failedWorker.StopCount);
        Assert.Equal(
            siblingWorker.ProcessId,
            sessions.List().Single(item => item.Name == "sibling").WorkerProcessId);
        Assert.Equal(0, siblingWorker.StopCount);
    }

    [Fact]
    public async Task State_transport_failure_recovers_the_selected_worker()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        var opened = await sessions.OpenAsync("state");
        var worker = fleet.Workers.Single(
            item => item.ProcessId == opened.WorkerProcessId);
        worker.StateHandler = (_, _) =>
        {
            worker.MarkTransportUnusable();
            return Task.FromException<WorkerStateSnapshot>(
                new EndOfStreamException("injected state transport failure"));
        };

        _ = await Assert.ThrowsAsync<EndOfStreamException>(
            () => sessions.StateAsync("state", listAvailable: false));

        var afterFailure = sessions.List().Single(item => item.Name == "state");
        Assert.False(
            afterFailure.State == NamedSessionState.Ready &&
            afterFailure.WorkerProcessId == worker.ProcessId);
        await WaitUntilAsync(() =>
            sessions.List().Single(item => item.Name == "state").State ==
                NamedSessionState.Ready &&
            sessions.List().Single(item => item.Name == "state").WorkerProcessId !=
                worker.ProcessId);
        Assert.Equal(1, worker.StopCount);
    }

    [Fact]
    public async Task Queued_same_session_call_is_refused_after_the_active_call_requires_recovery()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        var opened = await sessions.OpenAsync("queued");
        var worker = fleet.Workers.Single(
            item => item.ProcessId == opened.WorkerProcessId);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        worker.InvokeHandler = async (request, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new SessionWorkerInvocation(
                new WorkerResult(
                    request.RequestId,
                    WorkerResultStatus.TimedOut,
                    string.Empty,
                    "execution_timed_out"),
                ArtifactId: null,
                ArtifactContent: null);
        };

        var active = Invoke(sessions, "queued");
        await entered.Task.WaitAsync(CheckpointTimeout);
        var queued = Invoke(sessions, "queued");
        release.TrySetResult();

        var terminal = await active.WaitAsync(CheckpointTimeout);
        Assert.Equal(WorkerResultStatus.TimedOut, terminal.Result.Status);
        var refusal = await Assert.ThrowsAsync<NamedSessionException>(
            () => queued);
        Assert.Equal("session_recovering", refusal.DetailCode);
        Assert.Equal(1, calls);
        await WaitUntilAsync(() =>
            sessions.List().Single(item => item.Name == "queued").State ==
                NamedSessionState.Ready &&
            sessions.List().Single(item => item.Name == "queued").WorkerProcessId !=
                worker.ProcessId);
    }

    [Fact]
    public async Task Same_session_serializes_but_different_sessions_run_concurrently()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        var first = await sessions.OpenAsync("first");
        var second = await sessions.OpenAsync("second");
        var firstWorker = fleet.Workers.Single(
            worker => worker.ProcessId == first.WorkerProcessId);
        var secondWorker = fleet.Workers.Single(
            worker => worker.ProcessId == second.WorkerProcessId);

        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCalls = 0;
        firstWorker.InvokeHandler = async (request, cancellationToken) =>
        {
            if (Interlocked.Increment(ref firstCalls) == 1)
                firstEntered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Completed(request);
        };
        secondWorker.InvokeHandler = async (request, cancellationToken) =>
        {
            secondEntered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Completed(request);
        };

        var firstCall = Invoke(sessions, "first");
        await firstEntered.Task.WaitAsync(CheckpointTimeout);
        var queuedSameSession = Invoke(sessions, "first");
        var concurrentOtherSession = Invoke(sessions, "second");
        await secondEntered.Task.WaitAsync(CheckpointTimeout);
        Assert.Equal(1, Volatile.Read(ref firstCalls));
        Assert.False(queuedSameSession.IsCompleted);

        release.TrySetResult();
        await Task.WhenAll(firstCall, queuedSameSession, concurrentOtherSession)
            .WaitAsync(CheckpointTimeout);
        Assert.Equal(2, firstCalls);
    }

    [Fact]
    public async Task Completed_output_is_discoverable_through_public_list_without_response_handle()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        using var outputStore = CreateOutputStore();
        await sessions.OpenAsync("paid-review");

        var invokeCount = 0;
        fleet.Workers[0].InvokeHandler = (request, _) =>
        {
            Interlocked.Increment(ref invokeCount);
            return Task.FromResult(
                CompletedWithArtifact(request, "accepted-review-result"));
        };

        var completed = await sessions.InvokeAsync(
            "paid-review",
            "Invoke-PaidReview",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 900,
            outputStore);

        Assert.Equal(WorkerResultStatus.Completed, completed.Result.Status);
        Assert.NotNull(completed.OutputRecovery?.Handle);

        var listing = OutputTool.Output(
            outputStore,
            action: "list",
            session: "paid-review");
        Assert.Contains("action=list", listing, StringComparison.Ordinal);
        Assert.Contains("count=1", listing, StringComparison.Ordinal);
        Assert.Contains("limit=10", listing, StringComparison.Ordinal);
        Assert.Contains("session=paid-review", listing, StringComparison.Ordinal);
        var handleLine = Assert.Single(
            listing.Split(Environment.NewLine)
                .Where(line => line.StartsWith("handle=", StringComparison.Ordinal)));
        var discoveredHandle = handleLine["handle=".Length..]
            .Split(' ', 2, StringSplitOptions.None)[0];

        Assert.Contains(
            "accepted-review-result",
            OutputTool.Output(
                outputStore,
                discoveredHandle,
                maxBytes: OutputStore.MaximumReadBytes),
            StringComparison.Ordinal);
        Assert.Equal(1, invokeCount);
    }

    [Fact]
    public async Task Quota_refusal_disables_capture_before_dispatch_without_consuming_a_sibling_quota()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        using var outputStore = CreateOutputStore(
            maximumArtifactBytes: 1024,
            maximumSessionBytes: 1024,
            maximumAggregateBytes: 2048);
        var alpha = await sessions.OpenAsync("alpha");
        await sessions.OpenAsync("beta");
        Assert.True(
            outputStore.TryReserve(
                alpha.Identity.ToString("N"),
                out var heldAlphaQuota,
                out var reserveFailure),
            reserveFailure);
        using var held = heldAlphaQuota;
        var alphaWorker = fleet.Workers[0];
        var betaWorker = fleet.Workers[1];
        var alphaDispatched = 0;
        alphaWorker.InvokeHandler = (request, _) =>
        {
            Interlocked.Increment(ref alphaDispatched);
            Assert.Null(request.Artifact);
            return Task.FromResult(Completed(request));
        };
        betaWorker.InvokeHandler = (request, _) =>
            Task.FromResult(CompletedWithArtifact(request, "beta-output"));

        var alphaResult = await sessions.InvokeAsync(
            "alpha",
            "'alpha'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            outputStore);
        var betaResult = await sessions.InvokeAsync(
            "beta",
            "'beta'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            outputStore);

        Assert.Equal(1, alphaDispatched);
        Assert.Equal(WorkerResultStatus.Completed, alphaResult.Result.Status);
        Assert.Null(alphaResult.OutputRecovery?.Handle);
        Assert.Equal(
            "output_store_capacity",
            alphaResult.OutputRecovery?.DetailCode);
        var betaHandle = Assert.IsType<string>(
            betaResult.OutputRecovery?.Handle);
        Assert.Contains(
            "beta-output",
            outputStore.Read(
                betaHandle,
                offset: 0,
                maximumBytes: OutputStore.MaximumReadBytes).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Healthy_concurrent_sessions_wait_for_one_lane_and_both_publish()
    {
        using var firstWriteEntered = new ManualResetEventSlim();
        using var releaseFirstWrite = new ManualResetEventSlim();
        var writeStarts = 0;
        using var outputStore = CreateOutputStore(
            maximumArtifactBytes: 1024,
            maximumSessionBytes: 1024,
            maximumAggregateBytes: 2048,
            artifactWriteStartingForTests: _ =>
            {
                if (Interlocked.Increment(ref writeStarts) != 1)
                    return;
                firstWriteEntered.Set();
                Assert.True(releaseFirstWrite.Wait(CheckpointTimeout));
            });
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        await sessions.OpenAsync("alpha");
        await sessions.OpenAsync("beta");
        var betaInvoked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fleet.Workers[0].InvokeHandler = (request, _) =>
            Task.FromResult(CompletedWithArtifact(request, "alpha-output"));
        fleet.Workers[1].InvokeHandler = (request, _) =>
        {
            betaInvoked.TrySetResult();
            return Task.FromResult(CompletedWithArtifact(request, "beta-output"));
        };

        var alphaCall = sessions.InvokeAsync(
            "alpha",
            "'alpha'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            outputStore);
        Assert.True(firstWriteEntered.Wait(CheckpointTimeout));

        var stateStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var betaState = await sessions.StateAsync(
            "beta",
            listAvailable: false).WaitAsync(TimeSpan.FromSeconds(1));
        stateStopwatch.Stop();
        Assert.True(betaState.Available);
        Assert.True(
            stateStopwatch.Elapsed < TimeSpan.FromSeconds(1),
            stateStopwatch.Elapsed.ToString());

        var betaCall = sessions.InvokeAsync(
            "beta",
            "'beta'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            outputStore);
        await Task.Delay(50);
        Assert.False(betaInvoked.Task.IsCompleted);

        releaseFirstWrite.Set();
        var results = await Task.WhenAll(alphaCall, betaCall)
            .WaitAsync(CheckpointTimeout);

        Assert.Equal(2, writeStarts);
        Assert.True(betaInvoked.Task.IsCompletedSuccessfully);
        var alphaHandle = Assert.IsType<string>(
            results[0].OutputRecovery?.Handle);
        var betaHandle = Assert.IsType<string>(
            results[1].OutputRecovery?.Handle);
        Assert.Contains(
            "alpha-output",
            outputStore.Read(
                alphaHandle,
                offset: 0,
                maximumBytes: OutputStore.MaximumReadBytes).Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "beta-output",
            outputStore.Read(
                betaHandle,
                offset: 0,
                maximumBytes: OutputStore.MaximumReadBytes).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wedged_storage_lane_starts_no_contender_then_runs_the_command()
    {
        using var outputStore = CreateOutputStore();
        using var wedgeEntered = new ManualResetEventSlim();
        using var releaseWedge = new ManualResetEventSlim();
        var storageStarts = 0;
        var wedged = await outputStore.WaitToStartForegroundOperationAsync(
            () =>
            {
                Interlocked.Increment(ref storageStarts);
                wedgeEntered.Set();
                releaseWedge.Wait();
                return 1;
            },
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.NotNull(wedged);
        Assert.True(wedgeEntered.Wait(CheckpointTimeout));

        var fleet = new FakeFleet();
        await using var sessions = new NamedSessionSupervisor(
            fleet.CreateFactory,
            startupTimeout: TimeSpan.FromMilliseconds(250),
            containmentGrace: TimeSpan.FromMilliseconds(250),
            outputStorageWait: TimeSpan.FromMilliseconds(75));
        await sessions.OpenAsync("alpha");
        await sessions.OpenAsync("beta");
        var commandDispatched = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fleet.Workers[1].InvokeHandler = (request, _) =>
        {
            Assert.Null(request.Artifact);
            commandDispatched.TrySetResult();
            return Task.FromResult(Completed(request));
        };

        var betaCall = sessions.InvokeAsync(
            "beta",
            "'still runs'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            outputStore);
        var alphaState = await sessions.StateAsync(
            "alpha",
            listAvailable: false).WaitAsync(TimeSpan.FromSeconds(1));
        var result = await betaCall.WaitAsync(CheckpointTimeout);

        Assert.True(alphaState.Available);
        Assert.True(commandDispatched.Task.IsCompletedSuccessfully);
        Assert.Equal(WorkerResultStatus.Completed, result.Result.Status);
        Assert.Null(result.OutputRecovery?.Handle);
        Assert.Equal(
            "output_store_prepare_timed_out",
            result.OutputRecovery?.DetailCode);
        Assert.Equal(1, storageStarts);

        releaseWedge.Set();
        Assert.Equal(1, await wedged!.WaitAsync(CheckpointTimeout));
    }

    [Fact]
    public async Task Sealed_output_survives_close_but_unsealed_capture_never_attaches_to_reopen()
    {
        var fleet = new FakeFleet();
        await using var sessions = CreateSupervisor(fleet);
        var outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "named-session-output-tests",
            Guid.NewGuid().ToString("N"));
        using var outputStore = new OutputStore(new OutputStoreOptions(
            outputRoot,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            MaximumArtifactBytes: 1024 * 1024,
            MaximumSessionBytes: 2 * 1024 * 1024,
            MaximumAggregateBytes: 4 * 1024 * 1024));
        await sessions.OpenAsync("output");
        var worker = Assert.Single(fleet.Workers);
        worker.InvokeHandler = (request, _) =>
        {
            var content = request.Artifact is null
                ? null
                : new OutputArtifactContent(
                    "sealed-output",
                    StandardError: [],
                    Errors: [],
                    Warnings: [],
                    ExitCode: null,
                    OutputProvenance.DirectText);
            return Task.FromResult(new SessionWorkerInvocation(
                new WorkerResult(
                    request.RequestId,
                    WorkerResultStatus.Completed,
                    "ok",
                    DetailCode: null),
                request.Artifact?.ArtifactId,
                content));
        };

        var sealedResult = await sessions.InvokeAsync(
            "output",
            "'sealed'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            outputStore);
        var handle = Assert.IsType<string>(sealedResult.OutputRecovery?.Handle);
        var beforeClose = outputStore.Read(
            handle,
            offset: 0,
            maximumBytes: OutputStore.MaximumReadBytes);
        Assert.Contains("sealed-output", beforeClose.Text);

        worker.InvokeHandler = (request, _) =>
            Task.FromResult(Completed(request));
        var unsealed = await sessions.InvokeAsync(
            "output",
            "'unsealed'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            outputStore);
        Assert.Null(unsealed.OutputRecovery?.Handle);

        var oldIdentity = sessions.List().Single(item => item.Name == "output").Identity;
        await sessions.CloseAsync("output");
        var afterClose = outputStore.Read(
            handle,
            offset: 0,
            maximumBytes: OutputStore.MaximumReadBytes);
        Assert.Equal(beforeClose.Text, afterClose.Text);

        var reopened = await sessions.OpenAsync("output");
        Assert.NotEqual(oldIdentity, reopened.Identity);
        Assert.Equal(
            OutputArtifactState.Available,
            outputStore.Status(handle).State);
    }

    [Fact]
    public async Task Supervisors_are_disjoint_and_shutdown_reaps_only_owned_workers()
    {
        var firstFleet = new FakeFleet();
        var secondFleet = new FakeFleet();
        await using var first = CreateSupervisor(firstFleet);
        await using var second = CreateSupervisor(secondFleet);
        await first.OpenAsync("shared-label");
        await second.OpenAsync("shared-label");

        var firstSnapshot = first.List().Single(item => item.Name == "shared-label");
        var secondSnapshot = second.List().Single(item => item.Name == "shared-label");
        Assert.NotEqual(firstSnapshot.Identity, secondSnapshot.Identity);
        Assert.NotEqual(firstSnapshot.WorkerProcessId, secondSnapshot.WorkerProcessId);
        await first.OpenAsync("first-only");
        var invisible = await Assert.ThrowsAsync<NamedSessionException>(
            () => second.StateAsync("first-only", listAvailable: false));
        Assert.Equal("session_not_found", invisible.DetailCode);

        await first.ShutdownAsync();
        Assert.Empty(first.List());
        Assert.All(firstFleet.Workers, worker => Assert.Equal(1, worker.StopCount));
        Assert.Equal(0, secondFleet.Workers.Single().StopCount);
        Assert.Equal(
            NamedSessionState.Ready,
            second.List().Single(item => item.Name == "shared-label").State);
    }

    private static NamedSessionSupervisor CreateSupervisor(FakeFleet fleet) =>
        new(
            fleet.CreateFactory,
            startupTimeout: TimeSpan.FromMilliseconds(250),
            containmentGrace: TimeSpan.FromMilliseconds(250));

    private static Task<NamedSessionInvokeResult> Invoke(
        NamedSessionSupervisor sessions,
        string name) =>
        sessions.InvokeAsync(
            name,
            "'work'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            outputStore: null);

    private static SessionWorkerInvocation Completed(FakeInvokeRequest request) =>
        new(
            new WorkerResult(
                request.RequestId,
                WorkerResultStatus.Completed,
                "ok",
                DetailCode: null),
            ArtifactId: null,
            ArtifactContent: null);

    private static SessionWorkerInvocation CompletedWithArtifact(
        FakeInvokeRequest request,
        string text) =>
        new(
            new WorkerResult(
                request.RequestId,
                WorkerResultStatus.Completed,
                "ok",
                DetailCode: null),
            request.Artifact?.ArtifactId,
            request.Artifact is null
                ? null
                : new OutputArtifactContent(
                    text,
                    StandardError: [],
                    Errors: [],
                    Warnings: [],
                    ExitCode: null,
                    OutputProvenance.DirectText));

    private static OutputStore CreateOutputStore(
        long maximumArtifactBytes = 1024,
        long maximumSessionBytes = 2048,
        long maximumAggregateBytes = 4096,
        Action<string>? artifactWriteStartingForTests = null)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "named-session-output-tests",
            Guid.NewGuid().ToString("N"));
        return new OutputStore(new OutputStoreOptions(
            root,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            maximumArtifactBytes,
            maximumSessionBytes,
            maximumAggregateBytes,
            ArtifactWriteStartingForTests:
                artifactWriteStartingForTests));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + CheckpointTimeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed record FakeStartContext(Guid SessionId, long Incarnation);

    private sealed record FakeInvokeRequest(
        long RequestId,
        string Script,
        WorkerArtifactRequest? Artifact);

    private sealed class FakeFleet
    {
        private static int _nextProcessId = 40000;
        private readonly ConcurrentQueue<
            Func<FakeStartContext, CancellationToken, Task<ISessionWorker>>>
            _startBehaviors = new();
        private int _startCount;
        private int _factoryCount;

        internal int StartCount => Volatile.Read(ref _startCount);
        internal int FactoryCount => Volatile.Read(ref _factoryCount);
        internal List<FakeWorker> Workers { get; } = [];

        internal ISessionWorkerFactory CreateFactory()
        {
            Interlocked.Increment(ref _factoryCount);
            return new FakeFactory(this);
        }

        internal void EnqueueStart(
            Func<FakeStartContext, CancellationToken, Task<ISessionWorker>> behavior) =>
            _startBehaviors.Enqueue(behavior);

        internal FakeWorker CreateWorker(FakeStartContext context)
        {
            var worker = new FakeWorker(
                Interlocked.Increment(ref _nextProcessId),
                context.SessionId,
                context.Incarnation);
            lock (Workers) Workers.Add(worker);
            return worker;
        }

        private async Task<ISessionWorker> StartAsync(
            Guid sessionId,
            long incarnation,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);
            var context = new FakeStartContext(sessionId, incarnation);
            if (_startBehaviors.TryDequeue(out var behavior))
                return await behavior(context, cancellationToken);
            return CreateWorker(context);
        }

        private sealed class FakeFactory(FakeFleet owner) : ISessionWorkerFactory
        {
            public Task<ISessionWorker> StartAsync(
                Guid sessionId,
                long incarnation,
                DateTimeOffset deadlineUtc,
                CancellationToken cancellationToken) =>
                owner.StartAsync(sessionId, incarnation, cancellationToken);
        }
    }

    private sealed class ThrowingLauncher(Exception exception) :
        IWorkerProcessLauncher
    {
        public Task<IWorkerContainedProcess> LaunchAsync(
            WorkerLaunchCommand command,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IWorkerContainedProcess>(exception);
    }

    private sealed class FakeWorker : ISessionWorker
    {
        private readonly TaskCompletionSource _fatal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _containment =
            CompletedContainment();
        private long _requestId;

        internal FakeWorker(int processId, Guid sessionId, long incarnation)
        {
            _processId = processId;
            SessionId = sessionId;
            Incarnation = incarnation;
            StateHandler = DefaultStateAsync;
        }

        private readonly int _processId;
        public int ProcessId =>
            ThrowOnProcessIdReadAfterStop && StopCount != 0
                ? throw new InvalidOperationException(
                    "A stopped process no longer exposes its PID.")
                : _processId;
        public Guid SessionId { get; }
        public long Incarnation { get; }
        public bool IsTransportUsable =>
            Volatile.Read(ref _transportUsable) != 0;
        public Task Fatal => _fatal.Task;
        public Task ContainmentEmpty => _containment.Task;
        internal int StopCount { get; private set; }
        internal bool ThrowOnProcessIdReadAfterStop { get; set; }
        internal WorkerContainmentResult StopResult { get; set; } =
            WorkerContainmentResult.Confirmed();
        internal Func<FakeInvokeRequest, CancellationToken, Task<SessionWorkerInvocation>>
            InvokeHandler
        { get; set; } =
                (request, _) => Task.FromResult(Completed(request));
        internal Func<bool, CancellationToken, Task<WorkerStateSnapshot>>
            StateHandler
        { get; set; }
        private int _transportUsable = 1;

        public async Task<SessionWorkerInvocation> InvokeAsync(
            string script,
            bool raw,
            WorkerInvokeRoute route,
            int timeoutSeconds,
            IWorkerArtifactCapture? artifactCapture,
            CancellationToken cancellationToken)
        {
            var requestId = Interlocked.Increment(ref _requestId);
            artifactCapture?.BindRequest(requestId);
            var request = new FakeInvokeRequest(
                requestId,
                script,
                artifactCapture?.Request);
            var invocation = await InvokeHandler(
                request,
                cancellationToken).ConfigureAwait(false);
            if (artifactCapture is null)
                return invocation;

            if (invocation.ArtifactId == artifactCapture.Request.ArtifactId &&
                invocation.ArtifactContent is { } content)
            {
                var bytes = WorkerOutputArtifactCodec.Encode(
                    content,
                    artifactCapture.Request.MaximumBytes);
                artifactCapture.Accept(
                    new WorkerArtifactChunk(
                        requestId,
                        artifactCapture.Request.ArtifactId,
                        Offset: 0,
                        bytes));
                artifactCapture.Accept(
                    new WorkerArtifactSeal(
                        requestId,
                        artifactCapture.Request.ArtifactId,
                        bytes.Length,
                        Convert.ToHexString(
                                System.Security.Cryptography.SHA256.HashData(bytes))
                            .ToLowerInvariant()));
            }

            return invocation with
            {
                OutputRecoveryCompletion =
                    artifactCapture.CompleteAtResultAsync(),
            };
        }

        public Task<WorkerStateSnapshot> StateAsync(
            bool listAvailable,
            CancellationToken cancellationToken) =>
            StateHandler(listAvailable, cancellationToken);

        private Task<WorkerStateSnapshot> DefaultStateAsync(
            bool listAvailable,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new WorkerStateSnapshot(
                    Interlocked.Increment(ref _requestId),
                    Available: true,
                    Text: $"pid={ProcessId}",
                    DetailCode: null));

        public Task<WorkerContainmentResult> StopAsync(
            WorkerContainmentReason reason,
            CancellationToken cancellationToken)
        {
            StopCount++;
            MarkTransportUnusable();
            _fatal.TrySetResult();
            return Task.FromResult(StopResult);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void Fail(Exception exception)
        {
            MarkTransportUnusable();
            _fatal.TrySetException(exception);
        }

        internal void MarkTransportUnusable() =>
            Interlocked.Exchange(ref _transportUsable, 0);

        internal void SetContainmentPending() =>
            _containment = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ConfirmContainment() =>
            _containment.TrySetResult();

        private static TaskCompletionSource CompletedContainment()
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completion.TrySetResult();
            return completion;
        }
    }
}
