using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PtkSiemReceiver.Tests;

/// <summary>
/// mini-SIEM S6 first half (audit-restoration R5c): the gap-disposition
/// state machine. A chain gap opens durable gap evidence; later valid
/// records beyond the gap are stored flagged post-gap with sub-chain
/// continuity enforced — never silently re-anchored; the sole resumption
/// authority is an authenticated operator disposition, custody-recorded;
/// and the state survives a restart.
/// </summary>
[Collection(SiemReceiverProcessCollection.Name)]
public sealed class GapDispositionTests
{
    private const string IngestToken = "gap-suite-ingest-0123456789abcdef";

    [Fact]
    public async Task A_chain_gap_stores_post_gap_evidence_and_disposition_resumes()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        var chain = BuildChain(6);

        // Sequence 1 lands; sequence 2 is lost; sequence 3 is a chain gap.
        Assert.Equal(HttpStatusCode.OK, await IngestAsync(client, host, chain[0].Body));
        Assert.Equal(
            HttpStatusCode.BadRequest, await IngestAsync(client, host, chain[2].Body));

        // The rejection left durable gap evidence, open, one per boot.
        var gap = await SingleGapAsync(host, client);
        Assert.Equal("open", gap.GetProperty("state").GetString());
        Assert.Equal(3, gap.GetProperty("claimed_sequence").GetInt64());
        Assert.Equal(1, gap.GetProperty("observed_head_sequence").GetInt64());
        var gapId = gap.GetProperty("gap_id").GetInt64();

        // The producer's retry of the same record now anchors the post-gap
        // sub-chain: stored, flagged, acknowledged — head stays frozen.
        Assert.Equal(HttpStatusCode.OK, await IngestAsync(client, host, chain[2].Body));
        Assert.Equal(HttpStatusCode.OK, await IngestAsync(client, host, chain[3].Body));
        Assert.Equal(1, await HeadSequenceAsync(host, client));
        var events = await EventsAsync(host, client);
        Assert.True(events.Single(e =>
                e.GetProperty("sequence").GetInt64() == 3)
            .GetProperty("post_gap").GetBoolean());
        Assert.False(events.Single(e =>
                e.GetProperty("sequence").GetInt64() == 1)
            .GetProperty("post_gap").GetBoolean());

        // A byte-identical post-gap replay stays idempotent.
        Assert.Equal(HttpStatusCode.OK, await IngestAsync(client, host, chain[3].Body));

        // A record that skips past the stored sub-chain is rejected, and a
        // second gap never opens while the first is undecided.
        Assert.Equal(
            HttpStatusCode.BadRequest, await IngestAsync(client, host, chain[5].Body));
        _ = await SingleGapAsync(host, client);

        // Disposition refusals: garbage body, unknown gap.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await DispositionAsync(host, client, gapId, "shrugged")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await DispositionAsync(host, client, 424242, "resolved")).StatusCode);

        // The operator's disposition resumes the chain at the stored
        // sub-chain: the head moves to its tail and the gap records who.
        using (var response = await DispositionAsync(host, client, gapId, "resolved"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("resumed", payload.RootElement.GetProperty("state").GetString());
        }

        Assert.Equal(4, await HeadSequenceAsync(host, client));
        gap = await SingleGapAsync(host, client);
        Assert.Equal("resumed", gap.GetProperty("state").GetString());
        Assert.Equal("resolved", gap.GetProperty("disposition").GetString());
        Assert.Equal(
            chain[2].EventId, gap.GetProperty("resume_event_id").GetString());
        var expectedActor = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(SiemReceiverTestHost.OperatorToken)))
            .ToLowerInvariant();
        Assert.Equal(expectedActor, gap.GetProperty("disposition_actor").GetString());

        // A second disposition is an illegal transition.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await DispositionAsync(host, client, gapId, "resolved")).StatusCode);

        // After the resume, ordinary chaining continues from the sub-chain
        // tail, unflagged.
        Assert.Equal(HttpStatusCode.OK, await IngestAsync(client, host, chain[4].Body));
        Assert.Equal(5, await HeadSequenceAsync(host, client));
        events = await EventsAsync(host, client);
        Assert.False(events.Single(e =>
                e.GetProperty("sequence").GetInt64() == 5)
            .GetProperty("post_gap").GetBoolean());
    }

    [Fact]
    public async Task A_disposition_without_stored_post_gap_records_survives_restart_and_resumes()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        var chain = BuildChain(3);
        string hostRoot;

        // Reject → operator disposition, then a full restart: the plan's
        // reject → disposition → restart → resume ordering.
        {
            await using var first = await SiemReceiverTestHost.StartAsync(
                server,
                [root],
                ingestToken: IngestToken,
                preserveRootOnDispose: true);
            hostRoot = first.Root;
            using var client = first.CreateClient();
            Assert.Equal(HttpStatusCode.OK, await IngestAsync(client, first, chain[0].Body));
            Assert.Equal(
                HttpStatusCode.BadRequest, await IngestAsync(client, first, chain[2].Body));
            var gap = await SingleGapAsync(first, client);
            using var response = await DispositionAsync(
                first, client, gap.GetProperty("gap_id").GetInt64(), "accepted-loss");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(
                "dispositioned", payload.RootElement.GetProperty("state").GetString());
        }

        await using var restarted = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken,
            existingRoot: hostRoot);
        using (var client = restarted.CreateClient())
        {
            // The disposition survived the restart on the durable row.
            var gap = await SingleGapAsync(restarted, client);
            Assert.Equal("dispositioned", gap.GetProperty("state").GetString());
            Assert.Equal("accepted-loss", gap.GetProperty("disposition").GetString());

            // The first record beyond the gap anchors AND resumes: the
            // operator already authorized it.
            Assert.Equal(
                HttpStatusCode.OK, await IngestAsync(client, restarted, chain[2].Body));
            gap = await SingleGapAsync(restarted, client);
            Assert.Equal("resumed", gap.GetProperty("state").GetString());
            Assert.Equal(3, await HeadSequenceAsync(restarted, client));
            var events = await EventsAsync(restarted, client);
            Assert.True(events.Single(e =>
                    e.GetProperty("sequence").GetInt64() == 3)
                .GetProperty("post_gap").GetBoolean());
        }
    }

    [Fact]
    public async Task A_version_one_store_migrates_to_the_gap_capable_schema()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        var chain = BuildChain(4);
        string hostRoot;
        string databasePath;

        {
            await using var first = await SiemReceiverTestHost.StartAsync(
                server,
                [root],
                ingestToken: IngestToken,
                preserveRootOnDispose: true);
            hostRoot = first.Root;
            databasePath = first.DatabasePath;
            using var client = first.CreateClient();
            Assert.Equal(HttpStatusCode.OK, await IngestAsync(client, first, chain[0].Body));
        }

        // Reconstruct the genuine v1 shape from the v2 store: drop exactly
        // what the v2 migration adds, and roll the recorded versions back.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                   new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                   {
                       DataSource = databasePath,
                       Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                       Pooling = false,
                   }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE gaps;
                DROP TABLE alert_queue;
                DROP TABLE alerts;
                DELETE FROM meta WHERE key = 'alert_cursor';
                ALTER TABLE events DROP COLUMN post_gap;
                UPDATE meta SET value = '1' WHERE key = 'schema_version';
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }

        // Reopening migrates and the whole gap machinery works on the
        // migrated store.
        await using var restarted = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken,
            existingRoot: hostRoot);
        using (var client = restarted.CreateClient())
        {
            Assert.Equal(
                HttpStatusCode.OK, await IngestAsync(client, restarted, chain[1].Body));
            Assert.Equal(
                HttpStatusCode.BadRequest,
                await IngestAsync(client, restarted, chain[3].Body));
            var gap = await SingleGapAsync(restarted, client);
            Assert.Equal("open", gap.GetProperty("state").GetString());
        }
    }

    // ---- Helpers ----

    /// <summary>A hash-linked chain of records with UUIDv7 event IDs,
    /// sequence 1..count.</summary>
    private static List<OtlpTestRequest.TestAuditRecord> BuildChain(int count)
    {
        var records = new List<OtlpTestRequest.TestAuditRecord>(count);
        string? previousHash = null;
        for (var sequence = 1; sequence <= count; sequence++)
        {
            var record = OtlpTestRequest.CreateRecord(
                eventId: $"33333333-4444-7555-8666-{sequence:D12}",
                sequence: sequence,
                previousEventHash: previousHash);
            records.Add(record);
            previousHash = record.EventHash;
        }

        return records;
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

    private static Task<HttpResponseMessage> DispositionAsync(
        SiemReceiverTestHost host,
        HttpClient client,
        long gapId,
        string disposition)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(host.OperatorEndpoint, $"/api/gaps/{gapId}/disposition"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { disposition }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", SiemReceiverTestHost.OperatorToken);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> SingleGapAsync(
        SiemReceiverTestHost host,
        HttpClient client)
    {
        using var response = await OperatorGetAsync(host, client, "/api/gaps");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Assert.Single(payload.RootElement.GetProperty("gaps").EnumerateArray());
    }

    private static async Task<long> HeadSequenceAsync(
        SiemReceiverTestHost host,
        HttpClient client)
    {
        using var response = await OperatorGetAsync(host, client, "/api/chains");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var chain = Assert.Single(payload.RootElement.GetProperty("chains").EnumerateArray());
        return chain.GetProperty("head_sequence").GetInt64();
    }

    private static async Task<List<JsonElement>> EventsAsync(
        SiemReceiverTestHost host,
        HttpClient client)
    {
        using var response = await OperatorGetAsync(host, client, "/api/events");
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("events").EnumerateArray().ToList();
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
}
