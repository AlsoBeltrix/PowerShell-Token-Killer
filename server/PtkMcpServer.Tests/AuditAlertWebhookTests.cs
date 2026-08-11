using System.Net;
using System.Text;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.Export;
using PtkMcpServer.Audit.Web;

namespace PtkMcpServer.Tests;

/// <summary>
/// audit-restoration R4, reporting surface (c): edge-triggered operator
/// alerts over a plain webhook. Conditions post when they appear or grow,
/// never repeatedly for an unchanged state, and an undeliverable webhook
/// retries the pending edge without ever gating anything.
/// </summary>
public sealed class AuditAlertWebhookTests : IDisposable
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
    public async Task Conditions_post_on_edges_and_never_repeat_for_unchanged_state()
    {
        var root = NewRoot("webhook-edges");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var exportHealth = new AuditExportHealth();
        exportHealth.SetConfigured("otlp_http https://siem.example/");
        using var receiver = new WebhookReceiver();
        await using var service = new AuditAlertWebhookService(
            options,
            health,
            exportHealth,
            receiver.BaseUri);

        // Nothing wrong: nothing posts.
        Assert.False(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Empty(receiver.Bodies);

        // A gap appears: one post carrying the condition.
        exportHealth.SetExportGaps(2, 5);
        Assert.True(await service.CheckOnceAsync(CancellationToken.None));
        var body = Assert.Single(receiver.Bodies);
        Assert.Contains("export_gaps", body, StringComparison.Ordinal);
        Assert.Contains("ptk-audit", body, StringComparison.Ordinal);

        // Unchanged state: silent.
        Assert.False(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Single(receiver.Bodies);

        // Growth fires again.
        exportHealth.SetExportGaps(3, 6);
        Assert.True(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Equal(2, receiver.Bodies.Count);
    }

    [Fact]
    public async Task An_undeliverable_webhook_keeps_the_edge_pending()
    {
        var root = NewRoot("webhook-retry");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var exportHealth = new AuditExportHealth();
        exportHealth.SetConfigured("otlp_http https://siem.example/");
        using var receiver = new WebhookReceiver { ResponseStatus = HttpStatusCode.BadGateway };
        await using var service = new AuditAlertWebhookService(
            options,
            health,
            exportHealth,
            receiver.BaseUri);

        exportHealth.SetRefusedRecords(1);
        Assert.False(await service.CheckOnceAsync(CancellationToken.None));

        // The destination heals; the pending edge delivers on the next pass.
        receiver.ResponseStatus = HttpStatusCode.OK;
        Assert.True(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Contains(
            "refused_records",
            Assert.Single(receiver.Bodies),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_historic_quarantine_artifact_appearing_after_startup_does_not_page()
    {
        // cr5-2: the constructor must do no filesystem baseline count, so
        // "new" is judged from the quarantine instant embedded in the file
        // name. A restored historic artifact is not a new quarantine event.
        var root = NewRoot("webhook-quarantine-historic");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var exportHealth = new AuditExportHealth();
        exportHealth.SetConfigured("otlp_http https://siem.example/");
        using var receiver = new WebhookReceiver();
        await using var service = new AuditAlertWebhookService(
            options,
            health,
            exportHealth,
            receiver.BaseUri);

        WriteQuarantineArtifact(root, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.False(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Empty(receiver.Bodies);
    }

    [Fact]
    public async Task A_quarantine_minted_after_startup_pages_and_growth_pages_again()
    {
        var root = NewRoot("webhook-quarantine-new");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var exportHealth = new AuditExportHealth();
        exportHealth.SetConfigured("otlp_http https://siem.example/");
        using var receiver = new WebhookReceiver();
        await using var service = new AuditAlertWebhookService(
            options,
            health,
            exportHealth,
            receiver.BaseUri);

        WriteQuarantineArtifact(root, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Contains(
            "quarantine",
            Assert.Single(receiver.Bodies),
            StringComparison.Ordinal);

        // Acknowledged: unchanged state stays silent.
        Assert.False(await service.CheckOnceAsync(CancellationToken.None));

        // Growth fires again.
        WriteQuarantineArtifact(root, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Equal(2, receiver.Bodies.Count);
    }

    [Fact]
    public async Task A_quarantine_name_without_a_parseable_instant_counts_as_new()
    {
        // An alerting channel fails noisy: a foreign artifact the naming
        // scheme cannot date pages once instead of never.
        var root = NewRoot("webhook-quarantine-foreign");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var exportHealth = new AuditExportHealth();
        exportHealth.SetConfigured("otlp_http https://siem.example/");
        using var receiver = new WebhookReceiver();
        await using var service = new AuditAlertWebhookService(
            options,
            health,
            exportHealth,
            receiver.BaseUri);

        var directory = SecureAuditStorage.PrepareRoot(
            Path.Combine(root, AuditJournalFactory.QuarantineDirectoryName));
        File.WriteAllText(Path.Combine(directory, "mystery-artifact"), "?");
        Assert.True(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Contains(
            "quarantine",
            Assert.Single(receiver.Bodies),
            StringComparison.Ordinal);
        Assert.False(await service.CheckOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Lineage_failure_growth_and_new_episodes_page_again()
    {
        // cr5-7: lineage is live state — growth within an episode pages
        // like every other counter, and recovery to zero re-arms the edge
        // so a NEW episode pages instead of being ignored for the process
        // lifetime.
        var root = NewRoot("webhook-lineage");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var exportHealth = new AuditExportHealth();
        exportHealth.SetConfigured("otlp_http https://siem.example/");
        using var receiver = new WebhookReceiver();
        await using var service = new AuditAlertWebhookService(
            options,
            health,
            exportHealth,
            receiver.BaseUri);

        health.UpdateLineagePublishFailures(1);
        Assert.True(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Contains(
            "lineage_unpublished",
            Assert.Single(receiver.Bodies),
            StringComparison.Ordinal);

        // Unchanged: silent. Growth within the episode: pages again.
        Assert.False(await service.CheckOnceAsync(CancellationToken.None));
        health.UpdateLineagePublishFailures(2);
        Assert.True(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Equal(2, receiver.Bodies.Count);

        // Recovery, then a fresh episode: pages again.
        health.UpdateLineagePublishFailures(0);
        Assert.False(await service.CheckOnceAsync(CancellationToken.None));
        health.UpdateLineagePublishFailures(1);
        Assert.True(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Equal(3, receiver.Bodies.Count);
    }

    [Fact]
    public async Task A_failed_edge_survives_the_condition_healing()
    {
        // cr5-6: an observed edge whose post failed must still reach the
        // operator even when the condition recovers before the retry —
        // otherwise a transient episode that raced the webhook outage is
        // silently lost.
        var root = NewRoot("webhook-pending-heal");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var exportHealth = new AuditExportHealth();
        exportHealth.SetConfigured("otlp_http https://siem.example/");
        using var receiver = new WebhookReceiver { ResponseStatus = HttpStatusCode.BadGateway };
        await using var service = new AuditAlertWebhookService(
            options,
            health,
            exportHealth,
            receiver.BaseUri);

        health.UpdateLineagePublishFailures(1);
        Assert.False(await service.CheckOnceAsync(CancellationToken.None));

        // The condition heals AND the destination recovers: the observed
        // edge still delivers, once.
        health.UpdateLineagePublishFailures(0);
        receiver.ResponseStatus = HttpStatusCode.OK;
        Assert.True(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Contains(
            "lineage_unpublished",
            Assert.Single(receiver.Bodies),
            StringComparison.Ordinal);
        Assert.False(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Single(receiver.Bodies);
    }

    [Fact]
    public async Task Webhook_delivery_failure_is_visible_in_export_health()
    {
        // cr5-8: the paging surface must not be able to fail silently. A
        // failing webhook shows on the export status line (ptk_state) and
        // in the snapshot; recovery clears it; none of it gates anything.
        var root = NewRoot("webhook-health");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var exportHealth = new AuditExportHealth();
        exportHealth.SetConfigured("otlp_http https://siem.example/");

        // No webhook configured: the line says nothing about one.
        Assert.DoesNotContain(
            "alert_webhook",
            exportHealth.Snapshot().StatusLine(),
            StringComparison.OrdinalIgnoreCase);

        using var receiver = new WebhookReceiver { ResponseStatus = HttpStatusCode.BadGateway };
        await using var service = new AuditAlertWebhookService(
            options,
            health,
            exportHealth,
            receiver.BaseUri);

        exportHealth.SetRefusedRecords(1);
        Assert.False(await service.CheckOnceAsync(CancellationToken.None));
        var failing = exportHealth.Snapshot();
        Assert.Equal(1, failing.AlertWebhookConsecutiveFailures);
        Assert.Equal("http_502", failing.AlertWebhookLastFailure);
        Assert.Null(failing.AlertWebhookLastSuccessUtc);
        Assert.Contains(
            "ALERT_WEBHOOK_FAILING=1 detail=http_502",
            failing.StatusLine(),
            StringComparison.Ordinal);

        // Recovery clears the failure and stamps the delivery.
        receiver.ResponseStatus = HttpStatusCode.OK;
        Assert.True(await service.CheckOnceAsync(CancellationToken.None));
        var healthy = exportHealth.Snapshot();
        Assert.Equal(0, healthy.AlertWebhookConsecutiveFailures);
        Assert.Null(healthy.AlertWebhookLastFailure);
        Assert.NotNull(healthy.AlertWebhookLastSuccessUtc);
        Assert.Contains(
            "alert_webhook=ok",
            healthy.StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quarantine_minted_within_the_starting_millisecond_still_pages()
    {
        // cr5-2 reopen round 1: filename stamps carry milliseconds, the
        // construction instant carried sub-millisecond ticks — a
        // quarantine minted later within the same millisecond compared
        // earlier and vanished. The comparison must be fail-noisy at the
        // shared granularity.
        var root = NewRoot("webhook-quarantine-same-ms");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var exportHealth = new AuditExportHealth();
        exportHealth.SetConfigured("otlp_http https://siem.example/");
        using var receiver = new WebhookReceiver();
        var started = new DateTimeOffset(2026, 8, 11, 12, 0, 0, 123, TimeSpan.Zero)
            .AddTicks(7_777);
        await using var service = new AuditAlertWebhookService(
            options,
            health,
            exportHealth,
            receiver.BaseUri,
            startedUtc: started);

        // The artifact's stamp is the same millisecond, sub-ms truncated.
        WriteQuarantineArtifact(
            root,
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, 123, TimeSpan.Zero));
        Assert.True(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Contains(
            "quarantine",
            Assert.Single(receiver.Bodies),
            StringComparison.Ordinal);
    }

    private static void WriteQuarantineArtifact(string root, DateTimeOffset minted)
    {
        var directory = SecureAuditStorage.PrepareRoot(
            Path.Combine(root, AuditJournalFactory.QuarantineDirectoryName));
        var name = $"host.id.{minted:yyyyMMddTHHmmssfffZ}.{Guid.NewGuid():N}";
        File.WriteAllText(Path.Combine(directory, name), "quarantined bytes");
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

    private sealed class WebhookReceiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _loop;

        internal WebhookReceiver()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUri.ToString());
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        internal Uri BaseUri { get; }

        internal HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;

        internal List<string> Bodies { get; } = [];

        private async Task AcceptAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var status = ResponseStatus;
                lock (Bodies)
                {
                    if (status == HttpStatusCode.OK) Bodies.Add(body);
                }
                context.Response.StatusCode = (int)status;
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();
            try { _listener.Stop(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _listener.Close();
            _stopping.Dispose();
        }
    }
}
