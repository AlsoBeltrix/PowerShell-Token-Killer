using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkSiemReceiver.Configuration;

namespace PtkSiemReceiver.Tests;

/// <summary>
/// mini-SIEM S6 second half (audit-restoration R5c): the alert pipeline.
/// Ingest durably enqueues work items in its own transaction; a persisted
/// cursor plus idempotent replay makes every committed item yield its alert
/// exactly once — including across a crash before alert persistence; the
/// lifecycle is open → acknowledged → closed and nothing else; the webhook
/// fires after commit with a bounded retry and can never block evaluation.
/// </summary>
[Collection(SiemReceiverProcessCollection.Name)]
public sealed class AlertPipelineTests
{
    private const string IngestToken = "alert-suite-ingest-0123456789abcdef";

    private static readonly IReadOnlyList<AlertRule> StandardRules =
    [
        new("completed", "event_match", "tool.completed", null, null),
        new("breaks", "chain_break", null, null, null),
        new("gaps", "gap_detected", null, null, null),
    ];

    [Fact]
    public async Task Committed_work_items_yield_alerts_and_the_webhook_fires()
    {
        using var webhook = new RecordingWebhookServer(statusCode: 200);
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken,
            alertRules: StandardRules,
            alertWebhookUrl: webhook.Url);
        using var client = host.CreateClient();

        // One accepted event, one chain break, one chain gap: three durable
        // rows, three work items, three distinct rules.
        var first = OtlpTestRequest.CreateRecord();
        var broken = OtlpTestRequest.CreateRecord(
            eventId: "44444444-5555-7666-8777-000000000002",
            sequence: 2,
            previousEventHash: new string('e', 64));
        var gapped = OtlpTestRequest.CreateRecord(
            eventId: "44444444-5555-7666-8777-000000000004",
            sequence: 4,
            previousEventHash: new string('f', 64));
        Assert.Equal(HttpStatusCode.OK, await IngestAsync(client, host, first.Body));
        Assert.Equal(
            HttpStatusCode.BadRequest, await IngestAsync(client, host, broken.Body));
        Assert.Equal(
            HttpStatusCode.BadRequest, await IngestAsync(client, host, gapped.Body));

        var alerts = await PollAlertsAsync(host, client, expectedCount: 3);
        var byRule = alerts.ToDictionary(a => a.GetProperty("rule").GetString()!);
        Assert.Equal("open", byRule["completed"].GetProperty("state").GetString());
        Assert.Equal("event", byRule["completed"].GetProperty("subject_kind").GetString());
        Assert.Contains(
            "tool.completed",
            byRule["completed"].GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("quarantine", byRule["breaks"].GetProperty("subject_kind").GetString());
        Assert.Equal("gap", byRule["gaps"].GetProperty("subject_kind").GetString());

        // Every alert reached the webhook after its commit.
        await WaitUntilAsync(
            () => webhook.Requests.Count >= 3,
            "the webhook never saw all three alerts");
        Assert.Contains(webhook.Requests, r => r.Contains("\"rule\":\"completed\"", StringComparison.Ordinal));
        Assert.Contains(webhook.Requests, r => r.Contains("\"rule\":\"breaks\"", StringComparison.Ordinal));
        Assert.Contains(webhook.Requests, r => r.Contains("\"rule\":\"gaps\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_crash_before_alert_persistence_yields_the_alert_after_restart()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        var record = OtlpTestRequest.CreateRecord();
        string hostRoot;

        // The kill test: the ingest transaction committed the work item, and
        // the receiver dies before any alert is persisted (the hold flag is
        // the deterministic crash point).
        {
            await using var first = await SiemReceiverTestHost.StartAsync(
                server,
                [root],
                ingestToken: IngestToken,
                alertRules: StandardRules,
                alertEvaluationHoldForTests: true,
                preserveRootOnDispose: true);
            hostRoot = first.Root;
            using var client = first.CreateClient();
            Assert.Equal(HttpStatusCode.OK, await IngestAsync(client, first, record.Body));
            using var response = await OperatorGetAsync(first, client, "/api/alerts");
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Empty(payload.RootElement.GetProperty("alerts").EnumerateArray());
        }

        // Restart with a CHANGED rule set (one added): replay evaluates the
        // committed item, and the alert records BOTH config identities so
        // the rule change across the crash is evident, never silent.
        var changedRules = StandardRules
            .Append(new AlertRule("burst", "ingest_rate", null, 1000, 60))
            .ToList();
        await using var restarted = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken,
            existingRoot: hostRoot,
            alertRules: changedRules);
        using (var client = restarted.CreateClient())
        {
            var alerts = await PollAlertsAsync(restarted, client, expectedCount: 1);
            var alert = alerts.Single();
            Assert.Equal("completed", alert.GetProperty("rule").GetString());
            var enqueueHash = alert.GetProperty("enqueue_config_hash").GetString();
            var evaluationHash = alert.GetProperty("evaluation_config_hash").GetString();
            Assert.Equal(AlertRuleSet.ComputeConfigHash(StandardRules), enqueueHash);
            Assert.Equal(AlertRuleSet.ComputeConfigHash(changedRules), evaluationHash);
            Assert.NotEqual(enqueueHash, evaluationHash);
        }
    }

    [Fact]
    public async Task Alert_transitions_follow_the_lifecycle_exactly()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken,
            alertRules: StandardRules);
        using var client = host.CreateClient();

        Assert.Equal(
            HttpStatusCode.OK,
            await IngestAsync(client, host, OtlpTestRequest.CreateRecord().Body));
        var alert = (await PollAlertsAsync(host, client, expectedCount: 1)).Single();
        var alertId = alert.GetProperty("alert_id").GetInt64();

        // open → closed skips acknowledgement: illegal.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await TransitionAsync(host, client, alertId, "closed")).StatusCode);
        // Unknown alert and garbage state.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await TransitionAsync(host, client, 424242, "acknowledged")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await TransitionAsync(host, client, alertId, "resolved")).StatusCode);

        // The legal path, each step recorded with the operator identity.
        Assert.Equal(
            HttpStatusCode.OK,
            (await TransitionAsync(host, client, alertId, "acknowledged")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await TransitionAsync(host, client, alertId, "acknowledged")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await TransitionAsync(host, client, alertId, "closed")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await TransitionAsync(host, client, alertId, "acknowledged")).StatusCode);

        using var closed = await OperatorGetAsync(host, client, "/api/alerts?state=closed");
        using var payload = JsonDocument.Parse(await closed.Content.ReadAsStringAsync());
        var row = Assert.Single(payload.RootElement.GetProperty("alerts").EnumerateArray());
        Assert.Equal(alertId, row.GetProperty("alert_id").GetInt64());
        var expectedActor = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(SiemReceiverTestHost.OperatorToken)))
            .ToLowerInvariant();
        Assert.Equal(expectedActor, row.GetProperty("updated_by").GetString());

        using var badFilter = await OperatorGetAsync(host, client, "/api/alerts?state=weird");
        Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
    }

    [Fact]
    public async Task Detail_json_survives_metacharacters_in_event_type()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        // The stored event type contains a literal double quote (the record
        // body carries it JSON-escaped); the rule names the same value.
        const string QuotedType = "tool.\"q";
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken,
            alertRules: [new AlertRule("quoted", "event_match", QuotedType, null, null)]);
        using var client = host.CreateClient();

        var record = OtlpTestRequest.CreateRecord(eventType: "tool.\\\"q");
        Assert.Equal(HttpStatusCode.OK, await IngestAsync(client, host, record.Body));

        // cr8-7: interpolation stored malformed detail here; a serializer
        // must round-trip the exact value.
        var alert = (await PollAlertsAsync(host, client, expectedCount: 1)).Single();
        using var detail = JsonDocument.Parse(alert.GetProperty("detail").GetString()!);
        Assert.Equal(
            QuotedType, detail.RootElement.GetProperty("event_type").GetString());
    }

    [Fact]
    public async Task Webhook_failure_is_bounded_and_never_blocks_evaluation()
    {
        using var webhook = new RecordingWebhookServer(statusCode: 500);
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken,
            alertRules: [new AlertRule("completed", "event_match", "tool.completed", null, null)],
            alertWebhookUrl: webhook.Url);
        using var client = host.CreateClient();

        var first = OtlpTestRequest.CreateRecord();
        var second = OtlpTestRequest.CreateRecord(
            eventId: "55555555-6666-7777-8888-000000000002",
            sequence: 2,
            previousEventHash: first.EventHash);
        Assert.Equal(HttpStatusCode.OK, await IngestAsync(client, host, first.Body));
        Assert.Equal(HttpStatusCode.OK, await IngestAsync(client, host, second.Body));

        // Both alerts exist although delivery never succeeds — the webhook
        // is reporting, not a gate — and each alert's retry is bounded.
        _ = await PollAlertsAsync(host, client, expectedCount: 2);
        await WaitUntilAsync(
            () => webhook.Requests.Count >= 2 * Alerting.AlertEvaluationService.WebhookAttempts,
            "the bounded retries never completed");
        await Task.Delay(750);
        Assert.Equal(
            2 * Alerting.AlertEvaluationService.WebhookAttempts,
            webhook.Requests.Count);
    }

    // ---- Helpers ----

    private static async Task<List<JsonElement>> PollAlertsAsync(
        SiemReceiverTestHost host,
        HttpClient client,
        int expectedCount)
    {
        List<JsonElement> alerts = [];
        await WaitUntilAsync(
            async () =>
            {
                using var response = await OperatorGetAsync(host, client, "/api/alerts");
                if (response.StatusCode != HttpStatusCode.OK) return false;
                var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                alerts = payload.RootElement.GetProperty("alerts")
                    .EnumerateArray().ToList();
                return alerts.Count >= expectedCount;
            },
            $"expected {expectedCount} alerts");
        Assert.Equal(expectedCount, alerts.Count);
        return alerts;
    }

    private static Task WaitUntilAsync(Func<bool> condition, string failure) =>
        WaitUntilAsync(() => Task.FromResult(condition()), failure);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }

        Assert.Fail(failure);
    }

    private static async Task<HttpStatusCode> IngestAsync(
        HttpClient client,
        SiemReceiverTestHost host,
        string recordBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, host.Endpoint)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(
                "{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[" +
                "{\"body\":{\"stringValue\":" +
                JsonSerializer.Serialize(recordBody) +
                "}}]}]}]}")),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", IngestToken);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static Task<HttpResponseMessage> TransitionAsync(
        SiemReceiverTestHost host,
        HttpClient client,
        long alertId,
        string state)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(host.OperatorEndpoint, $"/api/alerts/{alertId}/transition"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { state }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", SiemReceiverTestHost.OperatorToken);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> OperatorGetAsync(
        SiemReceiverTestHost host,
        HttpClient client,
        string pathAndQuery)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(host.OperatorEndpoint, pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", SiemReceiverTestHost.OperatorToken);
        return client.SendAsync(request);
    }

    /// <summary>Loopback webhook target recording every request body and
    /// answering a fixed status.</summary>
    private sealed class RecordingWebhookServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly List<string> _requests = [];
        private readonly CancellationTokenSource _stop = new();

        internal RecordingWebhookServer(int statusCode)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/alerts";
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch
                    {
                        return;
                    }

                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    lock (_requests) _requests.Add(body);
                    context.Response.StatusCode = statusCode;
                    context.Response.Close();
                }
            });
        }

        internal string Url { get; }

        internal IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests) return _requests.ToList();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }

        private static int FreePort()
        {
            using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }
    }
}
