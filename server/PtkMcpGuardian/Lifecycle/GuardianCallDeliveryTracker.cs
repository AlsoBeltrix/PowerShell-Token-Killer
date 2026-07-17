using PtkSharedContracts;

namespace PtkMcpGuardian.Lifecycle;

/// <summary>
/// Guardian-local public delivery truth. This deliberately includes the final
/// public write boundary, which is not part of the frozen private wire enum.
/// </summary>
internal enum GuardianPublicDeliveryState
{
    NotDispatched,
    WriteStarted,
    TerminalDecoded,
    PublicTerminalSent,
}

internal sealed record GuardianPrivateRequestIdentity
{
    /// <summary>
    /// The exact correlation scope owned by this delivery tracker: one host
    /// boot, host generation, and guardian-originated private request ID.
    /// Guardian boot, session transition, worker, plan, operation, capability,
    /// and payload correlation are validated by the private client before it
    /// calls this tracker.
    /// </summary>
    internal GuardianPrivateRequestIdentity(
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        PrivateRequestId requestId)
    {
        ArgumentNullException.ThrowIfNull(hostBootId);
        ArgumentNullException.ThrowIfNull(hostGeneration);
        ArgumentNullException.ThrowIfNull(requestId);
        HostBootId = hostBootId;
        HostGeneration = hostGeneration;
        RequestId = requestId;
    }

    internal HostBootId HostBootId { get; }
    internal HostGeneration HostGeneration { get; }
    internal PrivateRequestId RequestId { get; }

    internal bool MatchesHost(HostBootId hostBootId, HostGeneration hostGeneration) =>
        HostBootId == hostBootId && HostGeneration == hostGeneration;
}

internal enum GuardianHostLossDisposition
{
    BackendLostBeforeDispatch,
    OutcomeUnknown,
    RetainedAuthoritativeTerminal,
    PublicTerminalAlreadySent,
    StaleHostIdentity,
}

internal readonly record struct GuardianHostLossClassification(
    GuardianHostLossDisposition Disposition,
    bool IsFirstObservation);

internal enum GuardianTerminalCorrelationResult
{
    Accepted,
    DuplicateTerminalFatal,
    UnknownRequestFatal,
    MismatchedHostFatal,
    UnexpectedDeliveryStateFatal,
    GenerationLostFatal,
    CorrelationAlreadyFatal,
    Stopped,
}

internal enum GuardianLocalTerminalResult
{
    Accepted,
    WriteAlreadyStarted,
    TerminalAlreadyDecoded,
    PublicTerminalAlreadySent,
    Stopped,
}

internal enum GuardianCancellationClaimResult
{
    BeforeDispatchTerminalInstalled,
    SignalOriginalRequest,
    AlreadySignaled,
    TerminalAlreadyKnown,
    IdentityMismatch,
    GenerationLost,
    CorrelationFatal,
    Stopped,
}

internal enum GuardianPublicTerminalDeliveryResult
{
    Sent,
    NotAvailable,
    AlreadyClaimed,
    AlreadySent,
}

internal sealed record GuardianCallDeliverySnapshot(
    GuardianPublicDeliveryState State,
    GuardianPrivateRequestIdentity Identity,
    GuardianHostLossDisposition? LossDisposition,
    bool CancellationSignaled,
    bool PublicDeliveryClaimed,
    bool CorrelationFatal,
    bool Stopped,
    bool HasRetainedTerminal);

/// <summary>
/// Owns delivery and public-terminal truth for exactly one private request.
/// It never launches, retries, queues, or rebinds work to another host.
/// </summary>
internal sealed class GuardianCallDeliveryTracker<TTerminal>
    where TTerminal : notnull
{
    private readonly object _sync = new();
    private readonly GuardianPrivateRequestIdentity _identity;

    private GuardianPublicDeliveryState _state;
    private GuardianHostLossDisposition? _lossDisposition;
    private TTerminal? _terminal;
    private bool _hasTerminal;
    private bool _cancellationSignaled;
    private bool _publicDeliveryClaimed;
    private bool _correlationFatal;
    private bool _stopped;

    internal GuardianCallDeliveryTracker(GuardianPrivateRequestIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
    }

    internal GuardianPrivateRequestIdentity Identity => _identity;

    /// <summary>
    /// Advances delivery truth before invoking the first callback that may
    /// write a private byte. A synchronous or asynchronous callback failure
    /// therefore remains in WriteStarted and is never classified as safe.
    /// </summary>
    internal ValueTask BeginFirstWriteAsync(
        Func<GuardianPrivateRequestIdentity, CancellationToken, ValueTask> firstPossiblyWriting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(firstPossiblyWriting);

        ValueTask write;
        lock (_sync)
        {
            if (_stopped)
                throw new InvalidOperationException("Delivery tracking is stopped.");
            if (_correlationFatal)
                throw new InvalidOperationException("Private correlation is already fatal.");
            if (_lossDisposition is not null)
                throw new InvalidOperationException("The bound host generation is already lost.");
            if (_state != GuardianPublicDeliveryState.NotDispatched)
                throw new InvalidOperationException("The first private write was already started.");

            _state = GuardianPublicDeliveryState.WriteStarted;
            // Invoke the first possibly-writing API while the same gate still
            // owns stop, loss, and pre-dispatch cancellation. Its synchronous
            // portion therefore cannot start after one of those transitions.
            // Awaiting its completion happens after the gate is released.
            write = firstPossiblyWriting(_identity, cancellationToken);
        }

        return write;
    }

    internal GuardianHostLossClassification ObserveHostLoss(
        HostBootId hostBootId,
        HostGeneration hostGeneration)
    {
        ArgumentNullException.ThrowIfNull(hostBootId);
        ArgumentNullException.ThrowIfNull(hostGeneration);

        lock (_sync)
        {
            if (!_identity.MatchesHost(hostBootId, hostGeneration))
            {
                return new(
                    GuardianHostLossDisposition.StaleHostIdentity,
                    IsFirstObservation: false);
            }

            if (_lossDisposition is { } existing)
                return new(existing, IsFirstObservation: false);

            _lossDisposition = _state switch
            {
                GuardianPublicDeliveryState.NotDispatched =>
                    GuardianHostLossDisposition.BackendLostBeforeDispatch,
                GuardianPublicDeliveryState.WriteStarted =>
                    GuardianHostLossDisposition.OutcomeUnknown,
                GuardianPublicDeliveryState.TerminalDecoded =>
                    GuardianHostLossDisposition.RetainedAuthoritativeTerminal,
                GuardianPublicDeliveryState.PublicTerminalSent =>
                    GuardianHostLossDisposition.PublicTerminalAlreadySent,
                _ => throw new InvalidOperationException("Unknown public delivery state."),
            };
            return new(_lossDisposition.Value, IsFirstObservation: true);
        }
    }

    internal GuardianTerminalCorrelationResult TryDecodeTerminal(
        GuardianPrivateRequestIdentity identity,
        TTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(terminal);

        lock (_sync)
        {
            if (_stopped)
                return GuardianTerminalCorrelationResult.Stopped;
            if (_correlationFatal)
                return GuardianTerminalCorrelationResult.CorrelationAlreadyFatal;
            if (!_identity.MatchesHost(identity.HostBootId, identity.HostGeneration))
                return LatchFatal(GuardianTerminalCorrelationResult.MismatchedHostFatal);
            if (_identity.RequestId != identity.RequestId)
                return LatchFatal(GuardianTerminalCorrelationResult.UnknownRequestFatal);
            if (_state is GuardianPublicDeliveryState.TerminalDecoded or
                GuardianPublicDeliveryState.PublicTerminalSent)
            {
                return LatchFatal(GuardianTerminalCorrelationResult.DuplicateTerminalFatal);
            }
            if (_lossDisposition is not null)
                return LatchFatal(GuardianTerminalCorrelationResult.GenerationLostFatal);
            if (_state != GuardianPublicDeliveryState.WriteStarted)
                return LatchFatal(GuardianTerminalCorrelationResult.UnexpectedDeliveryStateFatal);

            _terminal = terminal;
            _hasTerminal = true;
            _state = GuardianPublicDeliveryState.TerminalDecoded;
            return GuardianTerminalCorrelationResult.Accepted;
        }
    }

    /// <summary>
    /// Installs one guardian-local proved-no-start terminal. It is intentionally
    /// unavailable after the first private write boundary.
    /// </summary>
    internal GuardianLocalTerminalResult TrySetLocalTerminal(TTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        lock (_sync)
        {
            if (_state == GuardianPublicDeliveryState.TerminalDecoded)
                return GuardianLocalTerminalResult.TerminalAlreadyDecoded;
            if (_state == GuardianPublicDeliveryState.PublicTerminalSent)
                return GuardianLocalTerminalResult.PublicTerminalAlreadySent;
            if (_stopped)
                return GuardianLocalTerminalResult.Stopped;
            if (_state == GuardianPublicDeliveryState.WriteStarted)
                return GuardianLocalTerminalResult.WriteAlreadyStarted;

            _terminal = terminal;
            _hasTerminal = true;
            _state = GuardianPublicDeliveryState.TerminalDecoded;
            return GuardianLocalTerminalResult.Accepted;
        }
    }

    /// <summary>
    /// Cancels only this exact request in its original host generation.
    /// Before dispatch, the supplied local terminal is installed atomically
    /// and no private callback runs. After dispatch, the first possibly-writing
    /// cancellation callback is invoked while the same gate owns loss and stop;
    /// it is marked signaled before invocation and is never retried. A private
    /// cancellation signal never owns the request terminal.
    /// </summary>
    internal ValueTask<GuardianCancellationClaimResult> TryCancelAsync(
        GuardianPrivateRequestIdentity identity,
        TTerminal beforeDispatchTerminal,
        Func<GuardianPrivateRequestIdentity, CancellationToken, ValueTask> signalOriginalRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(beforeDispatchTerminal);
        ArgumentNullException.ThrowIfNull(signalOriginalRequest);

        ValueTask signal;
        lock (_sync)
        {
            if (_identity != identity)
                return ValueTask.FromResult(GuardianCancellationClaimResult.IdentityMismatch);
            if (_stopped)
                return ValueTask.FromResult(GuardianCancellationClaimResult.Stopped);
            if (_correlationFatal)
                return ValueTask.FromResult(GuardianCancellationClaimResult.CorrelationFatal);
            if (_lossDisposition is not null)
                return ValueTask.FromResult(GuardianCancellationClaimResult.GenerationLost);

            if (_state == GuardianPublicDeliveryState.NotDispatched)
            {
                _terminal = beforeDispatchTerminal;
                _hasTerminal = true;
                _state = GuardianPublicDeliveryState.TerminalDecoded;
                return ValueTask.FromResult(
                    GuardianCancellationClaimResult.BeforeDispatchTerminalInstalled);
            }
            if (_state is GuardianPublicDeliveryState.TerminalDecoded or
                GuardianPublicDeliveryState.PublicTerminalSent)
            {
                return ValueTask.FromResult(
                    GuardianCancellationClaimResult.TerminalAlreadyKnown);
            }
            if (_cancellationSignaled)
                return ValueTask.FromResult(GuardianCancellationClaimResult.AlreadySignaled);

            _cancellationSignaled = true;
            signal = signalOriginalRequest(_identity, cancellationToken);
        }

        return CompleteCancellationSignalAsync(signal);
    }

    /// <summary>
    /// Grants exactly one public writer and invokes it with the retained
    /// terminal. Claiming or beginning that write does not mean it was sent:
    /// the state remains TerminalDecoded through an in-flight or failed write
    /// and advances only after successful completion. A failed writer remains
    /// the sole owner because retrying a possibly-partial public write could
    /// duplicate a terminal.
    /// </summary>
    internal ValueTask<GuardianPublicTerminalDeliveryResult> DeliverPublicTerminalAsync(
        Func<TTerminal, CancellationToken, ValueTask> writePublicTerminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writePublicTerminal);

        ValueTask write;
        lock (_sync)
        {
            if (_state == GuardianPublicDeliveryState.PublicTerminalSent)
                return ValueTask.FromResult(
                    GuardianPublicTerminalDeliveryResult.AlreadySent);
            if (_state != GuardianPublicDeliveryState.TerminalDecoded || !_hasTerminal)
                return ValueTask.FromResult(
                    GuardianPublicTerminalDeliveryResult.NotAvailable);
            if (_publicDeliveryClaimed)
                return ValueTask.FromResult(
                    GuardianPublicTerminalDeliveryResult.AlreadyClaimed);

            _publicDeliveryClaimed = true;
            write = writePublicTerminal(_terminal!, cancellationToken);
        }

        return CompletePublicDeliveryAsync(write);
    }

    /// <summary>
    /// Terminal stop prevents every new private/local transition. A terminal
    /// decoded before stop remains claimable once because it is authoritative.
    /// </summary>
    internal bool Stop()
    {
        lock (_sync)
        {
            if (_stopped) return false;
            _stopped = true;
            return true;
        }
    }

    internal GuardianCallDeliverySnapshot Snapshot()
    {
        lock (_sync)
        {
            return new(
                _state,
                _identity,
                _lossDisposition,
                _cancellationSignaled,
                _publicDeliveryClaimed,
                _correlationFatal,
                _stopped,
                _hasTerminal);
        }
    }

    private GuardianTerminalCorrelationResult LatchFatal(
        GuardianTerminalCorrelationResult result)
    {
        _correlationFatal = true;
        return result;
    }

    private static async ValueTask<GuardianCancellationClaimResult>
        CompleteCancellationSignalAsync(ValueTask signal)
    {
        await signal.ConfigureAwait(false);
        return GuardianCancellationClaimResult.SignalOriginalRequest;
    }

    private async ValueTask<GuardianPublicTerminalDeliveryResult>
        CompletePublicDeliveryAsync(ValueTask write)
    {
        await write.ConfigureAwait(false);

        lock (_sync)
        {
            if (_state != GuardianPublicDeliveryState.TerminalDecoded || !_hasTerminal)
            {
                throw new InvalidOperationException(
                    "The retained public terminal changed while its writer was active.");
            }

            _state = GuardianPublicDeliveryState.PublicTerminalSent;
            _terminal = default;
            _hasTerminal = false;
            return GuardianPublicTerminalDeliveryResult.Sent;
        }
    }
}
