# Plan: SIEM operator readiness and attributable activity audit

## Status and authority

**DRAFT — owner-directed planning work, 2026-08-13. No implementation is
approved.** This plan follows a published-artifact acceptance run that proved
the receiver backend works but the installed product does not provide a usable
operator workflow. Decision A is settled: every possibly relevant fact and
evidence artifact PTK captures must be exposed in the receiver and external
SIEM; sensitive evidence is protected rather than suppressed. Decision B is
also settled: supported clients must supply per-call agent/model identity when
technically possible, with source/trust recorded and no guessing. Decisions C-D
remain owner gates. Two owner-authorized,
unprimed Claude Fable 5 `openreview` attempts over the committed plan produced
no verdict. The first failed in the harness before model output. After the owner
explicitly authorized one fresh attempt, Anthropic's cyber safeguard refused it
before any repository read or git command. Per the owner's expensive-review
rule, no further Fable call was made. Canonical latest record:
`.agents/review/siem-operator-readiness-fable5-r2-refused.md`.
An owner-requested Kimi Code 0.35.0 / `k3` / transcript-high openreview over the
same pins returned a valid `best_approach` verdict with no material changes or
candidate findings on the plan before the owner settled Decisions A-B. Its
canonical record is `.agents/review/siem-operator-readiness-kimi-r1.md`. Review
endorsement does not approve implementation or settle Decisions C-D; it does
not review the post-review Decision A-B amendments.

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
- **Forensic evidence:** every possibly relevant fact or evidence artifact PTK
  captures for an activity, including exact submitted command bytes, complete
  captured response/output and error evidence, raw lifecycle events,
  attribution, execution context, and custody records. It may contain secrets;
  that changes its protection and retention requirements, not whether it is
  exported or available to an authorized operator.
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
command.byte_count | null
command.availability        receiver_and_external | not_observed |
                            retained_then_purged | delivery_incomplete
command.preview | null
response.evidence_id | null
response.sha256 | null
response.byte_count | null
response.availability       receiver_and_external | not_observed |
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

Decisions A-B below are settled. Decisions C-D record recommendations, not
approvals; ask and record each separately.

### Decision A — full-fidelity evidence at SIEM destinations

**SETTLED 2026-08-13:** the receiver and external SIEM are full-fidelity
forensic destinations. Every possibly relevant fact and evidence artifact PTK
captures must be exported and available to an authorized operator, including
exact command bytes and complete captured response/output and errors. There is
no metadata-only mode. Summary lists may remain bounded, but they must drill
into the complete record. Authentication, authorization, encrypted transport,
protected storage, access auditing, and retention protect sensitive evidence;
field suppression does not. A fact PTK never received is labeled unavailable,
not inferred. Canonical ruling: `.agents/decisions.md`.

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
  records. Any captured item absent from the remote forensic record is a test
  failure.
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

Exit evidence: an authorized operator retrieves and verifies the exact command,
complete captured result/error evidence, raw events, attribution, context, and
custody chain for a published-producer marker from both the receiver and the
external-SIEM adapter. Removing any evidence kind fails the acceptance guard.

### S3 — local activity viewer

- Replace the raw-record-first page with an activity list derived from the
  journal. Retain health and a separate raw/system-evidence view.
- Show client, supplied model/agent or explicit absence, tool/action, session,
  effective context, state, duration, exit code, and full-evidence delivery and
  retention status.
- Make rows keyboard-accessible links to a detail page joining accepted and
  terminal events, command evidence, correlation IDs, and chain context.
- Require authenticated drill-down for exact command, response/output, errors,
  and other retained evidence, with clear sensitive-data handling. Do not put
  bearer tokens or evidence in URLs; use an operator-token prompt/session
  header pattern.
- Add time, client, model, session, tool, state, and free-text digest/ID filters
  within bounded journal-reading limits. State the retained search window.

Exit evidence: a published-artifact acceptance marker is findable with its
client, exact command, complete captured result/errors, raw events, context,
attribution, and custody evidence visible without reading JSON or SQLite.

### S4 — receiver activity API and dashboard

- Add `/api/activities` and `/api/activities/{activityId}` projections over
  stored immutable events, with bounded filters and stable pagination.
- Keep `/api/events` for evidence/debug compatibility. Do not require clients
  to correlate call pairs themselves.
- Make dashboard activity rows clickable and display the complete activity
  contract. Link alert/gap/quarantine subjects to relevant activity/event
  detail.
- Add human-readable health summaries for delivery, chain integrity, custody,
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
  and loopback-only endpoints. It always enables full-fidelity evidence export
  and performs no trust-store mutation.
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

This slice is gated on Decision D. Decision A already requires full-fidelity
evidence export.

- Publish a Splunk guide covering HEC creation, index and sourcetype, TLS,
  credential storage, PTK configuration, field extraction, retention, and
  searches for client/model availability/tool/outcome, exact command and
  response evidence, raw events, and evidence/custody status.
- Provide a checked, versioned Splunk field mapping and sample dashboard/search
  definitions. Unknown fields remain preserved rather than dropped.
- Add a manual release-gate harness against a real, version-pinned Splunk
  instance. It uses published PTK artifacts, performs a recognizable call,
  waits for cursor acceptance, then queries Splunk and proves the expected
  fields and complete evidence bodies and manifests.
- Keep protocol fakes in ordinary CI for determinism, but label them adapter
  conformance—not external-SIEM acceptance.

Exit evidence: the release record names the Splunk version, PTK artifact SHA,
query, returned event ID/call ID, and digest-verified full-evidence result.

### S7 — published-artifact operator acceptance and corrective release

- Run the complete path on macOS arm64, Windows x64, Linux x64, and packaging
  smoke on the remaining published RIDs. Host-specific gaps remain explicit.
- Acceptance uses a fresh home and only archived artifacts plus public setup
  commands. It may not read source-tree test fixtures, call internal APIs, edit
  generated configuration, or introduce an undocumented proxy.
- The witnessed scenario installs, sets up local evaluation, connects PTK,
  executes a recognizable successful command and a failing command, finds both
  activities, retrieves exact command and complete captured response/error
  evidence from receiver and Splunk, displays attribution strength, investigates
  an alert, verifies zero unexplained gaps/quarantine or evidence-delivery gaps,
  restarts both products, and finds the records again.
- Run the real-Splunk gate and all existing producer/receiver durability,
  custody, security, compatibility, packaging, and dependency checks.
- Publish only after the owner explicitly approves the version/tag/release.
  Release notes state whether model attribution depends on client support and
  state that supported SIEM destinations receive the full retained forensic
  record.

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
- exact command/output/error evidence round-trip, chunking, authorization, and
  non-leakage into process logs, URLs, or unauthorized responses;
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
- Inventing prompts, reasoning, chat transcripts, model identity, or other
  client-only facts PTK did not receive. Any such context deliberately supplied
  to PTK is part of the full-fidelity forensic record.
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
