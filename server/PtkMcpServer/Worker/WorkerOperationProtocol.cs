using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PtkMcpServer.Worker;

internal enum WorkerInvokeRoute
{
    Auto,
    Pwsh,
    Rtk,
}

internal enum WorkerResultStatus
{
    Completed,
    Refused,
    Canceled,
    TimedOut,
    Failed,
}

internal sealed record WorkerProtocolLimits(
    int MaximumScriptBytes,
    long MaximumArtifactBytes,
    int MaximumArtifactChunkBytes,
    int DefaultTimeoutSeconds,
    int MaximumTimeoutSeconds);

internal sealed record WorkerInitializeRequest(
    Guid SessionId,
    long Incarnation,
    long RequestId,
    DateTimeOffset DeadlineUtc,
    WorkerProtocolLimits Limits);

internal abstract record WorkerOperationRequest(
    long RequestId,
    int TimeoutSeconds,
    TimeSpan Timeout);

internal sealed record WorkerArtifactRequest(
    Guid ArtifactId,
    long MaximumBytes);

internal sealed record WorkerInvokeRequest(
    long RequestId,
    int TimeoutSeconds,
    TimeSpan Timeout,
    string Script,
    bool Raw,
    WorkerInvokeRoute Route,
    WorkerArtifactRequest? Artifact) :
    WorkerOperationRequest(RequestId, TimeoutSeconds, Timeout);

internal sealed record WorkerStateQueryRequest(
    long RequestId,
    TimeSpan Timeout,
    bool ListAvailable) :
    WorkerOperationRequest(RequestId, 0, Timeout);

internal sealed record WorkerOperationCancel(long RequestId);

internal abstract record WorkerExecutionResult;

internal sealed record WorkerArtifactPayload(
    Guid ArtifactId,
    ReadOnlyMemory<byte> Bytes);

internal sealed record WorkerInvokeExecutionResult(
    WorkerResultStatus Status,
    string Text,
    string? DetailCode = null,
    WorkerArtifactPayload? Artifact = null) : WorkerExecutionResult;

internal sealed record WorkerStateExecutionResult(
    bool Available,
    string Text,
    string? DetailCode = null) : WorkerExecutionResult;

internal sealed record WorkerResult(
    long RequestId,
    WorkerResultStatus Status,
    string Text,
    string? DetailCode);

internal sealed record WorkerStateSnapshot(
    long RequestId,
    bool Available,
    string Text,
    string? DetailCode);

internal sealed record WorkerArtifactChunk(
    long RequestId,
    Guid ArtifactId,
    long Offset,
    byte[] Bytes);

internal sealed record WorkerArtifactSeal(
    long RequestId,
    Guid ArtifactId,
    long Length,
    string Sha256);

/// <summary>
/// Strict payload codec for the minimal worker protocol. The outer envelope
/// owns framing and duplicate detection; this type freezes each closed payload
/// shape and binds every post-initialize frame to one session incarnation.
/// </summary>
internal static class WorkerOperationProtocol
{
    internal const int MaximumScriptBytes = 128 * 1024;
    internal const long MaximumArtifactBytes = 8 * 1024 * 1024;
    internal const int MaximumArtifactChunkBytes = 64 * 1024;
    internal const int MaximumLogicalTextBytes = 128 * 1024;
    internal const int MaximumTimeoutSeconds = 24 * 60 * 60;
    internal const int MaximumCodeLength = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static WorkerProtocolLimits CreateLimits(
        TimeSpan defaultTimeout,
        TimeSpan maximumTimeout)
    {
        var defaultSeconds = WholePositiveSeconds(defaultTimeout, nameof(defaultTimeout));
        var maximumSeconds = WholePositiveSeconds(maximumTimeout, nameof(maximumTimeout));
        if (defaultSeconds > maximumSeconds)
            throw new ArgumentOutOfRangeException(nameof(defaultTimeout));
        return ValidateLimits(new WorkerProtocolLimits(
            MaximumScriptBytes,
            MaximumArtifactBytes,
            MaximumArtifactChunkBytes,
            defaultSeconds,
            maximumSeconds));
    }

    internal static WorkerEnvelope CreateInitializeEnvelope(
        Guid sessionId,
        long incarnation,
        long requestId,
        DateTimeOffset deadlineUtc,
        WorkerProtocolLimits limits)
    {
        limits = ValidateLimits(limits);
        return Envelope(
            WorkerMessageKind.Initialize,
            sessionId,
            incarnation,
            requestId,
            JsonSerializer.SerializeToElement(new
            {
                deadlineUnixTimeMilliseconds = deadlineUtc.ToUnixTimeMilliseconds(),
                maximumScriptBytes = limits.MaximumScriptBytes,
                maximumArtifactBytes = limits.MaximumArtifactBytes,
                maximumArtifactChunkBytes = limits.MaximumArtifactChunkBytes,
                defaultTimeoutSeconds = limits.DefaultTimeoutSeconds,
                maximumTimeoutSeconds = limits.MaximumTimeoutSeconds,
            }));
    }

    internal static WorkerInitializeRequest ParseInitialize(WorkerEnvelope envelope)
    {
        ValidateEnvelope(envelope, WorkerMessageKind.Initialize);
        var fields = ClosedObject(
            envelope.Payload,
            "deadlineUnixTimeMilliseconds",
            "maximumScriptBytes",
            "maximumArtifactBytes",
            "maximumArtifactChunkBytes",
            "defaultTimeoutSeconds",
            "maximumTimeoutSeconds");
        var deadlineMilliseconds = PositiveInt64(
            Required(fields, "deadlineUnixTimeMilliseconds"),
            "deadlineUnixTimeMilliseconds");
        DateTimeOffset deadlineUtc;
        try
        {
            deadlineUtc = DateTimeOffset.FromUnixTimeMilliseconds(deadlineMilliseconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw InvalidField("deadlineUnixTimeMilliseconds", exception);
        }

        var limits = ValidateLimits(new WorkerProtocolLimits(
            PositiveInt32(Required(fields, "maximumScriptBytes"), "maximumScriptBytes"),
            PositiveInt64(Required(fields, "maximumArtifactBytes"), "maximumArtifactBytes"),
            PositiveInt32(
                Required(fields, "maximumArtifactChunkBytes"),
                "maximumArtifactChunkBytes"),
            PositiveInt32(
                Required(fields, "defaultTimeoutSeconds"),
                "defaultTimeoutSeconds"),
            PositiveInt32(
                Required(fields, "maximumTimeoutSeconds"),
                "maximumTimeoutSeconds")));
        return new WorkerInitializeRequest(
            envelope.SessionId,
            envelope.Incarnation,
            envelope.RequestId!.Value,
            deadlineUtc,
            limits);
    }

    internal static WorkerEnvelope CreateReadyEnvelope(WorkerInitializeRequest initialize)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        var limits = ValidateLimits(initialize.Limits);
        return Envelope(
            WorkerMessageKind.Ready,
            initialize.SessionId,
            initialize.Incarnation,
            initialize.RequestId,
            SerializeLimits(limits));
    }

    internal static WorkerProtocolLimits ParseReady(
        WorkerEnvelope envelope,
        Guid expectedSessionId,
        long expectedIncarnation,
        long expectedRequestId)
    {
        ValidateEnvelope(
            envelope,
            WorkerMessageKind.Ready,
            expectedSessionId,
            expectedIncarnation,
            expectedRequestId);
        return ParseLimits(envelope.Payload);
    }

    internal static WorkerEnvelope CreateInvokeEnvelope(
        Guid sessionId,
        long incarnation,
        long requestId,
        string script,
        bool raw,
        WorkerInvokeRoute route,
        int timeoutSeconds,
        WorkerArtifactRequest? artifact,
        WorkerProtocolLimits limits)
    {
        limits = ValidateLimits(limits);
        script = LogicalText(script, "script", limits.MaximumScriptBytes);
        _ = ParseTimeout(timeoutSeconds, limits);
        ValidateArtifactRequest(artifact, limits);
        return Envelope(
            WorkerMessageKind.Invoke,
            sessionId,
            incarnation,
            requestId,
            JsonSerializer.SerializeToElement(new
            {
                script,
                raw,
                route = RouteName(route),
                timeoutSeconds,
                artifact = artifact is null
                    ? null
                    : new
                    {
                        artifactId = artifact.ArtifactId.ToString("D"),
                        maximumBytes = artifact.MaximumBytes,
                    },
            }));
    }

    internal static WorkerInvokeRequest ParseInvoke(
        WorkerEnvelope envelope,
        Guid expectedSessionId,
        long expectedIncarnation,
        WorkerProtocolLimits limits)
    {
        limits = ValidateLimits(limits);
        ValidateEnvelope(
            envelope,
            WorkerMessageKind.Invoke,
            expectedSessionId,
            expectedIncarnation);
        var fields = ClosedObject(
            envelope.Payload,
            "script",
            "raw",
            "route",
            "timeoutSeconds",
            "artifact");
        var timeoutSeconds = NonnegativeInt32(
            Required(fields, "timeoutSeconds"),
            "timeoutSeconds");
        var timeout = ParseTimeout(timeoutSeconds, limits);
        var artifactValue = Required(fields, "artifact");
        var artifact = artifactValue.ValueKind == JsonValueKind.Null
            ? null
            : ParseArtifactRequest(artifactValue, limits);
        return new WorkerInvokeRequest(
            envelope.RequestId!.Value,
            timeoutSeconds,
            timeout,
            LogicalText(
                StringField(Required(fields, "script"), "script"),
                "script",
                limits.MaximumScriptBytes),
            BooleanField(Required(fields, "raw"), "raw"),
            ParseRoute(Required(fields, "route")),
            artifact);
    }

    internal static WorkerEnvelope CreateStateQueryEnvelope(
        Guid sessionId,
        long incarnation,
        long requestId,
        bool listAvailable)
        => Envelope(
            WorkerMessageKind.StateQuery,
            sessionId,
            incarnation,
            requestId,
            JsonSerializer.SerializeToElement(new { listAvailable }));

    internal static WorkerStateQueryRequest ParseStateQuery(
        WorkerEnvelope envelope,
        Guid expectedSessionId,
        long expectedIncarnation,
        WorkerProtocolLimits limits)
    {
        limits = ValidateLimits(limits);
        ValidateEnvelope(
            envelope,
            WorkerMessageKind.StateQuery,
            expectedSessionId,
            expectedIncarnation);
        var fields = ClosedObject(envelope.Payload, "listAvailable");
        return new WorkerStateQueryRequest(
            envelope.RequestId!.Value,
            TimeSpan.FromSeconds(limits.DefaultTimeoutSeconds),
            BooleanField(Required(fields, "listAvailable"), "listAvailable"));
    }

    internal static WorkerEnvelope CreateCancelEnvelope(
        Guid sessionId,
        long incarnation,
        long requestId)
        => Envelope(
            WorkerMessageKind.Cancel,
            sessionId,
            incarnation,
            requestId,
            JsonSerializer.SerializeToElement(new { }));

    internal static WorkerOperationCancel ParseCancel(
        WorkerEnvelope envelope,
        Guid expectedSessionId,
        long expectedIncarnation)
    {
        ValidateEnvelope(
            envelope,
            WorkerMessageKind.Cancel,
            expectedSessionId,
            expectedIncarnation);
        RequireEmptyPayload(envelope.Payload, WorkerMessageKind.Cancel);
        return new WorkerOperationCancel(envelope.RequestId!.Value);
    }

    internal static WorkerEnvelope CreateResultEnvelope(
        Guid sessionId,
        long incarnation,
        WorkerResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateResult(result.Status, result.Text, result.DetailCode);
        return Envelope(
            WorkerMessageKind.Result,
            sessionId,
            incarnation,
            result.RequestId,
            JsonSerializer.SerializeToElement(new
            {
                status = ResultStatusName(result.Status),
                text = LogicalText(
                    result.Text,
                    "text",
                    MaximumLogicalTextBytes),
                detailCode = result.DetailCode,
            }));
    }

    internal static WorkerResult ParseResult(
        WorkerEnvelope envelope,
        Guid expectedSessionId,
        long expectedIncarnation)
    {
        ValidateEnvelope(
            envelope,
            WorkerMessageKind.Result,
            expectedSessionId,
            expectedIncarnation);
        var fields = ClosedObject(envelope.Payload, "status", "text", "detailCode");
        var status = ParseResultStatus(Required(fields, "status"));
        var text = LogicalText(
            StringField(Required(fields, "text"), "text"),
            "text",
            MaximumLogicalTextBytes);
        var detailValue = Required(fields, "detailCode");
        var detailCode = detailValue.ValueKind == JsonValueKind.Null
            ? null
            : Code(detailValue, "detailCode");
        ValidateResult(status, text, detailCode);
        return new WorkerResult(envelope.RequestId!.Value, status, text, detailCode);
    }

    internal static WorkerEnvelope CreateStateSnapshotEnvelope(
        Guid sessionId,
        long incarnation,
        WorkerStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateStateSnapshot(snapshot.Available, snapshot.Text, snapshot.DetailCode);
        return Envelope(
            WorkerMessageKind.StateSnapshot,
            sessionId,
            incarnation,
            snapshot.RequestId,
            JsonSerializer.SerializeToElement(new
            {
                available = snapshot.Available,
                text = LogicalText(
                    snapshot.Text,
                    "text",
                    MaximumLogicalTextBytes),
                detailCode = snapshot.DetailCode,
            }));
    }

    internal static WorkerStateSnapshot ParseStateSnapshot(
        WorkerEnvelope envelope,
        Guid expectedSessionId,
        long expectedIncarnation)
    {
        ValidateEnvelope(
            envelope,
            WorkerMessageKind.StateSnapshot,
            expectedSessionId,
            expectedIncarnation);
        var fields = ClosedObject(envelope.Payload, "available", "text", "detailCode");
        var available = BooleanField(Required(fields, "available"), "available");
        var text = LogicalText(
            StringField(Required(fields, "text"), "text"),
            "text",
            MaximumLogicalTextBytes);
        var detailValue = Required(fields, "detailCode");
        var detailCode = detailValue.ValueKind == JsonValueKind.Null
            ? null
            : Code(detailValue, "detailCode");
        ValidateStateSnapshot(available, text, detailCode);
        return new WorkerStateSnapshot(
            envelope.RequestId!.Value,
            available,
            text,
            detailCode);
    }

    internal static WorkerEnvelope CreateArtifactChunkEnvelope(
        Guid sessionId,
        long incarnation,
        WorkerArtifactChunk chunk,
        WorkerProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        limits = ValidateLimits(limits);
        ValidateArtifactChunk(chunk, limits);
        return Envelope(
            WorkerMessageKind.ArtifactChunk,
            sessionId,
            incarnation,
            chunk.RequestId,
            JsonSerializer.SerializeToElement(new
            {
                artifactId = chunk.ArtifactId.ToString("D"),
                offset = chunk.Offset,
                data = Convert.ToBase64String(chunk.Bytes),
            }));
    }

    internal static WorkerArtifactChunk ParseArtifactChunk(
        WorkerEnvelope envelope,
        Guid expectedSessionId,
        long expectedIncarnation,
        WorkerProtocolLimits limits)
    {
        limits = ValidateLimits(limits);
        ValidateEnvelope(
            envelope,
            WorkerMessageKind.ArtifactChunk,
            expectedSessionId,
            expectedIncarnation);
        var fields = ClosedObject(envelope.Payload, "artifactId", "offset", "data");
        var dataValue = Required(fields, "data");
        if (dataValue.ValueKind != JsonValueKind.String)
            throw InvalidField("data");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dataValue.GetString()!);
        }
        catch (FormatException exception)
        {
            throw InvalidField("data", exception);
        }
        var chunk = new WorkerArtifactChunk(
            envelope.RequestId!.Value,
            GuidField(Required(fields, "artifactId"), "artifactId"),
            NonnegativeInt64(Required(fields, "offset"), "offset"),
            bytes);
        ValidateArtifactChunk(chunk, limits);
        return chunk;
    }

    internal static WorkerEnvelope CreateArtifactSealEnvelope(
        Guid sessionId,
        long incarnation,
        WorkerArtifactSeal seal)
    {
        ArgumentNullException.ThrowIfNull(seal);
        ValidateArtifactSeal(seal);
        return Envelope(
            WorkerMessageKind.ArtifactSeal,
            sessionId,
            incarnation,
            seal.RequestId,
            JsonSerializer.SerializeToElement(new
            {
                artifactId = seal.ArtifactId.ToString("D"),
                length = seal.Length,
                sha256 = seal.Sha256,
            }));
    }

    internal static WorkerArtifactSeal ParseArtifactSeal(
        WorkerEnvelope envelope,
        Guid expectedSessionId,
        long expectedIncarnation)
    {
        ValidateEnvelope(
            envelope,
            WorkerMessageKind.ArtifactSeal,
            expectedSessionId,
            expectedIncarnation);
        var fields = ClosedObject(envelope.Payload, "artifactId", "length", "sha256");
        var seal = new WorkerArtifactSeal(
            envelope.RequestId!.Value,
            GuidField(Required(fields, "artifactId"), "artifactId"),
            NonnegativeInt64(Required(fields, "length"), "length"),
            StringField(Required(fields, "sha256"), "sha256"));
        ValidateArtifactSeal(seal);
        return seal;
    }

    internal static WorkerEnvelope CreateEmptyEnvelope(
        WorkerMessageKind kind,
        Guid sessionId,
        long incarnation,
        long requestId)
    {
        if (kind is not (WorkerMessageKind.Shutdown or WorkerMessageKind.Stopped))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return Envelope(
            kind,
            sessionId,
            incarnation,
            requestId,
            JsonSerializer.SerializeToElement(new { }));
    }

    internal static void ParseEmpty(
        WorkerEnvelope envelope,
        WorkerMessageKind kind,
        Guid expectedSessionId,
        long expectedIncarnation)
    {
        if (kind is not (WorkerMessageKind.Shutdown or WorkerMessageKind.Stopped))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ValidateEnvelope(
            envelope,
            kind,
            expectedSessionId,
            expectedIncarnation);
        RequireEmptyPayload(envelope.Payload, kind);
    }

    private static WorkerEnvelope Envelope(
        WorkerMessageKind kind,
        Guid sessionId,
        long incarnation,
        long requestId,
        JsonElement payload)
    {
        if (sessionId == Guid.Empty)
            throw InvalidField("sessionId");
        if (incarnation <= 0)
            throw InvalidField("incarnation");
        if (requestId <= 0)
            throw InvalidField("requestId");
        return new WorkerEnvelope(
            WorkerProtocol.Version,
            kind,
            sessionId,
            incarnation,
            requestId,
            payload);
    }

    private static void ValidateEnvelope(
        WorkerEnvelope envelope,
        WorkerMessageKind expectedKind,
        Guid? expectedSessionId = null,
        long? expectedIncarnation = null,
        long? expectedRequestId = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.ProtocolVersion != WorkerProtocol.Version)
            throw new WorkerProtocolException(
                "unknown_version",
                "Worker operation envelope uses an unsupported protocol version.");
        if (envelope.Kind != expectedKind)
            throw new WorkerProtocolException(
                "operation_kind_mismatch",
                $"Expected worker operation kind '{expectedKind}'.");
        if (envelope.SessionId == Guid.Empty ||
            expectedSessionId is { } sessionId && envelope.SessionId != sessionId)
        {
            throw new WorkerProtocolException(
                "session_identity_mismatch",
                "Worker operation frame targets a different session identity.");
        }
        if (envelope.Incarnation <= 0 ||
            expectedIncarnation is { } incarnation && envelope.Incarnation != incarnation)
        {
            throw new WorkerProtocolException(
                "worker_incarnation_mismatch",
                "Worker operation frame targets a stale worker incarnation.");
        }
        if (envelope.RequestId is not > 0)
            throw new WorkerProtocolException(
                "request_id_required",
                "Worker operation frame requires a positive request ID.");
        if (expectedRequestId is { } requestId && envelope.RequestId != requestId)
            throw new WorkerProtocolException(
                "request_id_mismatch",
                "Worker operation response targets a different request.");
        if (envelope.Payload.ValueKind != JsonValueKind.Object)
            throw InvalidField("payload");
    }

    private static WorkerProtocolLimits ParseLimits(JsonElement payload)
    {
        var fields = ClosedObject(
            payload,
            "maximumScriptBytes",
            "maximumArtifactBytes",
            "maximumArtifactChunkBytes",
            "defaultTimeoutSeconds",
            "maximumTimeoutSeconds");
        return ValidateLimits(new WorkerProtocolLimits(
            PositiveInt32(Required(fields, "maximumScriptBytes"), "maximumScriptBytes"),
            PositiveInt64(Required(fields, "maximumArtifactBytes"), "maximumArtifactBytes"),
            PositiveInt32(
                Required(fields, "maximumArtifactChunkBytes"),
                "maximumArtifactChunkBytes"),
            PositiveInt32(
                Required(fields, "defaultTimeoutSeconds"),
                "defaultTimeoutSeconds"),
            PositiveInt32(
                Required(fields, "maximumTimeoutSeconds"),
                "maximumTimeoutSeconds")));
    }

    private static JsonElement SerializeLimits(WorkerProtocolLimits limits) =>
        JsonSerializer.SerializeToElement(new
        {
            maximumScriptBytes = limits.MaximumScriptBytes,
            maximumArtifactBytes = limits.MaximumArtifactBytes,
            maximumArtifactChunkBytes = limits.MaximumArtifactChunkBytes,
            defaultTimeoutSeconds = limits.DefaultTimeoutSeconds,
            maximumTimeoutSeconds = limits.MaximumTimeoutSeconds,
        });

    private static WorkerProtocolLimits ValidateLimits(WorkerProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumScriptBytes is < 1 or > MaximumScriptBytes ||
            limits.MaximumArtifactBytes is < 1 or > MaximumArtifactBytes ||
            limits.MaximumArtifactChunkBytes is < 1 or > MaximumArtifactChunkBytes ||
            limits.MaximumArtifactChunkBytes > limits.MaximumArtifactBytes ||
            limits.DefaultTimeoutSeconds is < 1 or > MaximumTimeoutSeconds ||
            limits.MaximumTimeoutSeconds is < 1 or > MaximumTimeoutSeconds ||
            limits.DefaultTimeoutSeconds > limits.MaximumTimeoutSeconds)
        {
            throw new WorkerProtocolException(
                "invalid_protocol_limits",
                "Worker protocol limits are outside the frozen bounds.");
        }
        return limits;
    }

    private static WorkerArtifactRequest? ParseArtifactRequest(
        JsonElement value,
        WorkerProtocolLimits limits)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw InvalidField("artifact");
        var fields = ClosedObject(value, "artifactId", "maximumBytes");
        var artifact = new WorkerArtifactRequest(
            GuidField(Required(fields, "artifactId"), "artifactId"),
            PositiveInt64(Required(fields, "maximumBytes"), "maximumBytes"));
        ValidateArtifactRequest(artifact, limits);
        return artifact;
    }

    private static void ValidateArtifactRequest(
        WorkerArtifactRequest? artifact,
        WorkerProtocolLimits limits)
    {
        if (artifact is null)
            return;
        if (artifact.ArtifactId == Guid.Empty ||
            artifact.MaximumBytes is < 1 ||
            artifact.MaximumBytes > limits.MaximumArtifactBytes)
        {
            throw InvalidField("artifact");
        }
    }

    private static void ValidateArtifactChunk(
        WorkerArtifactChunk chunk,
        WorkerProtocolLimits limits)
    {
        if (chunk.RequestId <= 0 ||
            chunk.ArtifactId == Guid.Empty ||
            chunk.Offset < 0 ||
            chunk.Bytes is null ||
            chunk.Bytes.Length is < 1 ||
            chunk.Bytes.Length > limits.MaximumArtifactChunkBytes)
        {
            throw InvalidField("artifactChunk");
        }
    }

    private static void ValidateArtifactSeal(WorkerArtifactSeal seal)
    {
        if (seal.RequestId <= 0 ||
            seal.ArtifactId == Guid.Empty ||
            seal.Length < 0 ||
            !IsSha256(seal.Sha256))
        {
            throw InvalidField("artifactSeal");
        }
    }

    private static void ValidateResult(
        WorkerResultStatus status,
        string text,
        string? detailCode)
    {
        _ = LogicalText(text, "text", MaximumLogicalTextBytes);
        if (!Enum.IsDefined(status) ||
            status == WorkerResultStatus.Completed && detailCode is not null ||
            status != WorkerResultStatus.Completed &&
            (detailCode is null || !IsCode(detailCode)))
        {
            throw new WorkerProtocolException(
                "invalid_operation_result",
                "Worker result fields do not match its status.");
        }
    }

    private static void ValidateStateSnapshot(
        bool available,
        string text,
        string? detailCode)
    {
        _ = LogicalText(text, "text", MaximumLogicalTextBytes);
        if (available && detailCode is not null ||
            !available && (detailCode is null || !IsCode(detailCode)))
        {
            throw new WorkerProtocolException(
                "invalid_state_snapshot",
                "Worker state snapshot fields do not match availability.");
        }
    }

    private static Dictionary<string, JsonElement> ClosedObject(
        JsonElement value,
        params string[] allowedFields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw InvalidField("object");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value))
                throw new WorkerProtocolException(
                    "duplicate_field",
                    "Worker operation payload contains a duplicate field.");
            if (!allowedFields.Contains(property.Name, StringComparer.Ordinal))
                throw new WorkerProtocolException(
                    "unknown_operation_field",
                    "Worker operation payload contains an unknown field.");
        }
        return fields;
    }

    private static JsonElement Required(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value))
            throw new WorkerProtocolException(
                "missing_operation_field",
                "Worker operation payload is missing a required field.");
        return value;
    }

    private static string StringField(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidField(field);
        try
        {
            return value.GetString() ?? throw InvalidField(field);
        }
        catch (InvalidOperationException exception)
        {
            throw InvalidField(field, exception);
        }
    }

    private static Guid GuidField(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(value.GetString(), "D", out var parsed) ||
            parsed == Guid.Empty)
        {
            throw InvalidField(field);
        }
        return parsed;
    }

    private static bool BooleanField(JsonElement value, string field)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidField(field);
        return value.GetBoolean();
    }

    private static long PositiveInt64(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var parsed) || parsed <= 0)
        {
            throw InvalidField(field);
        }
        return parsed;
    }

    private static long NonnegativeInt64(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var parsed) || parsed < 0)
        {
            throw InvalidField(field);
        }
        return parsed;
    }

    private static int PositiveInt32(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var parsed) || parsed <= 0)
        {
            throw InvalidField(field);
        }
        return parsed;
    }

    private static int NonnegativeInt32(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var parsed) || parsed < 0)
        {
            throw InvalidField(field);
        }
        return parsed;
    }

    private static WorkerInvokeRoute ParseRoute(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidField("route");
        return value.GetString() switch
        {
            "auto" => WorkerInvokeRoute.Auto,
            "pwsh" => WorkerInvokeRoute.Pwsh,
            "rtk" => WorkerInvokeRoute.Rtk,
            _ => throw InvalidField("route"),
        };
    }

    private static string RouteName(WorkerInvokeRoute route) => route switch
    {
        WorkerInvokeRoute.Auto => "auto",
        WorkerInvokeRoute.Pwsh => "pwsh",
        WorkerInvokeRoute.Rtk => "rtk",
        _ => throw InvalidField("route"),
    };

    private static WorkerResultStatus ParseResultStatus(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidField("status");
        return value.GetString() switch
        {
            "completed" => WorkerResultStatus.Completed,
            "refused" => WorkerResultStatus.Refused,
            "canceled" => WorkerResultStatus.Canceled,
            "timed_out" => WorkerResultStatus.TimedOut,
            "failed" => WorkerResultStatus.Failed,
            _ => throw InvalidField("status"),
        };
    }

    private static string ResultStatusName(WorkerResultStatus status) => status switch
    {
        WorkerResultStatus.Completed => "completed",
        WorkerResultStatus.Refused => "refused",
        WorkerResultStatus.Canceled => "canceled",
        WorkerResultStatus.TimedOut => "timed_out",
        WorkerResultStatus.Failed => "failed",
        _ => throw InvalidField("status"),
    };

    private static TimeSpan ParseTimeout(
        int timeoutSeconds,
        WorkerProtocolLimits limits)
    {
        var effective = timeoutSeconds == 0
            ? limits.DefaultTimeoutSeconds
            : timeoutSeconds;
        if (effective < 1 || effective > limits.MaximumTimeoutSeconds)
            throw InvalidField("timeoutSeconds");
        return TimeSpan.FromSeconds(effective);
    }

    private static int WholePositiveSeconds(TimeSpan value, string parameter)
    {
        if (value <= TimeSpan.Zero ||
            value.TotalSeconds > MaximumTimeoutSeconds ||
            value.TotalSeconds != Math.Truncate(value.TotalSeconds))
        {
            throw new ArgumentOutOfRangeException(parameter);
        }
        return checked((int)value.TotalSeconds);
    }

    private static string LogicalText(string? value, string field, int maximumBytes)
    {
        if (value is null)
            throw InvalidField(field);
        int bytes;
        try
        {
            bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw InvalidField(field, exception);
        }
        if (bytes > maximumBytes)
            throw new WorkerProtocolException(
                "operation_text_too_large",
                $"Worker operation field '{field}' exceeds its UTF-8 byte limit.");
        return value;
    }

    private static string Code(JsonElement value, string field)
    {
        var parsed = StringField(value, field);
        if (!IsCode(parsed))
            throw InvalidField(field);
        return parsed;
    }

    private static bool IsCode(string value)
    {
        if (value.Length is < 1 or > MaximumCodeLength ||
            value[0] is < 'a' or > 'z')
        {
            return false;
        }
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (character is >= 'a' and <= 'z' ||
                character is >= '0' and <= '9' ||
                character == '_')
            {
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64)
            return false;
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    private static void RequireEmptyPayload(JsonElement payload, WorkerMessageKind kind)
    {
        if (payload.EnumerateObject().Any())
            throw new WorkerProtocolException(
                "invalid_payload",
                $"Worker protocol kind '{kind}' requires an empty payload.");
    }

    private static WorkerProtocolException InvalidField(
        string field,
        Exception? innerException = null)
        => innerException is null
            ? new WorkerProtocolException(
                "invalid_operation_field",
                $"Worker operation field '{field}' is invalid.")
            : new WorkerProtocolException(
                "invalid_operation_field",
                $"Worker operation field '{field}' is invalid.",
                innerException);
}

/// <summary>
/// Supervisor-side ordering and digest validator for one optional artifact.
/// Storage stays outside this type; it only validates immutable protocol facts.
/// </summary>
internal sealed class WorkerArtifactReceiver : IDisposable
{
    private readonly long _requestId;
    private readonly WorkerArtifactRequest _artifact;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _nextOffset;
    private bool _sealed;
    private bool _disposed;

    internal WorkerArtifactReceiver(long requestId, WorkerArtifactRequest artifact)
    {
        if (requestId <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestId));
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.ArtifactId == Guid.Empty || artifact.MaximumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(artifact));
        _requestId = requestId;
        _artifact = artifact;
    }

    internal long Length => _nextOffset;
    internal bool IsSealed => _sealed;

    internal void Accept(WorkerArtifactChunk chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(chunk);
        if (_sealed ||
            chunk.RequestId != _requestId ||
            chunk.ArtifactId != _artifact.ArtifactId ||
            chunk.Offset != _nextOffset ||
            chunk.Bytes.Length < 1 ||
            chunk.Bytes.Length > _artifact.MaximumBytes - _nextOffset)
        {
            throw new WorkerProtocolException(
                "artifact_sequence_invalid",
                "Worker artifact chunks are missing, duplicated, misrouted, or oversized.");
        }
        _hash.AppendData(chunk.Bytes);
        _nextOffset = checked(_nextOffset + chunk.Bytes.Length);
    }

    internal void Accept(WorkerArtifactSeal seal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(seal);
        if (_sealed ||
            seal.RequestId != _requestId ||
            seal.ArtifactId != _artifact.ArtifactId ||
            seal.Length != _nextOffset)
        {
            throw new WorkerProtocolException(
                "artifact_seal_invalid",
                "Worker artifact seal does not match the ordered transfer.");
        }
        var actual = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(seal.Sha256)))
        {
            throw new WorkerProtocolException(
                "artifact_digest_mismatch",
                "Worker artifact seal digest does not match the transferred bytes.");
        }
        _sealed = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _hash.Dispose();
    }
}
