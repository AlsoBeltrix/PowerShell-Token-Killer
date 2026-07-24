using System.Text.Json;
using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

/// <summary>
/// Correlates already-validated worker operational events to the exact
/// prepared private operation that opened the commit barrier. Registrations
/// are installed before commit can be written, remain bounded, and retain a
/// started background operation until its single terminal fact is forwarded.
/// </summary>
internal sealed class PrivateHostWorkerEventBridge
{
    private readonly object _gate = new();
    private readonly PrivateHostServerIdentity _identity;
    private readonly IPrivateHostEventSink _events;
    private readonly Dictionary<Guid, RegistrationState> _registrations = [];

    internal PrivateHostWorkerEventBridge(
        PrivateHostServerIdentity identity,
        IPrivateHostEventSink events)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _events = events ?? throw new ArgumentNullException(nameof(events));
    }

    internal int RegistrationCount
    {
        get
        {
            lock (_gate) return _registrations.Count;
        }
    }

    internal PrivateHostWorkerEventRegistration Register(
        OperationRequest request,
        PrivateHostWorkerSlot slot,
        WorkerPreparedPlanDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(descriptor);
        var operation = request.OperationIdentity ??
            throw InvalidEvent(
                "Prepared worker event registration requires an operation identity.");
        var requestWorker = request.Worker ??
            throw InvalidEvent(
                "Prepared worker event registration requires a worker identity.");
        var publicJobId = request.Operation switch
        {
            InvokeForegroundOperation => (long?)null,
            InvokeBackgroundOperation background => background.PublicJobId.Value,
            _ => throw InvalidEvent(
                "Only prepared invocations can register worker events."),
        };
        var expectedContext = publicJobId is null
            ? ResolutionContext.Warm
            : ResolutionContext.Cold;
        if (request.GuardianBootId != _identity.GuardianBootId ||
            request.HostBootId != _identity.HostBootId ||
            request.HostGeneration != _identity.HostGeneration ||
            request.SessionAlias != slot.Binding.Alias ||
            request.SessionTransitionVersion != slot.Binding.TransitionVersion ||
            requestWorker.BootId != slot.Identity.BootId ||
            requestWorker.Generation != slot.Identity.Generation ||
            descriptor.PlanId != operation.PlanId.Value ||
            descriptor.WorkerBootId != slot.Identity.BootId.Value ||
            descriptor.Generation != slot.Identity.Generation.Value ||
            descriptor.DeadlineUtc.ToUnixTimeMilliseconds() !=
                request.DeadlineUnixTimeMilliseconds ||
            descriptor.ResolutionContext != expectedContext)
        {
            throw InvalidEvent(
                "Prepared worker event registration does not match its private operation.");
        }

        var descriptorDigest =
            WorkerPreparedOperationCodec.ComputePreparedDescriptorDigest(descriptor);
        var registration = new RegistrationState(
            request,
            slot.Identity,
            descriptor,
            descriptorDigest,
            publicJobId);
        lock (_gate)
        {
            if (_registrations.Count >=
                ContractLimits.MaximumOutstandingPrivateRequests)
            {
                throw new WorkerProtocolException(
                    "worker_event_capacity_exceeded",
                    "Prepared worker event registration capacity is exhausted.");
            }
            if (!_registrations.TryAdd(descriptor.PlanId, registration))
            {
                throw new WorkerProtocolException(
                    "worker_event_plan_replay",
                    "Prepared worker event plan IDs cannot be reused.");
            }
        }
        return new PrivateHostWorkerEventRegistration(this, registration);
    }

    internal async ValueTask HandleAsync(
        WorkerEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Kind != WorkerMessageKind.Event ||
            envelope.RequestId is not null ||
            envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            throw InvalidEvent("Worker event bridge received a non-event frame.");
        }

        var fields = ClosedFields(envelope.Payload);
        var eventName = RequiredString(fields, "event");
        var generation = RequiredPositiveInt64(fields, "generation");
        var planId = RequiredPlanId(fields, "planId");
        var descriptorDigest = RequiredDigest(fields, "descriptorDigest");

        RegistrationState registration;
        GuardianHostEventFactory factory;
        lock (_gate)
        {
            if (!_registrations.TryGetValue(planId, out registration!) ||
                !registration.CommitAuthorized ||
                envelope.WorkerBootId != registration.Worker.BootId.Value ||
                generation != registration.Worker.Generation.Value ||
                !string.Equals(
                    descriptorDigest,
                    registration.DescriptorDigest,
                    StringComparison.Ordinal))
            {
                throw InvalidEvent(
                    "Worker event does not match one commit-authorized preparation.");
            }

            factory = eventName switch
            {
                "validator_started" =>
                    BeginValidatorStarted(registration, fields),
                "validator_completed" =>
                    BeginValidatorCompleted(registration, fields),
                "job_terminal" =>
                    BeginJobTerminal(registration, fields),
                _ => throw InvalidEvent(
                    "Worker event bridge received an unknown event."),
            };
        }

        await _events.WriteEventAsync(
                sequence => factory(sequence),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal void MarkCommitAuthorized(RegistrationState registration)
    {
        lock (_gate)
        {
            RequireCurrent(registration);
            if (registration.CommitAuthorized)
                throw InvalidEvent("Prepared worker commit was authorized twice.");
            registration.CommitAuthorized = true;
        }
    }

    internal void CompleteForeground(RegistrationState registration)
    {
        lock (_gate)
        {
            RequireCurrent(registration);
            if (registration.PublicJobId is not null)
            {
                throw InvalidEvent(
                    "A background worker event registration cannot complete as foreground.");
            }
            _registrations.Remove(registration.Descriptor.PlanId);
            registration.Released = true;
        }
    }

    internal void CompleteBackgroundStart(
        RegistrationState registration,
        bool started)
    {
        lock (_gate)
        {
            RequireCurrent(registration);
            if (registration.PublicJobId is null)
            {
                throw InvalidEvent(
                    "A foreground worker event registration cannot complete as background.");
            }
            if (registration.BackgroundStartKnown)
                throw InvalidEvent("Background worker start was completed twice.");
            if (!started && registration.JobTerminalSeen)
            {
                throw InvalidEvent(
                    "A refused background start emitted a job terminal.");
            }

            registration.BackgroundStartKnown = true;
            registration.BackgroundStarted = started;
            if (!started || registration.JobTerminalSeen)
            {
                _registrations.Remove(registration.Descriptor.PlanId);
                registration.Released = true;
            }
            else
            {
                registration.Released = true;
            }
        }
    }

    internal void Abandon(RegistrationState registration)
    {
        lock (_gate)
        {
            if (registration.Released)
                return;
            if (_registrations.TryGetValue(
                    registration.Descriptor.PlanId,
                    out var current) &&
                ReferenceEquals(current, registration))
            {
                _registrations.Remove(registration.Descriptor.PlanId);
            }
            registration.Released = true;
        }
    }

    internal void RetireWorker(GuardianHostWorkerIdentity worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        lock (_gate)
        {
            foreach (var registration in _registrations.Values
                .Where(value =>
                    value.Worker.BootId == worker.BootId &&
                    value.Worker.Generation == worker.Generation)
                .ToArray())
            {
                _registrations.Remove(registration.Descriptor.PlanId);
                registration.Released = true;
            }
        }
    }

    private GuardianHostEventFactory BeginValidatorStarted(
        RegistrationState registration,
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        RequireExactFields(
            fields,
            "event",
            "generation",
            "planId",
            "descriptorDigest",
            "executionPath");
        RequireValidatorDescriptor(registration);
        if (RequiredString(fields, "executionPath") != "bash_via_rtk" ||
            registration.ValidatorStarted ||
            registration.ValidatorCompleted)
        {
            throw InvalidEvent("Worker validator start is out of order.");
        }
        registration.ValidatorStarted = true;
        var digest = new Sha256Digest(
            registration.Descriptor.BashBinaryDigest!);
        return sequence => new PreparedValidatorLifecycleEvent(
            _identity.GuardianBootId,
            _identity.HostBootId,
            _identity.HostGeneration,
            sequence,
            registration.Request.RequestId,
            registration.Request.SessionAlias!,
            registration.Request.SessionTransitionVersion!,
            registration.Worker,
            registration.Request.OperationIdentity!,
            GuardianHostValidatorPhase.Started,
            digest,
            exitCode: null);
    }

    private GuardianHostEventFactory BeginValidatorCompleted(
        RegistrationState registration,
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        RequireExactFields(
            fields,
            "event",
            "generation",
            "planId",
            "descriptorDigest",
            "executionPath",
            "detailCode",
            "processStarted",
            "exitCode",
            "rootTerminationConfirmed");
        RequireValidatorDescriptor(registration);
        if (RequiredString(fields, "executionPath") != "bash_via_rtk" ||
            registration.ValidatorCompleted)
        {
            throw InvalidEvent("Worker validator completion is out of order.");
        }
        _ = RequiredCode(fields, "detailCode");
        var processStarted = RequiredBoolean(fields, "processStarted");
        var exitCode = NullableInt32(fields, "exitCode");
        _ = NullableBoolean(fields, "rootTerminationConfirmed");
        if (processStarted != registration.ValidatorStarted ||
            processStarted && exitCode is null)
        {
            throw InvalidEvent(
                "Worker validator completion does not match its start fact.");
        }
        registration.ValidatorCompleted = true;
        var digest = new Sha256Digest(
            registration.Descriptor.BashBinaryDigest!);
        return sequence => new PreparedValidatorLifecycleEvent(
            _identity.GuardianBootId,
            _identity.HostBootId,
            _identity.HostGeneration,
            sequence,
            registration.Request.RequestId,
            registration.Request.SessionAlias!,
            registration.Request.SessionTransitionVersion!,
            registration.Worker,
            registration.Request.OperationIdentity!,
            GuardianHostValidatorPhase.Completed,
            digest,
            exitCode);
    }

    private GuardianHostEventFactory BeginJobTerminal(
        RegistrationState registration,
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        RequireExactFields(
            fields,
            "event",
            "generation",
            "planId",
            "descriptorDigest",
            "publicJobId",
            "state",
            "exitCode",
            "outputState",
            "outputBytes",
            "outputDigest");
        if (registration.PublicJobId is not { } expectedJobId ||
            RequiredPositiveInt64(fields, "publicJobId") != expectedJobId ||
            registration.JobTerminalSeen)
        {
            throw InvalidEvent(
                "Worker job terminal does not match one background invocation.");
        }

        var state = RequiredString(fields, "state") switch
        {
            "completed" => GuardianHostJobState.Completed,
            "failed" => GuardianHostJobState.Failed,
            "canceled" => GuardianHostJobState.Canceled,
            "lost" => GuardianHostJobState.Lost,
            "outcome_unknown" => GuardianHostJobState.OutcomeUnknown,
            _ => throw InvalidEvent("Worker job terminal state is invalid."),
        };
        var outputState = RequiredString(fields, "outputState") switch
        {
            "sealed" => GuardianHostOutputState.Sealed,
            "sealed_incomplete" => GuardianHostOutputState.SealedIncomplete,
            "unavailable" => GuardianHostOutputState.Unavailable,
            _ => throw InvalidEvent("Worker job output state is invalid."),
        };
        var exitCode = NullableInt32(fields, "exitCode");
        var outputBytes = RequiredNonnegativeInt32(fields, "outputBytes");
        if (outputBytes > ContractLimits.MaximumOutputBytes)
            throw InvalidEvent("Worker job terminal output size is invalid.");
        var outputDigest = NullableDigest(fields, "outputDigest");

        registration.JobTerminalSeen = true;
        if (registration.BackgroundStartKnown)
        {
            if (!registration.BackgroundStarted)
            {
                throw InvalidEvent(
                    "A refused background start emitted a job terminal.");
            }
            _registrations.Remove(registration.Descriptor.PlanId);
        }

        return sequence => new JobLifecycleEvent(
            _identity.GuardianBootId,
            _identity.HostBootId,
            _identity.HostGeneration,
            sequence,
            requestId: null,
            registration.Request.SessionAlias!,
            registration.Request.SessionTransitionVersion!,
            registration.Worker,
            registration.Request.OperationIdentity!,
            new PublicJobId(expectedJobId),
            state,
            exitCode,
            outputState,
            outputBytes,
            outputDigest);
    }

    private void RequireCurrent(RegistrationState registration)
    {
        if (registration.Released ||
            !_registrations.TryGetValue(
                registration.Descriptor.PlanId,
                out var current) ||
            !ReferenceEquals(current, registration))
        {
            throw InvalidEvent(
                "Prepared worker event registration is no longer current.");
        }
    }

    private static void RequireValidatorDescriptor(
        RegistrationState registration)
    {
        if (registration.Descriptor.PreExecutionValidation !=
                PreExecutionValidation.BashSyntax ||
            registration.Descriptor.EffectiveRoute != ExecutionPath.BashViaRtk ||
            registration.Descriptor.BashBinaryDigest is null)
        {
            throw InvalidEvent(
                "Worker validator event is not covered by its prepared descriptor.");
        }
    }

    private static Dictionary<string, JsonElement> ClosedFields(
        JsonElement payload)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value))
                throw InvalidEvent("Worker event contains a duplicate field.");
        }
        return fields;
    }

    private static void RequireExactFields(
        IReadOnlyDictionary<string, JsonElement> fields,
        params string[] allowed)
    {
        if (fields.Count != allowed.Length ||
            fields.Keys.Any(name =>
                !allowed.Contains(name, StringComparer.Ordinal)))
        {
            throw InvalidEvent("Worker event has an invalid field set.");
        }
    }

    private static string RequiredString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(value.GetString()))
        {
            throw InvalidEvent($"Worker event field '{name}' is invalid.");
        }
        return value.GetString()!;
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
            throw InvalidEvent($"Worker event field '{name}' is invalid.");
        }
        return parsed;
    }

    private static int RequiredNonnegativeInt32(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var parsed) ||
            parsed < 0)
        {
            throw InvalidEvent($"Worker event field '{name}' is invalid.");
        }
        return parsed;
    }

    private static Guid RequiredPlanId(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var value = RequiredString(fields, name);
        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            parsed == Guid.Empty ||
            parsed.ToString("D") != value ||
            (parsed.ToByteArray()[7] & 0xf0) != 0x40 ||
            (parsed.ToByteArray()[8] & 0xc0) != 0x80)
        {
            throw InvalidEvent($"Worker event field '{name}' is invalid.");
        }
        return parsed;
    }

    private static string RequiredDigest(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var value = RequiredString(fields, name);
        try
        {
            return new Sha256Digest(value).Value;
        }
        catch (ArgumentException)
        {
            throw InvalidEvent($"Worker event field '{name}' is invalid.");
        }
    }

    private static string RequiredCode(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var value = RequiredString(fields, name);
        if (value.Length > 128 ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z') and
                    not (>= '0' and <= '9') and not '_'))
        {
            throw InvalidEvent($"Worker event field '{name}' is invalid.");
        }
        return value;
    }

    private static bool RequiredBoolean(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw InvalidEvent($"Worker event field '{name}' is invalid.");
        }
        return value.GetBoolean();
    }

    private static int? NullableInt32(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value))
            throw InvalidEvent($"Worker event field '{name}' is invalid.");
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var parsed))
        {
            throw InvalidEvent($"Worker event field '{name}' is invalid.");
        }
        return parsed;
    }

    private static bool? NullableBoolean(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value))
            throw InvalidEvent($"Worker event field '{name}' is invalid.");
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidEvent($"Worker event field '{name}' is invalid."),
        };
    }

    private static Sha256Digest? NullableDigest(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value))
            throw InvalidEvent($"Worker event field '{name}' is invalid.");
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidEvent($"Worker event field '{name}' is invalid.");
        return new Sha256Digest(value.GetString()!);
    }

    private static WorkerProtocolException InvalidEvent(string message) =>
        new("invalid_worker_event_correlation", message);

    internal sealed class RegistrationState(
        OperationRequest request,
        GuardianHostWorkerIdentity worker,
        WorkerPreparedPlanDescriptor descriptor,
        string descriptorDigest,
        long? publicJobId)
    {
        internal OperationRequest Request { get; } = request;
        internal GuardianHostWorkerIdentity Worker { get; } = worker;
        internal WorkerPreparedPlanDescriptor Descriptor { get; } = descriptor;
        internal string DescriptorDigest { get; } = descriptorDigest;
        internal long? PublicJobId { get; } = publicJobId;
        internal bool CommitAuthorized { get; set; }
        internal bool ValidatorStarted { get; set; }
        internal bool ValidatorCompleted { get; set; }
        internal bool BackgroundStartKnown { get; set; }
        internal bool BackgroundStarted { get; set; }
        internal bool JobTerminalSeen { get; set; }
        internal bool Released { get; set; }
    }

    private delegate GuardianHostEvent GuardianHostEventFactory(
        HostEventSequence sequence);
}

internal sealed class PrivateHostWorkerEventRegistration
{
    private PrivateHostWorkerEventBridge? _owner;
    private readonly PrivateHostWorkerEventBridge.RegistrationState _state;

    internal PrivateHostWorkerEventRegistration(
        PrivateHostWorkerEventBridge owner,
        PrivateHostWorkerEventBridge.RegistrationState state)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    internal void MarkCommitAuthorized() =>
        RequireOwner().MarkCommitAuthorized(_state);

    internal void CompleteForeground()
    {
        RequireOwner().CompleteForeground(_state);
        _owner = null;
    }

    internal void CompleteBackgroundStart(bool started)
    {
        RequireOwner().CompleteBackgroundStart(_state, started);
        _owner = null;
    }

    internal void Abandon()
    {
        Interlocked.Exchange(ref _owner, null)?.Abandon(_state);
    }

    private PrivateHostWorkerEventBridge RequireOwner() =>
        Volatile.Read(ref _owner) ??
        throw new InvalidOperationException(
            "Prepared worker event registration is no longer owned.");
}
