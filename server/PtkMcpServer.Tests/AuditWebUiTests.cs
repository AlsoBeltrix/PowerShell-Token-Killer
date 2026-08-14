using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.Export;
using PtkMcpServer.Audit.Web;

namespace PtkMcpServer.Tests;

/// <summary>
/// audit-restoration R4: the loopback journal web UI — "open a browser, see
/// the logs". Journal-backed, token-authenticated, one instance per audit
/// root, and incapable of gating execution.
/// </summary>
public sealed class AuditWebUiTests : IDisposable
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
    public async Task The_ui_serves_journal_records_health_and_the_page_behind_its_token()
    {
        var root = NewRoot("webui-serves");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        using var journal = AuditJournalFactory.Open(options, health, "test-version");
        AppendEvents(journal, 3);

        var exportHealth = new AuditExportHealth();
        var port = FreePort();
        await using var service = new AuditWebUiService(
            options,
            health,
            exportHealth,
            () => journal,
            port);
        await service.StartAsync(CancellationToken.None);
        var token = await WaitForTokenAsync(root);
        using var client = new HttpClient();

        // No token: refused. Wrong token: refused.
        using (var anonymous = await client.GetAsync($"http://127.0.0.1:{port}/api/health"))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var wrong = await GetAsync(client, port, "/api/health", "not-the-token"))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // Health JSON carries the audit state and the export line.
        using (var response = await GetAsync(client, port, "/api/health", token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(
                "healthy",
                payload.RootElement.GetProperty("audit").GetProperty("state").GetString());
            Assert.True(payload.RootElement.GetProperty("spool").GetProperty("segments").GetInt32() >= 1);
        }

        // Records include this supervisor's LIVE tail, read through the
        // journal writer's handle — the segment file itself is locked.
        using (var response = await GetAsync(client, port, "/api/records?tail=10", token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var records = payload.RootElement.GetProperty("records");
            Assert.Equal(3, records.GetArrayLength());
            Assert.Contains(
                "call.completed",
                records[0].GetString(),
                StringComparison.Ordinal);
        }

        // The page itself serves.
        using (var response = await GetAsync(client, port, "/", token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(
                "PTK Audit",
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_settings_page_round_trips_without_ever_echoing_the_credential()
    {
        var root = NewRoot("webui-settings");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var port = FreePort();
        await using var service = new AuditWebUiService(
            options,
            health,
            new AuditExportHealth(),
            () => null,
            port);
        await service.StartAsync(CancellationToken.None);
        var token = await WaitForTokenAsync(root);
        using var client = new HttpClient();

        // An invalid endpoint for a configured kind is refused with the
        // loader's own rule (plaintext HTTP only for loopback).
        using (var response = await PutAsync(client, port, token, new
        {
            kind = "otlp_http",
            endpoint = "http://siem.example.com:4318/",
            credential = "secret-token-1234",
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var response = await PutAsync(client, port, token, new
        {
            kind = "otlp_http",
            endpoint = "https://siem.example.com:4318/",
            credential = "secret-token-1234",
        }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The write is loader-readable and the credential survives an
        // endpoint-only update; the GET never echoes it.
        using (var response = await GetAsync(client, port, "/api/settings", token))
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"credential_set\":true", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-token-1234", text, StringComparison.Ordinal);
        }
        using (var response = await PutAsync(client, port, token, new
        {
            kind = "otlp_http",
            endpoint = "https://other.example.com:4318/",
        }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var settings = AuditExportSettings.Load(root, out var failure);
        Assert.Null(failure);
        Assert.Equal(AuditDestinationKind.OtlpHttp, settings.Kind);
        Assert.Equal("https://other.example.com:4318/", settings.Endpoint!.ToString());
        Assert.Equal("secret-token-1234", settings.Credential);
    }

    [Fact]
    public async Task A_second_supervisor_stands_by_when_the_port_is_taken()
    {
        var root = NewRoot("webui-standby");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var port = FreePort();
        await using var first = new AuditWebUiService(
            options, health, new AuditExportHealth(), () => null, port);
        await first.StartAsync(CancellationToken.None);
        _ = await WaitForTokenAsync(root);
        Assert.True(await WaitAsync(() => first.IsServing));

        await using var second = new AuditWebUiService(
            options, health, new AuditExportHealth(), () => null, port,
            bindRetryInterval: TimeSpan.FromMilliseconds(100));
        await second.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.False(second.IsServing);

        // The loser takes over when the holder goes away.
        await first.DisposeAsync();
        Assert.True(await WaitAsync(() => second.IsServing));
    }

    [Fact]
    public async Task An_unreadable_closed_segment_marks_the_answer_partial()
    {
        // cr5-3: a closed segment that fails to read is omitted evidence,
        // and the answer must say so instead of returning HTTP 200 as
        // though the record list were complete.
        var root = NewRoot("webui-partial");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = Guid.NewGuid();
        var locked = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(boot, 0).FileName);
        File.WriteAllText(locked, """{"event_type":"call.completed","event_id":"e0"}""" + "\n");
        var readable = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(boot, 1).FileName);
        File.WriteAllText(readable, """{"event_type":"call.completed","event_id":"e1"}""" + "\n");

        var health = new AuditHealth(options);
        var port = FreePort();
        await using var service = new AuditWebUiService(
            options, health, new AuditExportHealth(), () => null, port);
        await service.StartAsync(CancellationToken.None);
        var token = await WaitForTokenAsync(root);
        using var client = new HttpClient();

        // A non-newest segment held the way a writer holds its live one is
        // NOT a live segment; its failure must be reported, while readable
        // records still serve.
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var response = await GetAsync(client, port, "/api/records?tail=10", token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(payload.RootElement.GetProperty("partial").GetBoolean());
            Assert.Equal(1, payload.RootElement.GetProperty("unreadable_count").GetInt32());
            var records = payload.RootElement.GetProperty("records");
            Assert.Equal(1, records.GetArrayLength());
            Assert.Contains("e1", records[0].GetString(), StringComparison.Ordinal);
            Assert.Equal(
                Path.GetFileName(locked),
                payload.RootElement.GetProperty("unreadable_segments")[0]
                    .GetProperty("segment").GetString());
        }

        // Released: the answer is complete again and says so.
        using (var response = await GetAsync(client, port, "/api/records?tail=10", token))
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(payload.RootElement.GetProperty("partial").GetBoolean());
            Assert.Equal(2, payload.RootElement.GetProperty("records").GetArrayLength());
        }
    }

    [Fact]
    public async Task Another_supervisors_locked_live_segment_is_expected_not_partial()
    {
        // The one position a live segment can occupy is the newest of its
        // boot; a lock there is the stated shared-root limit, not evidence
        // loss, and must not false-alarm the partial marker.
        var root = NewRoot("webui-live-lock");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = Guid.NewGuid();
        var closed = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(boot, 0).FileName);
        File.WriteAllText(closed, """{"event_type":"call.completed","event_id":"e0"}""" + "\n");
        var live = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(boot, 1).FileName);
        File.WriteAllText(live, """{"event_type":"call.completed","event_id":"e1"}""" + "\n");

        var health = new AuditHealth(options);
        var port = FreePort();
        await using var service = new AuditWebUiService(
            options, health, new AuditExportHealth(), () => null, port);
        await service.StartAsync(CancellationToken.None);
        var token = await WaitForTokenAsync(root);
        using var client = new HttpClient();

        using (new FileStream(live, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var response = await GetAsync(client, port, "/api/records?tail=10", token))
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(payload.RootElement.GetProperty("partial").GetBoolean());
            Assert.Equal(0, payload.RootElement.GetProperty("unreadable_count").GetInt32());
            var records = payload.RootElement.GetProperty("records");
            Assert.Equal(1, records.GetArrayLength());
            Assert.Contains("e0", records[0].GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_populated_closed_spool_does_not_hide_the_live_tail()
    {
        // cr5-4: the live tail holds the NEWEST records; a closed spool
        // already holding 4x the requested tail must not short-circuit it
        // into serving older evidence as the newest.
        var root = NewRoot("webui-live-tail");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        using var journal = AuditJournalFactory.Open(options, health, "test-version");
        AppendEvents(journal, 3);
        var closedBoot = Guid.NewGuid();
        var closed = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(closedBoot, 0).FileName);
        File.WriteAllLines(closed, Enumerable.Range(0, 20).Select(index =>
            $$"""{"event_type":"call.completed","event_id":"closed-{{index}}"}"""));
        // The closed spool is genuinely OLDER than the live tail (written
        // afterwards only for TryReserve mechanics); newest-evidence
        // ordering must therefore surface the live records.
        File.SetLastWriteTimeUtc(closed, DateTime.UtcNow.AddHours(-1));

        var port = FreePort();
        await using var service = new AuditWebUiService(
            options, health, new AuditExportHealth(), () => journal, port);
        await service.StartAsync(CancellationToken.None);
        var token = await WaitForTokenAsync(root);
        using var client = new HttpClient();

        using var response = await GetAsync(client, port, "/api/records?tail=5", token);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var records = payload.RootElement.GetProperty("records");
        Assert.Equal(5, records.GetArrayLength());
        var liveBoot = journal.SupervisorBootId.ToString("D");
        Assert.Contains(
            records.EnumerateArray(),
            record => record.GetString()!.Contains(liveBoot, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_token_rotates_per_bind_and_a_stale_token_dies()
    {
        // cr5-1: the bearer token is minted fresh for each bind, published
        // only while this process owns the listener, and deleted on stop —
        // so a token a port squatter harvests is worthless against every
        // future real listener.
        var root = NewRoot("webui-rotate");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var port = FreePort();
        string token1;
        await using (var first = new AuditWebUiService(
            options, health, new AuditExportHealth(), () => null, port))
        {
            await first.StartAsync(CancellationToken.None);
            token1 = await WaitForTokenAsync(root);
            using var probe = new HttpClient();
            using var ok = await GetAsync(probe, port, "/api/health", token1);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // The credential does not outlive its listener.
        Assert.False(File.Exists(Path.Combine(root, AuditWebUiService.TokenFileName)));

        await using var second = new AuditWebUiService(
            options, health, new AuditExportHealth(), () => null, port);
        await second.StartAsync(CancellationToken.None);
        var token2 = await WaitForTokenAsync(root);
        Assert.NotEqual(token1, token2);
        using var client = new HttpClient();
        using (var stale = await GetAsync(client, port, "/api/health", token1))
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        using (var fresh = await GetAsync(client, port, "/api/health", token2))
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task A_bind_failed_standby_never_writes_the_token_file()
    {
        // cr5-5: token creation belongs to the bind winner alone. A
        // contender that lost the port must not create or replace the
        // published credential — that race is how the serving UI could end
        // up not recognizing the token the file holds.
        var root = NewRoot("webui-standby-token");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var port = FreePort();
        var squatter = new TcpListener(IPAddress.Loopback, port);
        squatter.Start();
        try
        {
            await using var service = new AuditWebUiService(
                options, health, new AuditExportHealth(), () => null, port,
                bindRetryInterval: TimeSpan.FromMilliseconds(50));
            await service.StartAsync(CancellationToken.None);
            await Task.Delay(400);
            Assert.False(service.IsServing);
            Assert.False(File.Exists(Path.Combine(root, AuditWebUiService.TokenFileName)));
        }
        finally
        {
            squatter.Stop();
        }
    }

    [Fact]
    public void Segment_read_failure_classification_reports_a_vanished_spool_directory()
    {
        // cr5-3 repair round 1: only the FILE vanishing is retention. A
        // vanished parent DIRECTORY is the spool itself going away —
        // reportable evidence loss, never silence, whatever the segment's
        // position. Both exceptions derive from IOException, so the
        // decision table's arm order is load-bearing.
        Assert.Equal(
            AuditWebUiService.SegmentReadFailureClass.Reportable,
            AuditWebUiService.ClassifySegmentReadFailure(
                new DirectoryNotFoundException(), newestOfBoot: true));
        Assert.Equal(
            AuditWebUiService.SegmentReadFailureClass.Reportable,
            AuditWebUiService.ClassifySegmentReadFailure(
                new DirectoryNotFoundException(), newestOfBoot: false));
        Assert.Equal(
            AuditWebUiService.SegmentReadFailureClass.VanishedSegment,
            AuditWebUiService.ClassifySegmentReadFailure(
                new FileNotFoundException(), newestOfBoot: false));
        Assert.Equal(
            AuditWebUiService.SegmentReadFailureClass.ExpectedLive,
            AuditWebUiService.ClassifySegmentReadFailure(
                new IOException(), newestOfBoot: true));
        Assert.Equal(
            AuditWebUiService.SegmentReadFailureClass.Reportable,
            AuditWebUiService.ClassifySegmentReadFailure(
                new IOException(), newestOfBoot: false));
        Assert.Equal(
            AuditWebUiService.SegmentReadFailureClass.Reportable,
            AuditWebUiService.ClassifySegmentReadFailure(
                new UnauthorizedAccessException(), newestOfBoot: true));
    }

    [Fact]
    public async Task Production_ui_is_destination_status_only_and_redacts_forensic_data()
    {
        const string credential = "STATUS-UI-MUST-NOT-EXPOSE-THIS";
        const string forensicMarker = "RAW-FORENSIC-RECORD-MUST-NOT-APPEAR";
        var root = NewRoot("webui-destination-status-only");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        Directory.CreateDirectory(options.EvidenceDirectory);
        var health = new AuditHealth(options);
        var exportHealth = new AuditExportHealth();
        var registry = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var openFailure);
        Assert.Null(openFailure);
        Assert.True(registry.TryAdd(
            new AuditDestinationDraft(
                AuditDestinationKind.OtlpHttp,
                "primary",
                new Uri("https://siem.example/private/collector/path"),
                credential),
            confirmedSensitiveDuplication: false,
            DateTimeOffset.UtcNow,
            out var destination,
            out var addFailure), addFailure);

        var bootId = Guid.NewGuid();
        var record = JsonSerializer.Serialize(new
        {
            schema_version = AuditEventSerializer.DestinationObligationSchemaVersion,
            event_id = Guid.NewGuid(),
            event_type = "call.completed",
            occurred_utc = "2026-08-14T10:00:00Z",
            required_destination_ids = new[] { destination!.DestinationId.ToString("D") },
            marker = forensicMarker,
        });
        File.WriteAllText(
            Path.Combine(
                options.SpoolDirectory,
                AuditSpoolSegmentIdentity.Create(bootId, 0).FileName),
            record + Environment.NewLine,
            new UTF8Encoding(false));

        var backfills = new AuditBackfillRegistry(root);
        var evidence = new ScriptEvidenceStoreProvider(options);
        await using var coordinator = new AuditExportCoordinator(
            options,
            registry,
            backfills,
            evidence,
            () => null,
            exportHealth);
        using var validator = new AuditDestinationCredentialValidator();
        var operations = new AuditDestinationOperations(
            options,
            registry,
            backfills,
            coordinator,
            validator);
        var port = FreePort();
        await using var service = new AuditWebUiService(
            options,
            health,
            exportHealth,
            () => null,
            port: port,
            coordinator: coordinator,
            destinationOperations: operations);
        await service.StartAsync(CancellationToken.None);
        var token = await WaitForTokenAsync(root);
        using var client = new HttpClient();

        using (var response = await GetAsync(client, port, "/api/status", token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            Assert.Contains(destination.DestinationId.ToString("D"), json, StringComparison.Ordinal);
            Assert.DoesNotContain(credential, json, StringComparison.Ordinal);
            Assert.DoesNotContain("/private/collector/path", json, StringComparison.Ordinal);
            Assert.DoesNotContain(forensicMarker, json, StringComparison.Ordinal);
        }

        using (var response = await GetAsync(client, port, "/", token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(credential, html, StringComparison.Ordinal);
            Assert.DoesNotContain("/private/collector/path", html, StringComparison.Ordinal);
            Assert.DoesNotContain(forensicMarker, html, StringComparison.Ordinal);
        }

        foreach (var path in new[] { "/api/records", "/api/quarantine", "/api/settings" })
        {
            using var response = await GetAsync(client, port, path, token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://127.0.0.1:{port}/api/destinations/{destination.DestinationId:D}/disable"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains(
                "pending_obligations_require_abandonment",
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_stale_live_tail_does_not_outrank_newer_closed_evidence()
    {
        // cr5-4 reopen round 1: with several supervisors on one root, the
        // quiet UI bind winner's live tail can be OLDER than a busy peer's
        // rotated segments. The newest evidence must win whichever unit
        // holds it — position (live-appended-last) is not chronology.
        var root = NewRoot("webui-stale-live");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        using var journal = AuditJournalFactory.Open(options, health, "test-version");
        AppendEvents(journal, 3);
        var busyBoot = Guid.NewGuid();
        var rotated = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(busyBoot, 0).FileName);
        File.WriteAllLines(rotated, Enumerable.Range(0, 20).Select(index =>
            $$"""{"event_type":"call.completed","event_id":"closed-{{index}}"}"""));
        File.SetLastWriteTimeUtc(rotated, DateTime.UtcNow.AddHours(1));

        var port = FreePort();
        await using var service = new AuditWebUiService(
            options, health, new AuditExportHealth(), () => journal, port);
        await service.StartAsync(CancellationToken.None);
        var token = await WaitForTokenAsync(root);
        using var client = new HttpClient();

        using var response = await GetAsync(client, port, "/api/records?tail=5", token);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var records = payload.RootElement.GetProperty("records");
        Assert.Equal(5, records.GetArrayLength());
        Assert.Contains(
            records.EnumerateArray(),
            record => record.GetString()!.Contains("closed-19", StringComparison.Ordinal));
        var liveBoot = journal.SupervisorBootId.ToString("D");
        Assert.DoesNotContain(
            records.EnumerateArray(),
            record => record.GetString()!.Contains(liveBoot, StringComparison.Ordinal));
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private static async Task<string> WaitForTokenAsync(string root)
    {
        var path = Path.Combine(root, AuditWebUiService.TokenFileName);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path)) return (await File.ReadAllTextAsync(path)).Trim();
            await Task.Delay(50);
        }
        throw new TimeoutException("The UI token file never appeared.");
    }

    private static Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        int port,
        string path,
        string token)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://127.0.0.1:{port}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        int port,
        string token,
        object payload)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"http://127.0.0.1:{port}/api/settings")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                new UTF8Encoding(false),
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static void AppendEvents(AuditJournal journal, int count)
    {
        for (var index = 0; index < count; index++)
        {
            Assert.True(journal.TryReserve(1, out var reservation, out _));
            journal.Append(reservation!, new AuditEventInput
            {
                EventType = "call.completed",
                Session = new AuditSession { Name = "default", Generation = 0, BindingKind = "default" },
                Actor = new AuditActor { AttributionStrength = "unknown" },
                Correlation = new AuditCorrelation(),
                Request = new AuditRequest(),
                Routing = new AuditRouting(),
                Outcome = new AuditOutcome { State = "completed", TerminationCertainty = "not_applicable" },
                Coverage = new AuditCoverage
                {
                    PtkRequest = true,
                    RootProcessObserved = "not_applicable",
                    DescendantsObserved = "not_applicable",
                    RemoteEffectObserved = "not_applicable",
                },
                Audit = new AuditEventHealth { ProtectionMode = "local-only", HealthState = "healthy" },
            });
            reservation!.Release();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
}
