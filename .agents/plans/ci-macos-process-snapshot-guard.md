# Plan: macOS process-snapshot descriptor guard

**Status:** IMPLEMENTING — owner authorized autonomous remediation of observed
CI failures on 2026-08-01. This plan narrows that standing approval to the
recurring macOS test-harness failure below; it does not authorize production
containment changes.

## Evidence

`ProcessTreeContainmentTests.Mac_process_table_snapshots_release_redirected_output_immediately`
counts every descriptor in the test host before and after 32 successful
`ProcessTableSnapshot.TryTake` calls. Production already scopes both the
`Process` and redirected stdout stream; the regression fixed by `f7d8df6`
omitted the explicit stdout-stream disposal.

- GitHub Actions run `30689168285` attempt 1 failed only this macOS guard with
  an observed descriptor delta of 8; attempt 2 passed unchanged.
- Run `30710404489` failed only the same macOS guard with a delta of 2. The
  immediately preceding run was green, and unchanged server descendant run
  `30711210700` passed all six jobs.
- `server/PtkMcpServer.Tests/xunit.runner.json` disables test-collection
  parallelism. The remaining count is still process-global and includes
  testhost/runtime child-process plumbing whose bounded lifetime is not owned
  by `ProcessTableSnapshot`.
- A per-call redirected-stream regression accumulates across calls. The two
  observed bounded deltas do not scale with the 32-call workload.

## Change

1. Change only
   `server/PtkMcpServer.Tests/ProcessTreeContainmentTests.cs`; production source
   remains byte-identical.
2. Rename the guard to
   `Mac_process_table_snapshots_do_not_accumulate_redirected_output`.
3. Retain one successful warm-up snapshot followed by forced GC/finalizers.
4. Run 32 successful snapshots and record the process descriptor count, then
   run another 32 successful snapshots without intervening or trailing GC and
   record it again.
5. Assert the second count is no more than the first count plus 8. Eight is the
   largest observed non-accumulating hosted delta; it remains four times below
   the minimum 32-descriptor signal from one retained descriptor per call in
   the second batch.
6. Do not add retries, sleeps, production disposal changes, or another
   platform-specific skip.

No forced collection or finalizer pass occurs between the two measured batches,
so the test itself cannot reclaim a first-batch retained-stream regression before
measuring second-batch growth.

## Verification

1. Run the repository server verification entry point locally. The macOS-only
   guard will skip on the current Windows host; report that limitation. Direct
   mutation proof against the pre-`f7d8df6` stdout-disposal implementation is
   unavailable because `nagatha.local` cannot be reached. Do not claim an
   empirical red/green guard proof; the retained analytical proof is that one
   leaked descriptor per call adds at least 32 in the second batch against the
   evidence-backed ceiling of 8.
2. Have Claude Opus 5 review the exact staged test slice at max effort before
   commit.
3. Commit and push the one-test slice under the repository always-push policy.
4. Require all six GitHub Actions jobs at the exact pushed head, then rerun the
   macOS test job twice at that same head. These green runs establish only that
   the changed guard is stable and introduces no new breakage; remediation rests
   on the differential batch design, not rerun success. If any macOS attempt
   fails the revised guard above the 8-descriptor ceiling, stop widening
   tolerances; preserve the log as new evidence and require a different
   isolation design.

## Non-goals

- Changing `ProcessTableSnapshot.FromPs` or containment behavior.
- Treating a rerun alone as remediation evidence.
- Claiming direct macOS mutation proof while `nagatha.local` is unreachable
  from the current host.
