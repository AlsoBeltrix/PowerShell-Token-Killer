# Audit, export, and receiver contract

## Operator-readiness status

Current source adds truthful per-call attribution, execution context, and
full-fidelity command/response/output evidence to the exported stream. The
standalone receiver indexes and exposes that evidence through authenticated
operator APIs and an attributable activity drill-down. It is separately deployed;
PTK never installs or selects it.

The local audit journal is mandatory admission/replay/delivery
infrastructure, not a SIEM destination or investigation dashboard. PTK
exports to one explicitly selected SIEM destination by default; additional
destinations require deliberate opt-in and each receives the full stream with
independent delivery accounting. The mini-SIEM is a separate deployment for
sites without a full SIEM and is never installed or selected automatically.

Published `0.3.0-rc.1` predates the full-evidence contract described below. It
does not provide destination-side exact command and complete response evidence.
Current source remains operator-not-ready until the later investigation and
acceptance slices land; passing backend suites alone does not close that gate.

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
  `PTK_AUDIT_ROOT`. It holds the journal AND owner-only exact-script
  evidence: every invoke persists the exact submitted script bytes, which
  can contain passwords, tokens, or customer data — protect the root, and
  any backup or copy of it, as sensitive data. This is the CURRENT
  contract, not only a legacy-store property; the legacy administration
  section below applies the same care to pre-restoration stores. Protected-path admission refuses symlinked
  components, foreign ownership, and over-permissive modes; corrupt host
  identity or boot-lineage artifacts are quarantined under
  `<root>/quarantine/` with original bytes preserved, and the service
  continues on fresh identity.
- **Export never gates execution.** The destination coordinator drains the
  journal asynchronously. Protected `destinations.json` is the versioned
  authority. Each enabled destination has its own cursor, gap ledger, lease,
  retry state, pending counts, and acknowledgment time; one acknowledgment
  never advances another destination. Existing `export.json` or
  `PTK_AUDIT_EXPORT_*` configuration migrates once to one stable
  legacy-inclusive destination, not a second delivery path. Adapters are
  `splunk_hec` and `otlp_http`, including a separately deployed
  `PtkSiemReceiver`. Plaintext HTTP is accepted only for loopback endpoints;
  credentials never reach the journal, logs, status JSON, HTML, or
`ptk_state`. Each audit record's `producer.version` is the same exact
`<version>+<short-commit>.build.<build-identity>` reported by `ptk_state` and
recorded in the package's `BUILD-PROVENANCE.json`. Proven loss reports
`EXPORT_GAPS`; suspicion reports
  `unverified_boot_boundaries` — never conflated.
- **The loopback producer UI** (default port 8317, `PTK_AUDIT_UI_PORT`,
  `PTK_AUDIT_UI_DISABLED=1` to disable) is configuration and delivery status
  only. It shows redacted destination identity, each independent delivery and
  backfill state, and local journal capacity. It does not expose event lists,
  quarantine, commands, output, errors, or evidence. Auth is a bearer token
  minted per bind into an owner-only `ui-token` file in the audit root, plus
  loopback/Host pinning. One UI per root; supervisors race for the port and
  losers stand by.
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

## Destination configuration and delivery operations

Open `http://127.0.0.1:8317/` and use the bearer token in
`<audit-root>/ui-token`, or call the same loopback API directly:

```text
GET  /api/status
POST /api/destinations
PUT  /api/destinations/<destination-id>
POST /api/destinations/<destination-id>/enable
POST /api/destinations/<destination-id>/disable
POST /api/destinations/<destination-id>/remove
POST /api/destinations/<destination-id>/abandon
POST /api/destinations/<destination-id>/backfill
```

Add and update bodies contain `operator_label`, `kind`, `endpoint`, `credential`,
and optional `server_certificate_sha256`. The exact leaf-certificate pin is
destination-local trust used by both the preflight probe and delivery; it does not
change a user or machine trust store. Adding a second destination also requires
`confirm_sensitive_duplication: true`; PTK rejects the request before probing
credentials when that confirmation is absent. PTK validates an activated
endpoint with a non-ingesting `OPTIONS` request. A 401 or 403 is a credential
refusal; another HTTP response proves reachability but cannot prove a bearer
token when that endpoint does not authenticate `OPTIONS`.

Packaged operators do not need to write those JSON bodies. PTK ships
`scripts/ptk-audit-destination.ps1` for list/add/update/enable/disable/remove and
mini-SIEM query-back doctor workflows. A successful live destination change reports
`ptk_restart_required = false`. The mini-SIEM release archive separately ships
`manage.ps1` for checksum/version/RID-verified deployment, foreground execution,
native service definitions, status, dashboard open, upgrade, manifest-safe
uninstall, and separately confirmed evidence deletion.

`GET /api/status` returns every destination ID, label, adapter, redacted
endpoint, opaque credential reference, configuration revision, activation and
enabled state. Delivery state is independent per destination: pending core and
evidence records/bytes, oldest pending time, last scan, attempt and
acknowledgment, failures, gaps, refusals, and standby state. It also reports an
active or completed backfill and local journal/evidence capacity. It never
returns the credential, endpoint path, or forensic records.

New `ptk.audit/6` core events and their `ptk.evidence/2` envelopes carry a
sorted immutable `required_destination_ids` set captured at admission. A new
destination therefore applies prospectively and does not receive older
evidence silently. Historical delivery is a separate confirmed backfill with
an explicit half-open UTC range (`from_utc` inclusive, `to_utc` exclusive) and
`actor`; its cursor, failures, pending counts, and completion survive restart
independently of live delivery.

Ordinary disable or remove returns HTTP 409 while any obligation remains.
`abandon` requires nonempty `actor` and `reason`; it first writes a durable
owner-only `export-abandonment-<id>.json` with counts, observed event/evidence
source ranges, cursor positions, action, and custody consequence, then disables
or removes the destination. Retention takes the most conservative floor across
all enabled destinations and active backfills. A missing or unreadable required
cursor retains everything rather than guessing acknowledgment.

PTK never discovers or automatically enables a destination. A separately
deployed mini-SIEM is simply an `otlp_http` destination chosen by the operator;
it is not installed locally, used as fallback, or silently added alongside a
real SIEM.

## Per-call attribution and execution context

Current journal admission emits `ptk.audit/6` with destination obligations.
Historical attribution-only `ptk.audit/4` and full-evidence `ptk.audit/5`
records remain accepted without changing their field sets. Server and receiver
also continue accepting the exact `ptk.audit/1` and `ptk.audit/2` field sets;
host-state `ptk.audit/3` remains a separate established contract.

An MCP client that knows its active agent, model, or task/run identity can send
this optional namespaced `_meta` member on each `tools/call` request:

```json
{
  "_meta": {
    "io.github.also-beltrix.ptk/call-context/v1": {
      "agent_name": "codex",
      "model_provider": "openai",
      "model_name": "gpt-5.6-sol",
      "task_id": "task-17",
      "task_name": "SIEM attribution",
      "run_id": "run-29",
      "requested_cwd": "/operator/requested/path"
    }
  }
}
```

All members are optional bounded strings, but the namespaced object is strict:
unknown fields, wrong JSON kinds, empty values, and oversized values refuse the
call before execution. PTK labels values from this object as source `client`
and strength `client_asserted`; the client cannot promote them to
`authenticated`. A future dedicated static setting may use source
`operator_configuration`, but it likewise cannot become authenticated without
an independently authenticated binding.

The initialize handshake's client name/version/session stays in `actor` and is
not treated as the per-call agent or model. When the client omits identity,
`call_attribution` stores `not_supplied_by_client` rather than guessing from
the executable, process, prompt, session, or command. `client_context` stores
bounded task/run values and the MCP task TTL when supplied. At dispatch,
`execution_context` records the effective working directory and, when found by
walking parent paths for a `.git` marker, only the bounded repository root and
relative path. PTK does not invoke Git or read repository remotes.

The PTK registrations installed by `scripts/ptk_init.ps1` currently launch the
server but do not have a documented per-call metadata injection surface. Those
clients therefore appear with initialize client identity and explicit
per-call `not_supplied_by_client` labels unless the client itself sends the
namespace. This is an honest capability gap, not a fallback to static model
labels.

## Full-fidelity SIEM evidence

For every production MCP call, the terminal `ptk.audit/6` record carries an
`evidence_manifest`. The manifest names every retained exact artifact by
evidence ID, kind, SHA-256 digest, byte count, UTF-8 encoding, producer
event/call lineage, forensic retention class, capture state, and bounded
chunk/reassembly metadata. Current artifact kinds are:

- `submitted_command` — exact submitted command bytes;
- `caller_response` — the complete text returned to the MCP caller;
- `captured_output` — the immutable unshaped output/error/warning recovery
  artifact when the invocation produced one.

Each current manifest entry is exported as a `ptk.evidence/2` envelope carrying
the same destination obligations. Historical `ptk.audit/5` expands to
`ptk.evidence/1`. Chunks have
independent digests and an artifact-wide digest. A chunk stream starts at
sequence 1, links subsequent chunks by event hash, and can be replayed
idempotently. The exporter treats a v5 or v6 core record and all of its evidence as
one logical delivery unit: the durable journal cursor cannot advance past it
until every required record receives a successful destination acknowledgment.
A retryable response or lost response replays the unit. Missing/corrupt local
evidence reports `export.evidence_unavailable` or `export.evidence_invalid`;
permanent refusal reports `export.evidence_refused`; all hold the cursor.

The OTLP adapter sends evidence as ordinary OTLP log records without dropping
unknown fields. The Splunk HEC adapter uses explicit sourcetype
`ptk:evidence` (core records remain `ptk:audit`) and retains the complete event
body. Evidence bodies are sensitive: restrict the destination index, roles,
search access, retention, backup, and replication accordingly.

The standalone receiver validates both chunk and artifact metadata, stores the
exact envelope under the same commit/custody transaction as other events,
indexes call/source/artifact fields, and correlates received chunks against the
the core manifest. An authorized operator can use:

```text
GET /api/events?call=<call-uuid>
GET /api/events?source=<core-event-uuid>
GET /api/events?artifact=<artifact-uuid>
GET /api/events/<core-event-uuid>       # evidence_delivery complete/incomplete
GET /api/evidence/<artifact-uuid>       # exact reassembled bytes + UTF-8 text
```

All require the operator bearer token in the `Authorization` header. Exact
evidence is never accepted in a URL parameter, returned by the unauthenticated
dashboard shell, or written to process logs. Retention may purge old chunks;
the retained core then reports incomplete delivery and exact-artifact retrieval
returns `409 evidence_incomplete`, while custody/tombstone evidence remains.

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
| Missing or invalid client credential (no validated certificate, and no or wrong bearer token) | `401` with protobuf `google.rpc.Status` code 16 (UNAUTHENTICATED). PTK's exporter classifies this as retryable — an operator-fixable configuration failure, not data loss. |

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
