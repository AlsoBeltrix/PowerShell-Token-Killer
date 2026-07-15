using PtkSiemReceiver.Configuration;

namespace PtkSiemReceiver;

/// <summary>
/// S1 skeleton lifecycle service: proves the Generic Host composition and
/// logs a secret-free startup summary. The ingest and operator endpoints
/// land in later slices (S2+).
/// </summary>
internal sealed class ReceiverLifecycleService : BackgroundService
{
    private readonly SiemReceiverOptions _options;
    private readonly ILogger<ReceiverLifecycleService> _logger;

    public ReceiverLifecycleService(
        SiemReceiverOptions options,
        ILogger<ReceiverLifecycleService> logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "PtkSiemReceiver skeleton started: ingest {IngestAddress}:{IngestPort}, " +
            "operator {OperatorAddress}:{OperatorPort}, store {SqlitePath}. " +
            "Ingest/operator endpoints land in later slices.",
            _options.IngestBindAddress,
            _options.IngestPort,
            _options.OperatorBindAddress,
            _options.OperatorPort,
            _options.SqlitePath);
        return Task.CompletedTask;
    }
}
