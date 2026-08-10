using System.Globalization;

namespace PtkMcpServer.Audit.Export;

internal sealed record AuditExportHealthSnapshot(
    bool Configured,
    string Destination,
    long DeliveredRecords,
    long PendingBytes,
    int ConsecutiveFailures,
    string? LastFailureDetail,
    DateTimeOffset? LastDeliveryUtc)
{
    /// <summary>
    /// The operator-facing line in ptk_state. Export health is reported
    /// separately from audit health on purpose: a failing exporter is never
    /// an execution problem, and the two must not be confused (contract
    /// rule 2).
    /// </summary>
    internal string StatusLine()
    {
        if (!Configured) return "audit export: not configured (local journal only)";
        var state = ConsecutiveFailures == 0 ? "healthy" : "retrying";
        var text =
            $"audit export: {state} destination={Destination} " +
            $"delivered={DeliveredRecords.ToString(CultureInfo.InvariantCulture)} " +
            $"pending_bytes={PendingBytes.ToString(CultureInfo.InvariantCulture)}";
        if (ConsecutiveFailures > 0)
        {
            text +=
                $" consecutive_failures={ConsecutiveFailures.ToString(CultureInfo.InvariantCulture)}" +
                $" detail={LastFailureDetail ?? "unknown"}";
        }
        if (LastDeliveryUtc is not null)
            text += $" last_delivery_utc={LastDeliveryUtc.Value.ToString("O", CultureInfo.InvariantCulture)}";
        return text;
    }
}

/// <summary>Thread-safe holder so ptk_state can read export health from the
/// supervisor while the exporter runs.</summary>
internal sealed class AuditExportHealth
{
    private readonly object _gate = new();
    private AuditExportHealthSnapshot _snapshot =
        new(false, "none", 0, 0, 0, null, null);

    internal AuditExportHealthSnapshot Snapshot()
    {
        lock (_gate) return _snapshot;
    }

    internal void SetConfigured(string destination)
    {
        lock (_gate)
            _snapshot = _snapshot with { Configured = true, Destination = destination };
    }

    internal void RecordDelivery(int records, DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                DeliveredRecords = _snapshot.DeliveredRecords + records,
                ConsecutiveFailures = 0,
                LastFailureDetail = null,
                LastDeliveryUtc = utcNow,
            };
        }
    }

    internal void RecordFailure(string detailCode)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                ConsecutiveFailures = _snapshot.ConsecutiveFailures + 1,
                LastFailureDetail = detailCode,
            };
        }
    }

    internal void RecordPendingBytes(long pendingBytes)
    {
        lock (_gate)
            _snapshot = _snapshot with { PendingBytes = Math.Max(0, pendingBytes) };
    }
}
