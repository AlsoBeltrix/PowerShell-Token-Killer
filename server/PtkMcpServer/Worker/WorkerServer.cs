using System.Text.Json;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Worker;

internal sealed record WorkerInitializeRequest(
    long Generation,
    DateTimeOffset DeadlineUtc);

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
    private static readonly Task Never = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously).Task;

    private readonly WorkerProtocolReader _reader;
    private readonly WorkerProtocolWriter _writer;
    private readonly Func<WorkerInitializeRequest, CancellationToken, Task<ISessionLifetime>>
        _runtimeFactory;
    private readonly Guid _workerBootId;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<DateTimeOffset, CancellationToken, Task> _waitUntilDeadline;
    private readonly TaskScheduler _factoryScheduler;
    private int _started;

    internal WorkerServer(
        Stream requestStream,
        Stream eventStream,
        Func<WorkerInitializeRequest, CancellationToken, Task<ISessionLifetime>> runtimeFactory,
        Guid workerBootId,
        Func<DateTimeOffset>? utcNow = null,
        Func<DateTimeOffset, CancellationToken, Task>? waitUntilDeadline = null,
        TaskScheduler? factoryScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(eventStream);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        if (workerBootId == Guid.Empty)
            throw new ArgumentException("Worker boot ID cannot be empty.", nameof(workerBootId));

        _reader = new WorkerProtocolReader(requestStream);
        _writer = new WorkerProtocolWriter(eventStream);
        _runtimeFactory = runtimeFactory;
        _workerBootId = workerBootId;
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
        await WriteEnvelopeAsync(
            Envelope(
                WorkerMessageKind.Event,
                requestId: null,
                JsonSerializer.SerializeToElement(new { @event = "hello" })),
            cancellationToken).ConfigureAwait(false);

        ownership.PendingRead = ReadEnvelopeAsync(ownership.ReaderToken);
        await Task.WhenAny(
            ownership.PendingRead,
            ownership.HostCancellation).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var initializeEnvelope = await ownership.TakePendingReadAsync().ConfigureAwait(false);
        if (initializeEnvelope is null)
            return new WorkerServerExit(WorkerServerExitKind.Eof, "eof_before_initialize");
        ValidateBootId(initializeEnvelope);
        if (initializeEnvelope.Kind != WorkerMessageKind.Initialize)
        {
            throw new WorkerProtocolException(
                "initialize_required",
                "The first supervisor frame must initialize the worker.");
        }
        var initializeRequestId = RequireRequestId(initializeEnvelope);
        var initialize = ParseInitialize(initializeEnvelope.Payload);

        if (DeadlineExpired(initialize))
        {
            await WriteFailureAsync(
                initializeRequestId,
                initialize.Generation,
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
            ValidateBeforeReady(queuedEnvelope);
        }
        if (ownership.DeadlineTask.IsCompleted)
        {
            await ownership.DeadlineTask.ConfigureAwait(false);
            return await InitializeDeadlineExpiredAsync(
                ownership,
                initializeRequestId,
                initialize.Generation,
                cancellationToken).ConfigureAwait(false);
        }
        if (DeadlineExpired(initialize))
        {
            return await InitializeDeadlineExpiredAsync(
                ownership,
                initializeRequestId,
                initialize.Generation,
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
                ValidateBeforeReady(queuedEnvelope);
            }
            if (ownership.DeadlineTask.IsCompleted)
            {
                await ownership.DeadlineTask.ConfigureAwait(false);
                return await InitializeDeadlineExpiredAsync(
                    ownership,
                    initializeRequestId,
                    initialize.Generation,
                    cancellationToken).ConfigureAwait(false);
            }
            if (DeadlineExpired(initialize))
            {
                return await InitializeDeadlineExpiredAsync(
                    ownership,
                    initializeRequestId,
                    initialize.Generation,
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
                    initialize.Generation,
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
                    initialize.Generation,
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
                ValidateBeforeReady(queuedEnvelope);
            }
            if (DeadlineExpired(initialize))
            {
                return await InitializeDeadlineExpiredAsync(
                    ownership,
                    initializeRequestId,
                    initialize.Generation,
                    cancellationToken).ConfigureAwait(false);
            }

            // The supervisor must wait for this response before sending the
            // next frame. Dedicated pipes have no cross-stream total order;
            // once the ready write is admitted, the retained read belongs to
            // the ready-state protocol.
            await WriteEnvelopeAsync(
                Envelope(
                    WorkerMessageKind.Response,
                    initializeRequestId,
                    JsonSerializer.SerializeToElement(new
                    {
                        status = "ready",
                        generation = initialize.Generation,
                    })),
                cancellationToken).ConfigureAwait(false);
            break;
        }

        if (ownership.Session is IWorkerSessionRuntime workerRuntime)
        {
            ownership.OperationScheduler = new WorkerOperationScheduler(
                _workerBootId,
                initialize.Generation,
                initializeRequestId,
                workerRuntime,
                WriteEnvelopeAsync,
                _utcNow,
                _waitUntilDeadline);
        }

        var requestIdHighWater = initializeRequestId;
        while (true)
        {
            ownership.PendingRead ??= ReadEnvelopeAsync(ownership.ReaderToken);
            var schedulerFatal = ownership.OperationScheduler?.Fatal;
            await Task.WhenAny(
                ownership.PendingRead,
                ownership.HostCancellation,
                schedulerFatal ?? Never)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (schedulerFatal?.IsCompleted == true)
            {
                try
                {
                    await schedulerFatal.ConfigureAwait(false);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    throw new WorkerRuntimeException(
                        "operation_scheduler_failed",
                        exception);
                }
            }

            var envelope = await ownership.TakePendingReadAsync().ConfigureAwait(false);
            if (envelope is null)
                return new WorkerServerExit(WorkerServerExitKind.Eof, "eof_after_ready");
            ValidateBootId(envelope);
            switch (envelope.Kind)
            {
                case WorkerMessageKind.Request:
                {
                    var request = WorkerOperationProtocol.ParseRequest(
                        envelope,
                        _workerBootId,
                        initialize.Generation);
                    AdvanceRequestId(ref requestIdHighWater, request.RequestId);
                    if (request.Operation == WorkerSessionOperationCodec.InvokeOperation)
                    {
                        throw new WorkerProtocolException(
                            "ordinary_invoke_forbidden",
                            "Script-bearing invoke requires the prepared operation protocol.");
                    }
                    RequireOperationScheduler(ownership).Admit(envelope);
                    continue;
                }
                case WorkerMessageKind.Cancel:
                    _ = WorkerOperationProtocol.ParseCancel(
                        envelope,
                        _workerBootId,
                        initialize.Generation);
                    RequireOperationScheduler(ownership).Admit(envelope);
                    continue;
                case WorkerMessageKind.Shutdown:
                    break;
                default:
                    throw new WorkerProtocolException(
                        "unsupported_message",
                        $"Worker lifecycle core does not accept '{envelope.Kind}' after initialize.");
            }

            var shutdownRequestId = RequireRequestId(envelope);
            AdvanceRequestId(ref requestIdHighWater, shutdownRequestId);
            RequireEmptyPayload(envelope.Payload, WorkerMessageKind.Shutdown);
            var shutdownFailed = await ownership.DrainSessionAsync().ConfigureAwait(false);
            if (shutdownFailed)
            {
                await WriteFailureAsync(
                    shutdownRequestId,
                    initialize.Generation,
                    "shutdown_failed",
                    cancellationToken).ConfigureAwait(false);
                return new WorkerServerExit(
                    WorkerServerExitKind.RuntimeFailure,
                    "shutdown_failed");
            }

            await WriteEnvelopeAsync(
                Envelope(
                    WorkerMessageKind.Response,
                    shutdownRequestId,
                    JsonSerializer.SerializeToElement(new
                    {
                        status = "stopped",
                        generation = initialize.Generation,
                    })),
                cancellationToken).ConfigureAwait(false);
            return new WorkerServerExit(WorkerServerExitKind.Shutdown, "shutdown");
        }
    }

    private static WorkerOperationScheduler RequireOperationScheduler(
        WorkerRunOwnership ownership) =>
        ownership.OperationScheduler ??
        throw new WorkerRuntimeException(
            "runtime_operations_unavailable",
            new InvalidOperationException(
                "The initialized worker runtime does not implement worker operations."));

    private static void AdvanceRequestId(ref long highWater, long requestId)
    {
        if (requestId <= highWater)
        {
            throw new WorkerProtocolException(
                "operation_request_replay",
                "Worker request IDs must increase strictly.");
        }
        highWater = requestId;
    }

    private async Task<WorkerServerExit> InitializeDeadlineExpiredAsync(
        WorkerRunOwnership ownership,
        long initializeRequestId,
        long generation,
        CancellationToken cancellationToken)
    {
        var cleanupFailed = await ownership.StopFactoryAsync().ConfigureAwait(false);
        cleanupFailed |= await ownership.DrainSessionAsync().ConfigureAwait(false);
        var detailCode = cleanupFailed
            ? "initialize_cleanup_failed"
            : "initialize_deadline_expired";
        await WriteFailureAsync(
            initializeRequestId,
            generation,
            detailCode,
            cancellationToken).ConfigureAwait(false);
        return new WorkerServerExit(
            cleanupFailed
                ? WorkerServerExitKind.RuntimeFailure
                : WorkerServerExitKind.InitializeFailed,
            detailCode);
    }

    private WorkerEnvelope Envelope(
        WorkerMessageKind kind,
        long? requestId,
        JsonElement payload)
        => new(WorkerProtocol.Version, kind, _workerBootId, requestId, payload);

    private void ValidateBootId(WorkerEnvelope envelope)
    {
        if (envelope.WorkerBootId != _workerBootId)
        {
            throw new WorkerProtocolException(
                "worker_boot_mismatch",
                "Worker protocol frame targets a different worker boot.");
        }
    }

    private void ValidateBeforeReady(WorkerEnvelope envelope)
    {
        ValidateBootId(envelope);
        throw new WorkerProtocolException(
            "message_before_ready",
            $"Worker received '{envelope.Kind}' before initialize completed.");
    }

    private static long RequireRequestId(WorkerEnvelope envelope)
    {
        if (envelope.RequestId is not > 0)
        {
            throw new WorkerProtocolException(
                "request_id_required",
                $"Worker protocol kind '{envelope.Kind}' requires a positive request ID.");
        }
        return envelope.RequestId.Value;
    }

    private static WorkerInitializeRequest ParseInitialize(JsonElement payload)
    {
        long? generation = null;
        long? deadlineUnixTimeMilliseconds = null;
        foreach (var property in payload.EnumerateObject())
        {
            switch (property.Name)
            {
                case "generation":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt64(out var parsedGeneration) ||
                        parsedGeneration <= 0)
                    {
                        throw InvalidInitialize("generation");
                    }
                    generation = parsedGeneration;
                    break;
                case "deadlineUnixTimeMilliseconds":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt64(out var parsedDeadline) ||
                        parsedDeadline <= 0)
                    {
                        throw InvalidInitialize("deadlineUnixTimeMilliseconds");
                    }
                    deadlineUnixTimeMilliseconds = parsedDeadline;
                    break;
                default:
                    throw new WorkerProtocolException(
                        "unknown_initialize_field",
                        $"Worker initialize payload contains unknown field '{property.Name}'.");
            }
        }

        if (generation is null || deadlineUnixTimeMilliseconds is null)
        {
            throw new WorkerProtocolException(
                "missing_initialize_field",
                "Worker initialize payload is missing a required field.");
        }

        DateTimeOffset deadlineUtc;
        try
        {
            deadlineUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                deadlineUnixTimeMilliseconds.Value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new WorkerProtocolException(
                "invalid_initialize_field",
                "Worker initialize deadline is outside the supported UTC range.",
                exception);
        }

        return new WorkerInitializeRequest(generation.Value, deadlineUtc);
    }

    private static WorkerProtocolException InvalidInitialize(string field) =>
        new(
            "invalid_initialize_field",
            $"Worker initialize field '{field}' is invalid.");

    private static void RequireEmptyPayload(JsonElement payload, WorkerMessageKind kind)
    {
        if (payload.EnumerateObject().Any())
        {
            throw new WorkerProtocolException(
                "invalid_payload",
                $"Worker protocol kind '{kind}' requires an empty payload.");
        }
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
        long generation,
        string detailCode,
        CancellationToken cancellationToken)
    {
        await WriteEnvelopeAsync(
            Envelope(
                WorkerMessageKind.Response,
                requestId,
                JsonSerializer.SerializeToElement(new
                {
                    status = "failed",
                    detailCode,
                    generation,
                })),
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

    private static async Task<bool> TryDrainSessionAsync(ISessionLifetime session)
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
        Task<ISessionLifetime> factoryTask,
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
        Task<ISessionLifetime> factoryTask,
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
        Task<ISessionLifetime> factoryTask)
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
        internal Task<ISessionLifetime>? FactoryTask { get; set; }
        internal CancellationTokenSource? DeadlineCancellation { get; set; }
        internal Task? DeadlineTask { get; set; }
        internal Task<WorkerEnvelope?>? PendingRead { get; set; }
        internal ISessionLifetime? Session { get; set; }
        internal WorkerOperationScheduler? OperationScheduler { get; set; }

        internal Task<WorkerEnvelope?> TakePendingReadAsync()
        {
            var pendingRead = PendingRead
                ?? throw new InvalidOperationException("No worker protocol read is pending.");
            PendingRead = null;
            return pendingRead;
        }

        internal Task<ISessionLifetime> TakeFactory()
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
            var failed = false;
            var scheduler = OperationScheduler;
            OperationScheduler = null;
            if (scheduler is not null)
            {
                try
                {
                    await scheduler.CancelAndDrainAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    failed = true;
                }
            }

            var session = Session;
            Session = null;
            if (session is not null)
                failed |= await TryDrainSessionAsync(session).ConfigureAwait(false);
            return failed;
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

            var sessionCleanupFailed = await DrainSessionAsync().ConfigureAwait(false);
            TryDispose(_hostCancellationRegistration);
            return factoryCleanupFailed || sessionCleanupFailed;
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
