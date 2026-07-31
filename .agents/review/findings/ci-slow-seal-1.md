# ci-slow-seal-1: Slow-seal test overlaps its shaping reserve

**Severity**: LOW — hosted macOS CI intermittently reports a call-budget
failure instead of exercising the slow output-store seal path.
**Status**: In progress
**Branch**: `fix/ci-slow-seal-budget`
**Commit**: `44b31ed`

## Evidence

Draft PR 17 run `30658534883` failed
`Slow_output_store_seal_is_bounded_and_never_reruns` because the response
contained `call budget expired before output shaping`. The test supplied a
one-second caller budget while `OutputSealWaitBeforeShaping` deliberately
reserves two seconds of the remaining budget for shaping.

## Predicted observable failure

When hosted scheduling consumes the one-second caller budget before shaping,
the test observes the budget-expired fallback even though the blocked seal is
correctly abandoned and the user script is not rerun.

## Approach

Raise only this test invocation's caller budget from one to three seconds. The
seal wait can then consume at most roughly one second before the two-second
shaping reserve, so the existing sub-two-second response bound still verifies
containment. Production timeout behavior and the assertions remain unchanged.

## Files changed

- `server/PtkMcpServer.Tests/InvokeToolTests.cs:447` — give the slow-seal test
  a three-second caller budget.

## Guard proof

- Red: hosted macOS run `30658534883` reached the unique budget-expired
  assertion failure with the one-second budget.
- Green: focused test passed 1/1 and the full local server suite passed
  1,215/1,215. Draft PR 19 run `30662384648` then passed all six hosted
  jobs, including macOS server tests and handshake.

## Known gaps

The failure is scheduler-sensitive and the current Windows host does not
reproduce it deterministically. Hosted macOS supplies the revert-side guard.

## Reviewer comments

Pending Claude Opus 5 review.
