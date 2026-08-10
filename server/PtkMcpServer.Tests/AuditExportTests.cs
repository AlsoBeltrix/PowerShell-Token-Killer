using System.Net;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Tests;

public sealed class AuditExportTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Preserve the assertion failure that prevented cleanup. */ }
        }
    }

    [Fact]
    public void Settings_require_tls_for_every_nonloopback_destination()
    {
        var root = NewRoot("export-settings");
        // Plaintext to a remote SIEM is refused outright: a credential must
        // never cross the network in the clear.
        WriteSettings(root, "otlp_http", "http://siem.example.com/v1/logs", "token");
        var remote = AuditExportSettings.Load(root, out var remoteFailure);
        Assert.False(remote.IsConfigured);
        Assert.Equal("export.endpoint_invalid", remoteFailure);

        // Loopback plaintext IS allowed: that is the zero-config local
        // fallback receiver, never a network hop.
        WriteSettings(root, "otlp_http", "http://127.0.0.1:4318/v1/logs", "token");
        var loopback = AuditExportSettings.Load(root, out var loopbackFailure);
        Assert.True(loopback.IsConfigured);
        Assert.Null(loopbackFailure);

        WriteSettings(root, "splunk_hec", "https://splunk.example.com:8088", "hec-token");
        var splunk = AuditExportSettings.Load(root, out _);
        Assert.Equal(AuditDestinationKind.SplunkHec, splunk.Kind);
        Assert.True(splunk.IsConfigured);
    }

    [Fact]
    public void Settings_never_describe_a_destination_with_its_credential()
    {
        var root = NewRoot("export-describe");
        WriteSettings(root, "splunk_hec", "https://splunk.example.com:8088", "SUPER-SECRET-TOKEN");
        var settings = AuditExportSettings.Load(root, out _);
        var description = settings.Describe();
        Assert.DoesNotContain("SUPER-SECRET-TOKEN", description, StringComparison.Ordinal);
        Assert.Contains("splunk_hec", description, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_export_configuration_disables_delivery_without_throwing()
    {
        var root = NewRoot("export-broken-config");
        File.WriteAllText(Path.Combine(root, AuditExportSettings.FileName), "{ not json");
        var settings = AuditExportSettings.Load(root, out var failure);
        Assert.False(settings.IsConfigured);
        Assert.Equal("export.configuration_unreadable", failure);
    }

    [Fact]
    public async Task Records_reach_a_splunk_hec_destination_with_its_token()
    {
        using var receiver = new FakeHttpDestination();
        var settings = new AuditExportSettings(
            AuditDestinationKind.SplunkHec,
            receiver.BaseUri,
            "hec-token");
        using var destination = new HttpAuditDestination(settings);

        var result = await destination.DeliverAsync(
            ["""{"event_type":"call.completed","event_id":"e1"}"""],
            CancellationToken.None);

        Assert.Equal(AuditDeliveryDisposition.Delivered, result.Disposition);
        var request = Assert.Single(receiver.Requests);
        Assert.Equal("/services/collector/event", request.Path);
        Assert.Equal("Splunk hec-token", request.Authorization);
        Assert.Contains("\"sourcetype\":\"ptk:audit\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("call.completed", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Records_reach_an_otlp_destination_as_valid_otlp_json_logs()
    {
        using var receiver = new FakeHttpDestination();
        var settings = new AuditExportSettings(
            AuditDestinationKind.OtlpHttp,
            receiver.BaseUri,
            "bearer-token");
        using var destination = new HttpAuditDestination(settings);

        var result = await destination.DeliverAsync(
            [
                """{"event_type":"call.completed","event_id":"e1","observed_utc":"2026-08-10T12:00:00.0000000Z"}""",
                """{"event_type":"server.started","event_id":"e2"}""",
            ],
            CancellationToken.None);

        Assert.Equal(AuditDeliveryDisposition.Delivered, result.Disposition);
        var request = Assert.Single(receiver.Requests);
        Assert.Equal("/v1/logs", request.Path);
        Assert.Equal("Bearer bearer-token", request.Authorization);

        // The payload must be well-formed OTLP/HTTP JSON logs, not a
        // hand-rolled shape a collector would reject.
        using var document = JsonDocument.Parse(request.Body);
        var logRecords = document.RootElement
            .GetProperty("resourceLogs")[0]
            .GetProperty("scopeLogs")[0]
            .GetProperty("logRecords");
        Assert.Equal(2, logRecords.GetArrayLength());
        var first = logRecords[0];
        Assert.Contains(
            "call.completed",
            first.GetProperty("body").GetProperty("stringValue").GetString(),
            StringComparison.Ordinal);
        // 2026-08-10T12:00:00Z in nanoseconds: the record's own observed_utc
        // carries into OTLP, not the delivery time.
        Assert.Equal(
            "1786363200000000000",
            first.GetProperty("timeUnixNano").GetString());
        Assert.Contains(
            first.GetProperty("attributes").EnumerateArray(),
            attribute => attribute.GetProperty("key").GetString() == "ptk.event_type");
    }

    [Fact]
    public async Task A_malformed_record_is_still_delivered_rather_than_dropped()
    {
        using var receiver = new FakeHttpDestination();
        var settings = new AuditExportSettings(
            AuditDestinationKind.OtlpHttp,
            receiver.BaseUri,
            null);
        using var destination = new HttpAuditDestination(settings);

        var result = await destination.DeliverAsync(
            ["this is not json at all"],
            CancellationToken.None);

        Assert.Equal(AuditDeliveryDisposition.Delivered, result.Disposition);
        var request = Assert.Single(receiver.Requests);
        Assert.Contains("this is not json at all", request.Body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "retryable")]
    [InlineData(HttpStatusCode.TooManyRequests, "retryable")]
    [InlineData(HttpStatusCode.Unauthorized, "retryable")]
    [InlineData(HttpStatusCode.BadRequest, "permanent")]
    public async Task Destination_responses_map_to_retry_or_permanent_dispositions(
        HttpStatusCode status,
        string expectedDisposition)
    {
        var expected = expectedDisposition == "retryable"
            ? AuditDeliveryDisposition.Retryable
            : AuditDeliveryDisposition.Permanent;
        using var receiver = new FakeHttpDestination { ResponseStatus = status };
        var settings = new AuditExportSettings(
            AuditDestinationKind.OtlpHttp,
            receiver.BaseUri,
            null);
        using var destination = new HttpAuditDestination(settings);

        var result = await destination.DeliverAsync(
            ["""{"event_type":"call.completed"}"""],
            CancellationToken.None);

        Assert.Equal(expected, result.Disposition);
    }

    [Fact]
    public async Task The_export_service_drains_the_spool_and_resumes_from_its_cursor()
    {
        var root = NewRoot("export-drain");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var segment = WriteSegment(options, index: 0, records:
        [
            """{"event_type":"server.started","event_id":"e1"}""",
            """{"event_type":"call.completed","event_id":"e2"}""",
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        var cursorStore = new AuditExportCursorStore(root);
        await using (var service = NewService(options, receiver, cursorStore, health))
        {
            Assert.Equal(2, await service.DrainOnceAsync(CancellationToken.None));
            // A second pass with nothing new delivers nothing: the cursor is
            // durable, so records are not re-sent on every tick.
            Assert.Equal(0, await service.DrainOnceAsync(CancellationToken.None));
        }

        var cursor = cursorStore.Read();
        Assert.Equal(Path.GetFileName(segment), cursor.SegmentFileName);
        Assert.Equal(new FileInfo(segment).Length, cursor.ByteOffset);

        // A fresh service (restart) resumes after the cursor, then picks up
        // records appended since.
        AppendRecords(segment, ["""{"event_type":"call.completed","event_id":"e3"}"""]);
        var health2 = new AuditExportHealth();
        await using var resumed = NewService(options, receiver, new AuditExportCursorStore(root), health2);
        Assert.Equal(1, await resumed.DrainOnceAsync(CancellationToken.None));

        var delivered = receiver.Requests
            .SelectMany(request => JsonDocument.Parse(request.Body).RootElement
                .GetProperty("resourceLogs")[0]
                .GetProperty("scopeLogs")[0]
                .GetProperty("logRecords")
                .EnumerateArray()
                .Select(record => record.GetProperty("body").GetProperty("stringValue").GetString()!))
            .ToArray();
        Assert.Equal(3, delivered.Length);
        Assert.Contains(delivered, text => text.Contains("e3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_torn_trailing_record_is_left_for_the_next_pass()
    {
        var root = NewRoot("export-torn");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var segment = WriteSegment(options, index: 0, records:
        [
            """{"event_type":"server.started","event_id":"e1"}""",
        ]);
        // A record the writer has not finished flushing: no trailing newline.
        File.AppendAllText(segment, """{"event_type":"call.completed","event_id":"tor""");

        using var receiver = new FakeHttpDestination();
        var cursorStore = new AuditExportCursorStore(root);
        await using var service = NewService(options, receiver, cursorStore, new AuditExportHealth());
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));

        var body = Assert.Single(receiver.Requests).Body;
        Assert.DoesNotContain("\"tor", body, StringComparison.Ordinal);

        // Once the writer completes the record, it is delivered whole.
        File.AppendAllText(segment, "ndelivered\"}\n");
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        Assert.Contains(
            "torndelivered",
            receiver.Requests[^1].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_destination_holds_the_cursor_and_reports_health()
    {
        var root = NewRoot("export-failing");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        WriteSegment(options, index: 0, records:
        [
            """{"event_type":"server.started","event_id":"e1"}""",
        ]);

        using var receiver = new FakeHttpDestination
        {
            ResponseStatus = HttpStatusCode.ServiceUnavailable,
        };
        var health = new AuditExportHealth();
        var cursorStore = new AuditExportCursorStore(root);
        await using var service = NewService(options, receiver, cursorStore, health);

        Assert.Equal(0, await service.DrainOnceAsync(CancellationToken.None));
        // Nothing acknowledged means nothing skipped: the cursor stays put so
        // the outage costs lag, never lost custody.
        Assert.Null(cursorStore.Read().SegmentFileName);

        var snapshot = health.Snapshot();
        Assert.True(snapshot.Configured);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
        Assert.Equal("export.http_503", snapshot.LastFailureDetail);
        Assert.True(snapshot.PendingBytes > 0);
        Assert.Contains("retrying", snapshot.StatusLine(), StringComparison.Ordinal);

        // When the destination recovers, the same records are delivered.
        receiver.ResponseStatus = HttpStatusCode.OK;
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        Assert.Equal(0, health.Snapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task A_permanently_refused_batch_does_not_wedge_later_records()
    {
        var root = NewRoot("export-refused");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var segment = WriteSegment(options, index: 0, records:
        [
            """{"event_type":"server.started","event_id":"e1"}""",
        ]);

        using var receiver = new FakeHttpDestination
        {
            ResponseStatus = HttpStatusCode.BadRequest,
        };
        var health = new AuditExportHealth();
        var cursorStore = new AuditExportCursorStore(root);
        await using var service = NewService(options, receiver, cursorStore, health);

        await service.DrainOnceAsync(CancellationToken.None);
        Assert.Equal("export.http_400", health.Snapshot().LastFailureDetail);
        // Stepped over, and reported — a poison batch cannot block custody of
        // everything recorded after it.
        Assert.Equal(
            Path.GetFileName(segment),
            cursorStore.Read().SegmentFileName);

        receiver.ResponseStatus = HttpStatusCode.OK;
        AppendRecords(segment, ["""{"event_type":"call.completed","event_id":"e2"}"""]);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
    }

    [Fact]
    public void Export_health_reports_a_missing_destination_as_local_journal_only()
    {
        var health = new AuditExportHealth();
        Assert.Contains(
            "not configured (local journal only)",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    private static AuditExportService NewService(
        AuditOptions options,
        FakeHttpDestination receiver,
        AuditExportCursorStore cursorStore,
        AuditExportHealth health)
    {
        var settings = new AuditExportSettings(
            AuditDestinationKind.OtlpHttp,
            receiver.BaseUri,
            null);
        return new AuditExportService(
            options,
            new HttpAuditDestination(settings),
            cursorStore,
            health);
    }

    private static string WriteSegment(
        AuditOptions options,
        int index,
        string[] records)
    {
        var identity = AuditSpoolSegmentIdentity.Create(Guid.NewGuid(), index);
        var path = Path.Combine(options.SpoolDirectory, identity.FileName);
        File.WriteAllText(path, string.Concat(records.Select(record => record + "\n")));
        return path;
    }

    private static void AppendRecords(string segmentPath, string[] records) =>
        File.AppendAllText(
            segmentPath,
            string.Concat(records.Select(record => record + "\n")));

    private static void WriteSettings(
        string root,
        string kind,
        string endpoint,
        string credential)
    {
        var path = Path.Combine(root, AuditExportSettings.FileName);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                kind,
                endpoint,
                credential,
            }));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, SecureAuditStorage.OwnerFileMode);
    }

    private string NewRoot(string label)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            $"test-{label}-{Guid.NewGuid():N}");
        _roots.Add(root);
        return SecureAuditStorage.PrepareRoot(root);
    }

    /// <summary>Minimal in-process HTTP endpoint standing in for Splunk, a
    /// collector, or the PTK receiver — all three are reached identically.</summary>
    private sealed class FakeHttpDestination : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _loop;

        internal FakeHttpDestination()
        {
            var port = FreePort();
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUri.ToString());
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        internal Uri BaseUri { get; }

        internal HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;

        internal List<ReceivedRequest> Requests { get; } = [];

        private async Task AcceptAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                using var reader = new StreamReader(
                    context.Request.InputStream,
                    Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                lock (Requests)
                {
                    Requests.Add(new ReceivedRequest(
                        context.Request.Url?.AbsolutePath ?? string.Empty,
                        context.Request.Headers["Authorization"],
                        body));
                }
                context.Response.StatusCode = (int)ResponseStatus;
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _stopping.Cancel();
            try { _listener.Stop(); } catch { /* already stopped */ }
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { /* shutting down */ }
            _listener.Close();
            _stopping.Dispose();
        }

        internal sealed record ReceivedRequest(
            string Path,
            string? Authorization,
            string Body);
    }
}
