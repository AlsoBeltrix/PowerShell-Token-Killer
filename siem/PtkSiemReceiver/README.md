# PtkSiemReceiver

Mini-SIEM OTLP/HTTP receiver for anchored audit export. Accepts
`/v1/logs` over mTLS, enforces endpoint-owned request custody
(bounded reads, commit/quarantine semantics), and persists events,
quarantine, and custody records to a SQLite ingest store.

## Retention

`RetentionMaxAgeDays` and `RetentionMaxTotalBytes` are enforced by a
background sweep (`Storage/RetentionService.cs`, every 15 minutes and
at startup) — rbc-11, fixed 2026-08-10 in the audit-restoration R3
slice; the earlier "parsed but never enforced, do not deploy" warning
no longer applies.

What a sweep removes, and what it never touches:

- **Events and quarantine attempts** age out past `RetentionMaxAgeDays`
  and are trimmed oldest-first until the database fits
  `RetentionMaxTotalBytes` (reclaiming pages, so a size bound actually
  converges).
- **Custody receipts are never removed.** The custody ledger is the
  append-only witness of what this receiver accepted; deleting it would
  destroy the evidence retention exists to protect.
- **Chain heads are never removed**, so a later record from the same
  supervisor boot still validates against its predecessor's hash.

With neither bound configured the store grows until the operator bounds
it — set at least one for unattended ingest.
