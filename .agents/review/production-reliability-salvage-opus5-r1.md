# Production reliability salvage — Claude Opus 5 review round 1

**Status:** REOPENED — the reviewer returned `REVISE`. Every blocker and major
finding below is admitted into the plan revision, with the B5 containment claim
narrowed as described under `Intake`. No product implementation is authorized.

## Review identity

- Reviewed commit:
  `2b6361a9864fcf35524d424ffd56ceef162e5eda`
- Reviewed plan blob:
  `a6f516ebf3ceb9458c683642090e1aa1e7d27976`
- Parts-bin branch:
  `feature/mcp-resilience-r1` at
  `93e79922a77bd5aab8e2959c69958dd165ea5087`
- Reviewer: Claude Code `2.1.220`, canonical model `claude-opus-5`,
  effort `max`
- Prompt:
  `.agents/review/production-reliability-salvage-opus5-r1.prompt.md`
- Prompt SHA-256:
  `a5862e69eec213546ef17d58d8e3484742367bf572a156c1deaf93796675d84e`
- Invocation: read-only, headless, no session persistence, strict empty MCP
  configuration, detached clean worktree
- Result: exit 0, 64 turns, no repository edit, no verification command run
  because the review was source-only
- Preflight and postflight independently confirmed the exact reviewed SHA and
  a clean main and review worktree. One reviewer `git` command was denied by
  its read-only Bash allow-list; this did not alter the checkout or the verdict.

## Verdict

`REVISE`

The two-process direction is sound: a public MCP supervisor plus a contained
PowerShell worker is materially simpler than the guardian/private-host/worker
line and gives PTK a process it can kill without losing the public pipe.
However, the draft did not enforce the multi-agent isolation the owner asked
for, and several extraction, contract-retirement, state, containment, output,
and job details were not implementable as written.

## Blocking findings

### PRS-B1 — salvage-map provenance is wrong and internally contradictory

The draft classified six files already present on `master` as branch ports:
`WorkerProtocol.cs`, `WorkerOperationProtocol.cs`, `WorkerServer.cs`,
`WorkerProcessEntry.cs`, `WindowsWorkerBootstrap.cs`, and
`WindowsWorkerNative.cs`. `WorkerProtocol.cs` is byte-identical at both heads.
The branch-only `WorkerClient.cs` depends on `PtkSharedContracts` and the
rejected prepare/commit/abort protocol. The Unix containment broker is under
`PtkMcpGuardian/Native/`, despite the blanket instruction not to port that
tree.

Correction: separate the map into `master` code to extend in place,
branch-only code to port/relocate, branch deltas to inspect hunk by hunk, and
rejected code. Rewrite the supervisor-side client instead of porting
`WorkerClient.cs` or `WorkerProcessClient.cs`.

### PRS-B2 — frozen guardian contracts on `master` block the new public surface

`ToolSchemaConformanceTests.cs` freezes five tools.
`McpResilienceR0ContractTests.cs` pins the guardian-era public-contract digest,
package roles, recovery artifacts, and helper set. The solution still includes
`PtkResilienceTestFixture`, and other tests and native fixtures freeze the
discarded guardian topology.

Correction: add an owner-gated contract-retirement slice before any session
surface change. It must remove or re-freeze the obsolete contract artifacts,
tests, fixture project, and solution membership rather than bypassing a guard
whose provenance is owner-approved.

### PRS-B3 — the draft silently shares `default` across multiplexed agents

Today `ISessionOperations` is one singleton and the tools have no caller or
session identity. The draft kept `default` implicit. Two agents sharing one
MCP connection and omitting `session` therefore share modules, credentials,
variables, environment, and working directory. MCP supplies no trustworthy
agent identity with which PTK could detect or warn about this.

Correction: make the real topology choice explicit. The recommended first
production topology is one agent-owned MCP connection, one supervisor, and one
worker/runspace, with agent multiplexing on one connection unsupported until a
harness supplies an enforceable identity. If the owner instead accepts
cooperative multiplexing, the plan must say that equal session values,
including implicit `default`, share by design and cannot be detected by PTK.

### PRS-B4 — current `ptk_state` data cannot be supervisor-local

The public tool currently promises engine, current directory, modules, and
environment/PATH/variable drift, all of which live in the worker. The draft
listed only supervisor state while also promising schema preservation.

Correction: split state into an always-answerable supervisor section and a
worker-sourced section. When the worker is absent or busy, the latter is
explicitly unavailable with a reason; it is never silently omitted.

### PRS-B5 — complete Unix descendant death is not portable or provable

The Unix implementation controls a process group. A descendant can leave that
group with `setsid` or `setpgid`, as `ProcessTreeContainment.cs` already
documents. The draft required proof that the complete tree was dead before any
replacement, which can create a permanent fault while still overstating what
PTK knows.

Correction: scope proof to PTK's containment domain. Replacement waits for the
worker to exit and for the Windows Job Object or Unix broker group to be swept.
Escaped or remote descendants are reported as `descendants_unknown`; PTK does
not claim they died. This is an honesty boundary, not a sandbox guarantee.

## Major findings

### PRS-M1 — audit removal leaves lifecycle duties without an owner

`AuditRuntimeGate` also creates the current session, owns active-call drain and
ordered shutdown, and supplies the idle-watchdog activity clock.

Correction: assign those non-audit duties to a small supervisor lifecycle
service when the audit gate is removed.

### PRS-M2 — cold-job ownership is contradictory

The draft made jobs conditionally worker-originated but supervisor-owned, while
also forbidding the supervisor from executing submitted commands. Current Unix
process-group fallback can also cross session boundaries if jobs share the
supervisor domain.

Correction: remove `ptk_job` from the first production cut unless the owner
explicitly keeps it. If kept later, make it worker-owned with a required job
terminal.

### PRS-M3 — output recovery has no cross-process transfer

Worker result text is bounded far below the supervisor-owned output artifact
cap. Moving invoke into the worker without a transfer protocol would silently
truncate or lose `ptk_output`.

Correction: use bounded ordered artifact-chunk frames followed by a sealed
length/digest terminal. Partial transfer produces an explicitly incomplete
artifact and never reruns the command.

### PRS-M4 — Unix worker wiring needs two explicit rewrites

The branch's Unix launcher has only a private-host registry implementation, and
`WorkerProcessEntry` on `master` opens only the Windows bootstrap.

Correction: add a supervisor-owned Unix containment registry and a Unix branch
in `WorkerProcessEntry.RunAsync` to the narrow rewrite list.

### PRS-M5 — PTK cannot stop a client from resubmitting

No public tool has an idempotency key. A client timeout followed by a new tool
call is a second request.

Correction: promise only that PTK never resends or retries a submitted script;
a client resubmission is a new execution.

### PRS-M6 — the audit build dependency controls the ARM64 Linux blocker

`PtkMcpServer.csproj` compiles the OTLP proto through `Grpc.Tools`, and current
state records a direct ARM64 Linux MSBuild/protoc crash. Merely disabling audit
construction does not remove that build path.

Correction: make removal of the exporter/protobuf build dependency from the
runtime project part of the audit owner decision. If it stays, production
acceptance remains blocked on the clean ARM64 build issue.

### PRS-M7 — worker and command containment can stack

The Unix broker makes the worker a process-group leader, while
`ProcessTreeContainment` independently tries to create an exclusive group for
the process executing commands.

Correction: choose one group owner and add a direct guard proving the inner
containment state under broker launch.

### PRS-M8 — jobs lack a terminal on worker loss

If jobs remain, worker loss must not leave a public job ID permanently running
or eligible for reuse.

Correction: a job owned by a lost worker terminalizes once as
`lost`/`outcome_unknown`, is never restarted, and its ID is never reused within
the connection. This finding becomes deferred with `ptk_job` if PRS-M2 removes
jobs from the first cut.

## Minor findings

1. Correct the branch diffstat from 3,542 to 3,558 deletions, or remove it.
2. Make the delivery boundary write-attempt based; PTK cannot prove how many
   bytes a failed `WriteAsync` transferred.
3. Narrow the stale-topology documentation claim to the root `README.md`;
   `server/README.md` describes the current in-process server.
4. Reclaim stale per-server output roots at startup or state that residue is
   accepted.
5. Give the replacement soak a measurable resource-growth threshold.
6. Retire `PtkResilienceTestFixture` solution membership with PRS-B2.

## Over-engineering cuts admitted

- Do not put named multiplexed sessions in the first production cut unless the
  topology decision proves they are required.
- If named sessions are later approved, omit `ptk_session restart`; scoped
  `ptk_reset` already replaces a worker.
- Do not duplicate session listing in `ptk_session`; `ptk_state` owns it.
- Use a fixed tested session/process bound rather than a new configuration knob.
- Remove the conditional `job_terminal`; it is either required by a later
  worker-owned job design or absent.
- Fold candidate dependency checks into the protocol/launcher slices instead
  of producing a separate mapping deliverable.
- Keep worker incarnation as an internal stale-frame key unless a public
  operation can act on it.
- Remove the four-hour idle acceptance run until idle policy is actually part
  of the approved topology.
- Do not replace mandatory audit with a compatibility shim.

## Intake

All five blockers, all eight major findings, and all six minor findings are
admitted as plan defects.

PRS-B5 is admitted with one qualification: `descendants_unknown` permits
replacement only after the worker and PTK-owned containment domain are
confirmed gone. It never converts the uncertain prior command into a retryable
outcome and never claims an escaped or remote descendant stopped.

PRS-M2 is resolved in the revised recommendation by dropping cold
`ptk_job` from the first production cut. PRS-M8 is therefore retained as a
later-job contract, not first-cut work.

The revised plan will present only four owner design decisions, one at a time:

1. whether the first production topology requires one agent-owned MCP
   connection and one worker/runspace, with shared-connection multiplexing
   unsupported;
2. whether to retire the frozen R0 guardian contract artifacts and guards;
3. whether cold `ptk_job` leaves the first production surface; and
4. whether mandatory audit and its OTLP/protobuf build dependency leave the
   default runtime.

Canary activation remains a separately gated action at rollout time, not a
design question. Output-chunk transfer, lifecycle ownership, Unix wiring, and
fixed bounds are implementation decisions and will be settled directly in the
plan.
