using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerPreparedPlanDescriptorCodecTests
{
    private static readonly Guid PlanId =
        Guid.ParseExact("12345678-1234-4234-9234-123456789abc", "D");
    private static readonly Guid BootId =
        Guid.ParseExact("87654321-4321-4321-8321-cba987654321", "D");
    private static readonly DateTimeOffset Deadline =
        DateTimeOffset.FromUnixTimeMilliseconds(1_900_000_000_123);
    private const string Script = "x";
    private const string ScriptDigest =
        "2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881";

    [Fact]
    public void Prepared_descriptor_projects_the_exact_closed_audit_shape()
    {
        var descriptor = Descriptor();

        var encoded =
            WorkerPreparedOperationCodec.CreatePreparedDescriptor(descriptor);
        var names = encoded.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(
        [
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
            "descriptorDigest",
        ], names);
        Assert.Equal(
            WorkerPreparedOperationCodec.ComputePreparedDescriptorDigest(descriptor),
            encoded.GetProperty("descriptorDigest").GetString());
        Assert.Equal(
            descriptor,
            WorkerPreparedOperationCodec.ParsePreparedDescriptor(encoded));
    }

    [Fact]
    public void Prepared_descriptor_rejects_shape_enum_and_digest_tampering()
    {
        var encoded =
            WorkerPreparedOperationCodec.CreatePreparedDescriptor(Descriptor());

        foreach (var field in encoded.EnumerateObject().Select(property => property.Name))
        {
            var missing = JsonNode.Parse(encoded.GetRawText())!.AsObject();
            Assert.True(missing.Remove(field));
            AssertDetail(
                "missing_prepared_field",
                () => WorkerPreparedOperationCodec.ParsePreparedDescriptor(
                    Json(missing.ToJsonString())));
        }

        var unknown = JsonNode.Parse(encoded.GetRawText())!.AsObject();
        unknown["extra"] = true;
        AssertDetail(
            "unknown_prepared_field",
            () => WorkerPreparedOperationCodec.ParsePreparedDescriptor(
                Json(unknown.ToJsonString())));

        var invalidRoute = JsonNode.Parse(encoded.GetRawText())!.AsObject();
        invalidRoute["requestedRoute"] = "Auto";
        AssertDetail(
            "invalid_prepared_field",
            () => WorkerPreparedOperationCodec.ParsePreparedDescriptor(
                Json(invalidRoute.ToJsonString())));

        var tampered = JsonNode.Parse(encoded.GetRawText())!.AsObject();
        tampered["effectiveRoute"] = "native_direct";
        AssertDetail(
            "prepared_descriptor_digest_mismatch",
            () => WorkerPreparedOperationCodec.ParsePreparedDescriptor(
                Json(tampered.ToJsonString())));
    }

    [Fact]
    public void Prepared_descriptor_rejects_unbounded_or_duplicate_fallbacks()
    {
        var descriptor = Descriptor();

        AssertDetail(
            "invalid_prepared_field",
            () => WorkerPreparedOperationCodec.CreatePreparedDescriptor(
                descriptor with
                {
                    PermittedFallbacks =
                    [
                        ExecutionPath.PowerShellDirect,
                        ExecutionPath.PowerShellDirect,
                    ],
                }));
        AssertDetail(
            "invalid_prepared_field",
            () => WorkerPreparedOperationCodec.CreatePreparedDescriptor(
                descriptor with
                {
                    PermittedFallbacks =
                    [
                        ExecutionPath.PowerShellDirect,
                        ExecutionPath.NativeDirect,
                        ExecutionPath.Rtk,
                    ],
                }));
    }

    [Fact]
    public void Projection_binds_script_route_boot_generation_and_deadline_to_plan()
    {
        var prepare = Prepare();
        var plan = Plan();

        var descriptor = WorkerPreparedOperationCodec.ProjectPreparedDescriptor(
            BootId,
            prepare,
            plan);

        Assert.Equal(Descriptor(), descriptor);
        AssertDetail(
            "prepared_script_digest_mismatch",
            () => WorkerPreparedOperationCodec.ProjectPreparedDescriptor(
                BootId,
                prepare,
                Plan("y")));
        AssertDetail(
            "invalid_prepared_field",
            () => WorkerPreparedOperationCodec.ProjectPreparedDescriptor(
                Guid.Empty,
                prepare,
                plan));
        AssertDetail(
            "invalid_prepared_field",
            () => WorkerPreparedOperationCodec.ProjectPreparedDescriptor(
                BootId,
                prepare with
                {
                    Arguments = prepare.Arguments with { Route = WorkerInvokeRoute.Rtk },
                },
                plan));
    }

    private static WorkerPreparedPlanDescriptor Descriptor() => new(
        PlanId,
        BootId,
        ScriptDigest,
        7,
        Deadline,
        ExecutionDomain.PowerShell,
        RequestedExecutionRoute.Auto,
        ExecutionPath.PowerShellDirect,
        PreExecutionValidation.None,
        ResolutionContext.Warm,
        OutputProvenance.PowerShellObjects,
        ImmutableArray<ExecutionPath>.Empty,
        FallbackReason: null,
        WorkingDirectoryDigest: null,
        RtkBinaryDigest: null,
        BashBinaryDigest: null,
        OutputShapingRtkBinaryDigest: null);

    private static WorkerInvokePreparePayload Prepare() => new(
        PlanId,
        7,
        Deadline,
        ScriptDigest,
        new WorkerInvokeArguments(Script, false, WorkerInvokeRoute.Auto));

    private static ExecutionPlan Plan(string script = Script) => new(
        script,
        script,
        ExecutionDomain.PowerShell,
        ExecutionPath.PowerShellDirect,
        PreExecutionValidation.None,
        ResolutionContext.Warm,
        RequestedExecutionRoute.Auto,
        OutputProvenance.PowerShellObjects,
        ImmutableArray<ExecutionPath>.Empty,
        fallbackReason: null,
        rtkExecutableIdentity: null);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertDetail(string detailCode, Action action)
    {
        var exception = Assert.Throws<WorkerProtocolException>(action);
        Assert.Equal(detailCode, exception.DetailCode);
    }
}
