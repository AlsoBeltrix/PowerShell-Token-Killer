using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PtkSiemReceiver.Tests;

[Collection(SiemReceiverProcessCollection.Name)]
public sealed class DestinationObligationPersistenceTests
{
    private const string IngestToken = "destination-obligation-ingest-token-012345";

    [Fact]
    public async Task V6_destination_obligations_survive_storage_and_both_event_apis()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();
        var firstDestination = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var secondDestination = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var manifest = JsonSerializer.Serialize(new[]
        {
            new
            {
                evidence_id = Guid.NewGuid().ToString("D"),
                envelope_event_id = Guid.CreateVersion7().ToString("D"),
                evidence_kind = "submitted_command",
                digest = new string('a', 64),
                byte_count = 4,
                encoding = "utf-8",
                artifact_id = Guid.NewGuid().ToString("D"),
                artifact_digest = new string('b', 64),
                artifact_byte_count = 4,
                chunk_index = 0,
                chunk_count = 1,
                chunk_offset = 0,
                retention_class = "forensic",
                capture_state = "complete",
            },
        });
        var record = OtlpTestRequest.CreateRecord(
            schemaVersion: "ptk.audit/6",
            eventType: "server.started",
            evidenceManifestJson: manifest,
            requiredDestinationIds: [firstDestination, secondDestination]);

        using (var ingest = IngestJsonRequest(host.Endpoint, record.Body))
        using (var response = await client.SendAsync(ingest))
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using (var response = await OperatorGetAsync(host, client, "/api/events"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var stored = Assert.Single(
                payload.RootElement.GetProperty("events").EnumerateArray());
            Assert.Equal(
                new[] { firstDestination.ToString("D"), secondDestination.ToString("D") },
                stored.GetProperty("required_destination_ids")
                    .EnumerateArray()
                    .Select(item => item.GetString()));
        }

        using (var response = await OperatorGetAsync(
                   host,
                   client,
                   $"/api/events/{record.EventId}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var detail = payload.RootElement.GetProperty("event");
            Assert.Equal(
                new[] { firstDestination.ToString("D"), secondDestination.ToString("D") },
                detail.GetProperty("required_destination_ids")
                    .EnumerateArray()
                    .Select(item => item.GetString()));
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = host.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM evidence_manifest_items
            WHERE source_event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", record.EventId);
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
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
}
