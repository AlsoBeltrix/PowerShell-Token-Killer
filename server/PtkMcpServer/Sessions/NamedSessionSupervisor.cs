using System.Text.RegularExpressions;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Sessions;

internal enum NamedSessionState
{
    Cold,
    Starting,
    Ready,
    Recovering,
    Faulted,
    Closing,
    Closed,
}

internal sealed record NamedSessionSnapshot(
    string Name,
    Guid Identity,
    NamedSessionState State,
    int? WorkerProcessId,
    bool Active,
    bool WarmStateLost,
    string? LastFailure,
    bool ResetRequired);

internal sealed record NamedSessionInvokeResult(
    WorkerResult Result,
    OutputRecoverySummary? OutputRecovery);

internal sealed class NamedSessionException : InvalidOperationException
{
    internal NamedSessionException(string detailCode, string message)
        : base(message)
    {
        DetailCode = detailCode;
    }

    internal string DetailCode { get; }
}

/// <summary>
/// Connection-owned, fixed-bound named-session registry. Public tools are not
/// wired to it until Slice 6; Slice 5 freezes lifecycle and isolation behind
/// this internal boundary.
/// </summary>
internal sealed class NamedSessionSupervisor : IAsyncDisposable
{
    internal const string DefaultName = "default";
    internal const int MaximumSessions = 8;
    private static readonly TimeSpan OutputStorageWait =
        TimeSpan.FromSeconds(5);

    private static readonly Regex ValidName = new(
        "^[a-z0-9][a-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionSlot> _slots =
        new(StringComparer.Ordinal);
    private readonly Func<ISessionWorkerFactory> _createWorkerFactory;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _containmentGrace;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _shutdownComplete = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private int _disposed;

    internal NamedSessionSupervisor(
        Func<ISessionWorkerFactory> createWorkerFactory,
        TimeSpan startupTimeout,
        TimeSpan containmentGrace)
    {
        _createWorkerFactory = createWorkerFactory ??
            throw new ArgumentNullException(nameof(createWorkerFactory));
        if (startupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        if (containmentGrace <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(containmentGrace));
        _startupTimeout = startupTimeout;
        _containmentGrace = containmentGrace;
        _slots.Add(DefaultName, CreateSlot(DefaultName));
    }

    internal NamedSessionSnapshot[] List()
    {
        lock (_gate)
        {
            return [..
                _slots.Values
                    .OrderBy(slot => slot.Name, StringComparer.Ordinal)
                    .Select(SnapshotLocked)];
        }
    }

    internal Task<NamedSessionSnapshot> OpenAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        OpenCoreAsync(
            ValidateName(name),
            allowNonDefaultCreate: true,
            cancellationToken);

    internal async Task<NamedSessionInvokeResult> InvokeAsync(
        string name,
        string script,
        bool raw,
        WorkerInvokeRoute route,
        int timeoutSeconds,
        OutputStore? outputStore,
        CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await EnsureReadyForUseAsync(name, cancellationToken)
            .ConfigureAwait(false);

        var lease = await AcquireOperationAsync(name, cancellationToken)
            .ConfigureAwait(false);
        var artifact = outputStore is null
            ? null
            : new WorkerArtifactRequest(
                Guid.NewGuid(),
                Math.Min(
                    outputStore.MaximumArtifactBytes,
                    WorkerOperationProtocol.MaximumArtifactBytes));
        var beginRecovery = false;
        var recoveryReason = WorkerContainmentReason.LaunchFailure;
        try
        {
            var invocation = await lease.Worker.InvokeAsync(
                script,
                raw,
                route,
                timeoutSeconds,
                artifact,
                cancellationToken).ConfigureAwait(false);
            OutputRecoverySummary? outputRecovery = null;
            if (artifact is not null &&
                invocation.ArtifactId == artifact.ArtifactId &&
                invocation.ArtifactContent is { } content)
            {
                using var capture = new ForegroundOutputCapture(
                    outputStore!,
                    sessionAlias: lease.Slot.Identity.ToString("N"));
                await capture.PrepareAsync(
                    OutputStorageWait,
                    CancellationToken.None).ConfigureAwait(false);
                outputRecovery = await capture.SealAsync(
                    content,
                    OutputStorageWait).ConfigureAwait(false);
            }
            else if (invocation.ArtifactId is not null ||
                     invocation.ArtifactContent is not null)
            {
                throw new WorkerProtocolException(
                    "artifact_identity_mismatch",
                    "Worker output does not match the reserved artifact.");
            }

            if (invocation.Result.Status == WorkerResultStatus.TimedOut)
            {
                lock (_gate)
                {
                    beginRecovery = BeginAutomaticRecoveryLocked(
                        lease.Slot,
                        lease.Worker,
                        lease.Incarnation,
                        "execution_timed_out");
                }
                recoveryReason = WorkerContainmentReason.Timeout;
            }
            return new NamedSessionInvokeResult(
                invocation.Result,
                outputRecovery);
        }
        catch (Exception exception)
        {
            if (!IsFatal(exception) &&
                !lease.Worker.IsTransportUsable)
            {
                lock (_gate)
                {
                    beginRecovery = BeginAutomaticRecoveryLocked(
                        lease.Slot,
                        lease.Worker,
                        lease.Incarnation,
                        "worker_transport_failed");
                }
            }
            throw;
        }
        finally
        {
            lease.Dispose();
            if (beginRecovery)
            {
                _ = RecoverSlotAsync(
                    lease.Slot,
                    lease.Worker,
                    lease.Incarnation,
                    recoveryReason);
            }
        }
    }

    internal async Task<WorkerStateSnapshot> StateAsync(
        string name,
        bool listAvailable,
        CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        cancellationToken.ThrowIfCancellationRequested();
        OperationLease lease;
        lock (_gate)
        {
            ThrowIfClosedLocked();
            if (!_slots.TryGetValue(name, out var slot))
                throw Unknown(name);
            if (slot.State != NamedSessionState.Ready)
            {
                return new WorkerStateSnapshot(
                    RequestId: 1,
                    Available: false,
                    Text: string.Empty,
                    DetailCode: StateReason(slot.State));
            }
            if (slot.ActiveOperations != 0)
            {
                return new WorkerStateSnapshot(
                    RequestId: 1,
                    Available: false,
                    Text: string.Empty,
                    DetailCode: "session_busy");
            }
            if (slot.Worker is null ||
                !slot.Foreground.Wait(0, CancellationToken.None))
            {
                throw new InvalidOperationException(
                    "An idle ready session must own an available foreground gate.");
            }
            slot.ActiveOperations = 1;
            slot.Idle = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lease = new OperationLease(
                this,
                slot,
                slot.Worker,
                slot.Incarnation);
        }

        var beginRecovery = false;
        try
        {
            return await lease.Worker.StateAsync(
                listAvailable,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!IsFatal(exception) &&
                !lease.Worker.IsTransportUsable)
            {
                lock (_gate)
                {
                    beginRecovery = BeginAutomaticRecoveryLocked(
                        lease.Slot,
                        lease.Worker,
                        lease.Incarnation,
                        "worker_transport_failed");
                }
            }
            throw;
        }
        finally
        {
            lease.Dispose();
            if (beginRecovery)
            {
                _ = RecoverSlotAsync(
                    lease.Slot,
                    lease.Worker,
                    lease.Incarnation,
                    WorkerContainmentReason.LaunchFailure);
            }
        }
    }

    internal Task<NamedSessionSnapshot> ResetAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        SessionSlot slot;
        ISessionWorker? worker;
        long incarnation;
        TaskCompletionSource<NamedSessionSnapshot> completion;
        lock (_gate)
        {
            ThrowIfClosedLocked();
            if (!_slots.TryGetValue(name, out slot!))
                throw Unknown(name);
            RequireIdleLocked(slot);
            if (slot.State is NamedSessionState.Starting or
                NamedSessionState.Recovering or
                NamedSessionState.Closing)
            {
                throw Busy(name);
            }
            RequireContainmentConfirmedLocked(slot);

            worker = slot.Worker;
            incarnation = slot.Incarnation;
            slot.State = NamedSessionState.Recovering;
            slot.LastFailure = null;
            completion = NewTransition(slot);
        }

        _ = ReplaceSlotAsync(
            slot,
            worker,
            incarnation,
            WorkerContainmentReason.Reset,
            completion,
            CancellationToken.None);
        return completion.Task.WaitAsync(cancellationToken);
    }

    internal Task CloseAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        if (name == DefaultName)
        {
            throw new NamedSessionException(
                "default_session_required",
                "The default session exists for the connection lifetime.");
        }

        SessionSlot slot;
        ISessionWorker? worker;
        long incarnation;
        TaskCompletionSource<NamedSessionSnapshot> completion;
        lock (_gate)
        {
            ThrowIfClosedLocked();
            if (!_slots.TryGetValue(name, out slot!))
                throw Unknown(name);
            RequireIdleLocked(slot);
            if (slot.State is NamedSessionState.Starting or
                NamedSessionState.Recovering or
                NamedSessionState.Closing)
            {
                throw Busy(name);
            }
            RequireContainmentConfirmedLocked(slot);

            worker = slot.Worker;
            incarnation = slot.Incarnation;
            slot.State = NamedSessionState.Closing;
            completion = NewTransition(slot);
        }

        _ = CloseSlotAsync(
            slot,
            worker,
            incarnation,
            completion,
            CancellationToken.None);
        return completion.Task.WaitAsync(cancellationToken);
    }

    internal async Task ShutdownAsync()
    {
        SessionSlot[]? slots = null;
        Task? existingShutdown = null;
        lock (_gate)
        {
            if (_closed)
            {
                existingShutdown = _shutdownComplete.Task;
            }
            else
            {
                _closed = true;
                slots = [.. _slots.Values];
                foreach (var slot in slots)
                    slot.State = NamedSessionState.Closing;
            }
        }

        if (existingShutdown is not null)
        {
            await existingShutdown.ConfigureAwait(false);
            return;
        }

        try
        {
            _shutdown.Cancel();
            await Task.WhenAll(slots!.Select(ShutdownSlotAsync))
                .ConfigureAwait(false);
            lock (_gate)
                _slots.Clear();
            _shutdownComplete.TrySetResult();
        }
        catch (Exception exception)
        {
            _shutdownComplete.TrySetException(exception);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task<NamedSessionSnapshot> EnsureReadyForUseAsync(
        string name,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfClosedLocked();
            if (!_slots.TryGetValue(name, out var slot))
                throw Unknown(name);
            if (slot.State == NamedSessionState.Ready)
                return SnapshotLocked(slot);
            if (name != DefaultName || slot.State != NamedSessionState.Cold)
                throw NotReady(slot);
        }

        return await OpenCoreAsync(
            name,
            allowNonDefaultCreate: false,
            cancellationToken).ConfigureAwait(false);
    }

    private Task<NamedSessionSnapshot> OpenCoreAsync(
        string name,
        bool allowNonDefaultCreate,
        CancellationToken cancellationToken)
    {
        SessionSlot slot;
        TaskCompletionSource<NamedSessionSnapshot>? completion = null;
        var removeOnPrelaunchFailure = false;
        lock (_gate)
        {
            ThrowIfClosedLocked();
            if (!_slots.TryGetValue(name, out slot!))
            {
                if (!allowNonDefaultCreate || name == DefaultName)
                    throw Unknown(name);
                if (_slots.Count >= MaximumSessions)
                {
                    throw new NamedSessionException(
                        "session_capacity_exceeded",
                        $"A connection admits at most {MaximumSessions} sessions.");
                }
                slot = CreateSlot(name);
                _slots.Add(name, slot);
                removeOnPrelaunchFailure = true;
            }

            switch (slot.State)
            {
                case NamedSessionState.Ready:
                    return Task.FromResult(SnapshotLocked(slot));
                case NamedSessionState.Starting:
                    return slot.Transition!.Task.WaitAsync(cancellationToken);
                case NamedSessionState.Cold:
                    slot.State = NamedSessionState.Starting;
                    completion = NewTransition(slot);
                    break;
                case NamedSessionState.Faulted:
                    throw new NamedSessionException(
                        "session_reset_required",
                        $"Session '{name}' is faulted and requires explicit reset.");
                default:
                    throw Busy(name);
            }
        }

        _ = StartSlotAsync(
            slot,
            slot.Incarnation,
            completion!,
            removeOnPrelaunchFailure,
            clearFailureOnSuccess: true);
        return completion!.Task.WaitAsync(cancellationToken);
    }

    private async Task StartSlotAsync(
        SessionSlot slot,
        long incarnation,
        TaskCompletionSource<NamedSessionSnapshot> completion,
        bool removeOnPrelaunchFailure,
        bool clearFailureOnSuccess)
    {
        ISessionWorker? worker = null;
        try
        {
            using var deadline =
                CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            deadline.CancelAfter(_startupTimeout);
            slot.WorkerFactory ??= CreateWorkerFactory();
            worker = await slot.WorkerFactory.StartAsync(
                slot.Identity,
                incarnation,
                DateTimeOffset.UtcNow + _startupTimeout,
                deadline.Token).ConfigureAwait(false);

            NamedSessionSnapshot snapshot;
            var accepted = false;
            lock (_gate)
            {
                if (!_closed &&
                    _slots.TryGetValue(slot.Name, out var current) &&
                    ReferenceEquals(current, slot) &&
                    slot.Incarnation == incarnation &&
                    slot.State is NamedSessionState.Starting or
                        NamedSessionState.Recovering)
                {
                    slot.Worker = worker;
                    slot.WorkerProcessId = worker.ProcessId;
                    slot.State = NamedSessionState.Ready;
                    if (clearFailureOnSuccess)
                        slot.LastFailure = null;
                    slot.ContainmentEmpty = null;
                    slot.Transition = null;
                    snapshot = SnapshotLocked(slot);
                    accepted = true;
                }
                else
                {
                    snapshot = SnapshotLocked(slot);
                }
            }

            if (!accepted)
            {
                using var cleanup = new CancellationTokenSource(_containmentGrace);
                _ = await worker.StopAsync(
                    WorkerContainmentReason.SupervisorShutdown,
                    cleanup.Token).ConfigureAwait(false);
                await worker.DisposeAsync().ConfigureAwait(false);
                completion.TrySetException(
                    new NamedSessionException(
                        "stale_session_transition",
                        "A late worker start cannot mutate a replaced session."));
                return;
            }

            completion.TrySetResult(snapshot);
            _ = ObserveWorkerFailureAsync(slot, worker, incarnation);
            worker = null;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var start = exception as SessionWorkerStartException;
            lock (_gate)
            {
                if (_slots.TryGetValue(slot.Name, out var current) &&
                    ReferenceEquals(current, slot) &&
                    slot.Incarnation == incarnation)
                {
                    if (removeOnPrelaunchFailure &&
                        start is { ProcessLaunched: false })
                    {
                        _slots.Remove(slot.Name);
                        slot.State = NamedSessionState.Closed;
                    }
                    else if (slot.Name == DefaultName &&
                             start is { ProcessLaunched: false })
                    {
                        slot.State = NamedSessionState.Cold;
                    }
                    else
                    {
                        slot.State = NamedSessionState.Faulted;
                    }
                    slot.LastFailure = StartFailureCode(exception);
                    slot.ContainmentEmpty = start?.Containment?.Outcome ==
                        WorkerContainmentOutcome.DescendantsUnknown
                            ? start.ContainmentEmpty
                            : null;
                    slot.Transition = null;
                    if (slot.ContainmentEmpty is { } observer)
                        ObserveContainment(slot, incarnation, observer);
                }
            }
            completion.TrySetException(
                new NamedSessionException(
                    StartFailureCode(exception),
                    $"Session '{slot.Name}' did not start."));
        }
        finally
        {
            if (worker is not null)
                await worker.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReplaceSlotAsync(
        SessionSlot slot,
        ISessionWorker? worker,
        long incarnation,
        WorkerContainmentReason reason,
        TaskCompletionSource<NamedSessionSnapshot> completion,
        CancellationToken cancellationToken)
    {
        if (!await StopOldWorkerAsync(
                slot,
                worker,
                incarnation,
                reason,
                completion,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        lock (_gate)
        {
            if (!_slots.TryGetValue(slot.Name, out var current) ||
                !ReferenceEquals(current, slot) ||
                slot.Incarnation != incarnation ||
                slot.State != NamedSessionState.Recovering)
            {
                completion.TrySetException(
                    new NamedSessionException(
                        "stale_session_transition",
                        "A stale reset cannot replace a newer session."));
                return;
            }
            slot.Worker = null;
            slot.WorkerProcessId = null;
            slot.Incarnation++;
            slot.WarmStateLost = true;
        }

        await StartSlotAsync(
            slot,
            slot.Incarnation,
            completion,
            removeOnPrelaunchFailure: false,
            clearFailureOnSuccess:
                reason == WorkerContainmentReason.Reset).ConfigureAwait(false);
    }

    private async Task RecoverSlotAsync(
        SessionSlot slot,
        ISessionWorker worker,
        long incarnation,
        WorkerContainmentReason reason)
    {
        await WaitUntilIdleAsync(slot).ConfigureAwait(false);
        TaskCompletionSource<NamedSessionSnapshot> completion;
        lock (_gate)
        {
            if (!_slots.TryGetValue(slot.Name, out var current) ||
                !ReferenceEquals(current, slot) ||
                slot.Worker != worker ||
                slot.Incarnation != incarnation ||
                slot.State != NamedSessionState.Recovering)
            {
                return;
            }
            completion = slot.Transition ??
                throw new InvalidOperationException(
                    "Recovering session has no transition owner.");
        }

        await ReplaceSlotAsync(
            slot,
            worker,
            incarnation,
            reason,
            completion,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> StopOldWorkerAsync(
        SessionSlot slot,
        ISessionWorker? worker,
        long incarnation,
        WorkerContainmentReason reason,
        TaskCompletionSource<NamedSessionSnapshot> completion,
        CancellationToken cancellationToken)
    {
        if (worker is null)
            return true;

        WorkerContainmentResult containment;
        try
        {
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            grace.CancelAfter(_containmentGrace);
            containment = await worker.StopAsync(reason, grace.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            containment = WorkerContainmentResult.Unknown(
                "worker_containment_unconfirmed");
        }

        var containmentEmpty = worker.ContainmentEmpty;
        if (containmentEmpty.IsCompletedSuccessfully)
        {
            containment = WorkerContainmentResult.Confirmed();
        }
        else if (containment.Outcome == WorkerContainmentOutcome.ConfirmedEmpty)
        {
            containment = WorkerContainmentResult.Unknown(
                "worker_containment_proof_pending");
        }

        if (containment.Outcome == WorkerContainmentOutcome.DescendantsUnknown)
        {
            lock (_gate)
            {
                if (_slots.TryGetValue(slot.Name, out var current) &&
                    ReferenceEquals(current, slot) &&
                    slot.Incarnation == incarnation)
                {
                    slot.State = NamedSessionState.Faulted;
                    slot.WarmStateLost = true;
                    slot.LastFailure = containment.DetailCode;
                    slot.WorkerProcessId = null;
                    slot.ContainmentEmpty = containmentEmpty;
                    slot.Transition = null;
                    ObserveContainment(
                        slot,
                        incarnation,
                        containmentEmpty);
                }
            }
            completion.TrySetException(
                new NamedSessionException(
                    "descendants_unknown",
                    $"Session '{slot.Name}' containment is unconfirmed."));
            return false;
        }

        await worker.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    private async Task CloseSlotAsync(
        SessionSlot slot,
        ISessionWorker? worker,
        long incarnation,
        TaskCompletionSource<NamedSessionSnapshot> completion,
        CancellationToken cancellationToken)
    {
        if (!await StopOldWorkerAsync(
                slot,
                worker,
                incarnation,
                WorkerContainmentReason.Close,
                completion,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        NamedSessionSnapshot snapshot;
        lock (_gate)
        {
            if (_slots.TryGetValue(slot.Name, out var current) &&
                ReferenceEquals(current, slot) &&
                slot.Incarnation == incarnation &&
                slot.State == NamedSessionState.Closing)
            {
                _slots.Remove(slot.Name);
                slot.State = NamedSessionState.Closed;
                slot.Worker = null;
                slot.WorkerProcessId = null;
                slot.Transition = null;
            }
            snapshot = SnapshotLocked(slot);
        }
        completion.TrySetResult(snapshot);
    }

    private async Task ShutdownSlotAsync(SessionSlot slot)
    {
        ISessionWorker? worker;
        Task? transition;
        lock (_gate)
        {
            worker = slot.Worker;
            transition = slot.Transition?.Task;
        }
        if (worker is not null)
        {
            try
            {
                using var grace = new CancellationTokenSource(_containmentGrace);
                _ = await worker.StopAsync(
                    WorkerContainmentReason.SupervisorShutdown,
                    grace.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
            await worker.DisposeAsync().ConfigureAwait(false);
        }

        if (transition is not null)
        {
            try
            {
                await transition.ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
        }

        ISessionWorker? lateWorker;
        lock (_gate)
            lateWorker = slot.Worker;
        if (lateWorker is not null &&
            !ReferenceEquals(lateWorker, worker))
        {
            try
            {
                using var grace = new CancellationTokenSource(_containmentGrace);
                _ = await lateWorker.StopAsync(
                    WorkerContainmentReason.SupervisorShutdown,
                    grace.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
            await lateWorker.DisposeAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            slot.Worker = null;
            slot.WorkerProcessId = null;
            slot.State = NamedSessionState.Closed;
            slot.Transition = null;
        }
    }

    private async Task ObserveWorkerFailureAsync(
        SessionSlot slot,
        ISessionWorker worker,
        long incarnation)
    {
        try
        {
            await worker.Fatal.ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }

        var recover = false;
        lock (_gate)
        {
            recover = BeginAutomaticRecoveryLocked(
                slot,
                worker,
                incarnation,
                "worker_lost");
        }
        if (recover)
        {
            _ = RecoverSlotAsync(
                slot,
                worker,
                incarnation,
                WorkerContainmentReason.LaunchFailure);
        }
    }

    private bool BeginAutomaticRecoveryLocked(
        SessionSlot slot,
        ISessionWorker worker,
        long incarnation,
        string failure)
    {
        if (_closed ||
            !_slots.TryGetValue(slot.Name, out var current) ||
            !ReferenceEquals(current, slot) ||
            slot.Worker != worker ||
            slot.Incarnation != incarnation ||
            slot.State != NamedSessionState.Ready)
        {
            return false;
        }
        slot.State = NamedSessionState.Recovering;
        slot.LastFailure = failure;
        _ = NewTransition(slot);
        return true;
    }

    private async Task<OperationLease> AcquireOperationAsync(
        string name,
        CancellationToken cancellationToken)
    {
        SessionSlot slot;
        ISessionWorker worker;
        long incarnation;
        lock (_gate)
        {
            ThrowIfClosedLocked();
            if (!_slots.TryGetValue(name, out slot!))
                throw Unknown(name);
            if (slot.State != NamedSessionState.Ready ||
                slot.Worker is null)
            {
                throw NotReady(slot);
            }
            slot.ActiveOperations++;
            if (slot.ActiveOperations == 1)
            {
                slot.Idle = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            worker = slot.Worker;
            incarnation = slot.Incarnation;
        }

        var semaphoreHeld = false;
        try
        {
            await slot.Foreground.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            semaphoreHeld = true;
            lock (_gate)
            {
                if (_closed ||
                    !_slots.TryGetValue(name, out var current) ||
                    !ReferenceEquals(current, slot) ||
                    slot.State != NamedSessionState.Ready ||
                    slot.Worker != worker ||
                    slot.Incarnation != incarnation)
                {
                    throw NotReady(slot);
                }
            }
            return new OperationLease(
                this,
                slot,
                worker,
                incarnation);
        }
        catch
        {
            ReleaseOperation(slot, semaphoreHeld);
            throw;
        }
    }

    private void ReleaseOperation(SessionSlot slot, bool semaphoreHeld)
    {
        if (semaphoreHeld)
            slot.Foreground.Release();
        lock (_gate)
        {
            if (slot.ActiveOperations <= 0)
                throw new InvalidOperationException(
                    "Session operation lease underflow.");
            slot.ActiveOperations--;
            if (slot.ActiveOperations == 0)
                slot.Idle.TrySetResult();
        }
    }

    private Task WaitUntilIdleAsync(SessionSlot slot)
    {
        lock (_gate)
            return slot.ActiveOperations == 0 ? Task.CompletedTask : slot.Idle.Task;
    }

    private void ObserveContainment(
        SessionSlot slot,
        long incarnation,
        Task observer)
    {
        _ = ObserveAsync();
        async Task ObserveAsync()
        {
            try
            {
                await observer.ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                return;
            }
            lock (_gate)
            {
                if (_slots.TryGetValue(slot.Name, out var current) &&
                    ReferenceEquals(current, slot) &&
                    slot.Incarnation == incarnation &&
                    ReferenceEquals(slot.ContainmentEmpty, observer))
                {
                    slot.ContainmentEmpty = null;
                }
            }
        }
    }

    private SessionSlot CreateSlot(string name) =>
        new(
            name,
            Guid.NewGuid());

    private ISessionWorkerFactory CreateWorkerFactory()
    {
        try
        {
            return _createWorkerFactory() ??
                throw new InvalidOperationException(
                    "The session worker factory provider returned null.");
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new SessionWorkerStartException(
                "worker_factory_failed",
                processLaunched: false,
                containment: null,
                containmentEmpty: null,
                exception);
        }
    }

    private static TaskCompletionSource<NamedSessionSnapshot> NewTransition(
        SessionSlot slot)
    {
        var completion = new TaskCompletionSource<NamedSessionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        slot.Transition = completion;
        _ = IgnoreTransitionFailureAsync(completion.Task);
        return completion;
    }

    private static async Task IgnoreTransitionFailureAsync(Task transition)
    {
        try
        {
            await transition.ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private static NamedSessionSnapshot SnapshotLocked(SessionSlot slot) =>
        new(
            slot.Name,
            slot.Identity,
            slot.State,
            slot.WorkerProcessId,
            slot.ActiveOperations != 0,
            slot.WarmStateLost,
            slot.LastFailure,
            slot.State == NamedSessionState.Faulted);

    private static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!ValidName.IsMatch(name))
        {
            throw new NamedSessionException(
                "invalid_session_name",
                "Session names must be canonical lowercase labels of 1-64 characters.");
        }
        return name;
    }

    private void ThrowIfClosedLocked()
    {
        if (_closed)
        {
            throw new NamedSessionException(
                "supervisor_closed",
                "The owning connection is closed.");
        }
    }

    private static void RequireIdleLocked(SessionSlot slot)
    {
        if (slot.ActiveOperations != 0)
            throw Busy(slot.Name);
    }

    private static void RequireContainmentConfirmedLocked(SessionSlot slot)
    {
        if (slot.ContainmentEmpty is { IsCompletedSuccessfully: false })
        {
            throw new NamedSessionException(
                "descendants_unknown",
                $"Session '{slot.Name}' still owns unconfirmed descendants.");
        }
        if (slot.ContainmentEmpty is { IsFaulted: true })
        {
            throw new NamedSessionException(
                "descendants_unknown",
                $"Session '{slot.Name}' containment observer failed.");
        }
        slot.ContainmentEmpty = null;
    }

    private static NamedSessionException Unknown(string name) =>
        new(
            "session_not_found",
            $"Session '{name}' is not open.");

    private static NamedSessionException Busy(string name) =>
        new(
            "session_busy",
            $"Session '{name}' is busy.");

    private static NamedSessionException NotReady(SessionSlot slot) =>
        new(
            StateReason(slot.State),
            $"Session '{slot.Name}' is not ready.");

    private static string StateReason(NamedSessionState state) => state switch
    {
        NamedSessionState.Cold => "session_cold",
        NamedSessionState.Starting => "session_starting",
        NamedSessionState.Recovering => "session_recovering",
        NamedSessionState.Faulted => "session_faulted",
        NamedSessionState.Closing => "session_closing",
        NamedSessionState.Closed => "session_closed",
        _ => "session_unavailable",
    };

    private static string StartFailureCode(Exception exception) =>
        exception is SessionWorkerStartException start
            ? start.DetailCode
            : exception is OperationCanceledException
                ? "worker_start_timed_out"
                : "worker_start_failed";

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class SessionSlot(
        string name,
        Guid identity)
    {
        internal string Name { get; } = name;
        internal Guid Identity { get; } = identity;
        internal ISessionWorkerFactory? WorkerFactory;
        internal SemaphoreSlim Foreground { get; } = new(1, 1);
        internal NamedSessionState State =
            name == DefaultName ? NamedSessionState.Cold : NamedSessionState.Cold;
        internal long Incarnation = 1;
        internal ISessionWorker? Worker;
        internal int? WorkerProcessId;
        internal int ActiveOperations;
        internal bool WarmStateLost;
        internal string? LastFailure;
        internal Task? ContainmentEmpty;
        internal TaskCompletionSource<NamedSessionSnapshot>? Transition;
        internal TaskCompletionSource Idle { get; set; } =
            CompletedIdle();

        private static TaskCompletionSource CompletedIdle()
        {
            var idle = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            idle.TrySetResult();
            return idle;
        }
    }

    private sealed class OperationLease : IDisposable
    {
        private NamedSessionSupervisor? _owner;

        internal OperationLease(
            NamedSessionSupervisor owner,
            SessionSlot slot,
            ISessionWorker worker,
            long incarnation)
        {
            _owner = owner;
            Slot = slot;
            Worker = worker;
            Incarnation = incarnation;
        }

        internal SessionSlot Slot { get; }
        internal ISessionWorker Worker { get; }
        internal long Incarnation { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?
                .ReleaseOperation(Slot, semaphoreHeld: true);
        }
    }

}
