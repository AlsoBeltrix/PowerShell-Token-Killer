# ci-worker-cancel-1: Scheduler cancellation checkpoint is load-sensitive

**Severity**: LOW — hosted Windows CI can time out while waiting for a
deliberately separate cancellation thread after the expected fatal state.
**Status**: In progress
**Branch**: `fix/ci-worker-cancel-checkpoint`
**Commit**: `82c89a1`

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

Pending Claude Opus 5 review.
