using System.Globalization;

namespace PtkMcpServer.Audit.Export;

internal sealed record AuditExportHealthSnapshot(
    bool Configured,
    string Destination,
    long DeliveredRecords,
    long PendingBytes,
    int ConsecutiveFailures,
    string? LastFailureDetail,
    DateTimeOffset? LastDeliveryUtc,
    long ExportGaps = 0)
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
        // A gap is permanently lost custody, not a transient condition: it
        // stays on the line for the life of the process.
        if (ExportGaps > 0)
        {
            text +=
                $" EXPORT_GAPS={ExportGaps.ToString(CultureInfo.InvariantCulture)} " +
                "(spool retention deleted undelivered records)";
        }
        return text;
    }
}

/// <summary>Thread-safe holder so ptk_state can read export health from the
/// supervisor while the exporter runs.</summary>
internal sealed class AuditExportHealth
{
    private readonly object _gate = new();
    private readonly HashSet<string> _gapSegments = new(StringComparer.Ordinal);
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
                // A gap is permanent: a later success clears the transient
                // failure detail but never the gap record itself.
                LastFailureDetail = _snapshot.ExportGaps > 0
                    ? "export.gap_spool_deleted"
                    : null,
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

    /// <summary>
    /// Records that local spool retention deleted a segment before it was
    /// delivered — permanently lost custody at the destination. Counted once
    /// per segment so a repeating drain cannot inflate the number, and never
    /// cleared by a later success: the gap happened.
    /// </summary>
    internal void RecordExportGap(string segmentFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentFileName);
        lock (_gate)
        {
            if (!_gapSegments.Add(segmentFileName)) return;
            _snapshot = _snapshot with
            {
                ExportGaps = _snapshot.ExportGaps + 1,
                LastFailureDetail = "export.gap_spool_deleted",
            };
        }
    }

    internal void RecordPendingBytes(long pendingBytes)
    {
        lock (_gate)
            _snapshot = _snapshot with { PendingBytes = Math.Max(0, pendingBytes) };
    }
}
