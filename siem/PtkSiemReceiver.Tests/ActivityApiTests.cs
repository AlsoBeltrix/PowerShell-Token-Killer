using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PtkSiemReceiver.Tests;

[Collection(SiemReceiverProcessCollection.Name)]
public sealed class ActivityApiTests
{
    private const string IngestToken = "activity-suite-ingest-0123456789abcdef";

    [Fact]
    public async Task Activity_api_correlates_attribution_outcome_and_exact_evidence()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        var callId = Guid.CreateVersion7();
        var acceptedId = Guid.CreateVersion7();
        var terminalId = Guid.CreateVersion7();
        var commandBytes = Encoding.UTF8.GetBytes("Get-Process | Select-Object -First 1");
        var responseBytes = Encoding.UTF8.GetBytes("pwsh process completed\nexit code 0");
        var commandEvidence = EvidenceEnvelopeTests.EvidenceSet.Create(
            terminalId,
            "submitted_command",
            commandBytes,
            activityCallId: callId);
        var responseEvidence = EvidenceEnvelopeTests.EvidenceSet.Create(
            terminalId,
            "caller_response",
            responseBytes,
            activityCallId: callId);
        var manifest = CombineManifests(commandEvidence.ManifestJson, responseEvidence.ManifestJson);

        var accepted = CreateActivityRecord(
            callId,
            acceptedId,
            sequence: 1,
            previousEventHash: null,
            eventType: "call.accepted",
            occurredUtc: "2026-08-14T12:00:00.0000000Z",
            state: "accepted",
            evidenceManifestJson: null,
            agentName: "codex",
            modelProvider: "openai",
            modelName: "gpt-5.6");
        var terminal = CreateActivityRecord(
            callId,
            terminalId,
            sequence: 2,
            previousEventHash: accepted.EventHash,
            eventType: "call.completed",
            occurredUtc: "2026-08-14T12:00:01.0000000Z",
            state: "completed",
            evidenceManifestJson: manifest,
            agentName: "codex",
            modelProvider: "openai",
            modelName: "gpt-5.6");

        using (var response = await client.SendAsync(IngestJsonRequest(
                   host.Endpoint,
                   new[] { accepted.Body, terminal.Body }
                       .Concat(commandEvidence.Records)
                       .Concat(responseEvidence.Records)
                       .ToArray())))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var response = await OperatorGetAsync(host, client, "/api/activities"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var activity = Assert.Single(payload.RootElement.GetProperty("activities").EnumerateArray());
            Assert.Equal(callId.ToString("D"), activity.GetProperty("activity_id").GetString());
            Assert.Equal("completed", activity.GetProperty("state").GetString());
            Assert.Equal("ptk_invoke", activity.GetProperty("request").GetProperty("tool").GetString());
            Assert.Equal("codex", activity.GetProperty("agent").GetProperty("name").GetString());
            Assert.Equal("openai", activity.GetProperty("model").GetProperty("provider").GetString());
            Assert.Equal("gpt-5.6", activity.GetProperty("model").GetProperty("name").GetString());
            Assert.Equal("client_asserted", activity.GetProperty("attribution").GetProperty("strength").GetString());
            Assert.Equal(
                "activity projection",
                activity.GetProperty("client_context").GetProperty("task_name").GetString());
            Assert.Equal(
                "run-123",
                activity.GetProperty("client_context").GetProperty("run_id").GetString());
            Assert.Equal("/work/repo", activity.GetProperty("context").GetProperty("effective_cwd").GetString());
            Assert.Equal("destination", activity.GetProperty("command").GetProperty("availability").GetString());
            Assert.Equal(
                Encoding.UTF8.GetString(commandBytes),
                activity.GetProperty("command").GetProperty("preview").GetString());
            Assert.Equal("destination", activity.GetProperty("response").GetProperty("availability").GetString());
            Assert.Equal("intact", activity.GetProperty("chain").GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, activity.GetProperty("raw_events").ValueKind);
        }

        using (var response = await OperatorGetAsync(
                   host,
                   client,
                   "/api/activities?query=Get-Process"))
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Single(payload.RootElement.GetProperty("activities").EnumerateArray());
        }

        using (var response = await OperatorGetAsync(
                   host,
                   client,
                   $"/api/activities/{callId:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var activity = payload.RootElement.GetProperty("activity");
            Assert.Equal(4, activity.GetProperty("raw_events").GetArrayLength());
            Assert.Contains(
                activity.GetProperty("raw_events").EnumerateArray(),
                item => item.GetProperty("event_type").GetString() == "call.accepted");
            Assert.Equal(
                "/api/alerts",
                payload.RootElement.GetProperty("system_views").GetProperty("alerts").GetString());
        }

        using (var response = await OperatorGetAsync(
                   host,
                   client,
                   $"/api/evidence/{commandEvidence.ArtifactId:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(
                Encoding.UTF8.GetString(commandBytes),
                payload.RootElement.GetProperty("evidence").GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task Activity_filters_and_cursor_are_stable_and_absence_is_explicit()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        var firstCall = Guid.CreateVersion7();
        var firstAccepted = CreateActivityRecord(
            firstCall,
            Guid.CreateVersion7(),
            1,
            null,
            "call.accepted",
            "2026-08-14T10:00:00.0000000Z",
            "accepted",
            null,
            "codex",
            "openai",
            "gpt-5.6");
        var firstTerminal = CreateActivityRecord(
            firstCall,
            Guid.CreateVersion7(),
            2,
            firstAccepted.EventHash,
            "call.completed",
            "2026-08-14T10:00:01.0000000Z",
            "completed",
            null,
            "codex",
            "openai",
            "gpt-5.6");

        var secondCall = Guid.CreateVersion7();
        var secondAccepted = CreateActivityRecord(
            secondCall,
            Guid.CreateVersion7(),
            3,
            firstTerminal.EventHash,
            "call.accepted",
            "2026-08-14T11:00:00.0000000Z",
            "accepted",
            null,
            null,
            null,
            null);
        var secondTerminal = CreateActivityRecord(
            secondCall,
            Guid.CreateVersion7(),
            4,
            secondAccepted.EventHash,
            "call.failed",
            "2026-08-14T11:00:01.0000000Z",
            "failed",
            null,
            null,
            null,
            null);

        using (var response = await client.SendAsync(IngestJsonRequest(
                   host.Endpoint,
                   firstAccepted.Body,
                   firstTerminal.Body,
                   secondAccepted.Body,
                   secondTerminal.Body)))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string cursor;
        using (var response = await OperatorGetAsync(host, client, "/api/activities?limit=1"))
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var activity = Assert.Single(payload.RootElement.GetProperty("activities").EnumerateArray());
            Assert.Equal(secondCall.ToString("D"), activity.GetProperty("activity_id").GetString());
            Assert.Equal(JsonValueKind.Null, activity.GetProperty("agent").GetProperty("name").ValueKind);
            Assert.Equal(
                "not_supplied_by_client",
                activity.GetProperty("agent").GetProperty("unavailable_reason").GetString());
            cursor = payload.RootElement.GetProperty("next_cursor").GetString()!;
        }

        using (var response = await OperatorGetAsync(
                   host,
                   client,
                   $"/api/activities?limit=1&cursor={cursor}"))
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var activity = Assert.Single(payload.RootElement.GetProperty("activities").EnumerateArray());
            Assert.Equal(firstCall.ToString("D"), activity.GetProperty("activity_id").GetString());
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("next_cursor").ValueKind);
        }

        using (var response = await OperatorGetAsync(
                   host,
                   client,
                   "/api/activities?agent=codex&model=gpt-5.6&state=completed&tool=ptk_invoke"))
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Single(payload.RootElement.GetProperty("activities").EnumerateArray());
        }

        using (var response = await OperatorGetAsync(host, client, "/api/activities?state=made_up"))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var response = await OperatorGetAsync(host, client, "/api/activities?cursor=not-a-cursor"))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Accepted_activity_stays_visible_and_a_late_terminal_updates_the_same_row()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        var callId = Guid.CreateVersion7();
        var accepted = CreateActivityRecord(
            callId,
            Guid.CreateVersion7(),
            1,
            null,
            "call.accepted",
            "2026-08-14T12:00:00.0000000Z",
            "accepted",
            null,
            null,
            null,
            null);
        using (var response = await client.SendAsync(IngestJsonRequest(host.Endpoint, accepted.Body)))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var response = await OperatorGetAsync(host, client, "/api/activities"))
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var activity = Assert.Single(payload.RootElement.GetProperty("activities").EnumerateArray());
            Assert.Equal("accepted", activity.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, activity.GetProperty("terminal_event_id").ValueKind);
            Assert.Equal(JsonValueKind.Null, activity.GetProperty("finished_utc").ValueKind);
        }

        var terminal = CreateActivityRecord(
            callId,
            Guid.CreateVersion7(),
            2,
            accepted.EventHash,
            "call.completed",
            "2026-08-14T12:00:02.0000000Z",
            "completed",
            null,
            null,
            null,
            null);
        using (var response = await client.SendAsync(IngestJsonRequest(host.Endpoint, terminal.Body)))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var response = await OperatorGetAsync(host, client, "/api/activities"))
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var activity = Assert.Single(payload.RootElement.GetProperty("activities").EnumerateArray());
            Assert.Equal(callId.ToString("D"), activity.GetProperty("activity_id").GetString());
            Assert.Equal("completed", activity.GetProperty("state").GetString());
            Assert.Equal(terminal.EventId, activity.GetProperty("terminal_event_id").GetString());
        }
    }

    [Fact]
    public async Task Dashboard_and_health_present_activity_first_without_embedding_evidence()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        using (var response = await client.GetAsync(new Uri(host.OperatorEndpoint, "/api/activities")))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await client.GetAsync(new Uri(
                   host.OperatorEndpoint,
                   $"/api/health?token={SiemReceiverTestHost.OperatorToken}")))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var activityOnIngest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(host.Endpoint, "/api/activities"));
        activityOnIngest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            SiemReceiverTestHost.OperatorToken);
        using (var response = await client.SendAsync(activityOnIngest))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using (var response = await client.GetAsync(new Uri(host.OperatorEndpoint, "/")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Activities", html, StringComparison.Ordinal);
            Assert.Contains("/api/activities", html, StringComparison.Ordinal);
            Assert.Contains("Activity detail", html, StringComparison.Ordinal);
            Assert.Contains("System events", html, StringComparison.Ordinal);
            Assert.Contains("Acknowledge", html, StringComparison.Ordinal);
            Assert.Contains("Accept data loss", html, StringComparison.Ordinal);
            Assert.Contains("textContent", html, StringComparison.Ordinal);
            Assert.DoesNotContain(SiemReceiverTestHost.OperatorToken, html, StringComparison.Ordinal);
        }

        using (var response = await OperatorGetAsync(host, client, "/api/health"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("waiting_for_first_event", payload.RootElement
                .GetProperty("ingest").GetProperty("status").GetString());
            Assert.Equal("complete", payload.RootElement
                .GetProperty("evidence").GetProperty("status").GetString());
            Assert.Equal("intact", payload.RootElement
                .GetProperty("integrity").GetProperty("status").GetString());
            Assert.True(payload.RootElement.GetProperty("custody").TryGetProperty("status", out _));
            Assert.True(payload.RootElement.GetProperty("retention").TryGetProperty("explanation", out _));
        }
    }

    [Fact]
    public async Task Quarantine_list_stays_bounded_but_detail_returns_complete_rejected_evidence()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        const string rejectedRecord = "{ not an audit record";
        using (var response = await client.SendAsync(IngestJsonRequest(host.Endpoint, rejectedRecord)))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        long attemptId;
        using (var response = await OperatorGetAsync(host, client, "/api/quarantine"))
        {
            var listText = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("raw_request_base64", listText, StringComparison.Ordinal);
            using var payload = JsonDocument.Parse(listText);
            var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());
            attemptId = item.GetProperty("attempt_id").GetInt64();
        }

        using (var response = await OperatorGetAsync(
                   host,
                   client,
                   $"/api/quarantine/{attemptId}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var encoded = payload.RootElement.GetProperty("evidence")
                .GetProperty("raw_request_base64").GetString()!;
            var rawRequest = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            Assert.Contains(rejectedRecord, rawRequest, StringComparison.Ordinal);
        }

        using (var response = await client.GetAsync(
                   new Uri(host.OperatorEndpoint, $"/api/quarantine/{attemptId}")))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static OtlpTestRequest.TestAuditRecord CreateActivityRecord(
        Guid callId,
        Guid eventId,
        long sequence,
        string? previousEventHash,
        string eventType,
        string occurredUtc,
        string state,
        string? evidenceManifestJson,
        string? agentName,
        string? modelProvider,
        string? modelName)
    {
        var source = OtlpTestRequest.CreateRecord(
            schemaVersion: "ptk.audit/6",
            eventId: eventId.ToString("D"),
            sequence: sequence,
            previousEventHash: previousEventHash,
            eventType: eventType,
            evidenceManifestJson: evidenceManifestJson);
        var root = JsonNode.Parse(source.Body)!.AsObject();
        root["occurred_utc"] = occurredUtc;
        root["observed_utc"] = occurredUtc;
        root["session"] = new JsonObject
        {
            ["name"] = "default",
            ["generation"] = 1,
        };
        root["actor"] = new JsonObject
        {
            ["transport"] = "stdio",
            ["client_name"] = "codex-cli",
            ["client_version"] = "1.0",
            ["client_session_id"] = "test-session",
            ["attribution_strength"] = "transport_only",
        };
        root["call_attribution"] = new JsonObject
        {
            ["agent_name"] = agentName,
            ["agent_unavailable_reason"] = agentName is null ? "not_supplied_by_client" : null,
            ["model_provider"] = modelProvider,
            ["model_name"] = modelName,
            ["model_unavailable_reason"] = modelName is null ? "not_supplied_by_client" : null,
            ["source"] = agentName is null && modelName is null ? null : "client",
            ["strength"] = agentName is null && modelName is null
                ? "transport_only"
                : "client_asserted",
        };
        root["client_context"] = new JsonObject
        {
            ["task_id"] = "task-123",
            ["task_name"] = "activity projection",
            ["mcp_task_ttl_ms"] = 60000,
            ["task_unavailable_reason"] = null,
            ["run_id"] = "run-123",
            ["run_unavailable_reason"] = null,
            ["source"] = "client",
            ["strength"] = "client_asserted",
        };
        root["execution_context"] = new JsonObject
        {
            ["requested_cwd"] = "/work/repo",
            ["requested_cwd_unavailable_reason"] = null,
            ["effective_cwd"] = "/work/repo",
            ["effective_cwd_unavailable_reason"] = null,
            ["repository_root"] = "/work/repo",
            ["repository_relative_path"] = ".",
            ["repository_unavailable_reason"] = null,
        };
        root["correlation"]!["call_id"] = callId.ToString("D");
        var request = root["request"]!.AsObject();
        request["tool"] = "ptk_invoke";
        request["action"] = "invoke";
        request["route"] = "pwsh";
        request["timeout_ms"] = 30000;
        root["outcome"] = new JsonObject
        {
            ["state"] = state,
            ["detail_code"] = null,
            ["exit_code"] = state == "completed" ? 0 : 1,
            ["duration_ms"] = eventType == "call.accepted" ? null : 1000,
            ["bytes_returned"] = eventType == "call.accepted" ? null : 34,
            ["termination_certainty"] = "confirmed",
        };

        root.Remove("event_hash");
        var preHash = root.ToJsonString();
        var eventHash = Digest(Encoding.UTF8.GetBytes(preHash));
        var body = preHash[..^1] + $",\"event_hash\":\"{eventHash}\"}}";
        return new OtlpTestRequest.TestAuditRecord(
            body,
            eventId.ToString("D"),
            eventType,
            eventHash,
            source.SupervisorBootId,
            sequence,
            "ptk.audit/6");
    }

    private static string CombineManifests(params string[] manifests)
    {
        var combined = new JsonArray();
        foreach (var manifest in manifests)
        {
            foreach (var item in JsonNode.Parse(manifest)!.AsArray())
                combined.Add(item!.DeepClone());
        }
        return combined.ToJsonString();
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Task<HttpResponseMessage> OperatorGetAsync(
        SiemReceiverTestHost host,
        HttpClient client,
        string pathAndQuery)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(host.OperatorEndpoint, pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            SiemReceiverTestHost.OperatorToken);
        return client.SendAsync(request);
    }

    private static HttpRequestMessage IngestJsonRequest(Uri endpoint, params string[] records)
    {
        var builder = new StringBuilder();
        builder.Append("{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[");
        for (var index = 0; index < records.Length; index++)
        {
            if (index > 0) builder.Append(',');
            builder.Append("{\"body\":{\"stringValue\":")
                .Append(JsonSerializer.Serialize(records[index]))
                .Append("}}");
        }
        builder.Append("]}]}]}");
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(builder.ToString())),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", IngestToken);
        return request;
    }
}
