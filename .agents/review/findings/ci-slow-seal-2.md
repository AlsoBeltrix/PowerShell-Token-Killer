# ci-slow-seal-2: Slow-seal elapsed guard is unsatisfiable at its timer edge

**Severity**: LOW — hosted macOS CI intermittently fails a correct bounded
output-seal path by measuring the asynchronous timeout continuation a fraction
after its configured delay.

**Status**: Accepted; remediation approved in
`.agents/plans/ci-slow-seal-elapsed-headroom.md`.

## Evidence

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

## Required repair boundary

Change only the slow-seal test. Keep the two-second seal limit, add one second
of scheduler tolerance, and raise the caller budget to five seconds so the
three-second elapsed bound remains below and discriminates from caller-budget
expiry. Restore the exact prior host test-seam value in `finally`. Preserve all
semantic assertions and every production timeout/source line.

## Required guard

Temporarily raise only the test's seal limit to four seconds while retaining
the three-second elapsed bound and five-second caller budget; the elapsed
assertion must fail. Restore the two-second limit, pass the focused test and
full server suite, then require all six exact-head CI jobs plus two additional
macOS job attempts.

## Reviewer

Claude Opus 5
(`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`)
accepted the corrected test-only repair after rejecting a first proposal whose
four-second elapsed bound exceeded the caller budget. No product change was
reviewed or authorized.
