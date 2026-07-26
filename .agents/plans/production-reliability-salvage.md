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
- isolated state for unrelated agents or logical sessions;
- truthful, bounded failure behavior;
- recovery from a crashed or timed-out PowerShell worker without replaying an
  uncertain command.

The reliability contract is not literal uninterrupted availability. The OS,
the MCP client, credentials, remote services, storage, and the public stdio
connection can fail outside PTK's control. PTK's enforceable contract is:

1. never report success without a complete result;
2. never execute one public request more than once;
3. never silently share warm state between distinct PTK sessions;
4. never overlap an old worker tree with its replacement;
5. return a precise no-start, outcome-unknown, or completed result;
6. remain able to report supervisor and session health while a worker is lost;
7. keep optional operational features from disabling ordinary execution.

## Current repository facts

- `master` is the only implementation base. At plan time it is
  `c9b11bcb0b4e41a11110c5870562b4980c0b86b3`.
- The experimental resilience line is retained at
  `feature/mcp-resilience-r1`, head
  `93e79922a77bd5aab8e2959c69958dd165ea5087`.
- The resilience line is not merged into `master`. It changes 327 files with
  107,482 insertions and 3,542 deletions while changing no file in
  `src/PwshTokenCompressor.*` or its Pester suite.
- The resilience line adds a public guardian, a private host, per-session
  workers, two private protocol layers, recovery circuits, generation
  catalogs, package pinning, and guardian ownership of audit/output/job state.
- `master` already contains non-core systems that predate the resilience line:
  mandatory exact-script audit evidence, audit export/SIEM support, cold
  background jobs, output handles, and partial worker-process scaffolding.
- Current repository guidance and `README.md` describe the three-process
  guardian topology as an approved target. That record conflicts with the
  owner's current direction and must be corrected only after this replacement
  plan is approved.

The resilience branch is evidence and a source of individually reusable code
and tests. It is never merged, rebased, or used as the base of this effort.

## Proposed target topology

One public PTK supervisor exists per MCP stdio connection. It launches one
replaceable PowerShell worker process per logical PTK session:

```text
MCP connection
└── PtkMcpServer supervisor
    ├── session "default" -> worker process -> one warm PowerShell runspace
    ├── session "agent-a" -> worker process -> one warm PowerShell runspace
    └── session "agent-b" -> worker process -> one warm PowerShell runspace
```

There is no public guardian/private-host split and no shared machine daemon.
The installed executable may use its existing internal `--worker` entry so the
package remains one managed application plus a platform-specific Unix
containment helper where required.

### Supervisor ownership

The public supervisor owns only:

- the original MCP stdin/stdout connection and frozen tool schemas;
- one connection-local session registry;
- public request correlation and one-response delivery;
- worker process creation, monitoring, cancellation, and containment;
- small connection-local job/output registries where those tools remain;
- health projection for `ptk_state`;
- monotonic, connection-local worker incarnation numbers.

It loads no user script into an in-process runspace and executes no submitted
PowerShell, RTK, Bash, or native command.

### Worker ownership

Each worker owns:

- exactly one warm `SessionRuntime` and PowerShell runspace;
- the selected session's modules, variables, functions, directory,
  environment drift, credentials/connections, and foreground serialization;
- command planning and PowerShell/RTK/validated-Bash execution;
- bounded output production;
- child processes created for that session.

Different workers may execute concurrently. Foreground calls within one worker
remain serialized.

### Session identity

- `default` is private to one MCP connection. Separate MCP server processes
  never share it.
- A harness that multiplexes several logical agents over one MCP connection
  must supply a stable explicit `session` name for each agent. MCP does not
  expose a trustworthy caller identity PTK can infer.
- `ptk_session open|list|close|restart` manages explicit sessions.
- Unknown or closed names fail; they never fall back to `default`.
- Sharing happens only when callers intentionally use the same explicit
  session name.
- A configurable, bounded maximum session count prevents accidental process
  exhaustion. Reaching the bound refuses a new session without changing any
  existing session.
- No idle timer recycles a live connection's warm worker by default. Public
  EOF or explicit close/reset/restart owns teardown.

## Minimal worker protocol

Keep one strict, bounded supervisor-to-worker protocol. It is internal to the
server project; no separate `PtkSharedContracts` project is required.

Required message kinds:

1. `initialize` / `ready`, binding protocol version, session name, worker
   incarnation, and immutable limits;
2. `invoke`, carrying one bounded strict-UTF-8 script and route/background
   options;
3. `cancel`, naming one active request;
4. `result`, carrying exactly one completed, refused, cancelled, timed-out, or
   failed terminal;
5. `job_terminal` only if cold jobs remain worker-originated;
6. `shutdown` / `stopped`.

The frame reader rejects invalid UTF-8, duplicate or unknown fields, wrong
versions, stale incarnations, oversized frames, and unsolicited terminals. A
worker executes only after a complete valid request frame is decoded.

Do not port the resilience branch's prepare/descriptor/commit/abort protocol.
The supervisor uses one conservative delivery boundary:

- before it writes any request byte, failure is proved-no-start;
- after it writes any request byte and before a complete valid terminal is
  decoded, failure is `outcome_unknown`;
- after a complete valid terminal is decoded, that terminal is delivered once
  even if the worker exits immediately afterward.

This deliberately gives up some retryable classifications to remove a large
state machine without weakening correctness. PTK never automatically resends a
public invoke.

## Failure and recovery contract

### Unexpected worker loss

1. Atomically mark only that session unavailable.
2. Terminalize its active request using the delivery boundary above.
3. Stop admitting new work for that session; other sessions remain usable.
4. Kill and confirm the complete old worker tree.
5. Only after confirmed death, allocate the next connection-local incarnation
   and make one immediate replacement attempt.
6. A successful replacement becomes `Ready` with
   `warm_state_lost=true` and an empty, sound runspace.
7. A failed replacement marks the session `Faulted`. No automatic retry loop
   runs. An explicit `ptk_reset` or `ptk_session restart` makes one new
   replacement attempt.

No modules, variables, credentials, connections, profiles, or previous calls
are replayed automatically.

### Timeout

- A timeout requests cancellation, then terminates the complete worker tree if
  execution does not stop within the configured containment grace.
- If the worker proves no command started, the result is a retryable no-start.
- Otherwise the call is nonretryable `outcome_unknown` unless a complete
  timeout terminal was already decoded.
- The replacement rules are identical to unexpected worker loss.

### Supervisor or public-pipe loss

Supervisor death or public EOF ends the MCP connection and every worker owned
by it. PTK cannot repair dead stdio endpoints in-process. The harness must
start a fresh MCP server. Installation and client guidance must state this
boundary plainly.

### State projection

`ptk_state` remains supervisor-local and reports, per session:

- `Cold`, `Starting`, `Ready`, `Recovering`, `Faulted`, or `Closed`;
- worker PID and incarnation where one exists;
- whether warm state was lost;
- current active request/job counts;
- the last bounded failure class;
- whether explicit reset/restart is required.

Calls during `Starting`, `Recovering`, `Faulted`, or `Closed` fail promptly.
They are never queued for later execution.

## Operational features

### Audit and exact-script evidence

Mandatory audit is not part of the proposed production-critical path.
`master` currently writes exact submitted scripts under
`~/.ptk/audit/evidence` and can disable all effects when that storage is
unavailable or contains an unknown artifact. That coupling directly conflicts
with the availability goal.

The proposed core:

- does not persist exact submitted scripts by default;
- does not make audit, SIEM, export, or evidence retention a prerequisite for
  execution;
- removes audit health from the ordinary invoke gate;
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

### `ptk_job`

Keep cold background jobs only as stateless child processes. They do not borrow
a warm session's state. Their process trees remain supervisor-owned and die on
kill or MCP connection teardown. Warm asynchronous runspace jobs remain out of
scope.

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
- the current public invoke/state/reset/job/output contracts unless a later
  owner decision removes an optional tool;
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
  tree death, stale-incarnation rejection, and output transfer;
- the public session shape only if the topology owner decision retains
  multiplexing; it is rewritten in the server project rather than ported from
  the frozen `PtkSharedContracts` contract.

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

- a connection-local `SessionRegistry`;
- a `WorkerSupervisor` owning one worker slot per session;
- direct public-tool adapters from MCP requests to the selected worker;
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
3. Record the exact base SHA and reject any candidate whose value cannot be
   separated from guardian/private-host policy more cheaply than a narrow
   rewrite.

Exit: clean baseline and exact base SHA. No runtime behavior changes.

### Slice 1 — remove audit from the execution gate

1. Add a failing integration test proving a valid invoke succeeds when the
   audit root is absent or unwritable.
2. Remove mandatory audit admission and exact-script evidence publication from
   ordinary tool execution.
3. Remove default startup construction of audit/SIEM/export resources.
4. Ensure `ptk_state` remains usable and truthfully says audit is not enabled
   rather than reporting a false protected boundary.
5. Keep any retained audit administration executable out of the installed
   runtime package pending a separate product decision.

Exit: no ordinary invoke depends on `~/.ptk/audit`; no exact script file is
created by default; full verification green.

### Slice 2 — minimal worker protocol

1. Freeze the minimal message union and strict bounds in server-local tests.
2. Reuse or rewrite the smallest existing worker codec that meets the frozen
   contract.
3. Bind one `SessionRuntime` behind the worker server in an unwired test
   fixture.
4. Prove fragmented/coalesced input, malformed UTF-8/JSON, stale incarnation,
   duplicate request IDs, cancellation, and exactly one terminal.

Exit: worker protocol is live only in a disposable fixture; public MCP behavior
is unchanged.

### Slice 3 — cross-platform worker launch and containment

1. Port the worker-only Unix broker/launcher and Windows creation-time Job
   Object launcher without the outer guardian/host registry.
2. Bind liveness to the public supervisor so supervisor death or EOF kills the
   worker tree.
3. Prove worker, direct child, and grandchild death on normal shutdown, reset,
   timeout, and hard supervisor termination.
4. Confirm no replacement starts until old-tree death is proved.

Exit: one disposable contained worker can be launched and killed on macOS,
Linux, and Windows; public MCP behavior is unchanged.

### Slice 4 — connection-local session registry

1. Add bounded session names and a bounded maximum count.
2. Add one worker slot, incarnation counter, lifecycle state, and operation
   lock per session.
3. Implement `ptk_session open|list|close|restart` and the optional `session`
   argument without routing real invokes yet.
4. Prove unknown/closed names never fall back to `default`, two sessions never
   share state, and different sessions may progress concurrently.

Exit: lifecycle and isolation are proven with fixture workers; current default
invoke path remains intact.

### Slice 5 — production cutover to workers

1. Route default and named foreground invokes through their selected worker.
2. Route state/reset and retained job/output operations through the smallest
   correct owning layer.
3. Remove the in-process production runspace path in the same slice; no dual
   execution mode remains.
4. Preserve exact public schemas except the already-planned session additions.
5. Prove one submitted script executes once and its PowerShell objects reach
   the unchanged compressor.

Exit: all production PowerShell work runs only in contained session workers;
full verification and handshake green.

### Slice 6 — truthful loss and one-attempt recovery

1. Implement the delivery boundary and one-response terminal ownership.
2. Implement confirmed-death-before-replacement.
3. Make one automatic replacement attempt, then fault the session until
   explicit restart/reset.
4. Prove no replay at every pre-write, partial-write, executing, terminal, and
   post-terminal death point.
5. Prove one session's crash cannot change another session's PID,
   incarnation, warm state, or successful operation.

Exit: real apphost fault matrix green on every supported platform.

### Slice 7 — optional jobs and output continuity

1. Retain only job/output behavior that remains independent of mandatory
   audit and discarded guardian capabilities.
2. Prove capture failure never blocks invoke or causes replay.
3. Prove connection teardown kills every cold job and removes unsealed
   temporary output.
4. Remove unneeded capability/provenance machinery rather than recreating it.

Exit: retained tools are bounded and cannot reduce core invoke availability.

### Slice 8 — install, rollback, and real smoke

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

### Slice 9 — production acceptance

Run at one exact committed SHA on macOS, x64 Linux, and Windows:

- complete Pester, .NET, and stdio handshake verification;
- at least two simultaneous isolated sessions with distinct variables,
  modules, working directories, and successful concurrent calls;
- worker hard-kill before write, during execution, after effect, during result,
  and after complete terminal decode;
- timeout with a child and grandchild process;
- 100 sequential worker replacements with bounded process/handle/fd and memory
  growth;
- public connection held idle beyond every former watchdog interval;
- malformed and oversized worker frames;
- supervisor hard-kill leaving no worker descendant;
- installer activation and rollback faults;
- a staged real workflow proving module/connection warmth across calls and no
  cross-session state leakage.

Hosted CI is supporting evidence, not a substitute for direct platform runs.
Record exact commands, SHAs, test counts, identities, and residue cleanup in
`.agents/machines.md`.

Exit: all required evidence green, no unexplained failure, no skipped
platform-specific behavior, and no known production blocker.

### Slice 10 — documentation and integration

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

1. **Topology:** approve one public supervisor per MCP connection with one
   worker per explicit PTK session, and no private host/guardian layer.
2. **Audit:** approve removal of mandatory exact-script audit from the default
   execution path; any future compliance audit is separate and explicit.
3. **Sessions:** approve `default` as connection-private and explicit session
   names as mandatory when one MCP connection multiplexes agents.
4. **Optional tools:** confirm whether cold `ptk_job` and connection-local
   `ptk_output` remain production features.
5. **Rollout:** approve a canary installed-package validation before replacing
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
