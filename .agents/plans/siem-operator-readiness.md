# Plan: SIEM operator readiness and attributable activity audit

## Status and authority

**ACTIVE — owner-directed plan, 2026-08-13. S0 was approved and executed;
S1-S7 are not approved.** This plan follows a published-artifact acceptance run that proved
the receiver backend works but the installed product does not provide a usable
operator workflow. Decision A is settled: every possibly relevant fact and
evidence artifact PTK captures must be exposed by either supported destination
type independently; sensitive evidence is protected rather than suppressed.
Decision B is also settled: supported clients must supply per-call agent/model
identity when technically possible, with source/trust recorded and no guessing.
Decision C is settled as explicit operator-chosen destination configuration:
one destination by default, with multiple destinations available only by
deliberate opt-in. The mini-SIEM remains separately deployed and is never
silently installed or selected. Decision D remains the only owner gate. Two owner-authorized,
unprimed Claude Fable 5 `openreview` attempts over the committed plan produced
no verdict. The first failed in the harness before model output. After the owner
explicitly authorized one fresh attempt, Anthropic's cyber safeguard refused it
before any repository read or git command. Per the owner's expensive-review
rule, no further Fable call was made. Canonical latest record:
`.agents/review/siem-operator-readiness-fable5-r2-refused.md`.
An owner-requested Kimi Code 0.35.0 / `k3` / transcript-high openreview over the
same pins returned a valid `best_approach` verdict with no material changes or
candidate findings on the plan before the owner settled Decisions A-C. Its
canonical record is `.agents/review/siem-operator-readiness-kimi-r1.md`. Review
endorsement does not approve implementation or settle Decision D; it does not
review the post-review Decision A-C amendments.

This plan supersedes any interpretation of “mini-SIEM S1-S8 complete” as an
operator-readiness or release-readiness claim. The completed work in
`.agents/plans/mini-siem-implementation.md` remains valid evidence for its
backend properties: authenticated ingest, durable-before-ack storage, chain and
gap handling, alerts, retention, custody, and packaging. It is not evidence
that an unaffiliated operator can install the receiver, connect PTK, or answer
who did what.

## Goal

Ship supported workflows from installation to investigation that let an
operator:

1. install PTK from published artifacts and, only when chosen, deploy the
   matching mini-SIEM package at an explicit operator-selected location;
2. select and configure one SIEM destination by default, or explicitly opt into
   more, without hidden destinations or forced duplication;
3. connect a PTK producer and prove complete delivery independently to every
   configured destination;
4. see one activity row per PTK operation rather than raw lifecycle noise;
5. identify the client, any supplied agent/model identity, working context,
   exact submitted operation, complete captured response/output and errors, and
   terminal result without reading JSONL or SQLite;
6. open every retained raw event and evidence artifact that could help
   reconstruct the activity, with sensitive data protected by access controls
   rather than omitted;
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

- `scripts/install.ps1` installs PTK only. The mini-SIEM is a separate release
  archive with no explicit deployment workflow yet; PTK installation must
  remain separate from mini-SIEM deployment.
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

- **Local audit journal:** mandatory, fail-closed PTK source journal/spool on the
  producer for admission, replay, and delivery proof. It is not an export
  destination or SIEM and has no operator investigation dashboard.
- **PTK mini-SIEM:** an optional, separately and explicitly deployed SIEM
  destination providing durable storage, query, alerts, gaps, quarantine, and
  custody verification when a full SIEM is unavailable.
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
- **Forensic evidence:** every possibly relevant fact or evidence artifact PTK
  captures for an activity, including exact submitted command bytes, complete
  captured response/output and error evidence, raw lifecycle events,
  attribution, execution context, and custody records. It may contain secrets;
  that changes its protection and retention requirements, not whether it is
  exported or available to an authorized operator.
- **Configured destination:** a mini-SIEM or external-SIEM endpoint explicitly
  selected by the operator. One is the default; each additional destination
  requires separate opt-in. Identity and delivery state are per destination.

## Operator-facing activity contract

The mini-SIEM API/dashboard and each external-SIEM mapping/query expose the same
versioned activity projection. The producer's local journal and delivery-status
surface do not expose this investigation projection.
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
command.byte_count | null
command.availability        destination | not_observed |
                            retained_then_purged | delivery_incomplete
command.preview | null
response.evidence_id | null
response.sha256 | null
response.byte_count | null
response.availability       destination | not_observed |
                            retained_then_purged | delivery_incomplete
outcome.exit_code | null
outcome.duration_ms | null
outcome.bytes_returned | null
outcome.detail_code | null
chain.boot_id
chain.first_sequence
chain.last_sequence
chain.status
```

The list response may use bounded summaries and safely encoded previews, but
every activity links to authenticated detail/evidence endpoints exposing all
retained correlated raw events, exact command bytes, complete captured
response/output and error evidence, and custody context. Every response labels
attribution strength and evidence availability; blank UI cells are not allowed
for unavailable, purged, or incompletely delivered facts.

Activities with no terminal record remain visibly in progress or incomplete.
Lifecycle, evidence-retention, disposition, and server events are available in
a separate system-events view. They must not be interleaved with activity rows
as if each were a user command.

## Owner decisions required before implementation

Decisions A-C below are settled. Decision D is open with a verified access
constraint; product and access path require a separate owner ruling.

### Decision A — full-fidelity evidence at SIEM destinations

**SETTLED 2026-08-13:** the mini-SIEM and external SIEM are each independently
capable full-fidelity forensic destinations. Every explicitly configured
destination must receive every possibly relevant fact and evidence artifact PTK
captures, including exact command bytes and complete captured response/output
and errors. One destination is the default; the operator may deliberately opt
into more. There is no metadata-only mode. Summary
lists may remain bounded, but they must drill into the complete record.
Authentication, authorization, encrypted transport, protected storage, access
auditing, and retention protect sensitive evidence; field suppression does not.
A fact PTK never received is labeled unavailable, not inferred. Canonical
ruling: `.agents/decisions.md`.

### Decision B — model/agent attribution source

**SETTLED 2026-08-13:** define a namespaced per-call attribution contract and
require every client integration PTK claims to support to supply agent/model
identity and useful task/run context whenever the client makes them technically
available. Record exact source and trust: client values are `client_asserted`;
dedicated static configuration is `operator_configuration`; neither becomes
`authenticated` without an authenticated binding. Keep initialize client
identity separate. Never infer identity from process names, executable paths,
prompts, session names, or command text. When a client cannot or does not supply
identity, the record and UI say “not supplied by client” and preserve the known
capability/source reason. Canonical ruling: `.agents/decisions.md`.

### Decision C — supported first-run deployment

**SETTLED 2026-08-13, AMENDED:** PTK exports one full-fidelity SIEM-compliant
stream. Setup defaults to one explicit operator-chosen destination. An operator
may deliberately configure additional destinations; PTK never forces, silently
adds, or discovers one. Every configured destination receives the full stream
and has independent delivery accounting. If no full SIEM is available and
visibility is wanted, the operator explicitly deploys the mini-SIEM at a chosen
location and selects it. PTK never automatically installs or selects a local
mini-SIEM. The mandatory local audit journal remains the disclosed fail-closed
source journal/spool, not a SIEM destination or operator dashboard. Canonical
ruling: `.agents/decisions.md`.

### Decision D — external SIEM acceptance target and test access

**OPEN — verified constraint:** the owner has no access to Splunk or any other
external SIEM instance for testing. This does not select another product, remove
real-product validation, or waive the external-SIEM acceptance requirement.
The earlier Splunk recommendation remains only a candidate because PTK already
ships a `splunk_hec` adapter; it is not actionable until a lawful, authorized,
reproducible test-access path is identified and separately approved. Decision D
must settle both the first product and how PTK obtains test access without
assuming an owner-provided instance. Protocol-shape tests remain adapter
conformance and must not be called real-SIEM proof. Canonical open record:
`.agents/decisions.md`.

## Implementation slices

Each slice lands as one or more focused commits with its tests and durable
records. No later slice may weaken backend durability/custody gates from the
original mini-SIEM plan.

### S0 — truth reset and executable acceptance specification — EXECUTED 2026-08-13

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

Execution evidence: `siem/operator-readiness-acceptance.ps1` extracted the
authentic published producer and receiver archives into a fresh isolated home,
verified both release identities, and evaluated the durable live-proof record.
Eight artifact/provenance requirements passed and 23 named operator-readiness
requirements failed. `siem/test-verify-package.ps1` rejected a deliberate
checkout-coupled verifier regression, then passed after restoration. Package
verification now accepts either the release's seven-character stamped source
identity or its full commit ID and never consults checkout `HEAD`.

### S1 — attribution and execution-context contract

- Extend the audit schema compatibly for per-call agent/model attribution,
  task/run context, source/strength, and an explicit unavailable reason.
  Preserve v1/v2 readers and exact historical field sets.
- Investigate the MCP SDK's request `_meta` path first, then documented client
  integration hooks. If a client cannot carry per-call metadata through the
  call filter, record that capability gap and display identity as unavailable;
  do not silently substitute tool arguments or process heuristics.
- Capture effective execution working directory at the dispatch boundary.
  Derive repository identity only as a bounded path/root value; do not invoke
  arbitrary repository hooks or include remote credentials.
- Keep initialize client identity and per-call model metadata separate. Update
  every PTK-supported client adapter, registration template, and setup guide to
  send attribution when its client exposes it.
- Add schema, serialization, exporter, receiver-conformance, spoofing-label,
  absence-label, and backward-compatibility tests.

Exit evidence: each supported identity-capable client sends exact per-call
agent/model values and they appear with their real provenance; an incapable or
omitting client is explicitly unattributed with a reason; no test can promote
client/configuration assertions to authenticated.

### S2 — full-fidelity evidence export, destination storage, and retention

Decision A is settled; this slice implements its full-fidelity requirement.

- Inventory every operation fact and artifact PTK captures, including exact
  command bytes, caller-visible response/output, error evidence, auxiliary
  immutable output artifacts, lifecycle records, and disposition/custody
  records. Any captured item absent from any configured destination's forensic
  record is a test failure.
- Introduce a versioned typed-evidence envelope keyed to evidence ID, with
  evidence kind, digest, byte count, encoding, producer boot/event/call IDs,
  retention class, and chunk/reassembly metadata for destination size limits.
- Preserve core event ordering and at-least-once behavior. Destination
  acknowledgment advances evidence delivery only after durable storage. Event
  and evidence cursors must survive either arrival order and restart.
- Receiver storage separates large exact evidence blobs from indexed activity
  fields while preserving authenticated correlation and search. Exact evidence
  never leaks into process logs, URL parameters, unauthenticated HTML, or an
  unauthorized role, but it is always retrievable by an authorized operator.
- Every supported adapter exports the complete retained evidence. Chunking,
  replay, destination acknowledgments, and manifest digests must prove that
  large command/output evidence arrived intact; core-event success with missing
  evidence is a visible incomplete-delivery failure.
- Splunk mapping uses explicit sensitive evidence event types and documents
  indexing, role, search, and retention consequences without dropping unknown
  fields or evidence bodies.
- Add lost-response replay, duplicate, mismatch, event-before-evidence,
  evidence-before-event, disk-full, purge, backup/restore, and custody tests.

Exit evidence: in mini-SIEM-only, external-SIEM-only, and explicit-multiple
tests, an authorized operator retrieves and verifies the exact command,
complete captured result/error evidence, raw events, attribution, context, and
custody chain for a published-producer marker from every configured destination.
Removing any evidence kind fails that destination's acceptance guard.

### S3 — explicit destination configuration and per-destination delivery status

- Replace ambiguous exporter configuration with one versioned destination set.
  Each entry has a stable destination ID, type, operator label, endpoint,
  adapter, credential reference, configuration revision, activation time, and
  enabled state. Secrets never appear in status output or process logs.
- First-run setup requires one explicit destination and creates no others.
  Adding a second or later destination is a separately named opt-in action that
  names the sensitive-data duplication consequence before confirmation. PTK
  never discovers, inherits, silently adds, or automatically enables a
  destination.
- Provide transactional add, update, disable, and remove operations. Validate
  the proposed endpoint and credentials before activation. A failed change
  preserves the prior destination set. There is no implicit failover or fallback
  destination; evidence is delivered to every enabled destination.
- Give every destination independent event and evidence acknowledgment cursors,
  pending record/byte counts, oldest-pending time, last attempt, last
  acknowledgment, health, and error. Acknowledgment by one destination never
  advances or conceals another destination's delivery state.
- Persist the required destination-ID set on each event/evidence item at
  admission. Adding a destination applies prospectively and does not silently
  send historical evidence to it. Historical backfill is a separately named,
  confirmed action with an explicit source range and destination, whose progress
  and acknowledgment are independently visible.
- Do not remove or disable a destination with unacknowledged evidence through an
  ordinary change. Require a separately confirmed abandonment operation that
  records the destination, undelivered event/evidence ranges, actor, time,
  reason, and custody consequence.
- Replace the producer's raw-event web page with configuration and delivery
  status only: the explicit destination list with redacted endpoints and the
  independent state above, plus local journal capacity. It exposes no event
  list, activity query, command, output, error, or evidence drill-down.
- Keep the disclosed local journal as the fail-closed delivery/replay source.
  Failure at any destination remains visible and queued; it never activates a
  local investigation view. Apply journal retention only after every enabled
  destination has acknowledged the item or an operator has recorded explicit
  abandonment for the lagging destination.
- Add configuration migration, concurrent update, crash-boundary,
  credential-redaction, add/remove/abandon, prospective-add, explicit-backfill,
  default-one, explicit-multiple, independent-cursor,
  partial-destination-failure, no-implicit-failover, and delivery-status accuracy
  tests.

Exit evidence: an operator can identify every destination receiving PTK
evidence and independently prove whether each event/evidence item was
acknowledged, while a default setup exposes data to only the one destination the
operator selected and no producer-local surface reveals the forensic record.

### S4 — mini-SIEM activity API and dashboard

- Add `/api/activities` and `/api/activities/{activityId}` projections over
  stored immutable events, with bounded filters and stable pagination.
- Keep `/api/events` for evidence/debug compatibility. Do not require clients
  to correlate call pairs themselves.
- Make dashboard activity rows clickable and display the complete activity
  contract. Link alert/gap/quarantine subjects to relevant activity/event
  detail.
- Add human-readable health summaries for ingest, chain integrity, custody,
  full-evidence delivery completeness, retention, and anchor status. Raw JSON
  remains available behind disclosure controls.
- Add alert acknowledge/close and gap disposition UI with actor/time/result,
  using the existing authenticated transition APIs.
- Add accessibility, escaping/XSS, authorization, pagination, large-evidence,
  and explicit-missing-attribution tests.

Exit evidence: an authorized operator can answer “which client/model did what,
where, what exactly was submitted, what exactly came back, and with what
result?” from the dashboard and can descend to every retained raw event and
evidence artifact. Genuinely unobserved or retention-purged facts say why.

### S5 — explicit mini-SIEM deployment and destination connection

Decision C is settled; this slice implements separate deployment and explicit
destination selection.

- Keep `scripts/install.ps1` PTK-only. It must not fetch, install, start,
  configure, or select the mini-SIEM. Add an installer guard proving a clean PTK
  installation leaves no mini-SIEM binary, service, data root, token, endpoint,
  or destination-configuration change.
- Retain the version/RID/checksum-verified mini-SIEM archive as a separate
  product package. Add an explicit deployment entry point in that package for
  operator-chosen local or remote paths, bind/endpoints, TLS material,
  ingest/operator credentials, retention, witness, anchor, and service identity.
  Defaults must not silently turn a same-user local deployment into an anchored
  claim.
- Use OS-native service definitions and documented foreground execution. Do not
  build a general process or job manager.
- Provide mini-SIEM administration for deployment validation, run/service
  instructions, status, doctor, dashboard open, upgrade, and uninstall.
  Uninstall removes only manifest-owned program/configuration files; it never
  deletes evidence/database roots without a separately named destructive action
  and confirmation.
- Use S3's destination interface to configure PTK for the deployed mini-SIEM or
  an external SIEM. First-run selection configures one destination; each
  additional destination is a separate opt-in. Changes are transactional,
  validate authentication/TLS without trust-store mutation, and report when a
  PTK restart is required.
- `doctor` validates the chosen destination health/auth, submits or observes a
  named synthetic/no-op activity, waits for event and evidence acknowledgment,
  queries it back from each configured destination, and reports each boundary.
  It refuses to inspect or contact any destination not explicitly configured.

Exit evidence: from a clean supported OS, one documented workflow deploys the
mini-SIEM separately and selects it; a separate workflow selects an external
SIEM without installing the mini-SIEM. A third explicit workflow opts into
both and proves independent delivery. None requires hand-written JSON,
undocumented certificates, or forwarding code.

### S6 — real external SIEM integration

This slice is gated on Decision D settling both the product and an authorized,
reproducible access path. The implementation may not assume the owner will
provide an existing SIEM instance. Decision A already requires full-fidelity
evidence export.

- Publish a guide for the selected Decision D product covering its ingest
  endpoint, storage/indexing, TLS, credential storage, PTK configuration, field
  extraction, retention, and searches for client/model
  availability/tool/outcome, exact command and response evidence, raw events,
  and evidence/custody status.
- Provide a checked, versioned field mapping and sample dashboard/search
  definitions for that product. Unknown fields remain preserved rather than
  dropped.
- Add a manual release-gate harness against the real, version-pinned Decision D
  product through the approved access path. It uses published PTK artifacts,
  performs a recognizable call, waits for cursor acceptance, then queries the
  SIEM and proves the expected fields plus complete evidence bodies and
  manifests.
- Keep protocol fakes in ordinary CI for determinism, but label them adapter
  conformance—not external-SIEM acceptance.

Exit evidence: the release record names the SIEM product/version, approved
access method, PTK artifact SHA, query, returned event ID/call ID, and
digest-verified full-evidence result.

### S7 — published-artifact operator acceptance and corrective release

- Run complete paths on macOS arm64, Windows x64, and Linux x64, with packaging
  smoke on remaining published RIDs. Host-specific gaps remain explicit.
- Acceptance uses fresh homes, archived artifacts, and public deployment/setup
  commands only. It may not read source-tree fixtures, call internal APIs, edit
  generated configuration, or introduce an undocumented proxy.
- Run a default mini-SIEM-only scenario: install PTK, explicitly deploy the
  matching mini-SIEM at the chosen location, select it as the one destination,
  execute recognizable successful and failing commands, find both activities,
  retrieve exact command and complete captured response/error evidence, display
  attribution strength, investigate an alert, prove zero unexplained
  gaps/quarantine/evidence-delivery gaps, restart PTK and the mini-SIEM, and find
  the records again. Prove no external-SIEM endpoint was configured or contacted.
- Run a default external-SIEM-only scenario: install PTK without installing or
  starting the mini-SIEM, select the Decision D SIEM as the one destination,
  execute recognizable successful and failing commands, query both activities
  and every required full-fidelity evidence kind, and prove no mini-SIEM
  endpoint, data root, process, service, or received evidence exists.
- Run an explicit multiple-destination scenario: begin with one destination,
  opt into the second through the separately named action, execute recognizable
  successful and failing commands, and verify the same full-fidelity record and
  digest at both. Stop one destination and prove its independent backlog/error
  grows while the other continues acknowledging; restore it and prove replay
  closes only its backlog. Prove no third destination is contacted.
- For every scenario, record the configured-destination list, destination-side
  event IDs and evidence digests, independent producer acknowledgment cursors,
  and controlled unconfigured-endpoint witnesses proving zero requests.
- Run the external-SIEM gate and all existing producer/mini-SIEM durability,
  custody, security, compatibility, packaging, and dependency checks.
- Publish only after the owner explicitly approves the version/tag/release.
  Release notes state attribution client dependencies, the default-one and
  opt-in-multiple rule, the disclosed local replay journal, and that every
  configured supported SIEM receives the full retained forensic record.

Exit evidence: the owner can reproduce both default single-destination workflows
and the explicit multiple-destination workflow. State may say “operator-ready”
only after this slice and the owner's release decision, never merely because
unit/integration counts are green.

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
- exact command/output/error evidence round-trip, chunking, authorization, and
  non-leakage into process logs, URLs, or unauthorized responses;
- mini-SIEM durable-before-ack and custody barriers for evidence as well as core
  events;
- PTK-installer non-installation/non-configuration of the mini-SIEM;
- mini-SIEM package/deployment version, RID, checksum, upgrade, and uninstall
  coherence;
- destination-set schema migration, default-one setup, explicit add/remove,
  credential redaction, prospective destination obligations, explicit bounded
  backfill, and abandonment-with-backlog custody recording;
- independent per-destination cursor/pending/error accuracy, including one
  destination failing while another acknowledges, without local evidence
  display;
- loopback-only plaintext refusal on any non-loopback bind/endpoint;
- mini-SIEM UI escaping, auth, accessibility, and detail navigation;
- published-artifact mini-SIEM-only acceptance proving zero external-SIEM
  delivery;
- published-artifact external-SIEM-only acceptance proving no installed/running
  mini-SIEM or delivery to one; and
- published-artifact explicit multiple-destination acceptance proving complete
  delivery and independent failure/replay accounting at each destination; and
- the Decision D real-SIEM acceptance before the corrective release.

Docs-only planning/review commits require `git diff --check`. Implementation
verification results and mutation transcripts belong in `.agents/machines.md`
or `.agents/review/`, with stable pointers from `.agents/state.md`.

## Non-goals

- Replacing the local fail-closed journal with a SIEM destination, or presenting
  the journal as an investigation store.
- Automatically installing, starting, or configuring the mini-SIEM with PTK.
- Silently adding a destination, forcing multiple destinations, or treating one
  destination as implicit failover/fallback for another.
- Inventing prompts, reasoning, chat transcripts, model identity, or other
  client-only facts PTK did not receive. Any such context deliberately supplied
  to PTK is part of the full-fidelity forensic record.
- Inferring model identity from process names, executable paths, session names,
  or command text.
- Building a general orchestration/job manager for mini-SIEM lifecycle.
- Supporting every SIEM vendor in the first corrective release.
- Weakening TLS, path protection, retention, custody, or durable-before-ack
  behavior for mini-SIEM deployments.
- Calling a protocol test or dashboard HTTP 200 an operator acceptance test.

## File ownership map

- Current-state truth and gates: `.agents/state.md`,
  `.agents/repo-guidance.md`, `.agents/machines.md`
- This plan: `.agents/plans/siem-operator-readiness.md`
- Prior backend plan: `.agents/plans/mini-siem-implementation.md`
- Producer schema/capture/export: `server/PtkMcpServer/Audit/`,
  `server/PtkMcpServer.Tests/`
- Producer destination/delivery status:
  `server/PtkMcpServer/Audit/Web/AuditWebUiService.cs`
- Mini-SIEM ingest/store/query/UI: `siem/PtkSiemReceiver/`
- Mini-SIEM tests: `siem/PtkSiemReceiver.Tests/`
- Installer and transaction helpers: `scripts/install.ps1`,
  `scripts/ptk_install_transaction.psm1`
- Package builder/verifier: `siem/build-package.ps1`,
  `siem/verify-package.ps1`, `.github/workflows/release.yml`
- Operator documentation: `README.md`, `server/README.md`,
  `siem/PtkSiemReceiver/README.md`, future `docs/integrations/`
