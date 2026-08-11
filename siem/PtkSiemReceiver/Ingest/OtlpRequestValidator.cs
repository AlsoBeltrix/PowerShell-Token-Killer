using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using PtkMcpServer.Audit.OtlpWire;

namespace PtkSiemReceiver.Ingest;

internal sealed record ValidatedOtlpRecord(
    byte[] RawRequestBytes,
    byte[] ExactJsonBody,
    string SchemaVersion,
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredUtc,
    DateTimeOffset ObservedUtc,
    Guid HostId,
    string EventHash,
    string? PreviousEventHash,
    Guid SupervisorBootId,
    Guid? WorkerBootId,
    long Sequence,
    string? SessionName,
    long? SessionGeneration,
    string? CallId,
    long? JobId,
    string? OutcomeState);

internal sealed record RejectedOtlpAttempt(
    byte[] RawRequestBytes,
    byte[]? ExactJsonBody,
    string FailureCode,
    string? ClaimedEventId,
    string? ClaimedEventHash,
    string? ClaimedPreviousEventHash,
    string? ClaimedSupervisorBootId,
    long? ClaimedSequence);

internal sealed record OtlpValidationResult(
    ValidatedOtlpRecord? Record,
    RejectedOtlpAttempt? RejectedAttempt,
    string? FailureCode)
{
    internal bool IsValid => Record is not null;

    internal static OtlpValidationResult Valid(ValidatedOtlpRecord record) =>
        new(record, null, null);

    internal static OtlpValidationResult Invalid(
        ReadOnlySpan<byte> requestBytes,
        string failureCode) =>
        new(null, DescribeRejectedAttempt(requestBytes, failureCode), failureCode);

    internal static OtlpValidationResult Invalid(RejectedOtlpAttempt attempt) =>
        new(null, attempt, attempt.FailureCode);

    private static RejectedOtlpAttempt DescribeRejectedAttempt(
        ReadOnlySpan<byte> requestBytes,
        string failureCode)
    {
        byte[]? exactBody = null;
        string? eventId = null;
        string? eventHash = null;
        string? previousEventHash = null;
        string? supervisorBootId = null;
        long? sequence = null;

        try
        {
            var request = ExportLogsServiceRequest.Parser.ParseFrom(requestBytes.ToArray());
            var body = request.ResourceLogs.Count == 1 &&
                       request.ResourceLogs[0].ScopeLogs.Count == 1 &&
                       request.ResourceLogs[0].ScopeLogs[0].LogRecords.Count == 1
                ? request.ResourceLogs[0].ScopeLogs[0].LogRecords[0].Body
                : null;
            if (body?.ValueCase == AnyValue.ValueOneofCase.StringValue)
            {
                exactBody = Encoding.UTF8.GetBytes(body.StringValue);
                using var document = JsonDocument.Parse(exactBody, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = document.RootElement;
                    eventId = ClaimString(root, "event_id");
                    eventHash = ClaimString(root, "event_hash");
                    previousEventHash = ClaimString(root, "previous_event_hash");
                    sequence = ClaimInt64(root, "sequence");
                    if (root.TryGetProperty("producer", out var producer) &&
                        producer.ValueKind == JsonValueKind.Object)
                    {
                        supervisorBootId = ClaimString(producer, "supervisor_boot_id");
                    }
                }
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Claims are advisory only. The exact bounded request remains the evidence.
        }

        return new RejectedOtlpAttempt(
            requestBytes.ToArray(),
            exactBody,
            failureCode,
            eventId,
            eventHash,
            previousEventHash,
            supervisorBootId,
            sequence);
    }

    private static string? ClaimString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ClaimInt64(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var result)
            ? result
            : null;

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

/// <summary>
/// Per-record outcomes for one OTLP JSON export request. A request-level
/// failure means the envelope itself was unusable and nothing was read from
/// it; otherwise every log record has exactly one result, in order.
/// </summary>
internal sealed record OtlpJsonIngestValidation(
    IReadOnlyList<OtlpValidationResult> Results,
    string? RequestFailureCode)
{
    internal static OtlpJsonIngestValidation RequestInvalid(string failureCode) =>
        new([], failureCode);
}

internal static class OtlpRequestValidator
{
    private const string V1 = "ptk.audit/1";
    private const string V2 = "ptk.audit/2";
    private const string V3 = "ptk.audit/3";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const string EventHashMarker = ",\"event_hash\":\"";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> V1RootProperties = new(
        [
            "schema_version", "event_id", "event_type", "occurred_utc",
            "observed_utc", "producer", "sequence", "previous_event_hash",
            "session", "actor", "correlation", "request", "routing",
            "outcome", "coverage", "audit", "event_hash",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> V2RootProperties = new(
        [
            "schema_version", "event_id", "event_type", "occurred_utc",
            "observed_utc", "producer", "sequence", "previous_event_hash",
            "session", "actor", "correlation", "request", "operator_disposition",
            "routing", "outcome", "coverage", "audit", "event_hash",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> V3RootProperties = new(
        V2RootProperties.Append("host"),
        StringComparer.Ordinal);

    private static readonly HashSet<string> AllowedHostStates = new(
        [
            "absent", "starting", "ready", "recovering", "backoff",
            "containment_unconfirmed", "circuit_open", "half_open", "stopped",
        ],
        StringComparer.Ordinal);

    internal static OtlpValidationResult Validate(byte[] requestBytes)
    {
        try
        {
            if (requestBytes.Length == 0) Fail("protobuf");
            ExportLogsServiceRequest request;
            try
            {
                request = ExportLogsServiceRequest.Parser.ParseFrom(requestBytes);
            }
            catch (InvalidProtocolBufferException)
            {
                return OtlpValidationResult.Invalid(requestBytes, "protobuf");
            }

            if (request.ResourceLogs.Count != 1) Fail("record_count");
            var resourceLogs = request.ResourceLogs[0];
            if (resourceLogs.Resource is null ||
                resourceLogs.ScopeLogs.Count != 1 ||
                resourceLogs.SchemaUrl.Length != 0)
            {
                Fail("otlp_shape");
            }

            var scopeLogs = resourceLogs.ScopeLogs[0];
            if (scopeLogs.Scope is null ||
                scopeLogs.LogRecords.Count != 1 ||
                scopeLogs.SchemaUrl.Length != 0 ||
                scopeLogs.Scope.Attributes.Count != 0 ||
                scopeLogs.Scope.DroppedAttributesCount != 0)
            {
                Fail("otlp_shape");
            }

            var log = scopeLogs.LogRecords[0];
            if (log.Body?.ValueCase != AnyValue.ValueOneofCase.StringValue ||
                log.DroppedAttributesCount != 0 ||
                log.SeverityNumber != 0 ||
                log.SeverityText.Length != 0 ||
                log.Flags != 0 ||
                log.SpanId.Length != 0)
            {
                Fail("log_shape");
            }

            var exactBody = StrictUtf8.GetBytes(log.Body.StringValue);
            using var body = ValidateBody(exactBody);
            var root = body.Root;
            var schemaVersion = body.SchemaVersion;
            var eventIdText = body.EventIdText;
            var eventId = body.EventId;
            var eventType = body.EventType;
            var occurred = body.Occurred;
            var observed = body.Observed;
            var sequence = body.Sequence;
            var previousHash = body.PreviousHash;
            var eventHash = body.EventHash;
            var hostId = body.HostId;
            var hostGuid = body.HostGuid;
            var supervisorBootText = body.SupervisorBootText;
            var supervisorBootId = body.SupervisorBootId;
            var workerBootId = body.WorkerBootId;
            var workerBootGuid = body.WorkerBootGuid;
            var producerVersion = body.ProducerVersion;
            var session = body.Session;
            var correlation = body.Correlation;
            var auditRequest = body.AuditRequest;
            var outcome = body.Outcome;

            var attributes = ReadAttributes(log.Attributes);
            var expectedAttributes = new Dictionary<string, ExpectedValue>(StringComparer.Ordinal)
            {
                ["ptk.audit.schema_version"] = ExpectedValue.String(schemaVersion),
                ["ptk.audit.event_id"] = ExpectedValue.String(eventIdText),
                ["ptk.audit.event_type"] = ExpectedValue.String(eventType),
                ["ptk.audit.sequence"] = ExpectedValue.Integer(sequence),
                ["ptk.audit.event_hash"] = ExpectedValue.String(eventHash),
                ["ptk.supervisor.boot_id"] = ExpectedValue.String(supervisorBootText),
            };
            AddOptional(expectedAttributes, "ptk.audit.previous_event_hash", previousHash);
            AddOptional(expectedAttributes, "ptk.worker.boot_id", workerBootId);
            AddOptional(expectedAttributes, "ptk.session.name", OptionalString(session, "name", "session"));
            AddOptional(expectedAttributes, "ptk.session.generation", OptionalInt64(session, "generation", "session"));
            AddOptional(expectedAttributes, "ptk.call.id", OptionalString(correlation, "call_id", "correlation"));
            AddOptional(expectedAttributes, "ptk.job.id", OptionalInt64(correlation, "job_id", "correlation"));
            AddOptional(expectedAttributes, "ptk.outcome.state", OptionalString(outcome, "state", "outcome"));
            AddOptional(
                expectedAttributes,
                "ptk.termination.certainty",
                OptionalString(outcome, "termination_certainty", "outcome"));

            if (schemaVersion is V2 or V3)
            {
                AddOptional(
                    expectedAttributes,
                    "ptk.evidence.subject.id",
                    OptionalString(auditRequest, "evidence_subject_id", "request"));
                AddOptional(
                    expectedAttributes,
                    "ptk.evidence.subject.digest",
                    OptionalString(auditRequest, "evidence_subject_digest", "request"));
                AddOptional(
                    expectedAttributes,
                    "ptk.evidence.subject.bytes",
                    OptionalInt64(auditRequest, "evidence_subject_bytes", "request"));
                AddOptional(
                    expectedAttributes,
                    "ptk.evidence.subject.state",
                    OptionalString(auditRequest, "evidence_subject_state", "request"));
                AddOptional(
                    expectedAttributes,
                    "ptk.evidence.retention.reason",
                    OptionalString(auditRequest, "retention_reason", "request"));
                AddDispositionAttributes(root, expectedAttributes);
            }

            if (schemaVersion == V3)
                AddHostAttributes(root, expectedAttributes);

            if (!AttributesMatch(attributes, expectedAttributes))
                Fail("attributes");

            ValidateResource(resourceLogs.Resource, hostId, supervisorBootText, producerVersion);
            if (!string.Equals(scopeLogs.Scope.Name, "PtkMcpServer.Audit", StringComparison.Ordinal) ||
                !string.Equals(scopeLogs.Scope.Version, producerVersion, StringComparison.Ordinal))
            {
                Fail("scope");
            }

            if (log.TimeUnixNano != ToUnixNanoseconds(occurred) ||
                log.ObservedTimeUnixNano != ToUnixNanoseconds(observed) ||
                !string.Equals(log.EventName, $"ptk.audit.{eventType}", StringComparison.Ordinal))
            {
                Fail("log_projection");
            }

            var traceId = OptionalString(correlation, "trace_id", "correlation");
            if (traceId is null)
            {
                if (log.TraceId.Length != 0) Fail("trace_id");
            }
            else if (!IsLowerHex(traceId, 32) ||
                     traceId.All(character => character == '0') ||
                     !log.TraceId.Span.SequenceEqual(Convert.FromHexString(traceId)))
            {
                Fail("trace_id");
            }

            return OtlpValidationResult.Valid(new ValidatedOtlpRecord(
                requestBytes.ToArray(),
                exactBody,
                schemaVersion,
                eventId,
                eventType,
                occurred,
                observed,
                hostGuid,
                eventHash,
                previousHash,
                supervisorBootId,
                workerBootGuid,
                sequence,
                OptionalString(session, "name", "session"),
                OptionalInt64(session, "generation", "session"),
                OptionalString(correlation, "call_id", "correlation"),
                OptionalInt64(correlation, "job_id", "correlation"),
                OptionalString(outcome, "state", "outcome")));
        }
        catch (OtlpValidationException exception)
        {
            return OtlpValidationResult.Invalid(requestBytes, exception.FailureCode);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return OtlpValidationResult.Invalid(requestBytes, "invalid_record");
        }
    }

    /// <summary>
    /// The audit-record body validation shared by both wire encodings: exact
    /// per-version root shape, canonical identifiers, and the recomputed
    /// event hash. The record line is the custody evidence; every path that
    /// accepts one must run this exact code (audit-restoration R3c).
    /// </summary>
    private static ParsedBody ValidateBody(byte[] exactBody)
    {
        var document = JsonDocument.Parse(
            exactBody,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        try
        {
            var root = RequireObject(document.RootElement, "body_shape");
            EnsureNoDuplicateProperties(root);

            var schemaVersion = RequireString(root, "schema_version", "schema_version");
            var expectedRoot = schemaVersion switch
            {
                V1 => V1RootProperties,
                V2 => V2RootProperties,
                V3 => V3RootProperties,
                _ => throw new OtlpValidationException("schema_version"),
            };
            RequireExactProperties(root, expectedRoot, "body_shape");

            var eventIdText = RequireString(root, "event_id", "event_id");
            var eventId = RequireCanonicalUuid(eventIdText, 7, "event_id");
            var eventType = RequireNonemptyString(root, "event_type", "event_type");
            var occurred = RequireUtc(root, "occurred_utc", "occurred_utc");
            var observed = RequireUtc(root, "observed_utc", "observed_utc");
            var sequence = RequireInt64(root, "sequence", "sequence");
            if (sequence < 1) Fail("sequence");
            var previousHash = OptionalString(root, "previous_event_hash", "previous_event_hash");
            if (sequence == 1 ? previousHash is not null : !IsLowerHex(previousHash, 64))
                Fail("previous_event_hash");
            var eventHash = RequireString(root, "event_hash", "event_hash");
            if (!IsLowerHex(eventHash, 64) || !HasValidEventHash(exactBody, eventHash))
                Fail("event_hash");

            var producer = RequireObjectProperty(root, "producer", "producer");
            var hostId = RequireString(producer, "host_id", "producer");
            var hostGuid = RequireCanonicalUuid(hostId, 4, "producer");
            var supervisorBootText = RequireString(
                producer,
                "supervisor_boot_id",
                "producer");
            var supervisorBootId = RequireCanonicalUuid(
                supervisorBootText,
                4,
                "producer");
            var workerBootId = OptionalString(producer, "worker_boot_id", "producer");
            Guid? workerBootGuid = null;
            if (workerBootId is not null)
                workerBootGuid = RequireCanonicalUuid(workerBootId, 4, "producer");
            // Boot lineage (R3d): absent on pre-lineage records, nullable on
            // current ones — and never garbage when present.
            if (producer.TryGetProperty("previous_supervisor_boot_id", out var previousBoot) &&
                previousBoot.ValueKind != JsonValueKind.Null)
            {
                if (previousBoot.ValueKind != JsonValueKind.String) Fail("producer");
                _ = RequireCanonicalUuid(previousBoot.GetString()!, 4, "producer");
            }
            var producerVersion = RequireNonemptyString(producer, "version", "producer");

            return new ParsedBody
            {
                Document = document,
                Root = root,
                SchemaVersion = schemaVersion,
                EventIdText = eventIdText,
                EventId = eventId,
                EventType = eventType,
                Occurred = occurred,
                Observed = observed,
                Sequence = sequence,
                PreviousHash = previousHash,
                EventHash = eventHash,
                HostId = hostId,
                HostGuid = hostGuid,
                SupervisorBootText = supervisorBootText,
                SupervisorBootId = supervisorBootId,
                WorkerBootId = workerBootId,
                WorkerBootGuid = workerBootGuid,
                ProducerVersion = producerVersion,
                Session = RequireObjectProperty(root, "session", "session"),
                Correlation = RequireObjectProperty(root, "correlation", "correlation"),
                AuditRequest = RequireObjectProperty(root, "request", "request"),
                Outcome = RequireObjectProperty(root, "outcome", "outcome"),
            };
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private sealed class ParsedBody : IDisposable
    {
        internal required JsonDocument Document { get; init; }
        internal required JsonElement Root { get; init; }
        internal required string SchemaVersion { get; init; }
        internal required string EventIdText { get; init; }
        internal required Guid EventId { get; init; }
        internal required string EventType { get; init; }
        internal required DateTimeOffset Occurred { get; init; }
        internal required DateTimeOffset Observed { get; init; }
        internal required long Sequence { get; init; }
        internal required string? PreviousHash { get; init; }
        internal required string EventHash { get; init; }
        internal required string HostId { get; init; }
        internal required Guid HostGuid { get; init; }
        internal required string SupervisorBootText { get; init; }
        internal required Guid SupervisorBootId { get; init; }
        internal required string? WorkerBootId { get; init; }
        internal required Guid? WorkerBootGuid { get; init; }
        internal required string ProducerVersion { get; init; }
        internal required JsonElement Session { get; init; }
        internal required JsonElement Correlation { get; init; }
        internal required JsonElement AuditRequest { get; init; }
        internal required JsonElement Outcome { get; init; }

        public void Dispose() => Document.Dispose();
    }

    /// <summary>Bound on log records in one JSON export request. The request
    /// byte bound is the real limit; this caps degenerate envelopes.</summary>
    internal const int MaximumJsonLogRecords = 4096;

    /// <summary>
    /// Validates an OTLP/HTTP JSON export request (audit-restoration R3c) —
    /// the generic-collector encoding PTK's own exporter emits identically
    /// for Splunk-style collectors, OTLP endpoints, and this receiver. The
    /// ENVELOPE is transport and read leniently (standard proto3 JSON, empty
    /// arrays omitted, unknown decorations ignored); each record BODY is the
    /// custody evidence and passes the exact validation the protobuf path
    /// runs, and the two indexing hints PTK writes are cross-checked so a
    /// decoration can never contradict the evidence it decorates. Batches
    /// are per-record results: one poison record refuses one record.
    /// </summary>
    internal static OtlpJsonIngestValidation ValidateJsonRequest(byte[] requestBytes)
    {
        try
        {
            if (requestBytes.Length == 0)
                return OtlpJsonIngestValidation.RequestInvalid("otlp_json");
            JsonDocument envelope;
            try
            {
                envelope = JsonDocument.Parse(requestBytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            }
            catch (JsonException)
            {
                return OtlpJsonIngestValidation.RequestInvalid("otlp_json");
            }

            using (envelope)
            {
                var root = envelope.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("resourceLogs", out var resourceLogs) ||
                    resourceLogs.ValueKind != JsonValueKind.Array)
                {
                    return OtlpJsonIngestValidation.RequestInvalid("otlp_shape");
                }

                var logRecords = new List<JsonElement>();
                foreach (var resourceEntry in resourceLogs.EnumerateArray())
                {
                    if (resourceEntry.ValueKind != JsonValueKind.Object)
                        return OtlpJsonIngestValidation.RequestInvalid("otlp_shape");
                    // Proto3 JSON omits empty repeated fields, so an absent
                    // array is an empty one, not a malformed request.
                    if (!resourceEntry.TryGetProperty("scopeLogs", out var scopeLogs))
                        continue;
                    if (scopeLogs.ValueKind != JsonValueKind.Array)
                        return OtlpJsonIngestValidation.RequestInvalid("otlp_shape");
                    foreach (var scopeEntry in scopeLogs.EnumerateArray())
                    {
                        if (scopeEntry.ValueKind != JsonValueKind.Object)
                            return OtlpJsonIngestValidation.RequestInvalid("otlp_shape");
                        if (!scopeEntry.TryGetProperty("logRecords", out var entries))
                            continue;
                        if (entries.ValueKind != JsonValueKind.Array)
                            return OtlpJsonIngestValidation.RequestInvalid("otlp_shape");
                        foreach (var entry in entries.EnumerateArray())
                        {
                            if (logRecords.Count == MaximumJsonLogRecords)
                                return OtlpJsonIngestValidation.RequestInvalid("record_count");
                            logRecords.Add(entry);
                        }
                    }
                }

                var results = new List<OtlpValidationResult>(logRecords.Count);
                foreach (var logRecord in logRecords)
                    results.Add(ValidateJsonLogRecord(logRecord));
                return new OtlpJsonIngestValidation(results, null);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return OtlpJsonIngestValidation.RequestInvalid("invalid_record");
        }
    }

    private static OtlpValidationResult ValidateJsonLogRecord(JsonElement logRecord)
    {
        // The per-record raw evidence is the exact log-record JSON, not the
        // whole request: the producer regroups batches across retries, so
        // request-level bytes would make an honest replay look like "same
        // event, different bytes" (quarantine), and a 256-record batch would
        // store its envelope 256 times.
        var rawRecordBytes = Encoding.UTF8.GetBytes(logRecord.GetRawText());
        byte[]? exactBody = null;
        try
        {
            if (logRecord.ValueKind != JsonValueKind.Object ||
                !logRecord.TryGetProperty("body", out var bodyElement) ||
                bodyElement.ValueKind != JsonValueKind.Object ||
                !bodyElement.TryGetProperty("stringValue", out var stringValue) ||
                stringValue.ValueKind != JsonValueKind.String)
            {
                throw new OtlpValidationException("log_shape");
            }

            exactBody = StrictUtf8.GetBytes(stringValue.GetString()!);
            using var body = ValidateBody(exactBody);
            RequireTruthfulJsonHints(logRecord, body.EventType, body.EventIdText);

            return OtlpValidationResult.Valid(new ValidatedOtlpRecord(
                rawRecordBytes,
                exactBody,
                body.SchemaVersion,
                body.EventId,
                body.EventType,
                body.Occurred,
                body.Observed,
                body.HostGuid,
                body.EventHash,
                body.PreviousHash,
                body.SupervisorBootId,
                body.WorkerBootGuid,
                body.Sequence,
                OptionalString(body.Session, "name", "session"),
                OptionalInt64(body.Session, "generation", "session"),
                OptionalString(body.Correlation, "call_id", "correlation"),
                OptionalInt64(body.Correlation, "job_id", "correlation"),
                OptionalString(body.Outcome, "state", "outcome")));
        }
        catch (OtlpValidationException exception)
        {
            return OtlpValidationResult.Invalid(
                DescribeRejectedJsonRecord(rawRecordBytes, exactBody, exception.FailureCode));
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return OtlpValidationResult.Invalid(
                DescribeRejectedJsonRecord(rawRecordBytes, exactBody, "invalid_record"));
        }
    }

    /// <summary>
    /// The two indexing hints PTK's exporter writes (<c>ptk.event_type</c>,
    /// <c>ptk.event_id</c>) must match the validated record when present.
    /// Other attribute keys are transport decoration and carry no claim.
    /// </summary>
    private static void RequireTruthfulJsonHints(
        JsonElement logRecord,
        string eventType,
        string eventIdText)
    {
        if (!logRecord.TryGetProperty("attributes", out var attributes)) return;
        if (attributes.ValueKind != JsonValueKind.Array) Fail("attributes");
        foreach (var attribute in attributes.EnumerateArray())
        {
            if (attribute.ValueKind != JsonValueKind.Object ||
                !attribute.TryGetProperty("key", out var keyElement) ||
                keyElement.ValueKind != JsonValueKind.String)
            {
                Fail("attributes");
                continue; // unreachable; Fail throws
            }

            var key = keyElement.GetString();
            var expected = key switch
            {
                "ptk.event_type" => eventType,
                "ptk.event_id" => eventIdText,
                _ => null,
            };
            if (expected is null) continue;
            if (!attribute.TryGetProperty("value", out var valueElement) ||
                valueElement.ValueKind != JsonValueKind.Object ||
                !valueElement.TryGetProperty("stringValue", out var hintValue) ||
                hintValue.ValueKind != JsonValueKind.String ||
                !string.Equals(hintValue.GetString(), expected, StringComparison.Ordinal))
            {
                Fail("attributes");
            }
        }
    }

    private static RejectedOtlpAttempt DescribeRejectedJsonRecord(
        byte[] rawRecordBytes,
        byte[]? exactBody,
        string failureCode)
    {
        string? eventId = null;
        string? eventHash = null;
        string? previousEventHash = null;
        string? supervisorBootId = null;
        long? sequence = null;
        try
        {
            if (exactBody is not null)
            {
                using var document = JsonDocument.Parse(exactBody, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = document.RootElement;
                    eventId = ClaimString(root, "event_id");
                    eventHash = ClaimString(root, "event_hash");
                    previousEventHash = ClaimString(root, "previous_event_hash");
                    sequence = ClaimInt64(root, "sequence");
                    if (root.TryGetProperty("producer", out var producer) &&
                        producer.ValueKind == JsonValueKind.Object)
                    {
                        supervisorBootId = ClaimString(producer, "supervisor_boot_id");
                    }
                }
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Claims are advisory only; the exact log record stays evidence.
        }

        return new RejectedOtlpAttempt(
            rawRecordBytes,
            exactBody,
            failureCode,
            eventId,
            eventHash,
            previousEventHash,
            supervisorBootId,
            sequence);
    }

    private static string? ClaimString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ClaimInt64(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var result)
            ? result
            : null;

    private static void AddHostAttributes(
        JsonElement root,
        IDictionary<string, ExpectedValue> expected)
    {
        var host = RequireObjectProperty(root, "host", "host");
        RequireExactProperties(
            host,
            new HashSet<string>(["boot_id", "generation", "state", "recovery_attempt"], StringComparer.Ordinal),
            "host");
        var bootId = OptionalString(host, "boot_id", "host");
        var generation = OptionalInt64(host, "generation", "host");
        if (bootId is null != generation is null) Fail("host_identity");
        if (bootId is not null) _ = RequireCanonicalUuid(bootId, 4, "host_identity");
        if (generation is <= 0) Fail("host_identity");
        var state = RequireString(host, "state", "host_state");
        if (!AllowedHostStates.Contains(state)) Fail("host_state");
        var attempt = RequireInt64(host, "recovery_attempt", "host_recovery_attempt");
        if (attempt < 0) Fail("host_recovery_attempt");

        AddOptional(expected, "ptk.host.boot_id", bootId);
        AddOptional(expected, "ptk.host.generation", generation);
        expected["ptk.host.state"] = ExpectedValue.String(state);
        expected["ptk.host.recovery_attempt"] = ExpectedValue.Integer(attempt);
    }

    private static void AddDispositionAttributes(
        JsonElement root,
        IDictionary<string, ExpectedValue> expected)
    {
        var element = root.GetProperty("operator_disposition");
        if (element.ValueKind == JsonValueKind.Null) return;
        var disposition = RequireObject(element, "operator_disposition");
        AddOptional(expected, "ptk.disposition.id", OptionalString(disposition, "disposition_id", "operator_disposition"));
        AddOptional(expected, "ptk.disposition.target.boot_id", OptionalString(disposition, "target_supervisor_boot_id", "operator_disposition"));
        AddOptional(expected, "ptk.disposition.target.event_id", OptionalString(disposition, "target_event_id", "operator_disposition"));
        AddOptional(expected, "ptk.disposition.proof_kind", OptionalString(disposition, "proof_kind", "operator_disposition"));
        AddOptional(expected, "ptk.disposition.failure_class", OptionalString(disposition, "failure_class", "operator_disposition"));
        AddOptional(expected, "ptk.disposition.target.export_configuration_identity", OptionalString(disposition, "target_export_configuration_identity", "operator_disposition"));
        AddOptional(expected, "ptk.disposition.verified_receipt_digest", OptionalString(disposition, "verified_receipt_digest", "operator_disposition"));
        AddOptional(expected, "ptk.disposition.acknowledged_gap_reason", OptionalString(disposition, "acknowledged_gap_reason", "operator_disposition"));
    }

    private static void ValidateResource(
        OtlpResource resource,
        string hostId,
        string supervisorBootId,
        string producerVersion)
    {
        if (resource.DroppedAttributesCount != 0) Fail("resource");
        var observed = ReadAttributes(resource.Attributes);
        var expected = new Dictionary<string, ExpectedValue>(StringComparer.Ordinal)
        {
            ["service.namespace"] = ExpectedValue.String("ptk"),
            ["service.name"] = ExpectedValue.String("powershell-token-killer"),
            ["service.version"] = ExpectedValue.String(producerVersion),
            ["service.instance.id"] = ExpectedValue.String(supervisorBootId),
            ["host.id"] = ExpectedValue.String(hostId),
        };
        if (!AttributesMatch(observed, expected)) Fail("resource");
    }

    private static Dictionary<string, ExpectedValue> ReadAttributes(
        IEnumerable<KeyValue> attributes)
    {
        var result = new Dictionary<string, ExpectedValue>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            if (attribute.Key.Length == 0 || attribute.Value is null)
                Fail("attributes");
            var value = attribute.Value.ValueCase switch
            {
                AnyValue.ValueOneofCase.StringValue => ExpectedValue.String(attribute.Value.StringValue),
                AnyValue.ValueOneofCase.IntValue => ExpectedValue.Integer(attribute.Value.IntValue),
                _ => throw new OtlpValidationException("attributes"),
            };
            if (!result.TryAdd(attribute.Key, value)) Fail("attributes");
        }
        return result;
    }

    private static bool AttributesMatch(
        IReadOnlyDictionary<string, ExpectedValue> observed,
        IReadOnlyDictionary<string, ExpectedValue> expected) =>
        observed.Count == expected.Count &&
        expected.All(pair => observed.TryGetValue(pair.Key, out var actual) && actual == pair.Value);

    private static void AddOptional(
        IDictionary<string, ExpectedValue> attributes,
        string key,
        string? value)
    {
        if (value is not null) attributes[key] = ExpectedValue.String(value);
    }

    private static void AddOptional(
        IDictionary<string, ExpectedValue> attributes,
        string key,
        long? value)
    {
        if (value.HasValue) attributes[key] = ExpectedValue.Integer(value.Value);
    }

    private static bool HasValidEventHash(ReadOnlySpan<byte> exactBody, string expectedHash)
    {
        var body = StrictUtf8.GetString(exactBody);
        var marker = body.LastIndexOf(EventHashMarker, StringComparison.Ordinal);
        if (marker < 1 || body[^1] != '}' ||
            marker + EventHashMarker.Length + 66 != body.Length)
        {
            return false;
        }
        var preHash = body[..marker] + '}';
        var actual = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(preHash)))
            .ToLowerInvariant();
        return string.Equals(actual, expectedHash, StringComparison.Ordinal);
    }

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) Fail("duplicate_property");
                EnsureNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) EnsureNoDuplicateProperties(item);
        }
    }

    private static void RequireExactProperties(
        JsonElement element,
        IReadOnlySet<string> expected,
        string failureCode)
    {
        var observed = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!observed.SetEquals(expected)) Fail(failureCode);
    }

    private static JsonElement RequireObjectProperty(
        JsonElement parent,
        string propertyName,
        string failureCode) =>
        RequireObject(parent.GetProperty(propertyName), failureCode);

    private static JsonElement RequireObject(JsonElement element, string failureCode)
    {
        if (element.ValueKind != JsonValueKind.Object) Fail(failureCode);
        return element;
    }

    private static string RequireString(
        JsonElement parent,
        string propertyName,
        string failureCode)
    {
        if (!parent.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            Fail(failureCode);
        }
        return element.GetString()!;
    }

    private static string RequireNonemptyString(
        JsonElement parent,
        string propertyName,
        string failureCode)
    {
        var value = RequireString(parent, propertyName, failureCode);
        if (value.Length == 0) Fail(failureCode);
        return value;
    }

    private static string? OptionalString(
        JsonElement parent,
        string propertyName,
        string failureCode)
    {
        if (!parent.TryGetProperty(propertyName, out var element)) Fail(failureCode);
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) Fail(failureCode);
        return element.GetString();
    }

    private static long RequireInt64(
        JsonElement parent,
        string propertyName,
        string failureCode)
    {
        long value = 0;
        if (!parent.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt64(out value))
        {
            Fail(failureCode);
        }
        return value;
    }

    private static long? OptionalInt64(
        JsonElement parent,
        string propertyName,
        string failureCode)
    {
        if (!parent.TryGetProperty(propertyName, out var element)) Fail(failureCode);
        if (element.ValueKind == JsonValueKind.Null) return null;
        long value = 0;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out value))
            Fail(failureCode);
        return value;
    }

    private static Guid RequireCanonicalUuid(string value, int version, string failureCode)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal) ||
            value[14] != (char)('0' + version) ||
            value[19] is not ('8' or '9' or 'a' or 'b'))
        {
            Fail(failureCode);
        }
        return parsed;
    }

    private static DateTimeOffset RequireUtc(
        JsonElement parent,
        string propertyName,
        string failureCode)
    {
        var text = RequireString(parent, propertyName, failureCode);
        if (!DateTimeOffset.TryParseExact(
                text,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value) ||
            !string.Equals(
                text,
                value.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            Fail(failureCode);
        }
        return value;
    }

    private static ulong ToUnixNanoseconds(DateTimeOffset value) =>
        checked((ulong)(value.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) * 100UL);

    private static bool IsLowerHex(string? value, int length) =>
        value is { Length: var actualLength } &&
        actualLength == length &&
        value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    [DoesNotReturn]
    private static void Fail(string failureCode) =>
        throw new OtlpValidationException(failureCode);

    private readonly record struct ExpectedValue(string? StringValue, long? IntValue)
    {
        internal static ExpectedValue String(string value) => new(value, null);

        internal static ExpectedValue Integer(long value) => new(null, value);
    }

    private sealed class OtlpValidationException : Exception
    {
        internal OtlpValidationException(string failureCode)
        {
            FailureCode = failureCode;
        }

        internal string FailureCode { get; }
    }
}
