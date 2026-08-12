# Audit, export, and receiver contract

## Current runtime contract (audit-restoration, 2026-08)

Auditing is base-level and non-bypassable. Every `PtkMcpServer` boot opens a
mandatory, fail-closed local journal before serving any tool call:

- **The local journal is the ONLY execution gate.** A healthy audit root
  means every invoke is journaled durably before it is served. An unwritable
  or refused root keeps the transport up but refuses every invoke fail-closed
  (`[operation not started]`); admission retries, so a repaired root heals
  without a restart. `ptk_state` reports real audit health
  (`audit: healthy mode=local-only` and export health when configured).
- **The audit root** defaults to `~/.ptk/audit` and is overridden with
  `PTK_AUDIT_ROOT`. Protected-path admission refuses symlinked
  components, foreign ownership, and over-permissive modes; corrupt host
  identity or boot-lineage artifacts are quarantined under
  `<root>/quarantine/` with original bytes preserved, and the service
  continues on fresh identity.
- **Export never gates execution.** The optional export leg drains the
  journal asynchronously behind a durable per-boot cursor and delivers
  at-least-once. It is configured by `export.json` in the audit root
  (written safely from the web UI's settings page through the loader's own
  validation) or by `PTK_AUDIT_EXPORT_KIND` / `PTK_AUDIT_EXPORT_ENDPOINT` /
  `PTK_AUDIT_EXPORT_TOKEN`. One contract, thin adapters: `splunk_hec`, or
  `otlp_http` (any OTLP/HTTP collector — including the standalone
  `PtkSiemReceiver` below, reached identically). Plaintext HTTP is accepted
  only for loopback endpoints; credentials never reach the journal, logs, or
  `ptk_state`. Delivery-loss detection rests on per-boot contiguous
  sequences plus boot lineage; proven loss reports `EXPORT_GAPS`, suspicion
  reports `unverified_boot_boundaries` — never conflated.
- **The loopback web UI** (default port 8317, `PTK_AUDIT_UI_PORT`,
  `PTK_AUDIT_UI_DISABLED=1` to disable) reads the journal directly: log
  view, quarantine evidence, audit + export health, and the settings page.
  Auth is a bearer token minted per bind into an owner-only `ui-token` file
  in the audit root, plus loopback/Host pinning. One UI per root;
  supervisors race for the port and losers stand by.
- **The alert webhook** (optional `alert_webhook` in `export.json` or
  `PTK_AUDIT_ALERT_WEBHOOK`, same HTTPS-or-loopback rule) posts
  edge-triggered operator alerts; an undeliverable webhook keeps the edge
  pending and can never gate anything.

The release gate proves the packaged bits journal
(`server/direct-product-proof.ps1`), and the handshake proves it for a
checkout.

This repository also contains:

- legacy local journal and exact-script evidence types needed to read and
  disposition existing stores;
- the separate `PtkAuditAdmin` executable for that legacy administration; and
- the standalone `PtkSiemReceiver` — the fallback SIEM destination for
  environments without one (see `siem/PtkSiemReceiver/README.md`).

`PtkAuditAdmin` is excluded from the runtime package. `PtkSiemReceiver` is a
separately installed application, not a service enabled by `PtkMcpServer`.

## Legacy local evidence administration

Legacy stores contain strict `ptk.audit/1` or `ptk.audit/2` core JSONL records
plus owner-only exact-script evidence. Core records reference script evidence
by opaque ID and SHA-256 digest; the exact script bytes are held separately and
may contain credentials, tokens, or customer data.

Point `PtkAuditAdmin` at an existing legacy root with `PTK_AUDIT_ROOT`. Keep the
target producer stopped while administering its store.

```text
PtkAuditAdmin evidence read --id <evidence-uuid>
PtkAuditAdmin evidence export --id <evidence-uuid> --output <absolute-path>
```

`read` writes the exact sensitive bytes to stdout. `export` exclusively creates
a new file in an already owner-only protected directory and refuses an existing
path, symlink, or reparse point. Protect stdout, shell history, redirections,
backups, and exported files as sensitive evidence.

Evidence-access failures use closed detail codes:

| Detail code | Meaning |
| --- | --- |
| `evidence.id_invalid` | The requested evidence ID was not canonical UUIDv4. |
| `evidence.path_invalid` | The export destination was not a valid absolute path. |
| `evidence.absent` | The exact protected evidence object was absent. |
| `evidence.storage_failed` | Evidence or destination storage failed without a narrower classification. |
| `evidence.control_invalid` | Evidence control metadata or protected identity was invalid. |
| `evidence.destination_refused` | OS protection or access control refused the destination. |
| `evidence.destination_exists` | The exclusive export destination already existed. |
| `operation.failed_before_disclosure` | A read failed before destination writing began. |
| `operation.disclosure_unknown` | A destination write began but did not return. |
| `operation.flush_failed_after_disclosure` | The complete write returned but destination flush failed. |
| `audit.outcome_failed_after_disclosure` | Bytes were written and flushed before the legacy terminal record failed. |
| `audit.outcome_failed_after_publish` | A protected export was published before the legacy terminal record failed. |

These stores provide legacy evidence, not hostile same-user isolation. Any
process allowed to execute arbitrary commands as the store owner can also
invoke an accessible administration binary. Use a separate operator identity
or OS application-control boundary when stronger separation is required.

## Legacy checkpoint disposition

A retained legacy exporter can leave one permanent partial/data/protocol block.
With its producer stopped, an operator can either attest a separately verified
durable receipt or explicitly acknowledge an evidence gap:

```text
PtkAuditAdmin disposition --boot-id <uuid> --event-id <uuid> \
  --verified-receipt-digest <lowercase-sha256>

PtkAuditAdmin disposition --boot-id <uuid> --event-id <uuid> \
  --acknowledged-gap-reason <machine-code>
```

The command takes the target boot's exclusive checkpoint lease, resolves the
exact blocked record, persists an idempotent proof-bound disposition intent,
and only then advances that checkpoint. The receipt digest is an operator
attestation; `PtkAuditAdmin` does not query or independently verify a SIEM.
Configuration or authentication failures require correction rather than
disposition.

The decision-log producer evidence at `.agents/decisions.md:312` is known stale
under the existing owner hold. Do not use it to restore the retired producer,
and do not edit that held record as part of runtime documentation maintenance.

## Standalone receiver wire and acknowledgment contract

`siem/PtkSiemReceiver` retains the binary-compatible OTLP logs subset in
`siem/PtkSiemReceiver/Protos/audit_otlp.proto`. Its ingest endpoint is
`POST /v1/logs` over TLS 1.2 or 1.3. Client authentication is mTLS by
default; with an `ingest.token` configured (audit-restoration R3c) a client
may instead present `Authorization: Bearer` — a certificate that IS presented
is still validated. Two encodings are accepted: `application/x-protobuf`
(single record per request) and `application/json` (the generic-collector
OTLP/HTTP JSON shape PTK's own exporter emits, batched; each record's JSONL
body passes the same validation core, and batch responses aggregate — first
transient stops the pass, any permanent yields `400` so the producer isolates
record-by-record).

The response contract is:

| Result | HTTP response |
| --- | --- |
| Durable acceptance or an identical already-committed duplicate | `200` with a serialized empty `ExportLogsServiceResponse`; `partial_success` is unset. |
| Permanent validation or protocol refusal | `400` with protobuf `google.rpc.Status`. |
| Transient storage failure, backpressure, or saturated admission | `503` with protobuf `google.rpc.Status` and `Retry-After: 1`. |

Success is written only after the receiver's commit operation reports
acceptance. Delivery is therefore at least once: a client may retry after a
lost response, and the receiver must accept an identical duplicate while
rejecting the same event identity with different content.

The receiver strictly validates the retained audit envelope and projected
attributes, bounds request bodies and concurrent admission, and persists raw
request bytes, event/quarantine state, and receiver custody records in SQLite.
Its deployment status and storage warning remain in
[`siem/PtkSiemReceiver/README.md`](../siem/PtkSiemReceiver/README.md). This
receiver contract is retained for compatibility and future migration work; it
is what the current PTK runtime's export leg speaks when pointed at the
receiver (`otlp_http` destination, JSON encoding), and the
producer-to-receiver conformance suite pins the two ends against shared
producer-owned golden corpora (mini-SIEM S4).
