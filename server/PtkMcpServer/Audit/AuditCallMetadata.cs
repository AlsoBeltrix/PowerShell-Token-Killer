using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace PtkMcpServer.Audit;

internal sealed record AuditClientContext(
    string? ClientName = null,
    string? ClientVersion = null,
    string? ClientSessionId = null);

internal sealed record AuditOperationProfile(
    int MaximumRecordSlots,
    bool RequiresScriptEvidence,
    bool MayHaveSideEffects)
{
    internal long MaximumReservationBytes(int maximumRecordBytes)
    {
        if (maximumRecordBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumRecordBytes));
        return checked((long)MaximumRecordSlots * maximumRecordBytes);
    }
}

internal sealed record AuditCallMetadata(
    AuditActor Actor,
    AuditRequest Request,
    AuditOperationProfile OperationProfile);

/// <summary>
/// Pure validation and normalization at the MCP call boundary. It never
/// persists, executes, logs, or includes submitted script text in core-event
/// metadata or failure text.
/// </summary>
internal static class AuditCallMetadataCapture
{
    private const int MaximumScriptUtf8Bytes = 131_072;
    private const int MaximumClientScalars = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> InvokeFields =
        new(["script", "raw", "route", "timeoutSeconds", "session"], StringComparer.Ordinal);
    private static readonly HashSet<string> OutputFields =
        new(["handle", "action", "offset", "maxBytes", "pattern", "session"], StringComparer.Ordinal);
    private static readonly HashSet<string> StateFields =
        new(["listAvailable", "session"], StringComparer.Ordinal);
    private static readonly HashSet<string> ResetFields =
        new(["session"], StringComparer.Ordinal);
    private static readonly HashSet<string> SessionFields =
        new(["action", "name"], StringComparer.Ordinal);

    internal static bool TryCapture(
        CallToolRequestParams call,
        AuditClientContext client,
        TimeSpan defaultTimeout,
        TimeSpan maximumTimeout,
        DateTimeOffset utcNow,
        out AuditCallMetadata? metadata,
        out string? exactSubmittedScript,
        out string? sanitizedFailure,
        AuditOutputRequestProtector? outputProtector = null)
    {
        metadata = null;
        exactSubmittedScript = null;
        sanitizedFailure = null;

        if (call is null)
            return Fail("audit_boundary_invalid: request is missing", out sanitizedFailure);
        if (client is null)
            return Fail("audit_boundary_invalid: client context is missing", out sanitizedFailure);
        if (!IsUtc(utcNow))
            return Fail("audit_boundary_invalid: boundary clock is not UTC", out sanitizedFailure);
        if (!TryValidateTimeout(defaultTimeout, "default timeout", out sanitizedFailure) ||
            !TryValidateTimeout(maximumTimeout, "maximum timeout", out sanitizedFailure))
        {
            return false;
        }

        if (!TryCaptureActor(client, out var actor, out sanitizedFailure))
            return false;

        var arguments = call.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var providedFields = arguments.Keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (providedFields.Length > 64)
            return Fail("audit_boundary_invalid: too many argument fields", out sanitizedFailure);

        switch (call.Name)
        {
            case "ptk_invoke":
                if (!TryCaptureInvoke(
                        arguments,
                        providedFields,
                        defaultTimeout,
                        maximumTimeout,
                        utcNow,
                        actor,
                        out metadata,
                        out exactSubmittedScript,
                        out sanitizedFailure))
                {
                    exactSubmittedScript = null;
                    return false;
                }
                return true;

            case "ptk_output":
                return TryCaptureOutput(
                    arguments,
                    providedFields,
                    actor,
                    outputProtector,
                    out metadata,
                    out sanitizedFailure);

            case "ptk_state":
                return TryCaptureState(arguments, providedFields, actor, out metadata, out sanitizedFailure);

            case "ptk_reset":
                return TryCaptureReset(arguments, providedFields, actor, out metadata, out sanitizedFailure);

            case "ptk_session":
                return TryCaptureSession(arguments, providedFields, actor, out metadata, out sanitizedFailure);

            default:
                return Fail("audit_boundary_invalid: unknown tool", out sanitizedFailure);
        }
    }

    private static bool TryCaptureInvoke(
        IDictionary<string, JsonElement> arguments,
        string[] providedFields,
        TimeSpan defaultTimeout,
        TimeSpan maximumTimeout,
        DateTimeOffset utcNow,
        AuditActor actor,
        out AuditCallMetadata? metadata,
        out string? exactSubmittedScript,
        out string? failure)
    {
        metadata = null;
        exactSubmittedScript = null;
        failure = null;

        if (!TryRejectUnknownFields(arguments, InvokeFields, "ptk_invoke", out failure))
            return false;
        if (!TryRequiredString(arguments, "script", out var script, out failure))
            return false;
        if (!TryStrictUtf8Length(script, MaximumScriptUtf8Bytes))
            return Fail("audit_boundary_invalid: ptk_invoke.arguments.script is not representable", out failure);
        if (!TryOptionalBoolean(arguments, "raw", defaultValue: false, out var raw, out failure) ||
            !TryOptionalInt32(arguments, "timeoutSeconds", defaultValue: 0, out var timeoutSeconds, out failure) ||
            !TryOptionalSession(arguments, "session", "default", out var session, out failure))
        {
            return false;
        }

        string route;
        if (arguments.TryGetValue("route", out var routeElement))
        {
            if (routeElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return Fail("audit_boundary_invalid: ptk_invoke.arguments.route has the wrong JSON kind", out failure);
            route = NormalizeRoute(routeElement.ValueKind == JsonValueKind.Null ? null : routeElement.GetString());
        }
        else
        {
            route = "auto";
        }

        var budget = timeoutSeconds > 0
            ? Min(TimeSpan.FromSeconds(timeoutSeconds), maximumTimeout)
            : defaultTimeout;
        if (!TryMilliseconds(budget, out var timeoutMilliseconds) ||
            !TryAdd(utcNow, budget, out var deadlineUtc))
        {
            return Fail("audit_boundary_invalid: ptk_invoke timeout is not representable", out failure);
        }

        var request = BaseRequest("ptk_invoke", "invoke", providedFields) with
        {
            TimeoutMs = timeoutMilliseconds,
            DeadlineUtc = deadlineUtc,
            Route = route,
            Raw = raw,
            SessionRequested = session,
        };
        var profile = new AuditOperationProfile(
            MaximumRecordSlots: 11,
            RequiresScriptEvidence: true,
            MayHaveSideEffects: true);

        exactSubmittedScript = script;
        metadata = new AuditCallMetadata(actor, request, profile);
        return true;
    }

    private static bool TryCaptureState(
        IDictionary<string, JsonElement> arguments,
        string[] providedFields,
        AuditActor actor,
        out AuditCallMetadata? metadata,
        out string? failure)
    {
        metadata = null;
        failure = null;
        if (!TryRejectUnknownFields(arguments, StateFields, "ptk_state", out failure) ||
            !TryOptionalBoolean(arguments, "listAvailable", false, out var listAvailable, out failure) ||
            !TryOptionalSession(arguments, "session", "default", out var session, out failure))
        {
            return false;
        }

        metadata = new AuditCallMetadata(
            actor,
            BaseRequest("ptk_state", "state", providedFields) with
            {
                ListAvailable = listAvailable,
                SessionRequested = session,
            },
            new AuditOperationProfile(5, RequiresScriptEvidence: false, MayHaveSideEffects: true));
        return true;
    }

    private static bool TryCaptureReset(
        IDictionary<string, JsonElement> arguments,
        string[] providedFields,
        AuditActor actor,
        out AuditCallMetadata? metadata,
        out string? failure)
    {
        metadata = null;
        failure = null;
        if (!TryRejectUnknownFields(arguments, ResetFields, "ptk_reset", out failure) ||
            !TryOptionalSession(arguments, "session", "default", out var session, out failure))
        {
            return false;
        }

        metadata = new AuditCallMetadata(
            actor,
            BaseRequest("ptk_reset", "reset", providedFields) with { SessionRequested = session },
            new AuditOperationProfile(4, RequiresScriptEvidence: false, MayHaveSideEffects: true));
        return true;
    }

    private static bool TryCaptureSession(
        IDictionary<string, JsonElement> arguments,
        string[] providedFields,
        AuditActor actor,
        out AuditCallMetadata? metadata,
        out string? failure)
    {
        metadata = null;
        failure = null;
        if (!TryRejectUnknownFields(arguments, SessionFields, "ptk_session", out failure) ||
            !TryRequiredString(arguments, "action", out var action, out failure))
        {
            return false;
        }

        if (action is not ("list" or "open" or "close"))
            return Fail("audit_boundary_invalid: ptk_session.arguments.action is unsupported", out failure);

        var hasName = arguments.TryGetValue("name", out var nameElement);
        string? name = null;
        if (hasName && nameElement.ValueKind != JsonValueKind.Null)
        {
            if (nameElement.ValueKind != JsonValueKind.String)
                return Fail("audit_boundary_invalid: ptk_session.arguments.name has the wrong JSON kind", out failure);

            name = nameElement.GetString();
            if (name is null || !IsSessionName(name))
                return Fail("audit_boundary_invalid: ptk_session.arguments.name is invalid", out failure);
        }

        if (action == "list" && hasName)
            return Fail("audit_boundary_invalid: ptk_session list does not accept name", out failure);
        if (action is "open" or "close" && name is null)
            return Fail("audit_boundary_invalid: ptk_session.arguments.name is required for this action", out failure);

        metadata = new AuditCallMetadata(
            actor,
            BaseRequest("ptk_session", action, providedFields) with { SessionRequested = name },
            new AuditOperationProfile(
                action == "list" ? 2 : 4,
                RequiresScriptEvidence: false,
                MayHaveSideEffects: action is "open" or "close"));
        return true;
    }

    private static bool TryCaptureOutput(
        IDictionary<string, JsonElement> arguments,
        string[] providedFields,
        AuditActor actor,
        AuditOutputRequestProtector? protector,
        out AuditCallMetadata? metadata,
        out string? failure)
    {
        metadata = null;
        failure = null;
        if (!TryRejectUnknownFields(arguments, OutputFields, "ptk_output", out failure))
            return false;

        var action = "read";
        if (arguments.TryGetValue("action", out var actionElement))
        {
            if (actionElement.ValueKind == JsonValueKind.String)
                action = actionElement.GetString()!.ToLowerInvariant();
            else if (actionElement.ValueKind != JsonValueKind.Null)
                return Fail("audit_boundary_invalid: ptk_output.arguments.action has the wrong JSON kind", out failure);
        }
        if (action is not ("read" or "search" or "status" or "list"))
            return Fail("audit_boundary_invalid: ptk_output.arguments.action is unsupported", out failure);

        if (action == "list")
        {
            // Mirror the tool's own semantics (OutputTool's list branch):
            // the schema's defaults are acceptable as explicit values, and
            // only a NON-default inapplicable value is rejected. Rejecting
            // on key presence narrowed the published MCP contract for
            // clients that serialize optional defaults (cr2-3).
            if (!IsAbsentOrNull(arguments, "handle") ||
                !IsAbsentOrDefaultNumber(arguments, "offset", 0) ||
                !IsAbsentOrDefaultNumber(arguments, "maxBytes", OutputStore.DefaultReadBytes) ||
                !IsAbsentOrNull(arguments, "pattern"))
            {
                return Fail("audit_boundary_invalid: ptk_output list contains an inapplicable argument", out failure);
            }

            string? session = null;
            if (arguments.TryGetValue("session", out var sessionElement) &&
                sessionElement.ValueKind != JsonValueKind.Null)
            {
                if (sessionElement.ValueKind != JsonValueKind.String)
                {
                    return Fail("audit_boundary_invalid: ptk_output.arguments.session has the wrong JSON kind", out failure);
                }

                session = sessionElement.GetString();
                if (session is null || !IsSessionName(session))
                {
                    return Fail("audit_boundary_invalid: ptk_output.arguments.session is invalid", out failure);
                }
            }

            metadata = new AuditCallMetadata(
                actor,
                BaseRequest("ptk_output", action, providedFields) with
                {
                    SessionRequested = session,
                },
                new AuditOperationProfile(
                    MaximumRecordSlots: 3,
                    RequiresScriptEvidence: false,
                    MayHaveSideEffects: false));
            return true;
        }

        if (!IsAbsentOrNull(arguments, "session"))
            return Fail("audit_boundary_invalid: ptk_output action contains an inapplicable session", out failure);
        if (protector is null)
            return Fail("audit_boundary_invalid: output request protection is unavailable", out failure);
        if (!TryRequiredString(arguments, "handle", out var handle, out failure) ||
            !TryStrictUtf8Length(handle, 256))
        {
            return failure is not null
                ? false
                : Fail("audit_boundary_invalid: ptk_output.arguments.handle is not representable", out failure);
        }

        long offset = 0;
        if (arguments.TryGetValue("offset", out var offsetElement))
        {
            if (offsetElement.ValueKind != JsonValueKind.Number ||
                !offsetElement.TryGetInt64(out offset) ||
                offset < 0)
            {
                return Fail("audit_boundary_invalid: ptk_output.arguments.offset must be a nonnegative int64", out failure);
            }
        }

        var maximumBytes = OutputStore.DefaultReadBytes;
        if (arguments.TryGetValue("maxBytes", out var maximumElement))
        {
            if (maximumElement.ValueKind != JsonValueKind.Number ||
                !maximumElement.TryGetInt32(out maximumBytes) ||
                maximumBytes is < 1 or > OutputStore.MaximumReadBytes)
            {
                return Fail(
                    $"audit_boundary_invalid: ptk_output.arguments.maxBytes must be 1..{OutputStore.MaximumReadBytes}",
                    out failure);
            }
        }

        string? pattern = null;
        if (arguments.TryGetValue("pattern", out var patternElement))
        {
            if (patternElement.ValueKind == JsonValueKind.String)
                pattern = patternElement.GetString();
            else if (patternElement.ValueKind != JsonValueKind.Null)
                return Fail("audit_boundary_invalid: ptk_output.arguments.pattern has the wrong JSON kind", out failure);
        }

        if (action == "status")
        {
            // Same default-tolerant rule as the list branch (cr2-3): the
            // audit boundary stays stricter than the tool (which ignores
            // these fields for status) by rejecting non-default values,
            // but never rejects the schema's own defaults.
            if (!IsAbsentOrDefaultNumber(arguments, "offset", 0) ||
                !IsAbsentOrDefaultNumber(arguments, "maxBytes", OutputStore.DefaultReadBytes) ||
                !IsAbsentOrNull(arguments, "pattern"))
            {
                return Fail("audit_boundary_invalid: ptk_output status contains an inapplicable argument", out failure);
            }
        }
        else if (action == "read")
        {
            if (!IsAbsentOrNull(arguments, "pattern"))
                return Fail("audit_boundary_invalid: ptk_output read contains an inapplicable argument", out failure);
        }
        else
        {
            if (pattern is null || pattern.Length == 0 ||
                !TryStrictUtf8Length(pattern, OutputStore.MaximumPatternBytes))
            {
                return Fail("audit_boundary_invalid: ptk_output search requires a bounded pattern", out failure);
            }
            if (StrictUtf8.GetByteCount(pattern) > maximumBytes)
            {
                return Fail(
                    "audit_boundary_invalid: ptk_output search maxBytes cannot contain its pattern",
                    out failure);
            }
        }

        string handleDigest;
        string? patternFingerprint;
        try
        {
            handleDigest = protector.HandleDigest(handle);
            patternFingerprint = pattern is null
                ? null
                : protector.PatternFingerprint(pattern);
        }
        catch (Exception exception) when (exception is EncoderFallbackException or ObjectDisposedException)
        {
            return Fail("audit_boundary_invalid: ptk_output sensitive fields are not representable", out failure);
        }

        var request = BaseRequest("ptk_output", action, providedFields) with
        {
            Offset = action == "status" ? null : offset,
            MaxBytes = action == "status" ? null : maximumBytes,
            PatternFingerprint = patternFingerprint,
            OutputHandleDigest = handleDigest,
        };
        metadata = new AuditCallMetadata(
            actor,
            request,
            new AuditOperationProfile(
                MaximumRecordSlots: 3,
                RequiresScriptEvidence: false,
                MayHaveSideEffects: false));
        return true;
    }

    private static AuditRequest BaseRequest(string tool, string? action, IReadOnlyList<string> providedFields) => new()
    {
        Tool = tool,
        Action = action,
        ProvidedFields = providedFields,
    };

    private static bool TryCaptureActor(
        AuditClientContext client,
        out AuditActor actor,
        out string? failure)
    {
        actor = null!;
        failure = null;
        if (!TryClientText(client.ClientName, "client name", out failure) ||
            !TryClientText(client.ClientVersion, "client version", out failure) ||
            !TryClientText(client.ClientSessionId, "client session id", out failure))
        {
            return false;
        }

        var asserted = client.ClientName is not null ||
                       client.ClientVersion is not null ||
                       client.ClientSessionId is not null;
        actor = new AuditActor
        {
            Transport = "mcp_stdio",
            ClientName = client.ClientName,
            ClientVersion = client.ClientVersion,
            ClientSessionId = client.ClientSessionId,
            AttributionStrength = asserted ? "client_asserted" : "transport_only",
        };
        return true;
    }

    private static bool TryRejectUnknownFields(
        IDictionary<string, JsonElement> arguments,
        HashSet<string> allowed,
        string tool,
        out string? failure)
    {
        foreach (var key in arguments.Keys)
        {
            if (!allowed.Contains(key))
                return Fail($"audit_boundary_invalid: {tool} contains an unknown argument field", out failure);
        }
        failure = null;
        return true;
    }

    private static bool TryRequiredString(
        IDictionary<string, JsonElement> arguments,
        string name,
        out string value,
        out string? failure)
    {
        value = string.Empty;
        if (!arguments.TryGetValue(name, out var element))
            return Fail($"audit_boundary_invalid: required argument {name} is missing", out failure);
        if (element.ValueKind != JsonValueKind.String)
            return Fail($"audit_boundary_invalid: argument {name} has the wrong JSON kind", out failure);
        value = element.GetString()!;
        failure = null;
        return true;
    }

    private static bool TryOptionalBoolean(
        IDictionary<string, JsonElement> arguments,
        string name,
        bool defaultValue,
        out bool value,
        out string? failure)
    {
        if (!arguments.TryGetValue(name, out var element))
        {
            value = defaultValue;
            failure = null;
            return true;
        }
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            value = default;
            return Fail($"audit_boundary_invalid: argument {name} has the wrong JSON kind", out failure);
        }
        value = element.GetBoolean();
        failure = null;
        return true;
    }

    private static bool TryOptionalInt32(
        IDictionary<string, JsonElement> arguments,
        string name,
        int defaultValue,
        out int value,
        out string? failure)
    {
        if (!arguments.TryGetValue(name, out var element))
        {
            value = defaultValue;
            failure = null;
            return true;
        }
        value = default;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
            return Fail($"audit_boundary_invalid: argument {name} must be an int32", out failure);
        failure = null;
        return true;
    }

    private static bool TryOptionalSession(
        IDictionary<string, JsonElement> arguments,
        string name,
        string defaultValue,
        out string value,
        out string? failure)
    {
        if (!arguments.TryGetValue(name, out var element))
        {
            value = defaultValue;
            failure = null;
            return true;
        }

        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
            return Fail($"audit_boundary_invalid: argument {name} has the wrong JSON kind", out failure);

        value = element.GetString()!;
        if (!IsSessionName(value))
            return Fail($"audit_boundary_invalid: argument {name} is not a valid session name", out failure);

        failure = null;
        return true;
    }

    private static bool TryClientText(string? value, string field, out string? failure)
    {
        if (value is null)
        {
            failure = null;
            return true;
        }
        if (value.Length == 0 || !TryScalarCount(value, out var scalars) || scalars > MaximumClientScalars)
            return Fail($"audit_boundary_invalid: {field} is not representable", out failure);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
                return Fail($"audit_boundary_invalid: {field} is not representable", out failure);
        }
        failure = null;
        return true;
    }

    private static bool TryStrictUtf8Length(string value, int maximumBytes)
    {
        try
        {
            return StrictUtf8.GetByteCount(value) <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryScalarCount(string value, out int count)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
            count = value.EnumerateRunes().Count();
            return true;
        }
        catch (EncoderFallbackException)
        {
            count = 0;
            return false;
        }
    }

    private static string NormalizeRoute(string? route) => route?.ToLowerInvariant() switch
    {
        "pwsh" => "pwsh",
        "rtk" => "rtk",
        _ => "auto",
    };

    private static bool IsSessionName(string value)
    {
        if (value.Length is < 1 or > 64 ||
            !((value[0] >= 'a' && value[0] <= 'z') ||
              (value[0] >= '0' && value[0] <= '9')))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if ((character < 'a' || character > 'z') &&
                (character < '0' || character > '9') &&
                character is not ('_' or '-' or '.'))
            {
                return false;
            }
        }
        return true;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static bool TryValidateTimeout(TimeSpan value, string name, out string? failure)
    {
        if (value <= TimeSpan.Zero || !TryMilliseconds(value, out _))
            return Fail($"audit_boundary_invalid: {name} is not representable", out failure);
        failure = null;
        return true;
    }

    private static bool TryMilliseconds(TimeSpan value, out long milliseconds)
    {
        if (value.Ticks < 0 || value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            milliseconds = 0;
            return false;
        }
        milliseconds = value.Ticks / TimeSpan.TicksPerMillisecond;
        return true;
    }

    private static bool TryAdd(DateTimeOffset value, TimeSpan duration, out DateTimeOffset result)
    {
        try
        {
            result = value + duration;
            return IsUtc(result);
        }
        catch (ArgumentOutOfRangeException)
        {
            result = default;
            return false;
        }
    }

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static bool Fail(string message, out string? failure)
    {
        failure = message;
        return false;
    }

    private static bool IsAbsentOrNull(
        IDictionary<string, JsonElement> arguments,
        string name) =>
        !arguments.TryGetValue(name, out var value) ||
        value.ValueKind == JsonValueKind.Null;

    private static bool IsAbsentOrDefaultNumber(
        IDictionary<string, JsonElement> arguments,
        string name,
        long defaultValue) =>
        !arguments.TryGetValue(name, out var value) ||
        value.ValueKind == JsonValueKind.Null ||
        (value.ValueKind == JsonValueKind.Number &&
         value.TryGetInt64(out var number) &&
         number == defaultValue);
}
