using System.Reflection;
using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianCallDeliveryTrackerTests
{
    private static readonly HostBootId HostA = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId HostB = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));

    [Fact]
    public void Public_delivery_state_is_closed_and_distinct_from_the_frozen_wire_state()
    {
        Assert.NotEqual(
            typeof(GuardianHostDeliveryState),
            typeof(GuardianPublicDeliveryState));
        Assert.Equal(
            [
                "NotDispatched",
                "WriteStarted",
                "TerminalDecoded",
                "PublicTerminalSent",
            ],
            Enum.GetNames<GuardianPublicDeliveryState>());
        Assert.Equal(
            ["NotDispatched", "WriteStarted", "TerminalDecoded"],
            Enum.GetNames<GuardianHostDeliveryState>());
    }

    [Fact]
    public void Loss_classification_is_exact_and_frozen_at_every_delivery_barrier()
    {
        var notDispatched = NewTracker();
        AssertLoss(
            notDispatched,
            GuardianHostLossDisposition.BackendLostBeforeDispatch,
            GuardianPublicDeliveryState.NotDispatched);
        Assert.Equal(
            GuardianLocalTerminalResult.Accepted,
            notDispatched.TrySetLocalTerminal("proved-no-start"));
        Assert.Equal("proved-no-start", AssertDelivered(notDispatched));

        var writeStarted = NewTracker();
        StartWrite(writeStarted);
        AssertLoss(
            writeStarted,
            GuardianHostLossDisposition.OutcomeUnknown,
            GuardianPublicDeliveryState.WriteStarted);
        Assert.Equal(
            GuardianLocalTerminalResult.WriteAlreadyStarted,
            writeStarted.TrySetLocalTerminal("unsafe-replacement"));

        var terminalDecoded = NewTracker();
        StartWrite(terminalDecoded);
        Assert.Equal(
            GuardianTerminalCorrelationResult.Accepted,
            terminalDecoded.TryDecodeTerminal(Identity(), "decoded"));
        AssertLoss(
            terminalDecoded,
            GuardianHostLossDisposition.RetainedAuthoritativeTerminal,
            GuardianPublicDeliveryState.TerminalDecoded);
        Assert.Equal("decoded", AssertDelivered(terminalDecoded));

        var publicSent = NewTracker();
        StartWrite(publicSent);
        Assert.Equal(
            GuardianTerminalCorrelationResult.Accepted,
            publicSent.TryDecodeTerminal(Identity(), "sent"));
        Assert.Equal("sent", AssertDelivered(publicSent));
        AssertLoss(
            publicSent,
            GuardianHostLossDisposition.PublicTerminalAlreadySent,
            GuardianPublicDeliveryState.PublicTerminalSent);
    }

    [Fact]
    public void First_possibly_writing_callback_observes_WriteStarted_and_sync_failure_is_unknown()
    {
        var tracker = NewTracker();
        GuardianCallDeliverySnapshot? observed = null;
        GuardianPrivateRequestIdentity? callbackIdentity = null;

        Assert.Throws<IOException>(() =>
        {
            _ = tracker.BeginFirstWriteAsync((identity, _) =>
            {
                callbackIdentity = identity;
                observed = tracker.Snapshot();
                throw new IOException("Injected synchronous writer failure.");
            });
        });

        Assert.Equal(Identity(), callbackIdentity);
        Assert.NotNull(observed);
        Assert.Equal(GuardianPublicDeliveryState.WriteStarted, observed.State);
        Assert.False(observed.HasRetainedTerminal);
        Assert.Equal(
            GuardianHostLossDisposition.OutcomeUnknown,
            tracker.ObserveHostLoss(HostA, new HostGeneration(7)).Disposition);
        Assert.Equal(
            GuardianPublicTerminalDeliveryResult.NotAvailable,
            DeliverWithoutWriting(tracker));
    }

    [Fact]
    public async Task Async_first_write_failure_remains_write_started_and_unknown()
    {
        var tracker = NewTracker();
        var callbackCount = 0;

        await Assert.ThrowsAsync<IOException>(async () =>
            await tracker.BeginFirstWriteAsync((identity, _) =>
            {
                Assert.Equal(Identity(), identity);
                Interlocked.Increment(ref callbackCount);
                return new ValueTask(Task.FromException(
                    new IOException("Injected asynchronous writer failure.")));
            }));

        Assert.Equal(1, callbackCount);
        var snapshot = tracker.Snapshot();
        Assert.Equal(GuardianPublicDeliveryState.WriteStarted, snapshot.State);
        Assert.False(snapshot.HasRetainedTerminal);
        Assert.Equal(
            GuardianHostLossDisposition.OutcomeUnknown,
            tracker.ObserveHostLoss(HostA, new HostGeneration(7)).Disposition);
    }

    [Fact]
    public async Task First_write_callback_invocation_owns_the_stop_and_loss_boundary()
    {
        var tracker = NewTracker();
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var stopAttempted = new ManualResetEventSlim();
        using var lossAttempted = new ManualResetEventSlim();

        var writeTask = Task.Run(() => tracker.BeginFirstWriteAsync((identity, _) =>
        {
            Assert.Equal(Identity(), identity);
            callbackEntered.Set();
            Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(5)));
            return ValueTask.CompletedTask;
        }).AsTask());

        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
        var stopTask = Task.Run(() =>
        {
            stopAttempted.Set();
            return tracker.Stop();
        });
        var lossTask = Task.Run(() =>
        {
            lossAttempted.Set();
            return tracker.ObserveHostLoss(HostA, new HostGeneration(7));
        });
        Assert.True(stopAttempted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(lossAttempted.Wait(TimeSpan.FromSeconds(5)));

        await Task.Delay(50);
        Assert.False(stopTask.IsCompleted);
        Assert.False(lossTask.IsCompleted);

        releaseCallback.Set();
        await writeTask;
        Assert.True(await stopTask);
        Assert.Equal(
            GuardianHostLossDisposition.OutcomeUnknown,
            (await lossTask).Disposition);
    }

    [Fact]
    public void Stale_old_host_loss_is_inert_and_cannot_block_the_bound_generation()
    {
        var tracker = NewTracker();
        var before = tracker.Snapshot();

        var staleBoot = tracker.ObserveHostLoss(HostB, new HostGeneration(7));
        var staleGeneration = tracker.ObserveHostLoss(HostA, new HostGeneration(6));

        Assert.Equal(GuardianHostLossDisposition.StaleHostIdentity, staleBoot.Disposition);
        Assert.Equal(GuardianHostLossDisposition.StaleHostIdentity, staleGeneration.Disposition);
        Assert.False(staleBoot.IsFirstObservation);
        Assert.False(staleGeneration.IsFirstObservation);
        Assert.Equal(before, tracker.Snapshot());

        StartWrite(tracker);
        Assert.Equal(
            GuardianTerminalCorrelationResult.Accepted,
            tracker.TryDecodeTerminal(Identity(), "current"));
        Assert.Equal("current", AssertDelivered(tracker));
    }

    [Fact]
    public void Private_terminal_correlation_fails_closed_and_never_installs_a_replacement()
    {
        var wrongHost = NewTracker();
        StartWrite(wrongHost);
        Assert.Equal(
            GuardianTerminalCorrelationResult.MismatchedHostFatal,
            wrongHost.TryDecodeTerminal(Identity(hostBootId: HostB), "wrong-host"));
        Assert.Equal(
            GuardianTerminalCorrelationResult.CorrelationAlreadyFatal,
            wrongHost.TryDecodeTerminal(Identity(), "replacement"));
        Assert.False(wrongHost.Snapshot().HasRetainedTerminal);

        var unknownRequest = NewTracker();
        StartWrite(unknownRequest);
        Assert.Equal(
            GuardianTerminalCorrelationResult.UnknownRequestFatal,
            unknownRequest.TryDecodeTerminal(Identity(requestId: 12), "unknown"));
        Assert.Equal(
            GuardianTerminalCorrelationResult.CorrelationAlreadyFatal,
            unknownRequest.TryDecodeTerminal(Identity(), "replacement"));
        Assert.False(unknownRequest.Snapshot().HasRetainedTerminal);

        var beforeWrite = NewTracker();
        Assert.Equal(
            GuardianTerminalCorrelationResult.UnexpectedDeliveryStateFatal,
            beforeWrite.TryDecodeTerminal(Identity(), "early"));
        Assert.False(beforeWrite.Snapshot().HasRetainedTerminal);

        var duplicate = NewTracker();
        StartWrite(duplicate);
        Assert.Equal(
            GuardianTerminalCorrelationResult.Accepted,
            duplicate.TryDecodeTerminal(Identity(), "authoritative"));
        Assert.Equal(
            GuardianTerminalCorrelationResult.DuplicateTerminalFatal,
            duplicate.TryDecodeTerminal(Identity(), "replacement"));
        Assert.True(duplicate.Snapshot().CorrelationFatal);
        Assert.Equal("authoritative", AssertDelivered(duplicate));
    }

    [Fact]
    public async Task Concurrent_terminal_decodes_and_public_claims_have_exactly_one_owner()
    {
        var tracker = NewTracker();
        StartWrite(tracker);
        var candidates = new[] { "first", "second" };

        var decodeTasks = candidates.Select(candidate => Task.Run(() =>
            (Candidate: candidate, Result: tracker.TryDecodeTerminal(Identity(), candidate))));
        var decoded = await Task.WhenAll(decodeTasks);

        var accepted = Assert.Single(
            decoded,
            result => result.Result == GuardianTerminalCorrelationResult.Accepted);
        Assert.Single(
            decoded,
            result => result.Result == GuardianTerminalCorrelationResult.DuplicateTerminalFatal);

        string? delivered = null;
        var callbackCount = 0;
        var claims = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Task.Run(async () => await tracker.DeliverPublicTerminalAsync((terminal, _) =>
            {
                Interlocked.Increment(ref callbackCount);
                delivered = terminal;
                return ValueTask.CompletedTask;
            }))));

        Assert.Equal(1, callbackCount);
        Assert.Equal(accepted.Candidate, delivered);
        Assert.Single(
            claims,
            claim => claim == GuardianPublicTerminalDeliveryResult.Sent);
        Assert.Equal(
            31,
            claims.Count(claim =>
                claim is GuardianPublicTerminalDeliveryResult.AlreadyClaimed or
                    GuardianPublicTerminalDeliveryResult.AlreadySent));
        Assert.Equal(
            GuardianPublicDeliveryState.PublicTerminalSent,
            tracker.Snapshot().State);
    }

    [Fact]
    public async Task Public_terminal_is_retained_and_single_owned_until_write_succeeds()
    {
        var tracker = NewTracker();
        StartWrite(tracker);
        Assert.Equal(
            GuardianTerminalCorrelationResult.Accepted,
            tracker.TryDecodeTerminal(Identity(), "authoritative"));
        var releaseWrite = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;

        var delivery = tracker.DeliverPublicTerminalAsync((terminal, _) =>
        {
            Assert.Equal("authoritative", terminal);
            Interlocked.Increment(ref callbackCount);
            return new ValueTask(releaseWrite.Task);
        });

        Assert.False(delivery.IsCompleted);
        var inFlight = tracker.Snapshot();
        Assert.Equal(GuardianPublicDeliveryState.TerminalDecoded, inFlight.State);
        Assert.True(inFlight.PublicDeliveryClaimed);
        Assert.True(inFlight.HasRetainedTerminal);
        Assert.Equal(1, callbackCount);
        Assert.Equal(
            GuardianPublicTerminalDeliveryResult.AlreadyClaimed,
            await tracker.DeliverPublicTerminalAsync((_, _) =>
            {
                Interlocked.Increment(ref callbackCount);
                return ValueTask.CompletedTask;
            }));
        Assert.Equal(1, callbackCount);

        releaseWrite.SetResult(true);
        Assert.Equal(GuardianPublicTerminalDeliveryResult.Sent, await delivery);
        var sent = tracker.Snapshot();
        Assert.Equal(GuardianPublicDeliveryState.PublicTerminalSent, sent.State);
        Assert.True(sent.PublicDeliveryClaimed);
        Assert.False(sent.HasRetainedTerminal);
        Assert.Equal(
            GuardianPublicTerminalDeliveryResult.AlreadySent,
            DeliverWithoutWriting(tracker));
    }

    [Fact]
    public async Task Failed_public_write_retains_authoritative_terminal_without_a_second_writer()
    {
        var tracker = NewTracker();
        StartWrite(tracker);
        Assert.Equal(
            GuardianTerminalCorrelationResult.Accepted,
            tracker.TryDecodeTerminal(Identity(), "authoritative"));
        var failWrite = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;

        var delivery = tracker.DeliverPublicTerminalAsync((terminal, _) =>
        {
            Assert.Equal("authoritative", terminal);
            Interlocked.Increment(ref callbackCount);
            return new ValueTask(failWrite.Task);
        });
        Assert.Equal(
            GuardianHostLossDisposition.RetainedAuthoritativeTerminal,
            tracker.ObserveHostLoss(HostA, new HostGeneration(7)).Disposition);

        failWrite.SetException(new IOException("Injected public writer failure."));
        await Assert.ThrowsAsync<IOException>(async () => await delivery);

        var failed = tracker.Snapshot();
        Assert.Equal(GuardianPublicDeliveryState.TerminalDecoded, failed.State);
        Assert.True(failed.PublicDeliveryClaimed);
        Assert.True(failed.HasRetainedTerminal);
        Assert.Equal(
            GuardianHostLossDisposition.RetainedAuthoritativeTerminal,
            failed.LossDisposition);
        Assert.Equal(
            GuardianPublicTerminalDeliveryResult.AlreadyClaimed,
            await tracker.DeliverPublicTerminalAsync((_, _) =>
            {
                Interlocked.Increment(ref callbackCount);
                return ValueTask.CompletedTask;
            }));
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void Synchronous_public_write_failure_retains_terminal_and_claim()
    {
        var tracker = NewTracker();
        StartWrite(tracker);
        Assert.Equal(
            GuardianTerminalCorrelationResult.Accepted,
            tracker.TryDecodeTerminal(Identity(), "authoritative"));

        Assert.Throws<IOException>(() =>
        {
            _ = tracker.DeliverPublicTerminalAsync((terminal, _) =>
            {
                Assert.Equal("authoritative", terminal);
                throw new IOException("Injected synchronous public writer failure.");
            });
        });

        var failed = tracker.Snapshot();
        Assert.Equal(GuardianPublicDeliveryState.TerminalDecoded, failed.State);
        Assert.True(failed.PublicDeliveryClaimed);
        Assert.True(failed.HasRetainedTerminal);
        Assert.Equal(
            GuardianHostLossDisposition.RetainedAuthoritativeTerminal,
            tracker.ObserveHostLoss(HostA, new HostGeneration(7)).Disposition);
        Assert.Equal(
            GuardianPublicTerminalDeliveryResult.AlreadyClaimed,
            DeliverWithoutWriting(tracker));
    }

    [Fact]
    public void Cancellation_before_write_uses_one_local_terminal_and_starts_no_private_write()
    {
        var tracker = NewTracker();

        var callbackCount = 0;
        Assert.Equal(
            GuardianCancellationClaimResult.BeforeDispatchTerminalInstalled,
            Cancel(tracker, signal: _ => Interlocked.Increment(ref callbackCount)));
        Assert.Equal(0, callbackCount);
        Assert.False(tracker.Snapshot().CancellationSignaled);
        Assert.Equal(
            GuardianLocalTerminalResult.TerminalAlreadyDecoded,
            tracker.TrySetLocalTerminal("replacement"));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = tracker.BeginFirstWriteAsync((_, _) => ValueTask.CompletedTask);
        });
        Assert.Equal("canceled-before-dispatch", AssertDelivered(tracker));
    }

    [Fact]
    public void Cancellation_after_write_targets_only_the_original_request_and_never_owns_terminal()
    {
        var tracker = NewTracker();
        StartWrite(tracker);
        var before = tracker.Snapshot();

        Assert.Equal(
            GuardianCancellationClaimResult.IdentityMismatch,
            Cancel(tracker, Identity(requestId: 12)));
        Assert.Equal(before, tracker.Snapshot());
        GuardianPrivateRequestIdentity? signaledIdentity = null;
        Assert.Equal(
            GuardianCancellationClaimResult.SignalOriginalRequest,
            Cancel(tracker, signal: identity => signaledIdentity = identity));
        Assert.Equal(Identity(), signaledIdentity);
        Assert.Equal(
            GuardianCancellationClaimResult.AlreadySignaled,
            Cancel(tracker));
        Assert.Equal(
            GuardianLocalTerminalResult.WriteAlreadyStarted,
            tracker.TrySetLocalTerminal("cancel-owned-terminal"));

        Assert.Equal(
            GuardianTerminalCorrelationResult.Accepted,
            tracker.TryDecodeTerminal(Identity(), "host-terminal"));
        Assert.Equal(
            GuardianCancellationClaimResult.TerminalAlreadyKnown,
            Cancel(tracker));
        Assert.Equal("host-terminal", AssertDelivered(tracker));
    }

    [Fact]
    public async Task Cancellation_and_first_write_race_has_no_unowned_before_dispatch_gap()
    {
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var tracker = NewTracker();
            using var raceGate = new ManualResetEventSlim();
            var firstWriteCount = 0;
            var cancellationSignalCount = 0;

            var writeTask = Task.Run(() =>
            {
                raceGate.Wait();
                try
                {
                    tracker.BeginFirstWriteAsync((identity, _) =>
                    {
                        Assert.Equal(Identity(), identity);
                        Interlocked.Increment(ref firstWriteCount);
                        return ValueTask.CompletedTask;
                    }).GetAwaiter().GetResult();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });
            var cancelTask = Task.Run(async () =>
            {
                raceGate.Wait();
                return await tracker.TryCancelAsync(
                    Identity(),
                    "canceled-before-dispatch",
                    (identity, _) =>
                    {
                        Assert.Equal(Identity(), identity);
                        Interlocked.Increment(ref cancellationSignalCount);
                        return ValueTask.CompletedTask;
                    });
            });

            raceGate.Set();
            var writeWon = await writeTask;
            var cancelResult = await cancelTask;
            var snapshot = tracker.Snapshot();

            if (cancelResult == GuardianCancellationClaimResult.BeforeDispatchTerminalInstalled)
            {
                Assert.False(writeWon);
                Assert.Equal(0, firstWriteCount);
                Assert.Equal(0, cancellationSignalCount);
                Assert.Equal(GuardianPublicDeliveryState.TerminalDecoded, snapshot.State);
                Assert.True(snapshot.HasRetainedTerminal);
                Assert.Equal("canceled-before-dispatch", AssertDelivered(tracker));
            }
            else
            {
                Assert.Equal(GuardianCancellationClaimResult.SignalOriginalRequest, cancelResult);
                Assert.True(writeWon);
                Assert.Equal(1, firstWriteCount);
                Assert.Equal(1, cancellationSignalCount);
                Assert.Equal(GuardianPublicDeliveryState.WriteStarted, snapshot.State);
                Assert.True(snapshot.CancellationSignaled);
                Assert.False(snapshot.HasRetainedTerminal);
            }
        }
    }

    [Fact]
    public async Task After_write_cancellation_callback_owns_stop_and_loss_boundary()
    {
        var tracker = NewTracker();
        StartWrite(tracker);
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var stopAttempted = new ManualResetEventSlim();
        using var lossAttempted = new ManualResetEventSlim();
        GuardianPrivateRequestIdentity? callbackIdentity = null;

        var cancellationTask = Task.Run(() => tracker.TryCancelAsync(
            Identity(),
            "unused-before-dispatch-terminal",
            (identity, _) =>
            {
                callbackIdentity = identity;
                Assert.True(tracker.Snapshot().CancellationSignaled);
                callbackEntered.Set();
                Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(5)));
                return ValueTask.CompletedTask;
            }).AsTask());

        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
        var stopTask = Task.Run(() =>
        {
            stopAttempted.Set();
            return tracker.Stop();
        });
        var lossTask = Task.Run(() =>
        {
            lossAttempted.Set();
            return tracker.ObserveHostLoss(HostA, new HostGeneration(7));
        });
        Assert.True(stopAttempted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(lossAttempted.Wait(TimeSpan.FromSeconds(5)));

        await Task.Delay(50);
        Assert.False(stopTask.IsCompleted);
        Assert.False(lossTask.IsCompleted);

        releaseCallback.Set();
        Assert.Equal(
            GuardianCancellationClaimResult.SignalOriginalRequest,
            await cancellationTask);
        Assert.Equal(Identity(), callbackIdentity);
        Assert.True(await stopTask);
        Assert.Equal(
            GuardianHostLossDisposition.OutcomeUnknown,
            (await lossTask).Disposition);
    }

    [Fact]
    public async Task Failed_cancellation_signal_is_marked_once_and_never_retried()
    {
        var tracker = NewTracker();
        StartWrite(tracker);
        var callbackCount = 0;

        await Assert.ThrowsAsync<IOException>(async () =>
            await tracker.TryCancelAsync(
                Identity(),
                "unused-before-dispatch-terminal",
                (_, _) =>
                {
                    Interlocked.Increment(ref callbackCount);
                    return new ValueTask(Task.FromException(
                        new IOException("Injected cancellation signal failure.")));
                }));

        Assert.Equal(1, callbackCount);
        Assert.True(tracker.Snapshot().CancellationSignaled);
        Assert.Equal(
            GuardianCancellationClaimResult.AlreadySignaled,
            Cancel(tracker, signal: _ => Interlocked.Increment(ref callbackCount)));
        Assert.Equal(1, callbackCount);

        Assert.Equal(
            GuardianTerminalCorrelationResult.Accepted,
            tracker.TryDecodeTerminal(Identity(), "authoritative-host-terminal"));
        Assert.Equal("authoritative-host-terminal", AssertDelivered(tracker));
    }

    [Fact]
    public void Stop_blocks_new_transitions_but_preserves_a_predecoded_terminal_for_one_delivery()
    {
        var beforeWrite = NewTracker();
        Assert.True(beforeWrite.Stop());
        Assert.False(beforeWrite.Stop());
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = beforeWrite.BeginFirstWriteAsync((_, _) => ValueTask.CompletedTask);
        });
        Assert.Equal(
            GuardianLocalTerminalResult.Stopped,
            beforeWrite.TrySetLocalTerminal("local"));
        Assert.Equal(
            GuardianTerminalCorrelationResult.Stopped,
            beforeWrite.TryDecodeTerminal(Identity(), "private"));
        Assert.Equal(
            GuardianCancellationClaimResult.Stopped,
            Cancel(beforeWrite));

        var afterWrite = NewTracker();
        StartWrite(afterWrite);
        Assert.True(afterWrite.Stop());
        Assert.Equal(
            GuardianTerminalCorrelationResult.Stopped,
            afterWrite.TryDecodeTerminal(Identity(), "late-private"));
        Assert.Equal(
            GuardianHostLossDisposition.OutcomeUnknown,
            afterWrite.ObserveHostLoss(HostA, new HostGeneration(7)).Disposition);

        var decoded = NewTracker();
        StartWrite(decoded);
        Assert.Equal(
            GuardianTerminalCorrelationResult.Accepted,
            decoded.TryDecodeTerminal(Identity(), "authoritative"));
        Assert.True(decoded.Stop());
        Assert.Equal("authoritative", AssertDelivered(decoded));
        Assert.Equal(
            GuardianPublicTerminalDeliveryResult.AlreadySent,
            DeliverWithoutWriting(decoded));
    }

    [Fact]
    public void Stop_precedes_all_terminal_correlation_checks_and_mutations()
    {
        var writeStarted = NewTracker();
        StartWrite(writeStarted);
        Assert.True(writeStarted.Stop());
        var stoppedWrite = writeStarted.Snapshot();

        Assert.Equal(
            GuardianTerminalCorrelationResult.Stopped,
            writeStarted.TryDecodeTerminal(
                Identity(hostBootId: HostB, generation: 99, requestId: 999),
                "mismatched-after-stop"));
        Assert.Equal(stoppedWrite, writeStarted.Snapshot());
        Assert.False(writeStarted.Snapshot().CorrelationFatal);

        var decoded = NewTracker();
        StartWrite(decoded);
        Assert.Equal(
            GuardianTerminalCorrelationResult.Accepted,
            decoded.TryDecodeTerminal(Identity(), "authoritative"));
        Assert.True(decoded.Stop());
        var stoppedDecoded = decoded.Snapshot();

        Assert.Equal(
            GuardianTerminalCorrelationResult.Stopped,
            decoded.TryDecodeTerminal(Identity(), "duplicate-after-stop"));
        Assert.Equal(stoppedDecoded, decoded.Snapshot());
        Assert.False(decoded.Snapshot().CorrelationFatal);
        Assert.Equal("authoritative", AssertDelivered(decoded));
    }

    [Fact]
    public void Tracker_scope_is_three_part_and_defers_deeper_correlation_to_private_client()
    {
        var correlationProperties = typeof(GuardianPrivateRequestIdentity)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Where(property => property.GetMethod?.IsAssembly == true)
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            ["HostBootId", "HostGeneration", "RequestId"],
            correlationProperties);
    }

    [Fact]
    public async Task Stop_races_cannot_start_work_after_stop_or_erase_a_winning_local_terminal()
    {
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var local = NewTracker();
            using var localGate = new ManualResetEventSlim();
            var stopTask = Task.Run(() =>
            {
                localGate.Wait();
                return local.Stop();
            });
            var localTask = Task.Run(() =>
            {
                localGate.Wait();
                return local.TrySetLocalTerminal("local");
            });
            localGate.Set();
            Assert.True(await stopTask);
            var localResult = await localTask;
            Assert.True(local.Snapshot().Stopped);
            if (localResult == GuardianLocalTerminalResult.Accepted)
                Assert.Equal("local", AssertDelivered(local));
            else
                Assert.Equal(GuardianLocalTerminalResult.Stopped, localResult);

            var write = NewTracker();
            using var writeGate = new ManualResetEventSlim();
            var callbackCount = 0;
            var writeTask = Task.Run(() =>
            {
                writeGate.Wait();
                try
                {
                    _ = write.BeginFirstWriteAsync((_, _) =>
                    {
                        Interlocked.Increment(ref callbackCount);
                        return ValueTask.CompletedTask;
                    });
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });
            var writeStopTask = Task.Run(() =>
            {
                writeGate.Wait();
                return write.Stop();
            });
            writeGate.Set();
            var writeWon = await writeTask;
            Assert.True(await writeStopTask);
            Assert.Equal(writeWon ? 1 : 0, Volatile.Read(ref callbackCount));
            Assert.Equal(
                writeWon
                    ? GuardianPublicDeliveryState.WriteStarted
                    : GuardianPublicDeliveryState.NotDispatched,
                write.Snapshot().State);
            Assert.True(write.Snapshot().Stopped);
        }
    }

    [Fact]
    public void Snapshot_reads_are_inert_in_every_observed_phase()
    {
        var tracker = NewTracker();
        AssertRepeatedSnapshot(tracker);

        StartWrite(tracker);
        AssertRepeatedSnapshot(tracker);

        Assert.Equal(
            GuardianTerminalCorrelationResult.Accepted,
            tracker.TryDecodeTerminal(Identity(), "terminal"));
        AssertRepeatedSnapshot(tracker);

        _ = tracker.ObserveHostLoss(HostA, new HostGeneration(7));
        AssertRepeatedSnapshot(tracker);

        Assert.Equal("terminal", AssertDelivered(tracker));
        AssertRepeatedSnapshot(tracker);
    }

    private static GuardianCallDeliveryTracker<string> NewTracker() => new(Identity());

    private static GuardianPrivateRequestIdentity Identity(
        HostBootId? hostBootId = null,
        long generation = 7,
        long requestId = 11) =>
        new(hostBootId ?? HostA, new HostGeneration(generation), new PrivateRequestId(requestId));

    private static void StartWrite(GuardianCallDeliveryTracker<string> tracker) =>
        tracker.BeginFirstWriteAsync((identity, _) =>
        {
            Assert.Equal(tracker.Identity, identity);
            return ValueTask.CompletedTask;
        }).GetAwaiter().GetResult();

    private static string AssertDelivered(GuardianCallDeliveryTracker<string> tracker)
    {
        string? delivered = null;
        var result = tracker.DeliverPublicTerminalAsync((terminal, _) =>
        {
            delivered = terminal;
            return ValueTask.CompletedTask;
        }).GetAwaiter().GetResult();
        Assert.Equal(GuardianPublicTerminalDeliveryResult.Sent, result);
        return Assert.IsType<string>(delivered);
    }

    private static GuardianPublicTerminalDeliveryResult DeliverWithoutWriting(
        GuardianCallDeliveryTracker<string> tracker) =>
        tracker.DeliverPublicTerminalAsync((_, _) => ValueTask.CompletedTask)
            .GetAwaiter()
            .GetResult();

    private static GuardianCancellationClaimResult Cancel(
        GuardianCallDeliveryTracker<string> tracker,
        GuardianPrivateRequestIdentity? identity = null,
        Action<GuardianPrivateRequestIdentity>? signal = null) =>
        tracker.TryCancelAsync(
                identity ?? Identity(),
                "canceled-before-dispatch",
                (boundIdentity, _) =>
                {
                    signal?.Invoke(boundIdentity);
                    return ValueTask.CompletedTask;
                })
            .GetAwaiter()
            .GetResult();

    private static void AssertLoss(
        GuardianCallDeliveryTracker<string> tracker,
        GuardianHostLossDisposition expected,
        GuardianPublicDeliveryState expectedState)
    {
        var first = tracker.ObserveHostLoss(HostA, new HostGeneration(7));
        Assert.Equal(expected, first.Disposition);
        Assert.True(first.IsFirstObservation);
        Assert.Equal(expectedState, tracker.Snapshot().State);

        var afterFirst = tracker.Snapshot();
        var duplicate = tracker.ObserveHostLoss(HostA, new HostGeneration(7));
        Assert.Equal(expected, duplicate.Disposition);
        Assert.False(duplicate.IsFirstObservation);
        Assert.Equal(afterFirst, tracker.Snapshot());
    }

    private static void AssertRepeatedSnapshot(GuardianCallDeliveryTracker<string> tracker)
    {
        var expected = tracker.Snapshot();
        for (var iteration = 0; iteration < 256; iteration++)
            Assert.Equal(expected, tracker.Snapshot());
    }
}
