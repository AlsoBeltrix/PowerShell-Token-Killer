# PtkSiemReceiver

Mini-SIEM OTLP/HTTP receiver: PTK's fallback audit destination for
environments without an external SIEM. Accepts `/v1/logs`, enforces
endpoint-owned request custody (bounded reads, commit/quarantine
semantics), and persists events, quarantine, and custody records to a
SQLite ingest store.

## Ingest authentication and encodings

Two client authentication modes on the one ingest port:

- **mTLS** (always available): the TLS handshake requires a client
  certificate chaining to `clientCaBundlePaths`. Custody receipts record
  the certificate's SHA-256.
- **Bearer token** (`ingest.token` in the configuration, optional): when
  configured, a client without a certificate may authenticate each
  request with `Authorization: Bearer <token>`. This is how PTK's own
  exporter connects — the same endpoint-plus-token configuration it uses
  for Splunk, Sentinel, or any OTLP collector, with no receiver-specific
  pairing. Custody receipts record the token's SHA-256 (the credential's
  name, never the credential), so configure a high-entropy token; the
  loader refuses tokens shorter than 16 characters. Without
  `ingest.token`, certificate-less connections are refused at the
  handshake exactly as before.

Two request encodings, orthogonal to authentication:

- **`application/x-protobuf`**: one strictly projected log record per
  request (the original anchored-export wire contract).
- **`application/json`** (audit-restoration R3c): standard OTLP/HTTP
  JSON, batched — the shape PTK's exporter emits for every destination.
  The envelope is transport; each record's JSONL body is the custody
  evidence and passes the same validation as the protobuf path,
  including event-hash recomputation. Records commit or quarantine
  individually: replayed or regrouped batches are idempotent by exact
  per-record bytes, and one poison record refuses one record.

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
