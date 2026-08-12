using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Ingest;

namespace PtkSiemReceiver.Storage;

/// <summary>
/// Applies the configured retention bounds on a bounded schedule (rbc-11:
/// the options were parsed but never enforced, so an unattended receiver's
/// SQLite store grew without bound and the README warned against deploying
/// it). A sweep failure is logged and retried on the next tick — retention
/// housekeeping must never take ingest down.
/// </summary>
internal sealed class RetentionService : BackgroundService
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    private readonly SiemReceiverOptions _options;
    private readonly IIngestCommitter _committer;
    private readonly ILogger<RetentionService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly CustodyHealthState _custodyHealth;

    public RetentionService(
        SiemReceiverOptions options,
        IIngestCommitter committer,
        ILogger<RetentionService> logger,
        CustodyHealthState custodyHealth,
        TimeSpan? interval = null,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _committer = committer;
        _logger = logger;
        _interval = interval ?? DefaultInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _custodyHealth = custodyHealth;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RetentionMaxAgeDays is null && _options.RetentionMaxTotalBytes is null)
        {
            _logger.LogInformation(
                "Retention is not configured: the ingest store grows until the operator bounds it.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(_interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task<SiemRetentionOutcome?> SweepOnceAsync(CancellationToken cancellationToken)
    {
        if (_committer is not SqliteIngestStore store) return null;
        if (!_custodyHealth.CanMutate) return null;
        try
        {
            var outcome = await store.EnforceRetentionAsync(
                _options.RetentionMaxAgeDays,
                _options.RetentionMaxTotalBytes,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            if (outcome.EventsRemoved > 0 || outcome.QuarantineRemoved > 0)
            {
                _logger.LogInformation(
                    "Retention removed {Events} event(s) and {Quarantine} quarantine attempt(s); " +
                    "store is now {Bytes} bytes. Custody receipts are never removed.",
                    outcome.EventsRemoved,
                    outcome.QuarantineRemoved,
                    outcome.DatabaseBytes);
            }
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "A retention sweep failed; ingest is unaffected and the sweep retries on the next interval.");
            return null;
        }
    }
}
