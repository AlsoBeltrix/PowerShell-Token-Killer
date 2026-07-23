using System.Text.Json;

namespace PtkMcpServer.Worker;

internal sealed record WorkerOperationRequest(
    long RequestId,
    long Generation,
    DateTimeOffset DeadlineUtc,
    string Operation,
    JsonElement Arguments);

internal sealed record WorkerOperationCancel(
    long RequestId,
    long Generation);

internal enum WorkerOperationStatus
{
    Completed,
    Failed,
    Canceled,
    TimedOut,
}

internal sealed record WorkerOperationResponse(
    long RequestId,
    long Generation,
    WorkerOperationStatus Status,
    JsonElement? Result,
    string? DetailCode)
{
    internal static WorkerOperationResponse Completed(
        long requestId,
        long generation,
        JsonElement result) =>
        new(requestId, generation, WorkerOperationStatus.Completed, result.Clone(), null);

    internal static WorkerOperationResponse Failed(
        long requestId,
        long generation,
        string detailCode) =>
        new(requestId, generation, WorkerOperationStatus.Failed, null, detailCode);

    internal static WorkerOperationResponse Canceled(
        long requestId,
        long generation,
        string detailCode) =>
        new(requestId, generation, WorkerOperationStatus.Canceled, null, detailCode);

    internal static WorkerOperationResponse TimedOut(
        long requestId,
        long generation,
        string detailCode) =>
        new(requestId, generation, WorkerOperationStatus.TimedOut, null, detailCode);
}

/// <summary>
/// Strict outer DTO codec for ordinary worker operation transport. Concrete
/// operation codecs own the contents of arguments and results.
/// </summary>
internal static class WorkerOperationProtocol
{
    internal const int MaximumCodeLength = 64;

    internal static WorkerEnvelope CreateRequestEnvelope(
        Guid workerBootId,
        long requestId,
        long generation,
        DateTimeOffset deadlineUtc,
        string operation,
        JsonElement arguments)
    {
        var envelope = new WorkerEnvelope(
            WorkerProtocol.Version,
            WorkerMessageKind.Request,
            workerBootId,
            requestId,
            JsonSerializer.SerializeToElement(new
            {
                generation,
                deadlineUnixTimeMilliseconds = deadlineUtc.ToUnixTimeMilliseconds(),
                operation,
                arguments,
            }));
        _ = ParseRequest(envelope, workerBootId, generation);
        return envelope;
    }

    internal static WorkerEnvelope CreateCancelEnvelope(
        Guid workerBootId,
        long requestId,
        long generation)
    {
        var envelope = new WorkerEnvelope(
            WorkerProtocol.Version,
            WorkerMessageKind.Cancel,
            workerBootId,
            requestId,
            JsonSerializer.SerializeToElement(new { generation }));
        _ = ParseCancel(envelope, workerBootId, generation);
        return envelope;
    }

    internal static WorkerOperationRequest ParseRequest(
        WorkerEnvelope envelope,
        Guid expectedBootId,
        long expectedGeneration)
    {
        ValidateEnvelopeIdentity(
            envelope,
            WorkerMessageKind.Request,
            expectedBootId,
            expectedGeneration);

        long? generation = null;
        long? deadlineUnixTimeMilliseconds = null;
        string? operation = null;
        JsonElement? arguments = null;
        foreach (var property in EnumerateUniqueFields(envelope.Payload))
        {
            switch (property.Name)
            {
                case "generation":
                    generation = PositiveInt64(property.Value, "generation");
                    break;
                case "deadlineUnixTimeMilliseconds":
                    deadlineUnixTimeMilliseconds = PositiveInt64(
                        property.Value,
                        "deadlineUnixTimeMilliseconds");
                    break;
                case "operation":
                    operation = Code(property.Value, "operation");
                    break;
                case "arguments":
                    if (property.Value.ValueKind != JsonValueKind.Object)
                        throw InvalidField("arguments");
                    arguments = property.Value.Clone();
                    break;
                default:
                    throw UnknownField("request", property.Name);
            }
        }

        if (generation is null || deadlineUnixTimeMilliseconds is null ||
            operation is null || arguments is null)
        {
            throw MissingField("request");
        }
        RequireGeneration(generation.Value, expectedGeneration);

        DateTimeOffset deadlineUtc;
        try
        {
            deadlineUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                deadlineUnixTimeMilliseconds.Value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new WorkerProtocolException(
                "invalid_operation_field",
                "Worker operation deadline is outside the supported UTC range.",
                exception);
        }

        return new WorkerOperationRequest(
            envelope.RequestId!.Value,
            generation.Value,
            deadlineUtc,
            operation,
            arguments.Value);
    }

    internal static WorkerOperationCancel ParseCancel(
        WorkerEnvelope envelope,
        Guid expectedBootId,
        long expectedGeneration)
    {
        ValidateEnvelopeIdentity(
            envelope,
            WorkerMessageKind.Cancel,
            expectedBootId,
            expectedGeneration);

        long? generation = null;
        foreach (var property in EnumerateUniqueFields(envelope.Payload))
        {
            if (property.Name != "generation")
                throw UnknownField("cancel", property.Name);
            generation = PositiveInt64(property.Value, "generation");
        }
        if (generation is null)
            throw MissingField("cancel");
        RequireGeneration(generation.Value, expectedGeneration);

        return new WorkerOperationCancel(envelope.RequestId!.Value, generation.Value);
    }

    internal static WorkerEnvelope CreateResponseEnvelope(
        Guid workerBootId,
        WorkerOperationResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (workerBootId == Guid.Empty)
            throw InvalidField("workerBootId");
        if (response.RequestId <= 0)
            throw InvalidField("requestId");
        if (response.Generation <= 0)
            throw InvalidField("generation");

        JsonElement payload;
        if (response.Status == WorkerOperationStatus.Completed)
        {
            if (response.Result is not { ValueKind: JsonValueKind.Object } result ||
                response.DetailCode is not null)
            {
                throw InvalidResponse();
            }
            payload = JsonSerializer.SerializeToElement(new
            {
                generation = response.Generation,
                status = "completed",
                result,
            });
        }
        else
        {
            if (response.Result is not null || response.DetailCode is null ||
                !IsCode(response.DetailCode))
            {
                throw InvalidResponse();
            }
            payload = JsonSerializer.SerializeToElement(new
            {
                generation = response.Generation,
                status = StatusName(response.Status),
                detailCode = response.DetailCode,
            });
        }

        return new WorkerEnvelope(
            WorkerProtocol.Version,
            WorkerMessageKind.Response,
            workerBootId,
            response.RequestId,
            payload);
    }

    internal static WorkerOperationResponse ParseResponse(
        WorkerEnvelope envelope,
        Guid expectedBootId,
        long expectedGeneration)
    {
        ValidateEnvelopeIdentity(
            envelope,
            WorkerMessageKind.Response,
            expectedBootId,
            expectedGeneration);

        long? generation = null;
        WorkerOperationStatus? status = null;
        JsonElement? result = null;
        string? detailCode = null;
        foreach (var property in EnumerateUniqueFields(envelope.Payload))
        {
            switch (property.Name)
            {
                case "generation":
                    generation = PositiveInt64(property.Value, "generation");
                    break;
                case "status":
                    if (property.Value.ValueKind != JsonValueKind.String ||
                        !TryParseStatus(property.Value.GetString(), out var parsedStatus))
                    {
                        throw InvalidField("status");
                    }
                    status = parsedStatus;
                    break;
                case "result":
                    if (property.Value.ValueKind != JsonValueKind.Object)
                        throw InvalidField("result");
                    result = property.Value.Clone();
                    break;
                case "detailCode":
                    detailCode = Code(property.Value, "detailCode");
                    break;
                default:
                    throw UnknownField("response", property.Name);
            }
        }

        if (generation is null || status is null)
            throw MissingField("response");
        RequireGeneration(generation.Value, expectedGeneration);
        if (status == WorkerOperationStatus.Completed)
        {
            if (result is null || detailCode is not null)
                throw InvalidResponse();
        }
        else if (result is not null || detailCode is null)
        {
            throw InvalidResponse();
        }

        return new WorkerOperationResponse(
            envelope.RequestId!.Value,
            generation.Value,
            status.Value,
            result,
            detailCode);
    }

    private static void ValidateEnvelopeIdentity(
        WorkerEnvelope envelope,
        WorkerMessageKind expectedKind,
        Guid expectedBootId,
        long expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (expectedBootId == Guid.Empty)
            throw new ArgumentException("Expected worker boot ID cannot be empty.", nameof(expectedBootId));
        if (expectedGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        if (envelope.ProtocolVersion != WorkerProtocol.Version)
        {
            throw new WorkerProtocolException(
                "unknown_version",
                "Worker operation envelope uses an unsupported protocol version.");
        }
        if (envelope.Kind != expectedKind)
        {
            throw new WorkerProtocolException(
                "operation_kind_mismatch",
                $"Expected worker operation kind '{expectedKind}'.");
        }
        if (envelope.WorkerBootId != expectedBootId)
        {
            throw new WorkerProtocolException(
                "worker_boot_mismatch",
                "Worker operation frame targets a different worker boot.");
        }
        if (envelope.RequestId is not > 0)
        {
            throw new WorkerProtocolException(
                "request_id_required",
                "Worker operation frame requires a positive request ID.");
        }
        if (envelope.Payload.ValueKind != JsonValueKind.Object)
            throw InvalidField("payload");
    }

    private static void RequireGeneration(long actual, long expected)
    {
        if (actual != expected)
        {
            throw new WorkerProtocolException(
                "worker_generation_mismatch",
                "Worker operation frame targets a different generation.");
        }
    }

    private static IEnumerable<JsonProperty> EnumerateUniqueFields(JsonElement payload)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new WorkerProtocolException(
                    "duplicate_field",
                    $"Worker operation payload contains duplicate field '{property.Name}'.");
            }
            yield return property;
        }
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

    private static string Code(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } parsed || !IsCode(parsed))
        {
            throw InvalidField(field);
        }
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
                character is >= '0' and <= '9' || character == '_')
            {
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool TryParseStatus(string? value, out WorkerOperationStatus status)
    {
        status = value switch
        {
            "completed" => WorkerOperationStatus.Completed,
            "failed" => WorkerOperationStatus.Failed,
            "canceled" => WorkerOperationStatus.Canceled,
            "timed_out" => WorkerOperationStatus.TimedOut,
            _ => default,
        };
        return value is "completed" or "failed" or "canceled" or "timed_out";
    }

    private static string StatusName(WorkerOperationStatus status) => status switch
    {
        WorkerOperationStatus.Failed => "failed",
        WorkerOperationStatus.Canceled => "canceled",
        WorkerOperationStatus.TimedOut => "timed_out",
        _ => throw InvalidResponse(),
    };

    private static WorkerProtocolException InvalidField(string field) =>
        new(
            "invalid_operation_field",
            $"Worker operation field '{field}' is invalid.");

    private static WorkerProtocolException UnknownField(string payload, string field) =>
        new(
            "unknown_operation_field",
            $"Worker {payload} payload contains unknown field '{field}'.");

    private static WorkerProtocolException MissingField(string payload) =>
        new(
            "missing_operation_field",
            $"Worker {payload} payload is missing a required field.");

    private static WorkerProtocolException InvalidResponse() =>
        new(
            "invalid_operation_response",
            "Worker operation response fields do not match its status.");
}
