using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Tests;

public sealed class AuditFullEvidenceExportTests
{
    [Fact]
    public async Task Evidence_refusal_holds_cursor_until_the_whole_logical_unit_is_acknowledged()
    {
        using var scenario = FullEvidenceScenario.Create("evidence-refusal", "exact response");
        using var receiver = new FakeDestination
        {
            RefusePredicate = body => body.Contains(
                "ptk.evidence/1", StringComparison.Ordinal),
        };
        var cursorStore = new AuditExportCursorStore(scenario.Options.RootDirectory);
        var health = new AuditExportHealth();

        await using (var service = NewService(scenario, receiver, cursorStore, health))
            _ = await service.DrainOnceAsync(CancellationToken.None);

        AssertCursorBeforeEnd(cursorStore, scenario);
        Assert.Equal("export.evidence_refused", health.Snapshot().LastFailureDetail);

        receiver.RefusePredicate = null;
        await using (var service = NewService(scenario, receiver, cursorStore, health))
            _ = await service.DrainOnceAsync(CancellationToken.None);

        AssertCursorAtEnd(cursorStore, scenario);
        Assert.Contains(
            receiver.Requests,
            body => body.Contains("ptk.evidence/1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V5_core_refusal_is_not_stepped_over_as_an_ordinary_poison_record()
    {
        using var scenario = FullEvidenceScenario.Create("core-refusal", "exact response");
        using var receiver = new FakeDestination
        {
            RefusePredicate = body =>
                body.Contains("ptk.audit/5", StringComparison.Ordinal),
        };
        var cursorStore = new AuditExportCursorStore(scenario.Options.RootDirectory);
        var health = new AuditExportHealth();

        await using var service = NewService(scenario, receiver, cursorStore, health);
        _ = await service.DrainOnceAsync(CancellationToken.None);

        AssertCursorBeforeEnd(cursorStore, scenario);
        Assert.Equal("export.evidence_refused", health.Snapshot().LastFailureDetail);
        Assert.Equal(0, health.Snapshot().RefusedRecords);
    }

    [Fact]
    public async Task V6_destination_filter_counts_and_delivers_v2_evidence_obligations()
    {
        using var scenario = FullEvidenceScenario.Create(
            "destination-evidence",
            "exact destination response");
        var destinationId = Guid.NewGuid();
        var terminalEventId = scenario.TerminalRecord
            .GetProperty("event_id")
            .GetString();
        var rewritten = File.ReadAllLines(scenario.SegmentPath)
            .Select(line =>
            {
                var node = JsonNode.Parse(line)!.AsObject();
                if (string.Equals(
                    node["event_id"]?.GetValue<string>(),
                    terminalEventId,
                    StringComparison.Ordinal))
                {
                    node["schema_version"] = AuditEventSerializer.DestinationObligationSchemaVersion;
                    node["required_destination_ids"] = new JsonArray(
                        JsonValue.Create(destinationId.ToString("D")));
                }

                return node.ToJsonString();
            });
        File.WriteAllText(
            scenario.SegmentPath,
            string.Concat(rewritten.Select(line => line + "\n")),
            new UTF8Encoding(false));

        using var receiver = new FakeDestination();
        var definition = new AuditDestinationDefinition(
            destinationId,
            AuditDestinationKind.OtlpHttp,
            "primary",
            receiver.BaseUri,
            "otlp_http",
            "destination-reference",
            string.Empty,
            1,
            DateTimeOffset.UtcNow,
            Enabled: true);
        var cursor = new AuditExportCursorStore(
            scenario.Options.RootDirectory,
            AuditExportCursorStore.DestinationFileName(destinationId));
        var health = new AuditExportHealth();
        await using var service = new AuditExportService(
            scenario.Options,
            new HttpAuditDestination(definition.ToExportSettings()),
            cursor,
            health,
            evidence: scenario.Evidence,
            gapStore: new AuditExportGapStore(
                scenario.Options.RootDirectory,
                AuditExportGapStore.DestinationFileName(destinationId)),
            lease: new AuditExportLease(
                AuditExportLease.DestinationFileName(destinationId)),
            recordFilter: record => AuditExportCoordinator.IsRequiredBy(record, definition),
            holdAllPermanentRefusals: true);

        Assert.True(service.HasPendingObligations());
        Assert.Equal(1, health.Snapshot().PendingEventRecords);
        Assert.True(health.Snapshot().PendingEvidenceRecords > 0);
        Assert.True(health.Snapshot().PendingEvidenceBytes > 0);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        AssertCursorAtEnd(cursor, scenario);

        var delivered = receiver.Requests
            .SelectMany(ReadDeliveredRecords)
            .ToArray();
        Assert.Single(delivered, record =>
            record.GetProperty("schema_version").GetString() ==
                AuditEventSerializer.DestinationObligationSchemaVersion);
        var evidence = delivered.Where(record =>
            record.GetProperty("schema_version").GetString() ==
                AuditEvidenceEnvelope.DestinationSchemaVersion).ToArray();
        Assert.NotEmpty(evidence);
        Assert.All(evidence, record => Assert.Equal(
            new[] { destinationId.ToString("D") },
            record.GetProperty("required_destination_ids")
                .EnumerateArray()
                .Select(item => item.GetString())));
    }

    [Fact]
    public async Task Lost_response_replays_and_large_chunks_arrive_intact_before_cursor_advances()
    {
        var response = string.Concat(Enumerable.Repeat("result-error-warning\n", 14_000));
        using var scenario = FullEvidenceScenario.Create("lost-response", response);
        using var receiver = new FakeDestination { AbortNextResponse = true };
        var cursorStore = new AuditExportCursorStore(scenario.Options.RootDirectory);
        var health = new AuditExportHealth();

        await using (var service = NewService(scenario, receiver, cursorStore, health))
            _ = await service.DrainOnceAsync(CancellationToken.None);
        AssertCursorBeforeEnd(cursorStore, scenario);
        Assert.NotEmpty(receiver.Requests);

        await using (var service = NewService(scenario, receiver, cursorStore, health))
            _ = await service.DrainOnceAsync(CancellationToken.None);
        AssertCursorAtEnd(cursorStore, scenario);

        var evidence = receiver.Requests
            .SelectMany(ReadDeliveredRecords)
            .Where(record => record.GetProperty("schema_version").GetString() == "ptk.evidence/1")
            .Where(record => record.GetProperty("evidence_kind").GetString() == "caller_response")
            .GroupBy(record => record.GetProperty("artifact_id").GetString())
            .OrderByDescending(group => group.Count())
            .First()
            .GroupBy(record => record.GetProperty("event_id").GetString())
            .Select(group => group.First())
            .OrderBy(record => record.GetProperty("chunk_index").GetInt32())
            .ToArray();
        Assert.True(evidence.Length > 1);
        using var combined = new MemoryStream();
        foreach (var chunk in evidence)
            combined.Write(chunk.GetProperty("payload_base64").GetBytesFromBase64());
        Assert.Equal(response, Encoding.UTF8.GetString(combined.ToArray()));
    }

    [Fact]
    public async Task Missing_local_evidence_holds_cursor_and_reports_unavailable()
    {
        using var scenario = FullEvidenceScenario.Create("missing-evidence", "exact response");
        var evidenceId = scenario.TerminalRecord
            .GetProperty("evidence_manifest")[0]
            .GetProperty("evidence_id")
            .GetString()!;
        var artifactPath = Directory.EnumerateFiles(scenario.Options.EvidenceDirectory)
            .Single(path => Path.GetFileName(path)
                .StartsWith(evidenceId, StringComparison.Ordinal));
        File.Delete(artifactPath);

        using var receiver = new FakeDestination();
        var cursorStore = new AuditExportCursorStore(scenario.Options.RootDirectory);
        var health = new AuditExportHealth();
        await using var service = NewService(scenario, receiver, cursorStore, health);
        _ = await service.DrainOnceAsync(CancellationToken.None);

        AssertCursorBeforeEnd(cursorStore, scenario);
        Assert.Equal("export.evidence_unavailable", health.Snapshot().LastFailureDetail);
        Assert.Empty(receiver.Requests);
    }

    private static AuditExportService NewService(
        FullEvidenceScenario scenario,
        FakeDestination receiver,
        AuditExportCursorStore cursorStore,
        AuditExportHealth health)
    {
        var settings = new AuditExportSettings(
            AuditDestinationKind.OtlpHttp,
            receiver.BaseUri,
            null);
        return new AuditExportService(
            scenario.Options,
            new HttpAuditDestination(settings),
            cursorStore,
            health,
            evidence: scenario.Evidence);
    }

    private static void AssertCursorAtEnd(
        AuditExportCursorStore cursorStore,
        FullEvidenceScenario scenario)
    {
        var position = cursorStore.Read().For(scenario.BootId);
        Assert.NotNull(position);
        Assert.Equal(
            new FileInfo(scenario.SegmentPath).Length,
            position!.ByteOffset);
    }

    private static void AssertCursorBeforeEnd(
        AuditExportCursorStore cursorStore,
        FullEvidenceScenario scenario)
    {
        var position = cursorStore.Read().For(scenario.BootId);
        if (position is not null)
        {
            Assert.True(
                position.ByteOffset < new FileInfo(scenario.SegmentPath).Length,
                $"Cursor advanced past the unacknowledged v5 unit at {position.ByteOffset}.");
        }
    }

    private static IEnumerable<JsonElement> ReadDeliveredRecords(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        foreach (var record in document.RootElement
                     .GetProperty("resourceLogs")[0]
                     .GetProperty("scopeLogs")[0]
                     .GetProperty("logRecords")
                     .EnumerateArray())
        {
            using var body = JsonDocument.Parse(
                record.GetProperty("body").GetProperty("stringValue").GetString()!);
            yield return body.RootElement.Clone();
        }
    }

    private sealed class FullEvidenceScenario : IDisposable
    {
        private FullEvidenceScenario(
            string root,
            AuditOptions options,
            ScriptEvidenceStoreProvider evidence,
            Guid bootId,
            string segmentPath,
            JsonElement terminalRecord)
        {
            Root = root;
            Options = options;
            Evidence = evidence;
            BootId = bootId;
            SegmentPath = segmentPath;
            TerminalRecord = terminalRecord;
        }

        internal string Root { get; }
        internal AuditOptions Options { get; }
        internal ScriptEvidenceStoreProvider Evidence { get; }
        internal Guid BootId { get; }
        internal string SegmentPath { get; }
        internal JsonElement TerminalRecord { get; }

        internal static FullEvidenceScenario Create(string label, string response)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ptk",
                $"test-full-export-{label}-{Guid.NewGuid():N}");
            SecureAuditStorage.PrepareRoot(root);
            var options = AuditOptions.Create(
                root,
                maxEvidenceBytes: AuditOptions.DefaultMaxEvidenceBytes,
                evidenceAggregateBytes: 32 * 1024 * 1024);
            var bootId = Guid.NewGuid();
            var evidence = new ScriptEvidenceStoreProvider(new ScriptEvidenceStore(options));
            using (var sink = new FileAuditJournalSink(options, bootId))
            using (var journal = new AuditJournal(
                       options,
                       new AuditHealth(options),
                       sink,
                       "evidence-export-test",
                       binaryDigest: null,
                       hostId: Guid.NewGuid(),
                       supervisorBootId: bootId))
            {
                var call = new CallToolRequestParams
                {
                    Name = "ptk_invoke",
                    Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["script"] = JsonSerializer.SerializeToElement("'exact command'"),
                    },
                };
                Assert.True(AuditCallMetadataCapture.TryCapture(
                    call,
                    new AuditClientContext("test", "1", "session"),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(30),
                    DateTimeOffset.UtcNow,
                    out var metadata,
                    out var exactScript,
                    out _));
                var outputStore = new OutputStore(new OutputStoreOptions(
                    Path.Combine(root, "output"),
                    TimeSpan.FromMinutes(15),
                    TimeSpan.FromHours(1),
                    8 * 1024 * 1024,
                    16 * 1024 * 1024,
                    32 * 1024 * 1024));
                var context = new AuditCallContext(journal, evidence, outputStore);
                Assert.True(context.TryBegin(metadata!, exactScript, out _));
                context.RecordInvokeResult(
                    new InvokeResult(
                        Success: false,
                        Output: string.Empty,
                        Errors: [],
                        Warnings: [],
                        TimedOut: false,
                        Disposition: InvokeDisposition.NotStarted,
                        UserExecutionStarted: false),
                    response);
            }

            var segmentPath = Assert.Single(Directory.EnumerateFiles(
                options.SpoolDirectory, "ptk-audit-*.jsonl"));
            var terminal = File.ReadLines(segmentPath)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .Single(rootElement =>
                    rootElement.GetProperty("schema_version").GetString() == "ptk.audit/5");
            return new FullEvidenceScenario(
                root, options, evidence, bootId, segmentPath, terminal);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeDestination : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _pump;

        internal FakeDestination()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _pump = Task.Run(AcceptAsync);
        }

        internal Uri BaseUri { get; }
        internal Func<string, bool>? RefusePredicate { get; set; }
        internal bool AbortNextResponse { get; set; }
        internal List<string> Requests { get; } = [];

        private async Task AcceptAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                }
                catch (Exception)
                {
                    return;
                }
                using (client)
                {
                    var stream = client.GetStream();
                    var body = await ReadBodyAsync(stream, _stopping.Token);
                    lock (Requests) Requests.Add(body);
                    if (AbortNextResponse)
                    {
                        AbortNextResponse = false;
                        client.LingerState = new LingerOption(true, 0);
                        continue;
                    }
                    var status = RefusePredicate?.Invoke(body) == true
                        ? "400 Bad Request"
                        : "200 OK";
                    var response = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, _stopping.Token);
                }
            }
        }

        private static async Task<string> ReadBodyAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var received = new List<byte>();
            var buffer = new byte[16 * 1024];
            var headerEnd = -1;
            var contentLength = -1;
            while (contentLength < 0 || received.Count < headerEnd + 4 + contentLength)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                received.AddRange(buffer.AsSpan(0, count).ToArray());
                if (headerEnd >= 0) continue;
                headerEnd = FindHeaderEnd(received);
                if (headerEnd < 0) continue;
                var headers = Encoding.ASCII.GetString(received.ToArray(), 0, headerEnd);
                var contentLengthLine = headers.Split("\r\n", StringSplitOptions.None)
                    .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                contentLength = int.Parse(
                    contentLengthLine["Content-Length:".Length..].Trim(),
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            return contentLength <= 0
                ? string.Empty
                : Encoding.UTF8.GetString(
                    received.ToArray(), headerEnd + 4, contentLength);
        }

        private static int FindHeaderEnd(IReadOnlyList<byte> bytes)
        {
            for (var index = 0; index <= bytes.Count - 4; index++)
            {
                if (bytes[index] == '\r' && bytes[index + 1] == '\n' &&
                    bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
                {
                    return index;
                }
            }
            return -1;
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            try { _pump.GetAwaiter().GetResult(); }
            catch (Exception) { }
            _stopping.Dispose();
        }
    }
}
