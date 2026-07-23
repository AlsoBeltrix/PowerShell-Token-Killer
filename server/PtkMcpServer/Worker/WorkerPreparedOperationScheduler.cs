using System.Text.Json;

namespace PtkMcpServer.Worker;

/// <summary>
/// Owns asynchronous prepared-operation protocol work for one initialized
/// worker generation. The reader admits validated frames without waiting for
/// planning or user execution; this owner retains request-to-plan correlation,
/// terminal response ownership, cancellation, and fatal writer state.
/// </summary>
internal sealed class WorkerPreparedOperationScheduler : IWorkerPreparedInvokeObserver
{
    private readonly object _gate = new();
    private readonly Guid _workerBootId;
    private readonly long _generation;
    private readonly WorkerPreparedInvokeController _controller;
    private readonly Func<WorkerEnvelope, CancellationToken, Task> _write;
    private readonly Dictionary<long, Guid> _requestPlans = [];
    private readonly HashSet<Task> _active = [];
    private readonly TaskCompletionSource _fatal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopped;
    private Task? _drainTask;

    internal WorkerPreparedOperationScheduler(
        Guid workerBootId,
        long generation,
        IWorkerPreparedInvokeRuntime runtime,
        Func<WorkerEnvelope, CancellationToken, Task> write,
        Func<DateTimeOffset>? utcNow = null,
        Func<DateTimeOffset, CancellationToken, Task>? waitUntilDeadline = null)
    {
        if (workerBootId == Guid.Empty)
            throw new ArgumentException("Worker boot ID cannot be empty.", nameof(workerBootId));
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(write);

        _workerBootId = workerBootId;
        _generation = generation;
        _write = write;
        _controller = new WorkerPreparedInvokeController(
            workerBootId,
            generation,
            runtime,
            this,
            utcNow,
            waitUntilDeadline);
    }

    internal Task Fatal => _fatal.Task;

    internal void AdmitPrepare(long requestId, WorkerInvokePreparePayload prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        Register(requestId, prepare.PlanId);
        Start(() => HandlePrepareAsync(requestId, prepare));
    }

    internal void AdmitCommit(long requestId, WorkerCommitPayload commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        Register(requestId, commit.PlanId);
        Start(() => HandleTerminalAsync(
            requestId,
            commit.PlanId,
            _controller.Commit(commit)));
    }

    internal void AdmitAbort(long requestId, WorkerAbortPayload abort)
    {
        ArgumentNullException.ThrowIfNull(abort);
        Register(requestId, abort.PlanId);
        Start(() => HandleTerminalAsync(
            requestId,
            abort.PlanId,
            _controller.Abort(abort)));
    }

    internal void AdmitCancel(long targetRequestId)
    {
        Guid planId;
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (!_requestPlans.TryGetValue(targetRequestId, out planId)) return;
        }
        Start(() => _controller.Cancel(planId));
    }

    internal Task CancelAndDrainAsync()
    {
        lock (_gate)
        {
            if (_drainTask is not null) return _drainTask;
            _stopped = true;
            _drainTask = DrainAsync();
            return _drainTask;
        }
    }

    public async ValueTask<bool> RecordValidatorStartedAsync(
        WorkerPreparedPlanDescriptor descriptor,
        ExecutionDispatch dispatch,
        CancellationToken cancellationToken)
    {
        try
        {
            await _write(
                WorkerPreparedOperationProtocol.CreateValidatorStartedEvent(
                    _workerBootId,
                    _generation,
                    descriptor,
                    dispatch),
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            LatchFatal(exception);
            return false;
        }
    }

    public async ValueTask<bool> RecordValidatorCompletedAsync(
        WorkerPreparedPlanDescriptor descriptor,
        ExecutionDispatch dispatch,
        BashSyntaxValidationResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await _write(
                WorkerPreparedOperationProtocol.CreateValidatorCompletedEvent(
                    _workerBootId,
                    _generation,
                    descriptor,
                    dispatch,
                    result),
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            LatchFatal(exception);
            return false;
        }
    }

    private void Register(long requestId, Guid planId)
    {
        if (requestId <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestId));
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (!_requestPlans.TryAdd(requestId, planId))
            {
                throw new WorkerProtocolException(
                    "operation_request_replay",
                    "Prepared-operation request IDs cannot be reused.");
            }
        }
    }

    private void Start(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? task = null;
        task = Task.Run(async () =>
        {
            await release.Task.ConfigureAwait(false);
            try
            {
                await handler().ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                LatchFatal(exception);
            }
            finally
            {
                lock (_gate) _active.Remove(task!);
            }
        });
        lock (_gate)
        {
            if (_stopped)
                throw new InvalidOperationException(
                    "Prepared-operation scheduling has stopped.");
            _active.Add(task);
        }
        release.TrySetResult();
    }

    private async Task HandlePrepareAsync(
        long requestId,
        WorkerInvokePreparePayload prepare)
    {
        try
        {
            var descriptor = await _controller.PrepareAsync(
                prepare,
                CancellationToken.None).ConfigureAwait(false);
            await _write(
                WorkerPreparedOperationProtocol.CreatePreparedResponse(
                    _workerBootId,
                    requestId,
                    _generation,
                    descriptor),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (WorkerProtocolException exception)
        {
            await WritePrepareFailureAsync(
                requestId,
                prepare,
                exception.DetailCode).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            await WritePrepareFailureAsync(
                requestId,
                prepare,
                "prepared_runtime_failure").ConfigureAwait(false);
        }
    }

    private async Task WritePrepareFailureAsync(
        long requestId,
        WorkerInvokePreparePayload prepare,
        string detailCode)
    {
        await _controller.AbandonAsync(prepare.PlanId).ConfigureAwait(false);
        RemovePlan(prepare.PlanId);
        var status = detailCode == "prepared_operation_expired"
            ? WorkerOperationStatus.TimedOut
            : WorkerOperationStatus.Failed;
        await _write(
            WorkerPreparedOperationProtocol.CreateFailureResponse(
                _workerBootId,
                requestId,
                _generation,
                status,
                detailCode),
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandleTerminalAsync(
        long requestId,
        Guid planId,
        Task<WorkerPreparedInvokeTerminal> terminalTask)
    {
        var terminal = await terminalTask.ConfigureAwait(false);
        await _write(
            WorkerPreparedOperationProtocol.CreateTerminalResponse(
                _workerBootId,
                requestId,
                _generation,
                terminal),
            CancellationToken.None).ConfigureAwait(false);
        RemovePlan(planId);
        _controller.Release(planId);
    }

    private void RemovePlan(Guid planId)
    {
        lock (_gate)
        {
            foreach (var requestId in _requestPlans
                .Where(value => value.Value == planId)
                .Select(value => value.Key)
                .ToArray())
            {
                _requestPlans.Remove(requestId);
            }
        }
    }

    private void LatchFatal(Exception exception)
    {
        _fatal.TrySetException(new IOException(
            "Prepared-operation response transport failed.",
            exception));
    }

    private void ThrowIfUnavailable()
    {
        if (_fatal.Task.IsCompleted)
        {
            throw new WorkerProtocolException(
                "prepared_scheduler_failed",
                "Prepared-operation scheduling is unavailable after a terminal failure.");
        }
        if (_stopped)
            throw new InvalidOperationException(
                "Prepared-operation scheduling has stopped.");
    }

    private async Task DrainAsync()
    {
        await _controller.CancelAndDrainAsync().ConfigureAwait(false);
        while (true)
        {
            Task[] tasks;
            lock (_gate)
            {
                tasks = _active.ToArray();
            }
            if (tasks.Length == 0) return;
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}

internal static class WorkerPreparedOperationProtocol
{
    internal static WorkerEnvelope CreatePreparedResponse(
        Guid workerBootId,
        long requestId,
        long generation,
        WorkerPreparedPlanDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.WorkerBootId != workerBootId ||
            descriptor.Generation != generation)
        {
            throw new WorkerProtocolException(
                "invalid_prepared_descriptor",
                "Prepared descriptor identity does not match its response.");
        }
        return Envelope(
            workerBootId,
            WorkerMessageKind.Response,
            requestId,
            JsonSerializer.SerializeToElement(new
            {
                generation,
                status = "prepared",
                descriptor = WorkerPreparedOperationCodec.CreatePreparedDescriptor(descriptor),
            }));
    }

    internal static WorkerEnvelope CreateTerminalResponse(
        Guid workerBootId,
        long requestId,
        long generation,
        WorkerPreparedInvokeTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var response = terminal.Kind switch
        {
            WorkerPreparedInvokeTerminalKind.Completed =>
                WorkerOperationResponse.Completed(
                    requestId,
                    generation,
                    WorkerSessionOperationCodec.CreateResult(
                        WorkerSessionOperationCodec.InvokeOperation,
                        new WorkerInvokeResult(terminal.Text ??
                            throw InvalidTerminal()))),
            WorkerPreparedInvokeTerminalKind.Expired =>
                WorkerOperationResponse.TimedOut(
                    requestId,
                    generation,
                    terminal.DetailCode ?? "prepared_operation_expired"),
            WorkerPreparedInvokeTerminalKind.Aborted or
            WorkerPreparedInvokeTerminalKind.Canceled =>
                WorkerOperationResponse.Canceled(
                    requestId,
                    generation,
                    terminal.DetailCode ?? "prepared_operation_canceled"),
            WorkerPreparedInvokeTerminalKind.ReplanRequired or
            WorkerPreparedInvokeTerminalKind.Failed =>
                WorkerOperationResponse.Failed(
                    requestId,
                    generation,
                    terminal.DetailCode ?? "prepared_runtime_failure"),
            _ => throw InvalidTerminal(),
        };
        return WorkerOperationProtocol.CreateResponseEnvelope(workerBootId, response);
    }

    internal static WorkerEnvelope CreateFailureResponse(
        Guid workerBootId,
        long requestId,
        long generation,
        WorkerOperationStatus status,
        string detailCode)
    {
        var response = status switch
        {
            WorkerOperationStatus.Failed =>
                WorkerOperationResponse.Failed(requestId, generation, detailCode),
            WorkerOperationStatus.Canceled =>
                WorkerOperationResponse.Canceled(requestId, generation, detailCode),
            WorkerOperationStatus.TimedOut =>
                WorkerOperationResponse.TimedOut(requestId, generation, detailCode),
            _ => throw InvalidTerminal(),
        };
        return WorkerOperationProtocol.CreateResponseEnvelope(workerBootId, response);
    }

    internal static WorkerEnvelope CreateValidatorStartedEvent(
        Guid workerBootId,
        long generation,
        WorkerPreparedPlanDescriptor descriptor,
        ExecutionDispatch dispatch)
    {
        ValidateValidator(workerBootId, generation, descriptor, dispatch);
        return Envelope(
            workerBootId,
            WorkerMessageKind.Event,
            requestId: null,
            JsonSerializer.SerializeToElement(new
            {
                @event = "validator_started",
                generation,
                planId = descriptor.PlanId.ToString("D"),
                descriptorDigest =
                    WorkerPreparedOperationCodec.ComputePreparedDescriptorDigest(descriptor),
                executionPath = dispatch.ExecutionPath.ToMachineCode(),
            }));
    }

    internal static WorkerEnvelope CreateValidatorCompletedEvent(
        Guid workerBootId,
        long generation,
        WorkerPreparedPlanDescriptor descriptor,
        ExecutionDispatch dispatch,
        BashSyntaxValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateValidator(workerBootId, generation, descriptor, dispatch);
        return Envelope(
            workerBootId,
            WorkerMessageKind.Event,
            requestId: null,
            JsonSerializer.SerializeToElement(new
            {
                @event = "validator_completed",
                generation,
                planId = descriptor.PlanId.ToString("D"),
                descriptorDigest =
                    WorkerPreparedOperationCodec.ComputePreparedDescriptorDigest(descriptor),
                executionPath = dispatch.ExecutionPath.ToMachineCode(),
                detailCode = result.DetailCode,
                processStarted = result.ProcessStarted,
                exitCode = result.ExitCode,
                rootTerminationConfirmed = result.RootTerminationConfirmed,
            }));
    }

    private static void ValidateValidator(
        Guid workerBootId,
        long generation,
        WorkerPreparedPlanDescriptor descriptor,
        ExecutionDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(dispatch);
        if (descriptor.WorkerBootId != workerBootId ||
            descriptor.Generation != generation ||
            descriptor.PlanId == Guid.Empty ||
            dispatch.ExecutionPath != ExecutionPath.BashViaRtk)
        {
            throw new WorkerProtocolException(
                "invalid_validator_event",
                "Validator event identity or execution path is invalid.");
        }
    }

    private static WorkerEnvelope Envelope(
        Guid workerBootId,
        WorkerMessageKind kind,
        long? requestId,
        JsonElement payload) =>
        new(WorkerProtocol.Version, kind, workerBootId, requestId, payload);

    private static WorkerProtocolException InvalidTerminal() =>
        new(
            "invalid_prepared_terminal",
            "Prepared-operation terminal response is invalid.");
}
