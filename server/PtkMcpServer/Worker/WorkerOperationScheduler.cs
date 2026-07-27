using System.Security.Cryptography;

namespace PtkMcpServer.Worker;

internal interface IWorkerOperationExecutor
{
    Task<WorkerExecutionResult> ExecuteAsync(
        WorkerOperationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns operation correlation, cancellation, deadlines, artifact ordering, and
/// exactly one terminal frame for each admitted request.
/// </summary>
internal sealed class WorkerOperationScheduler
{
    internal const int MaximumOutstandingRequests = 64;

    private static readonly TimeSpan MaximumDeadlinePoll = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Guid _sessionId;
    private readonly long _incarnation;
    private readonly WorkerProtocolLimits _limits;
    private readonly IWorkerOperationExecutor _executor;
    private readonly Func<WorkerEnvelope, CancellationToken, Task> _writeFrame;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<DateTimeOffset, CancellationToken, Task> _waitUntilDeadline;
    private readonly TaskScheduler _taskScheduler;
    private readonly Dictionary<long, ActiveRequest> _active = [];
    private readonly TaskCompletionSource _fatal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private long _requestIdHighWater;
    private bool _failed;
    private bool _stopped;
    private Task? _drainTask;

    internal WorkerOperationScheduler(
        Guid sessionId,
        long incarnation,
        WorkerProtocolLimits limits,
        long initialRequestIdHighWater,
        IWorkerOperationExecutor executor,
        Func<WorkerEnvelope, CancellationToken, Task> writeFrame,
        Func<DateTimeOffset>? utcNow = null,
        Func<DateTimeOffset, CancellationToken, Task>? waitUntilDeadline = null,
        TaskScheduler? taskScheduler = null)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
        if (incarnation <= 0)
            throw new ArgumentOutOfRangeException(nameof(incarnation));
        ArgumentNullException.ThrowIfNull(limits);
        if (initialRequestIdHighWater < 0)
            throw new ArgumentOutOfRangeException(nameof(initialRequestIdHighWater));
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(writeFrame);

        _sessionId = sessionId;
        _incarnation = incarnation;
        _limits = limits;
        _requestIdHighWater = initialRequestIdHighWater;
        _executor = executor;
        _writeFrame = writeFrame;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _waitUntilDeadline = waitUntilDeadline ?? WaitUntilDeadlineAsync;
        _taskScheduler = taskScheduler ?? TaskScheduler.Default;
    }

    /// <summary>Completes only when the scheduler's single fatal outcome is latched.</summary>
    internal Task Fatal => _fatal.Task;

    internal int OutstandingCount
    {
        get
        {
            lock (_gate) return _active.Count;
        }
    }

    /// <summary>
    /// Admits one request or cancel envelope without awaiting user operation
    /// execution or its terminal response write.
    /// </summary>
    internal void Admit(WorkerEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        switch (envelope.Kind)
        {
            case WorkerMessageKind.Invoke:
                AdmitRequest(WorkerOperationProtocol.ParseInvoke(
                    envelope,
                    _sessionId,
                    _incarnation,
                    _limits));
                return;
            case WorkerMessageKind.StateQuery:
                AdmitRequest(WorkerOperationProtocol.ParseStateQuery(
                    envelope,
                    _sessionId,
                    _incarnation,
                    _limits));
                return;
            case WorkerMessageKind.Cancel:
                AdmitCancel(WorkerOperationProtocol.ParseCancel(
                    envelope,
                    _sessionId,
                    _incarnation));
                return;
            default:
                throw new WorkerProtocolException(
                    "unsupported_operation_message",
                    "The operation scheduler accepts only invoke, state_query, and cancel frames.");
        }
    }

    internal Task CancelAndDrainAsync()
    {
        lock (_gate)
        {
            return BeginDrainUnderLock();
        }
    }

    internal Task ShutdownAndDrainAsync(long requestId)
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (requestId <= _requestIdHighWater)
            {
                throw new WorkerProtocolException(
                    "operation_request_replay",
                    "Worker operation request IDs must increase strictly.");
            }
            _requestIdHighWater = requestId;
            return BeginDrainUnderLock();
        }
    }

    private Task BeginDrainUnderLock()
    {
        if (_drainTask is not null) return _drainTask;
        _stopped = true;
        var requests = _active.Values.ToArray();
        foreach (var request in requests)
            request.RequestCancellation(CancellationReason.Shutdown);
        _drainTask = DrainAsync(requests);
        return _drainTask;
    }

    private void AdmitRequest(WorkerOperationRequest request)
    {
        DateTimeOffset deadline;
        try
        {
            deadline = _utcNow().Add(request.Timeout);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            LatchFatal(exception);
            return;
        }

        ActiveRequest active;
        Exception? scheduleFailure = null;
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (request.RequestId <= _requestIdHighWater)
            {
                throw new WorkerProtocolException(
                    "operation_request_replay",
                    "Worker operation request IDs must increase strictly.");
            }

            // Reserve before every later admission decision. A rejected request
            // can never be replayed under the same ID.
            _requestIdHighWater = request.RequestId;
            if (_active.Count >= MaximumOutstandingRequests)
            {
                throw new WorkerProtocolException(
                    "operation_capacity_exceeded",
                    "Worker operation request capacity is exhausted.");
            }

            active = new ActiveRequest(request, deadline);
            _active.Add(request.RequestId, active);
            // The outer admit hop must honor the injected scheduler (rbc-9):
            // dispatching on TaskScheduler.Default here silently bypassed the
            // deterministic test scheduler and hid ordering races.
            try
            {
                active.OwnerTask = Task.Factory.StartNew(
                    () => RunScheduledRequestAsync(active),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    _taskScheduler).Unwrap();
            }
            catch (Exception exception)
            {
                // A scheduler that cannot accept the hop is a terminal
                // scheduler fault, not a caller protocol error: release the
                // reservation so the slot is not leaked, then latch fatal
                // outside the lock (rbc-9).
                scheduleFailure = exception;
                _active.Remove(request.RequestId);
            }
        }

        if (scheduleFailure is not null)
        {
            active.Dispose();
            LatchFatal(scheduleFailure);
            return;
        }

        // Release the admission gate only after the scheduler lock is free so
        // an inline-executing scheduler can never run the executor on the
        // admission call stack (rbc-9).
        active.ReleaseAdmissionGate();
    }

    private void AdmitCancel(WorkerOperationCancel cancel)
    {
        ActiveRequest? active;
        lock (_gate)
        {
            ThrowIfUnavailable();
            _active.TryGetValue(cancel.RequestId, out active);
        }
        active?.RequestCancellation(CancellationReason.Explicit);
    }

    private async Task RunRequestAsync(ActiveRequest active)
    {
        WorkerExecutionResult? executionResult = null;
        WorkerResult? fallbackResult = null;
        if (_utcNow() >= active.DeadlineUtc)
        {
            active.MarkTerminal();
            fallbackResult = new WorkerResult(
                active.Request.RequestId,
                WorkerResultStatus.TimedOut,
                string.Empty,
                "request_deadline_expired");
        }
        else
        {
            active.DeadlineTask = ObserveDeadlineAsync(active);
            try
            {
                executionResult = await _executor.ExecuteAsync(
                    active.Request,
                    active.Token).ConfigureAwait(false);
                if (executionResult is null)
                {
                    fallbackResult = new WorkerResult(
                        active.Request.RequestId,
                        WorkerResultStatus.Failed,
                        string.Empty,
                        "invalid_operation_result");
                }
            }
            catch (OperationCanceledException exception)
            {
                if (active.IsCancellationRequested &&
                    exception.CancellationToken == active.Token)
                {
                    fallbackResult = new WorkerResult(
                        active.Request.RequestId,
                        active.Reason == CancellationReason.Deadline
                            ? WorkerResultStatus.TimedOut
                            : WorkerResultStatus.Canceled,
                        string.Empty,
                        active.Reason == CancellationReason.Deadline
                            ? "request_deadline_expired"
                            : "request_canceled");
                }
                else
                {
                    fallbackResult = new WorkerResult(
                        active.Request.RequestId,
                        WorkerResultStatus.Failed,
                        string.Empty,
                        "operation_failed");
                }
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                fallbackResult = new WorkerResult(
                    active.Request.RequestId,
                    WorkerResultStatus.Failed,
                    string.Empty,
                    "operation_failed");
            }
            finally
            {
                active.MarkTerminal();
                active.StopDeadline();
                await active.ObserveCancellationAsync().ConfigureAwait(false);
                await active.ObserveDeadlineAsync().ConfigureAwait(false);
            }
        }

        try
        {
            // Request cancellation must never suppress the one terminal write.
            await WriteTerminalFramesAsync(
                active.Request,
                executionResult,
                fallbackResult).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LatchFatal(exception);
        }
        finally
        {
            lock (_gate) _active.Remove(active.Request.RequestId);
            active.Dispose();
        }
    }

    private async Task WriteTerminalFramesAsync(
        WorkerOperationRequest request,
        WorkerExecutionResult? executionResult,
        WorkerResult? fallbackResult)
    {
        if (fallbackResult is not null)
        {
            await WriteFrameAsync(
                WorkerOperationProtocol.CreateResultEnvelope(
                    _sessionId,
                    _incarnation,
                    fallbackResult)).ConfigureAwait(false);
            return;
        }

        switch (request, executionResult)
        {
            case (WorkerInvokeRequest invoke, WorkerInvokeExecutionResult result):
                await WriteInvokeFramesAsync(invoke, result).ConfigureAwait(false);
                return;
            case (WorkerStateQueryRequest state, WorkerStateExecutionResult result):
                await WriteFrameAsync(
                    WorkerOperationProtocol.CreateStateSnapshotEnvelope(
                        _sessionId,
                        _incarnation,
                        new WorkerStateSnapshot(
                            state.RequestId,
                            result.Available,
                            result.Text,
                            result.DetailCode))).ConfigureAwait(false);
                return;
            default:
                await WriteFrameAsync(
                    WorkerOperationProtocol.CreateResultEnvelope(
                        _sessionId,
                        _incarnation,
                        new WorkerResult(
                            request.RequestId,
                            WorkerResultStatus.Failed,
                            string.Empty,
                            "invalid_operation_result"))).ConfigureAwait(false);
                return;
        }
    }

    private async Task WriteInvokeFramesAsync(
        WorkerInvokeRequest request,
        WorkerInvokeExecutionResult result)
    {
        if (result.Artifact is { } artifact)
        {
            if (result.Status != WorkerResultStatus.Completed ||
                request.Artifact is not { } requested ||
                artifact.ArtifactId != requested.ArtifactId ||
                artifact.Bytes.Length > requested.MaximumBytes)
            {
                throw new WorkerProtocolException(
                    "invalid_artifact_result",
                    "Worker execution returned an unsolicited or oversized artifact.");
            }

            long offset = 0;
            while (offset < artifact.Bytes.Length)
            {
                var count = Math.Min(
                    _limits.MaximumArtifactChunkBytes,
                    artifact.Bytes.Length - checked((int)offset));
                var bytes = artifact.Bytes.Slice(checked((int)offset), count).ToArray();
                await WriteFrameAsync(
                    WorkerOperationProtocol.CreateArtifactChunkEnvelope(
                        _sessionId,
                        _incarnation,
                        new WorkerArtifactChunk(
                            request.RequestId,
                            artifact.ArtifactId,
                            offset,
                            bytes),
                        _limits)).ConfigureAwait(false);
                offset = checked(offset + count);
            }

            var digest = Convert.ToHexString(SHA256.HashData(artifact.Bytes.Span))
                .ToLowerInvariant();
            await WriteFrameAsync(
                WorkerOperationProtocol.CreateArtifactSealEnvelope(
                    _sessionId,
                    _incarnation,
                    new WorkerArtifactSeal(
                        request.RequestId,
                        artifact.ArtifactId,
                        artifact.Bytes.Length,
                        digest))).ConfigureAwait(false);
        }

        await WriteFrameAsync(
            WorkerOperationProtocol.CreateResultEnvelope(
                _sessionId,
                _incarnation,
                new WorkerResult(
                    request.RequestId,
                    result.Status,
                    result.Text,
                    result.DetailCode))).ConfigureAwait(false);
    }

    private Task WriteFrameAsync(WorkerEnvelope envelope) =>
        _writeFrame(envelope, CancellationToken.None);

    private async Task RunScheduledRequestAsync(ActiveRequest active)
    {
        try
        {
            // Park until Admit has released the scheduler lock. An inline-
            // executing scheduler otherwise runs the executor on the admission
            // call stack; RunContinuationsAsynchronously guarantees the resume
            // is never inlined onto the admitting thread (rbc-9).
            await active.AdmissionSettled.ConfigureAwait(false);
            await Task.Factory.StartNew(
                () => RunRequestAsync(active),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                _taskScheduler).Unwrap().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LatchFatal(exception);
            active.MarkTerminal();
            active.StopDeadline();
            try
            {
                await active.ObserveCancellationAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                LatchFatal(cleanupException);
            }
            try
            {
                await active.ObserveDeadlineAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                LatchFatal(cleanupException);
            }
            lock (_gate) _active.Remove(active.Request.RequestId);
            active.Dispose();
        }
    }

    private async Task ObserveDeadlineAsync(ActiveRequest active)
    {
        try
        {
            await _waitUntilDeadline(
                active.DeadlineUtc,
                active.DeadlineToken).ConfigureAwait(false);
            active.RequestCancellation(CancellationReason.Deadline);
        }
        catch (OperationCanceledException) when (active.DeadlineToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LatchFatal(exception);
        }
    }

    private void LatchFatal(Exception exception)
    {
        ActiveRequest[] requests;
        lock (_gate)
        {
            if (_failed) return;
            _failed = true;
            requests = _active.Values.ToArray();
        }

        // Preserve the original typed failure so WorkerServer can keep
        // transport failures distinct from worker-runtime failures. Its
        // process-exit mapping exposes only bounded detail codes.
        _fatal.TrySetException(exception);
        foreach (var request in requests)
            request.RequestCancellation(CancellationReason.SchedulerFailure);
    }

    private void ThrowIfUnavailable()
    {
        if (_failed)
        {
            throw new WorkerProtocolException(
                "operation_scheduler_failed",
                "Worker operation scheduling is unavailable after a terminal failure.");
        }
        if (_stopped)
            throw new InvalidOperationException("Worker operation scheduling has stopped.");
    }

    private async Task DrainAsync(ActiveRequest[] requests)
    {
        var tasks = requests
            .Select(request => request.OwnerTask)
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task WaitUntilDeadlineAsync(
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = deadlineUtc - _utcNow();
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(
                remaining < MaximumDeadlinePoll ? remaining : MaximumDeadlinePoll,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private enum CancellationReason
    {
        None,
        Explicit,
        Deadline,
        Shutdown,
        SchedulerFailure,
    }

    private sealed class ActiveRequest : IDisposable
    {
        private readonly CancellationTokenSource _execution = new();
        private readonly CancellationTokenSource _deadline = new();
        private readonly object _cancellationGate = new();
        private readonly TaskCompletionSource _admissionGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _cancellationTask;
        private CancellationReason _reason;
        private bool _terminal;

        internal ActiveRequest(
            WorkerOperationRequest request,
            DateTimeOffset deadlineUtc)
        {
            Request = request;
            DeadlineUtc = deadlineUtc;
        }

        internal WorkerOperationRequest Request { get; }
        internal DateTimeOffset DeadlineUtc { get; }
        internal CancellationToken Token => _execution.Token;
        internal CancellationToken DeadlineToken => _deadline.Token;
        internal bool IsCancellationRequested => _execution.IsCancellationRequested;
        internal CancellationReason Reason
        {
            get
            {
                lock (_cancellationGate) return _reason;
            }
        }
        internal Task? OwnerTask { get; set; }
        internal Task? DeadlineTask { get; set; }
        internal Task AdmissionSettled => _admissionGate.Task;

        internal void ReleaseAdmissionGate() => _admissionGate.TrySetResult();

        internal void RequestCancellation(CancellationReason reason)
        {
            lock (_cancellationGate)
            {
                if (_terminal || _reason != CancellationReason.None) return;
                _reason = reason;
                _cancellationTask = CancelAsync(_execution);
            }
        }

        internal void MarkTerminal()
        {
            lock (_cancellationGate) _terminal = true;
        }

        internal void StopDeadline()
        {
            try
            {
                _deadline.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        internal async Task ObserveCancellationAsync()
        {
            Task? task;
            lock (_cancellationGate) task = _cancellationTask;
            if (task is not null) await task.ConfigureAwait(false);
        }

        internal async Task ObserveDeadlineAsync()
        {
            if (DeadlineTask is not null)
                await DeadlineTask.ConfigureAwait(false);
        }

        public void Dispose()
        {
            _deadline.Dispose();
            _execution.Dispose();
        }

        private static async Task CancelAsync(CancellationTokenSource cancellation)
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // Cancellation callbacks belong to the injected executor. Their
                // failure is observed and redacted; terminal ownership remains
                // with the request owner task.
            }
        }
    }
}
