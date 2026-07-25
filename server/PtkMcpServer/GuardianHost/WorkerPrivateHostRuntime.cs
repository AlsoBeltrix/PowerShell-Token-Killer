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
/// Production private-host runtime for the declared session bindings. Every
/// live operation is routed to the exact alias's contained worker slot;
/// script-bearing work uses the prepared dispatcher and ordinary job work uses
/// the worker request protocol. Reset and restart replace the whole worker
/// generation for that alias without touching any other alias's slot.
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
    private readonly Dictionary<CanonicalAlias, AliasRuntime> _aliases = [];

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
        _workerEvents.JobTerminalObserved = ReleaseJobTerminal;
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
            lock (_gate)
            {
                return _aliases.TryGetValue(new CanonicalAlias("default"), out var state)
                    ? state.Slot?.Identity
                    : null;
            }
        }
    }

    internal int OutstandingJobCapabilityCount
    {
        get
        {
            lock (_gate)
            {
                return _aliases.Values.Sum(
                    alias => alias.OutstandingJobs.Count);
            }
        }
    }

    private void ReleaseJobTerminal(CanonicalAlias alias, long publicJobId)
    {
        lock (_gate)
        {
            if (!_aliases.TryGetValue(alias, out var state) ||
                !state.OutstandingJobs.Remove(publicJobId, out var capability))
            {
                return;
            }
            state.CompletedJobs[publicJobId] = capability;
            if (state.CompletedJobs.Count >
                ContractLimits.MaximumOutstandingPrivateRequests)
            {
                state.CompletedJobs.Remove(state.CompletedJobs.Keys.Min());
            }
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

        var created = new List<PrivateHostWorkerSlot>();
        try
        {
            var declarations = ValidateInitialization(initialization);
            lock (_gate)
            {
                foreach (var declaration in declarations)
                {
                    _aliases.Add(
                        declaration.Binding.Alias,
                        new AliasRuntime(
                            declaration.Binding,
                            declaration.HighWatermark));
                }
            }
            foreach (var declaration in declarations)
            {
                if (!declaration.CreateSlot) continue;
                var slot = await _slots.CreateAsync(
                        declaration.Binding,
                        declaration.HighWatermark,
                        _workerEvents.HandleAsync,
                        cancellationToken)
                    .ConfigureAwait(false);
                created.Add(slot);
                await WriteReadyLifecycleAsync(
                        requestId: null,
                        declaration.Binding,
                        slot.Identity,
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
                    var alias = _aliases[declaration.Binding.Alias];
                    alias.Slot = slot;
                    alias.GenerationHighWatermark = new WorkerGenerationHighWatermark(
                        slot.Identity.Generation.Value);
                }
            }

            lock (_gate)
            {
                if (_state != WorkerPrivateHostRuntimeState.Initializing)
                {
                    throw new InvalidOperationException(
                        "Worker private-host initialization state changed unexpectedly.");
                }
                created.Clear();
                _state = WorkerPrivateHostRuntimeState.Ready;
            }
        }
        catch
        {
            foreach (var slot in created)
            {
                _workerEvents.RetireWorker(slot.Identity);
                await slot.DisposeAsync().ConfigureAwait(false);
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
        if (request.Operation is SessionOpenOperation openOperation)
            return await OpenWorkerAsync(
                request,
                openOperation,
                cancellationToken).ConfigureAwait(false);

        var validation = ValidateAndBind(request, cancellationToken);
        if (validation.Error is { } error)
            return await RefuseAsync(request, error, cancellationToken)
                .ConfigureAwait(false);

        var alias = validation.Alias!;
        var slot = alias.Slot!;
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
                    alias,
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
                    alias,
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
                    alias,
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
                    alias,
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
                    alias,
                    slot,
                    GuardianHostSessionLifecycleReason.RequestedReset,
                    cancellationToken).ConfigureAwait(false),
            SessionRestartOperation =>
                await ReplaceWorkerAsync(
                    request,
                    alias,
                    slot,
                    GuardianHostSessionLifecycleReason.RequestedRestart,
                    cancellationToken).ConfigureAwait(false),
            SessionCloseOperation =>
                await CloseWorkerAsync(
                    request,
                    alias,
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
        PrivateHostWorkerSlot[] slots;
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
            slots = _aliases.Values
                .OrderBy(alias => alias.Binding.Alias.Value, StringComparer.Ordinal)
                .Select(alias => alias.Slot)
                .Where(slot => slot is not null)
                .Cast<PrivateHostWorkerSlot>()
                .ToArray();
            foreach (var alias in _aliases.Values)
            {
                alias.Slot = null;
            }
        }

        try
        {
            foreach (var slot in slots)
            {
                await slot.Process.ShutdownAsync(cancellationToken)
                    .ConfigureAwait(false);
                _workerEvents.RetireWorker(slot.Identity);
                await slot.DisposeAsync().ConfigureAwait(false);
            }
            lock (_gate)
            {
                foreach (var alias in _aliases.Values)
                {
                    alias.OutstandingJobs.Clear();
            alias.CompletedJobs.Clear();
                }
                _state = WorkerPrivateHostRuntimeState.Stopped;
            }
        }
        catch
        {
            foreach (var slot in slots)
            {
                if (slot is null) continue;
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
            AliasRuntime alias,
            PrivateHostWorkerSlot slot,
            CancellationToken cancellationToken)
    {
        var operation = (InvokeBackgroundOperation)request.Operation;
        CapabilityToken capability;
        lock (_gate)
        {
            if (_aliases.Values.Sum(value => value.OutstandingJobs.Count) >=
                ContractLimits.MaximumOutstandingPrivateRequests)
            {
                return PrivateHostOperationOutcome.Failed(
                    GuardianHostPrivateDetailCode.SessionBusy);
            }
            capability = _createJobCapability() ??
                throw new InvalidOperationException(
                    "Private host job capability source returned no capability.");
            // Reserve before the commit write so a fast job's terminal can
            // never precede the capability's registration.
            if (!alias.OutstandingJobs.TryAdd(
                    operation.PublicJobId.Value,
                    capability))
            {
                throw new InvalidOperationException(
                    "Guardian-reserved background job ID was reused.");
            }
        }

        try
        {
            var start = await _prepared.ExecuteBackgroundAsync(
                    request,
                    slot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!start.Started)
            {
                lock (_gate)
                {
                    alias.OutstandingJobs.Remove(operation.PublicJobId.Value);
                }
                return PrivateHostOperationOutcome.Failed(
                    GuardianHostPrivateDetailCode.OperationNotDispatched);
            }
        }
        catch
        {
            lock (_gate)
            {
                alias.OutstandingJobs.Remove(operation.PublicJobId.Value);
            }
            throw;
        }
        return PrivateHostOperationOutcome.Completed(
            new InvokeBackgroundResult(operation.PublicJobId, capability));
    }

    private async ValueTask<PrivateHostOperationOutcome>
        ExecuteJobOperationAsync(
            OperationRequest request,
            AliasRuntime alias,
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
            capabilityValid =
                (alias.OutstandingJobs.TryGetValue(
                    operation.PublicJobId.Value,
                    out var capability) ||
                 alias.CompletedJobs.TryGetValue(
                    operation.PublicJobId.Value,
                    out capability)) &&
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

    private async ValueTask<PrivateHostOperationOutcome> OpenWorkerAsync(
        OperationRequest request,
        SessionOpenOperation operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _unixTimeMilliseconds();
        if (operation.DispatchCapability.ExpiresUnixTimeMilliseconds <= now)
        {
            return PrivateHostOperationOutcome.Failed(
                GuardianHostPrivateDetailCode.CapabilityInvalid);
        }
        if (operation.OutputCapability is { } output &&
            output.ExpiresUnixTimeMilliseconds <= now)
        {
            return PrivateHostOperationOutcome.Failed(
                GuardianHostPrivateDetailCode.OutputCapabilityInvalid);
        }
        if (operation.Template is not null)
        {
            return await RefuseAsync(
                    request,
                    GuardianHostPrivateDetailCode.UnsupportedOperation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        AliasRuntime? existing = null;
        lock (_gate)
        {
            if (_state != WorkerPrivateHostRuntimeState.Ready)
            {
                return PrivateHostOperationOutcome.Failed(
                    GuardianHostPrivateDetailCode.SessionFaulted);
            }
            if (_aliases.TryGetValue(request.SessionAlias, out existing))
            {
                if (existing.Faulted)
                {
                    return PrivateHostOperationOutcome.Failed(
                        GuardianHostPrivateDetailCode.SessionFaulted);
                }
                if (existing.Slot is not null)
                {
                    return PrivateHostOperationOutcome.Failed(
                        GuardianHostPrivateDetailCode.SessionBusy);
                }
            }
        }

        if (existing is not null)
        {
            return await ReopenWorkerAsync(
                    request,
                    existing,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var binding = new RecoveryBinding(
            request.SessionAlias,
            RecoveryBindingKind.Dynamic,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            operation.AllowColdBackground,
            DesiredSessionState.Ready,
            request.SessionTransitionVersion!,
            RecoveryBinding.ComputeBindingDigest(
                request.SessionAlias,
                RecoveryBindingKind.Dynamic,
                operation.AllowColdBackground,
                DesiredSessionState.Ready,
                request.SessionTransitionVersion!));
        var alias = new AliasRuntime(
            binding,
            new WorkerGenerationHighWatermark(1));
        PrivateHostWorkerSlot? slot = null;
        try
        {
            slot = await _slots.CreateAsync(
                    binding,
                    alias.GenerationHighWatermark,
                    _workerEvents.HandleAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteReadyLifecycleAsync(
                    request.RequestId,
                    binding,
                    slot.Identity,
                    GuardianHostSessionLifecycleReason.RequestedOpen,
                    warmStateLost: false,
                    cancellationToken)
                .ConfigureAwait(false);
            GuardianHostWorkerIdentity active;
            lock (_gate)
            {
                if (_state != WorkerPrivateHostRuntimeState.Ready ||
                    !_aliases.TryAdd(request.SessionAlias, alias))
                {
                    throw new InvalidOperationException(
                        "Worker open lost new-alias ownership.");
                }
                alias.GenerationHighWatermark = new WorkerGenerationHighWatermark(
                    slot.Identity.Generation.Value);
                alias.Slot = slot;
                active = slot.Identity;
                slot = null;
            }
            return PrivateHostOperationOutcome.Completed(
                new SessionOpenResult(
                    binding.Alias,
                    PublicSessionState.Ready,
                    active,
                    binding.TransitionVersion,
                    readyForEffects: true,
                    warmStateLost: false,
                    BootstrapState.Restored));
        }
        catch
        {
            if (slot is not null)
            {
                _workerEvents.RetireWorker(slot.Identity);
                await slot.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private async ValueTask<PrivateHostOperationOutcome> ReopenWorkerAsync(
        OperationRequest request,
        AliasRuntime alias,
        CancellationToken cancellationToken)
    {
        var binding = alias.Binding;
        PrivateHostWorkerSlot? slot = null;
        try
        {
            slot = await _slots.CreateAsync(
                    binding,
                    alias.GenerationHighWatermark,
                    _workerEvents.HandleAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteReadyLifecycleAsync(
                    request.RequestId,
                    binding,
                    slot.Identity,
                    GuardianHostSessionLifecycleReason.RequestedOpen,
                    warmStateLost: false,
                    cancellationToken)
                .ConfigureAwait(false);
            GuardianHostWorkerIdentity active;
            lock (_gate)
            {
                if (_state != WorkerPrivateHostRuntimeState.Ready ||
                    alias.Faulted ||
                    alias.Slot is not null)
                {
                    throw new InvalidOperationException(
                        "Worker reopen lost declared-alias ownership.");
                }
                alias.GenerationHighWatermark = new WorkerGenerationHighWatermark(
                    slot.Identity.Generation.Value);
                alias.Slot = slot;
                active = slot.Identity;
                slot = null;
            }
            return PrivateHostOperationOutcome.Completed(
                new SessionOpenResult(
                    binding.Alias,
                    PublicSessionState.Ready,
                    active,
                    binding.TransitionVersion,
                    readyForEffects: true,
                    warmStateLost: false,
                    BootstrapState.Restored));
        }
        catch
        {
            if (slot is not null)
            {
                _workerEvents.RetireWorker(slot.Identity);
                await slot.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private async ValueTask<PrivateHostOperationOutcome> ReplaceWorkerAsync(
        OperationRequest request,
        AliasRuntime alias,
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

        var (binding, highWatermark) = BeginReplacement(alias, current);
        long? workerRequestId = null;
        PrivateHostWorkerSlot? replacement = null;
        var relaunchStarted = false;
        var readyAnnounced = false;
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
            lock (_gate) { alias.OutstandingJobs.Clear(); alias.CompletedJobs.Clear(); }

            relaunchStarted = true;
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
            readyAnnounced = true;
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

            GuardianHostWorkerIdentity active;
            lock (_gate)
            {
                alias.Slot = replacement;
                alias.GenerationHighWatermark =
                    new WorkerGenerationHighWatermark(
                        replacement.Identity.Generation.Value);
                alias.Replacing = false;
                active = replacement.Identity;
                replacement = null;
            }
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
            if (readyAnnounced)
            {
                // The guardian already bound the announced replacement:
                // commit it. The operation is lost; the session is not.
                lock (_gate)
                {
                    if (replacement is not null)
                    {
                        alias.Slot = replacement;
                        alias.GenerationHighWatermark =
                            new WorkerGenerationHighWatermark(
                                replacement.Identity.Generation.Value);
                        replacement = null;
                    }
                    alias.Replacing = false;
                }
                throw;
            }
            if (replacement is not null)
            {
                _workerEvents.RetireWorker(replacement.Identity);
                await replacement.DisposeAsync().ConfigureAwait(false);
            }
            MarkAliasFaulted(alias);
            await TryWriteFaultLifecycleAsync(
                    request,
                    binding,
                    current.Identity,
                    PublicSessionState.Resetting,
                    relaunchStarted
                        ? GuardianHostSessionLifecycleReason.BootstrapFailed
                        : GuardianHostSessionLifecycleReason.ContainmentUnconfirmed)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<PrivateHostOperationOutcome> CloseWorkerAsync(
        OperationRequest request,
        AliasRuntime alias,
        PrivateHostWorkerSlot current,
        CancellationToken cancellationToken)
    {
        var operation = (SessionCloseOperation)request.Operation;
        if (alias.Binding.BindingKind == RecoveryBindingKind.Default)
        {
            return await RefuseAsync(
                    request,
                    GuardianHostPrivateDetailCode.UnsupportedOperation,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        if (operation.ExpectedGeneration != 0 &&
            operation.ExpectedGeneration != current.Identity.Generation.Value)
        {
            return await RefuseAsync(
                    request,
                    GuardianHostPrivateDetailCode.ExpectedGenerationMismatch,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var (binding, _) = BeginReplacement(alias, current);
        long? workerRequestId = null;
        var coldAnnounced = false;
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
            lock (_gate) { alias.OutstandingJobs.Clear(); alias.CompletedJobs.Clear(); }
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
            coldAnnounced = true;
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
                alias.Slot = null;
                alias.Replacing = false;
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
            if (coldAnnounced)
            {
                // The guardian already recorded the close: the operation's
                // terminal is lost, but the session is honestly cold, not
                // faulted.
                lock (_gate)
                {
                    alias.Slot = null;
                    alias.Replacing = false;
                }
                throw;
            }
            MarkAliasFaulted(alias);
            await TryWriteFaultLifecycleAsync(
                    request,
                    binding,
                    current.Identity,
                    PublicSessionState.Closing,
                    GuardianHostSessionLifecycleReason.ContainmentUnconfirmed)
                .ConfigureAwait(false);
            throw;
        }
    }

    private void MarkAliasFaulted(AliasRuntime alias)
    {
        lock (_gate)
        {
            alias.Slot = null;
            alias.Replacing = false;
            alias.Faulted = true;
            alias.OutstandingJobs.Clear();
            alias.CompletedJobs.Clear();
        }
    }

    private async ValueTask TryWriteFaultLifecycleAsync(
        OperationRequest request,
        RecoveryBinding binding,
        GuardianHostWorkerIdentity worker,
        PublicSessionState previousState,
        GuardianHostSessionLifecycleReason reason)
    {
        try
        {
            await _events.WriteEventAsync(
                    sequence => new SessionLifecycleEvent(
                        _identity.GuardianBootId,
                        _identity.HostBootId,
                        _identity.HostGeneration,
                        sequence,
                        request.RequestId,
                        binding.Alias,
                        binding.TransitionVersion,
                        worker,
                        previousState,
                        PublicSessionState.Faulted,
                        reason,
                        readyForEffects: false,
                        warmStateLost: true,
                        BootstrapState.Failed),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private (RecoveryBinding Binding, WorkerGenerationHighWatermark HighWatermark)
        BeginReplacement(AliasRuntime alias, PrivateHostWorkerSlot current)
    {
        lock (_gate)
        {
            if (_state != WorkerPrivateHostRuntimeState.Ready ||
                alias.Replacing ||
                !ReferenceEquals(alias.Slot, current))
            {
                throw new InvalidOperationException(
                    "Worker replacement lost current slot ownership.");
            }
            alias.Replacing = true;
            alias.Slot = null;
            return (alias.Binding, alias.GenerationHighWatermark);
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
            if (_state != WorkerPrivateHostRuntimeState.Ready)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.SessionFaulted);
            }
            if (!_aliases.TryGetValue(request.SessionAlias, out var alias))
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.SessionNotFound);
            }
            if (request.SessionTransitionVersion != alias.Binding.TransitionVersion)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.ExpectedGenerationMismatch);
            }
            if (alias.Replacing)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.SessionFaulted);
            }
            if (alias.Faulted)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.SessionFaulted);
            }
            if (alias.Slot is null)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.WorkerLost);
            }
            if (request.Worker is null)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.WorkerLost);
            }
            if (request.Worker.Generation != alias.Slot.Identity.Generation)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.WorkerGenerationMismatch);
            }
            if (request.Worker.BootId != alias.Slot.Identity.BootId)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.WorkerBootMismatch);
            }
            if (request.Operation is GuardianHostGenerationOperation generation &&
                generation.ExpectedGeneration != 0 &&
                generation.ExpectedGeneration !=
                    alias.Slot.Identity.Generation.Value)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.ExpectedGenerationMismatch);
            }
            return RuntimeValidation.Succeeded(alias);
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

    private static AliasDeclaration[] ValidateInitialization(
        PrivateHostInitialization initialization)
    {
        var manifest = initialization.Manifest;
        var watermarks = manifest.WorkerGenerationHighWatermarks
            .ToDictionary(entry => entry.Alias.Value, StringComparer.Ordinal);
        var declarations = new List<AliasDeclaration>(manifest.Bindings.Count);
        var defaultSeen = false;
        foreach (var binding in manifest.Bindings)
        {
            if (!watermarks.TryGetValue(binding.Alias.Value, out var watermark) ||
                watermark.Generation.Value <= 0 ||
                binding.TransitionVersion.Value <= 0)
            {
                throw new InvalidDataException(
                    "The worker runtime binding is not generation-bound.");
            }
            switch (binding.BindingKind)
            {
                case RecoveryBindingKind.Default:
                    if (defaultSeen ||
                        binding.Alias.Value != "default" ||
                        binding.DesiredState != DesiredSessionState.Ready)
                    {
                        throw new InvalidDataException(
                            "The worker runtime requires one ready default binding.");
                    }
                    defaultSeen = true;
                    declarations.Add(new AliasDeclaration(
                        binding,
                        watermark.Generation,
                        CreateSlot: true));
                    break;
                case RecoveryBindingKind.Dynamic:
                    if (binding.Alias.Value == "default")
                    {
                        throw new InvalidDataException(
                            "A dynamic worker runtime binding cannot use the default alias.");
                    }
                    declarations.Add(new AliasDeclaration(
                        binding,
                        watermark.Generation,
                        CreateSlot: binding.DesiredState ==
                            DesiredSessionState.Ready));
                    break;
                default:
                    throw new InvalidDataException(
                        "The worker runtime does not yet accept template bindings.");
            }
        }
        if (!defaultSeen)
        {
            throw new InvalidDataException(
                "The worker runtime requires one ready default binding.");
        }
        return declarations.ToArray();
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

    private sealed record AliasDeclaration(
        RecoveryBinding Binding,
        WorkerGenerationHighWatermark HighWatermark,
        bool CreateSlot);

    private sealed class AliasRuntime(
        RecoveryBinding binding,
        WorkerGenerationHighWatermark generationHighWatermark)
    {
        internal RecoveryBinding Binding { get; } = binding;
        internal WorkerGenerationHighWatermark GenerationHighWatermark { get; set; } =
            generationHighWatermark;
        internal PrivateHostWorkerSlot? Slot { get; set; }
        internal bool Replacing { get; set; }
        internal bool Faulted { get; set; }
        internal Dictionary<long, CapabilityToken> OutstandingJobs { get; } = [];
        internal Dictionary<long, CapabilityToken> CompletedJobs { get; } = [];
    }

    private sealed record RuntimeValidation(
        AliasRuntime? Alias,
        GuardianHostPrivateDetailCode? Error)
    {
        internal static RuntimeValidation Succeeded(
            AliasRuntime alias) => new(alias, Error: null);

        internal static RuntimeValidation Failed(
            GuardianHostPrivateDetailCode error) => new(Alias: null, error);
    }
}
