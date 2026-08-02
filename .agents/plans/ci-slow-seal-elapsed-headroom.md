# Plan: slow-seal elapsed headroom

**Status:** COMPLETE 2026-08-01 (approved 2026-08-01 — owner-authorized
autonomous remediation of observed CI failures; exact test-only scope accepted
by Claude Opus 5) — commit `5180d0b` passed mutation proof, 1,221/1,221 local
server tests, all six jobs in run `30748054339`, and macOS-only rerun attempts
2 and 3 of that run (jobs `91497781569`, `91498323805`). Production source and
timeouts remained unchanged.

## Evidence

`ci-slow-seal-2` records macOS run `30746137942`, where the only failure was a
two-second asynchronous timeout path measured `1.2565` milliseconds past the
test's strict two-second wall-clock assertion. Unchanged-product run
`30746744012` passed all six jobs, and five focused local repetitions passed.
This is the previously recorded `ci-slow-seal-1` residual, now observed.

## Change

1. Change only
   `server/PtkMcpServer.Tests/InvokeToolTests.cs` in
   `Slow_output_store_seal_is_bounded_and_never_reruns`.
2. Save `_host.OutputSealLimitForTests` before mutation and restore that exact
   value in `finally`.
3. Define a two-second seal limit, one-second scheduler tolerance,
   three-second elapsed bound, and five-second caller timeout. Set/pass the
   named values rather than retaining unrelated numeric literals.
4. Assert elapsed time is below the three-second bound and include both elapsed
   and bound in failure text.
5. Preserve every semantic assertion: seal entry, shaped output, unavailable
   recovery without rerun, no call-budget-expired response, no handle leak,
   one execution, late-hook completion, and restored store capacity.
6. Do not change production code, production timeouts, output-store reserve
   behavior, runner configuration, or test parallelism.

The internal `_sealDelayForTests` seam is not reachable through this
`SessionRuntime` path because the runtime constructs `ForegroundOutputCapture`
with default arguments. Adding a new seam is outside this recurrence's scope.

## Verification

1. Have Claude Opus 5 review the exact staged test change at max effort before
   commit.
2. Mutation proof: temporarily set only the named seal limit to four seconds,
   retain the three-second elapsed bound and five-second caller timeout, and
   confirm the focused test fails on the elapsed assertion. Restore two
   seconds and confirm the focused test passes.
3. Run `dotnet test server/PtkMcpServer.slnx`.
4. Commit and push the single test-only slice under the always-push policy.
5. Require all six GitHub Actions jobs at the exact pushed head, then rerun the
   macOS test job twice at that same head. Any recurrence stops tolerance
   changes and reopens diagnosis; do not widen the bound again.
6. Close `ci-slow-seal-2`, mark this plan complete, record exact evidence, and
   resume the queued `AuditCallMetadata.cs` review.

## Non-goals

- Changing the output-seal implementation or its two-second test seam.
- Relaxing the elapsed bound beyond the five-second caller budget.
- Treating a green rerun alone as root-cause evidence.
- Broad CI, timeout, or test-runner changes.
