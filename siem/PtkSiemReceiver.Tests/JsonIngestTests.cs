using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PtkSiemReceiver.Ingest;

namespace PtkSiemReceiver.Tests;

/// <summary>
/// audit-restoration R3c: the receiver accepts the OTLP/HTTP JSON encoding
/// PTK's own exporter emits — batched, generic-collector shaped — and a
/// bearer-token authentication mode so PTK reaches its own fallback receiver
/// with the same endpoint-plus-token configuration it uses for Splunk,
/// Sentinel, or any OTLP collector. The mTLS + protobuf path is unchanged
/// and stays pinned by the existing suites.
/// </summary>
[Collection(SiemReceiverProcessCollection.Name)]
public sealed class JsonIngestTests
{
    private const string IngestToken = "test-ingest-token-0123456789abcdef";

    // ---- Validator: the JSON envelope is transport, the body is evidence ----

    [Fact]
    public void A_producer_shaped_batch_validates_every_record_in_order()
    {
        var first = OtlpTestRequest.CreateRecord();
        var second = OtlpTestRequest.CreateRecord(
            eventId: "018f6a78-4c20-7a11-8a34-1234567890ac",
            sequence: 2,
            previousEventHash: first.EventHash);

        var outcome = OtlpRequestValidator.ValidateJsonRequest(
            ExporterJsonBytes(first.Body, second.Body));

        Assert.Null(outcome.RequestFailureCode);
        Assert.Equal(2, outcome.Results.Count);
        Assert.All(outcome.Results, result => Assert.True(result.IsValid));
        Assert.Equal(1, outcome.Results[0].Record!.Sequence);
        Assert.Equal(2, outcome.Results[1].Record!.Sequence);
        Assert.Equal(first.EventId, outcome.Results[0].Record!.EventId.ToString("D"));
        // Per-record raw evidence is the exact log-record JSON, not the whole
        // request: the producer regroups batches across retries, and identical
        // per-record bytes are what keeps an honest replay idempotent.
        var raw = Encoding.UTF8.GetString(outcome.Results[0].Record!.RawRequestBytes);
        using var rawDocument = JsonDocument.Parse(raw);
        Assert.Equal(
            first.Body,
            rawDocument.RootElement.GetProperty("body").GetProperty("stringValue").GetString());
    }

    [Fact]
    public void The_record_body_is_validated_as_deeply_as_the_protobuf_path()
    {
        // The shared core is the point: a record whose event_hash does not
        // recompute must fail identically on both encodings.
        var record = OtlpTestRequest.CreateRecord();
        var tampered = record.Body.Replace(
            "\"event_type\":\"tool.completed\"",
            "\"event_type\":\"tool.tampered\"",
            StringComparison.Ordinal);

        var outcome = OtlpRequestValidator.ValidateJsonRequest(ExporterJsonBytes(tampered));

        Assert.Null(outcome.RequestFailureCode);
        var result = Assert.Single(outcome.Results);
        Assert.False(result.IsValid);
        Assert.Equal("event_hash", result.FailureCode);
        Assert.Equal(record.EventId, result.RejectedAttempt!.ClaimedEventId);
    }

    [Fact]
    public void A_poison_record_costs_one_record_not_the_batch()
    {
        var first = OtlpTestRequest.CreateRecord();
        var third = OtlpTestRequest.CreateRecord(
            eventId: "018f6a78-4c20-7a11-8a34-1234567890ad",
            sequence: 2,
            previousEventHash: first.EventHash);

        var outcome = OtlpRequestValidator.ValidateJsonRequest(
            ExporterJsonBytes(first.Body, "{ this is not an audit record", third.Body));

        Assert.Null(outcome.RequestFailureCode);
        Assert.Equal(3, outcome.Results.Count);
        Assert.True(outcome.Results[0].IsValid);
        Assert.False(outcome.Results[1].IsValid);
        Assert.True(outcome.Results[2].IsValid);
    }

    [Fact]
    public void A_lying_indexing_hint_rejects_the_record()
    {
        // Transport decorations carry no authority, but they must not
        // contradict the evidence they decorate.
        var record = OtlpTestRequest.CreateRecord();
        var envelope =
            "{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[" +
            "{\"body\":{\"stringValue\":" + JsonSerializer.Serialize(record.Body) + "}," +
            "\"attributes\":[{\"key\":\"ptk.event_id\",\"value\":{\"stringValue\":" +
            "\"018f6a78-4c20-7a11-8a34-000000000000\"}}]}]}]}]}";

        var outcome = OtlpRequestValidator.ValidateJsonRequest(Encoding.UTF8.GetBytes(envelope));

        var result = Assert.Single(outcome.Results);
        Assert.False(result.IsValid);
        Assert.Equal("attributes", result.FailureCode);
    }

    [Fact]
    public void Envelope_failures_are_request_level_and_read_nothing()
    {
        Assert.Equal(
            "otlp_json",
            OtlpRequestValidator.ValidateJsonRequest("not json"u8.ToArray()).RequestFailureCode);
        Assert.Equal(
            "otlp_shape",
            OtlpRequestValidator.ValidateJsonRequest("{\"foo\":1}"u8.ToArray()).RequestFailureCode);
        Assert.Equal(
            "otlp_shape",
            OtlpRequestValidator.ValidateJsonRequest(
                "{\"resourceLogs\":[{\"scopeLogs\":\"nope\"}]}"u8.ToArray()).RequestFailureCode);

        // Proto3 JSON omits empty repeated fields: an empty export is not an
        // error, it is zero records.
        var empty = OtlpRequestValidator.ValidateJsonRequest(
            "{\"resourceLogs\":[]}"u8.ToArray());
        Assert.Null(empty.RequestFailureCode);
        Assert.Empty(empty.Results);
    }

    [Fact]
    public void A_log_record_without_a_string_body_is_rejected_per_record()
    {
        var record = OtlpTestRequest.CreateRecord();
        var envelope =
            "{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[" +
            "{\"body\":{\"intValue\":\"7\"}}," +
            "{\"body\":{\"stringValue\":" + JsonSerializer.Serialize(record.Body) + "}}" +
            "]}]}]}";

        var outcome = OtlpRequestValidator.ValidateJsonRequest(Encoding.UTF8.GetBytes(envelope));

        Assert.Null(outcome.RequestFailureCode);
        Assert.Equal(2, outcome.Results.Count);
        Assert.Equal("log_shape", outcome.Results[0].FailureCode);
        Assert.True(outcome.Results[1].IsValid);
    }

    // ---- End to end: token auth over the real transport and store ----

    [Fact]
    public async Task A_bearer_client_without_a_certificate_ingests_a_json_batch()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        var first = OtlpTestRequest.CreateRecord();
        var second = OtlpTestRequest.CreateRecord(
            eventId: "018f6a78-4c20-7a11-8a34-1234567890ae",
            sequence: 2,
            previousEventHash: first.EventHash);
        using var response = await client.SendAsync(
            JsonRequest(host.Endpoint, IngestToken, first.Body, second.Body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM quarantine;"));
        // The custody credential identity is the token's SHA-256 — naming the
        // credential that delivered the records, never the credential itself.
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IngestToken)))
                .ToLowerInvariant(),
            DatabaseString(
                host.DatabasePath,
                "SELECT DISTINCT client_certificate_thumbprint FROM custody;"));
    }

    [Fact]
    public async Task A_wrong_or_missing_bearer_token_is_401_and_commits_nothing()
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

        using (var wrong = await client.SendAsync(
                   JsonRequest(host.Endpoint, "wrong-token-0123456789abcdef", record.Body)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var missing = JsonRequest(host.Endpoint, token: null, record.Body);
        using (var response = await client.SendAsync(missing))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM custody;"));
    }

    [Fact]
    public async Task Without_a_configured_token_a_certificateless_client_cannot_connect()
    {
        // No token in the configuration means the R3c mode is off and the
        // TLS layer still requires a client certificate outright.
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(server, [root]);
        using var client = host.CreateClient();
        var record = OtlpTestRequest.CreateRecord();

        // The refusal must be the TLS handshake itself, not an HTTP status:
        // a transport-level HttpRequestException carries no status code.
        var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.SendAsync(JsonRequest(host.Endpoint, IngestToken, record.Body)));
        Assert.Null(exception.StatusCode);
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
    }

    [Fact]
    public async Task An_mtls_client_still_ingests_protobuf_when_a_token_is_configured()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient(clientCertificate);

        using var response = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(clientCertificate.RawData)).ToLowerInvariant(),
            DatabaseString(
                host.DatabasePath,
                "SELECT client_certificate_thumbprint FROM custody;"));
    }

    [Fact]
    public async Task A_regrouped_batch_replay_is_idempotent_without_quarantine()
    {
        // The producer redelivers at least once and regroups batches across
        // retries. The same record arriving inside a different envelope must
        // read as the identical event, not as "same event, different bytes".
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        var first = OtlpTestRequest.CreateRecord();
        var second = OtlpTestRequest.CreateRecord(
            eventId: "018f6a78-4c20-7a11-8a34-1234567890af",
            sequence: 2,
            previousEventHash: first.EventHash);

        using (var batch = await client.SendAsync(
                   JsonRequest(host.Endpoint, IngestToken, first.Body, second.Body)))
        {
            Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        }

        // The tail record alone, regrouped into its own envelope.
        using (var replay = await client.SendAsync(
                   JsonRequest(host.Endpoint, IngestToken, second.Body)))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        Assert.Equal(2L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM quarantine;"));
    }

    [Fact]
    public async Task A_cross_encoding_replay_of_the_same_event_is_idempotent()
    {
        // cr4-5: a record's identity is its exact JSONL body, never the
        // transport envelope. The same honest event delivered first through
        // mTLS/protobuf and replayed through token/JSON must be idempotent —
        // not a duplicate_mismatch quarantine.
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);

        var record = OtlpTestRequest.CreateRecord();
        using (var mtls = host.CreateClient(clientCertificate))
        using (var response = await mtls.PostAsync(host.Endpoint, OtlpTestRequest.Content()))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var bearer = host.CreateClient();
        using (var replay = await bearer.SendAsync(
                   JsonRequest(host.Endpoint, IngestToken, record.Body)))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        Assert.Equal(1L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM quarantine;"));
    }

    [Fact]
    public async Task A_batch_with_one_poison_record_commits_the_rest_and_returns_permanent()
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
        using var response = await client.SendAsync(JsonRequest(
            host.Endpoint,
            IngestToken,
            record.Body,
            "{ not an audit record"));

        // Permanent: the producer's isolation retries record-by-record and
        // the good records replay idempotently.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(1L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM quarantine;"));
    }

    // ---- Helpers ----

    /// <summary>The exact envelope PTK's exporter emits (HttpAuditDestination
    /// .FormatOtlpLogs), including the indexing hints; the R5 conformance
    /// suite owns keeping this and the producer from drifting apart.</summary>
    private static byte[] ExporterJsonBytes(params string[] records)
    {
        var builder = new StringBuilder();
        builder.Append(
            "{\"resourceLogs\":[{\"resource\":{\"attributes\":[" +
            "{\"key\":\"service.name\",\"value\":{\"stringValue\":\"ptk\"}}]}," +
            "\"scopeLogs\":[{\"scope\":{\"name\":\"ptk.audit\"},\"logRecords\":[");
        for (var index = 0; index < records.Length; index++)
        {
            if (index > 0) builder.Append(',');
            AppendExporterLogRecord(builder, records[index]);
        }
        builder.Append("]}]}]}");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void AppendExporterLogRecord(StringBuilder builder, string record)
    {
        string? eventType = null;
        string? eventId = null;
        try
        {
            using var document = JsonDocument.Parse(record);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                eventType = document.RootElement.TryGetProperty("event_type", out var type) &&
                    type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : null;
                eventId = document.RootElement.TryGetProperty("event_id", out var id) &&
                    id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
            }
        }
        catch (JsonException)
        {
            // Delivered verbatim without hints, exactly as the producer does.
        }

        builder.Append("{\"timeUnixNano\":\"1755000000000000000\"," +
                       "\"severityText\":\"INFO\",\"body\":{\"stringValue\":");
        builder.Append(JsonSerializer.Serialize(record));
        builder.Append("},\"attributes\":[");
        var wrote = false;
        if (eventType is not null)
        {
            builder.Append("{\"key\":\"ptk.event_type\",\"value\":{\"stringValue\":")
                .Append(JsonSerializer.Serialize(eventType))
                .Append("}}");
            wrote = true;
        }
        if (eventId is not null)
        {
            if (wrote) builder.Append(',');
            builder.Append("{\"key\":\"ptk.event_id\",\"value\":{\"stringValue\":")
                .Append(JsonSerializer.Serialize(eventId))
                .Append("}}");
        }
        builder.Append("]}");
    }

    private static HttpRequestMessage JsonRequest(
        Uri endpoint,
        string? token,
        params string[] records)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(ExporterJsonBytes(records)),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static long DatabaseInt64(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? throw new InvalidOperationException(sql));
    }

    private static string DatabaseString(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(command.ExecuteScalar() ?? throw new InvalidOperationException(sql));
    }
}
