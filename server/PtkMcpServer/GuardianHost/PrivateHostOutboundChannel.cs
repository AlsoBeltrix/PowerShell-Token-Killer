using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

/// <summary>
/// The event-only authority available to host runtime composition. Runtime
/// code can allocate a sequenced event at the shared serialization point, but
/// cannot write protocol responses or bypass frame ordering.
/// </summary>
internal interface IPrivateHostEventSink
{
    ValueTask WriteEventAsync(
        Func<HostEventSequence, GuardianHostEvent> createEvent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The host-side source-event correlation authority. A control event is
/// retained before its first wire byte and completes only after the protocol
/// server has written the exactly matched guardian response.
/// </summary>
internal interface IPrivateHostControlEventSink
{
    Task<GuardianHostRequest> ExchangeControlAsync(
        Func<HostEventSequence, GuardianHostEvent> createEvent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the host-to-guardian serialization point. All ordinary host frames
/// share one gate with event creation, so an event sequence is allocated only
/// after its exact wire position is owned.
/// </summary>
internal sealed class PrivateHostOutboundChannel :
    IPrivateHostEventSink,
    IPrivateHostControlEventSink
{
    private readonly object _controlSync = new();
    private readonly SemaphoreSlim _serialization = new(1, 1);
    private readonly GuardianHostProtocolWriter _writer;
    private readonly PrivateHostServerIdentity _identity;
    private readonly Dictionary<long, PendingControlEvent> _pendingControls = [];
    private long _lastAllocatedEventSequence;

    internal PrivateHostOutboundChannel(
        Stream hostEventStream,
        PrivateHostServerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(hostEventStream);
        ArgumentNullException.ThrowIfNull(identity);
        if (!hostEventStream.CanWrite)
            throw new ArgumentException(
                "Host event stream must be writable.",
                nameof(hostEventStream));

        _writer = new GuardianHostProtocolWriter(
            hostEventStream,
            GuardianHostPeer.Host);
        _identity = identity;
    }

    internal int PendingControlCount
    {
        get
        {
            lock (_controlSync)
                return _pendingControls.Count;
        }
    }

    internal async ValueTask WriteFrameAsync(
        GuardianHostMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is GuardianHostEvent)
        {
            throw new ArgumentException(
                "Host events require channel-owned sequence allocation.",
                nameof(message));
        }

        var acquired = false;
        try
        {
            await _serialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            ValidateIdentity(message);
            await _writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
                _serialization.Release();
        }
    }

    internal async ValueTask WriteEventAsync(
        Func<HostEventSequence, GuardianHostEvent> createEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createEvent);

        var acquired = false;
        GuardianHostEvent? message = null;
        try
        {
            await _serialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();

            var nextValue = checked(_lastAllocatedEventSequence + 1);
            _lastAllocatedEventSequence = nextValue;
            var assignedSequence = new HostEventSequence(nextValue);
            message = createEvent(assignedSequence) ?? throw Protocol(
                "outbound_event_factory_invalid",
                "Host event factory returned no event.");
            ValidateIdentity(message);
            if (message.EventSequence != assignedSequence)
            {
                throw Protocol(
                    "outbound_event_sequence_mismatch",
                    "Host event does not carry its channel-assigned sequence.");
            }

            await _writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (message is IDisposable disposable)
                disposable.Dispose();
            if (acquired)
                _serialization.Release();
        }
    }

    ValueTask IPrivateHostEventSink.WriteEventAsync(
        Func<HostEventSequence, GuardianHostEvent> createEvent,
        CancellationToken cancellationToken) =>
        WriteEventAsync(createEvent, cancellationToken);

    public async Task<GuardianHostRequest> ExchangeControlAsync(
        Func<HostEventSequence, GuardianHostEvent> createEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createEvent);
        PendingControlEvent? pending = null;
        try
        {
            await WriteEventAsync(
                    sequence =>
                    {
                        var sourceEvent = createEvent(sequence) ??
                            throw Protocol(
                                "outbound_control_event_invalid",
                                "Host control event factory returned no event.");
                        if (!IsSupportedControlEvent(sourceEvent))
                        {
                            throw Protocol(
                                "outbound_control_event_invalid",
                                "Host control exchange received an unsupported event.");
                        }

                        var created = new PendingControlEvent(sourceEvent);
                        lock (_controlSync)
                        {
                            if (_pendingControls.Count >=
                                ContractLimits.MaximumPendingControlEvents)
                            {
                                throw Protocol(
                                    "outbound_control_limit_exceeded",
                                    "Host pending control-event capacity is exhausted.");
                            }
                            _pendingControls.Add(sequence.Value, created);
                        }
                        pending = created;
                        return sourceEvent;
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return await pending!.Completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (pending is not null)
                AbandonControl(pending, cancellationToken);
            throw;
        }
    }

    internal PrivateHostControlAcknowledgement ClaimControlAcknowledgement(
        GuardianHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceSequence = request switch
        {
            WorkerCreateCapabilityGrantRequest grant =>
                grant.SourceEventSequence,
            WorkerContainmentAckRequest acknowledgement =>
                acknowledgement.SourceEventSequence,
            PreparedDispatchAuthorizeRequest authorization =>
                authorization.SourceEventSequence,
            _ => throw Protocol(
                "control_request_invalid",
                "Guardian request is not a supported host control acknowledgement."),
        };

        lock (_controlSync)
        {
            if (!_pendingControls.TryGetValue(
                    sourceSequence.Value,
                    out var pending) ||
                pending.Claimed ||
                !ControlPairMatches(pending.SourceEvent, request))
            {
                throw Protocol(
                    "control_correlation_invalid",
                    "Guardian control request does not match one pending host event.");
            }
            pending.Claimed = true;
            return new PrivateHostControlAcknowledgement(
                this,
                pending,
                request);
        }
    }

    internal void FailPendingControls()
    {
        PendingControlEvent[] pending;
        lock (_controlSync)
        {
            pending = _pendingControls.Values.ToArray();
            _pendingControls.Clear();
        }
        foreach (var item in pending)
        {
            item.Completion.TrySetException(Protocol(
                "control_channel_stopped",
                "Host control exchange stopped before acknowledgement."));
        }
    }

    private void CompleteControl(
        PendingControlEvent pending,
        GuardianHostRequest request)
    {
        lock (_controlSync)
        {
            if (!_pendingControls.Remove(
                    pending.SourceEvent.EventSequence.Value,
                    out var removed) ||
                !ReferenceEquals(removed, pending) ||
                !pending.Claimed)
            {
                throw new InvalidOperationException(
                    "Host control acknowledgement lost its pending source event.");
            }
        }
        if (!pending.Completion.TrySetResult(request))
        {
            throw new InvalidOperationException(
                "Host control acknowledgement completed more than once.");
        }
    }

    private void FailControl(PendingControlEvent pending)
    {
        lock (_controlSync)
        {
            if (!_pendingControls.Remove(
                    pending.SourceEvent.EventSequence.Value,
                    out var removed) ||
                !ReferenceEquals(removed, pending))
            {
                return;
            }
        }
        pending.Completion.TrySetException(Protocol(
            "control_response_not_written",
            "Host control acknowledgement response was not written."));
    }

    private void AbandonControl(
        PendingControlEvent pending,
        CancellationToken cancellationToken)
    {
        lock (_controlSync)
        {
            if (!_pendingControls.Remove(
                    pending.SourceEvent.EventSequence.Value,
                    out var removed) ||
                !ReferenceEquals(removed, pending))
            {
                return;
            }
        }
        pending.Completion.TrySetCanceled(cancellationToken);
    }

    private static bool IsSupportedControlEvent(GuardianHostEvent sourceEvent) =>
        sourceEvent is
            WorkerCreateCapabilityRequestedEvent or
            WorkerContainmentPendingEvent or
            WorkerContainmentArmedEvent or
            WorkerContainmentRemoveRequestedEvent or
            PreparedDispatchAuthorizationRequestedEvent;

    private static bool ControlPairMatches(
        GuardianHostEvent sourceEvent,
        GuardianHostRequest request)
    {
        if (sourceEvent.SessionAlias != request.SessionAlias ||
            sourceEvent.SessionTransitionVersion !=
                request.SessionTransitionVersion)
        {
            return false;
        }

        return (sourceEvent, request) switch
        {
            (WorkerCreateCapabilityRequestedEvent created,
                WorkerCreateCapabilityGrantRequest grant) =>
                grant.DeadlineUnixTimeMilliseconds ==
                    created.StartupDeadlineUnixTimeMilliseconds,
            (WorkerContainmentPendingEvent pending,
                WorkerContainmentPendingAckRequest acknowledgement) =>
                WorkerMatches(pending.WorkerIdentity, acknowledgement.WorkerIdentity),
            (WorkerContainmentArmedEvent armed,
                WorkerContainmentArmedAckRequest acknowledgement) =>
                WorkerMatches(armed.WorkerIdentity, acknowledgement.WorkerIdentity),
            (WorkerContainmentRemoveRequestedEvent remove,
                WorkerContainmentRemoveAckRequest acknowledgement) =>
                WorkerMatches(remove.WorkerIdentity, acknowledgement.WorkerIdentity),
            (PreparedDispatchAuthorizationRequestedEvent prepared,
                PreparedDispatchAuthorizeRequest authorization) =>
                WorkerMatches(
                    prepared.WorkerIdentity,
                    authorization.WorkerIdentity) &&
                OperationMatches(
                    prepared.OperationIdentity,
                    authorization.OperationIdentity) &&
                authorization.DeadlineUnixTimeMilliseconds ==
                    prepared.Descriptor.DeadlineUnixTimeMilliseconds &&
                authorization.DescriptorDigest ==
                    prepared.Descriptor.DescriptorDigest,
            _ => false,
        };
    }

    private static bool WorkerMatches(
        GuardianHostWorkerIdentity? left,
        GuardianHostWorkerIdentity? right) =>
        left is not null &&
        right is not null &&
        left.BootId == right.BootId &&
        left.Generation == right.Generation;

    private static bool OperationMatches(
        GuardianHostOperationIdentity? left,
        GuardianHostOperationIdentity? right) =>
        left is not null &&
        right is not null &&
        left.PlanId == right.PlanId &&
        left.OperationId == right.OperationId;

    private void ValidateIdentity(GuardianHostMessage message)
    {
        if (message.Sender != GuardianHostPeer.Host ||
            message.GuardianBootId != _identity.GuardianBootId ||
            message.HostBootId != _identity.HostBootId ||
            message.HostGeneration != _identity.HostGeneration)
        {
            throw Protocol(
                "outbound_identity_mismatch",
                "Host outbound identity does not match this generation.");
        }
    }

    private static GuardianHostProtocolException Protocol(
        string detailCode,
        string message) => new(detailCode, message);

    internal sealed class PendingControlEvent(GuardianHostEvent sourceEvent)
    {
        internal GuardianHostEvent SourceEvent { get; } = sourceEvent;
        internal TaskCompletionSource<GuardianHostRequest> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Claimed { get; set; }
    }

    internal sealed class PrivateHostControlAcknowledgement(
        PrivateHostOutboundChannel owner,
        PendingControlEvent pending,
        GuardianHostRequest request) : IDisposable
    {
        private PrivateHostOutboundChannel? _owner = owner;

        internal void Complete()
        {
            var claimedOwner = Interlocked.Exchange(ref _owner, null) ??
                throw new InvalidOperationException(
                    "Host control acknowledgement completed more than once.");
            claimedOwner.CompleteControl(pending, request);
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.FailControl(pending);
    }
}
