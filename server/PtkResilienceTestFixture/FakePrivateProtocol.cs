using System.Buffers;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PtkResilienceTestFixture;

internal enum FakePrivatePeer
{
    Guardian,
    Host,
}

internal enum FakePrivateMessageKind
{
    Hello,
    Initialize,
    Ready,
    Request,
    Cancel,
    Event,
    Response,
    Shutdown,
}

/// <summary>
/// A parsed R0 guardian/host envelope. Values contains exactly the fields that
/// follow the five common envelope fields; each JsonElement owns its data.
/// </summary>
internal sealed record FakePrivateEnvelope(
    int ProtocolVersion,
    FakePrivateMessageKind Kind,
    Guid GuardianBootId,
    Guid HostBootId,
    long HostGeneration,
    IReadOnlyDictionary<string, JsonElement> Values)
{
    internal JsonElement Value(string name) =>
        Values.TryGetValue(name, out var value)
            ? value
            : throw new FakePrivateProtocolException(
                "missing_field",
                $"Private protocol field '{name}' is missing.");
}

internal sealed class FakePrivateProtocolException : IOException
{
    internal FakePrivateProtocolException(string detailCode, string message)
        : base(message)
    {
        DetailCode = detailCode;
    }

    internal FakePrivateProtocolException(
        string detailCode,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        DetailCode = detailCode;
    }

    internal string DetailCode { get; }
}

/// <summary>
/// Strict bounded framing/envelope codec for the disposable R0 feasibility
/// fixture. The live fake peers deliberately exercise only initialization,
/// manifest transfer, and one fixture-only operation payload; the frozen JSON
/// schemas under server/Contracts/ResilienceR0 remain the complete production
/// v1 authority. Encode and Decode operate on a frame without its required LF;
/// the reader and writer own that NDJSON terminator.
/// </summary>
internal static class FakePrivateProtocol
{
    internal const int Version = 1;
    internal const int MaximumEncodedFrameBytes = 1_048_576;
    internal const int MaximumJsonDepth = 32;

    private const int MaximumManifestBytes = 25_165_824;
    private const int MaximumManifestChunkBytes = 524_288;
    private const int MaximumManifestChunks = 48;
    private const int MaximumAliases = 128;
    private const int MaximumTemplates = 128;

    private static readonly string[] CommonProperties =
    [
        "protocol_version",
        "kind",
        "guardian_boot_id",
        "host_boot_id",
        "host_generation",
    ];

    private static readonly string[] HelloProperties =
    [
        "host_pid",
        "host_executable_sha256",
        "host_build_sha256",
        "public_contract_sha256",
        "configuration_sha256",
        "request_channel_owned",
        "event_channel_owned",
    ];

    private static readonly string[] InitializeProperties =
    [
        "request_id",
        "guardian_protocol_version",
        "host_protocol_version",
        "host_executable_sha256",
        "host_build_sha256",
        "public_contract_sha256",
        "configuration_sha256",
        "package_manifest_sha256",
        "maximum_manifest_bytes",
        "maximum_manifest_chunk_raw_bytes",
        "maximum_aliases",
        "maximum_templates",
    ];

    private static readonly string[] ReadyProperties =
    [
        "initialize_request_id",
        "manifest_id",
        "manifest_sha256",
        "host_pid",
    ];

    private static readonly string[] RequestProperties =
    [
        "request_id",
        "method",
        "deadline_unix_time_milliseconds",
        "session_alias",
        "session_transition_version",
        "worker_boot_id",
        "worker_generation",
        "plan_id",
        "operation_id",
        "payload",
    ];

    private static readonly string[] CancelProperties =
    [
        "request_id",
        "target_request_id",
        "reason",
    ];

    private static readonly string[] EventProperties =
    [
        "event_sequence",
        "event_type",
        "request_id",
        "session_alias",
        "session_transition_version",
        "worker_boot_id",
        "worker_generation",
        "plan_id",
        "operation_id",
        "payload",
    ];

    private static readonly string[] ResponseProperties =
    [
        "request_id",
        "status",
        "payload",
        "error",
    ];

    private static readonly string[] ShutdownProperties =
    [
        "request_id",
        "deadline_unix_time_milliseconds",
        "reason",
    ];

    private static readonly HashSet<string> RequestMethods = new(StringComparer.Ordinal)
    {
        "manifest_header",
        "manifest_chunk",
        "manifest_seal",
        "operation",
        "worker_create_capability_grant",
        "worker_containment_pending_ack",
        "worker_containment_armed_ack",
        "worker_containment_remove_ack",
    };

    private static readonly HashSet<string> HostControlEventTypes = new(StringComparer.Ordinal)
    {
        "worker_create_capability_requested",
        "worker_containment_pending",
        "worker_containment_armed",
        "worker_containment_remove_requested",
    };

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumJsonDepth,
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        MaxDepth = MaximumJsonDepth,
    };

    internal static FakePrivateEnvelope Create(
        FakePrivateMessageKind kind,
        Guid guardianBootId,
        Guid hostBootId,
        long hostGeneration,
        params (string Name, object? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrEmpty(name) || !fields.TryAdd(name, ToElement(value)))
            {
                throw new FakePrivateProtocolException(
                    "duplicate_field",
                    $"Private protocol field '{name}' is invalid or duplicated.");
            }
        }

        return new FakePrivateEnvelope(
            Version,
            kind,
            guardianBootId,
            hostBootId,
            hostGeneration,
            new ReadOnlyDictionary<string, JsonElement>(fields));
    }

    internal static byte[] Encode(FakePrivateEnvelope envelope, FakePrivatePeer sender)
    {
        ValidateEnvelope(envelope, sender);

        using var buffer = new BoundedProtocolBuffer(MaximumEncodedFrameBytes);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocol_version", envelope.ProtocolVersion);
            writer.WriteString("kind", ToWireName(envelope.Kind));
            writer.WriteString("guardian_boot_id", envelope.GuardianBootId.ToString("D"));
            writer.WriteString("host_boot_id", envelope.HostBootId.ToString("D"));
            writer.WriteNumber("host_generation", envelope.HostGeneration);
            foreach (var name in PropertiesFor(envelope.Kind))
            {
                writer.WritePropertyName(name);
                envelope.Values[name].WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        try
        {
            using var depthCheck = JsonDocument.Parse(buffer.WrittenMemory, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new FakePrivateProtocolException(
                "invalid_json",
                $"Private protocol envelope exceeds maximum JSON depth {MaximumJsonDepth}.",
                exception);
        }

        return buffer.ToArray();
    }

    internal static FakePrivateEnvelope Decode(
        ReadOnlyMemory<byte> encodedFrame,
        FakePrivatePeer sender)
    {
        var bytes = encodedFrame.Span;
        if (bytes.Length == 0)
            throw new FakePrivateProtocolException("empty_frame", "Private protocol frames cannot be empty.");
        if (bytes.Length > MaximumEncodedFrameBytes)
            throw FrameTooLarge();
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
        {
            throw new FakePrivateProtocolException(
                "bom_forbidden",
                "Private protocol frames must be UTF-8 without a BOM.");
        }
        if (bytes.IndexOf((byte)'\r') >= 0 || bytes.IndexOf((byte)'\n') >= 0)
        {
            throw new FakePrivateProtocolException(
                "invalid_framing",
                "A private protocol encoded frame cannot contain a raw CR or LF.");
        }

        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new FakePrivateProtocolException(
                "invalid_utf8",
                "Private protocol frame is not strict UTF-8.",
                exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(encodedFrame, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new FakePrivateProtocolException(
                "invalid_json",
                "Private protocol frame is not valid JSON.",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FakePrivateProtocolException(
                    "invalid_envelope",
                    "Private protocol envelope must be a JSON object.");
            }

            RejectDuplicateProperties(root, containerDepth: 1);

            var rootProperties = root.EnumerateObject().ToArray();
            if (rootProperties.Length < CommonProperties.Length ||
                !string.Equals(rootProperties[0].Name, CommonProperties[0], StringComparison.Ordinal) ||
                !string.Equals(rootProperties[1].Name, CommonProperties[1], StringComparison.Ordinal))
            {
                throw PropertyOrderError();
            }

            var kind = ParseKind(rootProperties[1].Value);
            var kindProperties = PropertiesFor(kind);
            var expectedCount = CommonProperties.Length + kindProperties.Length;
            if (rootProperties.Length != expectedCount)
                throw PropertyOrderError();

            for (var index = 0; index < rootProperties.Length; index++)
            {
                var expected = index < CommonProperties.Length
                    ? CommonProperties[index]
                    : kindProperties[index - CommonProperties.Length];
                if (!string.Equals(rootProperties[index].Name, expected, StringComparison.Ordinal))
                    throw PropertyOrderError();
            }

            var protocolVersion = RequireInt32(rootProperties[0].Value, "protocol_version");
            if (protocolVersion != Version)
            {
                throw new FakePrivateProtocolException(
                    "unknown_version",
                    $"Private protocol version {protocolVersion} is not supported.");
            }

            var fields = new Dictionary<string, JsonElement>(kindProperties.Length, StringComparer.Ordinal);
            for (var index = 0; index < kindProperties.Length; index++)
            {
                fields.Add(
                    kindProperties[index],
                    rootProperties[index + CommonProperties.Length].Value.Clone());
            }

            var envelope = new FakePrivateEnvelope(
                protocolVersion,
                kind,
                RequireUuid(rootProperties[2].Value, "guardian_boot_id"),
                RequireUuid(rootProperties[3].Value, "host_boot_id"),
                RequirePositiveInt64(rootProperties[4].Value, "host_generation"),
                new ReadOnlyDictionary<string, JsonElement>(fields));
            ValidateEnvelope(envelope, sender);
            return envelope;
        }
    }

    /// <summary>
    /// Stateless one-frame read that never consumes bytes after the LF. For a
    /// stream carrying multiple frames, FakePrivateProtocolReader is faster
    /// because it retains bounded transport read-ahead.
    /// </summary>
    internal static async ValueTask<FakePrivateEnvelope?> ReadAsync(
        Stream stream,
        FakePrivatePeer sender,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));

        var frame = ArrayPool<byte>.Shared.Rent(MaximumEncodedFrameBytes + 1);
        try
        {
            var length = 0;
            while (true)
            {
                var read = await stream.ReadAsync(
                    frame.AsMemory(length, 1),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (length == 0) return null;
                    throw TruncatedFrame();
                }

                if (frame[length] == (byte)'\n')
                    return Decode(new ReadOnlyMemory<byte>(frame, 0, length), sender);

                length++;
                if (length > MaximumEncodedFrameBytes)
                    throw FrameTooLarge();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame, clearArray: true);
        }
    }

    private static void ValidateEnvelope(FakePrivateEnvelope envelope, FakePrivatePeer sender)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!Enum.IsDefined(sender))
            throw new ArgumentOutOfRangeException(nameof(sender));
        if (envelope.ProtocolVersion != Version)
        {
            throw new FakePrivateProtocolException(
                "unknown_version",
                $"Private protocol version {envelope.ProtocolVersion} is not supported.");
        }
        if (!Enum.IsDefined(envelope.Kind))
            throw new FakePrivateProtocolException("unknown_kind", "Private protocol kind is unknown.");
        ValidateUuid(envelope.GuardianBootId, "guardian_boot_id");
        ValidateUuid(envelope.HostBootId, "host_boot_id");
        if (envelope.HostGeneration <= 0)
            throw InvalidField("host_generation");
        if (ExpectedSender(envelope.Kind) != sender)
        {
            throw new FakePrivateProtocolException(
                "wrong_direction",
                $"Private protocol kind '{ToWireName(envelope.Kind)}' cannot be sent by {sender}.");
        }

        ArgumentNullException.ThrowIfNull(envelope.Values);
        var expectedProperties = PropertiesFor(envelope.Kind);
        if (envelope.Values.Count != expectedProperties.Length ||
            expectedProperties.Any(name => !envelope.Values.ContainsKey(name)) ||
            envelope.Values.Keys.Any(name => !expectedProperties.Contains(name, StringComparer.Ordinal)))
        {
            throw new FakePrivateProtocolException(
                "invalid_fields",
                "Private protocol envelope fields do not exactly match its kind.");
        }

        foreach (var value in envelope.Values.Values)
            RejectDuplicateProperties(value, containerDepth: 2);

        switch (envelope.Kind)
        {
            case FakePrivateMessageKind.Hello:
                ValidateHello(envelope);
                break;
            case FakePrivateMessageKind.Initialize:
                ValidateInitialize(envelope);
                break;
            case FakePrivateMessageKind.Ready:
                ValidateReady(envelope);
                break;
            case FakePrivateMessageKind.Request:
                ValidateRequest(envelope);
                break;
            case FakePrivateMessageKind.Cancel:
                ValidateCancel(envelope);
                break;
            case FakePrivateMessageKind.Event:
                ValidateEvent(envelope);
                break;
            case FakePrivateMessageKind.Response:
                ValidateResponse(envelope);
                break;
            case FakePrivateMessageKind.Shutdown:
                ValidateShutdown(envelope);
                break;
            default:
                throw new FakePrivateProtocolException("unknown_kind", "Private protocol kind is unknown.");
        }
    }

    private static void ValidateHello(FakePrivateEnvelope envelope)
    {
        _ = RequirePositiveInt32(envelope.Value("host_pid"), "host_pid");
        _ = RequireSha256(envelope.Value("host_executable_sha256"), "host_executable_sha256");
        _ = RequireSha256(envelope.Value("host_build_sha256"), "host_build_sha256");
        _ = RequireSha256(envelope.Value("public_contract_sha256"), "public_contract_sha256");
        _ = RequireSha256(envelope.Value("configuration_sha256"), "configuration_sha256");
        RequireTrue(envelope.Value("request_channel_owned"), "request_channel_owned");
        RequireTrue(envelope.Value("event_channel_owned"), "event_channel_owned");
    }

    private static void ValidateInitialize(FakePrivateEnvelope envelope)
    {
        _ = RequirePositiveInt64(envelope.Value("request_id"), "request_id");
        RequireExactInt32(envelope.Value("guardian_protocol_version"), "guardian_protocol_version", Version);
        RequireExactInt32(envelope.Value("host_protocol_version"), "host_protocol_version", Version);
        _ = RequireSha256(envelope.Value("host_executable_sha256"), "host_executable_sha256");
        _ = RequireSha256(envelope.Value("host_build_sha256"), "host_build_sha256");
        _ = RequireSha256(envelope.Value("public_contract_sha256"), "public_contract_sha256");
        _ = RequireSha256(envelope.Value("configuration_sha256"), "configuration_sha256");
        _ = RequireSha256(envelope.Value("package_manifest_sha256"), "package_manifest_sha256");
        RequireExactInt32(envelope.Value("maximum_manifest_bytes"), "maximum_manifest_bytes", MaximumManifestBytes);
        RequireExactInt32(
            envelope.Value("maximum_manifest_chunk_raw_bytes"),
            "maximum_manifest_chunk_raw_bytes",
            MaximumManifestChunkBytes);
        RequireExactInt32(envelope.Value("maximum_aliases"), "maximum_aliases", MaximumAliases);
        RequireExactInt32(envelope.Value("maximum_templates"), "maximum_templates", MaximumTemplates);
    }

    private static void ValidateReady(FakePrivateEnvelope envelope)
    {
        _ = RequirePositiveInt64(envelope.Value("initialize_request_id"), "initialize_request_id");
        _ = RequireUuid(envelope.Value("manifest_id"), "manifest_id");
        _ = RequireSha256(envelope.Value("manifest_sha256"), "manifest_sha256");
        _ = RequirePositiveInt32(envelope.Value("host_pid"), "host_pid");
    }

    private static void ValidateRequest(FakePrivateEnvelope envelope)
    {
        _ = RequirePositiveInt64(envelope.Value("request_id"), "request_id");
        var method = RequireString(envelope.Value("method"), "method");
        if (!RequestMethods.Contains(method)) throw InvalidField("method");
        var deadline = RequireNullablePositiveInt64(
            envelope.Value("deadline_unix_time_milliseconds"),
            "deadline_unix_time_milliseconds");
        var alias = RequireNullableAlias(envelope.Value("session_alias"), "session_alias");
        var transitionVersion = RequireNullablePositiveInt64(
            envelope.Value("session_transition_version"),
            "session_transition_version");
        var workerBootId = RequireNullableUuid(envelope.Value("worker_boot_id"), "worker_boot_id");
        var workerGeneration = RequireNullablePositiveInt64(
            envelope.Value("worker_generation"),
            "worker_generation");
        var planId = RequireNullableUuid(envelope.Value("plan_id"), "plan_id");
        var operationId = RequireNullableUuid(envelope.Value("operation_id"), "operation_id");

        var payload = RequireObject(envelope.Value("payload"), "payload");
        if (method == "operation")
        {
            if (deadline is null || alias is null || transitionVersion is null ||
                workerBootId is null || workerGeneration is null ||
                planId is not null || operationId is not null)
            {
                throw InvalidField("operation_correlation");
            }
        }
        else if (method is "manifest_header" or "manifest_chunk" or "manifest_seal")
        {
            if (deadline is not null || alias is not null || transitionVersion is not null ||
                workerBootId is not null || workerGeneration is not null ||
                planId is not null || operationId is not null)
            {
                throw InvalidField("manifest_correlation");
            }
        }
        ValidateRequestPayload(method, payload);
    }

    private static void ValidateCancel(FakePrivateEnvelope envelope)
    {
        _ = RequirePositiveInt64(envelope.Value("request_id"), "request_id");
        _ = RequirePositiveInt64(envelope.Value("target_request_id"), "target_request_id");
        var reason = RequireString(envelope.Value("reason"), "reason");
        if (reason is not ("caller_canceled" or "deadline_expired" or "guardian_shutdown"))
            throw InvalidField("reason");
    }

    private static void ValidateEvent(FakePrivateEnvelope envelope)
    {
        _ = RequirePositiveInt64(envelope.Value("event_sequence"), "event_sequence");
        var eventType = RequireString(envelope.Value("event_type"), "event_type");
        if (!HostControlEventTypes.Contains(eventType)) throw InvalidField("event_type");
        _ = RequireNullablePositiveInt64(envelope.Value("request_id"), "request_id");
        var alias = RequireNullableAlias(envelope.Value("session_alias"), "session_alias");
        var transition = RequireNullablePositiveInt64(
            envelope.Value("session_transition_version"),
            "session_transition_version");
        var workerBootId = RequireNullableUuid(envelope.Value("worker_boot_id"), "worker_boot_id");
        var workerGeneration = RequireNullablePositiveInt64(
            envelope.Value("worker_generation"),
            "worker_generation");
        var planId = RequireNullableUuid(envelope.Value("plan_id"), "plan_id");
        var operationId = RequireNullableUuid(envelope.Value("operation_id"), "operation_id");
        var payload = RequireObject(envelope.Value("payload"), "payload");

        if (alias is null || transition is null || planId is not null || operationId is not null)
            throw InvalidField("event_correlation");
        if (eventType == "worker_create_capability_requested")
        {
            if (workerBootId is not null || workerGeneration is not null)
                throw InvalidField("event_correlation");
        }
        else if (workerBootId is null || workerGeneration is null)
        {
            throw InvalidField("event_correlation");
        }

        ValidateEventPayload(eventType, payload);
    }

    private static void ValidateResponse(FakePrivateEnvelope envelope)
    {
        _ = RequirePositiveInt64(envelope.Value("request_id"), "request_id");
        var status = RequireString(envelope.Value("status"), "status");
        var payload = envelope.Value("payload");
        var error = envelope.Value("error");

        if (status == "ok")
        {
            ValidateOkResponsePayload(RequireObject(payload, "payload"));
            if (error.ValueKind != JsonValueKind.Null) throw InvalidField("error");
            return;
        }
        if (status != "error" || payload.ValueKind != JsonValueKind.Null)
            throw InvalidField("status");

        var errorObject = RequireObject(error, "error");
        RequireExactProperties(errorObject, "error", "detail_code", "message_code");
        _ = RequireNonEmptyString(errorObject.GetProperty("detail_code"), "detail_code");
        _ = RequireNonEmptyString(errorObject.GetProperty("message_code"), "message_code");
    }

    private static void ValidateShutdown(FakePrivateEnvelope envelope)
    {
        _ = RequirePositiveInt64(envelope.Value("request_id"), "request_id");
        _ = RequirePositiveInt64(
            envelope.Value("deadline_unix_time_milliseconds"),
            "deadline_unix_time_milliseconds");
        var reason = RequireString(envelope.Value("reason"), "reason");
        if (reason is not ("guardian_eof" or "guardian_shutdown" or "host_recycle"))
            throw InvalidField("reason");
    }

    private static void ValidateRequestPayload(string method, JsonElement payload)
    {
        switch (method)
        {
            case "manifest_header":
                RequireExactProperties(
                    payload,
                    "manifest_header payload",
                    "manifest_id",
                    "total_bytes",
                    "chunk_count",
                    "manifest_sha256",
                    "alias_count",
                    "template_count");
                _ = RequireUuid(payload.GetProperty("manifest_id"), "manifest_id");
                var totalBytes = RequireRange(
                    payload.GetProperty("total_bytes"),
                    "total_bytes",
                    1,
                    MaximumManifestBytes);
                var chunkCount = RequireRange(
                    payload.GetProperty("chunk_count"),
                    "chunk_count",
                    1,
                    MaximumManifestChunks);
                var expectedChunks = checked((totalBytes + MaximumManifestChunkBytes - 1) /
                    MaximumManifestChunkBytes);
                if (chunkCount != expectedChunks) throw InvalidField("chunk_count");
                _ = RequireSha256(payload.GetProperty("manifest_sha256"), "manifest_sha256");
                _ = RequireRange(payload.GetProperty("alias_count"), "alias_count", 1, MaximumAliases);
                _ = RequireRange(payload.GetProperty("template_count"), "template_count", 0, MaximumTemplates);
                break;
            case "manifest_chunk":
                RequireExactProperties(
                    payload,
                    "manifest_chunk payload",
                    "manifest_id",
                    "chunk_index",
                    "offset",
                    "raw_bytes",
                    "raw_base64",
                    "raw_sha256");
                _ = RequireUuid(payload.GetProperty("manifest_id"), "manifest_id");
                _ = RequireRange(
                    payload.GetProperty("chunk_index"),
                    "chunk_index",
                    0,
                    MaximumManifestChunks - 1);
                _ = RequireRange(
                    payload.GetProperty("offset"),
                    "offset",
                    0,
                    MaximumManifestBytes - 1);
                ValidateManifestChunk(payload);
                break;
            case "manifest_seal":
                RequireExactProperties(
                    payload,
                    "manifest_seal payload",
                    "manifest_id",
                    "total_bytes",
                    "chunk_count",
                    "manifest_sha256");
                _ = RequireUuid(payload.GetProperty("manifest_id"), "manifest_id");
                _ = RequireRange(
                    payload.GetProperty("total_bytes"),
                    "total_bytes",
                    1,
                    MaximumManifestBytes);
                _ = RequireRange(
                    payload.GetProperty("chunk_count"),
                    "chunk_count",
                    1,
                    MaximumManifestChunks);
                _ = RequireSha256(payload.GetProperty("manifest_sha256"), "manifest_sha256");
                break;
            case "worker_create_capability_grant":
                RequireExactProperties(
                    payload,
                    "worker_create_capability_grant payload",
                    "source_event_sequence",
                    "token",
                    "worker_generation");
                _ = RequirePositiveInt64(payload.GetProperty("source_event_sequence"), "source_event_sequence");
                ValidateCapabilityToken(payload.GetProperty("token"));
                _ = RequirePositiveInt64(payload.GetProperty("worker_generation"), "worker_generation");
                break;
            case "worker_containment_pending_ack":
            case "worker_containment_armed_ack":
            case "worker_containment_remove_ack":
                RequireExactProperties(payload, $"{method} payload", "source_event_sequence");
                _ = RequirePositiveInt64(payload.GetProperty("source_event_sequence"), "source_event_sequence");
                break;
            case "operation":
                ValidateFixtureOperationRequest(payload);
                break;
            default:
                throw InvalidField("method");
        }
    }

    private static void ValidateEventPayload(string eventType, JsonElement payload)
    {
        if (eventType == "worker_create_capability_requested")
        {
            RequireExactProperties(
                payload,
                "worker_create_capability_requested payload",
                "binding_digest",
                "startup_deadline_unix_time_milliseconds");
            _ = RequireSha256(payload.GetProperty("binding_digest"), "binding_digest");
            _ = RequirePositiveInt64(
                payload.GetProperty("startup_deadline_unix_time_milliseconds"),
                "startup_deadline_unix_time_milliseconds");
            return;
        }

        var groupField = eventType == "worker_containment_pending" ? "intended_pgid" : "pgid";
        RequireExactProperties(
            payload,
            $"{eventType} payload",
            "broker_pid",
            "broker_start_identity_high",
            "broker_start_identity_low",
            "worker_pid",
            "worker_start_identity_high",
            "worker_start_identity_low",
            groupField);
        _ = RequirePositiveUInt32(payload.GetProperty("broker_pid"), "broker_pid");
        _ = RequireUInt64(payload.GetProperty("broker_start_identity_high"), "broker_start_identity_high");
        _ = RequireUInt64(payload.GetProperty("broker_start_identity_low"), "broker_start_identity_low");
        var workerPid = RequirePositiveUInt32(payload.GetProperty("worker_pid"), "worker_pid");
        _ = RequireUInt64(payload.GetProperty("worker_start_identity_high"), "worker_start_identity_high");
        _ = RequireUInt64(payload.GetProperty("worker_start_identity_low"), "worker_start_identity_low");
        var groupId = RequirePositiveUInt32(payload.GetProperty(groupField), groupField);
        if (groupId != workerPid) throw InvalidField(groupField);
    }

    private static void ValidateManifestChunk(JsonElement payload)
    {
        var rawBytes = RequireRange(
            payload.GetProperty("raw_bytes"),
            "raw_bytes",
            1,
            MaximumManifestChunkBytes);
        var encoded = RequireString(payload.GetProperty("raw_base64"), "raw_base64");
        if (encoded.Length == 0 || encoded.Length % 4 != 0 ||
            encoded.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '=')))
        {
            throw InvalidField("raw_base64");
        }

        var maximumDecoded = checked(encoded.Length / 4 * 3);
        var decoded = ArrayPool<byte>.Shared.Rent(Math.Max(1, maximumDecoded));
        try
        {
            if (!Convert.TryFromBase64String(encoded, decoded, out var decodedLength) ||
                decodedLength is < 1 or > MaximumManifestChunkBytes ||
                decodedLength != rawBytes ||
                !string.Equals(
                    Convert.ToBase64String(decoded, 0, decodedLength),
                    encoded,
                    StringComparison.Ordinal))
            {
                throw InvalidField("raw_base64");
            }

            var expectedHash = RequireSha256(payload.GetProperty("raw_sha256"), "raw_sha256");
            Span<byte> actualHash = stackalloc byte[32];
            SHA256.HashData(decoded.AsSpan(0, decodedLength), actualHash);
            if (!string.Equals(Convert.ToHexString(actualHash).ToLowerInvariant(), expectedHash, StringComparison.Ordinal))
                throw InvalidField("raw_sha256");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(decoded, clearArray: true);
        }
    }

    private static void ValidateFixtureOperationRequest(JsonElement payload)
    {
        RequireExactProperties(
            payload,
            "operation payload",
            "operation",
            "call_id",
            "dispatch_capability",
            "output_capability",
            "arguments");
        if (RequireString(payload.GetProperty("operation"), "operation") != "job_list")
            throw InvalidField("operation");

        var callId = RequireUuidV7(payload.GetProperty("call_id"), "call_id");
        var dispatch = RequireObject(
            payload.GetProperty("dispatch_capability"),
            "dispatch_capability");
        RequireExactProperties(
            dispatch,
            "dispatch_capability",
            "token",
            "call_id",
            "expires_unix_time_milliseconds");
        ValidateCapabilityToken(dispatch.GetProperty("token"));
        if (RequireUuidV7(dispatch.GetProperty("call_id"), "dispatch_capability.call_id") != callId)
            throw InvalidField("dispatch_capability.call_id");
        _ = RequirePositiveInt64(
            dispatch.GetProperty("expires_unix_time_milliseconds"),
            "expires_unix_time_milliseconds");

        if (payload.GetProperty("output_capability").ValueKind != JsonValueKind.Null)
            throw InvalidField("output_capability");
        RequireExactProperties(payload.GetProperty("arguments"), "job_list arguments");
    }

    private static void ValidateOkResponsePayload(JsonElement payload)
    {
        if (!payload.TryGetProperty("response_type", out var responseTypeValue))
            throw InvalidField("response_type");

        switch (RequireString(responseTypeValue, "response_type"))
        {
            case "manifest_header_accepted":
                RequireExactProperties(
                    payload,
                    "manifest_header_accepted payload",
                    "response_type",
                    "manifest_id",
                    "next_chunk_index",
                    "next_offset");
                _ = RequireUuid(payload.GetProperty("manifest_id"), "manifest_id");
                RequireExactInt32(payload.GetProperty("next_chunk_index"), "next_chunk_index", 0);
                RequireExactInt32(payload.GetProperty("next_offset"), "next_offset", 0);
                break;
            case "manifest_chunk_accepted":
                RequireExactProperties(
                    payload,
                    "manifest_chunk_accepted payload",
                    "response_type",
                    "manifest_id",
                    "chunk_index",
                    "next_chunk_index",
                    "next_offset");
                _ = RequireUuid(payload.GetProperty("manifest_id"), "manifest_id");
                var chunkIndex = RequireRange(
                    payload.GetProperty("chunk_index"),
                    "chunk_index",
                    0,
                    MaximumManifestChunks - 1);
                var nextChunkIndex = RequireRange(
                    payload.GetProperty("next_chunk_index"),
                    "next_chunk_index",
                    1,
                    MaximumManifestChunks);
                if (nextChunkIndex != chunkIndex + 1) throw InvalidField("next_chunk_index");
                _ = RequireRange(
                    payload.GetProperty("next_offset"),
                    "next_offset",
                    1,
                    MaximumManifestBytes);
                break;
            case "manifest_sealed":
                RequireExactProperties(
                    payload,
                    "manifest_sealed payload",
                    "response_type",
                    "manifest_id",
                    "manifest_sha256",
                    "total_bytes");
                _ = RequireUuid(payload.GetProperty("manifest_id"), "manifest_id");
                _ = RequireSha256(payload.GetProperty("manifest_sha256"), "manifest_sha256");
                _ = RequireRange(
                    payload.GetProperty("total_bytes"),
                    "total_bytes",
                    1,
                    MaximumManifestBytes);
                break;
            case "operation_completed":
                ValidateFixtureOperationCompleted(payload);
                break;
            case "control_acknowledged":
                RequireExactProperties(
                    payload,
                    "control_acknowledged payload",
                    "response_type",
                    "source_event_sequence");
                _ = RequirePositiveInt64(
                    payload.GetProperty("source_event_sequence"),
                    "source_event_sequence");
                break;
            case "shutdown_accepted":
                RequireExactProperties(payload, "shutdown_accepted payload", "response_type");
                break;
            default:
                throw InvalidField("response_type");
        }
    }

    private static void ValidateFixtureOperationCompleted(JsonElement payload)
    {
        RequireExactProperties(
            payload,
            "operation_completed payload",
            "response_type",
            "operation",
            "result");
        if (RequireString(payload.GetProperty("operation"), "operation") != "job_list")
            throw InvalidField("operation");

        var result = RequireObject(payload.GetProperty("result"), "result");
        RequireExactProperties(result, "text_result", "text");
        var text = RequireString(result.GetProperty("text"), "text");
        if (text.Length > 131_072 || StrictUtf8.GetByteCount(text) > 131_072)
            throw InvalidField("text");
    }

    private static void ValidateCapabilityToken(JsonElement value)
    {
        var token = RequireString(value, "token");
        if (token.Length != 43 || token.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw InvalidField("token");
        }

        Span<char> padded = stackalloc char[44];
        token.AsSpan().CopyTo(padded);
        for (var index = 0; index < token.Length; index++)
        {
            if (padded[index] == '-') padded[index] = '+';
            else if (padded[index] == '_') padded[index] = '/';
        }
        padded[^1] = '=';
        Span<byte> decoded = stackalloc byte[32];
        if (!Convert.TryFromBase64Chars(padded, decoded, out var written) || written != 32)
            throw InvalidField("token");
        CryptographicOperations.ZeroMemory(decoded);
    }

    private static JsonElement ToElement(object? value)
    {
        if (value is JsonElement element) return element.Clone();
        return JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), SerializerOptions);
    }

    private static string[] PropertiesFor(FakePrivateMessageKind kind) => kind switch
    {
        FakePrivateMessageKind.Hello => HelloProperties,
        FakePrivateMessageKind.Initialize => InitializeProperties,
        FakePrivateMessageKind.Ready => ReadyProperties,
        FakePrivateMessageKind.Request => RequestProperties,
        FakePrivateMessageKind.Cancel => CancelProperties,
        FakePrivateMessageKind.Event => EventProperties,
        FakePrivateMessageKind.Response => ResponseProperties,
        FakePrivateMessageKind.Shutdown => ShutdownProperties,
        _ => throw new FakePrivateProtocolException("unknown_kind", "Private protocol kind is unknown."),
    };

    private static FakePrivatePeer ExpectedSender(FakePrivateMessageKind kind) => kind switch
    {
        FakePrivateMessageKind.Hello or
        FakePrivateMessageKind.Ready or
        FakePrivateMessageKind.Event or
        FakePrivateMessageKind.Response => FakePrivatePeer.Host,
        FakePrivateMessageKind.Initialize or
        FakePrivateMessageKind.Request or
        FakePrivateMessageKind.Cancel or
        FakePrivateMessageKind.Shutdown => FakePrivatePeer.Guardian,
        _ => throw new FakePrivateProtocolException("unknown_kind", "Private protocol kind is unknown."),
    };

    private static FakePrivateMessageKind ParseKind(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidField("kind");
        return value.GetString() switch
        {
            "hello" => FakePrivateMessageKind.Hello,
            "initialize" => FakePrivateMessageKind.Initialize,
            "ready" => FakePrivateMessageKind.Ready,
            "request" => FakePrivateMessageKind.Request,
            "cancel" => FakePrivateMessageKind.Cancel,
            "event" => FakePrivateMessageKind.Event,
            "response" => FakePrivateMessageKind.Response,
            "shutdown" => FakePrivateMessageKind.Shutdown,
            _ => throw new FakePrivateProtocolException(
                "unknown_kind",
                "Private protocol kind is missing or unknown."),
        };
    }

    private static string ToWireName(FakePrivateMessageKind kind) => kind switch
    {
        FakePrivateMessageKind.Hello => "hello",
        FakePrivateMessageKind.Initialize => "initialize",
        FakePrivateMessageKind.Ready => "ready",
        FakePrivateMessageKind.Request => "request",
        FakePrivateMessageKind.Cancel => "cancel",
        FakePrivateMessageKind.Event => "event",
        FakePrivateMessageKind.Response => "response",
        FakePrivateMessageKind.Shutdown => "shutdown",
        _ => throw new FakePrivateProtocolException("unknown_kind", "Private protocol kind is unknown."),
    };

    private static void RejectDuplicateProperties(JsonElement element, int containerDepth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                RejectExcessDepth(containerDepth);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new FakePrivateProtocolException(
                            "duplicate_field",
                            $"Private protocol JSON contains duplicate field '{property.Name}'.");
                    }
                    RejectDuplicateProperties(property.Value, containerDepth + 1);
                }
                break;
            case JsonValueKind.Array:
                RejectExcessDepth(containerDepth);
                foreach (var item in element.EnumerateArray())
                    RejectDuplicateProperties(item, containerDepth + 1);
                break;
        }
    }

    private static void RejectExcessDepth(int depth)
    {
        if (depth > MaximumJsonDepth)
        {
            throw new FakePrivateProtocolException(
                "invalid_json",
                $"Private protocol envelope exceeds maximum JSON depth {MaximumJsonDepth}.");
        }
    }

    private static void RequireExactProperties(
        JsonElement value,
        string context,
        params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidField(context);
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != expected.Length)
            throw InvalidField(context);
        for (var index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(properties[index].Name, expected[index], StringComparison.Ordinal))
                throw InvalidField(context);
        }
    }

    private static JsonElement RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidField(name);
        return value;
    }

    private static string RequireString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String) throw InvalidField(name);
        return value.GetString()!;
    }

    private static string RequireNonEmptyString(JsonElement value, string name)
    {
        var text = RequireString(value, name);
        if (text.Length == 0) throw InvalidField(name);
        return text;
    }

    private static int RequireInt32(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
            throw InvalidField(name);
        return parsed;
    }

    private static int RequirePositiveInt32(JsonElement value, string name)
    {
        var parsed = RequireInt32(value, name);
        if (parsed <= 0) throw InvalidField(name);
        return parsed;
    }

    private static long RequirePositiveInt64(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var parsed) ||
            parsed <= 0)
        {
            throw InvalidField(name);
        }
        return parsed;
    }

    private static long? RequireNullablePositiveInt64(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Null ? null : RequirePositiveInt64(value, name);

    private static uint RequirePositiveUInt32(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetUInt32(out var parsed) ||
            parsed == 0)
        {
            throw InvalidField(name);
        }
        return parsed;
    }

    private static ulong RequireUInt64(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var parsed))
            throw InvalidField(name);
        return parsed;
    }

    private static int RequireRange(JsonElement value, string name, int minimum, int maximum)
    {
        var parsed = RequireInt32(value, name);
        if (parsed < minimum || parsed > maximum) throw InvalidField(name);
        return parsed;
    }

    private static void RequireExactInt32(JsonElement value, string name, int expected)
    {
        if (RequireInt32(value, name) != expected) throw InvalidField(name);
    }

    private static void RequireTrue(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.True) throw InvalidField(name);
    }

    private static string RequireSha256(JsonElement value, string name)
    {
        var text = RequireString(value, name);
        if (text.Length != 64 || text.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw InvalidField(name);
        }
        return text;
    }

    private static Guid RequireUuid(JsonElement value, string name)
    {
        var text = RequireString(value, name);
        if (!Guid.TryParseExact(text, "D", out var parsed) ||
            !string.Equals(text, parsed.ToString("D"), StringComparison.Ordinal) ||
            text[14] != '4' ||
            text[19] is not ('8' or '9' or 'a' or 'b'))
        {
            throw InvalidField(name);
        }
        return parsed;
    }

    private static Guid? RequireNullableUuid(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Null ? null : RequireUuid(value, name);

    private static Guid RequireUuidV7(JsonElement value, string name)
    {
        var text = RequireString(value, name);
        if (!Guid.TryParseExact(text, "D", out var parsed) ||
            !string.Equals(text, parsed.ToString("D"), StringComparison.Ordinal) ||
            text[14] != '7' ||
            text[19] is not ('8' or '9' or 'a' or 'b'))
        {
            throw InvalidField(name);
        }
        return parsed;
    }

    private static void ValidateUuid(Guid value, string name)
    {
        var text = value.ToString("D");
        if (text[14] != '4' || text[19] is not ('8' or '9' or 'a' or 'b'))
            throw InvalidField(name);
    }

    private static string? RequireNullableAlias(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var alias = RequireString(value, name);
        if (alias.Length is < 1 or > 64 ||
            alias[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
            alias.Any(character => character is not
                ((>= 'a' and <= 'z') or
                 (>= '0' and <= '9') or '.' or '_' or '-')))
        {
            throw InvalidField(name);
        }
        return alias;
    }

    private static FakePrivateProtocolException InvalidField(string name) =>
        new("invalid_field", $"Private protocol field '{name}' is invalid.");

    private static FakePrivateProtocolException PropertyOrderError() =>
        new(
            "property_order",
            "Private protocol fields are missing, unknown, or out of order.");

    private static FakePrivateProtocolException FrameTooLarge() =>
        new(
            "frame_too_large",
            $"Private protocol frame exceeds {MaximumEncodedFrameBytes} encoded bytes.");

    private static FakePrivateProtocolException TruncatedFrame() =>
        new(
            "truncated_frame",
            "Private protocol stream ended before the required LF terminator.");

    private sealed class BoundedProtocolBuffer : IBufferWriter<byte>, IDisposable
    {
        private readonly int _maximumBytes;
        private byte[] _buffer;
        private int _written;
        private bool _disposed;

        internal BoundedProtocolBuffer(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(256, maximumBytes));
        }

        internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

        public void Advance(int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (count < 0 || count > _maximumBytes - _written || count > _buffer.Length - _written)
                throw FrameTooLarge();
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(
                _written,
                Math.Min(_buffer.Length - _written, _maximumBytes - _written));
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(
                _written,
                Math.Min(_buffer.Length - _written, _maximumBytes - _written));
        }

        internal byte[] ToArray()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _buffer.AsSpan(0, _written).ToArray();
        }

        private void EnsureCapacity(int sizeHint)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            if (sizeHint == 0) sizeHint = 1;
            if (sizeHint > _maximumBytes - _written) throw FrameTooLarge();
            if (sizeHint <= _buffer.Length - _written) return;

            var required = checked(_written + sizeHint);
            var doubled = Math.Min(
                _maximumBytes,
                Math.Max(required, checked(_buffer.Length * 2)));
            var replacement = ArrayPool<byte>.Shared.Rent(doubled);
            _buffer.AsSpan(0, _written).CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = replacement;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = Array.Empty<byte>();
            _written = 0;
        }
    }
}

/// <summary>
/// Incremental bounded reader that preserves coalesced-frame read-ahead and
/// returns every pooled frame buffer with clearing enabled.
/// </summary>
internal sealed class FakePrivateProtocolReader
{
    private const int TransportBufferBytes = 16 * 1024;

    private readonly Stream _stream;
    private readonly FakePrivatePeer _sender;
    private readonly ArrayPool<byte> _framePool;
    private readonly byte[] _transportBuffer = new byte[TransportBufferBytes];
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private int _transportOffset;
    private int _transportLength;

    internal FakePrivateProtocolReader(
        Stream stream,
        FakePrivatePeer sender,
        ArrayPool<byte>? framePool = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        if (!Enum.IsDefined(sender)) throw new ArgumentOutOfRangeException(nameof(sender));
        _stream = stream;
        _sender = sender;
        _framePool = framePool ?? ArrayPool<byte>.Shared;
    }

    internal async ValueTask<FakePrivateEnvelope?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var frame = _framePool.Rent(FakePrivateProtocol.MaximumEncodedFrameBytes);
        try
        {
            var frameLength = 0;
            while (true)
            {
                if (_transportOffset == _transportLength)
                {
                    _transportLength = await _stream.ReadAsync(
                        _transportBuffer.AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    _transportOffset = 0;
                    if (_transportLength == 0)
                    {
                        if (frameLength == 0) return null;
                        throw new FakePrivateProtocolException(
                            "truncated_frame",
                            "Private protocol stream ended before the required LF terminator.");
                    }
                }

                var available = _transportBuffer.AsSpan(
                    _transportOffset,
                    _transportLength - _transportOffset);
                var newlineOffset = available.IndexOf((byte)'\n');
                var payloadBytes = newlineOffset >= 0 ? newlineOffset : available.Length;
                if (payloadBytes > FakePrivateProtocol.MaximumEncodedFrameBytes - frameLength)
                {
                    throw new FakePrivateProtocolException(
                        "frame_too_large",
                        $"Private protocol frame exceeds {FakePrivateProtocol.MaximumEncodedFrameBytes} encoded bytes.");
                }

                available[..payloadBytes].CopyTo(frame.AsSpan(frameLength));
                frameLength += payloadBytes;
                _transportOffset += payloadBytes;
                if (newlineOffset < 0) continue;

                _transportOffset++;
                return FakePrivateProtocol.Decode(
                    new ReadOnlyMemory<byte>(frame, 0, frameLength),
                    _sender);
            }
        }
        finally
        {
            _framePool.Return(frame, clearArray: true);
            _readGate.Release();
        }
    }
}

/// <summary>Serializes complete LF-terminated frames and latches ambiguous write failure.</summary>
internal sealed class FakePrivateProtocolWriter
{
    private readonly Stream _stream;
    private readonly FakePrivatePeer _sender;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Exception? _terminalFailure;

    internal FakePrivateProtocolWriter(Stream stream, FakePrivatePeer sender)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        if (!Enum.IsDefined(sender)) throw new ArgumentOutOfRangeException(nameof(sender));
        _stream = stream;
        _sender = sender;
    }

    internal async ValueTask WriteAsync(
        FakePrivateEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        await WriteCoreAsync(
            envelope,
            encodedFrame: null,
            failAfterFirstByte: false,
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask WritePrefixThenFailAsync(
        FakePrivateEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        await WriteCoreAsync(
            envelope,
            encodedFrame: null,
            failAfterFirstByte: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a complete encoded frame, then preserves its exact bytes and
    /// appends only the contract LF. This is used by the fixture to prove that
    /// a noncanonical but valid response payload survives decoding unchanged.
    /// </summary>
    internal async ValueTask WriteRawAsync(
        ReadOnlyMemory<byte> encodedFrame,
        CancellationToken cancellationToken = default)
    {
        var ownedFrame = encodedFrame.ToArray();
        await WriteCoreAsync(
            envelope: null,
            ownedFrame,
            failAfterFirstByte: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteCoreAsync(
        FakePrivateEnvelope? envelope,
        byte[]? encodedFrame,
        bool failAfterFirstByte,
        CancellationToken cancellationToken)
    {
        var gateAcquired = false;
        byte[]? encoded = encodedFrame;
        byte[]? frame = null;
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            if (_terminalFailure is { } terminalFailure)
            {
                throw new FakePrivateProtocolException(
                    "writer_faulted",
                    "Private protocol writer is unusable after a prior transport failure.",
                    terminalFailure);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (encoded is null)
            {
                encoded = FakePrivateProtocol.Encode(
                    envelope ?? throw new ArgumentNullException(nameof(envelope)),
                    _sender);
            }
            else
            {
                _ = FakePrivateProtocol.Decode(encoded, _sender);
            }
            frame = GC.AllocateUninitializedArray<byte>(encoded.Length + 1);
            encoded.CopyTo(frame, 0);
            frame[^1] = (byte)'\n';

            try
            {
                if (failAfterFirstByte)
                {
                    await _stream.WriteAsync(frame.AsMemory(0, 1), cancellationToken)
                        .ConfigureAwait(false);
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    throw new IOException(
                        "Injected ambiguous private writer failure after one byte.");
                }
                await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _terminalFailure = exception;
                throw;
            }
        }
        finally
        {
            if (gateAcquired) _writeGate.Release();
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            if (frame is not null) CryptographicOperations.ZeroMemory(frame);
        }
    }
}
