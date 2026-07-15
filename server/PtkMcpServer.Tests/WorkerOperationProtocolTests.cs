using System.Text;
using System.Text.Json;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerOperationProtocolTests
{
    private static readonly Guid BootId =
        Guid.Parse("75db238b-5828-4aca-9b73-5c69d707d62d");
    private const long Generation = 7;
    private static readonly DateTimeOffset Deadline =
        DateTimeOffset.Parse("2035-06-07T08:09:10Z");

    [Fact]
    public void Request_and_cancel_payloads_parse_with_exact_identity()
    {
        var arguments = JsonSerializer.SerializeToElement(new { listAvailable = true });
        var request = WorkerOperationProtocol.ParseRequest(
            Request(41, Generation, Deadline, new string('a', 64), arguments),
            BootId,
            Generation);

        Assert.Equal(41, request.RequestId);
        Assert.Equal(Generation, request.Generation);
        Assert.Equal(Deadline, request.DeadlineUtc);
        Assert.Equal(new string('a', 64), request.Operation);
        Assert.True(request.Arguments.GetProperty("listAvailable").GetBoolean());

        var cancel = WorkerOperationProtocol.ParseCancel(
            Envelope(
                WorkerMessageKind.Cancel,
                41,
                JsonSerializer.SerializeToElement(new { generation = Generation })),
            BootId,
            Generation);
        Assert.Equal(new WorkerOperationCancel(41, Generation), cancel);
    }

    [Theory]
    [InlineData((int)WorkerOperationStatus.Completed, "completed")]
    [InlineData((int)WorkerOperationStatus.Failed, "failed")]
    [InlineData((int)WorkerOperationStatus.Canceled, "canceled")]
    [InlineData((int)WorkerOperationStatus.TimedOut, "timed_out")]
    public void Response_union_round_trips_with_exact_wire_names(
        int statusValue,
        string wireStatus)
    {
        var status = (WorkerOperationStatus)statusValue;
        var response = status == WorkerOperationStatus.Completed
            ? WorkerOperationResponse.Completed(
                51,
                Generation,
                JsonSerializer.SerializeToElement(new { text = "ok" }))
            : new WorkerOperationResponse(
                51,
                Generation,
                status,
                null,
                "fixed_detail");

        var envelope = WorkerOperationProtocol.CreateResponseEnvelope(BootId, response);
        var parsed = WorkerOperationProtocol.ParseResponse(envelope, BootId, Generation);

        Assert.Equal(WorkerMessageKind.Response, envelope.Kind);
        Assert.Equal(51, envelope.RequestId);
        Assert.Equal(wireStatus, envelope.Payload.GetProperty("status").GetString());
        Assert.Equal(status, parsed.Status);
        if (status == WorkerOperationStatus.Completed)
        {
            Assert.Equal("ok", parsed.Result!.Value.GetProperty("text").GetString());
            Assert.Null(parsed.DetailCode);
        }
        else
        {
            Assert.Null(parsed.Result);
            Assert.Equal("fixed_detail", parsed.DetailCode);
        }
    }

    [Fact]
    public void Request_rejects_missing_unknown_wrong_type_and_invalid_codes()
    {
        var validArguments = JsonSerializer.SerializeToElement(new { });
        var payloads = new[]
        {
            JsonSerializer.SerializeToElement(new
            {
                deadlineUnixTimeMilliseconds = Deadline.ToUnixTimeMilliseconds(),
                operation = "state",
                arguments = validArguments,
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                deadlineUnixTimeMilliseconds = Deadline.ToUnixTimeMilliseconds(),
                operation = "state",
                arguments = validArguments,
                extra = true,
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = 0,
                deadlineUnixTimeMilliseconds = Deadline.ToUnixTimeMilliseconds(),
                operation = "state",
                arguments = validArguments,
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                deadlineUnixTimeMilliseconds = 0,
                operation = "state",
                arguments = validArguments,
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                deadlineUnixTimeMilliseconds = long.MaxValue,
                operation = "state",
                arguments = validArguments,
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                deadlineUnixTimeMilliseconds = Deadline.ToUnixTimeMilliseconds(),
                operation = "State",
                arguments = validArguments,
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                deadlineUnixTimeMilliseconds = Deadline.ToUnixTimeMilliseconds(),
                operation = "state-name",
                arguments = validArguments,
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                deadlineUnixTimeMilliseconds = Deadline.ToUnixTimeMilliseconds(),
                operation = new string('a', 65),
                arguments = validArguments,
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                deadlineUnixTimeMilliseconds = Deadline.ToUnixTimeMilliseconds(),
                operation = "state",
                arguments = Array.Empty<object>(),
            }),
        };

        foreach (var payload in payloads)
        {
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerOperationProtocol.ParseRequest(
                    Envelope(WorkerMessageKind.Request, 1, payload),
                    BootId,
                    Generation));
        }
    }

    [Fact]
    public void Request_requires_each_outer_field_with_one_stable_failure()
    {
        var arguments = JsonSerializer.SerializeToElement(new { });
        var payloads = new[]
        {
            JsonSerializer.SerializeToElement(new
            {
                deadlineUnixTimeMilliseconds = Deadline.ToUnixTimeMilliseconds(),
                operation = "state",
                arguments,
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                operation = "state",
                arguments,
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                deadlineUnixTimeMilliseconds = Deadline.ToUnixTimeMilliseconds(),
                arguments,
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                deadlineUnixTimeMilliseconds = Deadline.ToUnixTimeMilliseconds(),
                operation = "state",
            }),
        };

        foreach (var payload in payloads)
        {
            var exception = Assert.Throws<WorkerProtocolException>(() =>
                WorkerOperationProtocol.ParseRequest(
                    Envelope(WorkerMessageKind.Request, 1, payload),
                    BootId,
                    Generation));
            Assert.Equal("missing_operation_field", exception.DetailCode);
        }
    }

    [Fact]
    public void Request_and_cancel_reject_stale_identity_generation_and_request_id()
    {
        var request = Request(
            1,
            Generation,
            Deadline,
            "state",
            JsonSerializer.SerializeToElement(new { }));
        Assert.Equal(
            "worker_boot_mismatch",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerOperationProtocol.ParseRequest(
                    request with { WorkerBootId = Guid.NewGuid() },
                    BootId,
                    Generation)).DetailCode);
        Assert.Equal(
            "worker_generation_mismatch",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerOperationProtocol.ParseRequest(
                    Request(
                        1,
                        Generation + 1,
                        Deadline,
                        "state",
                        JsonSerializer.SerializeToElement(new { })),
                    BootId,
                    Generation)).DetailCode);
        Assert.Equal(
            "request_id_required",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerOperationProtocol.ParseRequest(
                    request with { RequestId = null },
                    BootId,
                    Generation)).DetailCode);

        var badCancels = new[]
        {
            Envelope(WorkerMessageKind.Cancel, 1, JsonSerializer.SerializeToElement(new { })),
            Envelope(WorkerMessageKind.Cancel, 1, JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                extra = true,
            })),
            Envelope(WorkerMessageKind.Cancel, 1, JsonSerializer.SerializeToElement(new
            {
                generation = Generation + 1,
            })),
        };
        foreach (var cancel in badCancels)
        {
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerOperationProtocol.ParseCancel(cancel, BootId, Generation));
        }
    }

    [Fact]
    public void Response_union_rejects_cross_branch_null_unknown_and_hostile_detail()
    {
        var payloads = new[]
        {
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                status = "completed",
                detailCode = "wrong_branch",
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                status = "failed",
                result = new { },
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                status = "failed",
                detailCode = "HOSTILE",
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                status = "unknown",
                detailCode = "fixed_detail",
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                status = "completed",
                result = (object?)null,
            }),
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                status = "completed",
                result = new { },
                extra = true,
            }),
        };

        foreach (var payload in payloads)
        {
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerOperationProtocol.ParseResponse(
                    Envelope(WorkerMessageKind.Response, 9, payload),
                    BootId,
                    Generation));
        }

        Assert.Throws<WorkerProtocolException>(() =>
            WorkerOperationProtocol.CreateResponseEnvelope(
                BootId,
                new WorkerOperationResponse(
                    9,
                    Generation,
                    WorkerOperationStatus.Completed,
                    null,
                    null)));
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerOperationProtocol.CreateResponseEnvelope(
                BootId,
                new WorkerOperationResponse(
                    9,
                    Generation,
                    WorkerOperationStatus.Completed,
                    JsonSerializer.SerializeToElement(new { text = "ok" }),
                    "wrong_branch")));
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerOperationProtocol.CreateResponseEnvelope(
                BootId,
                new WorkerOperationResponse(
                    9,
                    Generation,
                    WorkerOperationStatus.Completed,
                    JsonSerializer.SerializeToElement(new[] { "not", "object" }),
                    null)));
        foreach (var status in new[]
        {
            WorkerOperationStatus.Failed,
            WorkerOperationStatus.Canceled,
            WorkerOperationStatus.TimedOut,
        })
        {
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerOperationProtocol.CreateResponseEnvelope(
                    BootId,
                    new WorkerOperationResponse(
                        9,
                        Generation,
                        status,
                        JsonSerializer.SerializeToElement(new { text = "wrong_branch" }),
                        "fixed_detail")));
        }
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerOperationProtocol.CreateResponseEnvelope(
                BootId,
                WorkerOperationResponse.Failed(9, Generation, "secret/path")));
    }

    [Fact]
    public void Response_rejects_stale_boot_generation_and_missing_request_identity()
    {
        var valid = WorkerOperationProtocol.CreateResponseEnvelope(
            BootId,
            WorkerOperationResponse.Completed(
                9,
                Generation,
                JsonSerializer.SerializeToElement(new { text = "ok" })));

        Assert.Equal(
            "worker_boot_mismatch",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerOperationProtocol.ParseResponse(
                    valid with { WorkerBootId = Guid.NewGuid() },
                    BootId,
                    Generation)).DetailCode);
        Assert.Equal(
            "request_id_required",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerOperationProtocol.ParseResponse(
                    valid with { RequestId = null },
                    BootId,
                    Generation)).DetailCode);

        var stalePayload = JsonSerializer.SerializeToElement(new
        {
            generation = Generation + 1,
            status = "completed",
            result = new { text = "ok" },
        });
        Assert.Equal(
            "worker_generation_mismatch",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerOperationProtocol.ParseResponse(
                    Envelope(WorkerMessageKind.Response, 9, stalePayload),
                    BootId,
                    Generation)).DetailCode);
    }

    [Fact]
    public void Full_codec_rejects_duplicate_payload_fields_before_operation_binding()
    {
        var json = string.Concat(
            "{\"protocolVersion\":1,\"kind\":\"request\",\"workerBootId\":\"",
            BootId.ToString("D"),
            "\",\"requestId\":1,\"payload\":{\"generation\":",
            Generation,
            ",\"generation\":",
            Generation,
            ",\"deadlineUnixTimeMilliseconds\":",
            Deadline.ToUnixTimeMilliseconds(),
            ",\"operation\":\"state\",\"arguments\":{}}}");

        var exception = Assert.Throws<WorkerProtocolException>(() =>
            WorkerProtocol.Decode(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("duplicate_field", exception.DetailCode);

        using var document = JsonDocument.Parse(string.Concat(
            "{\"generation\":",
            Generation,
            ",\"generation\":",
            Generation,
            ",\"deadlineUnixTimeMilliseconds\":",
            Deadline.ToUnixTimeMilliseconds(),
            ",\"operation\":\"state\",\"arguments\":{}}"));
        var direct = Assert.Throws<WorkerProtocolException>(() =>
            WorkerOperationProtocol.ParseRequest(
                Envelope(WorkerMessageKind.Request, 1, document.RootElement.Clone()),
                BootId,
                Generation));
        Assert.Equal("duplicate_field", direct.DetailCode);
    }

    private static WorkerEnvelope Request(
        long requestId,
        long generation,
        DateTimeOffset deadline,
        string operation,
        JsonElement arguments) =>
        Envelope(
            WorkerMessageKind.Request,
            requestId,
            JsonSerializer.SerializeToElement(new
            {
                generation,
                deadlineUnixTimeMilliseconds = deadline.ToUnixTimeMilliseconds(),
                operation,
                arguments,
            }));

    private static WorkerEnvelope Envelope(
        WorkerMessageKind kind,
        long? requestId,
        JsonElement payload) =>
        new(WorkerProtocol.Version, kind, BootId, requestId, payload);
}
