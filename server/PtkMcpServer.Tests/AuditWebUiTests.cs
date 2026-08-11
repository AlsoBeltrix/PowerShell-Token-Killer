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
