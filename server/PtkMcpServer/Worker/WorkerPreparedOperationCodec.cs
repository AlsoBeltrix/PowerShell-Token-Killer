using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PtkMcpServer.Worker;

internal sealed record WorkerInvokePreparePayload(
    Guid PlanId,
    long Generation,
    DateTimeOffset DeadlineUtc,
    string ScriptDigest,
    WorkerInvokeArguments Arguments);

internal sealed record WorkerPreparedCorrelation(
    Guid PlanId,
    string ScriptDigest,
    long Generation,
    DateTimeOffset DeadlineUtc);

internal sealed record WorkerCommitPayload(
    Guid PlanId,
    string ScriptDigest,
    long Generation,
    DateTimeOffset DeadlineUtc);

internal sealed record WorkerAbortPayload(
    Guid PlanId,
    string ScriptDigest,
    long Generation,
    DateTimeOffset DeadlineUtc);

internal sealed record WorkerPreparedPlanDescriptor(
    Guid PlanId,
    Guid WorkerBootId,
    string ScriptDigest,
    long Generation,
    DateTimeOffset DeadlineUtc,
    ExecutionDomain? Domain,
    RequestedExecutionRoute RequestedRoute,
    ExecutionPath EffectiveRoute,
    PreExecutionValidation PreExecutionValidation,
    ResolutionContext ResolutionContext,
    OutputProvenance OutputProvenance,
    ImmutableArray<ExecutionPath> PermittedFallbacks,
    ExecutionFallbackReason? FallbackReason,
    string? WorkingDirectoryDigest,
    string? RtkBinaryDigest,
    string? BashBinaryDigest,
    string? OutputShapingRtkBinaryDigest);

internal enum WorkerPreparedCorrelationMatch
{
    Mismatch,
    Match,
}

/// <summary>
/// Strict value codecs for the deliberately unwired Slice 7h prepared-operation
/// correlation payloads. These values are not bound to envelopes, a worker
/// reservation, or a session runtime.
/// </summary>
internal static class WorkerPreparedOperationCodec
{
    private const int Sha256HexLength = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static WorkerInvokePreparePayload ParsePrepare(JsonElement payload)
    {
        var fields = ClosedObject(
            payload,
            "planId",
            "generation",
            "deadlineUnixTimeMilliseconds",
            "scriptDigest",
            "operation",
            "arguments");
        var planId = PlanId(Required(fields, "planId"));
        var generation = PositiveInt64(Required(fields, "generation"));
        var deadlineUtc = Deadline(Required(fields, "deadlineUnixTimeMilliseconds"));
        var scriptDigest = ScriptDigest(Required(fields, "scriptDigest"));
        var operation = StringField(Required(fields, "operation"));
        if (!string.Equals(
                operation,
                WorkerSessionOperationCodec.InvokeOperation,
                StringComparison.Ordinal))
        {
            throw UnsupportedOperation();
        }

        var invokeArguments = ParseInvokeArguments(Required(fields, "arguments"));

        RequireMatchingScriptDigest(invokeArguments.Script, scriptDigest);
        return new WorkerInvokePreparePayload(
            planId,
            generation,
            deadlineUtc,
            scriptDigest,
            invokeArguments);
    }

    internal static JsonElement CreatePrepare(WorkerInvokePreparePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var planId = PlanId(payload.PlanId);
        var generation = PositiveInt64(payload.Generation);
        var deadline = DeadlineMilliseconds(payload.DeadlineUtc);
        var scriptDigest = ScriptDigest(payload.ScriptDigest);
        var arguments = PrepareArguments(payload.Arguments, scriptDigest);

        return JsonSerializer.SerializeToElement(new
        {
            planId,
            generation,
            deadlineUnixTimeMilliseconds = deadline,
            scriptDigest,
            operation = WorkerSessionOperationCodec.InvokeOperation,
            arguments,
        });
    }

    internal static WorkerPreparedCorrelation ParsePreparedCorrelation(JsonElement payload)
    {
        var fields = ParseCorrelation(payload);
        return new WorkerPreparedCorrelation(
            fields.PlanId,
            fields.ScriptDigest,
            fields.Generation,
            fields.DeadlineUtc);
    }

    internal static JsonElement CreatePreparedCorrelation(
        WorkerPreparedCorrelation payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return CreateCorrelation(
            payload.PlanId,
            payload.ScriptDigest,
            payload.Generation,
            payload.DeadlineUtc);
    }

    internal static WorkerCommitPayload ParseCommit(JsonElement payload)
    {
        var fields = ParseCorrelation(payload);
        return new WorkerCommitPayload(
            fields.PlanId,
            fields.ScriptDigest,
            fields.Generation,
            fields.DeadlineUtc);
    }

    internal static JsonElement CreateCommit(WorkerCommitPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return CreateCorrelation(
            payload.PlanId,
            payload.ScriptDigest,
            payload.Generation,
            payload.DeadlineUtc);
    }

    internal static WorkerAbortPayload ParseAbort(JsonElement payload)
    {
        var fields = ParseCorrelation(payload);
        return new WorkerAbortPayload(
            fields.PlanId,
            fields.ScriptDigest,
            fields.Generation,
            fields.DeadlineUtc);
    }

    internal static JsonElement CreateAbort(WorkerAbortPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return CreateCorrelation(
            payload.PlanId,
            payload.ScriptDigest,
            payload.Generation,
            payload.DeadlineUtc);
    }

    internal static WorkerPreparedPlanDescriptor ProjectPreparedDescriptor(
        Guid workerBootId,
        WorkerInvokePreparePayload prepare,
        ExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(plan);
        _ = BootId(workerBootId);
        _ = CreatePrepare(prepare);
        if (!string.Equals(
                prepare.Arguments.Script,
                plan.OriginalScript,
                StringComparison.Ordinal))
        {
            throw ScriptDigestMismatch();
        }
        var requestedRoute = prepare.Arguments.Route switch
        {
            WorkerInvokeRoute.Auto => RequestedExecutionRoute.Auto,
            WorkerInvokeRoute.Pwsh => RequestedExecutionRoute.PowerShell,
            WorkerInvokeRoute.Rtk => RequestedExecutionRoute.Rtk,
            _ => throw InvalidField(),
        };
        if (requestedRoute != plan.RequestedRoute)
            throw InvalidField();

        var descriptor = new WorkerPreparedPlanDescriptor(
            prepare.PlanId,
            workerBootId,
            prepare.ScriptDigest,
            prepare.Generation,
            prepare.DeadlineUtc,
            plan.Domain,
            plan.RequestedRoute,
            plan.ExecutionPath,
            plan.PreExecutionValidation,
            plan.ResolutionContext,
            plan.OutputProvenance,
            plan.PermittedFallbacks,
            plan.FallbackReason,
            plan.WorkingDirectory is null
                ? null
                : ComputeUtf8Digest(plan.WorkingDirectory),
            RequiredIdentityDigest(plan.RtkExecutableIdentity?.AuditBinaryDigest,
                plan.RtkExecutableIdentity is not null),
            RequiredIdentityDigest(plan.BashExecutableIdentity?.BinaryDigest,
                plan.BashExecutableIdentity is not null),
            RequiredIdentityDigest(plan.OutputShapingRtkIdentity?.AuditBinaryDigest,
                plan.OutputShapingRtkIdentity is not null));
        _ = CreatePreparedDescriptor(descriptor);
        return descriptor;
    }

    internal static WorkerPreparedPlanDescriptor ParsePreparedDescriptor(
        JsonElement payload)
    {
        var fields = ClosedObject(
            payload,
            "planId",
            "workerBootId",
            "generation",
            "deadlineUnixTimeMilliseconds",
            "scriptDigest",
            "domain",
            "requestedRoute",
            "effectiveRoute",
            "preExecutionValidation",
            "resolutionContext",
            "outputProvenance",
            "permittedFallbacks",
            "fallbackReason",
            "workingDirectoryDigest",
            "rtkBinaryDigest",
            "bashBinaryDigest",
            "outputShapingRtkBinaryDigest",
            "descriptorDigest");
        var descriptor = new WorkerPreparedPlanDescriptor(
            PlanId(Required(fields, "planId")),
            BootId(Required(fields, "workerBootId")),
            ScriptDigest(Required(fields, "scriptDigest")),
            PositiveInt64(Required(fields, "generation")),
            Deadline(Required(fields, "deadlineUnixTimeMilliseconds")),
            ParseDomain(Required(fields, "domain")),
            ParseRequestedRoute(Required(fields, "requestedRoute")),
            ParseExecutionPath(Required(fields, "effectiveRoute")),
            ParsePreExecutionValidation(Required(fields, "preExecutionValidation")),
            ParseResolutionContext(Required(fields, "resolutionContext")),
            ParseOutputProvenance(Required(fields, "outputProvenance")),
            ParsePermittedFallbacks(Required(fields, "permittedFallbacks")),
            ParseFallbackReason(Required(fields, "fallbackReason")),
            ParseOptionalDigest(Required(fields, "workingDirectoryDigest")),
            ParseOptionalDigest(Required(fields, "rtkBinaryDigest")),
            ParseOptionalDigest(Required(fields, "bashBinaryDigest")),
            ParseOptionalDigest(Required(fields, "outputShapingRtkBinaryDigest")));
        var expectedDigest = ScriptDigest(Required(fields, "descriptorDigest"));
        var actualDigest = ComputePreparedDescriptorDigest(descriptor);
        if (!string.Equals(expectedDigest, actualDigest, StringComparison.Ordinal))
            throw DescriptorDigestMismatch();
        return descriptor;
    }

    internal static JsonElement CreatePreparedDescriptor(
        WorkerPreparedPlanDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var core = CreatePreparedDescriptorCore(descriptor);
        var descriptorDigest = ComputeUtf8Digest(core.GetRawText());
        var fields = core.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(new
        {
            planId = fields["planId"],
            workerBootId = fields["workerBootId"],
            generation = fields["generation"],
            deadlineUnixTimeMilliseconds = fields["deadlineUnixTimeMilliseconds"],
            scriptDigest = fields["scriptDigest"],
            domain = fields["domain"],
            requestedRoute = fields["requestedRoute"],
            effectiveRoute = fields["effectiveRoute"],
            preExecutionValidation = fields["preExecutionValidation"],
            resolutionContext = fields["resolutionContext"],
            outputProvenance = fields["outputProvenance"],
            permittedFallbacks = fields["permittedFallbacks"],
            fallbackReason = fields["fallbackReason"],
            workingDirectoryDigest = fields["workingDirectoryDigest"],
            rtkBinaryDigest = fields["rtkBinaryDigest"],
            bashBinaryDigest = fields["bashBinaryDigest"],
            outputShapingRtkBinaryDigest = fields["outputShapingRtkBinaryDigest"],
            descriptorDigest,
        });
    }

    internal static string ComputePreparedDescriptorDigest(
        WorkerPreparedPlanDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return ComputeUtf8Digest(CreatePreparedDescriptorCore(descriptor).GetRawText());
    }

    internal static WorkerPreparedCorrelationMatch ComparePreparedToPrepare(
        WorkerInvokePreparePayload? prepare,
        WorkerPreparedCorrelation? prepared)
    {
        if (prepare is null || prepared is null ||
            !IsValidPrepareValue(prepare) ||
            !IsValidCorrelationValue(
                prepared.PlanId,
                prepared.ScriptDigest,
                prepared.Generation,
                prepared.DeadlineUtc) ||
            prepare.PlanId != prepared.PlanId ||
            prepare.Generation != prepared.Generation ||
            prepare.DeadlineUtc != prepared.DeadlineUtc ||
            !string.Equals(
                prepare.ScriptDigest,
                prepared.ScriptDigest,
                StringComparison.Ordinal))
        {
            return WorkerPreparedCorrelationMatch.Mismatch;
        }
        return WorkerPreparedCorrelationMatch.Match;
    }

    internal static WorkerPreparedCorrelationMatch CompareCommitToPrepared(
        WorkerPreparedCorrelation? prepared,
        WorkerCommitPayload? commit)
    {
        if (prepared is null || commit is null ||
            !IsValidCorrelationValue(
                prepared.PlanId,
                prepared.ScriptDigest,
                prepared.Generation,
                prepared.DeadlineUtc) ||
            !IsValidCorrelationValue(
                commit.PlanId,
                commit.ScriptDigest,
                commit.Generation,
                commit.DeadlineUtc) ||
            prepared.PlanId != commit.PlanId ||
            prepared.Generation != commit.Generation ||
            prepared.DeadlineUtc != commit.DeadlineUtc ||
            !string.Equals(
                prepared.ScriptDigest,
                commit.ScriptDigest,
                StringComparison.Ordinal))
        {
            return WorkerPreparedCorrelationMatch.Mismatch;
        }
        return WorkerPreparedCorrelationMatch.Match;
    }

    internal static WorkerPreparedCorrelationMatch CompareAbortToPrepared(
        WorkerPreparedCorrelation? prepared,
        WorkerAbortPayload? abort)
    {
        if (prepared is null || abort is null ||
            !IsValidCorrelationValue(
                prepared.PlanId,
                prepared.ScriptDigest,
                prepared.Generation,
                prepared.DeadlineUtc) ||
            !IsValidCorrelationValue(
                abort.PlanId,
                abort.ScriptDigest,
                abort.Generation,
                abort.DeadlineUtc) ||
            prepared.PlanId != abort.PlanId ||
            prepared.Generation != abort.Generation ||
            prepared.DeadlineUtc != abort.DeadlineUtc ||
            !string.Equals(
                prepared.ScriptDigest,
                abort.ScriptDigest,
                StringComparison.Ordinal))
        {
            return WorkerPreparedCorrelationMatch.Mismatch;
        }
        return WorkerPreparedCorrelationMatch.Match;
    }

    private static JsonElement CreatePreparedDescriptorCore(
        WorkerPreparedPlanDescriptor descriptor)
    {
        var planId = PlanId(descriptor.PlanId);
        var workerBootId = BootId(descriptor.WorkerBootId);
        var generation = PositiveInt64(descriptor.Generation);
        var deadline = DeadlineMilliseconds(descriptor.DeadlineUtc);
        var scriptDigest = ScriptDigest(descriptor.ScriptDigest);
        var domain = DomainName(descriptor.Domain);
        var requestedRoute = RequestedRouteName(descriptor.RequestedRoute);
        var effectiveRoute = ExecutionPathName(descriptor.EffectiveRoute);
        var preExecutionValidation =
            PreExecutionValidationName(descriptor.PreExecutionValidation);
        var resolutionContext = ResolutionContextName(descriptor.ResolutionContext);
        var outputProvenance = OutputProvenanceName(descriptor.OutputProvenance);
        var permittedFallbacks = PermittedFallbackNames(
            descriptor.PermittedFallbacks);
        var fallbackReason = FallbackReasonName(descriptor.FallbackReason);
        var workingDirectoryDigest = OptionalDigest(descriptor.WorkingDirectoryDigest);
        var rtkBinaryDigest = OptionalDigest(descriptor.RtkBinaryDigest);
        var bashBinaryDigest = OptionalDigest(descriptor.BashBinaryDigest);
        var outputShapingRtkBinaryDigest =
            OptionalDigest(descriptor.OutputShapingRtkBinaryDigest);

        return JsonSerializer.SerializeToElement(new
        {
            planId,
            workerBootId,
            generation,
            deadlineUnixTimeMilliseconds = deadline,
            scriptDigest,
            domain,
            requestedRoute,
            effectiveRoute,
            preExecutionValidation,
            resolutionContext,
            outputProvenance,
            permittedFallbacks,
            fallbackReason,
            workingDirectoryDigest,
            rtkBinaryDigest,
            bashBinaryDigest,
            outputShapingRtkBinaryDigest,
        });
    }

    private static string? RequiredIdentityDigest(string? digest, bool required)
    {
        if (!required)
        {
            if (digest is not null) throw InvalidField();
            return null;
        }
        return ScriptDigest(digest);
    }

    private static string? DomainName(ExecutionDomain? value) => value switch
    {
        null => null,
        ExecutionDomain.PowerShell => "powershell",
        ExecutionDomain.NativeTerminal => "native_terminal",
        ExecutionDomain.MixedDataflow => "mixed_dataflow",
        ExecutionDomain.Bash => "bash",
        _ => throw InvalidField(),
    };

    private static ExecutionDomain? ParseDomain(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        return StringField(value) switch
        {
            "powershell" => ExecutionDomain.PowerShell,
            "native_terminal" => ExecutionDomain.NativeTerminal,
            "mixed_dataflow" => ExecutionDomain.MixedDataflow,
            "bash" => ExecutionDomain.Bash,
            _ => throw InvalidField(),
        };
    }

    private static string RequestedRouteName(RequestedExecutionRoute value) => value switch
    {
        RequestedExecutionRoute.Auto => "auto",
        RequestedExecutionRoute.PowerShell => "pwsh",
        RequestedExecutionRoute.Rtk => "rtk",
        _ => throw InvalidField(),
    };

    private static RequestedExecutionRoute ParseRequestedRoute(JsonElement value) =>
        StringField(value) switch
        {
            "auto" => RequestedExecutionRoute.Auto,
            "pwsh" => RequestedExecutionRoute.PowerShell,
            "rtk" => RequestedExecutionRoute.Rtk,
            _ => throw InvalidField(),
        };

    private static string ExecutionPathName(ExecutionPath value) => value switch
    {
        ExecutionPath.PowerShellDirect => "powershell_direct",
        ExecutionPath.Rtk => "rtk",
        ExecutionPath.NativeDirect => "native_direct",
        ExecutionPath.BashViaRtk => "bash_via_rtk",
        _ => throw InvalidField(),
    };

    private static ExecutionPath ParseExecutionPath(JsonElement value) =>
        StringField(value) switch
        {
            "powershell_direct" => ExecutionPath.PowerShellDirect,
            "rtk" => ExecutionPath.Rtk,
            "native_direct" => ExecutionPath.NativeDirect,
            "bash_via_rtk" => ExecutionPath.BashViaRtk,
            _ => throw InvalidField(),
        };

    private static string PreExecutionValidationName(
        PreExecutionValidation value) => value switch
    {
        PreExecutionValidation.None => "none",
        PreExecutionValidation.BashSyntax => "bash_syntax",
        _ => throw InvalidField(),
    };

    private static PreExecutionValidation ParsePreExecutionValidation(
        JsonElement value) => StringField(value) switch
    {
        "none" => PreExecutionValidation.None,
        "bash_syntax" => PreExecutionValidation.BashSyntax,
        _ => throw InvalidField(),
    };

    private static string ResolutionContextName(ResolutionContext value) => value switch
    {
        ResolutionContext.Warm => "warm",
        ResolutionContext.Cold => "cold",
        _ => throw InvalidField(),
    };

    private static ResolutionContext ParseResolutionContext(JsonElement value) =>
        StringField(value) switch
        {
            "warm" => ResolutionContext.Warm,
            "cold" => ResolutionContext.Cold,
            _ => throw InvalidField(),
        };

    private static string OutputProvenanceName(OutputProvenance value) => value switch
    {
        OutputProvenance.PowerShellObjects => "powershell_objects",
        OutputProvenance.DirectText => "direct_text",
        OutputProvenance.RtkUnknown => "rtk_unknown",
        OutputProvenance.RtkFiltered => "rtk_filtered",
        OutputProvenance.RtkPassthrough => "rtk_passthrough",
        _ => throw InvalidField(),
    };

    private static OutputProvenance ParseOutputProvenance(JsonElement value) =>
        StringField(value) switch
        {
            "powershell_objects" => OutputProvenance.PowerShellObjects,
            "direct_text" => OutputProvenance.DirectText,
            "rtk_unknown" => OutputProvenance.RtkUnknown,
            "rtk_filtered" => OutputProvenance.RtkFiltered,
            "rtk_passthrough" => OutputProvenance.RtkPassthrough,
            _ => throw InvalidField(),
        };

    private static string[] PermittedFallbackNames(
        ImmutableArray<ExecutionPath> values)
    {
        if (values.IsDefault || values.Length > 2 ||
            values.Distinct().Count() != values.Length ||
            values.Any(value => value is not (
                ExecutionPath.PowerShellDirect or ExecutionPath.NativeDirect)))
        {
            throw InvalidField();
        }
        return values.Select(ExecutionPathName).ToArray();
    }

    private static ImmutableArray<ExecutionPath> ParsePermittedFallbacks(
        JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw InvalidField();
        var builder = ImmutableArray.CreateBuilder<ExecutionPath>();
        foreach (var item in value.EnumerateArray())
        {
            var parsed = ParseExecutionPath(item);
            if (parsed is not (
                    ExecutionPath.PowerShellDirect or ExecutionPath.NativeDirect) ||
                builder.Contains(parsed) ||
                builder.Count == 2)
            {
                throw InvalidField();
            }
            builder.Add(parsed);
        }
        return builder.ToImmutable();
    }

    private static string? FallbackReasonName(ExecutionFallbackReason? value) => value switch
    {
        null => null,
        ExecutionFallbackReason.RtkExecutableUnavailable =>
            "rtk_executable_unavailable",
        ExecutionFallbackReason.RtkExecutableBecameUnavailable =>
            "rtk_executable_became_unavailable",
        ExecutionFallbackReason.RtkIneligibleShape => "rtk_ineligible_shape",
        ExecutionFallbackReason.RtkSelfInvocation => "rtk_self_invocation",
        ExecutionFallbackReason.RtkResolutionNotApplication =>
            "rtk_resolution_not_application",
        ExecutionFallbackReason.RtkFidelityExclusion => "rtk_fidelity_exclusion",
        ExecutionFallbackReason.RtkExecutionPreparationFailed =>
            "rtk_execution_preparation_failed",
        ExecutionFallbackReason.RtkTargetResolutionChanged =>
            "rtk_target_resolution_changed",
        _ => throw InvalidField(),
    };

    private static ExecutionFallbackReason? ParseFallbackReason(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        return StringField(value) switch
        {
            "rtk_executable_unavailable" =>
                ExecutionFallbackReason.RtkExecutableUnavailable,
            "rtk_executable_became_unavailable" =>
                ExecutionFallbackReason.RtkExecutableBecameUnavailable,
            "rtk_ineligible_shape" => ExecutionFallbackReason.RtkIneligibleShape,
            "rtk_self_invocation" => ExecutionFallbackReason.RtkSelfInvocation,
            "rtk_resolution_not_application" =>
                ExecutionFallbackReason.RtkResolutionNotApplication,
            "rtk_fidelity_exclusion" =>
                ExecutionFallbackReason.RtkFidelityExclusion,
            "rtk_execution_preparation_failed" =>
                ExecutionFallbackReason.RtkExecutionPreparationFailed,
            "rtk_target_resolution_changed" =>
                ExecutionFallbackReason.RtkTargetResolutionChanged,
            _ => throw InvalidField(),
        };
    }

    private static string? ParseOptionalDigest(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        return ScriptDigest(value);
    }

    private static string? OptionalDigest(string? value) =>
        value is null ? null : ScriptDigest(value);

    private static JsonElement PrepareArguments(
        WorkerInvokeArguments? arguments,
        string scriptDigest)
    {
        if (arguments is null) throw InvalidField();
        JsonElement encoded;
        try
        {
            encoded = WorkerSessionOperationCodec.CreateArguments(
                WorkerSessionOperationCodec.InvokeOperation,
                arguments);
        }
        catch (WorkerProtocolException exception)
        {
            throw NestedArgumentsFailure(exception);
        }
        RequireMatchingScriptDigest(arguments.Script, scriptDigest);
        return encoded;
    }

    private static WorkerInvokeArguments ParseInvokeArguments(JsonElement arguments)
    {
        try
        {
            return WorkerSessionOperationCodec.ParseArguments(
                    WorkerSessionOperationCodec.InvokeOperation,
                    arguments) as WorkerInvokeArguments ?? throw InvalidField();
        }
        catch (WorkerProtocolException exception)
        {
            throw NestedArgumentsFailure(exception);
        }
    }

    private static bool IsValidPrepareValue(WorkerInvokePreparePayload payload)
    {
        try
        {
            _ = PlanId(payload.PlanId);
            _ = PositiveInt64(payload.Generation);
            _ = DeadlineMilliseconds(payload.DeadlineUtc);
            var scriptDigest = ScriptDigest(payload.ScriptDigest);
            _ = PrepareArguments(payload.Arguments, scriptDigest);
            return true;
        }
        catch (WorkerProtocolException)
        {
            return false;
        }
    }

    private static CorrelationFields ParseCorrelation(JsonElement payload)
    {
        var fields = ClosedObject(
            payload,
            "planId",
            "scriptDigest",
            "generation",
            "deadlineUnixTimeMilliseconds");
        return new CorrelationFields(
            PlanId(Required(fields, "planId")),
            ScriptDigest(Required(fields, "scriptDigest")),
            PositiveInt64(Required(fields, "generation")),
            Deadline(Required(fields, "deadlineUnixTimeMilliseconds")));
    }

    private static JsonElement CreateCorrelation(
        Guid planId,
        string scriptDigest,
        long generation,
        DateTimeOffset deadlineUtc)
    {
        var canonicalPlanId = PlanId(planId);
        var canonicalDigest = ScriptDigest(scriptDigest);
        var positiveGeneration = PositiveInt64(generation);
        var deadline = DeadlineMilliseconds(deadlineUtc);
        return JsonSerializer.SerializeToElement(new
        {
            planId = canonicalPlanId,
            scriptDigest = canonicalDigest,
            generation = positiveGeneration,
            deadlineUnixTimeMilliseconds = deadline,
        });
    }

    private static bool IsValidCorrelationValue(
        Guid planId,
        string scriptDigest,
        long generation,
        DateTimeOffset deadlineUtc)
    {
        try
        {
            _ = PlanId(planId);
            _ = ScriptDigest(scriptDigest);
            _ = PositiveInt64(generation);
            _ = DeadlineMilliseconds(deadlineUtc);
            return true;
        }
        catch (WorkerProtocolException)
        {
            return false;
        }
    }

    private static Dictionary<string, JsonElement> ClosedObject(
        JsonElement value,
        params string[] allowedFields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw InvalidField();

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value))
            {
                throw new WorkerProtocolException(
                    "duplicate_field",
                    "Worker prepared-operation payload contains a duplicate field.");
            }
            if (!allowedFields.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new WorkerProtocolException(
                    "unknown_prepared_field",
                    "Worker prepared-operation payload contains an unknown field.");
            }
        }
        return fields;
    }

    private static JsonElement Required(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value))
        {
            throw new WorkerProtocolException(
                "missing_prepared_field",
                "Worker prepared-operation payload is missing a required field.");
        }
        return value;
    }

    private static string StringField(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidField();
        try
        {
            return value.GetString() ?? throw InvalidField();
        }
        catch (InvalidOperationException)
        {
            throw InvalidField();
        }
    }

    private static long PositiveInt64(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var parsed) || parsed <= 0)
        {
            throw InvalidField();
        }
        return parsed;
    }

    private static long PositiveInt64(long value)
    {
        if (value <= 0) throw InvalidField();
        return value;
    }

    private static DateTimeOffset Deadline(JsonElement value)
    {
        var milliseconds = PositiveInt64(value);
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InvalidField();
        }
    }

    private static long DeadlineMilliseconds(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero ||
            value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw InvalidField();
        }
        var milliseconds = value.ToUnixTimeMilliseconds();
        if (milliseconds <= 0) throw InvalidField();
        return milliseconds;
    }

    private static Guid PlanId(JsonElement value)
    {
        var text = StringField(value);
        if (!Guid.TryParseExact(text, "D", out var parsed) ||
            !string.Equals(text, PlanId(parsed), StringComparison.Ordinal))
        {
            throw InvalidField();
        }
        return parsed;
    }

    private static string PlanId(Guid value)
    {
        return CanonicalUuid4(value);
    }

    private static Guid BootId(JsonElement value)
    {
        var text = StringField(value);
        if (!Guid.TryParseExact(text, "D", out var parsed) ||
            !string.Equals(text, BootId(parsed), StringComparison.Ordinal))
        {
            throw InvalidField();
        }
        return parsed;
    }

    private static string BootId(Guid value) => CanonicalUuid4(value);

    private static string CanonicalUuid4(Guid value)
    {
        var text = value.ToString("D", CultureInfo.InvariantCulture);
        if (value == Guid.Empty || text[14] != '4' ||
            text[19] is not ('8' or '9' or 'a' or 'b'))
        {
            throw InvalidField();
        }
        return text;
    }

    private static string ScriptDigest(JsonElement value) =>
        ScriptDigest(StringField(value));

    private static string ScriptDigest(string? value)
    {
        if (value is null || value.Length != Sha256HexLength)
            throw InvalidField();
        foreach (var character in value)
        {
            if (character is >= '0' and <= '9' or >= 'a' and <= 'f')
                continue;
            throw InvalidField();
        }
        return value;
    }

    private static void RequireMatchingScriptDigest(string script, string expected)
    {
        var actual = ComputeScriptDigest(script);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw ScriptDigestMismatch();
    }

    private static string ComputeScriptDigest(string? script)
        => ComputeUtf8Digest(script);

    private static string ComputeUtf8Digest(string? value)
    {
        if (value is null) throw InvalidField();

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw InvalidField();
        }

        var bytes = ArrayPool<byte>.Shared.Rent(Math.Max(1, byteCount));
        try
        {
            int written;
            try
            {
                written = StrictUtf8.GetBytes(
                    value.AsSpan(),
                    bytes.AsSpan(0, byteCount));
            }
            catch (EncoderFallbackException)
            {
                throw InvalidField();
            }

            Span<byte> hash = stackalloc byte[32];
            try
            {
                SHA256.HashData(bytes.AsSpan(0, written), hash);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes.AsSpan(0, byteCount));
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }

    private static WorkerProtocolException InvalidField() =>
        new(
            "invalid_prepared_field",
            "Worker prepared-operation payload contains an invalid field.");

    private static WorkerProtocolException UnsupportedOperation() =>
        new(
            "unsupported_prepared_operation",
            "Worker prepared-operation payload names an unsupported operation.");

    private static WorkerProtocolException ScriptDigestMismatch() =>
        new(
            "prepared_script_digest_mismatch",
            "Worker prepared-operation script digest does not match its script.");

    private static WorkerProtocolException DescriptorDigestMismatch() =>
        new(
            "prepared_descriptor_digest_mismatch",
            "Worker prepared-operation descriptor digest does not match its fields.");

    private static WorkerProtocolException NestedArgumentsFailure(
        WorkerProtocolException exception) =>
        exception.DetailCode == "duplicate_field"
            ? new WorkerProtocolException(
                "duplicate_field",
                "Worker prepared-operation arguments contain a duplicate field.")
            : InvalidField();

    private readonly record struct CorrelationFields(
        Guid PlanId,
        string ScriptDigest,
        long Generation,
        DateTimeOffset DeadlineUtc);
}
