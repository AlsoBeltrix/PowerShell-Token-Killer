# Plan: or5 remediation — per-alias fault scoping, lifecycle intent, reopen, job-capability release

Status: approved direction (owner, 2026-07-24: "plan the fixes, review the
plan with opus"); plan review by claude-opus-5 pending. Implements the four
admitted openreview findings `.agents/review/findings/or5-1..4.md`, one
commit per finding, each with focused tests and a red-to-green mutation
proof, each reviewed per the codereview playbook. or5-5 (integration
order) is direction, not code: sub-slice 5 follows after these fixes.

## or5-1 — per-alias fault scoping (`WorkerPrivateHostRuntime`)

Problem: `ReplaceWorkerAsync`/`CloseWorkerAsync` catch blocks set the
host-global `_state = Faulted`; every later operation on every alias then
fails `SessionFaulted`, and the guardian never replaces the protocol-
healthy host.

Change:

- `AliasRuntime` gains `Faulted` (bool).
- In both catch blocks: set `alias.Slot = null; alias.Faulted = true;`
  instead of `_state = WorkerPrivateHostRuntimeState.Faulted`. The
  host-wide `Faulted` transition remains for genuinely host-global
  failures (initialization, shutdown misuse).
- `ValidateAndBind`: keep the host-global check, then return
  `SessionFaulted` only when the resolved alias's `Faulted` is set.
  `OpenWorkerAsync`: a faulted alias cannot be re-opened
  (`SessionFaulted`).
- Emit a truthful lifecycle fact for the faulted alias:
  `SessionLifecycleEvent` `Ready -> Faulted` with the current worker,
  `readyForEffects: false`, `warmStateLost: true`, `BootstrapState.Failed`
  — so the guardian projects the fault and sub-slice 5 recovery can pick
  the alias up later. `FrozenDefaultSessionState.ObserveSessionLifecycle`
  gains a `Faulted` branch (state Faulted, readyForEffects false,
  bootstrap Failed); it currently throws on anything but Ready/Cold.
  Lifecycle reason: reuse `AutomaticRecovery`? No — check
  `GuardianHostSessionLifecycleReason` for an existing fault/worker-lost
  value; if none exists, do NOT add one in this fix (the faulted fact
  travels as an operation result instead: the failed reset/restart/close
  returns its error outcome, and the guardian's existing
  `ObserveSessionRecoveryUnknown`/operation-result path marks the alias
  without a new enum). Decide at implementation; the invariant is: no new
  wire values without need.

Tests (`WorkerPrivateHostRuntimeTests`):

- Failed replacement on alias `scratch` (rig: make the old worker's
  `ShutdownAsync` throw) → `scratch` operations return `SessionFaulted`;
  `default` keeps its PID, generation, and successful job-list operation;
  runtime state stays `Ready`.
- Failed close on `scratch` → same assertions.
- Mutation proof: restore host-global fault in `ReplaceWorkerAsync`'s
  catch → first test goes red.

## or5-3 — desired state follows explicit lifecycle intent

Problem: a closed dynamic alias is still declared `DesiredState.Ready`
in the immutable binding, so a later recovery manifest resurrects it.

Model: the immutable binding (alias, kind, allowColdBackground, transition
1, and the original digest computed with `desired=ready`) stays the
alias's stable identity and is what capability validation uses. Desired
state becomes mutable per-alias intent:

- `FrozenDefaultSessionState.AliasState` gains `DesiredState` (starts
  Ready for default and for declared dynamic aliases).
- `ObserveSessionOperationResult`: a `SessionCloseResult` sets
  `DesiredState = Cold`; `SessionOpenResult`/`ResetResult`/
  `SessionRestartResult` set `DesiredState = Ready`. The `Faulted`
  lifecycle path from or5-1 does not change desired.
- `Create` (manifest): emits each binding with `DesiredState` taken from
  the mutable alias state, keeping the ORIGINAL digest (computed at
  declaration). Construct the projected binding via
  `new RecoveryBinding(..., currentDesired, binding.TransitionVersion,
  binding.BindingDigest)`.
- Public projection (`SnapshotSessions`) reports `DesiredState` from the
  mutable state.
- Host: no change — `ValidateInitialization` already creates slots only
  for Ready bindings. Reopen (or5-2) recomputes the digest over
  `desired=ready`, which equals the original declaration digest, so
  capability validation still passes.

Tests (`FrozenDefaultSessionStateTests`):

- Close a dynamic alias → next manifest declares it `Cold`; open it again
  → manifest flips back to `Ready`; digests equal the original declaration
  digest in every manifest.
- Mutation proof: keep emitting the binding's frozen `Ready` desired state
  → the cold-declaration assertion goes red.

## or5-2 — reopen a cold alias; failed open cleans up

Problem: reopen is refused for any declared alias ("already exists", also
false), and a failed open leaves the alias declared and unopenable.

Change, guardian (`GuardianHostSupervisor.DispatchSessionOpenAsync`):

- Gate on observed state, not declaration: declared + Cold → reopen
  allowed; declared + anything else → refuse "already exists".
- Declare only when the alias is entirely new. For a reopen, dispatch with
  the existing declared binding (no `DeclareDynamicAlias` call).
- On terminal open failure (dispatch refused/failed before the ready
  lifecycle): `UndeclareDynamicAlias(alias)` on the session source —
  remove the alias; its burned generation watermark is NOT reused (a
  later open redeclares with the allocator's current watermark; the burned
  generation is never reissued, per the nonreusing-generation contract).
- `FrozenDefaultSessionState.UndeclareDynamicAlias`: removes the alias iff
  it is declared, dynamic, and not currently Ready with a live worker.

Change, host (`WorkerPrivateHostRuntime.OpenWorkerAsync`):

- Refuse `SessionBusy` only when the alias is present WITH a live slot; an
  alias present slotless (previously closed) is openable: create the slot,
  emit the `RequestedOpen` ready lifecycle, return `SessionOpenResult`.

Tests:

- `WorkerPrivateHostRuntimeTests`: open → close → open succeeds; the
  second worker gets the next generation; launch order log proves the
  old worker shut down before the new launch.
- `GuardianHostSupervisorTests`: a host-failed open (fake peer refuses)
  leaves the alias undeclared — a later open succeeds.
- Mutation proofs: restore declaration-based refusal → reopen test red;
  drop the undeclare → failed-open test red.

## or5-4 — release job capabilities on terminal

Problem: host-side job capabilities are never released; 64 lifetime
background jobs wedge new starts (`SessionBusy`) across all aliases.

Change (`WorkerPrivateHostRuntime` + `PrivateHostWorkerEventBridge`):

- Per-alias `JobCapabilities` splits into two maps: `OutstandingJobs`
  (gated by `MaximumOutstandingPrivateRequests`, summed across aliases as
  today) and `CompletedJobs` (per-alias, needed for post-terminal
  `ptk_job status/output`, capped at `MaximumOutstandingPrivateRequests`
  per alias with oldest-insertion eviction).
- `PrivateHostWorkerEventBridge` gains a terminal observer: on
  `BeginJobTerminal` (which already validates exact job correlation), it
  also reports (alias, publicJobId) to the runtime; the runtime moves the
  entry from `OutstandingJobs` to `CompletedJobs`. The bridge is
  constructed before the runtime, so the observer is a set-once callback
  wired by `DefaultPrivateHostRuntimeFactory` (or a ctor arg on the
  runtime's existing `_workerEvents` field — implementer picks the smaller
  diff).
- `ExecuteBackgroundAsync` inserts into `OutstandingJobs` (same gate);
  `ExecuteJobOperationAsync` authorizes against either map (status/output
  on completed jobs stays valid; an evicted job is `JobCapabilityInvalid`).
  Replacement/close/shutdown clear both maps for the alias (unchanged).

Tests (`WorkerPrivateHostRuntimeTests`):

- Start 64 background jobs → 65th refused `SessionBusy`; a terminal for
  one → next start succeeds; output on a completed job stays authorized;
  after 64 completions on one alias, the oldest completed entry is evicted
  and its output is `JobCapabilityInvalid` while newer ones still work.
- Mutation proof: stop reporting the terminal to the runtime → the
  freed-slot assertion goes red.

## Order and review

One commit per finding in the order or5-1, or5-3, or5-2, or5-4 (fault
scoping first because later fixes build on per-alias state). Each commit:
focused tests, red-to-green mutation proof, full battery
(`export TMPDIR=/private/var/folders/lx/d63h0hdj7xj24tqp2gsplcrr0000gn/T &&
dotnet test server/PtkMcpServer.slnx`, Pester, handshake), then a
codereplaybook review dispatch (claude-opus-5, the owner-confirmed
frontier pair). Sub-slice 5 proceeds after all four land, per or5-5's
thinnest-path direction.
