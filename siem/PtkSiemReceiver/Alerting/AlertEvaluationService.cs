using System.Text;
using System.Text.Json;
using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Storage;

namespace PtkSiemReceiver.Alerting;

/// <summary>
/// Drains the durable alert work-item queue (mini-SIEM S6): every row the
/// ingest transaction enqueued is evaluated against the startup-frozen
/// rules exactly once — the store commits the alerts, their custody
/// entries, and the cursor advance atomically, so a crash anywhere replays
/// or skips whole items, never halves. The optional webhook fires strictly
/// AFTER that commit with a bounded retry; delivery is reporting, and can
/// never gate or lose an alert.
/// </summary>
internal sealed class AlertEvaluationService(
    SiemReceiverOptions options,
    SqliteIngestStore store,
    ILogger<AlertEvaluationService> logger,
    TimeProvider timeProvider,
    CustodyHealthState custodyHealth) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    internal const int WebhookAttempts = 3;
    internal static readonly TimeSpan WebhookRetryDelay = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.AlertRuleConfigHash is null) return;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await EvaluateOnceAsync(client, stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                logger.LogWarning(
                    exception, "Alert evaluation failed; retrying after the poll interval.");
                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    internal async Task<bool> EvaluateOnceAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        if (!custodyHealth.CanMutate) return false;
        var created = await store.EvaluateNextAlertWorkItemAsync(
            options.AlertRules,
            options.AlertRuleConfigHash!,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (created is null) return false;

        foreach (var alert in created)
            await DeliverWebhookAsync(client, alert, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task DeliverWebhookAsync(
        HttpClient client,
        CreatedAlert alert,
        CancellationToken cancellationToken)
    {
        if (options.AlertWebhookUrl is null) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            alert_id = alert.AlertId,
            rule = alert.RuleName,
            subject_kind = alert.SubjectKind,
            subject_id = alert.SubjectId,
            created_utc = alert.CreatedUtc,
            detail = alert.Detail,
        });
        for (var attempt = 1; attempt <= WebhookAttempts; attempt++)
        {
            try
            {
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var response = await client.PostAsync(
                    options.AlertWebhookUrl, content, cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // fall through to the bounded retry
                _ = exception;
            }

            if (attempt < WebhookAttempts)
                await Task.Delay(WebhookRetryDelay, cancellationToken);
        }

        logger.LogWarning(
            "Alert webhook delivery failed after {Attempts} attempts for alert {AlertId} ({Rule}).",
            WebhookAttempts,
            alert.AlertId,
            alert.RuleName);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
