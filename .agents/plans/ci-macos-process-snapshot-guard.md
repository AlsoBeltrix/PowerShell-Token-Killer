# Plan: macOS process-snapshot descriptor guard

**Status:** COMPLETE 2026-08-01 — isolated-probe head `0499aa7` passed
1,221/1,221 local server tests on Windows. GitHub Actions run `30727607324`
passed all six jobs at that exact head; its macOS test job then passed two
additional same-head attempts (`91446210698`, `91446785138`). Direct macOS
mutation proof remained unavailable as recorded below. The owner authorized
autonomous remediation of observed CI failures on 2026-08-01; this plan narrowed
that standing approval to the recurring macOS test-harness failure and changed
no production containment source.

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
- The first differential-batch repair at `f466319` passed its landing run and
  many later runs, but run `30724838116` failed only the revised macOS guard
  with a delta of 10. Unchanged run `30726873007` then passed the macOS job.
  This crossed the plan's explicit ceiling and proves the shared testhost can
  still add more than eight unrelated descriptors during the measured window.
- `server/PtkMcpServer.Tests/xunit.runner.json` disables test-collection
  parallelism. The remaining count is still process-global and includes
  testhost/runtime child-process plumbing whose bounded lifetime is not owned
  by `ProcessTableSnapshot`.
- A per-call redirected-stream regression accumulates across calls. The two
  observed bounded deltas do not scale with the 32-call workload.

## Change

1. Change only
   `server/PtkMcpServer.Tests/ProcessTreeContainmentTests.cs` and
   `server/PtkContainmentTestFixture/Program.cs`; production source remains
   byte-identical.
2. Add a macOS-only `process-snapshot-descriptor-probe` fixture command. A
   non-macOS invocation returns a distinct nonzero exit code rather than
   silently succeeding.
3. Inside that fresh single-purpose process, perform exactly this sequence:
   one successful warm-up snapshot; forced GC and finalizers; 32 sequential
   successful snapshots; descriptor count one; 32 more sequential successful
   snapshots; descriptor count two; first console or JSON use. Every snapshot
   must be non-null without retries, and a failed snapshot exits distinctly
   with a reason on stderr.
4. Count descriptors with the existing `fcntl(F_GETFD)` technique. After the
   second count, emit one JSON object containing both raw counts, their delta,
   and `RLIMIT_NOFILE`. The child performs no logging, serialization, or console
   write before the second count.
5. The xUnit guard launches the existing fixture apphost through the same
   deterministic resolution used by containment integration tests. It drains
   redirected stdout and stderr concurrently to EOF, waits under a bounded
   timeout, kills the child and descendants on timeout, and fails on a missing
   apphost, nonzero exit, missing or malformed JSON, or unsuccessful probe.
   Failure text includes exit code, stderr, both counts, delta, and limit.
6. Keep the assertion at second count no more than first count plus 8. The
   dedicated process removes unrelated testhost activity without widening the
   prior ceiling. One descriptor retained per second-batch call would add 32
   absent an intervening background collection; the guard is deliberately
   one-directional because an unscheduled collection could mask finalizable
   leaked handles.
7. Do not add retries, sleeps, production disposal changes, or another
   platform-specific skip. Any isolated-process failure above 8 again stops
   tolerance changes and requires new evidence.

No forced collection or finalizer pass occurs between the two measured batches,
so the test itself cannot reclaim a first-batch retained-stream regression before
measuring second-batch growth.

## Verification

1. Run the repository server verification entry point locally. The macOS-only
   parent guard and child arm will not execute on the current Windows host;
   report that limitation. Direct mutation proof against the pre-`f7d8df6`
   stdout-disposal implementation is unavailable because `nagatha.local`
   cannot be reached. Do not claim an empirical red/green guard proof; retain
   only the one-directional analytical 32-call leak signal above.
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
