using System.Text.Json;

namespace PtkSharedContracts;

internal static class GuardianHostPreparedPlanDescriptorCodec
{
    internal static Sha256Digest ComputeDigest(
        GuardianHostPreparedPlanDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Sha256Digest.Compute(
            JsonSerializer.SerializeToUtf8Bytes(CreateElement(descriptor)));
    }

    internal static JsonElement CreateElement(
        GuardianHostPreparedPlanDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return JsonSerializer.SerializeToElement(new
        {
            plan_id = descriptor.PlanId.Value,
            worker_boot_id = descriptor.WorkerIdentity.BootId.Value,
            worker_generation = descriptor.WorkerIdentity.Generation.Value,
            deadline_unix_time_milliseconds = descriptor.DeadlineUnixTimeMilliseconds,
            script_sha256 = descriptor.ScriptDigest.Value,
            domain = descriptor.Domain is { } domain ? Wire(domain) : null,
            requested_route = Wire(descriptor.RequestedRoute),
            effective_route = Wire(descriptor.EffectiveRoute),
            pre_execution_validation = Wire(descriptor.PreExecutionValidation),
            resolution_context = Wire(descriptor.ResolutionContext),
            output_provenance = Wire(descriptor.OutputProvenance),
            permitted_fallbacks = descriptor.PermittedFallbacks.Select(Wire).ToArray(),
            fallback_reason = descriptor.FallbackReason is { } fallbackReason
                ? Wire(fallbackReason)
                : null,
            working_directory_sha256 = descriptor.WorkingDirectoryDigest?.Value,
            rtk_binary_sha256 = descriptor.RtkBinaryDigest?.Value,
            bash_binary_sha256 = descriptor.BashBinaryDigest?.Value,
            output_shaping_rtk_binary_sha256 =
                descriptor.OutputShapingRtkBinaryDigest?.Value,
        });
    }

    internal static GuardianHostPreparedPlanDescriptor Parse(JsonElement value)
    {
        var descriptor = new GuardianHostPreparedPlanDescriptor(
            new PlanId(value.GetProperty("plan_id").GetGuid()),
            new GuardianHostWorkerIdentity(
                new WorkerBootId(value.GetProperty("worker_boot_id").GetGuid()),
                new WorkerGeneration(PositiveInt64(
                    value.GetProperty("worker_generation")))),
            PositiveInt64(value.GetProperty("deadline_unix_time_milliseconds")),
            new Sha256Digest(value.GetProperty("script_sha256").GetString()!),
            NullableEnum<GuardianHostExecutionDomain>(value.GetProperty("domain")),
            ParseEnum<GuardianHostRequestedExecutionRoute>(
                value.GetProperty("requested_route")),
            ParseEnum<GuardianHostEffectiveExecutionRoute>(
                value.GetProperty("effective_route")),
            ParseEnum<GuardianHostPreExecutionValidation>(
                value.GetProperty("pre_execution_validation")),
            ParseEnum<GuardianHostResolutionContext>(
                value.GetProperty("resolution_context")),
            ParseEnum<GuardianHostOutputProvenance>(
                value.GetProperty("output_provenance")),
            value.GetProperty("permitted_fallbacks")
                .EnumerateArray()
                .Select(ParseEnum<GuardianHostEffectiveExecutionRoute>)
                .ToArray(),
            NullableEnum<GuardianHostExecutionFallbackReason>(
                value.GetProperty("fallback_reason")),
            NullableDigest(value.GetProperty("working_directory_sha256")),
            NullableDigest(value.GetProperty("rtk_binary_sha256")),
            NullableDigest(value.GetProperty("bash_binary_sha256")),
            NullableDigest(value.GetProperty("output_shaping_rtk_binary_sha256")));
        return descriptor;
    }

    private static Sha256Digest? NullableDigest(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null
            ? null
            : new Sha256Digest(value.GetString()!);

    private static T? NullableEnum<T>(JsonElement value)
        where T : struct, Enum =>
        value.ValueKind == JsonValueKind.Null ? null : ParseEnum<T>(value);

    private static T ParseEnum<T>(JsonElement value)
        where T : struct, Enum
    {
        var text = value.GetString();
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(Wire(candidate), text, StringComparison.Ordinal))
                return candidate;
        }
        throw new ArgumentException("Prepared descriptor enum value is invalid.");
    }

    private static long PositiveInt64(JsonElement value)
    {
        if (!value.TryGetDecimal(out var parsed) ||
            parsed != decimal.Truncate(parsed) ||
            parsed < 1 ||
            parsed > long.MaxValue)
        {
            throw new ArgumentException(
                "Prepared descriptor integer value is invalid.");
        }
        return decimal.ToInt64(parsed);
    }

    private static string Wire<T>(T value)
        where T : struct, Enum => value switch
    {
        GuardianHostRequestedExecutionRoute.Pwsh => "pwsh",
        GuardianHostExecutionDomain.PowerShell => "powershell",
        GuardianHostEffectiveExecutionRoute.PowerShellDirect => "powershell_direct",
        GuardianHostOutputProvenance.PowerShellObjects => "powershell_objects",
        _ => ToSnakeCase(value.ToString()),
    };

    private static string ToSnakeCase(string value)
    {
        var result = new System.Text.StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsUpper(value[index]) && index > 0)
                result.Append('_');
            result.Append(char.ToLowerInvariant(value[index]));
        }
        return result.ToString();
    }
}
