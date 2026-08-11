using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PtkSiemReceiver.Tests;

/// <summary>
/// mini-SIEM S5 (audit-restoration R5b): the read-only operator query API +
/// dashboard on its own listener — events by filters, event detail with
/// chain context, chain status, quarantine evidence — behind the operator
/// bearer token from the protected config. The two surfaces are disjoint:
/// ingest never serves on the operator port and the operator API never
/// serves on ingest, and the two tokens are not interchangeable.
/// </summary>
[Collection(SiemReceiverProcessCollection.Name)]
public sealed class OperatorApiTests
{
    private const string IngestToken = "operator-suite-ingest-0123456789abcdef";

    [Fact]
    public async Task Ingested_records_are_queryable_with_filters_and_detail()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        // End to end: real ingest first, operator queries after.
        var first = OtlpTestRequest.CreateRecord();
        var second = OtlpTestRequest.CreateRecord(
            eventId: "018f6a78-4c20-7a11-8a34-1234567890b1",
            sequence: 2,
            previousEventHash: first.EventHash);
        using (var response = await client.SendAsync(
                   IngestJsonRequest(host.Endpoint, first.Body, second.Body)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The newest-first list, then a type filter that matches everything
        // and a boot filter that matches nothing.
        using (var response = await OperatorGetAsync(host, client, "/api/events"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var events = payload.RootElement.GetProperty("events");
            Assert.Equal(2, events.GetArrayLength());
            Assert.Equal(2, events[0].GetProperty("sequence").GetInt64());
            Assert.Equal(1, events[1].GetProperty("sequence").GetInt64());
        }
        using (var response = await OperatorGetAsync(
                   host, client, "/api/events?type=tool.completed&limit=1"))
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(1, payload.RootElement.GetProperty("events").GetArrayLength());
        }
        using (var response = await OperatorGetAsync(
                   host,
                   client,
                   "/api/events?boot=00000000-0000-4000-8000-000000000000"))
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(0, payload.RootElement.GetProperty("events").GetArrayLength());
        }

        // Detail carries the exact stored body and the chain context.
        using (var response = await OperatorGetAsync(
                   host, client, $"/api/events/{first.EventId}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var detail = payload.RootElement.GetProperty("event");
            Assert.Equal(first.Body, detail.GetProperty("body").GetString());
            Assert.Equal(first.EventId, detail.GetProperty("event_id").GetString());
            var neighbor = Assert.Single(
                payload.RootElement.GetProperty("neighbors").EnumerateArray());
            Assert.Equal(2, neighbor.GetProperty("sequence").GetInt64());
            Assert.Equal(
                2,
                payload.RootElement.GetProperty("chain")
                    .GetProperty("head_sequence").GetInt64());
        }

        // Chains summary counts the stored events.
        using (var response = await OperatorGetAsync(host, client, "/api/chains"))
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var chain = Assert.Single(
                payload.RootElement.GetProperty("chains").EnumerateArray());
            Assert.Equal(2, chain.GetProperty("head_sequence").GetInt64());
            Assert.Equal(2, chain.GetProperty("stored_events").GetInt64());
        }

        // The dashboard serves behind the same token.
        using (var response = await OperatorGetAsync(host, client, "/"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(
                "PTK SIEM Receiver",
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Quarantine_evidence_is_listed_without_raw_bytes()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        var record = OtlpTestRequest.CreateRecord();
        using (var response = await client.SendAsync(IngestJsonRequest(
                   host.Endpoint,
                   record.Body,
                   "{ not an audit record")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var list = await OperatorGetAsync(host, client, "/api/quarantine");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var text = await list.Content.ReadAsStringAsync();
        using var payload = JsonDocument.Parse(text);
        var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("failure_code").GetString()));
        Assert.DoesNotContain("raw_request", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_operator_surface_requires_its_own_token()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        // Missing, wrong, and — critically — the INGEST token are all 401:
        // the two surfaces have distinct credentials.
        using (var missing = await client.GetAsync(new Uri(host.OperatorEndpoint, "/api/events")))
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        foreach (var wrong in new[] { "wrong-token-0123456789abcdef", IngestToken })
        {
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(host.OperatorEndpoint, "/api/events"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wrong);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // The query-parameter form works for the dashboard link.
        using (var viaQuery = await client.GetAsync(new Uri(
                   host.OperatorEndpoint,
                   $"/api/chains?token={SiemReceiverTestHost.OperatorToken}")))
        {
            Assert.Equal(HttpStatusCode.OK, viaQuery.StatusCode);
        }
    }

    [Fact]
    public async Task The_two_surfaces_do_not_serve_each_others_routes()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        // Ingest POST on the operator port: refused, and the operator token
        // buys nothing there.
        var record = OtlpTestRequest.CreateRecord();
        var ingestOnOperator = IngestJsonRequest(
            new Uri(host.OperatorEndpoint, "/v1/logs"),
            record.Body);
        ingestOnOperator.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", SiemReceiverTestHost.OperatorToken);
        using (var response = await client.SendAsync(ingestOnOperator))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Operator GET on the ingest port: refused even with the operator
        // token and a valid transport (ingest bearer mode is enabled).
        var operatorOnIngest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(host.Endpoint, "/api/events"));
        operatorOnIngest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", SiemReceiverTestHost.OperatorToken);
        using (var response = await client.SendAsync(operatorOnIngest))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_rebound_host_header_is_refused_on_the_plain_http_surface()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(host.OperatorEndpoint, "/api/chains"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", SiemReceiverTestHost.OperatorToken);
        request.Headers.Host = "rebound.example";
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Helpers ----

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

    /// <summary>The exporter-shaped JSON envelope, addressed explicitly so
    /// the same body can be aimed at either surface.</summary>
    private static HttpRequestMessage IngestJsonRequest(Uri endpoint, params string[] records)
    {
        var builder = new StringBuilder();
        builder.Append(
            "{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[");
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
