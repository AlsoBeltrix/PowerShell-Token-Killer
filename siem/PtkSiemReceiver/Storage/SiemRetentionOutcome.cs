namespace PtkSiemReceiver.Storage;

/// <summary>What one retention sweep removed, and the resulting database
/// size. The custody ledger is never included: it is append-only evidence
/// and outlives every retention bound.</summary>
internal sealed record SiemRetentionOutcome(
    long EventsRemoved,
    long QuarantineRemoved,
    long DatabaseBytes);
