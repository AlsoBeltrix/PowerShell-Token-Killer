using System.Security.Cryptography;
using System.Text;
using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

internal enum WorkerPrivateHostRuntimeState
{
    Created,
    Initializing,
    Ready,
    Replacing,
    Stopping,
    Stopped,
    Faulted,
}

/// <summary>
/// Production private-host runtime for one declared default alias. Every live
/// operation is routed to one contained worker slot; script-bearing work uses
/// the prepared dispatcher and ordinary job work uses the worker request
/// protocol. Reset and restart replace the whole worker generation.
/// </summary>
internal sealed class WorkerPrivateHostRuntime : IPrivateHostRuntime
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly object _gate = new();
    private readonly PrivateHostServerIdentity _identity;
    private readonly IPrivateHostEventSink _events;
    private readonly PrivateHostWorkerSlotFactory _slots;
    private readonly PrivateHostPreparedInvokeDispatcher _prepared;
    private readonly PrivateHostWorkerEventBridge _workerEvents;
    private readonly IPrivateHostOutputTransfer _output;
    private readonly Func<CapabilityToken> _createJobCapability;
    private readonly Func<long> _unixTimeMilliseconds;
    private readonly Dictionary<long, CapabilityToken> _jobCapabilities = [];

    private RecoveryBinding? _binding;
    private WorkerGenerationHighWatermark? _generationHighWatermark;
    private PrivateHostWorkerSlot? _slot;
    private WorkerPrivateHostRuntimeState _state;

    internal WorkerPrivateHostRuntime(
        PrivateHostServerIdentity identity,
        IPrivateHostEventSink events,
        PrivateHostWorkerSlotFactory slots,
        PrivateHostPreparedInvokeDispatcher prepared,
        PrivateHostWorkerEventBridge workerEvents,
        IPrivateHostOutputTransfer output,
        Func<CapabilityToken>? createJobCapability = null,
        Func<long>? unixTimeMilliseconds = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
        _prepared = prepared ?? throw new ArgumentNullException(nameof(prepared));
        _workerEvents = workerEvents ??
            throw new ArgumentNullException(nameof(workerEvents));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _createJobCapability = createJobCapability ?? CreateCapabilityToken;
        _unixTimeMilliseconds = unixTimeMilliseconds ??
            (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal WorkerPrivateHostRuntimeState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    internal GuardianHostWorkerIdentity? WorkerIdentity
    {
        get
        {
            lock (_gate) return _slot?.Identity;
        }
    }

    public async ValueTask InitializeAsync(
        PrivateHostInitialization initialization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialization);
        lock (_gate)
        {
            if (_state != WorkerPrivateHostRuntimeState.Created)
            {
                throw new InvalidOperationException(
                    "The worker private-host runtime is single-use.");
            }
            _state = WorkerPrivateHostRuntimeState.Initializing;
        }

        PrivateHostWorkerSlot? created = null;
        try
        {
            var (binding, highWatermark) =
                ValidateInitialization(initialization);
            created = await _slots.CreateAsync(
                    binding,
                    highWatermark,
                    _workerEvents.HandleAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteReadyLifecycleAsync(
                    requestId: null,
                    binding,
                    created.Identity,
                    GuardianHostSessionLifecycleReason.AutomaticRecovery,
                    warmStateLost: false,
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (_state != WorkerPrivateHostRuntimeState.Initializing)
                {
                    throw new InvalidOperationException(
                        "Worker private-host initialization state changed unexpectedly.");
                }
                _binding = binding;
                _generationHighWatermark = new WorkerGenerationHighWatermark(
                    created.Identity.Generation.Value);
                _slot = created;
                created = null;
                _state = WorkerPrivateHostRuntimeState.Ready;
            }
        }
        catch
        {
            if (created is not null)
            {
                _workerEvents.RetireWorker(created.Identity);
                await created.DisposeAsync().ConfigureAwait(false);
            }
            lock (_gate) _state = WorkerPrivateHostRuntimeState.Faulted;
            throw;
        }
    }

    public async ValueTask<PrivateHostOperationOutcome> ExecuteOperationAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateAndBind(request, cancellationToken);
        if (validation.Error is { } error)
            return await RefuseAsync(request, error, cancellationToken)
                .ConfigureAwait(false);

        var slot = validation.Slot!;
        return request.Operation switch
        {
            InvokeForegroundOperation =>
                await ExecuteForegroundAsync(
                    request,
                    slot,
                    cancellationToken).ConfigureAwait(false),
            InvokeBackgroundOperation =>
                await ExecuteBackgroundAsync(
                    request,
                    slot,
                    cancellationToken).ConfigureAwait(false),
            JobListOperation =>
                await ExecuteTextOperationAsync(
                    request,
                    slot,
                    WorkerSessionOperationCodec.JobListOperation,
                    new WorkerJobListArguments(),
                    static text => new JobListResult(text),
                    transferOutput: false,
                    cancellationToken).ConfigureAwait(false),
            JobStatusOperation operation =>
                await ExecuteJobOperationAsync(
                    request,
                    slot,
                    operation,
                    WorkerSessionOperationCodec.JobStatusOperation,
                    new WorkerJobStatusArguments(operation.PublicJobId.Value),
                    static text => new JobStatusResult(text),
                    transferOutput: false,
                    cancellationToken).ConfigureAwait(false),
            JobOutputOperation operation =>
                await ExecuteJobOperationAsync(
                    request,
                    slot,
                    operation,
                    WorkerSessionOperationCodec.JobOutputOperation,
                    new WorkerJobOutputArguments(
                        operation.PublicJobId.Value,
                        operation.Offset),
                    static text => new JobOutputResult(text),
                    transferOutput: true,
                    cancellationToken).ConfigureAwait(false),
            JobKillOperation operation =>
                await ExecuteJobOperationAsync(
                    request,
                    slot,
                    operation,
                    WorkerSessionOperationCodec.JobKillOperation,
                    new WorkerJobKillArguments(operation.PublicJobId.Value),
                    static text => new PtkSharedContracts.JobKillResult(text),
                    transferOutput: false,
                    cancellationToken).ConfigureAwait(false),
            ResetOperation =>
                await ReplaceWorkerAsync(
                    request,
                    slot,
                    GuardianHostSessionLifecycleReason.RequestedReset,
                    cancellationToken).ConfigureAwait(false),
            SessionRestartOperation =>
                await ReplaceWorkerAsync(
                    request,
                    slot,
                    GuardianHostSessionLifecycleReason.RequestedRestart,
                    cancellationToken).ConfigureAwait(false),
            SessionCloseOperation =>
                await CloseWorkerAsync(
                    request,
                    slot,
                    cancellationToken).ConfigureAwait(false),
            _ => await RefuseAsync(
                    request,
                    GuardianHostPrivateDetailCode.UnsupportedOperation,
                    cancellationToken)
                .ConfigureAwait(false),
        };
    }

    public async ValueTask ShutdownAsync(
        GuardianHostShutdown shutdown,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        PrivateHostWorkerSlot? slot;
        lock (_gate)
        {
            if (_state == WorkerPrivateHostRuntimeState.Stopped)
                return;
            if (_state is not (
                    WorkerPrivateHostRuntimeState.Ready or
                    WorkerPrivateHostRuntimeState.Faulted))
            {
                throw new InvalidOperationException(
                    "The worker private-host runtime is not ready to stop.");
            }
            _state = WorkerPrivateHostRuntimeState.Stopping;
            slot = _slot;
            _slot = null;
        }

        try
        {
            if (slot is not null)
            {
                await slot.Process.ShutdownAsync(cancellationToken)
                    .ConfigureAwait(false);
                _workerEvents.RetireWorker(slot.Identity);
                await slot.DisposeAsync().ConfigureAwait(false);
            }
            lock (_gate)
            {
                _jobCapabilities.Clear();
                _state = WorkerPrivateHostRuntimeState.Stopped;
            }
        }
        catch
        {
            if (slot is not null)
            {
                _workerEvents.RetireWorker(slot.Identity);
                await slot.DisposeAsync().ConfigureAwait(false);
            }
            lock (_gate) _state = WorkerPrivateHostRuntimeState.Faulted;
            throw;
        }
    }

    private async ValueTask<PrivateHostOperationOutcome>
        ExecuteForegroundAsync(
            OperationRequest request,
            PrivateHostWorkerSlot slot,
            CancellationToken cancellationToken)
    {
        var response = await _prepared.ExecuteForegroundAsync(
                request,
                slot,
                cancellationToken)
            .ConfigureAwait(false);
        var parsed = ParseTextResponse(
            response,
            WorkerSessionOperationCodec.InvokeOperation);
        if (parsed.Error is { } error)
            return PrivateHostOperationOutcome.Failed(error);

        await _output.TransferTextAsync(
                request,
                parsed.Text!,
                cancellationToken)
            .ConfigureAwait(false);
        return CompleteText(
            parsed.Text!,
            static text => new InvokeForegroundResult(text));
    }

    private async ValueTask<PrivateHostOperationOutcome>
        ExecuteBackgroundAsync(
            OperationRequest request,
            PrivateHostWorkerSlot slot,
            CancellationToken cancellationToken)
    {
        var operation = (InvokeBackgroundOperation)request.Operation;
        CapabilityToken capability;
        lock (_gate)
        {
            if (_jobCapabilities.Count >=
                ContractLimits.MaximumOutstandingPrivateRequests)
            {
                return PrivateHostOperationOutcome.Failed(
                    GuardianHostPrivateDetailCode.SessionBusy);
            }
            capability = _createJobCapability() ??
                throw new InvalidOperationException(
                    "Private host job capability source returned no capability.");
        }

        var start = await _prepared.ExecuteBackgroundAsync(
                request,
                slot,
                cancellationToken)
            .ConfigureAwait(false);
        if (!start.Started)
        {
            return PrivateHostOperationOutcome.Failed(
                GuardianHostPrivateDetailCode.OperationNotDispatched);
        }

        lock (_gate)
        {
            if (!_jobCapabilities.TryAdd(
                    operation.PublicJobId.Value,
                    capability))
            {
                throw new InvalidOperationException(
                    "Guardian-reserved background job ID was reused.");
            }
        }
        return PrivateHostOperationOutcome.Completed(
            new InvokeBackgroundResult(operation.PublicJobId, capability));
    }

    private async ValueTask<PrivateHostOperationOutcome>
        ExecuteJobOperationAsync(
            OperationRequest request,
            PrivateHostWorkerSlot slot,
            GuardianHostJobIdentityOperation operation,
            string workerOperation,
            WorkerSessionOperationArguments arguments,
            Func<string, GuardianHostOperationResult> createResult,
            bool transferOutput,
            CancellationToken cancellationToken)
    {
        var capabilityValid = false;
        lock (_gate)
        {
            capabilityValid = _jobCapabilities.TryGetValue(
                    operation.PublicJobId.Value,
                    out var capability) &&
                capability == operation.JobCapability;
        }
        if (!capabilityValid)
        {
            return await RefuseAsync(
                    request,
                    GuardianHostPrivateDetailCode.JobCapabilityInvalid,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        return await ExecuteTextOperationAsync(
                request,
                slot,
                workerOperation,
                arguments,
                createResult,
                transferOutput,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<PrivateHostOperationOutcome>
        ExecuteTextOperationAsync(
            OperationRequest request,
            PrivateHostWorkerSlot slot,
            string workerOperation,
            WorkerSessionOperationArguments arguments,
            Func<string, GuardianHostOperationResult> createResult,
            bool transferOutput,
            CancellationToken cancellationToken)
    {
        var response = await ExecuteOrdinaryAsync(
                request,
                slot,
                workerOperation,
                arguments,
                cancellationToken)
            .ConfigureAwait(false);
        var parsed = ParseTextResponse(response, workerOperation);
        if (parsed.Error is { } error)
            return PrivateHostOperationOutcome.Failed(error);
        if (transferOutput)
        {
            await _output.TransferTextAsync(
                    request,
                    parsed.Text!,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        return CompleteText(parsed.Text!, createResult);
    }

    private async Task<WorkerOperationResponse> ExecuteOrdinaryAsync(
        OperationRequest request,
        PrivateHostWorkerSlot slot,
        string operation,
        WorkerSessionOperationArguments arguments,
        CancellationToken cancellationToken)
    {
        var writeStarted = false;
        long? workerRequestId = null;
        try
        {
            var response = await slot.Process.ExecuteAsync(
                    operation,
                    arguments,
                    DateTimeOffset.FromUnixTimeMilliseconds(
                        request.DeadlineUnixTimeMilliseconds!.Value),
                    cancellationToken,
                    async (requestId, token) =>
                    {
                        await WriteDeliveryAsync(
                                request,
                                slot.Identity,
                                GuardianHostDeliveryState.WriteStarted,
                                new PrivateRequestId(requestId),
                                token)
                            .ConfigureAwait(false);
                        workerRequestId = requestId;
                        writeStarted = true;
                    })
                .ConfigureAwait(false);
            if (workerRequestId is null ||
                response.RequestId != workerRequestId.Value)
            {
                throw new InvalidDataException(
                    "Worker operation terminal does not match its request.");
            }
            await WriteDeliveryAsync(
                    request,
                    slot.Identity,
                    GuardianHostDeliveryState.TerminalDecoded,
                    new PrivateRequestId(workerRequestId.Value),
                    cancellationToken)
                .ConfigureAwait(false);
            return response;
        }
        catch
        {
            if (!writeStarted)
                await TryWriteNotDispatchedAsync(request, slot.Identity)
                    .ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<PrivateHostOperationOutcome> ReplaceWorkerAsync(
        OperationRequest request,
        PrivateHostWorkerSlot current,
        GuardianHostSessionLifecycleReason reason,
        CancellationToken cancellationToken)
    {
        var operation = (GuardianHostGenerationOperation)request.Operation;
        if (operation.ExpectedGeneration != 0 &&
            operation.ExpectedGeneration != current.Identity.Generation.Value)
        {
            return await RefuseAsync(
                    request,
                    GuardianHostPrivateDetailCode.ExpectedGenerationMismatch,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var (binding, highWatermark) = BeginReplacement(current);
        long? workerRequestId = null;
        PrivateHostWorkerSlot? replacement = null;
        try
        {
            await current.Process.ShutdownAsync(
                    cancellationToken,
                    async (requestId, token) =>
                    {
                        await WriteDeliveryAsync(
                                request,
                                current.Identity,
                                GuardianHostDeliveryState.WriteStarted,
                                new PrivateRequestId(requestId),
                                token)
                            .ConfigureAwait(false);
                        workerRequestId = requestId;
                    })
                .ConfigureAwait(false);
            _workerEvents.RetireWorker(current.Identity);
            await current.DisposeAsync().ConfigureAwait(false);
            lock (_gate) _jobCapabilities.Clear();

            replacement = await _slots.CreateAsync(
                    binding,
                    highWatermark,
                    _workerEvents.HandleAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteReadyLifecycleAsync(
                    request.RequestId,
                    binding,
                    replacement.Identity,
                    reason,
                    warmStateLost: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (workerRequestId is null)
            {
                throw new InvalidDataException(
                    "Worker replacement has no shutdown request identity.");
            }
            await WriteDeliveryAsync(
                    request,
                    current.Identity,
                    GuardianHostDeliveryState.TerminalDecoded,
                    new PrivateRequestId(workerRequestId.Value),
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _slot = replacement;
                _generationHighWatermark =
                    new WorkerGenerationHighWatermark(
                        replacement.Identity.Generation.Value);
                replacement = null;
                _state = WorkerPrivateHostRuntimeState.Ready;
            }
            var active = WorkerIdentity ??
                throw new InvalidOperationException(
                    "Worker replacement did not become active.");
            return PrivateHostOperationOutcome.Completed(
                request.Operation is ResetOperation
                    ? new ResetResult(
                        binding.Alias,
                        PublicSessionState.Ready,
                        active,
                        binding.TransitionVersion,
                        readyForEffects: true,
                        warmStateLost: true,
                        BootstrapState.Restored)
                    : new SessionRestartResult(
                        binding.Alias,
                        PublicSessionState.Ready,
                        active,
                        binding.TransitionVersion,
                        readyForEffects: true,
                        warmStateLost: true,
                        BootstrapState.Restored));
        }
        catch
        {
            if (replacement is not null)
            {
                _workerEvents.RetireWorker(replacement.Identity);
                await replacement.DisposeAsync().ConfigureAwait(false);
            }
            lock (_gate)
            {
                _slot = null;
                _state = WorkerPrivateHostRuntimeState.Faulted;
            }
            throw;
        }
    }

    private async ValueTask<PrivateHostOperationOutcome> CloseWorkerAsync(
        OperationRequest request,
        PrivateHostWorkerSlot current,
        CancellationToken cancellationToken)
    {
        var operation = (SessionCloseOperation)request.Operation;
        if (operation.ExpectedGeneration != 0 &&
            operation.ExpectedGeneration != current.Identity.Generation.Value)
        {
            return await RefuseAsync(
                    request,
                    GuardianHostPrivateDetailCode.ExpectedGenerationMismatch,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var (binding, _) = BeginReplacement(current);
        long? workerRequestId = null;
        try
        {
            await current.Process.ShutdownAsync(
                    cancellationToken,
                    async (requestId, token) =>
                    {
                        await WriteDeliveryAsync(
                                request,
                                current.Identity,
                                GuardianHostDeliveryState.WriteStarted,
                                new PrivateRequestId(requestId),
                                token)
                            .ConfigureAwait(false);
                        workerRequestId = requestId;
                    })
                .ConfigureAwait(false);
            _workerEvents.RetireWorker(current.Identity);
            await current.DisposeAsync().ConfigureAwait(false);
            lock (_gate) _jobCapabilities.Clear();
            await _events.WriteEventAsync(
                    sequence => new SessionLifecycleEvent(
                        _identity.GuardianBootId,
                        _identity.HostBootId,
                        _identity.HostGeneration,
                        sequence,
                        request.RequestId,
                        binding.Alias,
                        binding.TransitionVersion,
                        workerIdentity: null,
                        PublicSessionState.Closing,
                        PublicSessionState.Cold,
                        GuardianHostSessionLifecycleReason.RequestedClose,
                        readyForEffects: false,
                        warmStateLost: true,
                        BootstrapState.NotApplicable),
                    cancellationToken)
                .ConfigureAwait(false);
            if (workerRequestId is null)
            {
                throw new InvalidDataException(
                    "Worker close has no shutdown request identity.");
            }
            await WriteDeliveryAsync(
                    request,
                    current.Identity,
                    GuardianHostDeliveryState.TerminalDecoded,
                    new PrivateRequestId(workerRequestId.Value),
                    cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
            {
                _slot = null;
                _state = WorkerPrivateHostRuntimeState.Ready;
            }
            return PrivateHostOperationOutcome.Completed(
                new SessionCloseResult(
                    binding.Alias,
                    PublicSessionState.Cold,
                    workerIdentity: null,
                    binding.TransitionVersion,
                    readyForEffects: false,
                    warmStateLost: true,
                    BootstrapState.NotApplicable));
        }
        catch
        {
            lock (_gate)
            {
                _slot = null;
                _state = WorkerPrivateHostRuntimeState.Faulted;
            }
            throw;
        }
    }

    private (RecoveryBinding Binding, WorkerGenerationHighWatermark HighWatermark)
        BeginReplacement(PrivateHostWorkerSlot current)
    {
        lock (_gate)
        {
            if (_state != WorkerPrivateHostRuntimeState.Ready ||
                !ReferenceEquals(_slot, current) ||
                _binding is null ||
                _generationHighWatermark is null)
            {
                throw new InvalidOperationException(
                    "Worker replacement lost current slot ownership.");
            }
            _state = WorkerPrivateHostRuntimeState.Replacing;
            _slot = null;
            return (_binding, _generationHighWatermark);
        }
    }

    private RuntimeValidation ValidateAndBind(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _unixTimeMilliseconds();
        if (request.Operation.DispatchCapability.ExpiresUnixTimeMilliseconds <= now)
        {
            return RuntimeValidation.Failed(
                GuardianHostPrivateDetailCode.CapabilityInvalid);
        }
        if (request.Operation.OutputCapability is { } output &&
            output.ExpiresUnixTimeMilliseconds <= now)
        {
            return RuntimeValidation.Failed(
                GuardianHostPrivateDetailCode.OutputCapabilityInvalid);
        }

        lock (_gate)
        {
            if (_state != WorkerPrivateHostRuntimeState.Ready ||
                _binding is null)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.SessionFaulted);
            }
            if (request.SessionAlias != _binding.Alias)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.SessionNotFound);
            }
            if (request.SessionTransitionVersion != _binding.TransitionVersion)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.ExpectedGenerationMismatch);
            }
            if (_slot is null)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.WorkerLost);
            }
            if (request.Worker is null)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.WorkerLost);
            }
            if (request.Worker.Generation != _slot.Identity.Generation)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.WorkerGenerationMismatch);
            }
            if (request.Worker.BootId != _slot.Identity.BootId)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.WorkerBootMismatch);
            }
            if (request.Operation is GuardianHostGenerationOperation generation &&
                generation.ExpectedGeneration != 0 &&
                generation.ExpectedGeneration !=
                    _slot.Identity.Generation.Value)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.ExpectedGenerationMismatch);
            }
            return RuntimeValidation.Succeeded(_slot);
        }
    }

    private async ValueTask<PrivateHostOperationOutcome> RefuseAsync(
        OperationRequest request,
        GuardianHostPrivateDetailCode detailCode,
        CancellationToken cancellationToken)
    {
        if (request.Worker is { } worker)
            await TryWriteNotDispatchedAsync(request, worker, cancellationToken)
                .ConfigureAwait(false);
        return PrivateHostOperationOutcome.Failed(detailCode);
    }

    private async ValueTask TryWriteNotDispatchedAsync(
        OperationRequest request,
        GuardianHostWorkerIdentity worker,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await WriteDeliveryAsync(
                    request,
                    worker,
                    GuardianHostDeliveryState.NotDispatched,
                    workerRequestId: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private ValueTask WriteDeliveryAsync(
        OperationRequest request,
        GuardianHostWorkerIdentity worker,
        GuardianHostDeliveryState state,
        PrivateRequestId? workerRequestId,
        CancellationToken cancellationToken) =>
        _events.WriteEventAsync(
            sequence => new OperationDeliveryEvent(
                _identity.GuardianBootId,
                _identity.HostBootId,
                _identity.HostGeneration,
                sequence,
                request.RequestId,
                request.SessionAlias!,
                request.SessionTransitionVersion!,
                worker,
                request.OperationIdentity,
                request.Operation.DispatchCapability.Token,
                state,
                workerRequestId),
            cancellationToken);

    private ValueTask WriteReadyLifecycleAsync(
        PrivateRequestId? requestId,
        RecoveryBinding binding,
        GuardianHostWorkerIdentity worker,
        GuardianHostSessionLifecycleReason reason,
        bool warmStateLost,
        CancellationToken cancellationToken) =>
        _events.WriteEventAsync(
            sequence => new SessionLifecycleEvent(
                _identity.GuardianBootId,
                _identity.HostBootId,
                _identity.HostGeneration,
                sequence,
                requestId,
                binding.Alias,
                binding.TransitionVersion,
                worker,
                PublicSessionState.Starting,
                PublicSessionState.Ready,
                reason,
                readyForEffects: true,
                warmStateLost,
                BootstrapState.Restored),
            cancellationToken);

    private static ParsedTextResponse ParseTextResponse(
        WorkerOperationResponse response,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Status != WorkerOperationStatus.Completed)
        {
            return new ParsedTextResponse(
                Text: null,
                response.Status switch
                {
                    WorkerOperationStatus.Canceled =>
                        GuardianHostPrivateDetailCode.RequestCanceled,
                    WorkerOperationStatus.TimedOut =>
                        GuardianHostPrivateDetailCode.RequestDeadlineExpired,
                    _ => MapWorkerFailure(response.DetailCode),
                });
        }
        if (response.Result is not { } result)
        {
            return new ParsedTextResponse(
                Text: null,
                GuardianHostPrivateDetailCode.InvalidOperationResponse);
        }
        try
        {
            var parsed = WorkerSessionOperationCodec.ParseResult(
                operation,
                result);
            var text = parsed switch
            {
                WorkerInvokeResult value => value.Text,
                WorkerJobListResult value => value.Text,
                WorkerJobStatusResult value => value.Text,
                WorkerJobOutputResult value => value.Text,
                WorkerJobKillResult value => value.Text,
                WorkerStateResult value => value.Text,
                _ => null,
            };
            return text is null
                ? new ParsedTextResponse(
                    Text: null,
                    GuardianHostPrivateDetailCode.InvalidOperationResponse)
                : new ParsedTextResponse(text, Error: null);
        }
        catch (WorkerProtocolException)
        {
            return new ParsedTextResponse(
                Text: null,
                GuardianHostPrivateDetailCode.InvalidOperationResponse);
        }
    }

    private static GuardianHostPrivateDetailCode MapWorkerFailure(
        string? detailCode) => detailCode switch
    {
        "unsupported_operation" =>
            GuardianHostPrivateDetailCode.UnsupportedOperation,
        "operation_result_too_large" =>
            GuardianHostPrivateDetailCode.OperationResultTooLarge,
        "operation_script_too_large" =>
            GuardianHostPrivateDetailCode.OperationScriptTooLarge,
        _ => GuardianHostPrivateDetailCode.InvalidOperationResponse,
    };

    private static PrivateHostOperationOutcome CompleteText(
        string text,
        Func<string, GuardianHostOperationResult> createResult)
    {
        int encodedBytes;
        try
        {
            encodedBytes = StrictUtf8.GetByteCount(text);
        }
        catch (EncoderFallbackException)
        {
            return PrivateHostOperationOutcome.Failed(
                GuardianHostPrivateDetailCode.InvalidOperationResponse);
        }
        if (encodedBytes > ContractLimits.MaximumTextResultBytes)
        {
            return PrivateHostOperationOutcome.Failed(
                GuardianHostPrivateDetailCode.OperationResultTooLarge);
        }
        try
        {
            return PrivateHostOperationOutcome.Completed(createResult(text));
        }
        catch (ArgumentException)
        {
            return PrivateHostOperationOutcome.Failed(
                GuardianHostPrivateDetailCode.InvalidOperationResponse);
        }
    }

    private static (
        RecoveryBinding Binding,
        WorkerGenerationHighWatermark HighWatermark)
        ValidateInitialization(PrivateHostInitialization initialization)
    {
        var manifest = initialization.Manifest;
        if (manifest.Bindings.Count != 1 ||
            manifest.WorkerGenerationHighWatermarks.Count != 1)
        {
            throw new InvalidDataException(
                "The worker runtime currently requires one default binding.");
        }
        var binding = manifest.Bindings[0];
        var watermark = manifest.WorkerGenerationHighWatermarks[0];
        if (binding.Alias.Value != "default" ||
            binding.BindingKind != RecoveryBindingKind.Default ||
            binding.DesiredState != DesiredSessionState.Ready ||
            binding.TransitionVersion.Value <= 0 ||
            watermark.Alias != binding.Alias ||
            watermark.Generation.Value <= 0)
        {
            throw new InvalidDataException(
                "The worker runtime default binding is not ready and generation-bound.");
        }
        return (binding, watermark.Generation);
    }

    private static CapabilityToken CreateCapabilityToken()
    {
        Span<byte> bytes = stackalloc byte[ContractLimits.CapabilityTokenBytes];
        RandomNumberGenerator.Fill(bytes);
        try
        {
            return new CapabilityToken(Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_'));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed record ParsedTextResponse(
        string? Text,
        GuardianHostPrivateDetailCode? Error);

    private sealed record RuntimeValidation(
        PrivateHostWorkerSlot? Slot,
        GuardianHostPrivateDetailCode? Error)
    {
        internal static RuntimeValidation Succeeded(
            PrivateHostWorkerSlot slot) => new(slot, Error: null);

        internal static RuntimeValidation Failed(
            GuardianHostPrivateDetailCode error) => new(Slot: null, error);
    }
}
