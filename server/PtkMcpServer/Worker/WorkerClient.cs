using System.Text.Json;

namespace PtkMcpServer.Worker;

/// <summary>
/// Strict host-side owner for one worker protocol generation. One reader loop
/// validates every response and event, while the serialized writer preserves
/// monotonic host request IDs and targeted cancellation.
/// </summary>
internal sealed class WorkerClient : IAsyncDisposable
{
    internal const int MaximumPendingRequests = 64;

    private readonly object _gate = new();
    private readonly WorkerProtocolReader _reader;
    private readonly WorkerProtocolWriter _writer;
    private readonly Guid? _expectedWorkerBootId;
    private readonly Func<WorkerEnvelope, CancellationToken, ValueTask> _onEvent;
    private readonly CancellationTokenSource _readerCancellation = new();
    private readonly Dictionary<long, PendingRequest> _pending = [];
    private readonly TaskCompletionSource _fatal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private long _requestIdHighWater;
    private long _generation;
    private Guid _workerBootId;
    private Task? _readerTask;
    private bool _initializing;
    private bool _initialized;
    private bool _stopping;
    private bool _stopped;
    private int _disposed;

    internal WorkerClient(
        Stream requestStream,
        Stream eventStream,
        Guid? expectedWorkerBootId,
        Func<WorkerEnvelope, CancellationToken, ValueTask>? onEvent = null)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(eventStream);
        if (expectedWorkerBootId == Guid.Empty)
        {
            throw new ArgumentException(
                "Expected worker boot ID cannot be empty.",
                nameof(expectedWorkerBootId));
        }

        _writer = new WorkerProtocolWriter(requestStream);
        _reader = new WorkerProtocolReader(eventStream);
        _expectedWorkerBootId = expectedWorkerBootId;
        _onEvent = onEvent ?? ((_, _) => ValueTask.CompletedTask);
    }

    internal Task Fatal => _fatal.Task;
    internal Guid WorkerBootId
    {
        get
        {
            lock (_gate)
            {
                if (!_initialized)
                    throw new InvalidOperationException("Worker client is not initialized.");
                return _workerBootId;
            }
        }
    }

    internal long Generation
    {
        get
        {
            lock (_gate) return _generation;
        }
    }

    internal async Task InitializeAsync(
        long generation,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken = default)
    {
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        lock (_gate)
        {
            ThrowIfStopped();
            if (_initializing || _initialized)
                throw new InvalidOperationException("Worker client initialization runs once.");
            _initializing = true;
        }

        try
        {
            var hello = await ReadRequiredAsync(cancellationToken).ConfigureAwait(false);
            ValidateHello(hello);

            const long initializeRequestId = 1;
            var initialize = new WorkerEnvelope(
                WorkerProtocol.Version,
                WorkerMessageKind.Initialize,
                _workerBootId,
                initializeRequestId,
                JsonSerializer.SerializeToElement(new
                {
                    generation,
                    deadlineUnixTimeMilliseconds = deadlineUtc.ToUnixTimeMilliseconds(),
                }));
            await WriteAsync(initialize, cancellationToken).ConfigureAwait(false);

            var ready = await ReadRequiredAsync(cancellationToken).ConfigureAwait(false);
            ValidateReady(ready, initializeRequestId, generation);
            lock (_gate)
            {
                ThrowIfStopped();
                _generation = generation;
                _requestIdHighWater = initializeRequestId;
                _initializing = false;
                _initialized = true;
                _readerTask = Task.Run(ReadLoopAsync);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            lock (_gate) _initializing = false;
            Fail(new IOException("Worker initialization failed.", exception));
            throw;
        }
    }

    internal async Task<WorkerOperationResponse> ExecuteAsync(
        string operation,
        WorkerSessionOperationArguments arguments,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(
                operation,
                WorkerSessionOperationCodec.InvokeOperation,
                StringComparison.Ordinal))
        {
            throw new WorkerProtocolException(
                "ordinary_invoke_forbidden",
                "Script-bearing invoke requires the prepared operation protocol.");
        }
        var generation = RequireGeneration();
        var requestId = ReserveRequest();
        var payload = WorkerSessionOperationCodec.CreateArguments(operation, arguments);
        var response = await SendAsync(
            WorkerOperationProtocol.CreateRequestEnvelope(
                _workerBootId,
                requestId,
                generation,
                deadlineUtc,
                operation,
                payload),
            requestId,
            sendCancelOnCancellation: true,
            cancellationToken).ConfigureAwait(false);
        return ValidateResponse(() =>
            WorkerOperationProtocol.ParseResponse(
                response,
                _workerBootId,
                generation));
    }

    internal async Task<WorkerPreparedPlanDescriptor> PrepareAsync(
        WorkerInvokePreparePayload prepare,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        var generation = RequireGeneration();
        if (prepare.Generation != generation)
            throw new ArgumentException(
                "Prepared invocation targets a different worker generation.",
                nameof(prepare));
        var requestId = ReserveRequest();
        var response = await SendAsync(
            Envelope(
                WorkerMessageKind.Prepare,
                requestId,
                WorkerPreparedOperationCodec.CreatePrepare(prepare)),
            requestId,
            sendCancelOnCancellation: true,
            cancellationToken).ConfigureAwait(false);
        return ValidateResponse(() =>
        {
            var descriptor = WorkerPreparedOperationProtocol.ParsePreparedResponse(
                response,
                _workerBootId,
                requestId,
                generation);
            if (WorkerPreparedOperationCodec.ComparePreparedToPrepare(
                    prepare,
                    new WorkerPreparedCorrelation(
                        descriptor.PlanId,
                        descriptor.ScriptDigest,
                        descriptor.Generation,
                        descriptor.DeadlineUtc)) != WorkerPreparedCorrelationMatch.Match)
            {
                throw new WorkerProtocolException(
                    "prepared_correlation_mismatch",
                    "Worker prepared descriptor does not correlate to its request.");
            }
            return descriptor;
        });
    }

    internal Task<WorkerOperationResponse> CommitAsync(
        WorkerCommitPayload commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return SendPreparedTerminalAsync(
            WorkerMessageKind.Commit,
            WorkerPreparedOperationCodec.CreateCommit(commit),
            commit.Generation,
            cancellationToken);
    }

    internal Task<WorkerOperationResponse> AbortAsync(
        WorkerAbortPayload abort,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(abort);
        return SendPreparedTerminalAsync(
            WorkerMessageKind.Abort,
            WorkerPreparedOperationCodec.CreateAbort(abort),
            abort.Generation,
            cancellationToken);
    }

    internal async Task CancelAsync(
        long targetRequestId,
        CancellationToken cancellationToken = default)
    {
        if (targetRequestId <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRequestId));
        var generation = RequireGeneration();
        await WriteAsync(
            WorkerOperationProtocol.CreateCancelEnvelope(
                _workerBootId,
                targetRequestId,
                generation),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var generation = RequireGeneration();
        var requestId = ReserveShutdownRequest();
        try
        {
            var response = await SendAsync(
                Envelope(
                    WorkerMessageKind.Shutdown,
                    requestId,
                    JsonSerializer.SerializeToElement(new { })),
                requestId,
                sendCancelOnCancellation: false,
                cancellationToken,
                allowStopping: true).ConfigureAwait(false);
            _ = ValidateResponse(() =>
            {
                ValidateStopped(response, requestId, generation);
                return true;
            });
            lock (_gate) _stopped = true;
        }
        catch
        {
            lock (_gate) _stopping = false;
            throw;
        }
    }

    private async Task<WorkerOperationResponse> SendPreparedTerminalAsync(
        WorkerMessageKind kind,
        JsonElement payload,
        long generation,
        CancellationToken cancellationToken)
    {
        if (generation != RequireGeneration())
            throw new ArgumentException(
                "Prepared terminal targets a different worker generation.",
                nameof(generation));
        var requestId = ReserveRequest();
        var response = await SendAsync(
            Envelope(kind, requestId, payload),
            requestId,
            sendCancelOnCancellation: true,
            cancellationToken).ConfigureAwait(false);
        return ValidateResponse(() =>
            WorkerOperationProtocol.ParseResponse(
                response,
                _workerBootId,
                generation));
    }

    private async Task<WorkerEnvelope> SendAsync(
        WorkerEnvelope envelope,
        long requestId,
        bool sendCancelOnCancellation,
        CancellationToken cancellationToken,
        bool allowStopping = false)
    {
        var pending = RegisterPending(requestId, allowStopping);
        try
        {
            await WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RemovePending(requestId, pending);
            throw;
        }

        try
        {
            return await pending.Response.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested &&
            sendCancelOnCancellation)
        {
            try
            {
                await CancelAsync(requestId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                Fail(new IOException("Worker cancellation delivery failed.", exception));
            }
            throw;
        }
    }

    private PendingRequest RegisterPending(long requestId, bool allowStopping)
    {
        lock (_gate)
        {
            ThrowIfUnavailable(allowStopping);
            if (_pending.Count >= MaximumPendingRequests)
            {
                throw new WorkerProtocolException(
                    "worker_client_capacity_exceeded",
                    "Worker client request capacity is exhausted.");
            }
            var pending = new PendingRequest();
            if (!_pending.TryAdd(requestId, pending))
            {
                throw new WorkerProtocolException(
                    "operation_request_replay",
                    "Worker client request IDs cannot be reused.");
            }
            return pending;
        }
    }

    private long ReserveRequest()
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_requestIdHighWater == long.MaxValue)
            {
                throw new WorkerProtocolException(
                    "worker_request_id_exhausted",
                    "Worker client request ID space is exhausted.");
            }
            return ++_requestIdHighWater;
        }
    }

    private long ReserveShutdownRequest()
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_requestIdHighWater == long.MaxValue)
            {
                throw new WorkerProtocolException(
                    "worker_request_id_exhausted",
                    "Worker client request ID space is exhausted.");
            }
            _stopping = true;
            return ++_requestIdHighWater;
        }
    }

    private long RequireGeneration()
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            return _generation;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var envelope = await _reader.ReadAsync(_readerCancellation.Token)
                    .ConfigureAwait(false);
                if (envelope is null)
                {
                    lock (_gate)
                    {
                        if (_stopped || _stopping && _pending.Count == 0)
                            return;
                    }
                    throw new IOException("Worker event stream ended.");
                }
                ValidateOperationalIdentity(envelope);
                switch (envelope.Kind)
                {
                    case WorkerMessageKind.Response:
                        CompleteResponse(envelope);
                        break;
                    case WorkerMessageKind.Event:
                        ValidateOperationalEvent(envelope);
                        await _onEvent(envelope, _readerCancellation.Token)
                            .ConfigureAwait(false);
                        break;
                    default:
                        throw new WorkerProtocolException(
                            "unexpected_worker_message",
                            "Worker emitted an unexpected operational message kind.");
                }
            }
        }
        catch (OperationCanceledException) when (_readerCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            Fail(new IOException("Worker event processing failed.", exception));
        }
    }

    private void CompleteResponse(WorkerEnvelope envelope)
    {
        if (envelope.RequestId is not > 0)
        {
            throw new WorkerProtocolException(
                "request_id_required",
                "Worker response requires a positive request ID.");
        }

        PendingRequest pending;
        lock (_gate)
        {
            if (!_pending.Remove(envelope.RequestId.Value, out pending!))
            {
                throw new WorkerProtocolException(
                    "unexpected_worker_response",
                    "Worker response does not correlate to one pending request.");
            }
        }
        pending.Response.TrySetResult(envelope);
    }

    private void RemovePending(long requestId, PendingRequest pending)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(requestId, out var current) &&
                ReferenceEquals(current, pending))
            {
                _pending.Remove(requestId);
            }
        }
    }

    private T ValidateResponse<T>(Func<T> validation)
    {
        try
        {
            return validation();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            Fail(new IOException("Worker response validation failed.", exception));
            throw;
        }
    }

    private async Task<WorkerEnvelope> ReadRequiredAsync(
        CancellationToken cancellationToken)
    {
        var envelope = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return envelope ?? throw new IOException("Worker event stream ended.");
    }

    private async Task WriteAsync(
        WorkerEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            await _writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var failure = new IOException("Worker request transport failed.", exception);
            Fail(failure);
            throw failure;
        }
    }

    private void ValidateHello(WorkerEnvelope envelope)
    {
        if (!IsCanonicalUuidV4(envelope.WorkerBootId.ToString("D")) ||
            _expectedWorkerBootId is { } expected &&
            envelope.WorkerBootId != expected)
        {
            throw new WorkerProtocolException(
                "worker_boot_mismatch",
                "Worker hello has an invalid or unexpected boot identity.");
        }
        if (envelope.Kind != WorkerMessageKind.Event ||
            envelope.RequestId is not null)
        {
            throw new WorkerProtocolException(
                "worker_hello_required",
                "Worker must emit hello before initialization.");
        }
        var fields = ClosedObject(envelope.Payload, "event");
        if (RequiredString(fields, "event") != "hello")
        {
            throw new WorkerProtocolException(
                "worker_hello_required",
                "Worker must emit the exact hello event.");
        }
        _workerBootId = envelope.WorkerBootId;
    }

    private void ValidateReady(
        WorkerEnvelope envelope,
        long requestId,
        long generation)
    {
        ValidateIdentity(envelope);
        if (envelope.Kind != WorkerMessageKind.Response ||
            envelope.RequestId != requestId)
        {
            throw new WorkerProtocolException(
                "worker_ready_required",
                "Worker initialization did not return its correlated ready response.");
        }
        var fields = ClosedObject(envelope.Payload, "status", "generation");
        if (RequiredString(fields, "status") != "ready" ||
            RequiredPositiveInt64(fields, "generation") != generation)
        {
            throw new WorkerProtocolException(
                "worker_ready_required",
                "Worker initialization response is not ready for the expected generation.");
        }
    }

    private void ValidateStopped(
        WorkerEnvelope envelope,
        long requestId,
        long generation)
    {
        ValidateOperationalIdentity(envelope);
        if (envelope.Kind != WorkerMessageKind.Response ||
            envelope.RequestId != requestId)
        {
            throw new WorkerProtocolException(
                "worker_stopped_required",
                "Worker shutdown did not return its correlated stopped response.");
        }
        var fields = ClosedObject(envelope.Payload, "status", "generation");
        if (RequiredString(fields, "status") != "stopped" ||
            RequiredPositiveInt64(fields, "generation") != generation)
        {
            throw new WorkerProtocolException(
                "worker_stopped_required",
                "Worker shutdown response is not stopped for the expected generation.");
        }
    }

    private void ValidateOperationalIdentity(WorkerEnvelope envelope)
    {
        ValidateIdentity(envelope);
    }

    private void ValidateIdentity(WorkerEnvelope envelope)
    {
        if (envelope.WorkerBootId != _workerBootId)
        {
            throw new WorkerProtocolException(
                "worker_boot_mismatch",
                "Worker frame belongs to a different boot.");
        }
    }

    private void ValidateOperationalEvent(WorkerEnvelope envelope)
    {
        if (envelope.RequestId is not null)
        {
            throw new WorkerProtocolException(
                "invalid_worker_event",
                "Worker events cannot carry request IDs.");
        }

        if (envelope.Payload.ValueKind != JsonValueKind.Object)
            throw InvalidWorkerEvent();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in envelope.Payload.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value))
                throw InvalidWorkerEvent();
        }
        var eventName = RequiredString(fields, "event");
        string[] allowed = eventName switch
        {
            "validator_started" =>
            [
                "event",
                "generation",
                "planId",
                "descriptorDigest",
                "executionPath",
            ],
            "validator_completed" =>
            [
                "event",
                "generation",
                "planId",
                "descriptorDigest",
                "executionPath",
                "detailCode",
                "processStarted",
                "exitCode",
                "rootTerminationConfirmed",
            ],
            _ => throw new WorkerProtocolException(
                "unknown_worker_event",
                "Worker emitted an unknown operational event."),
        };
        if (fields.Count != allowed.Length ||
            fields.Keys.Any(name => !allowed.Contains(name, StringComparer.Ordinal)) ||
            RequiredPositiveInt64(fields, "generation") != Generation ||
            !IsCanonicalUuidV4(RequiredString(fields, "planId")) ||
            !IsLowerSha256(RequiredString(fields, "descriptorDigest")) ||
            RequiredString(fields, "executionPath") != "bash_via_rtk")
        {
            throw InvalidWorkerEvent();
        }
        if (eventName == "validator_completed")
        {
            if (!IsCode(RequiredString(fields, "detailCode")) ||
                fields["processStarted"].ValueKind is not
                    (JsonValueKind.True or JsonValueKind.False) ||
                fields["exitCode"].ValueKind is not
                    (JsonValueKind.Null or JsonValueKind.Number) ||
                fields["exitCode"].ValueKind == JsonValueKind.Number &&
                    !fields["exitCode"].TryGetInt32(out _) ||
                fields["rootTerminationConfirmed"].ValueKind is not
                    (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
            {
                throw InvalidWorkerEvent();
            }
        }
    }

    private WorkerEnvelope Envelope(
        WorkerMessageKind kind,
        long requestId,
        JsonElement payload) =>
        new(WorkerProtocol.Version, kind, _workerBootId, requestId, payload);

    private void ThrowIfUnavailable(bool allowStopping = false)
    {
        ThrowIfStopped();
        if (_stopping && !allowStopping)
            throw new InvalidOperationException("Worker client is stopping.");
        if (!_initialized)
            throw new InvalidOperationException("Worker client is not initialized.");
        if (_fatal.Task.IsCompleted)
            throw new IOException("Worker client has failed.");
    }

    private void ThrowIfStopped()
    {
        if (_stopped)
            throw new InvalidOperationException("Worker client has stopped.");
    }

    private void Fail(Exception exception)
    {
        PendingRequest[] pending;
        lock (_gate)
        {
            if (_fatal.Task.IsCompleted) return;
            _stopped = true;
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }
        _fatal.TrySetException(exception);
        foreach (var request in pending)
            request.Response.TrySetException(exception);
        try
        {
            _readerCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Task? readerTask;
        PendingRequest[] pending;
        lock (_gate)
        {
            _stopped = true;
            readerTask = _readerTask;
            _readerTask = null;
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }
        foreach (var request in pending)
        {
            request.Response.TrySetException(
                new ObjectDisposedException(nameof(WorkerClient)));
        }
        await _readerCancellation.CancelAsync().ConfigureAwait(false);
        if (readerTask is not null)
        {
            try
            {
                await readerTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
        }
        _readerCancellation.Dispose();
    }

    private static Dictionary<string, JsonElement> ClosedObject(
        JsonElement payload,
        params string[] allowed)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw InvalidPayload();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) ||
                !fields.TryAdd(property.Name, property.Value))
            {
                throw InvalidPayload();
            }
        }
        if (fields.Count != allowed.Length)
            throw InvalidPayload();
        return fields;
    }

    private static string RequiredString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw InvalidPayload();
        }
        return value.GetString() ?? throw InvalidPayload();
    }

    private static long RequiredPositiveInt64(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var parsed) ||
            parsed <= 0)
        {
            throw InvalidPayload();
        }
        return parsed;
    }

    private static WorkerProtocolException InvalidPayload() =>
        new("invalid_worker_lifecycle_payload", "Worker lifecycle payload is invalid.");

    private static WorkerProtocolException InvalidWorkerEvent() =>
        new("invalid_worker_event", "Worker operational event is invalid.");

    private static bool IsCanonicalUuidV4(string value) =>
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal) &&
        value[14] == '4' &&
        value[19] is '8' or '9' or 'a' or 'b';

    private static bool IsLowerSha256(string value) =>
        value.Length == 64 &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCode(string value) =>
        value.Length is > 0 and <= WorkerOperationProtocol.MaximumCodeLength &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class PendingRequest
    {
        internal TaskCompletionSource<WorkerEnvelope> Response { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
