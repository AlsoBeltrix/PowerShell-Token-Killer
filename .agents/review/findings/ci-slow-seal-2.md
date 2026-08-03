# ci-slow-seal-2: Slow-seal elapsed guard begins before seal entry

**Severity**: LOW — hosted macOS CI intermittently fails a correct bounded
output-seal path because the guard measures variable pre-seal execution and
scheduling together with the bounded seal interval.

**Status**: Reopened 2026-08-02 after recurrence in run `30784201961`.
The completed first-repair plan remains historical; a new test change is
blocked until an approved plan re-anchors measurement at witnessed seal entry.


Historical plan: `.agents/plans/ci-slow-seal-elapsed-headroom.md`.

## First-occurrence evidence

GitHub Actions run `30746137942` failed only
`InvokeToolTests.Slow_output_store_seal_is_bounded_and_never_reruns` on
macOS. The elapsed value was `00:00:02.0012565` against a strict
`stopwatch.Elapsed < TimeSpan.FromSeconds(2)` assertion; the other 1,220 server
tests and both other platforms passed. Unchanged-product run `30746744012`
then passed all six jobs. Five focused Windows repetitions also passed, with
the runner rounding each duration to two seconds.

The stopwatch starts before the invocation, while `Task.Delay(maximumWait)`
starts later inside `ForegroundOutputCapture.SealCoreAsync`; its continuation
cannot guarantee a response strictly before the delay duration. The original
assertion is therefore unsatisfiable by construction on the timeout edge, not
a product containment regression. The prior verified `ci-slow-seal-1` record
explicitly identified this residual and required elapsed headroom if observed.

## Completed first repair boundary

Change only the slow-seal test. Keep the two-second seal limit, add one second
of scheduler tolerance, and raise the caller budget to five seconds so the
three-second elapsed bound remains below and discriminates from caller-budget
expiry. Restore the exact prior host test-seam value in `finally`. Preserve all
semantic assertions and every production timeout/source line.

## Completed first guard

Temporarily raise only the test's seal limit to four seconds while retaining
the three-second elapsed bound and five-second caller budget; the elapsed
assertion must fail. Restore the two-second limit, pass the focused test and
full server suite, then require all six exact-head CI jobs plus two additional
macOS job attempts.

## First-repair resolution

`server/PtkMcpServer.Tests/InvokeToolTests.cs` now keeps the two-second seal
limit, uses an independent three-second elapsed bound below a five-second
caller budget, and restores the exact prior host test-seam value. Production
source and timeouts are unchanged.

The mutation leg raised only the seal limit to four seconds and failed the
focused test at `elapsed=00:00:03.1491933; bound=00:00:03`. Restoring two
seconds passed the focused test and the full server suite 1,221/1,221. Exact
head `5180d0b` passed all six jobs in run `30748054339`; macOS-only attempts 2
and 3 also passed as jobs `91497781569` and `91498323805`.

## Recurrence

GitHub Actions run `30784201961` failed the unchanged
`Slow_output_store_seal_is_bounded_and_never_reruns` test on macOS at
`elapsed=00:00:03.1263559; bound=00:00:03`. The stopwatch starts before the
entire `SessionRuntime.InvokeAsync` call, while the bounded seal delay starts
only after command execution and rendering reach the output-store seal hook.
The one-second allowance therefore still includes unrelated variable pre-seal
work. Unchanged-code run `30786526767` passed all six jobs, confirming
intermittency rather than a product containment regression. The completed plan
explicitly forbids another tolerance increase and requires recurrence to reopen
diagnosis.

## Proposed reopened repair boundary (not approved)

Change only
`Slow_output_store_seal_is_bounded_and_never_reruns`. Capture a monotonic
timestamp at the existing slow-storage hook immediately before it signals seal
entry and blocks, then measure response latency from that witnessed boundary.
Keep the two-second seal limit, three-second elapsed bound, five-second caller
budget, exact test-seam restoration, and every semantic assertion. Do not
change production code or widen any timeout or tolerance.

This boundary is a review recommendation only; no change may land until a new
plan is approved.

## Proposed reopened guard (not approved)

With timing re-anchored, temporarily raise only the test seal limit to four
seconds while retaining the three-second elapsed bound and five-second caller
budget; the focused test must fail on elapsed time. A separate test-only
mutation in the invoked command must add 1.25 seconds before producing output:
the old invocation-start measurement must exceed the three-second bound, while
the re-anchored measurement must pass and the combined 3.25-second nominal path
must remain below the five-second caller budget. Restore both mutations, run
the focused and full server suites, require all six exact-head Actions jobs,
and rerun the macOS test job three times at that head. This guard is proposed
evidence for a future approved plan, not authorization to edit the test.

## Reviewer

Claude Opus 5
(`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`)
accepted the corrected test-only repair after rejecting a first proposal whose
four-second elapsed bound exceeded the caller budget. No product change was
reviewed or authorized.

Current-head recurrence adjudication at `ecd3a4c` by the same owner-selected
Claude Opus 5 configuration returned `REOPEN_CI_SLOW_SEAL_2`, severity LOW.
It rejected both a duplicate finding and further tolerance widening.
