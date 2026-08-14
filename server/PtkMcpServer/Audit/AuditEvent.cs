using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PtkMcpServer.Audit;

internal sealed record AuditProducerContext(
    Guid HostId,
    Guid SupervisorBootId,
    Guid? WorkerBootId,
    long? Pid,
    string Version,
    string? BinaryDigest,
    Guid? PreviousSupervisorBootId = null);

internal sealed record AuditEventInput
{
    public required string EventType { get; init; }
    public required AuditSession Session { get; init; }
    public required AuditActor Actor { get; init; }
    public AuditCallAttribution? CallAttribution { get; init; }
    public AuditClientCallContext? ClientCallContext { get; init; }
    public AuditExecutionContext? ExecutionContext { get; init; }
    public IReadOnlyList<AuditEvidenceManifestEntry>? EvidenceManifest { get; init; }
    public required AuditCorrelation Correlation { get; init; }
    public required AuditRequest Request { get; init; }
    public AuditOperatorDispositionFacts? OperatorDisposition { get; init; }
    public required AuditRouting Routing { get; init; }
    public required AuditOutcome Outcome { get; init; }
    public required AuditCoverage Coverage { get; init; }
    public required AuditEventHealth Audit { get; init; }
}

internal sealed record AuditOperatorDispositionFacts
{
    public Guid? DispositionId { get; init; }
    public required Guid TargetSupervisorBootId { get; init; }
    public string? TargetSpoolFile { get; init; }
    public long? TargetStartOffset { get; init; }
    public long? TargetNextOffset { get; init; }
    public long? TargetSequence { get; init; }
    public required Guid TargetEventId { get; init; }
    public string? FailureClass { get; init; }
    public string? DetailCode { get; init; }
    public string? ResponseDigest { get; init; }
    public DateTimeOffset? FirstFailureUtc { get; init; }
    public string? TargetExportConfigurationIdentity { get; init; }
    public required string ProofKind { get; init; }
    public string? VerifiedReceiptDigest { get; init; }
    public string? AcknowledgedGapReason { get; init; }
}

internal sealed record AuditSession
{
    public string? Name { get; init; }
    public long? Generation { get; init; }
    public string? BindingKind { get; init; }
    public string? TemplateName { get; init; }
    public string? TemplateDigest { get; init; }
    public string? BootstrapDigest { get; init; }
    public string? DeclaredPurpose { get; init; }
    public string? DeclaredTarget { get; init; }
    public string? DeclaredIdentity { get; init; }
    public string? EffectiveIdentity { get; init; }
    public bool? AllowColdBackground { get; init; }
}

internal sealed record AuditActor
{
    public string? Transport { get; init; }
    public string? ClientName { get; init; }
    public string? ClientVersion { get; init; }
    public string? ClientSessionId { get; init; }
    public required string AttributionStrength { get; init; }

    internal AuditCallAttribution CallAttribution { get; init; } =
        AuditCallAttribution.NotSupplied;

    internal AuditClientCallContext ClientCallContext { get; init; } =
        AuditClientCallContext.NotSupplied;

    internal AuditExecutionContext InitialExecutionContext { get; init; } =
        AuditExecutionContext.NotSupplied;
}

internal sealed record AuditCorrelation
{
    public Guid? CallId { get; init; }
    public long? JobId { get; init; }
    public Guid? ParentEventId { get; init; }
    public string? TraceId { get; init; }
    public Guid? PlanId { get; init; }
}

internal sealed record AuditRequest
{
    public string? Tool { get; init; }
    public string? Action { get; init; }
    public IReadOnlyList<string> ProvidedFields { get; init; } = Array.Empty<string>();
    public string? SessionRequested { get; init; }
    public string? Cwd { get; init; }
    public string? DestinationKind { get; init; }
    public string? DestinationPath { get; init; }
    public long? TimeoutMs { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    public string? Route { get; init; }
    public bool? Background { get; init; }
    public bool? Raw { get; init; }
    public bool? ListAvailable { get; init; }
    public long? JobId { get; init; }
    public long? Offset { get; init; }
    public long? ExpectedGeneration { get; init; }
    public bool? Force { get; init; }
    public string? Template { get; init; }
    public bool? AllowColdBackground { get; init; }
    public long? MaxBytes { get; init; }
    public string? PatternFingerprint { get; init; }
    public string? OutputHandleDigest { get; init; }
    public string? OriginalScriptDigest { get; init; }
    public Guid? ScriptEvidenceId { get; init; }
    public Guid? EvidenceSubjectId { get; init; }
    public string? EvidenceSubjectDigest { get; init; }
    public long? EvidenceSubjectBytes { get; init; }
    public string? EvidenceSubjectState { get; init; }
    public string? RetentionReason { get; init; }
}

internal sealed record AuditRouting
{
    public string? Domain { get; init; }
    public string? RequestedRoute { get; init; }
    public string? EffectiveRoute { get; init; }
    public IReadOnlyList<string> PermittedFallbacks { get; init; } = Array.Empty<string>();
    public string? RtkVersion { get; init; }
    public string? RtkBinaryDigest { get; init; }
    public string? Provenance { get; init; }
    public string? FallbackReason { get; init; }
}

internal sealed record AuditOutcome
{
    public string? State { get; init; }
    public string? DetailCode { get; init; }
    public long? ExitCode { get; init; }
    public long? DurationMs { get; init; }
    public long? QueueMs { get; init; }
    public long? BytesReturned { get; init; }
    public long? NextOffset { get; init; }
    public bool? WarmStateLost { get; init; }
    public bool? WorkerReplaced { get; init; }
    public string? TerminationCertainty { get; init; }
}

internal sealed record AuditCoverage
{
    public required bool PtkRequest { get; init; }
    public required string RootProcessObserved { get; init; }
    public required string DescendantsObserved { get; init; }
    public required string RemoteEffectObserved { get; init; }
}

internal sealed record AuditEventHealth
{
    public required string ProtectionMode { get; init; }
    public string? ExportConfigurationIdentity { get; init; }
    public string? HealthState { get; init; }
    public string? FailureClass { get; init; }
    public DateTimeOffset? DegradedSinceUtc { get; init; }
    public long? EmergencyProbeCount { get; init; }
    public DateTimeOffset? EmergencyProbeFirstUtc { get; init; }
    public DateTimeOffset? EmergencyProbeLastUtc { get; init; }
}

internal readonly record struct SerializedAuditEvent(
    ReadOnlyMemory<byte> Utf8Line,
    string EventHash,
    Guid EventId,
    long Sequence);

internal sealed class AuditEventValidationException(string message) : Exception(message);

/// <summary>
/// Validates and serializes one strict ptk.audit/2 or ptk.audit/4 core record. The returned
/// bytes are the authoritative bytes: callers must persist/export them as-is,
/// never reserialize the event after its hash has been computed.
/// </summary>
internal static class AuditEventSerializer
{
    internal const int MaximumLineBytes = 65_536;
    internal const string LegacySchemaVersion = "ptk.audit/1";
    internal const string CurrentSchemaVersion = "ptk.audit/2";
    internal const string CallContextSchemaVersion = "ptk.audit/4";
    internal const string FullEvidenceSchemaVersion = "ptk.audit/5";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> BindingKinds =
        new(["default", "dynamic", "template"], StringComparer.Ordinal);
    private static readonly HashSet<string> Transports =
        new(["mcp_stdio"], StringComparer.Ordinal);
    private static readonly HashSet<string> AttributionStrengths =
        new(["unknown", "transport_only", "client_asserted", "authenticated"], StringComparer.Ordinal);
    private static readonly HashSet<string> CallContextSources =
        new(["client", "operator_configuration"], StringComparer.Ordinal);
    private static readonly HashSet<string> CallContextStrengths =
        new(["transport_only", "client_asserted"], StringComparer.Ordinal);
    private static readonly HashSet<string> NotSuppliedReasons =
        new(["not_supplied_by_client"], StringComparer.Ordinal);
    private static readonly HashSet<string> EffectiveCwdUnavailableReasons =
        new(["not_dispatched", "not_available_at_dispatch"], StringComparer.Ordinal);
    private static readonly HashSet<string> RepositoryUnavailableReasons =
        new(
            ["not_dispatched", "effective_cwd_unavailable", "repository_not_detected"],
            StringComparer.Ordinal);
    private static readonly HashSet<string> Routes =
        new(["auto", "pwsh", "rtk"], StringComparer.Ordinal);
    private static readonly HashSet<string> DestinationKinds =
        new(["stdout", "protected_file"], StringComparer.Ordinal);
    private static readonly HashSet<string> DispositionProofKinds =
        new(["verified_receipt", "acknowledged_gap"], StringComparer.Ordinal);
    private static readonly HashSet<string> DispositionFailureClasses =
        new(["partial_rejection", "data", "protocol"], StringComparer.Ordinal);
    private static readonly HashSet<string> Domains =
        new(["powershell", "native_terminal", "mixed_dataflow", "bash"], StringComparer.Ordinal);
    private static readonly HashSet<string> EffectiveRoutes =
        new(["powershell_direct", "rtk", "native_direct", "bash_via_rtk"], StringComparer.Ordinal);
    private static readonly HashSet<string> PermittedFallbackValues =
        new(["powershell_direct", "native_direct"], StringComparer.Ordinal);
    private static readonly HashSet<string> ProvenanceValues =
        new(["powershell_objects", "direct_text", "rtk_unknown", "rtk_filtered", "rtk_passthrough"], StringComparer.Ordinal);
    private static readonly HashSet<string> TerminationCertainties =
        new(["not_applicable", "confirmed", "unconfirmed", "unknown"], StringComparer.Ordinal);
    private static readonly HashSet<string> RootCoverageValues =
        new(["complete", "none", "unknown", "not_applicable"], StringComparer.Ordinal);
    private static readonly HashSet<string> OtherCoverageValues =
        new(["complete", "partial", "none", "unknown", "not_applicable"], StringComparer.Ordinal);
    private static readonly HashSet<string> ProtectionModes =
        new(["local-only", "anchored"], StringComparer.Ordinal);
    private static readonly HashSet<string> HealthStates =
        new(["healthy", "degraded", "recovered"], StringComparer.Ordinal);
    private static readonly HashSet<string> EventsRequiringPlanId =
        new([
            "execution.planned",
            "execution.dispatched",
            "execution.validation_started",
            "execution.validation_completed",
            "execution.validator_started",
            "execution.validator_completed",
            "execution.validator_failed",
            "execution.not_started",
            "execution.completed",
            "execution.failed",
            "execution.canceled",
            "execution.timed_out",
            "execution.outcome_unknown"
        ], StringComparer.Ordinal);
    private static readonly HashSet<string> OperatorDispositionEvents =
        new([
            "export.disposition_intent",
            "export.disposition_authorized",
            "export.disposition_completed",
            "export.disposition_failed",
        ], StringComparer.Ordinal);
    private static readonly HashSet<string> EvidenceRetentionEvents =
        new([
            "evidence.retention_intent",
            "evidence.retention_completed",
            "evidence.retention_failed",
        ], StringComparer.Ordinal);
    private static readonly HashSet<string> EvidenceSubjectStates =
        new(["local_committed", "anchored", "unreferenced", "temporary"], StringComparer.Ordinal);
    private static readonly HashSet<string> EvidenceRetentionReasons =
        new(["age_expired", "capacity_pressure", "crash_temporary"], StringComparer.Ordinal);

    internal static SerializedAuditEvent Serialize(
        long sequence,
        string? previousEventHash,
        AuditProducerContext producer,
        AuditEventInput input,
        Guid eventId,
        DateTimeOffset occurredUtc,
        DateTimeOffset observedUtc)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(input);

        var normalized = Validate(
            sequence,
            previousEventHash,
            producer,
            input,
            eventId,
            occurredUtc,
            observedUtc);

        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.Default,
            Indented = false,
            SkipValidation = false
        }))
        {
            WritePreHashEvent(
                writer,
                sequence,
                previousEventHash,
                producer,
                input,
                eventId,
                occurredUtc,
                observedUtc,
                normalized.ProvidedFields,
                normalized.PermittedFallbacks);
            writer.Flush();
        }

        var preHashBytes = buffer.WrittenSpan;
        if (preHashBytes.Length == 0 || preHashBytes[^1] != (byte)'}')
            throw new InvalidOperationException("Audit serializer did not produce one complete JSON object.");

        var eventHash = Convert.ToHexString(SHA256.HashData(preHashBytes)).ToLowerInvariant();
        var suffix = Encoding.ASCII.GetBytes($",\"event_hash\":\"{eventHash}\"}}");
        var lineLength = checked(preHashBytes.Length - 1 + suffix.Length + 1);
        if (lineLength > MaximumLineBytes)
        {
            throw Invalid(
                "event",
                $"serialized record is {lineLength} UTF-8 bytes including LF; maximum is {MaximumLineBytes}");
        }

        var line = new byte[lineLength];
        preHashBytes[..^1].CopyTo(line);
        suffix.CopyTo(line.AsSpan(preHashBytes.Length - 1));
        line[^1] = (byte)'\n';
        return new SerializedAuditEvent(line, eventHash, eventId, sequence);
    }

    private static NormalizedArrays Validate(
        long sequence,
        string? previousEventHash,
        AuditProducerContext producer,
        AuditEventInput input,
        Guid eventId,
        DateTimeOffset occurredUtc,
        DateTimeOffset observedUtc)
    {
        if (sequence < 1)
            throw Invalid("sequence", "must be in the range 1..Int64.MaxValue");
        if (sequence == 1)
        {
            if (previousEventHash is not null)
                throw Invalid("previous_event_hash", "must be null when sequence is 1");
        }
        else
        {
            RequireLowerHex(previousEventHash, 64, "previous_event_hash", nullable: false);
        }

        RequireUuid(eventId, 7, "event_id");
        RequireUtc(occurredUtc, "occurred_utc");
        RequireUtc(observedUtc, "observed_utc");
        RequireUuid(producer.HostId, 4, "producer.host_id");
        RequireUuid(producer.SupervisorBootId, 4, "producer.supervisor_boot_id");
        RequireNullableUuid(
            producer.PreviousSupervisorBootId,
            4,
            "producer.previous_supervisor_boot_id");
        RequireNullableUuid(producer.WorkerBootId, 4, "producer.worker_boot_id");
        RequirePositive(producer.Pid, "producer.pid");
        RequireText(producer.Version, 128, "producer.version", nullable: false);
        RequireLowerHex(producer.BinaryDigest, 64, "producer.binary_digest", nullable: true);

        RequireEventCode(input.EventType, "event_type");
        ArgumentNullException.ThrowIfNull(input.Session);
        ArgumentNullException.ThrowIfNull(input.Actor);
        ArgumentNullException.ThrowIfNull(input.Correlation);
        ArgumentNullException.ThrowIfNull(input.Request);
        ArgumentNullException.ThrowIfNull(input.Routing);
        ArgumentNullException.ThrowIfNull(input.Outcome);
        ArgumentNullException.ThrowIfNull(input.Coverage);
        ArgumentNullException.ThrowIfNull(input.Audit);

        ValidateSession(input.Session);
        ValidateActor(input.Actor);
        ValidateCallContext(input);
        ValidateEvidenceManifest(input.EvidenceManifest);
        ValidateCorrelation(input.Correlation);
        var providedFields = NormalizeProvidedFields(input.Request.ProvidedFields);
        ValidateRequest(input.EventType, input.Request);
        ValidateOperatorDispositionFacts(input.EventType, input.OperatorDisposition);
        var permittedFallbacks = NormalizePermittedFallbacks(input.Routing.PermittedFallbacks);
        ValidateRouting(input.Routing);
        ValidateOutcome(input.Outcome);
        ValidateCoverage(input.Coverage);
        ValidateAudit(input.Audit);

        if (EventsRequiringPlanId.Contains(input.EventType) && input.Correlation.PlanId is null)
            throw Invalid("correlation.plan_id", $"must be nonnull for {input.EventType}");
        if (input.Correlation.PlanId is null && permittedFallbacks.Length != 0)
            throw Invalid("routing.permitted_fallbacks", "must be empty when there is no prepared plan");

        return new NormalizedArrays(providedFields, permittedFallbacks);
    }

    internal static void ValidateOperatorDispositionFacts(
        string eventType,
        AuditOperatorDispositionFacts? disposition)
    {
        ValidateOperatorDisposition(disposition);
        if (OperatorDispositionEvents.Contains(eventType) !=
            (disposition is not null))
        {
            throw Invalid(
                "operator_disposition",
                OperatorDispositionEvents.Contains(eventType)
                    ? $"must be nonnull for {eventType}"
                    : $"must be null for {eventType}");
        }
        if (disposition is not null)
        {
            var resolved = disposition.DispositionId is not null;
            if (eventType == "export.disposition_intent" && resolved)
            {
                throw Invalid(
                    "operator_disposition.disposition_id",
                    "must be null on export.disposition_intent");
            }
            if (eventType is (
                    "export.disposition_authorized" or
                    "export.disposition_completed") && !resolved)
            {
                throw Invalid(
                    "operator_disposition.disposition_id",
                    $"must be nonnull on {eventType}");
            }
        }
    }

    private static void ValidateSession(AuditSession value)
    {
        RequireSessionName(value.Name, "session.name", nullable: true);
        RequireNonNegative(value.Generation, "session.generation");
        RequireEnum(value.BindingKind, BindingKinds, "session.binding_kind", nullable: true);
        RequireTemplateName(value.TemplateName, "session.template_name", nullable: true);
        RequireLowerHex(value.TemplateDigest, 64, "session.template_digest", nullable: true);
        RequireLowerHex(value.BootstrapDigest, 64, "session.bootstrap_digest", nullable: true);
        RequireText(value.DeclaredPurpose, 512, "session.declared_purpose", nullable: true);
        RequireText(value.DeclaredTarget, 256, "session.declared_target", nullable: true);
        RequireText(value.DeclaredIdentity, 256, "session.declared_identity", nullable: true);
        RequireText(value.EffectiveIdentity, 256, "session.effective_identity", nullable: true);
    }

    private static void ValidateActor(AuditActor value)
    {
        RequireEnum(value.Transport, Transports, "actor.transport", nullable: true);
        RequireText(value.ClientName, 256, "actor.client_name", nullable: true);
        RequireText(value.ClientVersion, 256, "actor.client_version", nullable: true);
        RequireText(value.ClientSessionId, 256, "actor.client_session_id", nullable: true);
        RequireEnum(value.AttributionStrength, AttributionStrengths, "actor.attribution_strength", nullable: false);
    }

    private static void ValidateCallContext(AuditEventInput input)
    {
        var present =
            (input.CallAttribution is null ? 0 : 1) +
            (input.ClientCallContext is null ? 0 : 1) +
            (input.ExecutionContext is null ? 0 : 1);
        if (present == 0)
            return;
        if (present != 3)
            throw Invalid("call_context", "all ptk.audit/4 call-context objects are required together");

        var attribution = input.CallAttribution!;
        RequireText(attribution.AgentName, 256, "call_attribution.agent_name", nullable: true);
        RequireEnum(
            attribution.AgentUnavailableReason,
            NotSuppliedReasons,
            "call_attribution.agent_unavailable_reason",
            nullable: true);
        RequireText(attribution.ModelProvider, 256, "call_attribution.model_provider", nullable: true);
        RequireText(attribution.ModelName, 256, "call_attribution.model_name", nullable: true);
        RequireEnum(
            attribution.ModelUnavailableReason,
            NotSuppliedReasons,
            "call_attribution.model_unavailable_reason",
            nullable: true);
        RequireEnum(attribution.Source, CallContextSources, "call_attribution.source", nullable: true);
        RequireEnum(attribution.Strength, CallContextStrengths, "call_attribution.strength", nullable: false);
        if ((attribution.AgentName is null) != (attribution.AgentUnavailableReason is not null))
            throw Invalid("call_attribution.agent_unavailable_reason", "must label exactly missing agent name");
        var modelIncomplete = attribution.ModelProvider is null || attribution.ModelName is null;
        if (modelIncomplete != (attribution.ModelUnavailableReason is not null))
            throw Invalid("call_attribution.model_unavailable_reason", "must label incomplete model identity");
        var hasIdentity = attribution.AgentName is not null ||
            attribution.ModelProvider is not null || attribution.ModelName is not null;
        if (hasIdentity)
        {
            if (attribution.Source is null || attribution.Strength != "client_asserted")
                throw Invalid("call_attribution", "supplied identity must remain an asserted source");
        }
        else if (attribution.Source is not null || attribution.Strength != "transport_only")
        {
            throw Invalid("call_attribution", "absent identity must remain transport_only with no source");
        }

        var clientContext = input.ClientCallContext!;
        RequireText(clientContext.TaskId, 256, "client_context.task_id", nullable: true);
        RequireText(clientContext.TaskName, 256, "client_context.task_name", nullable: true);
        RequireNonNegative(clientContext.McpTaskTtlMs, "client_context.mcp_task_ttl_ms");
        RequireEnum(
            clientContext.TaskUnavailableReason,
            NotSuppliedReasons,
            "client_context.task_unavailable_reason",
            nullable: true);
        RequireText(clientContext.RunId, 256, "client_context.run_id", nullable: true);
        RequireEnum(
            clientContext.RunUnavailableReason,
            NotSuppliedReasons,
            "client_context.run_unavailable_reason",
            nullable: true);
        RequireEnum(clientContext.Source, CallContextSources, "client_context.source", nullable: true);
        RequireEnum(clientContext.Strength, CallContextStrengths, "client_context.strength", nullable: false);
        var hasTaskIdentity = clientContext.TaskId is not null || clientContext.TaskName is not null;
        if (hasTaskIdentity == (clientContext.TaskUnavailableReason is not null))
            throw Invalid("client_context.task_unavailable_reason", "must label exactly missing task identity");
        if ((clientContext.RunId is null) != (clientContext.RunUnavailableReason is not null))
            throw Invalid("client_context.run_unavailable_reason", "must label exactly missing run identity");
        var hasClientContext = hasTaskIdentity ||
            clientContext.RunId is not null || clientContext.McpTaskTtlMs is not null;
        if (hasClientContext)
        {
            if (clientContext.Source is null || clientContext.Strength != "client_asserted")
                throw Invalid("client_context", "supplied context must remain an asserted source");
        }
        else if (clientContext.Source is not null || clientContext.Strength != "transport_only")
        {
            throw Invalid("client_context", "absent context must remain transport_only with no source");
        }

        var execution = input.ExecutionContext!;
        RequireText(execution.RequestedCwd, 4_096, "execution_context.requested_cwd", nullable: true);
        RequireEnum(
            execution.RequestedCwdUnavailableReason,
            NotSuppliedReasons,
            "execution_context.requested_cwd_unavailable_reason",
            nullable: true);
        RequirePath(execution.EffectiveCwd, "execution_context.effective_cwd", nullable: true);
        RequireEnum(
            execution.EffectiveCwdUnavailableReason,
            EffectiveCwdUnavailableReasons,
            "execution_context.effective_cwd_unavailable_reason",
            nullable: true);
        RequirePath(execution.RepositoryRoot, "execution_context.repository_root", nullable: true);
        RequireText(
            execution.RepositoryRelativePath,
            4_096,
            "execution_context.repository_relative_path",
            nullable: true);
        RequireEnum(
            execution.RepositoryUnavailableReason,
            RepositoryUnavailableReasons,
            "execution_context.repository_unavailable_reason",
            nullable: true);
        if ((execution.RequestedCwd is null) != (execution.RequestedCwdUnavailableReason is not null))
            throw Invalid("execution_context.requested_cwd_unavailable_reason", "must label exactly missing requested cwd");
        if ((execution.EffectiveCwd is null) != (execution.EffectiveCwdUnavailableReason is not null))
            throw Invalid("execution_context.effective_cwd_unavailable_reason", "must label exactly missing effective cwd");
        var hasRepository = execution.RepositoryRoot is not null;
        if (hasRepository != (execution.RepositoryRelativePath is not null) ||
            hasRepository == (execution.RepositoryUnavailableReason is not null))
        {
            throw Invalid("execution_context.repository", "root/path and absence reason are inconsistent");
        }
    }

    private static void ValidateEvidenceManifest(
        IReadOnlyList<AuditEvidenceManifestEntry>? manifest)
    {
        if (manifest is null)
            return;
        if (manifest.Count is < 1 or > AuditEvidenceManifest.MaximumEntries)
            throw Invalid("evidence_manifest", "entry count is outside the bounded contract");

        var evidenceIds = new HashSet<Guid>();
        var envelopeIds = new HashSet<Guid>();
        foreach (var entry in manifest)
        {
            RequireUuid(entry.EvidenceId, 4, "evidence_manifest.evidence_id");
            if (!evidenceIds.Add(entry.EvidenceId))
                throw Invalid("evidence_manifest.evidence_id", "must be unique");
            RequireUuid(entry.EnvelopeEventId, 7, "evidence_manifest.envelope_event_id");
            if (!envelopeIds.Add(entry.EnvelopeEventId))
                throw Invalid("evidence_manifest.envelope_event_id", "must be unique");
            RequireEnum(
                entry.EvidenceKind,
                new HashSet<string>(
                    ["submitted_command", "caller_response", "captured_output"],
                    StringComparer.Ordinal),
                "evidence_manifest.evidence_kind",
                nullable: false);
            RequireLowerHex(entry.Digest, 64, "evidence_manifest.digest", nullable: false);
            RequireNonNegative(entry.ByteCount, "evidence_manifest.byte_count");
            RequireEnum(
                entry.Encoding,
                new HashSet<string>(["utf-8"], StringComparer.Ordinal),
                "evidence_manifest.encoding",
                nullable: false);
            RequireUuid(entry.ArtifactId, 4, "evidence_manifest.artifact_id");
            RequireLowerHex(
                entry.ArtifactDigest,
                64,
                "evidence_manifest.artifact_digest",
                nullable: false);
            RequireNonNegative(entry.ArtifactByteCount, "evidence_manifest.artifact_byte_count");
            RequireNonNegative(entry.ChunkIndex, "evidence_manifest.chunk_index");
            if (entry.ChunkCount < 1 || entry.ChunkCount > AuditEvidenceManifest.MaximumEntries)
                throw Invalid("evidence_manifest.chunk_count", "is outside the bounded contract");
            RequireNonNegative(entry.ChunkOffset, "evidence_manifest.chunk_offset");
            RequireEnum(
                entry.RetentionClass,
                new HashSet<string>(["forensic"], StringComparer.Ordinal),
                "evidence_manifest.retention_class",
                nullable: false);
            RequireEnum(
                entry.CaptureState,
                new HashSet<string>(["complete", "incomplete"], StringComparer.Ordinal),
                "evidence_manifest.capture_state",
                nullable: false);
        }

        foreach (var artifact in manifest.GroupBy(entry => entry.ArtifactId))
        {
            var ordered = artifact.OrderBy(entry => entry.ChunkIndex).ToArray();
            var first = ordered[0];
            if (ordered.Length != first.ChunkCount ||
                ordered.Any(entry =>
                    entry.ArtifactDigest != first.ArtifactDigest ||
                    entry.ArtifactByteCount != first.ArtifactByteCount ||
                    entry.EvidenceKind != first.EvidenceKind ||
                    entry.Encoding != first.Encoding ||
                    entry.RetentionClass != first.RetentionClass ||
                    entry.CaptureState != first.CaptureState))
            {
                throw Invalid("evidence_manifest", "artifact metadata is inconsistent");
            }
            long offset = 0;
            for (var index = 0; index < ordered.Length; index++)
            {
                if (ordered[index].ChunkIndex != index || ordered[index].ChunkOffset != offset)
                    throw Invalid("evidence_manifest", "artifact chunks are not contiguous");
                offset = checked(offset + ordered[index].ByteCount);
            }
            if (offset != first.ArtifactByteCount)
                throw Invalid("evidence_manifest", "artifact byte count does not match chunks");
        }
    }

    private static void ValidateCorrelation(AuditCorrelation value)
    {
        RequireNullableUuid(value.CallId, 7, "correlation.call_id");
        RequirePositive(value.JobId, "correlation.job_id");
        RequireNullableUuid(value.ParentEventId, 7, "correlation.parent_event_id");
        RequireLowerHex(value.TraceId, 32, "correlation.trace_id", nullable: true);
        RequireNullableUuid(value.PlanId, 4, "correlation.plan_id");
    }

    private static void ValidateRequest(string eventType, AuditRequest value)
    {
        RequireMachineName(value.Tool, "request.tool", nullable: true);
        RequireMachineName(value.Action, "request.action", nullable: true);
        RequireSessionName(value.SessionRequested, "request.session_requested", nullable: true);
        RequirePath(value.Cwd, "request.cwd", nullable: true);
        RequireEnum(
            value.DestinationKind,
            DestinationKinds,
            "request.destination_kind",
            nullable: true);
        RequirePath(value.DestinationPath, "request.destination_path", nullable: true);
        if (value.DestinationKind != "protected_file" && value.DestinationPath is not null)
        {
            throw Invalid(
                "request.destination_path",
                "must be null without a protected_file destination");
        }
        RequireNonNegative(value.TimeoutMs, "request.timeout_ms");
        RequireNullableUtc(value.DeadlineUtc, "request.deadline_utc");
        RequireEnum(value.Route, Routes, "request.route", nullable: true);
        RequirePositive(value.JobId, "request.job_id");
        RequireNonNegative(value.Offset, "request.offset");
        RequireNonNegative(value.ExpectedGeneration, "request.expected_generation");
        RequireTemplateName(value.Template, "request.template", nullable: true);
        RequireNonNegative(value.MaxBytes, "request.max_bytes");
        RequireLowerHex(value.PatternFingerprint, 64, "request.pattern_fingerprint", nullable: true);
        RequireLowerHex(value.OutputHandleDigest, 64, "request.output_handle_digest", nullable: true);
        RequireLowerHex(value.OriginalScriptDigest, 64, "request.original_script_digest", nullable: true);
        RequireNullableUuid(value.ScriptEvidenceId, 4, "request.script_evidence_id");
        ValidateEvidenceRetentionRequestFacts(eventType, value);
    }

    internal static void ValidateEvidenceRetentionRequestFacts(
        string eventType,
        AuditRequest value)
    {
        RequireNullableUuid(value.EvidenceSubjectId, 4, "request.evidence_subject_id");
        RequireLowerHex(
            value.EvidenceSubjectDigest,
            64,
            "request.evidence_subject_digest",
            nullable: true);
        RequireNonNegative(value.EvidenceSubjectBytes, "request.evidence_subject_bytes");
        RequireEnum(
            value.EvidenceSubjectState,
            EvidenceSubjectStates,
            "request.evidence_subject_state",
            nullable: true);
        RequireEnum(
            value.RetentionReason,
            EvidenceRetentionReasons,
            "request.retention_reason",
            nullable: true);

        var isRetentionEvent = EvidenceRetentionEvents.Contains(eventType);
        var hasCompleteSubject =
            value.EvidenceSubjectId is not null &&
            value.EvidenceSubjectDigest is not null &&
            value.EvidenceSubjectBytes is not null &&
            value.EvidenceSubjectState is not null &&
            value.RetentionReason is not null;
        var hasAnySubject =
            value.EvidenceSubjectId is not null ||
            value.EvidenceSubjectDigest is not null ||
            value.EvidenceSubjectBytes is not null ||
            value.EvidenceSubjectState is not null ||
            value.RetentionReason is not null;

        if (isRetentionEvent && !hasCompleteSubject)
        {
            throw Invalid(
                "request.evidence_subject_id",
                $"all evidence subject fields must be nonnull for {eventType}");
        }
        if (!isRetentionEvent && hasAnySubject)
        {
            throw Invalid(
                "request.evidence_subject_id",
                "evidence subject fields must be null outside evidence retention events");
        }
        if (isRetentionEvent &&
            (value.ScriptEvidenceId is not null || value.OriginalScriptDigest is not null))
        {
            throw Invalid(
                "request.script_evidence_id",
                "script evidence reference fields must be null for evidence retention events");
        }
        if (isRetentionEvent && hasCompleteSubject &&
            (string.Equals(value.EvidenceSubjectState, "temporary", StringComparison.Ordinal) !=
             string.Equals(value.RetentionReason, "crash_temporary", StringComparison.Ordinal)))
        {
            throw Invalid(
                "request.retention_reason",
                "temporary evidence requires crash_temporary and crash_temporary requires temporary evidence");
        }
    }

    private static void ValidateRouting(AuditRouting value)
    {
        RequireEnum(value.Domain, Domains, "routing.domain", nullable: true);
        RequireEnum(value.RequestedRoute, Routes, "routing.requested_route", nullable: true);
        RequireEnum(value.EffectiveRoute, EffectiveRoutes, "routing.effective_route", nullable: true);
        RequireText(value.RtkVersion, 128, "routing.rtk_version", nullable: true);
        RequireLowerHex(value.RtkBinaryDigest, 64, "routing.rtk_binary_digest", nullable: true);
        RequireEnum(value.Provenance, ProvenanceValues, "routing.provenance", nullable: true);
        RequireMachineCode(value.FallbackReason, "routing.fallback_reason", nullable: true);
    }

    private static void ValidateOperatorDisposition(AuditOperatorDispositionFacts? value)
    {
        if (value is null) return;

        RequireUuid(
            value.TargetSupervisorBootId,
            4,
            "operator_disposition.target_supervisor_boot_id");
        RequireUuid(value.TargetEventId, 7, "operator_disposition.target_event_id");
        RequireEnum(
            value.ProofKind,
            DispositionProofKinds,
            "operator_disposition.proof_kind",
            nullable: false);
        switch (value.ProofKind)
        {
            case "verified_receipt":
                RequireLowerHex(
                    value.VerifiedReceiptDigest,
                    64,
                    "operator_disposition.verified_receipt_digest",
                    nullable: false);
                if (value.AcknowledgedGapReason is not null)
                {
                    throw Invalid(
                        "operator_disposition.acknowledged_gap_reason",
                        "must be null for verified_receipt proof");
                }
                break;
            case "acknowledged_gap":
                if (value.VerifiedReceiptDigest is not null)
                {
                    throw Invalid(
                        "operator_disposition.verified_receipt_digest",
                        "must be null for acknowledged_gap proof");
                }
                RequireDispositionGapReason(value.AcknowledgedGapReason);
                break;
        }

        if (value.DispositionId is null)
        {
            if (value.TargetSpoolFile is not null ||
                value.TargetStartOffset is not null ||
                value.TargetNextOffset is not null ||
                value.TargetSequence is not null ||
                value.FailureClass is not null ||
                value.DetailCode is not null ||
                value.ResponseDigest is not null ||
                value.FirstFailureUtc is not null ||
                value.TargetExportConfigurationIdentity is not null)
            {
                throw Invalid(
                    "operator_disposition",
                    "an unresolved request may contain only target boot/event and proof");
            }
            return;
        }

        RequireUuid(value.DispositionId.Value, 4, "operator_disposition.disposition_id");
        if (value.TargetSpoolFile is null ||
            !AuditSpoolSegmentIdentity.TryParse(value.TargetSpoolFile, out var spool) ||
            spool.SupervisorBootId != value.TargetSupervisorBootId)
        {
            throw Invalid(
                "operator_disposition.target_spool_file",
                "must be a canonical target-boot spool file");
        }
        RequireNonNegative(
            value.TargetStartOffset,
            "operator_disposition.target_start_offset");
        RequirePositive(value.TargetNextOffset, "operator_disposition.target_next_offset");
        RequirePositive(value.TargetSequence, "operator_disposition.target_sequence");
        if (value.TargetStartOffset is null ||
            value.TargetNextOffset is null ||
            value.TargetSequence is null ||
            value.TargetNextOffset <= value.TargetStartOffset)
        {
            throw Invalid(
                "operator_disposition.target_next_offset",
                "must be greater than target_start_offset");
        }
        RequireEnum(
            value.FailureClass,
            DispositionFailureClasses,
            "operator_disposition.failure_class",
            nullable: false);
        RequireMachineCode(
            value.DetailCode,
            "operator_disposition.detail_code",
            nullable: false);
        RequireLowerHex(
            value.ResponseDigest,
            64,
            "operator_disposition.response_digest",
            nullable: true);
        if (value.FirstFailureUtc is null)
        {
            throw Invalid(
                "operator_disposition.first_failure_utc",
                "must be nonnull for an authorized disposition");
        }
        RequireUtc(
            value.FirstFailureUtc.Value,
            "operator_disposition.first_failure_utc");
        RequireLowerHex(
            value.TargetExportConfigurationIdentity,
            64,
            "operator_disposition.target_export_configuration_identity",
            nullable: false);
    }

    private static void RequireDispositionGapReason(string? value)
    {
        const string field = "operator_disposition.acknowledged_gap_reason";
        RequireMachineCode(value, field, nullable: false);
        if (value is null) return;
        var previousWasSeparator = false;
        foreach (var character in value)
        {
            var separator = character is '_' or '.' or '-';
            if (separator && previousWasSeparator)
                throw Invalid(field, "must not contain consecutive separators");
            previousWasSeparator = separator;
        }
        if (previousWasSeparator)
            throw Invalid(field, "must end with a lowercase letter or digit");
    }

    private static void ValidateOutcome(AuditOutcome value)
    {
        RequireMachineCode(value.State, "outcome.state", nullable: true);
        RequireMachineCode(value.DetailCode, "outcome.detail_code", nullable: true);
        RequireNonNegative(value.DurationMs, "outcome.duration_ms");
        RequireNonNegative(value.QueueMs, "outcome.queue_ms");
        RequireNonNegative(value.BytesReturned, "outcome.bytes_returned");
        RequireNonNegative(value.NextOffset, "outcome.next_offset");
        RequireEnum(
            value.TerminationCertainty,
            TerminationCertainties,
            "outcome.termination_certainty",
            nullable: true);
    }

    private static void ValidateCoverage(AuditCoverage value)
    {
        RequireEnum(
            value.RootProcessObserved,
            RootCoverageValues,
            "coverage.root_process_observed",
            nullable: false);
        RequireEnum(
            value.DescendantsObserved,
            OtherCoverageValues,
            "coverage.descendants_observed",
            nullable: false);
        RequireEnum(
            value.RemoteEffectObserved,
            OtherCoverageValues,
            "coverage.remote_effect_observed",
            nullable: false);
    }

    private static void ValidateAudit(AuditEventHealth value)
    {
        RequireEnum(value.ProtectionMode, ProtectionModes, "audit.protection_mode", nullable: false);
        RequireLowerHex(
            value.ExportConfigurationIdentity,
            64,
            "audit.export_configuration_identity",
            nullable: true);
        RequireEnum(value.HealthState, HealthStates, "audit.health_state", nullable: true);
        RequireMachineCode(value.FailureClass, "audit.failure_class", nullable: true);
        RequireNullableUtc(value.DegradedSinceUtc, "audit.degraded_since_utc");
        RequireNonNegative(value.EmergencyProbeCount, "audit.emergency_probe_count");
        RequireNullableUtc(value.EmergencyProbeFirstUtc, "audit.emergency_probe_first_utc");
        RequireNullableUtc(value.EmergencyProbeLastUtc, "audit.emergency_probe_last_utc");

        if (value.ProtectionMode == "local-only" && value.ExportConfigurationIdentity is not null)
        {
            throw Invalid(
                "audit.export_configuration_identity",
                "must be null in local-only protection mode");
        }
        if (value.ProtectionMode == "anchored" && value.ExportConfigurationIdentity is null)
        {
            throw Invalid(
                "audit.export_configuration_identity",
                "must be nonnull in anchored protection mode");
        }

        switch (value.HealthState)
        {
            case null:
            case "healthy":
                RequireNoRecoveryDetail(value, value.HealthState ?? "null");
                break;
            case "degraded":
                if (value.FailureClass is null || value.DegradedSinceUtc is null)
                    throw Invalid("audit", "degraded health requires failure_class and degraded_since_utc");
                if (value.EmergencyProbeCount is not null ||
                    value.EmergencyProbeFirstUtc is not null ||
                    value.EmergencyProbeLastUtc is not null)
                {
                    throw Invalid("audit", "degraded health cannot carry a recovery probe summary");
                }
                break;
            case "recovered":
                if (value.FailureClass is null || value.DegradedSinceUtc is null || value.EmergencyProbeCount is null)
                {
                    throw Invalid(
                        "audit",
                        "recovered health requires failure_class, degraded_since_utc, and emergency_probe_count");
                }
                var hasFirst = value.EmergencyProbeFirstUtc is not null;
                var hasLast = value.EmergencyProbeLastUtc is not null;
                if (value.EmergencyProbeCount == 0 && (hasFirst || hasLast))
                    throw Invalid("audit", "a zero probe count requires null first and last probe timestamps");
                if (value.EmergencyProbeCount > 0 && (!hasFirst || !hasLast))
                    throw Invalid("audit", "a positive probe count requires nonnull first and last probe timestamps");
                break;
        }
    }

    private static void RequireNoRecoveryDetail(AuditEventHealth value, string state)
    {
        if (value.FailureClass is not null ||
            value.DegradedSinceUtc is not null ||
            value.EmergencyProbeCount is not null ||
            value.EmergencyProbeFirstUtc is not null ||
            value.EmergencyProbeLastUtc is not null)
        {
            throw Invalid("audit", $"{state} health cannot carry degraded/recovery detail");
        }
    }

    private static string[] NormalizeProvidedFields(IReadOnlyList<string> values)
    {
        if (values is null)
            throw Invalid("request.provided_fields", "must be an array, not null");
        var normalized = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (normalized.Length > 64)
            throw Invalid("request.provided_fields", "may contain at most 64 unique values");
        foreach (var value in normalized)
            RequirePropertyName(value, "request.provided_fields[]");
        return normalized;
    }

    private static string[] NormalizePermittedFallbacks(IReadOnlyList<string> values)
    {
        if (values is null)
            throw Invalid("routing.permitted_fallbacks", "must be an array, not null");
        var normalized = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (normalized.Length > 2)
            throw Invalid("routing.permitted_fallbacks", "may contain at most 2 unique values");
        foreach (var value in normalized)
            RequireEnum(value, PermittedFallbackValues, "routing.permitted_fallbacks[]", nullable: false);
        return normalized;
    }

    private static void WritePreHashEvent(
        Utf8JsonWriter writer,
        long sequence,
        string? previousEventHash,
        AuditProducerContext producer,
        AuditEventInput input,
        Guid eventId,
        DateTimeOffset occurredUtc,
        DateTimeOffset observedUtc,
        string[] providedFields,
        string[] permittedFallbacks)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "schema_version",
            input.EvidenceManifest is not null
                ? FullEvidenceSchemaVersion
                : input.CallAttribution is null
                    ? CurrentSchemaVersion
                    : CallContextSchemaVersion);
        writer.WriteString("event_id", FormatUuid(eventId));
        writer.WriteString("event_type", input.EventType);
        writer.WriteString("occurred_utc", FormatTimestamp(occurredUtc));
        writer.WriteString("observed_utc", FormatTimestamp(observedUtc));

        writer.WriteStartObject("producer");
        writer.WriteString("host_id", FormatUuid(producer.HostId));
        writer.WriteString("supervisor_boot_id", FormatUuid(producer.SupervisorBootId));
        WriteUuid(writer, "previous_supervisor_boot_id", producer.PreviousSupervisorBootId);
        WriteUuid(writer, "worker_boot_id", producer.WorkerBootId);
        WriteNumber(writer, "pid", producer.Pid);
        writer.WriteString("version", producer.Version);
        WriteString(writer, "binary_digest", producer.BinaryDigest);
        writer.WriteEndObject();

        writer.WriteNumber("sequence", sequence);
        WriteString(writer, "previous_event_hash", previousEventHash);

        writer.WriteStartObject("session");
        WriteString(writer, "name", input.Session.Name);
        WriteNumber(writer, "generation", input.Session.Generation);
        WriteString(writer, "binding_kind", input.Session.BindingKind);
        WriteString(writer, "template_name", input.Session.TemplateName);
        WriteString(writer, "template_digest", input.Session.TemplateDigest);
        WriteString(writer, "bootstrap_digest", input.Session.BootstrapDigest);
        WriteString(writer, "declared_purpose", input.Session.DeclaredPurpose);
        WriteString(writer, "declared_target", input.Session.DeclaredTarget);
        WriteString(writer, "declared_identity", input.Session.DeclaredIdentity);
        WriteString(writer, "effective_identity", input.Session.EffectiveIdentity);
        WriteBoolean(writer, "allow_cold_background", input.Session.AllowColdBackground);
        writer.WriteEndObject();

        writer.WriteStartObject("actor");
        WriteString(writer, "transport", input.Actor.Transport);
        WriteString(writer, "client_name", input.Actor.ClientName);
        WriteString(writer, "client_version", input.Actor.ClientVersion);
        WriteString(writer, "client_session_id", input.Actor.ClientSessionId);
        writer.WriteString("attribution_strength", input.Actor.AttributionStrength);
        writer.WriteEndObject();

        if (input.CallAttribution is { } attribution)
        {
            writer.WriteStartObject("call_attribution");
            WriteString(writer, "agent_name", attribution.AgentName);
            WriteString(writer, "agent_unavailable_reason", attribution.AgentUnavailableReason);
            WriteString(writer, "model_provider", attribution.ModelProvider);
            WriteString(writer, "model_name", attribution.ModelName);
            WriteString(writer, "model_unavailable_reason", attribution.ModelUnavailableReason);
            WriteString(writer, "source", attribution.Source);
            writer.WriteString("strength", attribution.Strength);
            writer.WriteEndObject();

            var clientContext = input.ClientCallContext!;
            writer.WriteStartObject("client_context");
            WriteString(writer, "task_id", clientContext.TaskId);
            WriteString(writer, "task_name", clientContext.TaskName);
            WriteNumber(writer, "mcp_task_ttl_ms", clientContext.McpTaskTtlMs);
            WriteString(writer, "task_unavailable_reason", clientContext.TaskUnavailableReason);
            WriteString(writer, "run_id", clientContext.RunId);
            WriteString(writer, "run_unavailable_reason", clientContext.RunUnavailableReason);
            WriteString(writer, "source", clientContext.Source);
            writer.WriteString("strength", clientContext.Strength);
            writer.WriteEndObject();

            var executionContext = input.ExecutionContext!;
            writer.WriteStartObject("execution_context");
            WriteString(writer, "requested_cwd", executionContext.RequestedCwd);
            WriteString(
                writer,
                "requested_cwd_unavailable_reason",
                executionContext.RequestedCwdUnavailableReason);
            WriteString(writer, "effective_cwd", executionContext.EffectiveCwd);
            WriteString(
                writer,
                "effective_cwd_unavailable_reason",
                executionContext.EffectiveCwdUnavailableReason);
            WriteString(writer, "repository_root", executionContext.RepositoryRoot);
            WriteString(
                writer,
                "repository_relative_path",
                executionContext.RepositoryRelativePath);
            WriteString(
                writer,
                "repository_unavailable_reason",
                executionContext.RepositoryUnavailableReason);
            writer.WriteEndObject();
        }

        if (input.EvidenceManifest is { } manifest)
        {
            writer.WriteStartArray("evidence_manifest");
            foreach (var evidence in manifest)
            {
                writer.WriteStartObject();
                writer.WriteString("evidence_id", FormatUuid(evidence.EvidenceId));
                writer.WriteString("envelope_event_id", FormatUuid(evidence.EnvelopeEventId));
                writer.WriteString("evidence_kind", evidence.EvidenceKind);
                writer.WriteString("digest", evidence.Digest);
                writer.WriteNumber("byte_count", evidence.ByteCount);
                writer.WriteString("encoding", evidence.Encoding);
                writer.WriteString("artifact_id", FormatUuid(evidence.ArtifactId));
                writer.WriteString("artifact_digest", evidence.ArtifactDigest);
                writer.WriteNumber("artifact_byte_count", evidence.ArtifactByteCount);
                writer.WriteNumber("chunk_index", evidence.ChunkIndex);
                writer.WriteNumber("chunk_count", evidence.ChunkCount);
                writer.WriteNumber("chunk_offset", evidence.ChunkOffset);
                writer.WriteString("retention_class", evidence.RetentionClass);
                writer.WriteString("capture_state", evidence.CaptureState);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteStartObject("correlation");
        WriteUuid(writer, "call_id", input.Correlation.CallId);
        WriteNumber(writer, "job_id", input.Correlation.JobId);
        WriteUuid(writer, "parent_event_id", input.Correlation.ParentEventId);
        WriteString(writer, "trace_id", input.Correlation.TraceId);
        WriteUuid(writer, "plan_id", input.Correlation.PlanId);
        writer.WriteEndObject();

        writer.WriteStartObject("request");
        WriteString(writer, "tool", input.Request.Tool);
        WriteString(writer, "action", input.Request.Action);
        writer.WriteStartArray("provided_fields");
        foreach (var field in providedFields)
            writer.WriteStringValue(field);
        writer.WriteEndArray();
        WriteString(writer, "session_requested", input.Request.SessionRequested);
        WriteString(writer, "cwd", input.Request.Cwd);
        WriteString(writer, "destination_kind", input.Request.DestinationKind);
        WriteString(writer, "destination_path", input.Request.DestinationPath);
        WriteNumber(writer, "timeout_ms", input.Request.TimeoutMs);
        WriteTimestamp(writer, "deadline_utc", input.Request.DeadlineUtc);
        WriteString(writer, "route", input.Request.Route);
        WriteBoolean(writer, "background", input.Request.Background);
        WriteBoolean(writer, "raw", input.Request.Raw);
        WriteBoolean(writer, "list_available", input.Request.ListAvailable);
        WriteNumber(writer, "job_id", input.Request.JobId);
        WriteNumber(writer, "offset", input.Request.Offset);
        WriteNumber(writer, "expected_generation", input.Request.ExpectedGeneration);
        WriteBoolean(writer, "force", input.Request.Force);
        WriteString(writer, "template", input.Request.Template);
        WriteBoolean(writer, "allow_cold_background", input.Request.AllowColdBackground);
        WriteNumber(writer, "max_bytes", input.Request.MaxBytes);
        WriteString(writer, "pattern_fingerprint", input.Request.PatternFingerprint);
        WriteString(writer, "output_handle_digest", input.Request.OutputHandleDigest);
        WriteString(writer, "original_script_digest", input.Request.OriginalScriptDigest);
        WriteUuid(writer, "script_evidence_id", input.Request.ScriptEvidenceId);
        WriteUuid(writer, "evidence_subject_id", input.Request.EvidenceSubjectId);
        WriteString(writer, "evidence_subject_digest", input.Request.EvidenceSubjectDigest);
        WriteNumber(writer, "evidence_subject_bytes", input.Request.EvidenceSubjectBytes);
        WriteString(writer, "evidence_subject_state", input.Request.EvidenceSubjectState);
        WriteString(writer, "retention_reason", input.Request.RetentionReason);
        writer.WriteEndObject();

        if (input.OperatorDisposition is not { } disposition)
        {
            writer.WriteNull("operator_disposition");
        }
        else
        {
            writer.WriteStartObject("operator_disposition");
            WriteUuid(writer, "disposition_id", disposition.DispositionId);
            writer.WriteString(
                "target_supervisor_boot_id",
                FormatUuid(disposition.TargetSupervisorBootId));
            WriteString(writer, "target_spool_file", disposition.TargetSpoolFile);
            WriteNumber(writer, "target_start_offset", disposition.TargetStartOffset);
            WriteNumber(writer, "target_next_offset", disposition.TargetNextOffset);
            WriteNumber(writer, "target_sequence", disposition.TargetSequence);
            writer.WriteString("target_event_id", FormatUuid(disposition.TargetEventId));
            WriteString(writer, "failure_class", disposition.FailureClass);
            WriteString(writer, "detail_code", disposition.DetailCode);
            WriteString(writer, "response_digest", disposition.ResponseDigest);
            WriteTimestamp(writer, "first_failure_utc", disposition.FirstFailureUtc);
            WriteString(
                writer,
                "target_export_configuration_identity",
                disposition.TargetExportConfigurationIdentity);
            writer.WriteString("proof_kind", disposition.ProofKind);
            WriteString(
                writer,
                "verified_receipt_digest",
                disposition.VerifiedReceiptDigest);
            WriteString(
                writer,
                "acknowledged_gap_reason",
                disposition.AcknowledgedGapReason);
            writer.WriteEndObject();
        }

        writer.WriteStartObject("routing");
        WriteString(writer, "domain", input.Routing.Domain);
        WriteString(writer, "requested_route", input.Routing.RequestedRoute);
        WriteString(writer, "effective_route", input.Routing.EffectiveRoute);
        writer.WriteStartArray("permitted_fallbacks");
        foreach (var fallback in permittedFallbacks)
            writer.WriteStringValue(fallback);
        writer.WriteEndArray();
        WriteString(writer, "rtk_version", input.Routing.RtkVersion);
        WriteString(writer, "rtk_binary_digest", input.Routing.RtkBinaryDigest);
        WriteString(writer, "provenance", input.Routing.Provenance);
        WriteString(writer, "fallback_reason", input.Routing.FallbackReason);
        writer.WriteEndObject();

        writer.WriteStartObject("outcome");
        WriteString(writer, "state", input.Outcome.State);
        WriteString(writer, "detail_code", input.Outcome.DetailCode);
        WriteNumber(writer, "exit_code", input.Outcome.ExitCode);
        WriteNumber(writer, "duration_ms", input.Outcome.DurationMs);
        WriteNumber(writer, "queue_ms", input.Outcome.QueueMs);
        WriteNumber(writer, "bytes_returned", input.Outcome.BytesReturned);
        WriteNumber(writer, "next_offset", input.Outcome.NextOffset);
        WriteBoolean(writer, "warm_state_lost", input.Outcome.WarmStateLost);
        WriteBoolean(writer, "worker_replaced", input.Outcome.WorkerReplaced);
        WriteString(writer, "termination_certainty", input.Outcome.TerminationCertainty);
        writer.WriteEndObject();

        writer.WriteStartObject("coverage");
        writer.WriteBoolean("ptk_request", input.Coverage.PtkRequest);
        writer.WriteString("root_process_observed", input.Coverage.RootProcessObserved);
        writer.WriteString("descendants_observed", input.Coverage.DescendantsObserved);
        writer.WriteString("remote_effect_observed", input.Coverage.RemoteEffectObserved);
        writer.WriteEndObject();

        writer.WriteStartObject("audit");
        writer.WriteString("protection_mode", input.Audit.ProtectionMode);
        WriteString(writer, "export_configuration_identity", input.Audit.ExportConfigurationIdentity);
        WriteString(writer, "health_state", input.Audit.HealthState);
        WriteString(writer, "failure_class", input.Audit.FailureClass);
        WriteTimestamp(writer, "degraded_since_utc", input.Audit.DegradedSinceUtc);
        WriteNumber(writer, "emergency_probe_count", input.Audit.EmergencyProbeCount);
        WriteTimestamp(writer, "emergency_probe_first_utc", input.Audit.EmergencyProbeFirstUtc);
        WriteTimestamp(writer, "emergency_probe_last_utc", input.Audit.EmergencyProbeLastUtc);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static void WriteUuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, FormatUuid(value.Value));
    }

    private static void WriteTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, FormatTimestamp(value.Value));
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteNumber(name, value.Value);
    }

    private static void WriteBoolean(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteBoolean(name, value.Value);
    }

    private static string FormatUuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static void RequireUuid(Guid value, int version, string field)
    {
        var text = FormatUuid(value);
        if (text[14] != (char)('0' + version) || text[19] is not ('8' or '9' or 'a' or 'b'))
            throw Invalid(field, $"must be an RFC 4122 UUIDv{version}");
    }

    private static void RequireNullableUuid(Guid? value, int version, string field)
    {
        if (value is not null)
            RequireUuid(value.Value, version, field);
    }

    private static void RequireUtc(DateTimeOffset value, string field)
    {
        if (value.Offset != TimeSpan.Zero)
            throw Invalid(field, "must have UTC offset zero");
    }

    private static void RequireNullableUtc(DateTimeOffset? value, string field)
    {
        if (value is not null)
            RequireUtc(value.Value, field);
    }

    private static void RequirePositive(long? value, string field)
    {
        if (value is not null && value < 1)
            throw Invalid(field, "must be in the range 1..Int64.MaxValue");
    }

    private static void RequireNonNegative(long? value, string field)
    {
        if (value is not null && value < 0)
            throw Invalid(field, "must be in the range 0..Int64.MaxValue");
    }

    private static void RequireText(string? value, int maximumScalars, string field, bool nullable)
    {
        if (value is null)
        {
            if (!nullable)
                throw Invalid(field, "must be nonnull");
            return;
        }

        var count = ScalarCount(value, field);
        if (count < 1 || count > maximumScalars)
            throw Invalid(field, $"must contain 1..{maximumScalars} Unicode scalar values");
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
                throw Invalid(field, "must not contain a Unicode Cc scalar");
        }
    }

    private static void RequirePath(string? value, string field, bool nullable)
    {
        if (value is null)
        {
            if (!nullable)
                throw Invalid(field, "must be nonnull");
            return;
        }

        var count = ScalarCount(value, field);
        if (count < 1 || count > 4096)
            throw Invalid(field, "must contain 1..4096 Unicode scalar values");
        if (value.Contains('\0'))
            throw Invalid(field, "must not contain NUL");
    }

    private static int ScalarCount(string value, string field)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw Invalid(field, "must contain valid Unicode scalar values");
        }
        return value.EnumerateRunes().Count();
    }

    private static void RequireMachineName(string? value, string field, bool nullable)
    {
        if (!RequireAsciiPattern(value, 64, field, nullable, IsAsciiLower, IsMachineRest))
            return;
    }

    private static void RequireMachineCode(string? value, string field, bool nullable)
    {
        if (!RequireAsciiPattern(value, 128, field, nullable, IsAsciiLower, IsMachineRest))
            return;
    }

    private static void RequireEventCode(string value, string field)
    {
        RequireMachineCode(value, field, nullable: false);
        if (!value.Contains('.', StringComparison.Ordinal))
            throw Invalid(field, "must contain at least one dot");
    }

    private static void RequirePropertyName(string value, string field)
    {
        RequireAsciiPattern(value, 64, field, nullable: false, IsAsciiLetter, IsPropertyRest);
    }

    private static void RequireSessionName(string? value, string field, bool nullable)
    {
        RequireAsciiPattern(value, 64, field, nullable, IsAsciiLowerOrDigit, IsMachineRest);
    }

    private static void RequireTemplateName(string? value, string field, bool nullable)
    {
        if (!RequireAsciiPattern(value, 64, field, nullable, IsAsciiLowerOrDigit, IsMachineRest))
            return;
        if (value == "default")
            throw Invalid(field, "the reserved session name 'default' is not a template name");
    }

    private static bool RequireAsciiPattern(
        string? value,
        int maximumLength,
        string field,
        bool nullable,
        Func<char, bool> first,
        Func<char, bool> rest)
    {
        if (value is null)
        {
            if (!nullable)
                throw Invalid(field, "must be nonnull");
            return false;
        }
        if (value.Length < 1 || value.Length > maximumLength || !first(value[0]))
            throw Invalid(field, "does not match the required ASCII pattern");
        for (var index = 1; index < value.Length; index++)
        {
            if (!rest(value[index]))
                throw Invalid(field, "does not match the required ASCII pattern");
        }
        return true;
    }

    private static void RequireLowerHex(string? value, int length, string field, bool nullable)
    {
        if (value is null)
        {
            if (!nullable)
                throw Invalid(field, "must be nonnull");
            return;
        }
        if (value.Length != length || value.Any(c => !IsLowerHex(c)))
            throw Invalid(field, $"must be exactly {length} lowercase hexadecimal characters");
    }

    private static void RequireEnum(
        string? value,
        HashSet<string> allowed,
        string field,
        bool nullable)
    {
        if (value is null)
        {
            if (!nullable)
                throw Invalid(field, "must be nonnull");
            return;
        }
        if (!allowed.Contains(value))
            throw Invalid(field, $"has unrecognized value '{value}'");
    }

    private static bool IsAsciiLower(char value) => value is >= 'a' and <= 'z';
    private static bool IsAsciiUpper(char value) => value is >= 'A' and <= 'Z';
    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
    private static bool IsAsciiLetter(char value) => IsAsciiLower(value) || IsAsciiUpper(value);
    private static bool IsAsciiLowerOrDigit(char value) => IsAsciiLower(value) || IsAsciiDigit(value);
    private static bool IsMachineRest(char value) =>
        IsAsciiLower(value) || IsAsciiDigit(value) || value is '_' or '.' or '-';
    private static bool IsPropertyRest(char value) =>
        IsAsciiLetter(value) || IsAsciiDigit(value) || value == '_';
    private static bool IsLowerHex(char value) =>
        IsAsciiDigit(value) || value is >= 'a' and <= 'f';

    private static AuditEventValidationException Invalid(string field, string detail) =>
        new($"Invalid audit field '{field}': {detail}.");

    private readonly record struct NormalizedArrays(
        string[] ProvidedFields,
        string[] PermittedFallbacks);
}
