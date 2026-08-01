# ci-worker-cancel-1: Scheduler cancellation checkpoint is load-sensitive

**Severity**: LOW — hosted Windows CI can time out while waiting for a
deliberately separate cancellation thread after the expected fatal state.
**Status**: Reopened after recurrence on current `master`; repair in progress
**Branch**: `fix/ci-worker-cancel-drain-checkpoint`
**Commit**: pending

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

## Recurrence 2026-08-01

The merged 5→10-second checkpoint repair was insufficient. GitHub Actions run `30692685449` at exact head `bf6abcfeb6520f2b2d8f09bbe415f16014967142` failed `test (windows-latest)` at `WorkerOperationSchedulerTests.cs:671` after `scheduler.Fatal` had already produced the expected injected writer failure; all other five jobs passed. The preceding Ubuntu run `30692302468` failed an independent quota-control publication race and its Windows job passed, so the recurrence remains host-scheduling-sensitive rather than a production semantic failure.

The dedicated `secondCanceled` wait is redundant: `WorkerOperationScheduler.DrainAsync` awaits each active request owner, and the request owner awaits `ActiveRequest.ObserveCancellationAsync` before terminal completion. The repair must await `CancelAndDrainAsync` under one bounded test watchdog and assert `secondCanceled` completed afterward. This preserves proof that fatal writer failure canceled peer work while eliminating the separate pre-drain scheduling deadline.
