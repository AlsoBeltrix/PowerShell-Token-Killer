# opr-22: Startup timeout classification races a later wall-clock deadline

**Severity**: LOW — an actual named-session startup timeout can be reported as caller or supervisor cancellation in public failure diagnostics.

**Status**: Accepted; unplanned. Product and test changes are blocked until an approved plan defines one authoritative startup deadline and a structural timeout-versus-shutdown discriminator.

**Source**: Four-pass no-tool Claude Opus 5 review of `server/PtkMcpServer/Worker/SessionWorkerClient.cs` at `bb2c53e79b6f8a535e4a095e44990ac2710aba78`, with a final missed-finding adjudication against `NamedSessionSupervisor.StartSlotAsync` and startup diagnostics.

## Evidence

`NamedSessionSupervisor.StartSlotAsync` creates a token linked to supervisor shutdown and immediately schedules `CancelAfter(_startupTimeout)`. It then creates the slot's worker factory when needed. Only after that potentially nontrivial first-use work does it call `StartAsync` with `deadlineUtc = DateTimeOffset.UtcNow + _startupTimeout`. The cancellation token therefore expires from an earlier origin than the deadline passed to the factory.

`ProcessSessionWorkerFactory.LaunchFailureCode` classifies `OperationCanceledException` as `worker_start_timed_out` only when a fresh `DateTimeOffset.UtcNow` is at or beyond that later deadline; otherwise it returns `worker_start_canceled`. `ReadBeforeDeadlineAsync` cannot repair the distinction because the combined caller token is already canceled, so its timeout-conversion filter does not run. Factory creation performs file/runtime resolution and freezes the full supervisor environment, making the first-use offset material; timer delivery slop versus that offset decides the label rather than cancellation provenance.

## Predicted observable failure

On first startup for a slot, factory creation consumes enough of the already-running timeout that the linked token fires before the later deadline passed into `StartAsync`. Launch or initialization observes the canceled token and throws, but the factory reports `worker_start_canceled` even though the supervisor's startup timer caused cancellation. That detail reaches named-session `LastFailure` and public open/reset diagnostics, sending the operator toward shutdown or caller cancellation instead of startup slowness.

## Required repair boundary

Use one startup deadline origin across supervisor admission, factory creation, process launch, and initialization. Distinguish timeout from supervisor shutdown by token provenance or another structural signal, not by comparing a fresh wall clock to a separately originated deadline. Preserve the existing bounded startup budget and containment evidence carried by `SessionWorkerStartException`.

## Required guard

Add a deterministic first-start test whose factory provider consumes the supervisor timeout budget and then returns a process factory that observes the already-canceled startup token. Assert public startup detail is `worker_start_timed_out`, not `worker_start_canceled`. Add the paired supervisor-shutdown case proving explicit shutdown remains cancellation-classified. Temporarily revert only the repair and prove the timeout assertion fails, restore it, then run focused startup tests and full server verification.

## Current re-review extension

A current-head review of all 888 lines at `c8e6c4e` found the inverse misclassification through the same predicate. In the client-present failure branch, `ProcessSessionWorkerFactory.StartAsync` awaits `StopAsync` at `server/PtkMcpServer/Worker/SessionWorkerClient.cs:162-166` and `DisposeAsync` at `:178` before calling `InitializationFailureCode` at `:182`. If caller cancellation occurs just before `deadlineUtc` and successful cleanup crosses that deadline, `LaunchFailureCode` samples the later `DateTimeOffset.UtcNow` at `:241-243` and reports `worker_start_timed_out` instead of `worker_start_canceled`.

This is the same late-clock and missing-token-provenance root as opr-22, and the existing structural repair covers both directions. Add a deterministic client-present guard that triggers caller cancellation before the deadline, holds successful cleanup until after it, and requires `worker_start_canceled`. Preserve the existing real-timeout and supervisor-shutdown paired guards. No severity change is warranted because the effect remains limited to public startup diagnosis.

Claude Opus 5 reviewed the 888-line file in three bounded exact-source passes plus one whole-file integration pass. Focused client, protocol, worker-server, and named-supervisor tests passed 80/80. Independent adjudication returned `MERGE_OPR22`; no distinct finding arose from this candidate.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `LOW`; confidence `high`.
- `guard_confirmed=false`; no repair implemented or tested.
- This finding is independent of `opr-21`: it concerns the classifier's timeout provenance, not cleanup aggregation after a second failure.
