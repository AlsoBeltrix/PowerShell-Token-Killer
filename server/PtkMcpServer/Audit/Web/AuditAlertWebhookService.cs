using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Audit.Web;

/// <summary>
/// Operator-wired webhook alerts (audit-restoration R4, reporting surface
/// (c)): when an audit condition that demands a human appears — quarantine,
/// undelivered eviction, export gaps, refused records, unattested boot,
/// degraded audit — one JSON POST goes to the configured URL
/// (Slack/email/pager wiring is the operator's side). Alerts are edges, not
/// levels: a condition posts when it appears or grows, never repeatedly for
/// the same state. Delivery is best-effort with the exporter's own
/// fail-open posture — a dead webhook costs alerts, never execution, and is
/// visible in the export health line it decorates.
/// </summary>
internal sealed class AuditAlertWebhookService : IHostedService, IAsyncDisposable
{
    private readonly AuditOptions _options;
    private readonly AuditHealth _health;
    private readonly AuditExportHealth _exportHealth;
    private readonly Uri? _webhook;
    private readonly HttpClient _client;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private int _disposed;

    private readonly DateTimeOffset _startedUtc;

    private long _notifiedEvictions;
    private long _notifiedGaps;
    private long _notifiedRefused;
    private long _notifiedBoundaries;
    private long _notifiedLineageFailures;
    private int _notifiedQuarantine;
    private bool _notifiedUnhealthy;

    internal AuditAlertWebhookService(
        AuditOptions options,
        AuditHealth health,
        AuditExportHealth exportHealth,
        Uri? webhook,
        HttpClient? client = null,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(exportHealth);
        _options = options;
        _health = health;
        _exportHealth = exportHealth;
        _webhook = webhook;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _interval = interval ?? TimeSpan.FromSeconds(30);
        // No filesystem work here (cr5-2): hosted-service construction sits
        // on the startup path, and an optional webhook must never gate it.
        // "New artifact" is judged per file against this instant, from the
        // quarantine timestamp every writer embeds in the filename — so
        // history stays silent without a startup baseline count.
        _startedUtc = DateTimeOffset.UtcNow;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_webhook is null) return Task.CompletedTask;
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is null) return;
        try { await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // Alerting must never take anything else down.
            }

            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One evaluation pass; separated so tests drive it
    /// deterministically instead of racing the timer.</summary>
    internal async Task<bool> CheckOnceAsync(CancellationToken cancellationToken)
    {
        if (_webhook is null) return false;
        var audit = _health.Snapshot();
        var export = _exportHealth.Snapshot();
        var quarantine = CountQuarantine();
        var conditions = new List<object>();

        var unhealthy = audit.State
            is AuditHealthState.Degraded or AuditHealthState.Unavailable;
        if (unhealthy && !_notifiedUnhealthy)
        {
            conditions.Add(new
            {
                condition = "audit_unhealthy",
                state = audit.State.ToString().ToLowerInvariant(),
                failure_class = audit.FailureClass,
            });
        }
        if (audit.UndeliveredEvictions > _notifiedEvictions)
        {
            conditions.Add(new
            {
                condition = "spool_evicted_undelivered",
                total = audit.UndeliveredEvictions,
            });
        }
        // Lineage is live state, not a monotonic total (cr5-7): recovery to
        // zero re-arms the edge so a NEW failure episode pages again, and
        // growth within an episode pages like every other counter.
        if (audit.LineagePublishFailures == 0) _notifiedLineageFailures = 0;
        if (audit.LineagePublishFailures > _notifiedLineageFailures)
        {
            conditions.Add(new
            {
                condition = "lineage_unpublished",
                consecutive_failures = audit.LineagePublishFailures,
            });
        }
        if (export.ExportGaps > _notifiedGaps)
        {
            conditions.Add(new
            {
                condition = "export_gaps",
                total = export.ExportGaps,
                missing_records = export.MissingRecords,
            });
        }
        if (export.RefusedRecords > _notifiedRefused)
        {
            conditions.Add(new
            {
                condition = "refused_records",
                total = export.RefusedRecords,
            });
        }
        if (export.UnverifiedBootBoundaries > _notifiedBoundaries)
        {
            conditions.Add(new
            {
                condition = "unverified_boot_boundaries",
                total = export.UnverifiedBootBoundaries,
            });
        }
        if (quarantine > _notifiedQuarantine)
        {
            conditions.Add(new
            {
                condition = "quarantine",
                total = quarantine,
            });
        }

        if (conditions.Count == 0)
        {
            _notifiedUnhealthy = unhealthy;
            return false;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            source = "ptk-audit",
            observed_utc = DateTimeOffset.UtcNow.ToString("O"),
            conditions,
            export_status = export.StatusLine(),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, _webhook)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };

        try
        {
            using var response = await _client
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Not delivered: the edge stays pending and retries next pass.
            return false;
        }

        // Acknowledged: advance the notified state so the same facts do not
        // repeat, while any further growth fires again.
        _notifiedUnhealthy = unhealthy;
        _notifiedEvictions = audit.UndeliveredEvictions;
        _notifiedLineageFailures = audit.LineagePublishFailures;
        _notifiedGaps = export.ExportGaps;
        _notifiedRefused = export.RefusedRecords;
        _notifiedBoundaries = export.UnverifiedBootBoundaries;
        _notifiedQuarantine = quarantine;
        return true;
    }

    /// <summary>
    /// Counts quarantine artifacts minted after this service came up, from
    /// the quarantine instant every writer embeds in the filename
    /// (<c>name.yyyyMMddTHHmmssfffZ.guid</c>) — a moved artifact keeps its
    /// original file times, so timestamps on disk cannot answer this. A name
    /// carrying no parseable instant counts as new: an alerting channel
    /// fails noisy, and acknowledgment silences it after one post.
    /// </summary>
    private int CountQuarantine()
    {
        try
        {
            var directory = Path.Combine(
                _options.RootDirectory,
                AuditJournalFactory.QuarantineDirectoryName);
            if (!Directory.Exists(directory)) return 0;
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!TryParseQuarantineInstant(Path.GetFileName(file), out var minted) ||
                    minted >= _startedUtc)
                {
                    count++;
                }
            }
            return count;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return _notifiedQuarantine;
        }
    }

    private static bool TryParseQuarantineInstant(string fileName, out DateTimeOffset minted)
    {
        minted = default;
        var parts = fileName.Split('.');
        return parts.Length >= 3 &&
            DateTimeOffset.TryParseExact(
                parts[^2],
                "yyyyMMddTHHmmssfffZ",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out minted);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopping.Dispose();
        _client.Dispose();
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
