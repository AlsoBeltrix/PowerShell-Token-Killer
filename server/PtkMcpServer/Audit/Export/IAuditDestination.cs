namespace PtkMcpServer.Audit.Export;

internal enum AuditDeliveryDisposition
{
    /// <summary>The destination durably accepted the batch.</summary>
    Delivered,

    /// <summary>Transient: the same batch is retried unchanged.</summary>
    Retryable,

    /// <summary>The destination refused the batch itself (schema, auth,
    /// payload). Retrying the same bytes cannot help, so the cursor advances
    /// past it rather than wedging every later record behind it — the refusal
    /// is reported, and the local journal remains the complete record.</summary>
    Permanent,
}

internal sealed record AuditDeliveryResult(
    AuditDeliveryDisposition Disposition,
    string? DetailCode)
{
    internal static AuditDeliveryResult Delivered { get; } =
        new(AuditDeliveryDisposition.Delivered, null);

    internal static AuditDeliveryResult Retryable(string detailCode) =>
        new(AuditDeliveryDisposition.Retryable, detailCode);

    internal static AuditDeliveryResult Permanent(string detailCode) =>
        new(AuditDeliveryDisposition.Permanent, detailCode);
}

/// <summary>
/// One destination adapter: endpoint plus credential, delivering batches of
/// canonical audit JSONL records at least once. Delivery is asynchronous and
/// NEVER gates execution (contract rule 2) — a destination that is down means
/// the spool catches up later, not that sessions stop.
/// </summary>
internal interface IAuditDestination : IDisposable
{
    string Describe();

    Task<AuditDeliveryResult> DeliverAsync(
        IReadOnlyList<string> records,
        CancellationToken cancellationToken);
}
