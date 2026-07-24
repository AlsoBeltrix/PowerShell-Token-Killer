# Plan: or5 remediation — per-alias fault scoping, lifecycle intent, reopen, job-capability release

Status: approved direction (owner, 2026-07-24: "plan the fixes, review the
plan with opus"). Plan reviewed by claude-opus-5 (openreview, 2026-07-24):
six findings, all admitted at coder triage and absorbed into this revision
(no undeclare-on-failure, reopen reuses the declared binding verbatim,
refuse mismatched `allowColdBackground` in-band, reserve job slots before
commit, clear job capabilities on fault, mark alias state before any
lifecycle write). Implements the four
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
- In both catch blocks, FIRST under `_gate` and in this order:
  `alias.Slot = null; alias.Replacing = false; alias.Faulted = true;`
  and clear the alias's job-capability maps (or5-4's
  `OutstandingJobs`/`CompletedJobs`, or today's single map) — a faulted
  alias must not permanently tax the host-global background budget.
  The host-wide `Faulted` transition remains for genuinely host-global
  failures (initialization, shutdown misuse).
- THEN attempt the fault lifecycle write with `CancellationToken.None`,
  swallowing non-fatal write failures the way `TryWriteNotDispatchedAsync`
  does — never with the (possibly cancelled) operation token, and never
  before the alias state is marked. Emit
  `SessionLifecycleEvent` `Resetting/Closing -> Faulted` with the current
  worker, `readyForEffects: false`, `warmStateLost: true`,
  `BootstrapState.Failed`, with the exact fault reason
  (`ContainmentUnconfirmed` for a failed shutdown, `BootstrapFailed` for a
  failed relaunch — the reason enum is
  `GuardianHostSessionLifecycleReason`; no new wire values are added).
- Post-announcement rule: once the ready lifecycle (replacement) or the
  cold lifecycle (close) has been written, a later failure in the same
  operation (for example a cancelled TerminalDecoded delivery write) does
  NOT fault the alias. A replacement whose ready lifecycle was announced
  is committed (the operation is lost; the session is healthy at the new
  generation); a close whose cold lifecycle was announced leaves the alias
  honestly cold (the operation's terminal is lost; the session is closed).
  Faulting is reserved for the pre-announcement window where containment
  or relaunch genuinely failed.
- `FrozenDefaultSessionState.ObserveSessionLifecycle` gains a `Faulted`
  branch: state Faulted, readyForEffects false, bootstrap Failed, and it
  CLEARS `PendingWorkerGeneration` exactly like the Cold branch — a failed
  replacement must not leave a dangling grant.
- `ValidateAndBind`: keep the host-global check, then return
  `SessionFaulted` only when the resolved alias's `Faulted` is set.
  `OpenWorkerAsync`: a faulted alias cannot be re-opened
  (`SessionFaulted`).

Tests (`WorkerPrivateHostRuntimeTests`):

- Failed replacement on alias `scratch` (rig: make the old worker's
  `ShutdownAsync` throw) → `scratch` operations return `SessionFaulted`;
  `default` keeps its PID, generation, and successful job-list operation;
  runtime state stays `Ready`; the alias's job maps are cleared (with
  or5-4's split maps when landed; until then the single map).
- Failed close on `scratch` → same assertions; a background job started
  on `scratch` before the failure no longer counts against the global
  background budget.
- Guardian side (`FrozenDefaultSessionStateTests`): a Faulted lifecycle
  clears the pending grant and projects Faulted/Failed.
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
  for Ready bindings. Reopen (or5-2) reuses the declared binding
  verbatim, so its digest always matches.

Tests (`FrozenDefaultSessionStateTests`):

- Close a dynamic alias → next manifest declares it `Cold`; open it again
  → manifest flips back to `Ready`; digests equal the original declaration
  digest in every manifest.
- Mutation proof: keep emitting the binding's frozen `Ready` desired state
  → the cold-declaration assertion goes red.

## or5-2 — reopen a cold alias; failed open leaves the alias closed

Problem: reopen is refused for any declared alias ("already exists", also
false), and a failed open strands the alias.

Decisions absorbed from the plan review:

- NO undeclare path exists. `DeclareDynamicAlias` hard-asserts generation
  1 and the allocator never reissues, so removal+redeclaration
  deterministically throws — removal is dropped entirely. On terminal open
  failure (dispatch refused/failed before the ready lifecycle), the
  guardian marks the alias Cold: mutable `DesiredState = Cold` (or5-3's
  field) and observed state Cold via the existing
  `ObserveSessionRecoveryUnknown`/operation-result semantics — and the
  "declared + Cold → reopen allowed" path handles the retry. No
  declaration is removed, no generation is reissued, no window exists
  where the guardian lacks a declaration the host could still reference.

Change, guardian (`GuardianHostSupervisor.DispatchSessionOpenAsync`):

- Gate on observed state, not declaration: declared + Cold → reopen
  allowed; declared + anything else → refuse "already exists".
- Declare only when the alias is entirely new. For a reopen, dispatch with
  the existing declared binding (no `DeclareDynamicAlias` call).
- REFUSE in-band with a clear message when the request's
  `allowColdBackground` differs from the declaration: the flag is
  digest-bearing, and a divergent host-side digest makes the capability
  grant fail and escalates to whole-host loss. (Changing the flag is not
  supported in this fix.)
- `FrozenDefaultSessionState`: the reopen grant/lifecycle flow is the
  existing one (pending grant → Ready lifecycle binds the new worker);
  the declaration's transition and digest are unchanged.

Change, host (`WorkerPrivateHostRuntime.OpenWorkerAsync`):

- Refuse `SessionBusy` only when the alias is present WITH a live slot or
  marked `Faulted`.
- Reopen branch: the alias entry exists, slotless, not faulted → REUSE
  the existing `AliasRuntime`: take its `Binding` (verbatim, so the digest
  matches the guardian's declaration) and its current
  `GenerationHighWatermark` for `_slots.CreateAsync`; assign the new slot
  and watermark into that entry under `_gate`; skip the
  TryAdd/new-AliasRuntime path entirely. Emit the `RequestedOpen` ready
  lifecycle and return `SessionOpenResult`.

Tests:

- `WorkerPrivateHostRuntimeTests`: open → close → open succeeds; the
  reopened worker's generation is strictly greater than the closed one;
  the order log proves old shutdown before new launch; the binding used
  is the original declared one.
- `GuardianHostSupervisorTests`: reopen with a flipped
  `allowColdBackground` is refused in-band before any declaration or
  dispatch; a host-failed open leaves the alias Cold and reopenable
  (no declaration removed).
- Mutation proofs: restore declaration-based refusal → reopen test red;
  drop the existing-entry reuse (always build a fresh AliasRuntime) →
  the strict-generation assertion red; drop the digest-verbatim reuse →
  grant-validation failure path red.

## or5-4 — release job capabilities on terminal

Problem: host-side job capabilities are never released; 64 lifetime
background jobs wedge new starts (`SessionBusy`) across all aliases.

Change (`WorkerPrivateHostRuntime` + `PrivateHostWorkerEventBridge`):

- Per-alias maps split into `OutstandingJobs` (gated by
  `MaximumOutstandingPrivateRequests`, summed across aliases as today)
  and `CompletedJobs` (per-alias, for post-terminal `ptk_job
  status/output`, capped at `MaximumOutstandingPrivateRequests` per alias
  with oldest-insertion eviction).
- Reserve-before-commit: `ExecuteBackgroundAsync` inserts the
  guardian-reserved public job ID into `OutstandingJobs` UNDER `_gate`
  BEFORE the prepared commit write (the ID is known up front), and
  removes it if the start is refused — so a fast job's terminal can never
  precede the insert. On terminal (reported through
  `PrivateHostWorkerEventBridge`, which already validates exact job
  correlation), the runtime moves the entry from `OutstandingJobs` to
  `CompletedJobs`. A terminal for an unknown ID is a protocol fault
  (existing bridge behavior).
- The bridge reports (alias, publicJobId) to the runtime via a set-once
  observer callback wired at composition (smaller diff than reordering
  construction).
- `ExecuteJobOperationAsync` authorizes against either map; an evicted
  completed job is `JobCapabilityInvalid`. Replacement/close/shutdown and
  the or5-1 fault path clear both maps for the alias.

Tests (`WorkerPrivateHostRuntimeTests`):

- Start 64 background jobs → 65th refused `SessionBusy`; a terminal for
  one → next start succeeds; output on a completed job stays authorized;
  a terminal delivered BEFORE the start response is decoded still frees
  exactly one slot (reserve-before-commit); after 64 completions on one
  alias, the oldest completed entry is evicted and its output is
  `JobCapabilityInvalid` while newer ones still work.
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

