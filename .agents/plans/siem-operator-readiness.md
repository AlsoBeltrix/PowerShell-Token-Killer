# Plan: SIEM operator readiness and attributable activity audit

## Status and authority

**DRAFT — owner-directed planning work, 2026-08-13. No implementation is
approved.** This plan follows a published-artifact acceptance run that proved
the receiver backend works but the installed product does not provide a usable
operator workflow. Decisions A-D below remain owner gates. Two owner-authorized,
unprimed Claude Fable 5 `openreview` attempts over the committed plan produced
no verdict. The first failed in the harness before model output. After the owner
explicitly authorized one fresh attempt, Anthropic's cyber safeguard refused it
before any repository read or git command. Per the owner's expensive-review
rule, no further Fable call was made. Canonical latest record:
`.agents/review/siem-operator-readiness-fable5-r2-refused.md`.

This plan supersedes any interpretation of “mini-SIEM S1-S8 complete” as an
operator-readiness or release-readiness claim. The completed work in
`.agents/plans/mini-siem-implementation.md` remains valid evidence for its
backend properties: authenticated ingest, durable-before-ack storage, chain and
gap handling, alerts, retention, custody, and packaging. It is not evidence
that an unaffiliated operator can install the receiver, connect PTK, or answer
who did what.

## Goal

Ship one supported path from installation to investigation that lets an
operator:

1. install PTK and its matching receiver from published artifacts;
2. configure a safe local evaluation without inventing certificates, tokens,
   paths, or process-management commands;
3. connect a PTK producer and prove delivery;
4. see one activity row per PTK operation rather than raw lifecycle noise;
5. identify the client, any supplied agent/model identity, working context,
   submitted operation, and terminal result without reading JSONL or SQLite;
6. open the underlying evidence and chain context under an explicit sensitive
   data policy;
7. investigate and disposition alerts, gaps, and quarantine records; and
8. follow and pass one tested integration with an independently maintained
   SIEM.

The UI must state unavailable facts as unavailable. It must never turn a
client-asserted label into authenticated identity or imply that PTK knows a
model when the MCP client did not supply one.

## Evidence basis

The durable host transcript belongs in `.agents/machines.md` under the
2026-08-13 published-artifact acceptance entry. The implementation must begin
by reproducing these observations at the then-current head:

- `scripts/install.ps1` installs PTK but has no receiver installation or setup
  path. The receiver is a second release archive.
- The installed PTK local page on port 8317 exposes health plus raw event time,
  type, session, and outcome. It has no activity correlation, drill-down,
  actor, command, duration, search, or investigation workflow.
- The receiver dashboard exposes raw event time, type, boot, sequence, session,
  and outcome. Rows are not links. Actor fields and the existing detail API are
  not presented.
- Core `ptk.audit/2` records already carry `actor.client_name`,
  `actor.client_version`, `actor.client_session_id`, and
  `actor.attribution_strength`. `AuditCallFilter` sources them from the MCP
  initialize handshake. These are client assertions, not model authentication.
- PTK stores exact submitted script bytes in the protected local evidence
  store. Exported records carry an evidence ID and digest but not the script
  bytes, so a remote receiver or external SIEM cannot show the command.
- The MCP initialize handshake supplies client name and version, not the
  model/provider/agent/task responsible for each call. PTK cannot infer those
  fields honestly.
- The released package verifier compares a package's informational source SHA
  with the current checkout SHA, so a genuine `0.3.0-rc.1+0c8ed87` package is
  rejected when verified from later `master`.
- On macOS, a released PTK producer did not trust the disposable receiver CA
  through `SSL_CERT_FILE`. The prior S7 smoke and the 2026-08-13 acceptance both
  required a loopback forwarding process that validated receiver TLS. No
  supported setup command creates that path.
- In the 2026-08-13 acceptance, the published receiver and PTK archives matched
  their GitHub SHA-256 digests. The published producer executed marker
  `PTK-LIVE-SIEM-PROOF-20260813`; after the TLS-validating loopback workaround,
  the receiver stored `server.started`, `call.accepted`, and `call.completed`,
  attributed the call to client `ptk-live-siem-proof`, advanced the producer
  cursor through sequence 3, and reported healthy custody. The dashboard still
  did not show the client or command.

## Product truth and terminology

Use these terms consistently in code, UI, documentation, and release records:

- **Local audit journal:** mandatory, fail-closed PTK tool-call evidence stored
  on the producer. The local web page is a viewer, not a SIEM.
- **PTK receiver:** optional remote or local-evaluation destination providing
  durable storage, query, alerts, gaps, quarantine, and custody verification.
- **External SIEM:** a separately maintained product receiving PTK audit events
  through a documented adapter such as Splunk HEC or OTLP/HTTP.
- **Activity:** the operator-facing correlation of one admitted PTK operation
  and its terminal record, keyed by `correlation.call_id`. Raw events remain
  available as evidence but are not the primary investigation unit.
- **Client identity:** MCP client name/version/session captured from initialize;
  client-asserted unless a future transport provides authentication.
- **Agent/model identity:** optional per-call context supplied by the initiating
  client under the attribution contract. Absence is displayed as “not supplied
  by client.”
- **Command evidence:** exact submitted bytes. It is sensitive and distinct
  from a command digest, tool/action metadata, or a human-readable summary.
- **Anchored deployment:** receiver custody under a separately administered
  principal/host. A loopback evaluation remains explicitly non-anchored.

## Operator-facing activity contract

Both local and receiver APIs expose the same versioned activity projection.
The projection is derived from immutable audit events; it does not replace or
rewrite them.

Required fields:

```text
activity_id                 correlation.call_id
admitted_event_id
terminal_event_id | null
started_utc
finished_utc | null
state                       accepted | completed | failed | canceled |
                            timed_out | outcome_unknown | not_started
client.name | null
client.version | null
client.session_id | null
client.attribution_strength transport_only | client_asserted | authenticated
agent.name | null
model.provider | null
model.name | null
attribution.source          transport | client | operator_configuration | null
attribution.strength        transport_only | client_asserted | authenticated
session.name
session.generation
context.requested_cwd | null
context.effective_cwd | null
context.repository | null
request.tool
request.action
request.route | null
request.timeout_ms | null
command.evidence_id | null
command.sha256 | null
command.availability        local | receiver | external | not_collected |
                            not_exported | retained_then_purged
command.preview | null
outcome.exit_code | null
outcome.duration_ms | null
outcome.bytes_returned | null
outcome.detail_code | null
chain.boot_id
chain.first_sequence
chain.last_sequence
chain.status
```

The list response includes bounded non-secret fields and a safely encoded
preview only when the selected evidence policy permits it. Exact script bytes
are returned only by an explicit detail/evidence endpoint. Every response
labels the attribution strength and evidence availability; blank UI cells are
not allowed for these facts.

Activities with no terminal record remain visibly in progress or incomplete.
Lifecycle, evidence-retention, disposition, and server events are available in
a separate system-events view. They must not be interleaved with activity rows
as if each were a user command.

## Owner decisions required before implementation

The plan records recommendations, not approvals. Ask and record each decision
separately after the independent review.

### Decision A — exact command evidence at remote destinations

Choose whether receiver/external-SIEM setup exports exact submitted script
bytes. The metadata-only mode can identify client/tool/outcome but cannot answer
what command ran. Full evidence can contain credentials, tokens, customer data,
and arbitrary secrets.

**Recommendation:** support two explicit policies: `metadata` and
`full_command`. Make the setup flow require an affirmative selection, recommend
`full_command` for a dedicated protected receiver, and default unattended setup
to `metadata`. Never claim command visibility under `metadata`. Full-command
records use a separately typed evidence envelope with digest, length, retention
class, producer event reference, and destination acknowledgment; do not add raw
script text to every core event or searchable list response.

### Decision B — model/agent attribution source

PTK currently receives no model identity. A static server label cannot reliably
describe clients that switch models, and a model-authored tool argument can lie.

**Recommendation:** define an optional, namespaced per-call MCP metadata
extension supplied by the initiating client and label it `client_asserted`.
Continue recording initialize client identity independently. Do not infer model
from executable names, process inspection, prompts, or session names. Provide a
documented operator-configured source label only for grouping a dedicated MCP
registration, label it `operator_configuration`, and never present either form
as authenticated. If Codex or Claude cannot send per-call metadata, their UI
records must say “model not supplied by client” until those integrations exist.

### Decision C — supported first-run deployment

The receiver's production security model requires a separately administered
identity, while the immediate evaluation need is same-user loopback.

**Recommendation:** ship both profiles without conflating them:

- `local-evaluation`: same-user, loopback only, explicitly non-anchored; one
  setup command creates owner-only roots/tokens, starts or prints the exact
  foreground command, writes validated PTK export settings, performs a delivery
  test, and opens the dashboard. Permit token-authenticated plaintext ingest
  only when the receiver bind and producer endpoint are both loopback, matching
  PTK's existing loopback-only plaintext rule. This removes custom trust-store
  mutations and the undocumented forwarder.
- `anchored`: TLS, dedicated service identity, separate host/equivalent
  boundary, witness and anchor required; the setup tool validates provided
  paths/material and emits exact OS service instructions. It must refuse to
  label a same-user loopback installation anchored.

### Decision D — external SIEM acceptance target

**Recommendation:** use Splunk Enterprise/HEC for the first witnessed external
integration because PTK already ships a `splunk_hec` adapter. Gate release on a
real Splunk instance accepting a recognizable published-artifact PTK event and
an operator query returning client, command availability, and outcome. Retain a
generic OTLP/HTTP guide, but do not call protocol-shape tests a real-SIEM proof.

## Implementation slices

Each slice lands as one or more focused commits with its tests and durable
records. No later slice may weaken backend durability/custody gates from the
original mini-SIEM plan.

### S0 — truth reset and executable acceptance specification

- Replace “mini-SIEM complete” product wording with the backend/operator split
  in `.agents/state.md`, user documentation, and release-readiness records.
- Add a published-artifact acceptance specification that fails at the current
  operator-visible gaps. It installs into a fresh isolated home and never uses
  checkout-built product binaries.
- Fix package verification so expected source SHA comes from the release
  manifest/tag supplied to the verifier, not the current checkout. Prove the
  current verifier fails against an authentic older package before the fix.
- Record the release gate: backend suite success cannot close operator
  readiness.

Exit evidence: the specification names every required visible field and fails
against `0.3.0-rc.1` for the intended reasons.

### S1 — attribution and execution-context contract

- Extend the audit schema compatibly for optional agent/model attribution and
  its source/strength. Preserve v1/v2 readers and exact historical field sets.
- Investigate the MCP SDK's request `_meta` path first. If it cannot carry
  per-call metadata through the call filter, stop and return Decision B to the
  owner; do not silently substitute tool arguments or process heuristics.
- Capture effective execution working directory at the dispatch boundary.
  Derive repository identity only as a bounded path/root value; do not invoke
  arbitrary repository hooks or include remote credentials.
- Keep initialize client identity and per-call model metadata separate.
- Add schema, serialization, exporter, receiver-conformance, spoofing-label,
  absence-label, and backward-compatibility tests.

Exit evidence: a supplied model appears as `client_asserted`; an unsupplied
model is explicitly absent; no test can promote either to authenticated.

### S2 — evidence export and destination retention

This slice is gated on Decision A.

- Introduce a versioned command-evidence envelope keyed to evidence ID, digest,
  byte count, producer boot/event/call IDs, content policy, and retention class.
- Preserve core event ordering and at-least-once behavior. Destination
  acknowledgment advances evidence delivery only after durable storage. Event
  and evidence cursors must survive either arrival order and restart.
- Receiver storage separates exact command bytes from indexed activity fields.
  Exact bytes are never placed in alert detail, logs, URL parameters, table
  rows, or unauthenticated dashboard HTML.
- Metadata mode exports digest/availability only. Full-command mode exports
  exact bytes over the authenticated destination and applies receiver
  retention/tombstone/custody semantics.
- Splunk mapping uses an explicit sensitive field/event type and documents its
  indexing/retention consequences.
- Add lost-response replay, duplicate, mismatch, event-before-evidence,
  evidence-before-event, disk-full, purge, backup/restore, and custody tests.

Exit evidence: a published producer's marker can be revealed from a protected
receiver in full mode and is accurately reported `not_exported` in metadata
mode.

### S3 — local activity viewer

- Replace the raw-record-first page with an activity list derived from the
  journal. Retain health and a separate raw/system-evidence view.
- Show client, supplied model/agent or explicit absence, tool/action, session,
  effective context, state, duration, exit code, and command availability.
- Make rows keyboard-accessible links to a detail page joining accepted and
  terminal events, command evidence, correlation IDs, and chain context.
- Require an explicit reveal action for exact script bytes and warn that they
  may contain secrets. Do not put bearer tokens or evidence in URLs; use an
  operator-token prompt/session header pattern.
- Add time, client, model, session, tool, state, and free-text digest/ID filters
  within bounded journal-reading limits. State the retained search window.

Exit evidence: the published-artifact acceptance marker is findable and its
client, command, and outcome are visible without hovering or reading JSON.

### S4 — receiver activity API and dashboard

- Add `/api/activities` and `/api/activities/{activityId}` projections over
  stored immutable events, with bounded filters and stable pagination.
- Keep `/api/events` for evidence/debug compatibility. Do not require clients
  to correlate call pairs themselves.
- Make dashboard activity rows clickable and display the complete activity
  contract. Link alert/gap/quarantine subjects to relevant activity/event
  detail.
- Add human-readable health summaries for delivery, chain integrity, custody,
  evidence policy, retention, and anchor status. Raw JSON remains available
  behind disclosure controls.
- Add alert acknowledge/close and gap disposition UI with actor/time/result,
  using the existing authenticated transition APIs.
- Add accessibility, escaping/XSS, authorization, pagination, large-evidence,
  and explicit-missing-attribution tests.

Exit evidence: an operator can answer “which client did what, where, and with
what result?” from the dashboard. When model or command bytes are absent, the
reason is shown rather than hidden.

### S5 — install, local setup, connection test, and lifecycle

This slice is gated on Decision C.

- Extend `scripts/install.ps1` with an explicit receiver component that fetches
  the same version/RID receiver archive as PTK, verifies `SHA256SUMS`, and
  installs it under a version-coherent receiver root. No implicit receiver
  installation or release mutation.
- Ship a supported `ptk-siem` administration entry point with at least:
  `setup-local`, `run`, `status`, `doctor`, `connect`, `open`, and
  `uninstall-local`. Commands are idempotent or fail with exact recovery text.
- `setup-local` creates owner-only paths, distinct random ingest/operator
  tokens, bounded retention, witness directory, explicit non-anchored label,
  and loopback-only endpoints. It performs no trust-store mutation.
- `connect` writes validated producer export configuration transactionally and
  explains that settings activate at the next PTK start. `doctor` validates
  receiver health/auth, submits or observes a named synthetic/no-op activity,
  waits for the producer cursor, queries it back, and reports each boundary.
- Use OS-native service templates for anchored deployment. Do not build a
  general process/job manager. The local evaluation command may run foreground
  and print one exact second-terminal command if portable background lifecycle
  cannot be made trustworthy.
- Uninstall removes only manifest-owned receiver files/configuration and never
  deletes evidence/database roots without a separately named destructive
  option and confirmation.

Exit evidence: from a clean supported OS, documented commands install matching
published artifacts, create a local evaluation, connect a producer, and open a
populated dashboard without hand-writing JSON, certificates, or forwarding
code.

### S6 — real external SIEM integration

This slice is gated on Decision D and, for exact command search, Decision A.

- Publish a Splunk guide covering HEC creation, index and sourcetype, TLS,
  credential storage, PTK configuration, field extraction, retention, and
  searches for client/model availability/tool/outcome/evidence status.
- Provide a checked, versioned Splunk field mapping and sample dashboard/search
  definitions. Unknown fields remain preserved rather than dropped.
- Add a manual release-gate harness against a real, version-pinned Splunk
  instance. It uses published PTK artifacts, performs a recognizable call,
  waits for cursor acceptance, then queries Splunk and proves the expected
  fields and evidence policy.
- Keep protocol fakes in ordinary CI for determinism, but label them adapter
  conformance—not external-SIEM acceptance.

Exit evidence: the release record names the Splunk version, PTK artifact SHA,
query, returned event ID/call ID, and evidence policy.

### S7 — published-artifact operator acceptance and corrective release

- Run the complete path on macOS arm64, Windows x64, Linux x64, and packaging
  smoke on the remaining published RIDs. Host-specific gaps remain explicit.
- Acceptance uses a fresh home and only archived artifacts plus public setup
  commands. It may not read source-tree test fixtures, call internal APIs, edit
  generated configuration, or introduce an undocumented proxy.
- The witnessed scenario installs, sets up local evaluation, connects PTK,
  executes a recognizable successful command and a failing command, finds both
  activities, reveals or explains command evidence, displays attribution
  strength, investigates an alert, verifies zero unexplained gaps/quarantine,
  restarts both products, and finds the records again.
- Run the real-Splunk gate and all existing producer/receiver durability,
  custody, security, compatibility, packaging, and dependency checks.
- Publish only after the owner explicitly approves the version/tag/release.
  Release notes state whether model attribution depends on client support and
  which evidence policy is active by default.

Exit evidence: the owner can reproduce the primary workflow from public
instructions. `.agents/state.md` may say “operator-ready” only after this slice
and the owner release decision, never merely because unit/integration counts
are green.

## Verification

Every behavior-changing slice runs the repo entry points in
`.agents/repo-guidance.md`, plus focused suites for its surface. New tests must
be proven to bite by reverting or disabling the behavior, observing the named
failure, restoring, and observing success.

Mandatory focused gates include:

- historical audit-schema and producer-owned OTLP/Splunk golden compatibility;
- activity correlation with missing, duplicate, terminal-late, and malformed
  pairs;
- actor/model absence and spoofing-strength labeling;
- exact evidence policy and secret non-disclosure in lists/logs/URLs;
- receiver durable-before-ack and custody barriers for evidence as well as core
  events;
- installer version/RID/checksum coherence and older-release verification;
- loopback-only plaintext refusal on any non-loopback bind/endpoint;
- local and receiver UI escaping, auth, accessibility, and detail navigation;
- published-artifact local-evaluation acceptance; and
- real Splunk acceptance before corrective release.

Docs-only planning/review commits require `git diff --check`. Implementation
verification results and mutation transcripts belong in `.agents/machines.md`
or `.agents/review/`, with stable pointers from `.agents/state.md`.

## Non-goals

- Replacing the local fail-closed journal with the receiver or external SIEM.
- Claiming hostile same-user isolation for local evaluation.
- Capturing prompts, reasoning, chat transcripts, or output contents by
  default.
- Inferring model identity from process names, executable paths, session names,
  or command text.
- Building a general orchestration/job manager for receiver lifecycle.
- Supporting every SIEM vendor in the first corrective release.
- Weakening TLS, path protection, retention, custody, or durable-before-ack
  behavior for anchored deployments.
- Calling a receiver protocol test or dashboard HTTP 200 an operator acceptance
  test.

## File ownership map

- Current-state truth and gates: `.agents/state.md`,
  `.agents/repo-guidance.md`, `.agents/machines.md`
- This plan: `.agents/plans/siem-operator-readiness.md`
- Prior backend plan: `.agents/plans/mini-siem-implementation.md`
- Producer schema/capture/export: `server/PtkMcpServer/Audit/`,
  `server/PtkMcpServer.Tests/`
- Local viewer: `server/PtkMcpServer/Audit/Web/AuditWebUiService.cs`
- Receiver ingest/store/query/UI: `siem/PtkSiemReceiver/`
- Receiver tests: `siem/PtkSiemReceiver.Tests/`
- Installer and transaction helpers: `scripts/install.ps1`,
  `scripts/ptk_install_transaction.psm1`
- Package builder/verifier: `siem/build-package.ps1`,
  `siem/verify-package.ps1`, `.github/workflows/release.yml`
- Operator documentation: `README.md`, `server/README.md`,
  `siem/PtkSiemReceiver/README.md`, future `docs/integrations/`
