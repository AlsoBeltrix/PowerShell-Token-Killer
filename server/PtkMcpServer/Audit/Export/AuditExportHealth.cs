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
    long ExportGaps = 0,
    long MissingRecords = 0,
    long UnverifiedBootBoundaries = 0,
    long RefusedRecords = 0)
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
        var state = ConsecutiveFailures == 0 &&
            ExportGaps == 0 &&
            RefusedRecords == 0 &&
            UnverifiedBootBoundaries == 0
            ? "healthy"
            : ConsecutiveFailures > 0 ? "retrying" : "degraded";
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
        // Suspicion is reported separately from proof: an unverified
        // boundary means the previous supervisor boot's tail cannot be shown
        // delivered (it ended without its lifecycle terminal), NOT that
        // records are known lost.
        if (UnverifiedBootBoundaries > 0)
        {
            text +=
                $" unverified_boot_boundaries={UnverifiedBootBoundaries.ToString(CultureInfo.InvariantCulture)}" +
                " (a previous supervisor boot ended without its terminal record;" +
                " its tail cannot be proved delivered)";
        }
        // A permanently refused record was never delivered: lost custody at
        // the destination, held to the same durable bar as a gap.
        if (RefusedRecords > 0)
        {
            text +=
                $" REFUSED_RECORDS={RefusedRecords.ToString(CultureInfo.InvariantCulture)}" +
                " (the destination permanently refused these records)";
        }
        if (ExportGaps > 0)
        {
            text +=
                $" EXPORT_GAPS={ExportGaps.ToString(CultureInfo.InvariantCulture)} " +
                $"missing_records={MissingRecords.ToString(CultureInfo.InvariantCulture)} " +
                "(records were removed locally before delivery)";
        }
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
                // A gap is permanent: a later success clears the transient
                // failure detail but never the gap record itself.
                // Permanent losses outlive a later success; only transient
                // failure detail is cleared.
                LastFailureDetail = _snapshot.ExportGaps > 0
                    ? "export.gap_records_lost"
                    : _snapshot.RefusedRecords > 0
                        ? "export.records_refused"
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
    /// Publishes the DURABLE gap count (see <see cref="AuditExportGapStore"/>):
    /// segments local retention deleted before delivery — permanently lost
    /// custody. The count is owned on disk so it survives restarts, and a
    /// later success never clears it: the gap happened.
    /// </summary>
    internal void SetExportGaps(long count, long missingRecords = 0)
    {
        if (count < 0) return;
        lock (_gate)
        {
            if (count <= _snapshot.ExportGaps) return;
            _snapshot = _snapshot with
            {
                ExportGaps = count,
                MissingRecords = Math.Max(_snapshot.MissingRecords, missingRecords),
                LastFailureDetail = "export.gap_records_lost",
            };
        }
    }

    /// <summary>
    /// Notes that a supervisor boot's final records cannot be proved
    /// delivered. Counted once per boot: this is an unprovable boundary, not
    /// evidence of loss, and it never inflates the gap count.
    /// </summary>
    internal void RecordUnverifiedBootBoundary(string supervisorBootId, long lastSequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supervisorBootId);
        lock (_gate)
        {
            if (!_unverifiedBoundaries.Add($"{supervisorBootId}:{lastSequence}")) return;
            _snapshot = _snapshot with
            {
                UnverifiedBootBoundaries = _snapshot.UnverifiedBootBoundaries + 1,
            };
        }
    }

    private readonly HashSet<string> _unverifiedBoundaries = new(StringComparer.Ordinal);

    internal void SetRefusedRecords(long count)
    {
        if (count < 0) return;
        lock (_gate)
        {
            if (count <= _snapshot.RefusedRecords) return;
            _snapshot = _snapshot with
            {
                RefusedRecords = count,
                LastFailureDetail = "export.records_refused",
            };
        }
    }

    internal void RecordPendingBytes(long pendingBytes)
    {
        lock (_gate)
            _snapshot = _snapshot with { PendingBytes = Math.Max(0, pendingBytes) };
    }
}
