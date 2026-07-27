namespace PtkMcpServer.Worker;

internal enum WorkerServerExitKind
{
    Shutdown,
    Eof,
    Canceled,
    InitializeFailed,
    ProtocolError,
    TransportFailure,
    RuntimeFailure,
}

internal readonly record struct WorkerServerExit(
    WorkerServerExitKind Kind,
    string DetailCode);

/// <summary>
/// Platform-neutral worker lifecycle core. The contained entry point supplies
/// private protocol streams and a runtime factory only after OS containment is
/// armed; this type never opens stdio, launches a process, or accepts
/// supervisor audit/output capabilities.
/// </summary>
internal sealed class WorkerServer
{
    private static readonly TimeSpan MaximumDeadlinePoll = TimeSpan.FromMinutes(1);

    private readonly WorkerProtocolReader _reader;
    private readonly WorkerProtocolWriter _writer;
    private readonly Func<WorkerInitializeRequest, CancellationToken, Task<IWorkerSession>>
        _runtimeFactory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<DateTimeOffset, CancellationToken, Task> _waitUntilDeadline;
    private readonly TaskScheduler _factoryScheduler;
    private int _started;

    internal WorkerServer(
        Stream requestStream,
        Stream eventStream,
        Func<WorkerInitializeRequest, CancellationToken, Task<IWorkerSession>> runtimeFactory,
        Func<DateTimeOffset>? utcNow = null,
        Func<DateTimeOffset, CancellationToken, Task>? waitUntilDeadline = null,
        TaskScheduler? factoryScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(eventStream);
        ArgumentNullException.ThrowIfNull(runtimeFactory);

        _reader = new WorkerProtocolReader(requestStream);
        _writer = new WorkerProtocolWriter(eventStream);
        _runtimeFactory = runtimeFactory;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _waitUntilDeadline = waitUntilDeadline ?? WaitUntilDeadlineAsync;
        _factoryScheduler = factoryScheduler ?? TaskScheduler.Default;
    }

    internal Task<WorkerServerExit> RunAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A worker server instance can run only once.");
        return RunProtectedAsync(cancellationToken);
    }

    private async Task<WorkerServerExit> RunProtectedAsync(CancellationToken cancellationToken)
    {
        var ownership = new WorkerRunOwnership(cancellationToken);
        WorkerServerExit primary;
        try
        {
            primary = await RunProtocolAsync(ownership, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            primary = new WorkerServerExit(WorkerServerExitKind.Canceled, "canceled");
        }
        catch (WorkerProtocolException exception)
        {
            primary = new WorkerServerExit(
                WorkerServerExitKind.ProtocolError,
                exception.DetailCode);
        }
        catch (WorkerTransportException exception)
        {
            primary = new WorkerServerExit(
                WorkerServerExitKind.TransportFailure,
                exception.DetailCode);
        }
        catch (WorkerRuntimeException exception)
        {
            primary = new WorkerServerExit(
                WorkerServerExitKind.RuntimeFailure,
                exception.DetailCode);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            primary = new WorkerServerExit(WorkerServerExitKind.RuntimeFailure, "runtime_failure");
        }

        var cleanupFailed = await ownership.CleanupAsync().ConfigureAwait(false);
        if (!cleanupFailed || primary.Kind is WorkerServerExitKind.ProtocolError or
            WorkerServerExitKind.TransportFailure or WorkerServerExitKind.RuntimeFailure)
        {
            return primary;
        }
        return new WorkerServerExit(WorkerServerExitKind.RuntimeFailure, "cleanup_failed");
    }

    private async Task<WorkerServerExit> RunProtocolAsync(
        WorkerRunOwnership ownership,
        CancellationToken cancellationToken)
    {
        ownership.PendingRead = ReadEnvelopeAsync(ownership.ReaderToken);
        await Task.WhenAny(
            ownership.PendingRead,
            ownership.HostCancellation).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var initializeEnvelope = await ownership.TakePendingReadAsync().ConfigureAwait(false);
        if (initializeEnvelope is null)
            return new WorkerServerExit(WorkerServerExitKind.Eof, "eof_before_initialize");
        if (initializeEnvelope.Kind != WorkerMessageKind.Initialize)
        {
            throw new WorkerProtocolException(
                "initialize_required",
                "The first supervisor frame must initialize the worker.");
        }
        var initialize = WorkerOperationProtocol.ParseInitialize(initializeEnvelope);
        var initializeRequestId = initialize.RequestId;

        if (DeadlineExpired(initialize))
        {
            await WriteFailureAsync(
                initializeRequestId,
                initialize,
                "initialize_deadline_expired",
                cancellationToken).ConfigureAwait(false);
            return new WorkerServerExit(
                WorkerServerExitKind.InitializeFailed,
                "initialize_deadline_expired");
        }

        ownership.DeadlineCancellation = new CancellationTokenSource();
        ownership.PendingRead = ReadEnvelopeAsync(ownership.ReaderToken);
        ownership.DeadlineTask = _waitUntilDeadline(
            initialize.DeadlineUtc,
            ownership.DeadlineCancellation.Token);
        cancellationToken.ThrowIfCancellationRequested();
        if (ownership.PendingRead.IsCompleted)
        {
            var queuedEnvelope = await ownership.TakePendingReadAsync().ConfigureAwait(false);
            if (queuedEnvelope is null)
            {
                return new WorkerServerExit(
                    WorkerServerExitKind.Eof,
                    "eof_during_initialize");
            }
            ValidateBeforeReady(queuedEnvelope, initialize);
        }
        if (ownership.DeadlineTask.IsCompleted)
        {
            await ownership.DeadlineTask.ConfigureAwait(false);
            return await InitializeDeadlineExpiredAsync(
                ownership,
                initializeRequestId,
                initialize,
                cancellationToken).ConfigureAwait(false);
        }
        if (DeadlineExpired(initialize))
        {
            return await InitializeDeadlineExpiredAsync(
                ownership,
                initializeRequestId,
                initialize,
                cancellationToken).ConfigureAwait(false);
        }

        var factoryToken = ownership.FactoryToken;
        var deadlineTask = ownership.DeadlineTask
            ?? throw new InvalidOperationException("No worker initialization deadline is pending.");
        ownership.FactoryTask = Task.Factory.StartNew(
            async () =>
            {
                factoryToken.ThrowIfCancellationRequested();
                if (deadlineTask.IsCompleted || DeadlineExpired(initialize))
                {
                    throw new OperationCanceledException(
                        "The worker initialization deadline expired before runtime construction.",
                        factoryToken);
                }
                var factoryTask = _runtimeFactory(initialize, factoryToken)
                    ?? throw new InvalidOperationException(
                        "The worker runtime factory returned null task.");
                return await factoryTask.ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "The worker runtime factory returned null.");
            },
            factoryToken,
            TaskCreationOptions.DenyChildAttach,
            _factoryScheduler).Unwrap();

        while (true)
        {
            await Task.WhenAny(
                ownership.FactoryTask,
                ownership.PendingRead,
                ownership.DeadlineTask,
                ownership.HostCancellation).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (ownership.PendingRead.IsCompleted)
            {
                var queuedEnvelope = await ownership.TakePendingReadAsync().ConfigureAwait(false);
                if (queuedEnvelope is null)
                {
                    return new WorkerServerExit(
                        WorkerServerExitKind.Eof,
                        "eof_during_initialize");
                }
                ValidateBeforeReady(queuedEnvelope, initialize);
            }
            if (ownership.DeadlineTask.IsCompleted)
            {
                await ownership.DeadlineTask.ConfigureAwait(false);
                return await InitializeDeadlineExpiredAsync(
                    ownership,
                    initializeRequestId,
                    initialize,
                    cancellationToken).ConfigureAwait(false);
            }
            if (DeadlineExpired(initialize))
            {
                return await InitializeDeadlineExpiredAsync(
                    ownership,
                    initializeRequestId,
                    initialize,
                    cancellationToken).ConfigureAwait(false);
            }
            if (!ownership.FactoryTask.IsCompleted) continue;

            var factoryTask = ownership.TakeFactory();
            try
            {
                ownership.Session = await factoryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await WriteFailureAsync(
                        initializeRequestId,
                        initialize,
                        "initialize_canceled",
                    cancellationToken).ConfigureAwait(false);
                return new WorkerServerExit(
                    WorkerServerExitKind.InitializeFailed,
                    "initialize_canceled");
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                await WriteFailureAsync(
                    initializeRequestId,
                    initialize,
                    "initialize_failed",
                    cancellationToken).ConfigureAwait(false);
                return new WorkerServerExit(
                    WorkerServerExitKind.InitializeFailed,
                    "initialize_failed");
            }
            ownership.StopDeadline();
            cancellationToken.ThrowIfCancellationRequested();
            if (ownership.PendingRead.IsCompleted)
            {
                var queuedEnvelope = await ownership.TakePendingReadAsync().ConfigureAwait(false);
                if (queuedEnvelope is null)
                {
                    return new WorkerServerExit(
                        WorkerServerExitKind.Eof,
                        "eof_after_initialize");
                }
                ValidateBeforeReady(queuedEnvelope, initialize);
            }
            if (DeadlineExpired(initialize))
            {
                return await InitializeDeadlineExpiredAsync(
                    ownership,
                    initializeRequestId,
                    initialize,
                    cancellationToken).ConfigureAwait(false);
            }

            ownership.Scheduler = new WorkerOperationScheduler(
                initialize.SessionId,
                initialize.Incarnation,
                initialize.Limits,
                initialize.RequestId,
                ownership.Session
                    ?? throw new InvalidOperationException(
                        "The initialized worker session is unavailable."),
                (frame, token) => WriteEnvelopeAsync(frame, token));

            // The supervisor must wait for ready before sending an operation.
            await WriteEnvelopeAsync(
                WorkerOperationProtocol.CreateReadyEnvelope(initialize),
                cancellationToken).ConfigureAwait(false);
            break;
        }

        while (true)
        {
            var scheduler = ownership.Scheduler
                ?? throw new InvalidOperationException("The worker scheduler is unavailable.");
            await Task.WhenAny(
                ownership.PendingRead,
                ownership.HostCancellation,
                scheduler.Fatal).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (scheduler.Fatal.IsCompleted)
            {
                try
                {
                    await scheduler.Fatal.ConfigureAwait(false);
                }
                catch (WorkerProtocolException exception)
                {
                    // The request reader validates supervisor input before
                    // admission. A protocol failure latched by the scheduler
                    // therefore came from encoding worker-owned output.
                    throw new WorkerRuntimeException(
                        "outbound_protocol_failure",
                        exception);
                }
            }

            var envelope = await ownership.TakePendingReadAsync().ConfigureAwait(false);
            if (envelope is null)
                return new WorkerServerExit(WorkerServerExitKind.Eof, "eof_after_ready");

            if (envelope.Kind is
                WorkerMessageKind.Invoke or
                WorkerMessageKind.StateQuery or
                WorkerMessageKind.Cancel)
            {
                ownership.PendingRead = ReadEnvelopeAsync(ownership.ReaderToken);
                scheduler.Admit(envelope);
                continue;
            }

            if (envelope.Kind != WorkerMessageKind.Shutdown)
            {
                throw new WorkerProtocolException(
                    "unsupported_message",
                    $"Worker does not accept '{envelope.Kind}' from the supervisor.");
            }

            WorkerOperationProtocol.ParseEmpty(
                envelope,
                WorkerMessageKind.Shutdown,
                initialize.SessionId,
                initialize.Incarnation);
            var shutdownRequestId = envelope.RequestId!.Value;
            var operationDrainFailed =
                await ownership.DrainOperationsAsync(
                    shutdownRequestId).ConfigureAwait(false);
            var shutdownFailed = await ownership.DrainSessionAsync().ConfigureAwait(false);
            if (operationDrainFailed || shutdownFailed)
            {
                await WriteFailureAsync(
                    shutdownRequestId,
                    initialize,
                    "shutdown_failed",
                    cancellationToken).ConfigureAwait(false);
                return new WorkerServerExit(
                    WorkerServerExitKind.RuntimeFailure,
                    "shutdown_failed");
            }

            await WriteEnvelopeAsync(
                WorkerOperationProtocol.CreateEmptyEnvelope(
                    WorkerMessageKind.Stopped,
                    initialize.SessionId,
                    initialize.Incarnation,
                    shutdownRequestId),
                cancellationToken).ConfigureAwait(false);
            return new WorkerServerExit(WorkerServerExitKind.Shutdown, "shutdown");
        }
    }

    private async Task<WorkerServerExit> InitializeDeadlineExpiredAsync(
        WorkerRunOwnership ownership,
        long initializeRequestId,
        WorkerInitializeRequest initialize,
        CancellationToken cancellationToken)
    {
        var cleanupFailed = await ownership.StopFactoryAsync().ConfigureAwait(false);
        cleanupFailed |= await ownership.DrainSessionAsync().ConfigureAwait(false);
        var detailCode = cleanupFailed
            ? "initialize_cleanup_failed"
            : "initialize_deadline_expired";
        await WriteFailureAsync(
            initializeRequestId,
            initialize,
            detailCode,
            cancellationToken).ConfigureAwait(false);
        return new WorkerServerExit(
            cleanupFailed
                ? WorkerServerExitKind.RuntimeFailure
                : WorkerServerExitKind.InitializeFailed,
            detailCode);
    }

    private static void ValidateBeforeReady(
        WorkerEnvelope envelope,
        WorkerInitializeRequest initialize)
    {
        if (envelope.SessionId != initialize.SessionId)
        {
            throw new WorkerProtocolException(
                "session_identity_mismatch",
                "Worker protocol frame targets a different session identity.");
        }
        if (envelope.Incarnation != initialize.Incarnation)
        {
            throw new WorkerProtocolException(
                "worker_incarnation_mismatch",
                "Worker protocol frame targets a stale worker incarnation.");
        }
        throw new WorkerProtocolException(
            "message_before_ready",
            $"Worker received '{envelope.Kind}' before initialize completed.");
    }

    private bool DeadlineExpired(WorkerInitializeRequest initialize) =>
        _utcNow() >= initialize.DeadlineUtc;

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

    private async Task WriteFailureAsync(
        long requestId,
        WorkerInitializeRequest initialize,
        string detailCode,
        CancellationToken cancellationToken)
    {
        await WriteEnvelopeAsync(
            WorkerOperationProtocol.CreateResultEnvelope(
                initialize.SessionId,
                initialize.Incarnation,
                new WorkerResult(
                    requestId,
                    WorkerResultStatus.Failed,
                    string.Empty,
                    detailCode)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkerEnvelope?> ReadEnvelopeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new WorkerTransportException("request_transport_failure", exception);
        }
    }

    private async Task WriteEnvelopeAsync(
        WorkerEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            await _writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkerProtocolException exception)
        {
            if (exception.DetailCode == "writer_faulted")
            {
                throw new WorkerTransportException(
                    "event_transport_failure",
                    exception);
            }
            throw new WorkerRuntimeException("outbound_protocol_failure", exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new WorkerTransportException("event_transport_failure", exception);
        }
    }

    private static async Task<bool> TryDrainSessionAsync(IWorkerSession session)
    {
        var failed = false;
        try
        {
            await session.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failed = true;
        }
        try
        {
            session.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failed = true;
        }
        return failed;
    }

    private static void ObserveLateFactory(
        Task<IWorkerSession> factoryTask,
        CancellationTokenSource cancellation,
        Task? cancellationTask = null)
    {
        var cancellationHandoff = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ObserveLateFactoryAsync(factoryTask, cancellation, cancellationHandoff.Task);
        cancellationHandoff.TrySetResult(
            cancellationTask ?? BeginCancellation(cancellation));
    }

    private static async Task ObserveLateFactoryAsync(
        Task<IWorkerSession> factoryTask,
        CancellationTokenSource cancellation,
        Task<Task> cancellationHandoff)
    {
        var factoryObservation = ObserveLateFactoryResultAsync(factoryTask);
        var cancellationObservation = ObserveTaskAsync(
            await cancellationHandoff.ConfigureAwait(false));
        await Task.WhenAll(factoryObservation, cancellationObservation).ConfigureAwait(false);
        TryDispose(cancellation);
    }

    private static async Task ObserveLateFactoryResultAsync(
        Task<IWorkerSession> factoryTask)
    {
        try
        {
            var session = await factoryTask.ConfigureAwait(false);
            if (session is not null)
                _ = await TryDrainSessionAsync(session).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // The contained entry point is already leaving this boot. Observe
            // a late result and drain any lifetime it eventually publishes.
        }
    }

    private static void ObserveDetachedTask(
        Task task,
        CancellationTokenSource cancellation,
        Task? cancellationTask = null)
    {
        var cancellationHandoff = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ObserveDetachedTaskAsync(task, cancellation, cancellationHandoff.Task);
        cancellationHandoff.TrySetResult(
            cancellationTask ?? BeginCancellation(cancellation));
    }

    private static async Task ObserveDetachedTaskAsync(
        Task task,
        CancellationTokenSource cancellation,
        Task<Task> cancellationHandoff)
    {
        var taskObservation = ObserveTaskAsync(task);
        var cancellationObservation = ObserveTaskAsync(
            await cancellationHandoff.ConfigureAwait(false));
        await Task.WhenAll(taskObservation, cancellationObservation).ConfigureAwait(false);
        TryDispose(cancellation);
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // The owning protocol path has already selected its terminal result.
        }
    }

    private static Task BeginCancellation(CancellationTokenSource cancellation)
    {
        try
        {
            return cancellation.CancelAsync();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return Task.FromException(exception);
        }
    }

    private static void TryDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Cleanup is already terminal and must not retry partial disposal.
        }
    }

    private sealed class WorkerRunOwnership
    {
        private readonly object _factoryCancellationGate = new();
        private readonly CancellationTokenRegistration _hostCancellationRegistration;
        private readonly CancellationTokenRegistration _factoryHostCancellationRegistration;
        private Task? _factoryCancellationTask;

        internal WorkerRunOwnership(CancellationToken cancellationToken)
        {
            ReaderCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            FactoryCancellation = new CancellationTokenSource();
            if (!cancellationToken.CanBeCanceled)
            {
                HostCancellation = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task;
                return;
            }

            var cancellationSignal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            HostCancellation = cancellationSignal.Task;
            _hostCancellationRegistration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                cancellationSignal);
            _factoryHostCancellationRegistration = cancellationToken.Register(
                static state => ((WorkerRunOwnership)state!).RequestFactoryCancellation(),
                this);
        }

        internal CancellationTokenSource ReaderCancellation { get; }
        internal CancellationToken ReaderToken => ReaderCancellation.Token;
        internal Task HostCancellation { get; }
        internal CancellationToken FactoryToken => FactoryCancellation?.Token ??
            throw new InvalidOperationException("The worker runtime factory is no longer owned.");
        private CancellationTokenSource? FactoryCancellation { get; set; }
        internal Task<IWorkerSession>? FactoryTask { get; set; }
        internal CancellationTokenSource? DeadlineCancellation { get; set; }
        internal Task? DeadlineTask { get; set; }
        internal Task<WorkerEnvelope?>? PendingRead { get; set; }
        internal IWorkerSession? Session { get; set; }
        internal WorkerOperationScheduler? Scheduler { get; set; }

        internal Task<WorkerEnvelope?> TakePendingReadAsync()
        {
            var pendingRead = PendingRead
                ?? throw new InvalidOperationException("No worker protocol read is pending.");
            PendingRead = null;
            return pendingRead;
        }

        internal Task<IWorkerSession> TakeFactory()
        {
            var factoryTask = FactoryTask
                ?? throw new InvalidOperationException("No worker runtime factory is pending.");
            FactoryTask = null;
            return factoryTask;
        }

        internal void StopDeadline()
        {
            var deadlineTask = DeadlineTask;
            var deadlineCancellation = DeadlineCancellation;
            DeadlineTask = null;
            DeadlineCancellation = null;
            if (deadlineCancellation is null) return;
            ObserveDetachedTask(deadlineTask ?? Task.CompletedTask, deadlineCancellation);
        }

        internal async Task<bool> DrainSessionAsync()
        {
            var session = Session;
            Session = null;
            return session is not null &&
                await TryDrainSessionAsync(session).ConfigureAwait(false);
        }

        internal async Task<bool> DrainOperationsAsync(long? shutdownRequestId = null)
        {
            var scheduler = Scheduler;
            if (scheduler is null)
                return false;
            Task drain;
            try
            {
                drain = shutdownRequestId is { } requestId
                    ? scheduler.ShutdownAndDrainAsync(requestId)
                    : scheduler.CancelAndDrainAsync();
            }
            catch (WorkerProtocolException)
            {
                throw;
            }
            Scheduler = null;
            try
            {
                await drain.ConfigureAwait(false);
                return false;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                return true;
            }
        }

        internal async Task<bool> StopFactoryAsync()
        {
            var factoryTask = FactoryTask;
            FactoryTask = null;
            var (factoryCancellation, cancellationTask) =
                TakeFactoryCancellationOwnership();
            if (factoryCancellation is null) return false;

            if (factoryTask is null)
            {
                ObserveDetachedTask(
                    Task.CompletedTask,
                    factoryCancellation,
                    cancellationTask);
                return false;
            }
            if (!factoryTask.IsCompleted)
            {
                ObserveLateFactory(factoryTask, factoryCancellation, cancellationTask);
                return false;
            }
            ObserveDetachedTask(
                Task.CompletedTask,
                factoryCancellation,
                cancellationTask);
            try
            {
                var session = await factoryTask.ConfigureAwait(false);
                return session is not null &&
                    await TryDrainSessionAsync(session).ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                return false;
            }
        }

        internal async Task<bool> CleanupAsync()
        {
            var factoryCleanupFailed = await StopFactoryAsync().ConfigureAwait(false);

            StopDeadline();

            var pendingRead = PendingRead;
            PendingRead = null;
            ObserveDetachedTask(pendingRead ?? Task.CompletedTask, ReaderCancellation);

            var operationCleanupFailed = await DrainOperationsAsync().ConfigureAwait(false);
            var sessionCleanupFailed = await DrainSessionAsync().ConfigureAwait(false);
            TryDispose(_hostCancellationRegistration);
            return factoryCleanupFailed || operationCleanupFailed || sessionCleanupFailed;
        }

        private void RequestFactoryCancellation()
        {
            Task? cancellationTask;
            lock (_factoryCancellationGate)
            {
                if (FactoryCancellation is null) return;
                cancellationTask = _factoryCancellationTask ??=
                    BeginCancellation(FactoryCancellation);
            }
            _ = ObserveTaskAsync(cancellationTask);
        }

        private (CancellationTokenSource? Cancellation, Task? CancellationTask)
            TakeFactoryCancellationOwnership()
        {
            TryDispose(_factoryHostCancellationRegistration);
            lock (_factoryCancellationGate)
            {
                var cancellation = FactoryCancellation;
                var cancellationTask = _factoryCancellationTask;
                FactoryCancellation = null;
                _factoryCancellationTask = null;
                return (cancellation, cancellationTask);
            }
        }
    }

    private sealed class WorkerTransportException(
        string detailCode,
        Exception innerException) : IOException(innerException.Message, innerException)
    {
        internal string DetailCode { get; } = detailCode;
    }

    private sealed class WorkerRuntimeException(
        string detailCode,
        Exception innerException) : Exception(innerException.Message, innerException)
    {
        internal string DetailCode { get; } = detailCode;
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}
