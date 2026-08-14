using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PtkSiemReceiver.Ingest;
using PtkSiemReceiver.Security;
using PtkSiemReceiver.Storage;

namespace PtkSiemReceiver.Tests;

[Collection(SiemReceiverProcessCollection.Name)]
public sealed class EvidenceEnvelopeTests
{
    private const string IngestToken = "evidence-ingest-token-0123456789abcdef";

    [Fact]
    public void Valid_evidence_envelope_is_accepted_with_indexed_metadata()
    {
        var sourceEventId = Guid.CreateVersion7();
        var evidence = EvidenceSet.Create(
            sourceEventId,
            "captured_output",
            "exact output"u8.ToArray());

        var result = Assert.Single(OtlpRequestValidator
            .ValidateJsonRequest(ExporterJsonBytes(evidence.Records[0]))
            .Results);

        Assert.True(result.IsValid, result.FailureCode);
        var metadata = Assert.IsType<EvidenceRecordMetadata>(result.Record!.Evidence);
        Assert.Equal(sourceEventId, metadata.SourceEventId);
        Assert.Equal(evidence.ArtifactId, metadata.ArtifactId);
        Assert.Equal("captured_output", metadata.EvidenceKind);
        Assert.Equal("forensic", metadata.RetentionClass);
        Assert.Equal("complete", metadata.CaptureState);
        Assert.Equal(12, metadata.ArtifactByteCount);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("digest")]
    [InlineData("event_hash")]
    public void Payload_digest_and_event_hash_tampering_is_rejected(string mutation)
    {
        var evidence = EvidenceSet.Create(
            Guid.CreateVersion7(),
            "caller_response",
            "exact response"u8.ToArray());
        var body = mutation switch
        {
            "payload" => RewriteAndRehash(evidence.Records[0], root =>
                root["payload_base64"] = Convert.ToBase64String("changed"u8.ToArray())),
            "digest" => RewriteAndRehash(evidence.Records[0], root =>
                root["digest"] = new string('0', 64)),
            _ => Rewrite(evidence.Records[0], root =>
                root["event_hash"] = new string('0', 64)),
        };

        var result = Assert.Single(OtlpRequestValidator
            .ValidateJsonRequest(ExporterJsonBytes(body))
            .Results);

        Assert.False(result.IsValid);
        Assert.Equal(
            mutation == "event_hash" ? "event_hash" : "payload",
            result.FailureCode);
    }

    [Fact]
    public void V5_manifest_with_overlapping_chunks_is_rejected_before_storage()
    {
        var sourceEventId = Guid.CreateVersion7();
        var evidence = EvidenceSet.Create(
            sourceEventId,
            "captured_output",
            "eightbit"u8.ToArray(),
            chunkBytes: 4);
        var manifest = JsonNode.Parse(evidence.ManifestJson)!.AsArray();
        manifest[1]!.AsObject()["chunk_offset"] = 0;
        var core = OtlpTestRequest.CreateRecord(
            "ptk.audit/5",
            eventId: sourceEventId.ToString("D"),
            evidenceManifestJson: manifest.ToJsonString());

        var result = Assert.Single(OtlpRequestValidator
            .ValidateJsonRequest(ExporterJsonBytes(core.Body))
            .Results);

        Assert.False(result.IsValid);
        Assert.Equal("evidence_manifest", result.FailureCode);
    }

    [Fact]
    public void Evidence_chunk_bounds_cannot_overflow()
    {
        var evidence = EvidenceSet.Create(
            Guid.CreateVersion7(),
            "caller_response",
            "exact response"u8.ToArray());
        var body = RewriteAndRehash(evidence.Records[0], root =>
        {
            root["artifact_byte_count"] = long.MaxValue;
            root["chunk_offset"] = long.MaxValue;
        });

        var result = Assert.Single(OtlpRequestValidator
            .ValidateJsonRequest(ExporterJsonBytes(body))
            .Results);

        Assert.False(result.IsValid);
        Assert.Equal("chunk", result.FailureCode);
    }

    [Fact]
    public async Task Either_arrival_order_replay_and_restart_preserve_delivery_state()
    {
        using var authority = new TestCertificateAuthority();
        using var rootCertificate = authority.Root;
        using var serverCertificate = authority.IssueServer();
        var firstSource = Guid.CreateVersion7();
        var firstEvidence = EvidenceSet.Create(
            firstSource, "submitted_command", "before core"u8.ToArray());
        var firstCore = OtlpTestRequest.CreateRecord(
            "ptk.audit/5",
            eventId: firstSource.ToString("D"),
            evidenceManifestJson: firstEvidence.ManifestJson);

        var secondSource = Guid.CreateVersion7();
        var secondEvidence = EvidenceSet.Create(
            secondSource, "caller_response", "after core"u8.ToArray());
        var secondCore = OtlpTestRequest.CreateRecord(
            "ptk.audit/5",
            eventId: secondSource.ToString("D"),
            sequence: 2,
            previousEventHash: firstCore.EventHash,
            evidenceManifestJson: secondEvidence.ManifestJson);

        string dataRoot;
        string witnessRoot;
        await using (var host = await SiemReceiverTestHost.StartAsync(
                         serverCertificate,
                         [rootCertificate],
                         ingestToken: IngestToken,
                         preserveRootOnDispose: true,
                         preserveWitnessOnDispose: true))
        {
            dataRoot = host.Root;
            witnessRoot = host.WitnessRoot;
            using var client = host.CreateClient();

            await AssertAcceptedAsync(host, client, firstEvidence.Records);
            await AssertAcceptedAsync(host, client, [firstCore.Body]);
            await AssertDeliveryStateAsync(host, client, firstSource, "complete", 1, 1);

            await AssertAcceptedAsync(host, client, [secondCore.Body]);
            await AssertDeliveryStateAsync(host, client, secondSource, "incomplete", 1, 0);
            await AssertAcceptedAsync(host, client, secondEvidence.Records);
            await AssertAcceptedAsync(host, client, secondEvidence.Records);
            await AssertDeliveryStateAsync(host, client, secondSource, "complete", 1, 1);

            Assert.Equal(0L, DatabaseInt64(
                host.DatabasePath, "SELECT COUNT(*) FROM quarantine;"));
        }

        try
        {
            await using var restarted = await SiemReceiverTestHost.StartAsync(
                serverCertificate,
                [rootCertificate],
                ingestToken: IngestToken,
                existingRoot: dataRoot,
                existingWitnessRoot: witnessRoot);
            using var client = restarted.CreateClient();
            await AssertDeliveryStateAsync(
                restarted, client, firstSource, "complete", 1, 1);
            await AssertDeliveryStateAsync(
                restarted, client, secondSource, "complete", 1, 1);
        }
        finally
        {
            DeleteProtectedRoot(dataRoot);
            DeleteProtectedRoot(witnessRoot);
        }
    }

    [Fact]
    public async Task Authorized_operator_reassembles_exact_multichunk_evidence_only()
    {
        using var authority = new TestCertificateAuthority();
        using var rootCertificate = authority.Root;
        using var serverCertificate = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            serverCertificate,
            [rootCertificate],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        var text = string.Concat(Enumerable.Repeat("command-output-error\n", 12_000));
        var sourceEventId = Guid.CreateVersion7();
        var evidence = EvidenceSet.Create(
            sourceEventId,
            "captured_output",
            Encoding.UTF8.GetBytes(text),
            chunkBytes: 64 * 1024);
        var core = OtlpTestRequest.CreateRecord(
            "ptk.audit/5",
            eventId: sourceEventId.ToString("D"),
            evidenceManifestJson: evidence.ManifestJson);
        await AssertAcceptedAsync(host, client, [core.Body, .. evidence.Records]);

        var evidenceUri = new Uri(host.OperatorEndpoint, $"/api/evidence/{evidence.ArtifactId:D}");
        using (var missing = await client.GetAsync(evidenceUri))
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using (var ingestCredential = new HttpRequestMessage(HttpMethod.Get, evidenceUri))
        {
            ingestCredential.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", IngestToken);
            using var response = await client.SendAsync(ingestCredential);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var authorized = new HttpRequestMessage(HttpMethod.Get, evidenceUri);
        authorized.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", SiemReceiverTestHost.OperatorToken);
        using var exact = await client.SendAsync(authorized);
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        using var payload = JsonDocument.Parse(await exact.Content.ReadAsStringAsync());
        var result = payload.RootElement.GetProperty("evidence");
        Assert.Equal(text, result.GetProperty("text").GetString());
        Assert.Equal(
            Encoding.UTF8.GetBytes(text),
            result.GetProperty("payload_base64").GetBytesFromBase64());
        Assert.Equal(evidence.Records.Count, result.GetProperty("chunk_count").GetInt64());
        Assert.Equal(
            evidence.Records.Count,
            result.GetProperty("event_ids").GetArrayLength());
        Assert.Equal(
            evidence.Records.Count + 1L,
            DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM custody;"));

        using var listed = await OperatorGetAsync(
            host,
            client,
            $"/api/events?artifact={evidence.ArtifactId:D}");
        using var listedPayload = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Equal(
            evidence.Records.Count,
            listedPayload.RootElement.GetProperty("events").GetArrayLength());
        using var byCall = await OperatorGetAsync(
            host,
            client,
            $"/api/events?call={evidence.CallId:D}");
        using var byCallPayload = JsonDocument.Parse(await byCall.Content.ReadAsStringAsync());
        Assert.Equal(
            evidence.Records.Count,
            byCallPayload.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task Retention_purge_is_custody_recorded_and_marks_the_retained_core_incomplete()
    {
        var root = SiemTestFileSystem.CreateProtectedRoot("ptk-siem-evidence-retention");
        var databasePath = Path.Combine(root, "siem.db");
        try
        {
            using var store = SqliteIngestStore.Open(databasePath);
            var sourceEventId = Guid.CreateVersion7();
            var evidence = EvidenceSet.Create(
                sourceEventId,
                "captured_output",
                Encoding.UTF8.GetBytes(new string('x', 220_000)),
                chunkBytes: 64 * 1024);
            var core = OtlpTestRequest.CreateRecord(
                "ptk.audit/5",
                eventId: sourceEventId.ToString("D"),
                evidenceManifestJson: evidence.ManifestJson);
            var records = OtlpRequestValidator.ValidateJsonRequest(
                    ExporterJsonBytes([core.Body, .. evidence.Records]))
                .Results
                .Select(result => result.Record!)
                .ToArray();
            var receipt = new IngestReceiptContext(
                new DateTimeOffset(2026, 7, 15, 16, 30, 45, TimeSpan.Zero),
                new string('a', 64),
                "127.0.0.1:4318");
            foreach (var record in records)
            {
                Assert.Equal(
                    IngestCommitResultKind.Accepted,
                    (await store.CommitAsync(record, receipt, CancellationToken.None)).Kind);
            }

            var outcome = await store.EnforceRetentionAsync(
                maximumAgeDays: 30,
                maximumTotalBytes: null,
                utcNow: receipt.ReceivedUtc.AddYears(1),
                CancellationToken.None);

            Assert.True(outcome.EventsRemoved > 0);
            Assert.Equal("incomplete", DatabaseString(
                databasePath,
                $"SELECT state FROM evidence_delivery_status WHERE source_event_id = '{sourceEventId:D}';"));
            Assert.Equal(1L, DatabaseInt64(
                databasePath,
                $"SELECT received_chunks FROM evidence_delivery_status WHERE source_event_id = '{sourceEventId:D}';"));
            Assert.Equal(
                evidence.Records.Count,
                DatabaseInt64(
                    databasePath,
                    $"SELECT expected_chunks FROM evidence_delivery_status WHERE source_event_id = '{sourceEventId:D}';"));
            Assert.True(
                DatabaseInt64(databasePath, "SELECT COUNT(*) FROM custody;") >
                DatabaseInt64(databasePath, "SELECT COUNT(*) FROM events;"));
            Assert.True((await store.VerifyCustodyAsync(CancellationToken.None)).Healthy);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Online_backup_restores_exact_evidence_and_healthy_custody()
    {
        var root = SiemTestFileSystem.CreateProtectedRoot("ptk-siem-evidence-backup");
        var databasePath = Path.Combine(root, "siem.db");
        var backupPath = Path.Combine(root, "siem-backup.db");
        var expected = Encoding.UTF8.GetBytes(new string('z', 180_000));
        try
        {
            var evidence = EvidenceSet.Create(
                Guid.CreateVersion7(),
                "captured_output",
                expected,
                chunkBytes: 64 * 1024);
            using (var store = SqliteIngestStore.Open(databasePath))
            {
                var records = OtlpRequestValidator.ValidateJsonRequest(
                        ExporterJsonBytes(evidence.Records.ToArray()))
                    .Results
                    .Select(result => result.Record!)
                    .ToArray();
                var receipt = new IngestReceiptContext(
                    new DateTimeOffset(2026, 7, 15, 16, 30, 45, TimeSpan.Zero),
                    new string('b', 64),
                    "127.0.0.1:4318");
                foreach (var record in records)
                {
                    Assert.Equal(
                        IngestCommitResultKind.Accepted,
                        (await store.CommitAsync(record, receipt, CancellationToken.None)).Kind);
                }

                using var source = new SqliteConnection($"Data Source={databasePath}");
                using var destination = new SqliteConnection($"Data Source={backupPath}");
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }
            _ = SiemProtectedPath.ProtectCreatedFile(backupPath);

            using var restored = SqliteIngestStore.Open(backupPath);
            Assert.True((await restored.VerifyCustodyAsync(CancellationToken.None)).Healthy);
            var chunks = new List<byte[]>();
            using (var connection = new SqliteConnection($"Data Source={backupPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT exact_json_body FROM events
                    WHERE artifact_id = $artifact
                    ORDER BY chunk_index;
                    """;
                command.Parameters.AddWithValue("$artifact", evidence.ArtifactId.ToString("D"));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    using var body = JsonDocument.Parse((byte[])reader.GetValue(0));
                    chunks.Add(body.RootElement.GetProperty("payload_base64").GetBytesFromBase64());
                }
            }
            Assert.Equal(expected, chunks.SelectMany(bytes => bytes).ToArray());
            Assert.Equal(evidence.Records.Count, chunks.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Disk_full_before_evidence_commit_returns_retryable_without_acknowledging()
    {
        using var authority = new TestCertificateAuthority();
        using var rootCertificate = authority.Root;
        using var serverCertificate = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            serverCertificate,
            [rootCertificate],
            ingestToken: IngestToken,
            storageFaultInjector: new DiskFullFault());
        using var client = host.CreateClient();
        var evidence = EvidenceSet.Create(
            Guid.CreateVersion7(),
            "caller_response",
            "not committed"u8.ToArray());

        using var response = await client.SendAsync(
            JsonRequest(host.Endpoint, evidence.Records));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM custody;"));
    }

    private static async Task AssertAcceptedAsync(
        SiemReceiverTestHost host,
        HttpClient client,
        IReadOnlyList<string> records)
    {
        using var response = await client.SendAsync(
            JsonRequest(host.Endpoint, records));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task AssertDeliveryStateAsync(
        SiemReceiverTestHost host,
        HttpClient client,
        Guid eventId,
        string state,
        long expected,
        long received)
    {
        using var response = await OperatorGetAsync(
            host, client, $"/api/events/{eventId:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var delivery = payload.RootElement.GetProperty("evidence_delivery");
        Assert.Equal(state, delivery.GetProperty("state").GetString());
        Assert.Equal(expected, delivery.GetProperty("expected_chunks").GetInt64());
        Assert.Equal(received, delivery.GetProperty("received_chunks").GetInt64());
        Assert.Equal(
            expected - received,
            delivery.GetProperty("missing_event_ids").GetArrayLength());
    }

    private static HttpRequestMessage JsonRequest(
        Uri endpoint,
        IReadOnlyList<string> records)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(ExporterJsonBytes(records.ToArray())),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", IngestToken);
        return request;
    }

    private static async Task<HttpResponseMessage> OperatorGetAsync(
        SiemReceiverTestHost host,
        HttpClient client,
        string path)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri(host.OperatorEndpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", SiemReceiverTestHost.OperatorToken);
        return await client.SendAsync(request);
    }

    private static byte[] ExporterJsonBytes(params string[] records)
    {
        var builder = new StringBuilder(
            "{\"resourceLogs\":[{\"resource\":{\"attributes\":[]}," +
            "\"scopeLogs\":[{\"scope\":{\"name\":\"ptk.audit\"},\"logRecords\":[");
        for (var index = 0; index < records.Length; index++)
        {
            if (index > 0) builder.Append(',');
            using var document = JsonDocument.Parse(records[index]);
            var root = document.RootElement;
            builder.Append("{\"body\":{\"stringValue\":")
                .Append(JsonSerializer.Serialize(records[index]))
                .Append("},\"attributes\":[")
                .Append("{\"key\":\"ptk.event_type\",\"value\":{\"stringValue\":")
                .Append(JsonSerializer.Serialize(root.GetProperty("event_type").GetString()))
                .Append("}},{\"key\":\"ptk.event_id\",\"value\":{\"stringValue\":")
                .Append(JsonSerializer.Serialize(root.GetProperty("event_id").GetString()))
                .Append("}}]}");
        }
        builder.Append("]}]}]}");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string Rewrite(string body, Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(body)!.AsObject();
        mutation(root);
        return root.ToJsonString();
    }

    private static string RewriteAndRehash(string body, Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(body)!.AsObject();
        root.Remove("event_hash");
        mutation(root);
        var preHash = root.ToJsonString();
        root["event_hash"] = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(preHash))).ToLowerInvariant();
        return root.ToJsonString();
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

    private static void DeleteProtectedRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    internal sealed record EvidenceSet(
        Guid ArtifactId,
        Guid CallId,
        IReadOnlyList<string> Records,
        string ManifestJson)
    {
        internal static EvidenceSet Create(
            Guid sourceEventId,
            string evidenceKind,
            byte[] payload,
            int chunkBytes = 96 * 1024,
            Guid? activityCallId = null)
        {
            var artifactId = Guid.NewGuid();
            var callId = activityCallId ?? Guid.CreateVersion7();
            var artifactDigest = Digest(payload);
            var count = Math.Max(1, (payload.Length + chunkBytes - 1) / chunkBytes);
            var records = new List<string>(count);
            var manifest = new JsonArray();
            string? previousHash = null;
            for (var index = 0; index < count; index++)
            {
                var offset = index * chunkBytes;
                var length = Math.Min(chunkBytes, payload.Length - offset);
                var chunk = payload.AsSpan(offset, Math.Max(0, length)).ToArray();
                var eventId = Guid.CreateVersion7();
                var evidenceId = Guid.NewGuid();
                var digest = Digest(chunk);
                var item = new EnvelopeItem(
                    eventId,
                    evidenceId,
                    evidenceKind,
                    digest,
                    chunk.LongLength,
                    artifactId,
                    artifactDigest,
                    payload.LongLength,
                    index,
                    count,
                    offset);
                var preHash = Serialize(
                    item, chunk, sourceEventId, callId, previousHash, null);
                var eventHash = Digest(Encoding.UTF8.GetBytes(preHash));
                records.Add(Serialize(
                    item, chunk, sourceEventId, callId, previousHash, eventHash));
                previousHash = eventHash;
                manifest.Add(new JsonObject
                {
                    ["evidence_id"] = evidenceId.ToString("D"),
                    ["envelope_event_id"] = eventId.ToString("D"),
                    ["evidence_kind"] = evidenceKind,
                    ["digest"] = digest,
                    ["byte_count"] = chunk.LongLength,
                    ["encoding"] = "utf-8",
                    ["artifact_id"] = artifactId.ToString("D"),
                    ["artifact_digest"] = artifactDigest,
                    ["artifact_byte_count"] = payload.LongLength,
                    ["chunk_index"] = index,
                    ["chunk_count"] = count,
                    ["chunk_offset"] = offset,
                    ["retention_class"] = "forensic",
                    ["capture_state"] = "complete",
                });
                CryptographicOperations.ZeroMemory(chunk);
            }
            return new EvidenceSet(artifactId, callId, records, manifest.ToJsonString());
        }

        private static string Serialize(
            EnvelopeItem item,
            byte[] payload,
            Guid sourceEventId,
            Guid callId,
            string? previousHash,
            string? eventHash)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("schema_version", "ptk.evidence/1");
                writer.WriteString("event_id", item.EventId);
                writer.WriteString("event_type", $"evidence.{item.EvidenceKind}");
                writer.WriteString("occurred_utc", "2026-07-15T12:34:57.1234567Z");
                writer.WriteString("observed_utc", "2026-07-15T12:34:57.1234567Z");
                writer.WriteStartObject("producer");
                writer.WriteString("host_id", OtlpTestRequest.DefaultHostId);
                writer.WriteString("supervisor_boot_id", OtlpTestRequest.DefaultSupervisorBootId);
                writer.WriteString("version", "1.2.3");
                writer.WriteEndObject();
                writer.WriteStartObject("stream");
                writer.WriteString("stream_id", item.ArtifactId);
                writer.WriteNumber("sequence", item.ChunkIndex + 1L);
                if (previousHash is null) writer.WriteNull("previous_event_hash");
                else writer.WriteString("previous_event_hash", previousHash);
                writer.WriteEndObject();
                writer.WriteString("source_event_id", sourceEventId);
                writer.WriteString("call_id", callId);
                writer.WriteString("evidence_id", item.EvidenceId);
                writer.WriteString("evidence_kind", item.EvidenceKind);
                writer.WriteString("digest", item.Digest);
                writer.WriteNumber("byte_count", item.ByteCount);
                writer.WriteString("encoding", "utf-8");
                writer.WriteString("artifact_id", item.ArtifactId);
                writer.WriteString("artifact_digest", item.ArtifactDigest);
                writer.WriteNumber("artifact_byte_count", item.ArtifactByteCount);
                writer.WriteNumber("chunk_index", item.ChunkIndex);
                writer.WriteNumber("chunk_count", item.ChunkCount);
                writer.WriteNumber("chunk_offset", item.ChunkOffset);
                writer.WriteString("retention_class", "forensic");
                writer.WriteString("capture_state", "complete");
                writer.WriteBase64String("payload_base64", payload);
                if (eventHash is not null) writer.WriteString("event_hash", eventHash);
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static string Digest(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record EnvelopeItem(
        Guid EventId,
        Guid EvidenceId,
        string EvidenceKind,
        string Digest,
        long ByteCount,
        Guid ArtifactId,
        string ArtifactDigest,
        long ArtifactByteCount,
        int ChunkIndex,
        int ChunkCount,
        long ChunkOffset);

    private sealed class DiskFullFault : ISqliteIngestFaultInjector
    {
        public void BeforeCommit(SqliteIngestWriteKind writeKind)
        {
            if (writeKind == SqliteIngestWriteKind.Event)
                throw new SqliteException("database or disk is full", 13, 13);
        }
    }
}
