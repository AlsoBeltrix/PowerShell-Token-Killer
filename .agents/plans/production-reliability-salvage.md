# Plan: production reliability salvage

**Status:** DRAFT — owner requested the plan and an independent
`claude-opus-5` review on 2026-07-26. No implementation is authorized by this
document. The design choices under `Owner decisions` remain proposals until
approved in chat, one at a time.

## Goal

Make PTK a dependable production execution service for AI agents while
preserving its actual product:

- compact PowerShell and native-command output;
- warm PowerShell state across calls;
- isolated state for unrelated agents through separate agent-owned MCP
  connections;
- truthful, bounded failure behavior;
- recovery from a crashed or timed-out PowerShell worker without replaying an
  uncertain command.

The reliability contract is not literal uninterrupted availability. The OS,
the MCP client, credentials, remote services, storage, and the public stdio
connection can fail outside PTK's control. PTK's enforceable contract is:

1. never report success without a complete result;
2. never send one accepted request to a worker more than once;
3. never share warm state between supported agent-owned connections;
4. never overlap a PTK-contained old worker with its replacement;
5. return a precise no-start, outcome-unknown, or completed result;
6. remain able to report supervisor and connection-worker health while a
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

## Proposed target topology

The first production topology requires one agent-owned MCP stdio connection.
That connection owns one public PTK supervisor and one replaceable PowerShell
worker:

```text
agent A -> MCP connection A -> PTK supervisor A -> worker A -> warm runspace A
agent B -> MCP connection B -> PTK supervisor B -> worker B -> warm runspace B
```

There is no public guardian/private-host split and no shared machine daemon.
The installed executable may use its existing internal `--worker` entry so the
package remains one managed application plus a platform-specific Unix
containment helper where required.

On Unix the outer worker broker is the sole process-group owner. Commands
executed inside that worker inherit its group; `ProcessTreeContainment` must not
try to create a second exclusive group for each command. Windows continues to
use the worker's creation-time Job Object as the owning containment domain.

### Supervisor ownership

The public supervisor owns only:

- the original MCP stdin/stdout connection and frozen tool schemas;
- one connection-owned worker slot;
- public request correlation and one-response delivery;
- worker process creation, monitoring, cancellation, and containment;
- active-call admission/drain and ordered connection shutdown;
- a small connection-local output registry;
- health projection for `ptk_state`.

The supervisor also keeps a monotonic connection-local worker incarnation as
an internal stale-frame key. It is not part of the public state surface because
no public operation can act on it.

It loads no user script into an in-process runspace and executes no submitted
PowerShell, RTK, Bash, or native command.

### Worker ownership

The connection's worker owns:

- exactly one warm `SessionRuntime` and PowerShell runspace;
- the connection's modules, variables, functions, directory,
  environment drift, credentials/connections, and foreground serialization;
- command planning and PowerShell/RTK/validated-Bash execution;
- bounded output production;
- child processes created for that connection's work.

Workers belonging to separate MCP connections may execute concurrently.
Foreground calls within one worker remain serialized.

### Connection identity and support boundary

- MCP does not expose a trustworthy agent identity that PTK can infer.
- Therefore the first production cut does not add named sessions or
  `ptk_session`, and it does not claim to isolate agents multiplexed over one
  MCP connection.
- A supported harness gives each unrelated agent its own MCP connection/server
  process. If an integration shares one connection, all callers share that
  connection's one warm runspace by definition and the integration is
  unsupported for unrelated agents.
- Slice 0 must prove the intended production harness actually supplies a
  distinct PTK server PID and stdio connection per agent. If it does not, this
  plan stops before runtime changes; a separately approved harness-identity
  design is required.
- No idle timer recycles a live connection's warm worker by default. Public
  EOF or explicit reset owns teardown.

## Minimal worker protocol

Keep one strict, bounded supervisor-to-worker protocol. It is internal to the
server project; no separate `PtkSharedContracts` project is required.

Required message kinds:

1. `initialize` / `ready`, binding protocol version, connection worker
   incarnation, and immutable limits;
2. `invoke`, carrying one bounded strict-UTF-8 script and route/background
   options;
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

1. Atomically mark the connection worker unavailable.
2. Terminalize its active request using the delivery boundary above.
3. Stop admitting new work on that connection. Other agents' separate PTK
   connections remain usable.
4. Kill the worker and sweep PTK's containment domain: the Windows Job Object
   or the Unix broker-owned process group.
5. Only after the worker has exited and that owned containment domain is
   confirmed empty, allocate the next connection-local incarnation and make
   one immediate replacement attempt.
6. A successful replacement becomes `Ready` with
   `warm_state_lost=true` and an empty, sound runspace.
7. A failed replacement marks the connection worker `Faulted`. No automatic
   retry loop runs. An explicit `ptk_reset` makes one new replacement attempt.

No modules, variables, credentials, connections, profiles, or previous calls
are replayed automatically.

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

`ptk_state` remains prompt and always returns a supervisor-owned section:

- `Cold`, `Starting`, `Ready`, `Recovering`, `Faulted`, or `Closed`;
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

New execution calls during `Starting`, `Recovering`, `Faulted`, or `Closed`
fail promptly. They are never queued for later execution.

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
  project; any retained exporter moves to a separately built optional product;
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
connection-local and expire by bounded memory/disk quota.

Each supervisor uses a uniquely owned output root with a creation identity and
exclusive live-owner marker. Startup reclaims only roots whose recorded owner
is provably dead; it never scans or deletes another live supervisor's root.
Normal connection teardown removes its own sealed and unsealed residue.

Before an invoke, the supervisor reserves one connection-local artifact ID.
The worker sends the exact recoverable output as monotonically ordered,
individually bounded chunks, then a seal with the total length and digest.
The supervisor rejects gaps, duplicates, wrong digests, and quota overflow,
publishes an immutable public handle only after a valid seal, and marks a
partially transferred artifact explicitly incomplete. The ordinary bounded
result remains independent of artifact publication; a transfer failure never
causes command resubmission.

### `ptk_job`

Remove cold `ptk_job` from the first production surface. It does not preserve
warm runspace state, and making the supervisor own submitted job execution
violates the minimal ownership boundary. Do not port or recreate its
guardian-era capability machinery.

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
- the current public invoke/state/reset/output contracts;
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
  tree death, stale-incarnation rejection, and output transfer.

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

- a `WorkerSupervisor` owning the connection's single worker slot;
- a supervisor-owned Unix containment registry replacing the branch's
  private-host-only registry;
- a Unix branch in `WorkerProcessEntry.RunAsync` that validates and opens the
  Unix bootstrap/IPC handles before starting `WorkerServer`;
- direct public-tool adapters from MCP requests to that worker;
- minimal state projection and one-attempt replacement;
- development installer smoke/rollback around the single supervisor package.

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
- the branch's public session schema, `ptk_session`, and multi-session registry;
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
3. Run a no-product-change production-harness probe and record whether two
   unrelated agents receive distinct PTK server PIDs and stdio connections.
   If they do not, stop: shared-connection multi-agent use is unsupported by
   this plan.
4. Record the exact base SHA and reject any candidate whose value cannot be
   separated from guardian/private-host policy more cheaply than a narrow
   rewrite.

Exit: clean baseline, exact base SHA, and evidence that the intended harness
gives each unrelated agent an independent PTK connection. No runtime behavior
changes.

### Slice 1 — retire the frozen R0 guardian contract

This slice is owner-gated because the current guards record an approved
guardian-era contract; they are not ordinary obsolete tests to delete around.

1. Replace the five-tool freeze in `ToolSchemaConformanceTests` with the
   owner-approved first-cut tool surface.
2. Remove or re-freeze `McpResilienceR0ContractTests`, the embedded R0 public
   contract and digest, recovery schemas/examples, package-role assertions,
   and native/helper inventories so they describe only the approved topology.
3. Remove `PtkResilienceTestFixture` from the solution and delete guardian-only
   test and native-fixture dependencies that no retained runtime path uses.
4. Add a focused conformance guard for the resulting supported surface before
   any public schema or tool list changes land.

Exit: no guardian-era contract or fixture claims to be the active production
surface, and the replacement conformance guard passes. No runtime execution
path changes in this slice.

### Slice 2 — remove audit from the execution gate

1. Add a failing integration test proving a valid invoke succeeds when the
   audit root is absent or unwritable.
2. Extract active-call admission/drain, ordered shutdown, and the connection
   activity clock from `AuditRuntimeGate` into a small audit-independent
   supervisor lifecycle service. Move runtime creation to `WorkerSupervisor`.
3. Prove shutdown stops new admission, cancels/drains the active request, and
   tears down the worker before the public process exits.
4. Remove mandatory audit admission and exact-script evidence publication from
   ordinary tool execution.
5. Remove default startup construction of audit/SIEM/export resources and the
   runtime project's OTLP protobuf/`Grpc.Tools` build dependency.
6. Prove a clean ARM64 Linux restore/build no longer enters the removed protoc
   path.
7. Ensure `ptk_state` remains usable and truthfully says audit is not enabled
   rather than reporting a false protected boundary.
8. Keep any retained audit administration executable out of the installed
   runtime package pending a separate product decision.

Exit: no ordinary invoke depends on `~/.ptk/audit`; no exact script file is
created by default; full verification green.

### Slice 3 — minimal worker protocol

1. Freeze the minimal message union and strict bounds in server-local tests.
2. Reuse or rewrite the smallest existing worker codec that meets the frozen
   contract.
3. Bind one `SessionRuntime` behind the worker server in an unwired test
   fixture.
4. Prove fragmented/coalesced input, malformed UTF-8/JSON, stale incarnation,
   duplicate request IDs, cancellation, bounded state snapshots, unavailable
   busy-state diagnostics, ordered artifact chunks, seal digest/length
   validation, and exactly one terminal.

Exit: worker protocol is live only in a disposable fixture; public MCP behavior
is unchanged.

### Slice 4 — cross-platform worker launch and containment

1. Port the worker-only Unix broker/launcher and Windows creation-time Job
   Object launcher without the outer guardian/host registry.
2. Implement the supervisor-owned Unix containment registry and wire
   `WorkerProcessEntry.RunAsync` to consume `UnixWorkerBootstrap`, validate
   inherited handles, remove bootstrap variables, and open the worker IPC
   channel.
3. Add an internal, validated worker-containment mode that makes
   `ProcessTreeContainment` reuse the broker-owned group. Prove it performs no
   nested `setpgid`/`setsid` ownership attempt and every ordinary direct child
   inherits the worker group.
4. Bind liveness to the public supervisor so supervisor death or EOF kills the
   worker and its PTK-owned containment domain.
5. Prove worker, direct child, and grandchild death on normal shutdown, reset,
   timeout, and hard supervisor termination.
6. Confirm no replacement starts until the old worker has exited and its
   Windows Job Object or Unix broker process group is empty.
7. On Unix, deliberately escape the process group where the platform permits
   and prove PTK reports `descendants_unknown` rather than claiming complete
   descendant death.

Exit: one disposable contained worker can be launched and killed on macOS,
Linux, and Windows; public MCP behavior is unchanged.

### Slice 5 — connection-owned worker lifecycle

1. Add one worker slot, internal incarnation counter, lifecycle state, and
   foreground operation lock to the public supervisor.
2. Bind the slot to the lifetime of the MCP stdio connection.
3. Prove a fixture connection owns exactly one runspace, a second supervisor
   owns a different runspace, and reset/replacement cannot cross the process
   boundary.

Exit: connection ownership and cross-process isolation are proven with fixture
workers; the current invoke path remains intact.

### Slice 6 — production cutover to workers

1. Route foreground invokes through the connection's worker.
2. Keep supervisor state and reset local; obtain worker state only through the
   bounded idle-worker query above. Keep output recovery in the supervisor.
3. Remove the in-process production runspace path in the same slice; no dual
   execution mode remains.
4. Preserve the existing public schema; add no session argument or
   `ptk_session` tool.
5. Prove one submitted script executes once and its PowerShell objects reach
   the unchanged compressor.

Exit: all production PowerShell work runs only in the connection's contained
worker;
full verification and handshake green.

### Slice 7 — truthful loss and one-attempt recovery

1. Implement the write-attempt boundary and one-response terminal ownership,
   including a fault injected at entry to and failure return from the first
   write call.
2. Implement confirmed worker exit and owned-containment sweep before
   replacement.
3. Make one automatic replacement attempt, then fault the session until
   explicit restart/reset.
4. Prove no replay at every pre-write, partial-write, executing, terminal, and
   post-terminal death point.
5. Prove one session's crash cannot change another session's PID,
   incarnation, warm state, or successful operation.

Exit: real apphost fault matrix green on every supported platform.

### Slice 8 — output continuity

1. Retain only output behavior that remains independent of mandatory audit and
   discarded guardian capabilities.
2. Implement the reserved artifact ID, chunk, seal, and immutable-publication
   path described above.
3. Prove wrong order, duplicate/gapped chunks, digest mismatch, quota overflow,
   worker loss mid-transfer, and capture failure never cause replay or a false
   complete handle.
4. Prove connection teardown removes its own output root, hard supervisor death
   leaves bounded residue, and the next startup reclaims that stale root without
   touching a simultaneously live supervisor's root.
5. Remove unneeded capability/provenance machinery rather than recreating it.

Exit: retained tools are bounded and cannot reduce core invoke availability.

### Slice 9 — install, rollback, and real smoke

1. Package the single public supervisor executable, its internal worker mode,
   and only required native containment helpers.
2. Stage and validate before replacing the installed payload.
3. Keep registrations unchanged until the staged package passes initialize,
   tools/list, a real `ptk_invoke`, state persistence across two calls, and
   reset.
4. On any failure, restore byte-identical prior payload and registrations.
5. Remove guardian/private-host snippets and package expectations.

Exit: an installed package, not a build-tree process, passes the real smoke and
rollback fault matrix.

### Slice 10 — production acceptance

Run at one exact committed SHA on macOS, x64 Linux, and Windows:

- complete Pester, .NET, and stdio handshake verification;
- clean ARM64 Linux server restore/build proving no audit protobuf toolchain is
  required;
- at least two simultaneous agent-owned PTK server processes with distinct
  variables, modules, working directories, and successful concurrent calls;
- worker hard-kill before the first write call, after write-call entry, during
  execution, after effect, during result, and after complete terminal decode;
- timeout with a child and grandchild process;
- Unix process-group escape reported as `descendants_unknown`, without replay
  or a false complete-containment claim;
- 100 sequential worker replacements after one warm-up cycle: live PTK process
  count returns exactly to baseline, open handles/fds return to baseline plus
  at most four, private memory settles within the larger of 10% or 32 MiB over
  baseline, and no measured resource grows monotonically over the final 20
  cycles;
- public connection held idle beyond every former watchdog interval;
- malformed and oversized worker frames;
- stale output-root reclamation with a simultaneous live supervisor root;
- prompt `ptk_state` during an active invoke, worker loss, startup, recovery,
  and fault, with every worker-owned field either populated or explicitly
  unavailable;
- supervisor hard-kill leaving no worker or PTK-contained descendant;
- installer activation and rollback faults;
- a staged real workflow proving module/connection warmth across calls and no
  state leakage between the two server processes.

Hosted CI is supporting evidence, not a substitute for direct platform runs.
Record exact commands, SHAs, test counts, identities, and residue cleanup in
`.agents/machines.md`.

Exit: all required evidence green, no unexplained failure, no skipped
platform-specific behavior, and no known production blocker.

### Slice 11 — documentation and integration

1. Update `README.md`, server guidance, `.agents/state.md`, and active plan
   pointers to the implemented two-layer topology.
2. Mark `.agents/plans/mcp-resilience.md` superseded by this plan without
   deleting its historical evidence.
3. Do not update `.agents/decisions.md` while its owner hold remains.
4. Reconcile current `origin/master`, rerun affected verification, integrate
   only the verified salvage branch, and prove content arrival.
5. Preserve the old resilience branch until separate deletion approval.

Exit: repository guidance can no longer direct an agent back to the discarded
guardian/private-host architecture.

## Verification entry points

Every code slice runs the relevant focused tests plus the repository battery:

```powershell
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
dotnet test server/PtkMcpServer.slnx
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
7. all known failures are fixed or explicitly ruled outside PTK's control.

The production rollout starts with a reversible canary registration. Public
release assets and deletion of the old resilience branch remain separately
gated.

## Owner decisions

Present and settle these in chat one at a time before implementation:

1. **Topology:** approve one agent-owned MCP connection, one public supervisor,
   and one worker/runspace, with unrelated-agent multiplexing on a shared
   connection unsupported in the first production cut. Recommendation: yes,
   because PTK receives no trustworthy caller identity and this makes
   isolation an enforceable process boundary. If declined, stop this plan and
   design a separately approved harness identity mechanism before coding.
2. **R0 contract retirement:** approve retirement of the frozen guardian-era
   public-contract digest, package-role guards, schemas, and
   `PtkResilienceTestFixture`, and approve the first-cut public surface as
   invoke/state/reset/output with no `ptk_job`. Recommendation: yes, because
   cold jobs preserve no warm state and otherwise blur worker ownership. If
   this is declined, public schema changes and implementation stop.
3. **Audit:** approve removal of mandatory exact-script audit from the default
   execution path and removal of its OTLP protobuf/`Grpc.Tools` build
   dependency from the runtime server project; any future compliance audit is
   separately built and explicitly approved.
4. **Rollout:** approve a canary installed-package validation before replacing
   the current development registration.

Silence approves none of these. Until decision 1 is settled, implementation is
blocked and only plan/review work may proceed.

## Review requirement

Before owner approval, dispatch one read-only, headless
`claude-opus-5` plan review at maximum effort over the exact committed plan
SHA. The reviewer may inspect the repository but may not edit, commit, push, or
make network mutations. It must evaluate whether this is the simplest safe
path to the stated reliability and multi-agent-isolation goal, identify
material omissions or unnecessary mechanisms, and return evidence-backed
findings.

Record the exact Claude Code version, model, effort, reviewed SHA, prompt,
verdict, and findings under `.agents/review/`. Amend the plan for admitted
findings and re-review the exact amended SHA so the owner receives a reviewed
final draft, not a stale verdict.
