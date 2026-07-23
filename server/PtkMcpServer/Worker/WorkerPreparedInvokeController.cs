using PtkMcpServer.Sessions;

namespace PtkMcpServer.Worker;

internal sealed record WorkerPreparedRuntimeResult(
    string Text,
    bool UserExecutionStarted);

internal interface IWorkerPreparedInvokeRuntime
{
    Task<WorkerPreparedRuntimeResult> InvokeAsync(
        WorkerInvokePreparePayload prepare,
        IInvocationAuthorizer authorizer,
        CancellationToken cancellationToken);
}

internal interface IWorkerSessionRuntime :
    ISessionLifetime,
    IWorkerPreparedInvokeRuntime,
    IWorkerOperationExecutor
{
}

internal interface IWorkerPreparedInvokeObserver
{
    ValueTask<bool> RecordValidatorStartedAsync(
        WorkerPreparedPlanDescriptor descriptor,
        ExecutionDispatch dispatch,
        CancellationToken cancellationToken);

    ValueTask<bool> RecordValidatorCompletedAsync(
        WorkerPreparedPlanDescriptor descriptor,
        ExecutionDispatch dispatch,
        BashSyntaxValidationResult result,
        CancellationToken cancellationToken);
}

internal enum WorkerPreparedInvokeTerminalKind
{
    Completed,
    ReplanRequired,
    Aborted,
    Expired,
    Canceled,
    Failed,
}

internal sealed record WorkerPreparedInvokeTerminal(
    WorkerPreparedInvokeTerminalKind Kind,
    string? Text,
    string? DetailCode);

/// <summary>
/// Owns prepared foreground-invoke reservations for one worker generation.
/// Planning runs under the runtime gate, but the authorizer does not release
/// that gate until one exact commit. Abort, expiry, correlation mismatch, and
/// shutdown all resolve the same held barrier without user execution.
/// </summary>
internal sealed class WorkerPreparedInvokeController
{
    internal const int MaximumOutstandingReservations = 64;

    private readonly object _gate = new();
    private readonly Guid _workerBootId;
    private readonly long _generation;
    private readonly IWorkerPreparedInvokeRuntime _runtime;
    private readonly IWorkerPreparedInvokeObserver _observer;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<DateTimeOffset, CancellationToken, Task> _waitUntilDeadline;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<Guid, Reservation> _reservations = [];
    private bool _stopped;

    internal WorkerPreparedInvokeController(
        Guid workerBootId,
        long generation,
        IWorkerPreparedInvokeRuntime runtime,
        IWorkerPreparedInvokeObserver observer,
        Func<DateTimeOffset>? utcNow = null,
        Func<DateTimeOffset, CancellationToken, Task>? waitUntilDeadline = null)
    {
        if (workerBootId == Guid.Empty)
            throw new ArgumentException("Worker boot ID cannot be empty.", nameof(workerBootId));
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(observer);

        _workerBootId = workerBootId;
        _generation = generation;
        _runtime = runtime;
        _observer = observer;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _waitUntilDeadline = waitUntilDeadline ?? WaitUntilDeadlineAsync;
    }

    internal async Task<WorkerPreparedPlanDescriptor> PrepareAsync(
        WorkerInvokePreparePayload prepare,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        _ = WorkerPreparedOperationCodec.CreatePrepare(prepare);
        if (prepare.Generation != _generation)
            throw ProtocolFailure(
                "worker_generation_mismatch",
                "Prepared invocation targets a different worker generation.");
        if (_utcNow() >= prepare.DeadlineUtc)
            throw ProtocolFailure(
                "prepared_operation_expired",
                "Prepared invocation deadline already expired.");

        Reservation reservation;
        lock (_gate)
        {
            if (_stopped)
                throw new InvalidOperationException("Prepared invocation scheduling has stopped.");
            if (_reservations.ContainsKey(prepare.PlanId))
            {
                throw ProtocolFailure(
                    "prepared_plan_replay",
                    "Prepared invocation plan IDs cannot be reused.");
            }
            if (_reservations.Count >= MaximumOutstandingReservations)
            {
                throw ProtocolFailure(
                    "prepared_capacity_exceeded",
                    "Prepared invocation reservation capacity is exhausted.");
            }

            reservation = new Reservation(
                _workerBootId,
                prepare,
                _observer,
                _shutdown.Token,
                cancellationToken,
                _waitUntilDeadline);
            _reservations.Add(prepare.PlanId, reservation);
        }

        Task<WorkerPreparedRuntimeResult> invocation;
        try
        {
            invocation = _runtime.InvokeAsync(
                prepare,
                reservation,
                reservation.ExecutionToken) ??
                Task.FromException<WorkerPreparedRuntimeResult>(
                    new InvalidOperationException(
                        "Prepared invocation runtime returned no task."));
        }
        catch (Exception exception)
        {
            invocation = Task.FromException<WorkerPreparedRuntimeResult>(exception);
        }
        reservation.AttachInvocation(invocation);
        return await reservation.Prepared.ConfigureAwait(false);
    }

    internal Task<WorkerPreparedInvokeTerminal> Commit(WorkerCommitPayload commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        Reservation? reservation;
        lock (_gate) _reservations.TryGetValue(commit.PlanId, out reservation);
        if (reservation is null)
        {
            ReplanAllBeforeCommit();
            return ReplanRequired();
        }

        if (_utcNow() >= commit.DeadlineUtc)
        {
            reservation.EndBeforeCommit(
                WorkerPreparedInvokeTerminalKind.Expired,
                "prepared_operation_expired");
        }
        else if (WorkerPreparedOperationCodec.CompareCommitToPrepared(
                reservation.Correlation,
                commit) != WorkerPreparedCorrelationMatch.Match)
        {
            reservation.EndBeforeCommit(
                WorkerPreparedInvokeTerminalKind.ReplanRequired,
                "replan_required");
        }
        else
        {
            reservation.Commit();
        }
        return reservation.Terminal;
    }

    internal Task<WorkerPreparedInvokeTerminal> Abort(WorkerAbortPayload abort)
    {
        ArgumentNullException.ThrowIfNull(abort);
        Reservation? reservation;
        lock (_gate) _reservations.TryGetValue(abort.PlanId, out reservation);
        if (reservation is null)
        {
            ReplanAllBeforeCommit();
            return ReplanRequired();
        }

        if (WorkerPreparedOperationCodec.CompareAbortToPrepared(
                reservation.Correlation,
                abort) != WorkerPreparedCorrelationMatch.Match)
        {
            reservation.EndBeforeCommit(
                WorkerPreparedInvokeTerminalKind.ReplanRequired,
                "replan_required");
        }
        else
        {
            reservation.EndBeforeCommit(
                WorkerPreparedInvokeTerminalKind.Aborted,
                "prepared_operation_aborted");
        }
        return reservation.Terminal;
    }

    internal void Release(Guid planId)
    {
        Reservation? reservation;
        lock (_gate)
        {
            if (!_reservations.TryGetValue(planId, out reservation) ||
                !reservation.Terminal.IsCompleted)
            {
                return;
            }
            _reservations.Remove(planId);
        }
        reservation.Dispose();
    }

    internal async Task CancelAndDrainAsync()
    {
        Reservation[] reservations;
        lock (_gate)
        {
            if (!_stopped)
            {
                _stopped = true;
                _shutdown.Cancel();
            }
            reservations = _reservations.Values.ToArray();
        }

        foreach (var reservation in reservations)
        {
            reservation.EndBeforeCommit(
                WorkerPreparedInvokeTerminalKind.Canceled,
                "prepared_operation_canceled");
        }
        await Task.WhenAll(reservations.Select(value => value.Terminal))
            .ConfigureAwait(false);

        lock (_gate) _reservations.Clear();
        foreach (var reservation in reservations) reservation.Dispose();
        _shutdown.Dispose();
    }

    private static Task<WorkerPreparedInvokeTerminal> ReplanRequired() =>
        Task.FromResult(new WorkerPreparedInvokeTerminal(
            WorkerPreparedInvokeTerminalKind.ReplanRequired,
            Text: null,
            DetailCode: "replan_required"));

    private void ReplanAllBeforeCommit()
    {
        Reservation[] reservations;
        lock (_gate) reservations = _reservations.Values.ToArray();
        foreach (var reservation in reservations)
        {
            reservation.EndBeforeCommit(
                WorkerPreparedInvokeTerminalKind.ReplanRequired,
                "replan_required");
        }
    }

    private static WorkerProtocolException ProtocolFailure(
        string detailCode,
        string message) => new(detailCode, message);

    private static async Task WaitUntilDeadlineAsync(
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        var delay = deadlineUtc - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero) return;
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private enum ReservationState
    {
        Preparing,
        Prepared,
        Committed,
        ReplanRequired,
        Aborted,
        Expired,
        Canceled,
        Failed,
        Completed,
    }

    private sealed class Reservation :
        IInvocationAuthorizer,
        IDisposable
    {
        private readonly object _gate = new();
        private readonly Guid _workerBootId;
        private readonly WorkerInvokePreparePayload _prepare;
        private readonly IWorkerPreparedInvokeObserver _observer;
        private readonly CancellationTokenSource _executionCancellation;
        private readonly CancellationTokenSource _deadlineCancellation = new();
        private readonly CancellationTokenRegistration _executionCancellationRegistration;
        private readonly TaskCompletionSource<WorkerPreparedPlanDescriptor> _prepared =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _commit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WorkerPreparedInvokeTerminal> _terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _deadlineTask;
        private ReservationState _state = ReservationState.Preparing;
        private ExecutionPlan? _plan;
        private WorkerPreparedPlanDescriptor? _descriptor;
        private Task<WorkerPreparedRuntimeResult>? _invocation;
        private int _dispatchCount;
        private ExecutionPath? _firstDispatchPath;
        private string? _terminalDetailCode;
        private int _disposed;

        internal Reservation(
            Guid workerBootId,
            WorkerInvokePreparePayload prepare,
            IWorkerPreparedInvokeObserver observer,
            CancellationToken shutdownToken,
            CancellationToken requestToken,
            Func<DateTimeOffset, CancellationToken, Task> waitUntilDeadline)
        {
            _workerBootId = workerBootId;
            _prepare = prepare;
            _observer = observer;
            _executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                shutdownToken,
                requestToken);
            _executionCancellationRegistration = _executionCancellation.Token.Register(
                static state => ((Reservation)state!).Cancel(),
                this);
            _deadlineTask = ObserveDeadlineAsync(waitUntilDeadline);
        }

        internal CancellationToken ExecutionToken => _executionCancellation.Token;
        internal Task<WorkerPreparedPlanDescriptor> Prepared => _prepared.Task;
        internal Task<WorkerPreparedInvokeTerminal> Terminal => _terminal.Task;

        internal WorkerPreparedCorrelation Correlation => new(
            _prepare.PlanId,
            _prepare.ScriptDigest,
            _prepare.Generation,
            _prepare.DeadlineUtc);

        internal void AttachInvocation(Task<WorkerPreparedRuntimeResult> invocation)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            lock (_gate)
            {
                if (_invocation is not null)
                    throw new InvalidOperationException(
                        "Prepared invocation task was already attached.");
                _invocation = invocation;
            }
            _ = ObserveInvocationAsync(invocation);
        }

        internal void Commit()
        {
            lock (_gate)
            {
                if (_state == ReservationState.Committed ||
                    IsTerminalState(_state))
                {
                    return;
                }
                if (_state != ReservationState.Prepared)
                {
                    EndBeforeCommitUnderLock(
                        WorkerPreparedInvokeTerminalKind.ReplanRequired,
                        "replan_required");
                    return;
                }
                _state = ReservationState.Committed;
                _commit.TrySetResult(true);
            }
        }

        internal void EndBeforeCommit(
            WorkerPreparedInvokeTerminalKind kind,
            string detailCode)
        {
            lock (_gate) EndBeforeCommitUnderLock(kind, detailCode);
        }

        public async ValueTask<bool> AuthorizePlanAsync(
            ExecutionPlan plan,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(plan);
            lock (_gate)
            {
                if (_state != ReservationState.Preparing || _plan is not null)
                    return false;
                try
                {
                    _descriptor =
                        WorkerPreparedOperationCodec.ProjectPreparedDescriptor(
                            _workerBootId,
                            _prepare,
                            plan);
                }
                catch (Exception exception)
                {
                    _state = ReservationState.Failed;
                    _terminalDetailCode = "invalid_prepared_plan";
                    _prepared.TrySetException(exception);
                    _commit.TrySetResult(false);
                    return false;
                }
                _plan = plan;
                _state = ReservationState.Prepared;
                _prepared.TrySetResult(_descriptor);
            }

            return await _commit.Task.ConfigureAwait(false);
        }

        public ValueTask<bool> AuthorizeDispatchAsync(
            ExecutionDispatch dispatch,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(dispatch);
            lock (_gate)
            {
                if (_state != ReservationState.Committed ||
                    _plan is null ||
                    _descriptor is null ||
                    !ReferenceEquals(_plan, dispatch.Plan) ||
                    !IsAuthorizedDispatch(dispatch))
                {
                    MarkReplanRequiredUnderLock();
                    return ValueTask.FromResult(false);
                }
                if (_dispatchCount == 0)
                    _firstDispatchPath = dispatch.ExecutionPath;
                _dispatchCount++;
                return ValueTask.FromResult(true);
            }
        }

        public async ValueTask<bool> RecordValidatorStartedAsync(
            ExecutionDispatch dispatch,
            CancellationToken cancellationToken)
        {
            WorkerPreparedPlanDescriptor descriptor;
            lock (_gate)
            {
                if (_state != ReservationState.Committed ||
                    _descriptor is null ||
                    _plan is null ||
                    !ReferenceEquals(_plan, dispatch.Plan) ||
                    dispatch.ExecutionPath != ExecutionPath.BashViaRtk ||
                    _dispatchCount == 0)
                {
                    MarkReplanRequiredUnderLock();
                    return false;
                }
                descriptor = _descriptor;
            }
            try
            {
                var recorded = await _observer.RecordValidatorStartedAsync(
                    descriptor,
                    dispatch,
                    CancellationToken.None).ConfigureAwait(false);
                if (!recorded) MarkAuthorizationFailure("validator_audit_unavailable");
                return recorded;
            }
            catch (Exception)
            {
                MarkAuthorizationFailure("validator_audit_unavailable");
                return false;
            }
        }

        public async ValueTask<bool> RecordValidatorCompletedAsync(
            ExecutionDispatch dispatch,
            BashSyntaxValidationResult result,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(result);
            WorkerPreparedPlanDescriptor descriptor;
            lock (_gate)
            {
                if (_state != ReservationState.Committed ||
                    _descriptor is null ||
                    _plan is null ||
                    !ReferenceEquals(_plan, dispatch.Plan) ||
                    dispatch.ExecutionPath != ExecutionPath.BashViaRtk ||
                    _dispatchCount == 0)
                {
                    MarkReplanRequiredUnderLock();
                    return false;
                }
                descriptor = _descriptor;
            }
            try
            {
                var recorded = await _observer.RecordValidatorCompletedAsync(
                    descriptor,
                    dispatch,
                    result,
                    CancellationToken.None).ConfigureAwait(false);
                if (!recorded) MarkAuthorizationFailure("validator_audit_unavailable");
                return recorded;
            }
            catch (Exception)
            {
                MarkAuthorizationFailure("validator_audit_unavailable");
                return false;
            }
        }

        private bool IsAuthorizedDispatch(ExecutionDispatch dispatch)
        {
            if (_dispatchCount == 0)
            {
                return dispatch.ExecutionPath == _descriptor!.EffectiveRoute ||
                    dispatch.IsFallback &&
                    _descriptor.PermittedFallbacks.Contains(dispatch.ExecutionPath);
            }
            return _dispatchCount == 1 &&
                _firstDispatchPath == ExecutionPath.Rtk &&
                dispatch.IsFallback &&
                _descriptor!.PermittedFallbacks.Contains(dispatch.ExecutionPath);
        }

        private async Task ObserveDeadlineAsync(
            Func<DateTimeOffset, CancellationToken, Task> waitUntilDeadline)
        {
            try
            {
                await waitUntilDeadline(
                    _prepare.DeadlineUtc,
                    _deadlineCancellation.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_state is ReservationState.Preparing or ReservationState.Prepared)
                    {
                        EndBeforeCommitUnderLock(
                            WorkerPreparedInvokeTerminalKind.Expired,
                            "prepared_operation_expired");
                    }
                }
            }
            catch (OperationCanceledException)
                when (_deadlineCancellation.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    if (_state is ReservationState.Preparing or ReservationState.Prepared)
                    {
                        EndBeforeCommitUnderLock(
                            WorkerPreparedInvokeTerminalKind.Failed,
                            "prepared_deadline_failure");
                    }
                }
            }
        }

        private async Task ObserveInvocationAsync(
            Task<WorkerPreparedRuntimeResult> invocation)
        {
            WorkerPreparedRuntimeResult? result = null;
            Exception? failure = null;
            try
            {
                result = await invocation.ConfigureAwait(false);
                if (result is null)
                {
                    failure = new InvalidOperationException(
                        "Prepared invocation runtime returned no result.");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            WorkerPreparedInvokeTerminal terminal;
            lock (_gate)
            {
                if (!_prepared.Task.IsCompleted)
                {
                    _prepared.TrySetException(failure ??
                        ProtocolFailure(
                            "prepared_plan_unavailable",
                            "Runtime completed without publishing a prepared plan."));
                }

                terminal = TerminalFromState(result, failure);
                _state = ReservationState.Completed;
                _deadlineCancellation.Cancel();
                _terminal.TrySetResult(terminal);
            }
        }

        private WorkerPreparedInvokeTerminal TerminalFromState(
            WorkerPreparedRuntimeResult? result,
            Exception? failure)
        {
            var kind = _state switch
            {
                ReservationState.ReplanRequired =>
                    WorkerPreparedInvokeTerminalKind.ReplanRequired,
                ReservationState.Aborted =>
                    WorkerPreparedInvokeTerminalKind.Aborted,
                ReservationState.Expired =>
                    WorkerPreparedInvokeTerminalKind.Expired,
                ReservationState.Canceled =>
                    WorkerPreparedInvokeTerminalKind.Canceled,
                ReservationState.Failed =>
                    WorkerPreparedInvokeTerminalKind.Failed,
                _ when failure is not null =>
                    WorkerPreparedInvokeTerminalKind.Failed,
                _ => WorkerPreparedInvokeTerminalKind.Completed,
            };
            var detailCode = _terminalDetailCode ??
                (failure is null ? null : "prepared_runtime_failure");
            if (kind != WorkerPreparedInvokeTerminalKind.Completed &&
                result?.UserExecutionStarted == true)
            {
                kind = WorkerPreparedInvokeTerminalKind.Failed;
                detailCode = "prepared_authorization_invariant_failed";
            }
            return new WorkerPreparedInvokeTerminal(
                kind,
                kind == WorkerPreparedInvokeTerminalKind.Completed
                    ? result?.Text
                    : null,
                detailCode);
        }

        private void EndBeforeCommitUnderLock(
            WorkerPreparedInvokeTerminalKind kind,
            string detailCode)
        {
            if (_state == ReservationState.Committed || IsTerminalState(_state))
                return;
            _state = kind switch
            {
                WorkerPreparedInvokeTerminalKind.ReplanRequired =>
                    ReservationState.ReplanRequired,
                WorkerPreparedInvokeTerminalKind.Aborted =>
                    ReservationState.Aborted,
                WorkerPreparedInvokeTerminalKind.Expired =>
                    ReservationState.Expired,
                WorkerPreparedInvokeTerminalKind.Canceled =>
                    ReservationState.Canceled,
                _ => ReservationState.Failed,
            };
            _terminalDetailCode = detailCode;
            _commit.TrySetResult(false);
        }

        private void MarkReplanRequiredUnderLock()
        {
            if (_state == ReservationState.Committed)
            {
                _state = ReservationState.ReplanRequired;
                _terminalDetailCode = "replan_required";
            }
        }

        private void MarkAuthorizationFailure(string detailCode)
        {
            lock (_gate)
            {
                if (_state == ReservationState.Committed)
                {
                    _state = ReservationState.Failed;
                    _terminalDetailCode = detailCode;
                }
            }
        }

        private void Cancel()
        {
            lock (_gate)
            {
                if (_state is ReservationState.Preparing or ReservationState.Prepared)
                {
                    EndBeforeCommitUnderLock(
                        WorkerPreparedInvokeTerminalKind.Canceled,
                        "prepared_operation_canceled");
                }
            }
        }

        private static bool IsTerminalState(ReservationState state) => state is
            ReservationState.ReplanRequired or
            ReservationState.Aborted or
            ReservationState.Expired or
            ReservationState.Canceled or
            ReservationState.Failed or
            ReservationState.Completed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _executionCancellationRegistration.Dispose();
            _executionCancellation.Dispose();
            _deadlineCancellation.Dispose();
        }
    }
}
