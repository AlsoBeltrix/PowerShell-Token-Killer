using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerOperationProtocolTests
{
    private static readonly Guid SessionId =
        Guid.Parse("4a7400b0-9793-4f59-b37d-5dde53a19ca8");
    private static readonly Guid ArtifactId =
        Guid.Parse("8c06d417-2071-4719-b6d7-f2e5e6367e8a");
    private const long Incarnation = 9;
    private static readonly WorkerProtocolLimits Limits =
        WorkerOperationProtocol.CreateLimits(
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1));

    [Fact]
    public void Initialize_and_ready_bind_identity_incarnation_and_immutable_limits()
    {
        var deadline = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_123);
        var initializeEnvelope = WorkerOperationProtocol.CreateInitializeEnvelope(
            SessionId,
            Incarnation,
            1,
            deadline,
            Limits);

        var initialize = WorkerOperationProtocol.ParseInitialize(initializeEnvelope);
        var readyEnvelope = WorkerOperationProtocol.CreateReadyEnvelope(initialize);
        var ready = WorkerOperationProtocol.ParseReady(
            readyEnvelope,
            SessionId,
            Incarnation,
            1);

        Assert.Equal(SessionId, initialize.SessionId);
        Assert.Equal(Incarnation, initialize.Incarnation);
        Assert.Equal(deadline, initialize.DeadlineUtc);
        Assert.Equal(Limits, initialize.Limits);
        Assert.Equal(Limits, ready);
        Assert.Equal(WorkerMessageKind.Ready, readyEnvelope.Kind);
    }

    [Fact]
    public void Invoke_state_cancel_result_and_snapshot_round_trip_closed_shapes()
    {
        var artifact = new WorkerArtifactRequest(ArtifactId, 4096);
        var invokeEnvelope = WorkerOperationProtocol.CreateInvokeEnvelope(
            SessionId,
            Incarnation,
            2,
            "$value = 42; $value",
            raw: true,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 17,
            artifact,
            Limits);
        var invoke = WorkerOperationProtocol.ParseInvoke(
            invokeEnvelope,
            SessionId,
            Incarnation,
            Limits);
        Assert.Equal("$value = 42; $value", invoke.Script);
        Assert.True(invoke.Raw);
        Assert.Equal(WorkerInvokeRoute.Pwsh, invoke.Route);
        Assert.Equal(17, invoke.TimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(17), invoke.Timeout);
        Assert.Equal(artifact, invoke.Artifact);

        var stateEnvelope = WorkerOperationProtocol.CreateStateQueryEnvelope(
            SessionId,
            Incarnation,
            3,
            listAvailable: true);
        var state = WorkerOperationProtocol.ParseStateQuery(
            stateEnvelope,
            SessionId,
            Incarnation,
            Limits);
        Assert.True(state.ListAvailable);
        Assert.Equal(TimeSpan.FromMinutes(5), state.Timeout);

        var cancelEnvelope = WorkerOperationProtocol.CreateCancelEnvelope(
            SessionId,
            Incarnation,
            2);
        Assert.Equal(
            new WorkerOperationCancel(2),
            WorkerOperationProtocol.ParseCancel(
                cancelEnvelope,
                SessionId,
                Incarnation));

        var resultEnvelope = WorkerOperationProtocol.CreateResultEnvelope(
            SessionId,
            Incarnation,
            new WorkerResult(
                2,
                WorkerResultStatus.Refused,
                "not started",
                "operation_not_started"));
        Assert.Equal(
            new WorkerResult(
                2,
                WorkerResultStatus.Refused,
                "not started",
                "operation_not_started"),
            WorkerOperationProtocol.ParseResult(
                resultEnvelope,
                SessionId,
                Incarnation));

        var snapshotEnvelope = WorkerOperationProtocol.CreateStateSnapshotEnvelope(
            SessionId,
            Incarnation,
            new WorkerStateSnapshot(3, false, "runspace: busy", "runspace_busy"));
        Assert.Equal(
            new WorkerStateSnapshot(3, false, "runspace: busy", "runspace_busy"),
            WorkerOperationProtocol.ParseStateSnapshot(
                snapshotEnvelope,
                SessionId,
                Incarnation));
    }

    [Fact]
    public void Payloads_reject_missing_unknown_duplicate_wrong_type_and_stale_identity()
    {
        var valid = WorkerOperationProtocol.CreateInvokeEnvelope(
            SessionId,
            Incarnation,
            2,
            "Get-Date",
            raw: false,
            WorkerInvokeRoute.Auto,
            timeoutSeconds: 0,
            artifact: null,
            Limits);

        AssertDetail(
            "session_identity_mismatch",
            () => WorkerOperationProtocol.ParseInvoke(
                valid with { SessionId = Guid.NewGuid() },
                SessionId,
                Incarnation,
                Limits));
        AssertDetail(
            "worker_incarnation_mismatch",
            () => WorkerOperationProtocol.ParseInvoke(
                valid with { Incarnation = Incarnation + 1 },
                SessionId,
                Incarnation,
                Limits));
        AssertDetail(
            "missing_operation_field",
            () => WorkerOperationProtocol.ParseInvoke(
                valid with
                {
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        script = "Get-Date",
                        raw = false,
                        route = "auto",
                        timeoutSeconds = 0,
                    }),
                },
                SessionId,
                Incarnation,
                Limits));
        AssertDetail(
            "unknown_operation_field",
            () => WorkerOperationProtocol.ParseStateQuery(
                WorkerOperationProtocol.CreateStateQueryEnvelope(
                    SessionId,
                    Incarnation,
                    3,
                    false) with
                {
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        listAvailable = false,
                        extra = true,
                    }),
                },
                SessionId,
                Incarnation,
                Limits));
        AssertDetail(
            "unknown_operation_field",
            () => WorkerOperationProtocol.ParseInvoke(
                valid with
                {
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        script = "Get-Date",
                        raw = false,
                        route = "auto",
                        timeoutSeconds = 0,
                        artifact = (object?)null,
                        background = false,
                    }),
                },
                SessionId,
                Incarnation,
                Limits));

        using var duplicate = JsonDocument.Parse(
            """{"script":"Get-Date","raw":false,"route":"auto","timeoutSeconds":0,"artifact":null,"script":"duplicate"}""");
        AssertDetail(
            "duplicate_field",
            () => WorkerOperationProtocol.ParseInvoke(
                valid with { Payload = duplicate.RootElement.Clone() },
                SessionId,
                Incarnation,
                Limits));
        AssertDetail(
            "invalid_operation_field",
            () => WorkerOperationProtocol.ParseInvoke(
                valid with
                {
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        script = "Get-Date",
                        raw = "false",
                        route = "auto",
                        timeoutSeconds = 0,
                        artifact = (object?)null,
                    }),
                },
                SessionId,
                Incarnation,
                Limits));
    }

    [Fact]
    public void Script_and_result_bounds_use_strict_logical_utf8()
    {
        var exact = new string('x', Limits.MaximumScriptBytes);
        var parsed = WorkerOperationProtocol.ParseInvoke(
            WorkerOperationProtocol.CreateInvokeEnvelope(
                SessionId,
                Incarnation,
                2,
                exact,
                false,
                WorkerInvokeRoute.Auto,
                0,
                null,
                Limits),
            SessionId,
            Incarnation,
            Limits);
        Assert.Equal(exact, parsed.Script);

        AssertDetail(
            "operation_text_too_large",
            () => WorkerOperationProtocol.CreateInvokeEnvelope(
                SessionId,
                Incarnation,
                2,
                exact + "x",
                false,
                WorkerInvokeRoute.Auto,
                0,
                null,
                Limits));
        AssertDetail(
            "invalid_operation_field",
            () => WorkerOperationProtocol.CreateInvokeEnvelope(
                SessionId,
                Incarnation,
                2,
                "\ud800",
                false,
                WorkerInvokeRoute.Auto,
                0,
                null,
                Limits));
        AssertDetail(
            "operation_text_too_large",
            () => WorkerOperationProtocol.CreateResultEnvelope(
                SessionId,
                Incarnation,
                new WorkerResult(
                    2,
                    WorkerResultStatus.Completed,
                    new string('x', WorkerOperationProtocol.MaximumLogicalTextBytes + 1),
                    null)));
        AssertDetail(
            "operation_text_too_large",
            () => WorkerOperationProtocol.CreateStateSnapshotEnvelope(
                SessionId,
                Incarnation,
                new WorkerStateSnapshot(
                    3,
                    true,
                    new string('x', WorkerOperationProtocol.MaximumLogicalTextBytes + 1),
                    null)));
    }

    [Fact]
    public void Artifact_chunks_and_seal_require_order_exact_length_and_digest()
    {
        var firstBytes = Encoding.UTF8.GetBytes("first-");
        var secondBytes = Encoding.UTF8.GetBytes("second");
        var allBytes = firstBytes.Concat(secondBytes).ToArray();
        var first = RoundTripChunk(new WorkerArtifactChunk(
            7,
            ArtifactId,
            0,
            firstBytes));
        var second = RoundTripChunk(new WorkerArtifactChunk(
            7,
            ArtifactId,
            firstBytes.Length,
            secondBytes));
        var digest = Convert.ToHexString(SHA256.HashData(allBytes)).ToLowerInvariant();
        var seal = RoundTripSeal(new WorkerArtifactSeal(
            7,
            ArtifactId,
            allBytes.Length,
            digest));

        using var receiver = new WorkerArtifactReceiver(
            7,
            new WorkerArtifactRequest(ArtifactId, allBytes.Length));
        receiver.Accept(first);
        receiver.Accept(second);
        receiver.Accept(seal);
        Assert.True(receiver.IsSealed);
        Assert.Equal(allBytes.Length, receiver.Length);

        using var gap = new WorkerArtifactReceiver(
            7,
            new WorkerArtifactRequest(ArtifactId, allBytes.Length));
        AssertDetail("artifact_sequence_invalid", () => gap.Accept(second));

        using var wrongDigest = new WorkerArtifactReceiver(
            7,
            new WorkerArtifactRequest(ArtifactId, allBytes.Length));
        wrongDigest.Accept(first);
        wrongDigest.Accept(second);
        AssertDetail(
            "artifact_digest_mismatch",
            () => wrongDigest.Accept(seal with { Sha256 = new string('0', 64) }));

        using var wrongLength = new WorkerArtifactReceiver(
            7,
            new WorkerArtifactRequest(ArtifactId, allBytes.Length));
        wrongLength.Accept(first);
        wrongLength.Accept(second);
        AssertDetail(
            "artifact_seal_invalid",
            () => wrongLength.Accept(seal with { Length = seal.Length - 1 }));
    }

    [Fact]
    public void Result_and_snapshot_unions_reject_cross_branch_fields()
    {
        AssertDetail(
            "invalid_operation_result",
            () => WorkerOperationProtocol.CreateResultEnvelope(
                SessionId,
                Incarnation,
                new WorkerResult(
                    1,
                    WorkerResultStatus.Completed,
                    "done",
                    "must_be_null")));
        AssertDetail(
            "invalid_state_snapshot",
            () => WorkerOperationProtocol.CreateStateSnapshotEnvelope(
                SessionId,
                Incarnation,
                new WorkerStateSnapshot(
                    1,
                    false,
                    "busy",
                    null)));
    }

    private static WorkerArtifactChunk RoundTripChunk(WorkerArtifactChunk chunk) =>
        WorkerOperationProtocol.ParseArtifactChunk(
            WorkerOperationProtocol.CreateArtifactChunkEnvelope(
                SessionId,
                Incarnation,
                chunk,
                Limits),
            SessionId,
            Incarnation,
            Limits);

    private static WorkerArtifactSeal RoundTripSeal(WorkerArtifactSeal seal) =>
        WorkerOperationProtocol.ParseArtifactSeal(
            WorkerOperationProtocol.CreateArtifactSealEnvelope(
                SessionId,
                Incarnation,
                seal),
            SessionId,
            Incarnation);

    private static void AssertDetail(string detailCode, Action action)
    {
        var exception = Assert.Throws<WorkerProtocolException>(action);
        Assert.Equal(detailCode, exception.DetailCode);
    }
}
