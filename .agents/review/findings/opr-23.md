# opr-23: Fast containment proof erases postlaunch slot provenance

**Severity**: MEDIUM — cancellation after a Windows worker was created can be reported as prelaunch failure, removing or cooling the slot instead of leaving the contract-required faulted state.

**Status**: Accepted; unplanned. Product and test changes are blocked until an approved plan separates process-creation provenance from containment-proof completion and defines deterministic slot-state guards.

**Source**: Three-pass no-tool Claude Opus 5 review of `server/PtkMcpServer/Worker/WindowsProcessTreeSupervisor.cs` at `f596c64bb8c9cc7a147fdd91610dfea6ffb92f8d`, followed by focused adjudication against `ProcessSessionWorkerFactory`, `NamedSessionSupervisor`, and the approved postlaunch failure contract.

## Evidence

After `CreateProcessInJob` succeeds and exact job membership is verified, `WindowsProcessTreeSupervisor.Launch` performs a cancellation checkpoint. If cancellation throws, rollback terminates the job and obtains an optional containment proof task. When the job is already empty or that proof completes before the immediate check, the catch rethrows the bare `OperationCanceledException`. When proof is still pending or faulted, `AttachContainment` instead wraps the cancellation in `WorkerProcessException` carrying the task.

`ProcessSessionWorkerFactory` receives no process object in either case. It infers `ProcessLaunched` solely from whether the exception carries a containment task. The bare-cancellation branch therefore becomes `ProcessLaunched: false`; the wrapped branch becomes `ProcessLaunched: true`. `NamedSessionSupervisor` removes a new named slot or returns the default slot to `Cold` for the false branch, but leaves the true branch `Faulted`. The approved lifecycle contract says every failure after process creation leaves the slot faulted even when containment was confirmed; proof timing may select confirmed versus unconfirmed containment, but not whether the process existed.

## Predicted observable failure

A startup cancellation lands after the Windows worker was atomically created and verified in its job. If the worker has already exited or job-empty observation completes quickly during rollback, the open/reset failure is classified as prelaunch and the named slot disappears or the default silently returns cold. The same cancellation with slower proof leaves the slot faulted. Operators therefore see different public slot lifecycle solely from containment-observer timing, and the fast path loses the durable record that a process crossed the launch boundary.

## Required repair boundary

Preserve whether process creation ever succeeded independently from containment proof state. Every post-creation exception must reach `SessionWorkerStartException` with `ProcessLaunched: true`. Completed proof may report confirmed containment; pending or failed proof must remain unconfirmed. Neither proof timing nor exception wrapping may change the slot's postlaunch `Faulted` terminal state. Keep timeout-versus-shutdown detail classification in `opr-22` separate.

## Required guard

Add deterministic Windows supervisor/factory tests that cancel after process creation and membership verification with three proof states: already empty, immediately completed, and pending. Assert every path reports `ProcessLaunched: true` and leaves the named slot `Faulted`; only containment outcome/task state may differ. Add the pre-create cancellation control proving it remains `ProcessLaunched: false` and removes/cools the slot. Temporarily revert only the repair and prove the fast-proof postlaunch assertions fail, restore it, then run focused Windows containment, factory, and named-session tests plus full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `MEDIUM`; confidence `high`.
- `guard_confirmed=false`; no repair implemented or tested.
- Atomic job assignment and containment remain sound; the accepted impact is slot provenance/state, not a process-tree escape.
