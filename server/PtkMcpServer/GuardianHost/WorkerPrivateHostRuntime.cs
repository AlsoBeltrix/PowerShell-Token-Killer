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
    private static readonly TimeSpan DefaultStabilityWindow =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan JobOutputSealGrace =
        TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly PrivateHostServerIdentity _identity;
    private readonly IPrivateHostEventSink _events;
    private readonly PrivateHostWorkerSlotFactory _slots;
    private readonly PrivateHostPreparedInvokeDispatcher _prepared;
    private readonly PrivateHostWorkerEventBridge _workerEvents;
    private readonly IPrivateHostOutputTransfer _output;
    private readonly Func<CapabilityToken> _createJobCapability;
    private readonly Func<long> _unixTimeMilliseconds;
    private readonly TimeSpan _stabilityWindow;
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
        Func<long>? unixTimeMilliseconds = null,
        TimeSpan? stabilityWindow = null)
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
        _stabilityWindow = stabilityWindow ??
            DefaultStabilityWindow;
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
        BackgroundJobCapture? capture;
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
            state.JobCaptures.Remove(publicJobId, out capture);
        }
        if (capture is not null)
            SealBackgroundJobOutput(publicJobId, capture);
    }

    /// <summary>One `job_output` page from the worker, or null if the fetch
    /// failed or the response could not be parsed.</summary>
    private async Task<string?> FetchJobOutputPageAsync(
        BackgroundJobCapture capture,
        long publicJobId,
        long offset)
    {
        var response = await capture.Slot.Process.ExecuteAsync(
                WorkerSessionOperationCodec.JobOutputOperation,
                new WorkerJobOutputArguments(publicJobId, offset),
                DateTimeOffset.FromUnixTimeMilliseconds(
                    capture.DeadlineUnixTimeMilliseconds),
                CancellationToken.None)
            .ConfigureAwait(false);
        return ParseTextResponse(
            response,
            WorkerSessionOperationCodec.JobOutputOperation).Text;
    }

    /// <summary>
    /// Reads the trailing `next offset: N` a job-output page always carries
    /// (`SessionRuntime.JobCoreAsync`'s "output" case). A page without one is
    /// treated as unknown rather than complete, so an unparseable page seals
    /// incomplete instead of overclaiming.
    /// </summary>
    private static bool TryReadNextOffset(string page, out long nextOffset)
    {
        nextOffset = 0;
        const string Marker = "next offset: ";
        var start = page.LastIndexOf(Marker, StringComparison.Ordinal);
        if (start < 0) return false;
        var digits = page.AsSpan(start + Marker.Length);
        var end = 0;
        while (end < digits.Length && char.IsAsciiDigit(digits[end]))
            end++;
        return end > 0 && long.TryParse(digits[..end], out nextOffset);
    }

    /// <summary>
    /// Drops every job record this alias holds and disposes any retained output
    /// captures with them. A capture whose alias lost its worker can never be
    /// sealed - the bytes died with the worker - so the guardian terminalizes
    /// the capability as unavailable, which is the truthful outcome.
    /// </summary>
    private static void ClearAliasJobsLocked(AliasRuntime alias)
    {
        alias.OutstandingJobs.Clear();
        alias.CompletedJobs.Clear();
        foreach (var capture in alias.JobCaptures.Values)
            capture.Dispose();
        alias.JobCaptures.Clear();
    }

    /// <summary>
    /// Undoes one background reservation whose job never started. A job that
    /// did not start can never produce the terminal that seals its capture, so
    /// the capture is disposed here instead of being left for a terminal that
    /// is not coming.
    /// </summary>
    private void ReleaseUnstartedBackgroundJob(
        AliasRuntime alias,
        long publicJobId)
    {
        BackgroundJobCapture? capture;
        lock (_gate)
        {
            alias.OutstandingJobs.Remove(publicJobId);
            alias.JobCaptures.Remove(publicJobId, out capture);
        }
        capture?.Dispose();
    }

    /// <summary>
    /// Seals the guardian's background output capability once the job's
    /// terminal is observed. The worker holds the bytes and has no channel to
    /// the guardian's output events, so this host fetches the job's output over
    /// the worker request protocol and seals the capture it retained at start.
    /// Without this the capability is registered and never written, the
    /// guardian's <c>TryGetJobRecovery</c> stays empty, and every background job
    /// reports <c>recovery=unavailable</c> for the rest of its life.
    /// </summary>
    private void SealBackgroundJobOutput(
        long publicJobId,
        BackgroundJobCapture capture)
    {
        // The terminal callback runs under the worker event bridge's lock and
        // this runtime's, so the worker round-trip cannot happen inline. The
        // guardian owns the capability's own expiry, so a seal that loses its
        // race is terminalized there rather than needing a result here.
        _ = SealCoreAsync();

        async Task SealCoreAsync()
        {
            try
            {
                // A failed fetch leaves the capability unsealed on purpose. The
                // guardian then reports the artifact unavailable, which is true;
                // sealing an empty artifact would advertise a handle to content
                // this host never read.
                if (await FetchJobOutputPageAsync(capture, publicJobId, 0)
                        .ConfigureAwait(false) is not { } first)
                {
                    return;
                }

                // The worker bounds one read at 128 KiB, so a page is not
                // necessarily the whole spool. Sealing a bounded page as
                // Complete advertised a complete artifact that silently omitted
                // later output (r6x-2 #3, raised in review). Pages cannot simply
                // be concatenated - each is shaped and carries its own framing -
                // so completeness is established instead by probing at the
                // offset this page reports: if nothing follows, the page is the
                // whole output.
                var complete = false;
                if (TryReadNextOffset(first, out var nextOffset))
                {
                    var probe = await FetchJobOutputPageAsync(
                            capture,
                            publicJobId,
                            nextOffset)
                        .ConfigureAwait(false);
                    complete = probe is not null &&
                        TryReadNextOffset(probe, out var probeNext) &&
                        probeNext == nextOffset;
                }

                var content = new OutputArtifactContent(
                    first,
                    [],
                    [],
                    [],
                    ExitCode: null,
                    OutputProvenance.DirectText);
                _ = complete
                    ? await capture.Capture.SealAsync(
                            content,
                            JobOutputSealGrace)
                        .ConfigureAwait(false)
                    : await capture.Capture.SealIncompleteAsync(
                            content,
                            "job_output_truncated",
                            JobOutputSealGrace)
                        .ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // Same truthfulness rule as a failed fetch: an unsealed
                // capability is honest, an invented artifact is not.
            }
            finally
            {
                capture.Dispose();
            }
        }
    }

    private void WatchWorkerDeath(AliasRuntime alias, PrivateHostWorkerSlot slot)
    {
        _ = WatchWorkerDeathAsync(alias, slot);
    }

    private async Task WatchWorkerDeathAsync(
        AliasRuntime alias,
        PrivateHostWorkerSlot slot)
    {
        try
        {
            await slot.Process.Fatal.ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }

        (RecoveryBinding Binding, WorkerGenerationHighWatermark HighWatermark)?
            lease = null;
        lock (_gate)
        {
            if (_state == WorkerPrivateHostRuntimeState.Ready &&
                !alias.Faulted &&
                !alias.Replacing &&
                ReferenceEquals(alias.Slot, slot))
            {
                lease = BeginReplacement(alias, slot);
                alias.ReplacingAutomatically = true;
            }
        }
        if (lease is null) return;

        var (binding, highWatermark) = lease.Value;
        PrivateHostWorkerSlot? relaunch = null;
        try
        {
            _workerEvents.RetireWorker(slot.Identity);
            int consecutive;
            bool timedOut;
            lock (_gate)
            {
                ClearAliasJobsLocked(alias);
                consecutive = ++alias.ConsecutiveDeaths;
                timedOut = alias.ExecutionTimeoutContainment;
                alias.ExecutionTimeoutContainment = false;
            }
            if (consecutive < 3)
            {
                // Tell the guardian the alias has left Ready before containment
                // begins. Until this event exists the guardian keeps projecting
                // the dead worker's last ready lifecycle for the whole
                // death-to-relaunch window, so a caller sees a usable session
                // that cannot run anything, and an invalidated dispatch target
                // has no recovery evidence to refuse with.
                await TryWriteRecoveringLifecycleAsync(
                        binding,
                        slot.Identity,
                        timedOut
                            ? GuardianHostSessionLifecycleReason.ExecutionTimeout
                            : GuardianHostSessionLifecycleReason.WorkerExit,
                        RecoveryPhase.Containment,
                        consecutive)
                    .ConfigureAwait(false);
            }
            await slot.DisposeAsync().ConfigureAwait(false);
            if (consecutive >= 3)
            {
                MarkAliasFaulted(alias);
                await TryWriteFaultLifecycleAsync(
                        requestId: null,
                        binding,
                        slot.Identity,
                        PublicSessionState.Ready,
                        GuardianHostSessionLifecycleReason.CircuitTransition)
                    .ConfigureAwait(false);
                return;
            }

            relaunch = await _slots.CreateAsync(
                    binding,
                    highWatermark,
                    _workerEvents.HandleAsync,
                    CancellationToken.None)
                .ConfigureAwait(false);
            await WriteReadyLifecycleAsync(
                    requestId: null,
                    binding,
                    relaunch.Identity,
                    GuardianHostSessionLifecycleReason.AutomaticRecovery,
                    warmStateLost: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            lock (_gate)
            {
                if (_state != WorkerPrivateHostRuntimeState.Ready)
                {
                    throw new InvalidOperationException(
                        "Worker recovery lost runtime readiness.");
                }
                alias.GenerationHighWatermark = new WorkerGenerationHighWatermark(
                    relaunch.Identity.Generation.Value);
                alias.Slot = relaunch;
                alias.Replacing = false;
                alias.ReplacingAutomatically = false;
            }
            WatchWorkerDeath(alias, relaunch);
            _ = ResetDeathCounterAfterStabilityAsync(alias, relaunch);
        }
        catch
        {
            if (relaunch is not null)
            {
                _workerEvents.RetireWorker(relaunch.Identity);
                await relaunch.DisposeAsync().ConfigureAwait(false);
            }
            MarkAliasFaulted(alias);
            await TryWriteFaultLifecycleAsync(
                    requestId: null,
                    binding,
                    slot.Identity,
                    PublicSessionState.Ready,
                    GuardianHostSessionLifecycleReason.BootstrapFailed)
                .ConfigureAwait(false);
        }
    }

    private async Task ResetDeathCounterAfterStabilityAsync(
        AliasRuntime alias,
        PrivateHostWorkerSlot relaunch)
    {
        try
        {
            await Task.Delay(_stabilityWindow).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(alias.Slot, relaunch))
                    alias.ConsecutiveDeaths = 0;
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
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

            PrivateHostWorkerSlot[] live;
            lock (_gate)
            {
                if (_state != WorkerPrivateHostRuntimeState.Initializing)
                {
                    throw new InvalidOperationException(
                        "Worker private-host initialization state changed unexpectedly.");
                }
                created.Clear();
                _state = WorkerPrivateHostRuntimeState.Ready;
                live = _aliases.Values
                    .Where(alias => alias.Slot is not null)
                    .Select(alias => alias.Slot!)
                    .ToArray();
            }
            // Arm watches only once the runtime is Ready: a worker that died
            // during the launch sequence has its Fatal already completed, so
            // the fresh watch fires immediately and starts recovery instead
            // of leaving a dead slot watched by nobody.
            foreach (var slot in live)
            {
                var alias = _aliases[slot.Binding.Alias];
                if (ReferenceEquals(alias.Slot, slot))
                    WatchWorkerDeath(alias, slot);
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
                    ClearAliasJobsLocked(alias);
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
        var commitWriteStarted = false;
        WorkerOperationResponse response;
        try
        {
            response = await _prepared.ExecuteForegroundAsync(
                    request,
                    slot,
                    cancellationToken,
                    () => commitWriteStarted = true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            commitWriteStarted &&
            cancellationToken.IsCancellationRequested &&
            request.DeadlineUnixTimeMilliseconds <= _unixTimeMilliseconds())
        {
            // The host and worker share one absolute deadline. If the host's
            // observer wins their race after the prepared commit crossed its
            // write boundary, the call is still an execution timeout with live
            // or uncertain effects and must take the same containment path.
            ContainAfterExecutionTimeout(request, slot);
            throw;
        }
        if (response.Status == WorkerOperationStatus.TimedOut)
        {
            // Prepared foreground invokes are the public script path. They must
            // converge on the same post-terminal containment transition as an
            // ordinary worker operation; otherwise a timed-out script leaves
            // its warm worker live and reusable.
            ContainAfterExecutionTimeout(request, slot);
        }
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
        // The capability's own expiry, not the request deadline: the job
        // outlives the call that started it, and the guardian scopes a
        // background output capability to the artifact's retention for exactly
        // that reason (r6x-2 #3).
        var deadline = operation.OutputCapability!.ExpiresUnixTimeMilliseconds;
        IExecutionOutputCaptureOwner? captureOwner;
        try
        {
            captureOwner = _output.CreateExecutionCapture(request) ??
                throw new InvalidOperationException(
                    "Private host output transfer returned no capture owner.");
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return await RefuseAsync(
                    request,
                    GuardianHostPrivateDetailCode.OutputCapabilityInvalid,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        CapabilityToken capability;
        try
        {
            // Preparing against the request deadline is preparing against the
            // output capability's own expiry: the guardian mints both from the
            // same admitted deadline. An unavailable preparation is not a
            // dispatch failure - the job still runs and the guardian reports the
            // artifact unavailable, exactly as it did before any capture existed.
            var preparation = await captureOwner.PrepareAsync(
                    DateTimeOffset.FromUnixTimeMilliseconds(deadline),
                    JobOutputSealGrace,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!preparation.Available)
            {
                captureOwner.Dispose();
                captureOwner = null;
            }

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
                // never precede the capability's registration. The output
                // capture is registered in the same breath and for the same
                // reason: the terminal is what seals it, so it has to be
                // findable before the job can produce one.
                if (!alias.OutstandingJobs.TryAdd(
                        operation.PublicJobId.Value,
                        capability))
                {
                    throw new InvalidOperationException(
                        "Guardian-reserved background job ID was reused.");
                }
                if (captureOwner is not null)
                {
                    alias.JobCaptures[operation.PublicJobId.Value] =
                        new BackgroundJobCapture(captureOwner, slot, deadline);
                    captureOwner = null;
                }
            }
        }
        finally
        {
            captureOwner?.Dispose();
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
                ReleaseUnstartedBackgroundJob(alias, operation.PublicJobId.Value);
                return PrivateHostOperationOutcome.Failed(
                    GuardianHostPrivateDetailCode.OperationNotDispatched);
            }
        }
        catch
        {
            ReleaseUnstartedBackgroundJob(alias, operation.PublicJobId.Value);
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
            if (response.Status == WorkerOperationStatus.TimedOut)
            {
                // The single timeout terminal is already decoded and delivered
                // above; only now is the runaway worker contained. A worker that
                // blew its deadline is still running whatever overran, so its
                // warm state cannot be trusted or reused - the alias recovers to
                // its fresh declared baseline instead. The timed-out call is
                // never replayed: containment converges on the same loss path as
                // an unexpected death, which only ever launches a next
                // generation and reruns nothing.
                ContainAfterExecutionTimeout(request, slot);
            }
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

    /// <summary>
    /// Contains the worker whose operation exceeded its execution deadline.
    /// Recovery containment leaves the process monitor armed while it kills the
    /// tree and awaits the monitor that completes <c>Fatal</c>, so the alias's
    /// existing death watch observes a confirmed death and owns allocating the
    /// next generation. The caller's timeout terminal is deliberately not held
    /// behind containment - the plan requires death confirmed before the next
    /// generation, not before the terminal, and containment can take the full
    /// grace period. Containment is skipped when the slot is no longer the
    /// alias's current worker or the alias is already being replaced, so a
    /// timeout racing an unrelated replacement never tears down a successor.
    /// </summary>
    private void ContainAfterExecutionTimeout(
        OperationRequest request,
        PrivateHostWorkerSlot slot)
    {
        lock (_gate)
        {
            // Ownership is the whole check: this slot must still be the alias's
            // current worker. Replacement and fault both null the slot before
            // anything else, so an alias already being replaced or faulted fails
            // here without needing its own clause. Shutdown is excluded because
            // that path owns disposal itself.
            if (_state != WorkerPrivateHostRuntimeState.Ready ||
                !_aliases.TryGetValue(request.SessionAlias!, out var alias) ||
                !ReferenceEquals(alias.Slot, slot))
            {
                return;
            }
            alias.ExecutionTimeoutContainment = true;
        }
        _ = ContainCoreAsync();

        async Task ContainCoreAsync()
        {
            try
            {
                await slot.Process.ContainForRecoveryAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // The death watch still owns recovery; a containment failure is
                // reported through the watch's own fault path, not here.
            }
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
            WatchWorkerDeath(alias, alias.Slot!);
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
                alias.ConsecutiveDeaths = 0;
                active = slot.Identity;
                slot = null;
            }
            WatchWorkerDeath(alias, alias.Slot!);
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
            lock (_gate) ClearAliasJobsLocked(alias);

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
                alias.ConsecutiveDeaths = 0;
                active = replacement.Identity;
                replacement = null;
            }
            WatchWorkerDeath(alias, alias.Slot!);
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
                        alias.ConsecutiveDeaths = 0;
                        replacement = null;
                    }
                    alias.Replacing = false;
                }
                if (alias.Slot is not null)
                    WatchWorkerDeath(alias, alias.Slot);
                throw;
            }
            if (replacement is not null)
            {
                _workerEvents.RetireWorker(replacement.Identity);
                await replacement.DisposeAsync().ConfigureAwait(false);
            }
            MarkAliasFaulted(alias);
            await TryWriteFaultLifecycleAsync(
                    request.RequestId,
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
            lock (_gate) ClearAliasJobsLocked(alias);
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
                alias.ConsecutiveDeaths = 0;
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
                    request.RequestId,
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
            alias.ReplacingAutomatically = false;
            alias.Faulted = true;
            ClearAliasJobsLocked(alias);
        }
    }

    /// <summary>
    /// Announces that one alias is under automatic recovery. Best-effort for the
    /// same reason the fault lifecycle is: a failed write must not abort the
    /// recovery it is only describing. The attempt ordinal is the alias's own
    /// consecutive-death count, which is this runtime's real recovery counter -
    /// no value is invented to satisfy the contract's completeness rule.
    /// </summary>
    private async ValueTask TryWriteRecoveringLifecycleAsync(
        RecoveryBinding binding,
        GuardianHostWorkerIdentity worker,
        GuardianHostSessionLifecycleReason reason,
        RecoveryPhase phase,
        long attempt)
    {
        try
        {
            await _events.WriteEventAsync(
                    sequence => new SessionLifecycleEvent(
                        _identity.GuardianBootId,
                        _identity.HostBootId,
                        _identity.HostGeneration,
                        sequence,
                        requestId: null,
                        binding.Alias,
                        binding.TransitionVersion,
                        worker,
                        PublicSessionState.Ready,
                        PublicSessionState.Recovering,
                        reason,
                        readyForEffects: false,
                        warmStateLost: true,
                        BootstrapState.Pending,
                        phase,
                        attempt,
                        ContractLimits.MinimumRetryAfterMilliseconds),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private async ValueTask TryWriteFaultLifecycleAsync(
        PrivateRequestId? requestId,
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
                        requestId,
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
            if (alias.ReplacingAutomatically)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.WorkerLost);
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
        internal bool ReplacingAutomatically { get; set; }
        internal bool Faulted { get; set; }
        internal int ConsecutiveDeaths { get; set; }

        /// <summary>
        /// Set when this alias's worker is being contained because one of its
        /// operations exceeded its execution deadline, rather than because the
        /// worker died on its own. The death watch consumes it to report the
        /// honest lifecycle reason; both causes otherwise converge on exactly
        /// the same loss path.
        /// </summary>
        internal bool ExecutionTimeoutContainment { get; set; }
        internal Dictionary<long, CapabilityToken> OutstandingJobs { get; } = [];
        internal Dictionary<long, CapabilityToken> CompletedJobs { get; } = [];

        /// <summary>
        /// Guardian output captures retained for this alias's outstanding
        /// background jobs, keyed by public job ID. Held from the reservation
        /// until the job's terminal seals it, because the job's bytes live in
        /// the worker and only the terminal says when they are final.
        /// </summary>
        internal Dictionary<long, BackgroundJobCapture> JobCaptures { get; } = [];
    }

    /// <summary>
    /// One background job's retained guardian output capture, together with the
    /// worker slot that can serve its output and the deadline the guardian
    /// minted the capability against.
    /// </summary>
    private sealed class BackgroundJobCapture(
        IExecutionOutputCapture capture,
        PrivateHostWorkerSlot slot,
        long deadlineUnixTimeMilliseconds) : IDisposable
    {
        internal IExecutionOutputCapture Capture { get; } = capture;
        internal PrivateHostWorkerSlot Slot { get; } = slot;
        internal long DeadlineUnixTimeMilliseconds { get; } =
            deadlineUnixTimeMilliseconds;

        public void Dispose() => Capture.Dispose();
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
