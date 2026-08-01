# ci-worker-cancel-1: Scheduler cancellation checkpoint is load-sensitive

**Severity**: LOW — hosted Windows CI can time out while waiting for a
deliberately separate cancellation thread after the expected fatal state.
**Status**: Reopened; second recurrence repaired locally, Opus and hosted closure pending
**Branch**: `fix/ci-worker-cancel-drain-checkpoint` (deleted after verified merge)
**Commit**: `8588374f8d19b97a9c38d9606a6e331ba38b8452`; merge `d7eefc5f7159469570135646a2667ca94b52d553`

## Evidence

PR 19 run `30664616413`, attempt 1, failed
`Writer_failure_latches_fatal_and_cancels_other_work` at
`WorkerOperationSchedulerTests.cs:671`. The test had already observed the
second operation enter and the expected injected writer failure latch as
`scheduler.Fatal`; only the five-second wait for the cancellation callback
timed out. Attempt 2 passed without a code change.

`ActiveRequest.RequestCancellation` intentionally schedules token cancellation
as `TaskCreationOptions.LongRunning` so a blocking PowerShell cancellation
callback cannot consume the deadline observer. Starting that dedicated thread
is host-scheduler-sensitive and is not a five-second product contract.

## Predicted observable failure

Under hosted Windows load, the dedicated cancellation thread can start after
the test-only five-second checkpoint even though the fatal state is latched and
the same code passes on retry.

## Approach

Raise only the cancellation-callback checkpoint from five to ten seconds, the
checkpoint convention already used by adjacent worker/session test suites.
Keep the scheduler implementation and every behavioral assertion unchanged.

## Files changed

- `server/PtkMcpServer.Tests/WorkerOperationSchedulerTests.cs:671` — allow ten
  seconds for the dedicated cancellation thread to signal.

## Guard proof

- Red: hosted Windows run `30664616413`, attempt 1, timed out at the exact
  five-second callback checkpoint after observing the expected fatal error.
- Green: the same head passed on attempt 2; the focused test passed 1/1 and the
  full local server suite passed 1,215/1,215. Draft PR 20 run `30666271214`
  then passed all six hosted jobs, including the Windows server suite and
  handshake.

## Known gaps

The failure is scheduler-sensitive, so the current host does not provide a
deterministic revert-side failure. The assertion still requires cancellation
and the subsequent drain to complete.

## Reviewer comments

Reviewer: claude / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`
/ max / frontier — escalated: owner (inline, session-only)

- Reviewed head `659a279`, base `d07563c`; `guard_confirmed=true`;
  verdict `accepted`; UTC `2026-07-31T21:38:07Z`.
- `LatchFatal` fans out through `ActiveRequest.RequestCancellation`, which
  starts a `LongRunning` thread. The wait measures host thread-start latency,
  not a product contract, so the one-line checkpoint change is correctly
  scoped.
- Hosted red reached line 671 only after fatal latched; unchanged retry and
  PR 20 green run supply the accepted manual guard. No commands were run in
  the review transport.
- Non-blocking follow-up: if adjacent five-second scheduler checkpoints recur,
  align the suite on ten seconds rather than repeating isolated bumps.

## Second recurrence 2026-08-01

GitHub Actions run `30701276509`, exact head
`5187eba3e36a68496056a19d0cbe5f819396d662`, failed
`Writer_failure_latches_fatal_and_cancels_other_work` on
`test (windows-latest)`: `CancelAndDrainAsync` completed, but the subsequent
`secondCanceled.Task.IsCompletedSuccessfully` assertion was false. All other
five jobs passed. The next exact-head run, `30702067881`, passed all six jobs,
confirming a recurrent scheduling-sensitive test defect rather than a product
regression.

The earlier drain-based repair relied on a `CancellationToken.Register`
callback installed before `Task.Delay` registered its own callback. Cancellation
may complete the delay first; the executor then unwinds and disposes the still
pending test registration before its callback runs. Drain correctly observes
the operation and cancellation task, but the auxiliary callback is not a
reliable witness.

### Approach

- Witness cancellation in the operation's own `OperationCanceledException`
  unwind path and rethrow with `throw;`.
- Await that witness before `CancelAndDrainAsync`, proving scheduler-failure
  fan-out rather than allowing cleanup cancellation to satisfy the test.
- Keep the 30-second bound as a failing harness watchdog, not as the
  synchronization mechanism.

### Guard proof

- Red: with only `LatchFatal` request-cancellation fan-out temporarily
  suppressed, the repaired focused test failed at the new witness wait after
  30 seconds. The production mutation was restored immediately.
- Green: restored focused test passed; then passed 100/100 fresh test
  processes. Full `server/PtkMcpServer.slnx` passed 1,221/1,221.

### Reviewer

Diagnostic adjudication: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`
(`max`, session-only) accepted the callback-disposal race and the catch-based,
pre-drain witness. Exact-slice review and hosted closure remain pending.

## First recurrence 2026-08-01

Repair verification:

- Hosted red: Windows run `30692685449` timed out at the redundant standalone cancellation checkpoint after fatal state was already latched.
- Local green: repaired focused test passed 20/20 fresh test processes; full `server/PtkMcpServer.slnx` passed 1,221/1,221.
- Scope: only `server/PtkMcpServer.Tests/WorkerOperationSchedulerTests.cs` changed; production code is unchanged.

Recurrence reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`max`, session-only), exact head `93a021d284a70320aabcc3b87a5436d56f7116ef` against base `777e687d8752c525953e28092b5fcb2ab74a8ee3`.

- Verdict: `accepted`; `guard_confirmed=true`; no actionable findings.
- Confirmed drain snapshots and awaits active owner tasks, and owner completion awaits cancellation observation before removal.
- Non-blocking observation: a narrow ordering window allows drain to label cancellation as shutdown after fatal publication but before fatal fan-out; the assertion still proves cancellation completion, while this test does not distinguish that internal reason.
- No commands or file changes were permitted in the review transport.

Hosted closure: PR 29 run `30694440416` passed all six jobs, including `test (windows-latest)`. PR 29 merged as `d7eefc5f7159469570135646a2667ca94b52d553`; a full branch-to-`origin/master` tree diff was empty before branch deletion.

The merged 5→10-second checkpoint repair was insufficient. GitHub Actions run `30692685449` at exact head `bf6abcfeb6520f2b2d8f09bbe415f16014967142` failed `test (windows-latest)` at `WorkerOperationSchedulerTests.cs:671` after `scheduler.Fatal` had already produced the expected injected writer failure; all other five jobs passed. The preceding Ubuntu run `30692302468` failed an independent quota-control publication race and its Windows job passed, so the recurrence remains host-scheduling-sensitive rather than a production semantic failure.

The dedicated `secondCanceled` wait is redundant: `WorkerOperationScheduler.DrainAsync` awaits each active request owner, and the request owner awaits `ActiveRequest.ObserveCancellationAsync` before terminal completion. The repair must await `CancelAndDrainAsync` under one bounded test watchdog and assert `secondCanceled` completed afterward. This preserves proof that fatal writer failure canceled peer work while eliminating the separate pre-drain scheduling deadline.
