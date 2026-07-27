# Plan: production reliability salvage

**Status:** OWNER DECISIONS PENDING — topology decision 1 was settled by the
owner on 2026-07-26: one agent-owned MCP connection may own several explicitly
named isolated PowerShell sessions, and every session is a separate long-lived
PowerShell worker process. The earlier Claude Opus 5 acceptance of the
one-worker-per-connection draft is superseded. Claude Opus 5 round 4 returned
`REVISE`; round 5 closed every round-4 finding and accepted the single global
output lane, then returned `REVISE` for three local omissions. Round 6 closed
the containment and output-lane omissions and found one incomplete Slice 2
consumer inventory. Round 7 closed that inventory finding, accepted the local
evidence/admin boundary, and found five mechanical ownership and documentation
gaps. Every supported finding is incorporated below. Exact-blob closure review
is pending; its canonical verdict will be recorded externally under
`.agents/review/`, so no post-review status edit to this plan is required. No
implementation is authorized unless that verdict is `ACCEPT` and pending
decisions 2-4 under `Owner decisions` are approved in chat, one at a time.

## Goal

Make PTK a dependable production execution service for AI agents while
preserving its actual product:

- compact PowerShell and native-command output;
- warm PowerShell state across calls;
- several explicitly named, isolated warm PowerShell sessions within one
  agent-owned MCP connection;
- isolation between unrelated agents through separate agent-owned MCP
  connections;
- truthful, bounded failure behavior;
- recovery from a crashed or timed-out PowerShell worker without replaying an
  uncertain command.

The reliability contract is not literal uninterrupted availability. The OS,
the MCP client, credentials, remote services, storage, and the public stdio
connection can fail outside PTK's control. PTK's enforceable contract is:

1. never report success without a complete result;
2. never send one accepted request to a worker more than once;
3. never share warm state between distinct named sessions or supported
   agent-owned connections;
4. never overlap a PTK-contained old session worker with its replacement;
5. return a precise no-start, outcome-unknown, or completed result;
6. remain able to report supervisor and every session-worker health while one
   worker is lost;
7. keep optional operational features from disabling ordinary execution.

## Current repository facts

- `master` is the only implementation base. At plan time it is
  `c9b11bcb0b4e41a11110c5870562b4980c0b86b3`.
- The experimental resilience line is retained at
  `feature/mcp-resilience-r1`, head
  `93e79922a77bd5aab8e2959c69958dd165ea5087`.
- The resilience line is not merged into `master`. It changes 327 files with
  107,482 insertions and 3,558 deletions while changing no file in
  `src/PwshTokenCompressor.*` or its Pester suite.
- The resilience line adds a public guardian, a private host, per-session
  workers, two private protocol layers, recovery circuits, generation
  catalogs, package pinning, and guardian ownership of audit/output/job state.
- `master` already contains non-core systems that predate the resilience line:
  mandatory exact-script audit evidence, audit export/SIEM support, cold
  background jobs, output handles, and partial worker-process scaffolding.
- The root `README.md` still presents the three-process guardian topology as an
  approved target. `server/README.md` instead describes the current direct
  in-process server. The root README conflicts with the owner's current
  direction and is corrected only after this replacement plan is approved.

The resilience branch is evidence and a source of individually reusable code
and tests. It is never merged, rebased, or used as the base of this effort.

### Known production blockers this topology does not erase

The worker boundary is not permission to declare the compressor or client
integration production-ready. Production cutover remains blocked until:

- the open EXO/Outlook shaping failure is fixed under its own approved scope
  and a real workflow proves useful values without unsafe active-member
  evaluation;
- the current Windows worker/Job Object kill-path failures pass on a supported
  Windows host;
- the intended harness proves that server restart replaces a stale MCP
  transport instead of continuing to call the dead endpoint;
- the audit-root/startup incidents are covered by the audit-removal regression
  proof in this plan; and
- installed-package and security-scanner behavior needed by the chosen
  production distribution is either green or explicitly outside PTK's support
  promise; and
- the toolkit-owned `AGENTS.md` PTK guidance, which currently teaches
  `background=true` and `ptk_job`, is updated at its upstream governance source
  and refreshed normally before the reduced public surface is activated.

These are gates, not additional worker-salvage implementation slices. A
separately scoped product fix still needs its own approved plan.

## Proposed target topology

The first production topology requires one MCP stdio connection per unrelated
agent. Each connection owns one public PTK supervisor and a bounded set of
explicitly named PowerShell sessions. Every open session owns one replaceable
PowerShell worker process and that process owns exactly one warm runspace:

```text
agent A -> MCP connection A -> PTK supervisor A
                                  |-> default worker -> default runspace
                                  |-> exchange-onprem worker -> on-prem runspace
                                  `-> exchange-online worker -> EXO runspace

agent B -> MCP connection B -> PTK supervisor B
                                  `-> default worker -> default runspace
```

This intentionally mirrors interactive PowerShell: separate windows are
separate PowerShell processes. On-prem Exchange and Exchange Online modules
may therefore expose overlapping cmdlet names without sharing command tables,
loaded assemblies, static module state, variables, credentials, remote
connections, or failure state.

There is no public guardian/private-host split and no shared machine daemon.
The installed executable may use its existing internal `--worker` entry so the
package remains one managed application plus a platform-specific Unix
containment helper where required.

On Unix each session's outer worker broker is the sole owner of that session's
process group. Commands executed inside that worker inherit its group;
`ProcessTreeContainment` must not try to create a second exclusive group for
each command. Windows gives each session worker its own creation-time Job
Object. A timeout, reset, crash, or close in one session must never kill or
recycle another session's containment domain.

### Supervisor ownership

The public supervisor owns only:

- the original MCP stdin/stdout connection and frozen tool schemas;
- one bounded connection-owned registry of named session slots;
- public request correlation and one-response delivery;
- per-session worker process creation, monitoring, cancellation, and
  containment;
- active-call admission/drain and ordered connection shutdown;
- a small connection-local output registry;
- supervisor-local session listing and health projection for `ptk_state`.

Every session slot keeps its own monotonic worker incarnation as an internal
stale-frame key. It is not part of the public control surface because no public
operation can act on it.

It loads no user script into an in-process runspace and executes no submitted
PowerShell, RTK, Bash, or native command.

### Worker ownership

Each session worker owns:

- exactly one warm `SessionRuntime` and PowerShell runspace;
- that named session's modules, variables, functions, directory,
  environment drift, credentials/connections, and foreground serialization;
- command planning and PowerShell/RTK/validated-Bash execution;
- bounded output production;
- child processes created for that session's work.

Workers belonging to different named sessions or different MCP connections may
execute concurrently. Foreground calls within one session worker remain
serialized.

### Connection identity and support boundary

- MCP does not expose a trustworthy agent identity that PTK can infer.
- Therefore PTK cannot infer which unrelated agent owns a call multiplexed over
  one MCP connection. Explicit session names are routing labels inside one
  connection, not caller identities or security boundaries.
- A supported harness gives each unrelated agent its own MCP connection/server
  process. If an integration multiplexes unrelated agents over one connection,
  every caller can name every session on that connection; that integration is
  unsupported.
- Slice 0 must prove the intended production harness actually supplies a
  distinct PTK server PID and stdio connection per agent. If it does not, this
  plan stops before runtime changes; a separately approved harness-identity
  design is required.
- No session, worker, runspace, or output handle survives its agent-owned
  supervisor. There is no daemon, cross-connection attach, shared session, or
  durable session.
- No idle timer recycles an open session's warm worker by default. Public EOF,
  explicit session close, or explicit reset owns teardown.

### Named-session contract

Keep existing unqualified behavior by reserving `default`. Add an optional
`session` argument, defaulting to `default`, to `ptk_invoke`, `ptk_state`, and
`ptk_reset`. Add:

```text
ptk_session(action, name=null)
  action = list | open | close
```

The contract is deliberately small:

- `default` always exists. Its worker starts lazily on the first effectful
  unqualified call; `ptk_state` and `ptk_session list` never start it.
- A non-default session must be opened explicitly before use. An unknown,
  misspelled, closing, or closed name refuses before dispatch and never falls
  back to `default` or silently creates a worker.
- Session names are connection-local, nonsecret semantic labels, canonical
  lowercase, and match `[a-z0-9][a-z0-9._-]{0,63}`.
- There is no mutable `select` or current-session state. Every non-default
  invoke, state, or reset call names its session.
- `open` is idempotent for an already-open name. It starts one fresh contained
  worker and succeeds only after that worker is ready. It accepts no bootstrap
  template, script, credential, profile, or policy parameter.
- Concurrent opens of the same cold name share one bounded startup task; they
  can never launch two workers. `open` on `Faulted` refuses with that session's
  bounded failure and directs the caller to `ptk_reset(session=...)`; it never
  silently retries under a second lifecycle verb. `open` during recovery or
  closing returns `session_busy`.
- `close` is valid only for a non-default idle session. It stops that worker,
  confirms its containment domain is empty, and removes the name. It refuses
  while an operation is active or the old containment domain is unconfirmed.
  An unconfirmed alias remains reserved and cannot be closed/reopened around
  the no-overlap guard. EOF remains the unconditional all-session teardown
  boundary.
- `ptk_reset(session=...)` replaces only the selected session's worker and
  leaves every other session untouched. Omitting `session` preserves today's
  default-session behavior. Explicit reset is idle-only in this first cut: an
  active, starting, recovering, or closing session returns `session_busy`
  promptly with no cancellation or side effect. Execution timeout retains its
  separate internal cancel/containment path.
- One supervisor admits at most eight open sessions including `default`.
  Admission beyond that fixed first-cut bound refuses before worker creation.
- `ptk_session list` is supervisor-local, never starts or queries a worker, and
  returns every open name plus its lifecycle state, worker PID when present,
  active-call flag, and last bounded failure class.
- `ptk_output` remains handle-only. Handles are globally unique inside the
  connection and internally attributed to their originating session; reading
  one never opens, selects, or executes a session.
- Closing a session cancels and discards only its unsealed captures. Already
  sealed handles remain supervisor-owned and readable until their existing TTL
  or quota eviction; reopening the same alias creates a new session identity
  and never restores warm state. Artifact quota attribution remains with the
  immutable originating identity until expiry; the supervisor aggregate quota
  bounds close/reopen churn.
- Session names provide operational isolation, not authorization. The
  supervisor process still runs under one OS identity, and upstream Exchange,
  Graph, AD, and other service permissions remain authoritative.

Every session slot has one asynchronous lifecycle gate. Open publishes
`Starting`, and close/reset publishes `Closing`/`Recovering`, before releasing
that gate; later worker-bound admissions then refuse rather than queue behind
the transition. A ready invoke captures the slot identity and worker
incarnation and takes one operation lease before the gate is released. Close
and explicit reset require zero leases. A late frame may complete only its
captured caller; it can never install state, output, or readiness into a
replacement incarnation or a reopened alias.

Worker open and replacement use one operator-configured startup deadline; it
is not another public parameter. Failure before process launch leaves a new
named slot absent and `default` cold. Failure after launch stops protocol,
terminates that session's containment domain, and waits only the configured
containment grace. Confirmed empty containment leaves the slot `Faulted`.
Unconfirmed containment also leaves it `Faulted`, reports
`descendants_unknown`, and refuses open/reset/close until the observer later
confirms the old domain empty. No later worker may overlap it.

## Minimal worker protocol

Keep one strict, bounded supervisor-to-worker protocol. It is internal to the
server project; no separate `PtkSharedContracts` project is required.

Required message kinds:

1. `initialize` / `ready`, binding protocol version, supervisor-local session
   identity, that session's worker incarnation, and immutable limits;
2. `invoke`, carrying one bounded strict-UTF-8 script plus raw, route, and
   timeout options; the first cut has no background option;
3. `state_query` / `state_snapshot`, carrying only bounded runspace diagnostics
   when the worker is idle;
4. `cancel`, naming one active request;
5. ordered bounded `artifact_chunk` frames plus one `artifact_seal` carrying
   the final byte length and SHA-256 digest when output recovery is available;
6. `result`, carrying exactly one completed, refused, cancelled, timed-out, or
   failed terminal;
7. `shutdown` / `stopped`.

The frame reader rejects invalid UTF-8, duplicate or unknown fields, wrong
versions, stale incarnations, oversized frames, and unsolicited terminals. A
worker executes only after a complete valid request frame is decoded.

Do not port the resilience branch's prepare/descriptor/commit/abort protocol.
The supervisor uses one conservative write-attempt boundary:

- before the first pipe-write call is entered, failure is proved-no-start;
- once the first pipe-write call is entered, even if it throws or reports a
  short write, failure before a complete valid terminal is decoded is
  `outcome_unknown`; PTK cannot prove how many bytes the OS transferred;
- after a complete valid terminal is decoded, that terminal is delivered once
  even if the worker exits immediately afterward.

This deliberately gives up some retryable classifications to remove a large
state machine without weakening correctness. PTK never automatically resends a
public invoke. A caller that submits the script again creates a new request and
may repeat external effects; PTK has no public idempotency key with which to
recognize that resubmission. Every `outcome_unknown` response therefore says
not to resubmit automatically.

## Failure and recovery contract

### Unexpected worker loss

1. Atomically mark only the affected session worker unavailable.
2. Terminalize its active request using the delivery boundary above.
3. Stop admitting new work to that session. Other named sessions on the same
   connection and other agents' PTK connections remain usable.
4. Kill the worker and sweep that session's containment domain: its Windows
   Job Object or Unix broker-owned process group.
5. Only after the worker has exited and that owned containment domain is
   confirmed empty, allocate the session's next incarnation and make one
   immediate replacement attempt.
6. A successful replacement becomes `Ready` with
   `warm_state_lost=true` and an empty, sound runspace.
7. A failed replacement marks that session `Faulted`. No automatic retry loop
   runs. An explicit `ptk_reset(session=...)` makes one new replacement
   attempt.

No modules, variables, credentials, connections, profiles, or previous calls
are replayed automatically.

If worker or containment death is not confirmed within the bounded grace, the
session remains `Faulted` with `descendants_unknown` and its alias stays
reserved. Its containment observer continues supervisor-locally, but no open,
reset, close, or replacement is admitted until that exact old domain is
confirmed empty. Other sessions remain usable.

This is not a sandbox guarantee. A Unix descendant can leave a process group,
and a remote service can continue work after the local process disappears.
PTK never claims those effects stopped. Observed or unprovable escape is
reported as `descendants_unknown`, keeps the preceding request
`outcome_unknown`, and never makes it eligible for automatic retry.

### Timeout

- A timeout requests cancellation, then terminates the worker and sweeps its
  PTK-owned containment domain if execution does not stop within the configured
  containment grace.
- If the worker proves no command started, the result is a retryable no-start.
- Otherwise the call is nonretryable `outcome_unknown` unless a complete
  timeout terminal was already decoded.
- The replacement rules are identical to unexpected worker loss.

### Supervisor or public-pipe loss

Supervisor death or public EOF ends the MCP connection and every worker in its
owned containment domain. PTK cannot repair dead stdio endpoints in-process or
prove that an escaped local descendant or remote effect stopped. The harness
must start a fresh MCP server. Installation and client guidance must state this
boundary plainly.

### State projection

`ptk_state(session=...)` remains prompt and always returns a supervisor-owned
section plus the selected session's supervisor-owned summary. The supervisor
section is `Running`, `Draining`, or `Closed`. The selected session is `Cold`,
`Starting`, `Ready`, `Recovering`, `Faulted`, `Closing`, or `Closed`, and
reports:

- worker PID where one exists;
- whether warm state was lost;
- whether a request is active;
- the last bounded failure class;
- whether explicit reset is required.

Runspace facts remain worker-owned. When the worker is ready and idle,
`ptk_state` may request one bounded snapshot containing engine identity, current
directory, loaded modules, environment/PATH/variable drift, and—only when
`listAvailable=true`—the installed-module enumeration. When the worker is
busy, absent, starting, recovering, faulted, or closed, the worker section is
present as `unavailable` with the exact reason. No worker-owned field is
silently omitted or guessed from supervisor state.

New execution calls to a selected session during `Starting`, `Recovering`,
`Faulted`, `Closing`, or `Closed` fail promptly. They are never queued for
later execution and do not affect calls to another ready session.

## Operational features

### Audit and exact-script evidence

Mandatory audit is not part of the proposed production-critical path.
`master` currently writes exact submitted scripts under
`~/.ptk/audit/evidence` and can disable all effects when that storage is
unavailable or contains an unknown artifact. That coupling directly conflicts
with the availability goal. `AuditRuntimeGate` also carries non-audit lifecycle
duties; those duties are reassigned before the gate is removed.

Audit also remains a build-time dependency: `PtkMcpServer.csproj` compiles the
OTLP protobuf through `Grpc.Tools`, and the current state records a clean ARM64
Linux MSBuild/protoc crash on that path. Removing audit only at runtime would
leave this production blocker intact.

The proposed core:

- does not persist exact submitted scripts by default;
- does not make audit, SIEM, export, or evidence retention a prerequisite for
  execution;
- removes audit health from the ordinary invoke gate;
- removes the OTLP protobuf and `Grpc.Tools` build path from the runtime server
  project and retires the core producer-to-SIEM conformance step that consumes
  those runtime types; the standalone SIEM receiver and its own tests remain
  parked, and any future exporter moves to a separately built optional product;
- retains `SecureAuditStorage` as the already-proved protected-local-storage
  dependency used by `OutputStore`; keeping that primitive does not retain
  audit admission, evidence publication, export, or startup coupling;
- moves active-call admission/drain and ordered shutdown into a small
  supervisor lifecycle service, while `WorkerSupervisor` owns worker creation
  and reset;
- retains no silent compatibility mode that can unexpectedly become
  fail-closed.

If compliance audit is later required, design it as a separately approved,
explicit mode or sidecar with its own availability contract. Do not preserve
the existing mandatory behavior merely because code exists.

### `ptk_output`

Keep same-invocation bounded output capture if it remains independent of audit.
Capture failure must degrade to ordinary bounded output plus
`recovery=unavailable`; it must not refuse or rerun the command. Handles remain
connection-local, are attributed to their originating session, and expire by
bounded per-session and aggregate memory/disk quota.

Each supervisor uses a uniquely owned output root with a creation identity and
exclusive live-owner marker. Startup reclaims only roots whose recorded owner
is provably dead; it never scans or deletes another live supervisor's root.
Normal connection teardown removes its own sealed and unsealed residue.

`OutputStore` deliberately retains one connection-wide foreground storage lane
to cap potentially uninterruptible filesystem work at one task per supervisor.
Lane acquisition waits only the existing bounded capture interval and never
starts a second storage task while waiting. Healthy concurrent reservations
and seals therefore serialize and retain their handles; a wedged lane times out
as `recovery=unavailable`, after which the ordinary invoke, runspace state, and
`ptk_state` continue. Result delivery never waits beyond its existing capture
budget. This shared optional-output degradation is an explicit non-guarantee,
not session state sharing. Do not replace it with one potentially wedged
storage task per session. Per-session byte quotas remain independent so
ordinary quota exhaustion in one session does not consume another session's
allocation.

Before an invoke, the supervisor reserves one connection-local artifact ID,
charges it to the selected session, and reserves the complete per-invocation
artifact quota. If either the session quota or aggregate quota cannot satisfy
the reservation, the invoke frame disables artifact transfer and the command
still returns its ordinary bounded result. When enabled, the frame carries the
reserved artifact ID and maximum byte count; the worker sends the exact
recoverable output as monotonically ordered, individually bounded chunks, then
a seal with the total length and digest.

The supervisor publishes an immutable public handle only after a valid seal.
One dedicated protocol reader per session worker always drains that worker's
pipe and never awaits artifact storage. For an enabled artifact, quota
reservation also reserves a fixed in-memory queue large enough for that
invocation's maximum artifact.
The reader copies chunks into that queue with a nonblocking `TryWrite` and
continues parsing state, cancellation, and result frames. A separate sink owns
disk writes and digest/length verification.

If the queue unexpectedly refuses a chunk, the sink stalls or fails, or the
sink has not completed the valid seal when the ordinary result terminal
arrives, the supervisor atomically switches that artifact to
discard-and-drain, cancels/cleans its sink, and reports
`recovery=unavailable`; it never delays the ordinary result waiting for
storage. Only a sink already complete at result delivery publishes the public
handle. Gaps, duplicates, over-reservation bytes, unsolicited chunks, or a
wrong seal remain worker protocol violations and use the ordinary
`outcome_unknown` worker-loss path; they never cause resubmission. A worker
lost mid-transfer leaves an explicitly incomplete artifact. Every queued
buffer is cleared before release.

### `ptk_job`

Remove cold `ptk_job` from the first production surface. It does not preserve
warm runspace state, and making the supervisor own submitted job execution
violates the minimal ownership boundary. Do not port or recreate its
guardian-era capability machinery. The same owner decision removes
`ptk_invoke(background=true)`; retaining a start-job flag without a job
management tool would be an incoherent public contract.

A later owner-approved job plan may add worker-owned cold jobs. That design must
give every accepted job exactly one terminal, mark all jobs owned by a lost
worker `lost`/`outcome_unknown`, never reuse an ID within the connection, and
never restart a job automatically. Warm asynchronous runspace jobs remain out
of scope.

### Routing and compression

Preserve the `master` behavior for PowerShell object shaping, RTK routing,
validated Bash fallback, terminal cleanup, and bounded text. The salvage effort
does not redesign compression.

## Salvage map

Port behavior and focused tests, not commits or directory trees. Every candidate
must compile without the discarded guardian/private-host graph before it is
accepted.

### Keep from `master`

- `src/PwshTokenCompressor.psm1` and `.psd1`;
- `RunspaceHost`, `SessionRuntime`, and command resolution/routing;
- `WorkerProtocol.cs`, `WorkerOperationProtocol.cs`, `WorkerServer.cs`,
  `WorkerProcessEntry.cs`, `WindowsWorkerBootstrap.cs`, and
  `WindowsWorkerNative.cs`; these files already exist on `master` and are
  modified in place rather than ported from the resilience branch;
- the current invoke/state/reset/output behavior, subject to the owner-approved
  removal of invoke's background option;
- bounded output shaping and `OutputStore` behavior that does not depend on
  mandatory audit;
- the existing one-process handshake as the initial compatibility baseline.

### Branch-only candidates from `feature/mcp-resilience-r1`

- `UnixWorkerBootstrap.cs` and `UnixWorkerProcessLauncher.cs`, after their
  private-host registry dependency is replaced with a supervisor-owned
  interface;
- the worker-only native broker currently located at
  `server/PtkMcpGuardian/Native/ptk_containment_broker.c`, relocated under the
  server project with no guardian dependency;
- only the worker-focused tests that prove framing, cancellation, timeout,
  tree death, stale-incarnation rejection, output transfer, and isolation
  between two worker processes;
- the existing R0 session-name grammar and unqualified-`default`
  compatibility assertions as evidence for the smaller replacement schema,
  not the guardian-era schema or registry implementation.

The first implementation slice that touches a candidate inspects that file's
direct dependencies and either removes guardian/private-host dependencies or
rejects the candidate. It does not produce a separate mapping deliverable.
Similar names are not evidence that a file can be transplanted.

### Inspect branch deltas; do not port whole files

- inspect the branch changes to the six worker files already on `master` hunk
  by hunk and take only behavior required by this plan;
- do not port `WorkerClient.cs` or `WorkerProcessClient.cs`: both are coupled
  to `PtkSharedContracts` and prepare/commit/abort machinery. Write the smaller
  supervisor-side client directly against the minimal worker protocol;
- inspect Windows launcher deltas against the `master` implementation rather
  than replacing the known baseline.

### Rewrite narrowly

- a `WorkerSupervisor` owning a fixed-bound map of named session slots, each
  with one worker client, one lifecycle gate, one incarnation counter, and one
  containment domain;
- a supervisor-owned Unix containment registry replacing the branch's
  private-host-only registry and keeping containment ownership per session;
- a Unix branch in `WorkerProcessEntry.RunAsync` that validates and opens the
  Unix bootstrap/IPC handles before starting `WorkerServer`;
- direct public-tool adapters that validate and resolve one explicit session
  before dispatch;
- the small `ptk_session` list/open/close surface defined above;
- per-session state projection and one-attempt replacement;
- development installer smoke/rollback around the single supervisor package.

### Remove from `master`

- `WorkerPreparedOperationCodec.cs` and its prepared-operation tests;
- prepare/prepared/commit/abort message kinds and parsing branches already
  present in `WorkerProtocol` and `WorkerOperationProtocol`;
- prepared-only branches in `WorkerOperationScheduler` and their tests, while
  retaining only direct foreground serialization needed by the minimal
  protocol.

### Do not port

- all of `server/PtkMcpGuardian` except the worker-only native broker source,
  which is relocated and loses the guardian project dependency;
- all guardian-host and recovery-manifest code in `PtkSharedContracts`;
- `server/PtkMcpServer/GuardianHost`;
- the separate private-host mode and outer guardian broker;
- `RecoveryCircuitMachine`, retry/backoff tables, half-open logic, readiness
  polling gates, frozen catalogs/manifests, template bootstrap, guardian-owned
  generation allocation, and host/session dual recovery;
- prepared descriptor/commit/abort scheduling;
- the branch's guardian-coupled public session schema, profiles/templates,
  durable bindings, host registry, and recovery catalog; re-freeze only the
  smaller connection-local session contract in this plan;
- guardian-owned audit/output/job capability registries;
- matched guardian/host package loaders and R7 guardian cutover;
- resilience tests whose only purpose is a discarded layer.

## Implementation sequence

Each slice is one coherent commit. A failed or incomplete slice is not merged
into the implementation branch. New tests receive the repository-required
red/green guard proof.

### Slice 0 — baseline

1. Create a new implementation branch from current `origin/master`; never from
   the resilience branch.
2. Run and record the complete existing verification battery.
3. Record every existing failure as a production blocker; do not baseline it
   away or silently convert it to an expected skip.
4. Run a no-product-change production-harness probe and record whether two
   unrelated agents receive distinct PTK server PIDs and stdio connections.
   If they do not, stop: shared-connection multi-agent use is unsupported by
   this plan.
5. Record the exact base SHA and reject any candidate whose value cannot be
   separated from guardian/private-host policy more cheaply than a narrow
   rewrite.

Exit: clean baseline, exact base SHA, and evidence that the intended harness
gives each unrelated agent an independent PTK connection. No runtime behavior
changes.

### Slice 1 — retire the frozen R0 guardian contract

This slice is owner-gated because the current guards record an approved
guardian-era contract; they are not ordinary obsolete tests to delete around.

1. Keep `ToolSchemaConformanceTests` matched to the live five-tool direct server
   in this no-runtime-change slice; it changes atomically with job removal in
   Slice 6.
2. Remove or re-freeze `McpResilienceR0ContractTests`, the embedded R0 public
   contract and digest, recovery schemas/examples, package-role assertions,
   and native/helper inventories so they describe only the approved topology.
3. Delete the guardian/private-host-only `PtkResilienceTestFixture`,
   `ResilienceFakeGuardianTests.cs`, and `FakePrivateProtocolTests.cs`, plus
   their project/solution references. They contain no retained containment
   coverage.
4. Preserve the already guardian-free `PtkContainmentTestFixture` under its
   existing name, together with `WindowsContainmentIntegrationTests.cs` and
   `WindowsNestedJobResilienceIntegrationTests.cs`. Do not rename, replace, or
   weaken that coverage.
5. Add a guardian-free conformance guard for the still-live direct-server
   surface. The owner-approved replacement five-tool guard replaces it only in
   the Slice 6 commit that atomically removes `ptk_job`, adds `ptk_session`,
   and changes the related schemas and runtime.

Exit: no guardian-era contract or fixture claims to be the active production
surface, the existing containment fixture remains green and unchanged, and the
replacement direct-server guard still matches live behavior. No runtime
execution path or public tool list changes in this slice.

### Slice 2 — remove audit from the execution gate

1. Add a failing integration test proving a valid invoke succeeds when the
   audit root is absent or unwritable.
2. Extract active-call admission/drain and ordered shutdown from
   `AuditRuntimeGate` into a small audit-independent supervisor lifecycle
   service. Move per-session runtime creation to `WorkerSupervisor`; delete the
   idle watchdog and its activity clock rather than recreating them.
3. Prove shutdown stops new admission, cancels/drains every active request,
   and tears down every session worker before the public process exits.
4. Remove mandatory audit admission and exact-script evidence publication from
   ordinary tool execution.
5. Remove default startup construction of audit/SIEM/export resources from
   `Program.cs`, remove the anchored-export construction from
   `AuditRuntimeResources.cs`, and remove only the runtime project's
   `<Protobuf>` item plus its `Grpc.Tools` and `Google.Protobuf` package
   references. Retain local journal/evidence administration only where a
   non-OTLP caller remains. Retain `SecureAuditStorage` because `OutputStore`
   uses its protected root/file operations; do not rename or rewrite that
   proved storage primitive in this slice.
6. Remove the producer-to-SIEM conformance surface atomically:
   - delete `server/PtkMcpServer.Tests/SiemConformance/` and
     `server/PtkMcpServer.Tests/AuditOtlpSiemConformanceTests.cs`;
   - remove the `AuditOtlpSiemConformanceTests.cs` and
     `SiemConformance/**/*.cs` exclusions from
     `server/PtkMcpServer.Tests/PtkMcpServer.Tests.csproj`;
   - retain `AuditCoreSchemaTestRecords.cs`, which remains ordinary
     main-project test input and has no OTLP producer dependency;
   - remove only the producer-to-SIEM conformance step from
     `.github/workflows/ci.yml`.
   Keep the standalone `siem/PtkSiem.slnx` receiver tests and their CI step; a
   future producer/exporter requires a separately approved optional project.
7. In the same commit, remove the complete anchored OTLP export path rather
   than retaining dead abstractions merely to make the project compile:
   - relocate `server/PtkMcpServer/Protos/audit_otlp.proto` and
     `server/PtkMcpServer/Protos/LICENSE.OpenTelemetry-Apache-2.0.txt` into
     `siem/PtkSiemReceiver/Protos/`, then repoint
     `siem/PtkSiemReceiver/PtkSiemReceiver.csproj` and the active
     `.agents/plans/mini-siem-implementation.md` source pointer to that local
     copy; this is a retained receiver wire contract, not a runtime producer
     dependency;
   - delete `AuditOtlpRecordMapper.cs`, `AuditOtlpHttpExporter.cs`,
     `AuditExportCoordinator.cs`,
     `AuditBootExportSource.cs`, `AuditClosedSpoolExportPump.cs`,
     `AuditExportLoop.cs`, `AuditExportAcknowledgmentObserver.cs`,
     `AuditExportConfiguration.cs`, `AuditExportRetrySchedule.cs`,
     `AuditExportTransitionRecorder.cs`, `ExportConfigurationIdentity.cs`, and
     export-only transition/health branches;
   - delete `FakeOtlpHttpsReceiver.cs`, `AuditOtlpRecordMapperTests.cs`,
     `AuditOtlpHttpExporterTests.cs`,
     `AuditOtlpHttpExporterIntegrationTests.cs`,
     `AuditOtlpExportCompositionTests.cs`,
     `AuditClosedSpoolExportPumpTests.cs`,
     `AuditExportCoordinatorTests.cs`, `AuditExportLoopTests.cs`,
     `AuditExportAcknowledgmentObserverTests.cs`,
     `AuditExportConfigurationTests.cs`, `AuditExportRetryScheduleTests.cs`,
     `AuditExportTransitionRecorderTests.cs`, and
     `ExportConfigurationIdentityTests.cs`;
   - remove anchored-export cases and transport stubs from
     `AuditLiveSpoolReaderTests.cs`, `AuditAnchoredRuntimeTests.cs`,
     `AuditEvidenceRetentionTests.cs`, and
     `AuditEvidenceOrphanReconcilerTests.cs`, while retaining their unrelated
     local journal, reader, evidence, and reconciliation coverage;
   - remove export-loop/coordinator cases and fake export sources from
     `AuditRuntimeGateTests.cs`, `AuditCallFilterTests.cs`, and
     `AuditOptionsHealthTests.cs`, while retaining unrelated lifecycle,
     call-filter, and health coverage;
   - edit `AuditRuntimeResources.cs`, `AuditStartupConfiguration.cs`,
     `AuditEvidenceOrphanReconciler.cs`, `AuditHealth.cs`, and their retained
     tests to remove only the now-dead anchored-export branches;
   - retain and edit `AuditExportCheckpoint.cs`,
     `AuditExportCheckpointStore.cs`, and their tests because
     `AuditAdminOperations.cs`, `FileAuditJournalSink.cs`,
     `ScriptEvidenceStore.cs`, `AuditAnchoredWriterPreparation.cs`, and
     `AuditCompletedChainRetirement.cs` remain non-OTLP callers.
   `AuditOtlpHttpExporter.cs` owns `IAuditOtlpExportTransport`; the production
   and test files above are its consumers, so moving or stubbing that interface
   elsewhere is forbidden. Recreating `AuditExportLoop`,
   `AuditExportCoordinator`, or their step, state, or snapshot types in runtime
   or test code is equally forbidden. No source is retained solely because
   another dead exporter source or its test references it; every retained
   export-named type must have a cited non-OTLP production or administration
   caller. A reference inventory must prove no `IAuditOtlpExportTransport`,
   `Grpc.Tools`, or `Google.Protobuf` reference remains in the runtime project
   and no anchored export loop remains compiled but unreachable.
8. Update `server/test-handshake.ps1` atomically: retire its audit segment,
   exact-script-evidence, and fail-closed audit-outage assertions; replace them
   with the Slice 2 regression that an unwritable audit root does not block an
   effect and `ptk_state` says audit is not enabled. Preserve every non-audit
   handshake and schema assertion.
9. Prove a clean ARM64 Linux restore/build no longer enters the removed protoc
   path.
10. Ensure `ptk_state` remains usable and truthfully says audit is not enabled
   rather than reporting a false protected boundary.
11. Keep any retained audit administration executable out of the installed
   runtime package pending a separate product decision.
12. Retain the operator-disposition path and its `PtkAuditAdmin` command only
   as legacy-state administration: a pre-upgrade checkpoint can still contain
   a permanent export block, the Slice 2 runtime can create no new block, and
   the retained command remains the supported way to clear that old state.

Exit: no ordinary invoke depends on `~/.ptk/audit`; no exact script file is
created by default; no runtime-project reference to
`IAuditOtlpExportTransport`, `Grpc.Tools`, or `Google.Protobuf` remains; no
anchored export loop remains compiled but unreachable;
`dotnet test siem/PtkSiem.slnx` passes against the relocated receiver-owned
wire contract; full verification green.

### Slice 3 — minimal worker protocol

1. Delete the existing prepared codec, message kinds, and scheduler branches
   listed above.
2. Freeze the minimal message union and strict bounds in server-local tests.
3. Reuse or rewrite the smallest existing worker codec that meets the frozen
   contract.
4. Bind one `SessionRuntime` behind each worker server in an unwired test
   fixture.
5. Prove fragmented/coalesced input, malformed UTF-8/JSON, stale incarnation,
   duplicate request IDs, cancellation, bounded state snapshots, unavailable
   busy-state diagnostics, ordered artifact chunks, seal digest/length
   validation, and exactly one terminal.
6. Prove two worker servers initialized for different supervisor-local session
   identities reject each other's stale or misrouted frames.

Exit: worker protocol is live only in a disposable fixture; public MCP behavior
is unchanged.

### Slice 4 — cross-platform worker launch and containment

1. Port the worker-only Unix broker/launcher and Windows creation-time Job
   Object launcher without the outer guardian/host registry.
2. In the same commit, reduce
   `UnixGuardianBrokerIntegrationTests.cs` and
   `Native/ptk_guardian_broker_fixture.c` to the worker-broker half. Preserve
   direct Unix parent-death, worker-as-process-group-leader, descendant-tree
   death, and native-binding assertions; remove outer guardian/host transcript
   fields and assertions only after their worker equivalents pass on Linux and
   macOS.
3. Implement the supervisor-owned per-session Unix containment registry and wire
   `WorkerProcessEntry.RunAsync` to consume `UnixWorkerBootstrap`, validate
   inherited handles, remove bootstrap variables, and open the worker IPC
   channel.
4. Add an internal, validated worker-containment mode that makes
   `ProcessTreeContainment` reuse the broker-owned group. Prove it performs no
   nested `setpgid`/`setsid` ownership attempt and every ordinary direct child
   inherits the worker group.
5. Require the broker for every Unix worker launch. Keep the old unbrokered,
   process-global containment fallback only for the still-live in-process
   public path through Slice 5; it is not a supported worker mode.
6. Bind liveness to the public supervisor so supervisor death or EOF kills
   every worker and PTK-owned containment domain.
7. Prove one selected worker, direct child, and grandchild die on normal
   session close, reset, timeout, and hard supervisor termination while a
   sibling session worker and its descendants remain alive except at
   supervisor termination.
8. Confirm no replacement starts until the old worker has exited and its
   Windows Job Object or Unix broker process group is empty.
9. On Unix, deliberately escape the process group where the platform permits
   and prove PTK reports `descendants_unknown` rather than claiming complete
   descendant death.

Exit: two disposable workers with distinct containment domains can be launched
concurrently and killed independently on macOS, Linux, and Windows; public MCP
behavior is unchanged.

### Slice 5 — connection-owned named-session lifecycle

1. Add the fixed eight-slot map to the public supervisor. Reserve `default`;
   give every slot its own worker client, lifecycle gate, internal incarnation,
   lifecycle state, and foreground operation lock.
2. Implement the named-session contract above behind an internal fixture:
   strict names, explicit non-default open, idempotent open, idle-only close,
   no fallback, no auto-create, no `select`, fixed-bound refusal, and
   supervisor-local list.
3. Prove concurrent opens share one startup task, startup obeys its deadline,
   a faulted open requires explicit reset, reset/close refuse while busy, and
   no late frame can mutate a replacement or reopened session. Also prove
   close-then-reopen cannot free or reuse an alias while its old containment
   domain is unconfirmed.
4. Bind every slot and containment domain to the MCP stdio connection lifetime.
5. Prove one fixture connection concurrently owns two different worker PIDs
   and runspaces. Give both the same function/cmdlet name with different
   implementations and prove each explicit session resolves only its own
   command, variables, modules, directory, environment, and connection state.
6. Prove reset, timeout, crash, replacement, and close affect only the selected
   slot; public EOF removes every slot and worker.
7. Prove sealed output handles survive close until normal expiry while
   unsealed captures are discarded and never attach to a reopened alias.
8. Prove a second supervisor owns a disjoint session registry and cannot
   address, reset, or observe the first supervisor's sessions.

Exit: bounded named-session ownership and both within-connection and
cross-connection process isolation are proven with fixture workers; the current
public invoke path remains intact.

### Slice 6 — production cutover to workers

1. Route foreground invokes through the explicitly resolved session worker.
2. Keep session listing, lifecycle state, and reset ownership in the
   supervisor; obtain selected-worker state only through the bounded
   idle-worker query above. Keep output recovery in the supervisor.
3. If the owner approved cold-job removal, delete `JobTool`, `JobManager`,
   `BackgroundJobContainment`, `JobManagerTests`, their DI/factory wiring, and
   every background branch/parameter/description in `InvokeTool`,
   `ISessionOperations`, and `SessionRuntime`.
4. In that same atomic change, remove `ptk_job` and invoke's background
   property, add `ptk_session`, and add the optional `session` field to invoke,
   state, and reset. Replace the old five-tool conformance expectation with the
   replacement five-tool contract in `ToolSchemaConformanceTests`,
   `server/test-handshake.ps1`, the replacement
   `Contracts/ResilienceR0/public-tool-contract.json`, and its frozen digest. A
   test must never describe a surface the server does not yet expose.
5. Remove the in-process production runspace path in the same slice; no dual
   execution mode remains. On Unix, remove the now-unreachable process-global
   exclusive-group/polled-closure fallback in `ProcessTreeContainment`; a
   worker without its broker fails startup closed.
6. Preserve the remaining public schema except the owner-approved job removal
   and named-session additions defined above. `ptk_output` remains
   session-argument-free.
7. Prove unqualified calls still use `default`; unknown or closed names refuse
   before worker dispatch; and one submitted script executes once in only the
   named worker and reaches the unchanged compressor.
8. Prove two named sessions accept concurrent calls while calls within either
   individual session remain serialized.

Exit: all production PowerShell work runs only in the selected session's
contained worker; the five-tool named-session contract, full verification, and
handshake are green.

### Slice 7 — truthful loss and one-attempt recovery

1. Implement the write-attempt boundary and one-response terminal ownership,
   including a fault injected at entry to and failure return from the first
   write call.
2. Implement confirmed worker exit and owned-containment sweep before
   replacement.
3. Make one automatic replacement attempt, then fault only that session until
   explicit reset.
4. Prove no replay at every pre-write, partial-write, executing, terminal, and
   post-terminal death point.
5. Prove one session worker's crash cannot change a sibling session's worker
   PID, warm state, or successful operation, or anything in another server
   process.

Exit: real apphost fault matrix green on every supported platform.

### Slice 8 — output continuity

1. Retain only output behavior that remains independent of mandatory audit and
   discarded guardian capabilities.
2. Implement full-quota reservation before execution, artifact-disabled invoke,
   session attribution, reserved artifact ID, the nonblocking protocol reader,
   fixed preallocated queue, independent sink, chunk/seal validation,
   discard-and-drain fallback, and immutable-publication paths described above.
3. Prove wrong order, duplicate/gapped chunks, digest mismatch, quota overflow,
   worker loss mid-transfer, a deliberately stalled sink, a full queue, and
   local write failure never block the ordinary result, replace the worker,
   cause replay, or publish a false complete handle.
4. Prove connection teardown removes its own output root, hard supervisor death
   leaves bounded residue, and the next startup reclaims that stale root without
   touching a simultaneously live supervisor's root.
5. Prove a stalled or failed artifact sink for one session cannot delay an
   ordinary result or state call in another session beyond the existing
   bounded capture interval, and a handle remains readable without selecting
   or reopening its originating session.
6. Prove a wedged connection-wide storage lane makes sibling capture fail
   within that bounded interval as `recovery=unavailable`, then allows the
   sibling command to run without starting another potentially wedged storage
   task.
7. Prove two healthy concurrent sessions wait on the single lane within that
   bound and both publish valid handles.
8. Prove one session exhausting its byte quota does not consume another
   session's quota or disable that sibling's healthy capture.
9. Remove unneeded capability/provenance machinery rather than recreating it.

Exit: retained tools are bounded and cannot reduce core invoke availability.

### Slice 9 — install, rollback, and real smoke

1. Package the single public supervisor executable, its internal worker mode,
   and only required native containment helpers.
2. Stage and validate before replacing the installed payload.
3. Keep registrations unchanged until the staged package passes initialize,
   tools/list, unqualified default invocation, two explicitly opened named
   sessions with isolated variables and same-named functions, per-session
   state persistence across two calls, reset of one session without changing
   the other, close, and public EOF cleanup.
4. On any failure, restore byte-identical prior payload and registrations.
5. Remove guardian/private-host snippets and package expectations.

Exit: an installed package, not a build-tree process, passes the real smoke and
rollback fault matrix.

### Slice 10 — production acceptance

Run at one exact committed SHA on macOS, x64 Linux, and Windows:

- complete Pester, .NET, and stdio handshake verification;
- clean ARM64 Linux server restore/build proving no audit protobuf toolchain is
  required;
- one agent-owned PTK server process with at least `exchange-onprem` and
  `exchange-online` open concurrently, distinct worker PIDs, deliberately
  overlapping function/cmdlet names, distinct variables/modules/directories,
  and successful concurrent calls;
- at least two simultaneous agent-owned PTK server processes with distinct
  session registries, variables, modules, working directories, and successful
  concurrent calls;
- worker hard-kill before the first write call, after write-call entry, during
  execution, after effect, during result, and after complete terminal decode;
- timeout with a child and grandchild process;
- Unix process-group escape reported as `descendants_unknown`, without replay
  or a false complete-containment claim;
- 100 sequential replacements of one named session after one warm-up cycle,
  while a sibling session remains warm and usable: live PTK process count
  returns exactly to baseline, open handles/fds return to baseline plus at most
  four, private memory settles within the larger of 10% or 32 MiB over
  baseline, no measured resource grows monotonically over the final 20 cycles,
  and the sibling worker PID and state never change;
- a lifecycle guard proving no idle watchdog/timer is registered; do not build
  a clock abstraction or spend four wall-clock hours on an acceptance wait;
- malformed and oversized worker frames;
- stale output-root reclamation with a simultaneous live supervisor root;
- two healthy concurrent sessions both publishing handles, one session
  exhausting its output quota while a sibling still publishes a healthy
  handle, and one deliberately wedged global storage operation whose later
  contenders time out within the capture bound before their commands run;
- prompt selected-session `ptk_state` and supervisor-local `ptk_session list`
  during active invokes in other sessions, worker loss, startup, recovery, and
  fault, with every worker-owned field either populated or explicitly
  unavailable;
- strict unknown-name, closed-name, invalid-name, and ninth-open-session
  refusals before worker creation or command dispatch;
- supervisor hard-kill leaving no session worker or PTK-contained descendant;
- installer activation and rollback faults;
- a staged real Exchange workflow on a supported Windows admin host: import and
  connect the on-prem Exchange tooling in `exchange-onprem`, import and connect
  Exchange Online in `exchange-online`, prove each remains warm across calls,
  prove overlapping cmdlet names resolve to the intended module/connection,
  then hard-kill or reset `exchange-online` and prove the existing on-prem
  worker PID and remote connection remain usable without reauthentication;
- a staged real workflow proving no state leakage between two independent
  agent-owned server processes;
- a real EXO/Outlook shaping check closing the known compressor blocker;
- direct Windows runs closing the known worker/Job Object kill-path failures;
- an intended-harness restart proving a dead PTK transport is replaced rather
  than reused.

Hosted CI is supporting evidence, not a substitute for direct platform runs.
Record exact commands, SHAs, test counts, identities, and residue cleanup in
`.agents/machines.md`.

Exit: all required evidence green, no unexplained failure, no skipped
platform-specific behavior, and no known production blocker.

### Slice 11 — documentation and integration

1. Update `README.md`, `server/README.md`, `.agents/state.md`, and active plan
   pointers to the implemented one-supervisor/many-session-workers topology.
2. Reduce `server/AUDIT-EXPORT.md` to retained local journal/evidence
   administration, legacy checkpoint disposition, and the receiver-side
   wire/ack contract. Remove or mark superseded every producer-enablement and
   anchored-startup instruction, then update `README.md` and
   `server/README.md` so neither claims the runtime can enable anchored export.
   Record `.agents/decisions.md:312` as known stale producer evidence under the
   existing owner hold; do not edit it in this slice.
3. Mark `.agents/plans/mcp-resilience.md` superseded by this plan without
   deleting its historical evidence.
4. Do not update `.agents/decisions.md` while its owner hold remains.
5. Do not edit the toolkit-owned `AGENTS.md` copy. Route removal of
   `background=true`/`ptk_job` guidance to the governance toolkit owner, then
   use the normal governance refresh after that upstream change is available.
6. Reconcile current `origin/master`, rerun affected verification, integrate
   only the verified salvage branch, and prove content arrival.
7. Preserve the old resilience branch until separate deletion approval.

Exit: repository guidance can no longer direct an agent back to the discarded
guardian/private-host architecture or advertise the deleted anchored producer.

## Verification entry points

Every code slice runs the relevant focused tests plus the repository battery:

```powershell
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
dotnet test server/PtkMcpServer.slnx
dotnet test siem/PtkSiem.slnx
pwsh -NoProfile -File server/test-handshake.ps1
```

Server-facing slices run the handshake manually even if the test suite is
green. Platform-specific containment changes require direct execution on that
platform. A new test must fail when its product change is temporarily reverted
and pass again after restoration.

## Deployment gates

No production use or default registration until:

1. the final plan is owner-approved;
2. every slice is committed and locally green;
3. the exact final SHA passes direct macOS, Linux, and Windows acceptance;
4. the installed-package smoke succeeds from a clean and an upgrade state;
5. rollback is fault-injected and byte-identical;
6. a canary production workflow runs without unexplained failure or state
   leakage;
7. toolkit-owned PTK guidance no longer tells agents to call the removed job
   surface; and
8. all known failures are fixed or explicitly ruled outside PTK's control.

The production rollout starts with a reversible canary registration. Public
release assets and deletion of the old resilience branch remain separately
gated.

## Owner decisions

Present and settle these in chat one at a time before implementation:

1. **Topology — APPROVED 2026-07-26:** one agent-owned MCP connection owns one
   public supervisor and may own several explicitly named warm sessions. Every
   session is a separate long-lived PowerShell worker process, analogous to one
   `powershell.exe` per interactive window. This is required for real workflows
   that must keep on-prem Exchange and Exchange Online modules, overlapping
   cmdlet names, connections, and module/process state isolated. Unrelated
   agents still require separate MCP connections because PTK receives no
   trustworthy caller identity; sharing one connection between unrelated
   agents remains unsupported. No shared or durable session is approved.
2. **R0 contract retirement:** approve retirement of the frozen guardian-era
   public-contract digest, package-role guards, schemas, the
   guardian/private-host-only `PtkResilienceTestFixture`, and its two consuming
   tests before freezing the replacement contract. Preserve the separate,
   already guardian-free `PtkContainmentTestFixture` and its Windows
   containment tests unchanged. Recommendation: yes, because the retired
   guards and fake fixture freeze the topology this plan rejects, while the
   actual containment fixture protects code this plan keeps. If declined,
   public schema changes and implementation stop.
3. **Cold jobs:** remove `ptk_job` and `ptk_invoke(background=true)` from the
   first production surface while retaining foreground invoke/state/reset/
   output. Recommendation: yes, because cold jobs preserve no warm state and
   otherwise blur worker ownership. If declined, stop and add a separately
   reviewed worker-owned job design before coding.
4. **Audit:** approve removal of mandatory exact-script audit from the default
   execution path and removal of the runtime server project's OTLP
   protobuf/`Grpc.Tools` build dependency. Relocate the vendored OTLP wire
   contract and its license into the retained SIEM receiver, which keeps its
   own protobuf tooling and tests. Remove the complete anchored OTLP export
   path — mapper/exporter, transport interface and every runtime consumer,
   export loop, export-only identity/health/transition branches, fixtures, and
   tests — while preserving unrelated local journal/evidence administration
   only where a non-OTLP caller remains. Retire the core producer-to-SIEM
   conformance project/CI step that consumes those types; keep the standalone
   SIEM receiver tests parked; and retain `SecureAuditStorage` only as
   `OutputStore`'s proved local-storage primitive. Any future compliance
   producer/exporter is separately built and explicitly approved.
   Recommendation: yes, because the current gate has already disabled valid
   execution and the build dependency blocks clean ARM64 Linux builds. If
   declined, those availability and build failures remain accepted production
   blockers.

Silence approves none of the pending decisions. Until decisions 2-4 are settled
and the owner later gives an explicit implementation go, implementation is
blocked and only plan/review work may proceed.

Canary activation remains a separately gated outward action after
implementation and verification. It is not a design decision and is never
authorized by plan approval alone.

## Review requirement

Before owner approval, dispatch one read-only, headless
`claude-opus-5` plan review at maximum effort over the exact committed plan
SHA. The reviewer may inspect the repository but may not edit, commit, push, or
make network mutations. It must evaluate whether this is the simplest safe
path to the stated reliability, explicit multi-session module isolation, and
cross-agent isolation goals; verify that one failed or reset session cannot
damage a sibling; adjudicate the deliberate single fail-fast output-storage
lane against the rejected per-session-lane remedy; identify material omissions
or unnecessary mechanisms; and return evidence-backed findings.

Record the exact Claude Code version, model, effort, reviewed SHA, prompt,
verdict, and findings under `.agents/review/`. Amend the plan for admitted
findings and re-review the exact amended SHA so the owner receives a reviewed
final draft, not a stale verdict.
