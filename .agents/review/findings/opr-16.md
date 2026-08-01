# opr-16: Deadline cancellation witness can unregister before observation

**Severity**: LOW — a scheduler test can time out even though deadline
cancellation completed correctly.

**Status**: Accepted; unplanned. Test change is blocked until an approved plan
defines the focused repair and guard proof.

**Source**: Bounded Claude Opus 5 follow-up review of
`WorkerOperationSchedulerTests.Deadline_cancellation_does_not_terminate_a_responsive_worker`
at `2e63e1b`, following the independently reproduced callback-disposal race in
`ci-worker-cancel-1`.

## Evidence

`server/PtkMcpServer.Tests/WorkerOperationSchedulerTests.cs` installs a
`CancellationToken.Register` callback that completes `canceled`, then awaits
`Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)`. The test releases the
deadline observer and waits five seconds for only that callback signal.

The delay registers its cancellation callback after the test callback.
Cancellation can complete the delay first; executor unwind then disposes the
still-pending test registration before its callback runs. The registration is
the only setter for `canceled`. `ActiveRequest` can therefore complete and
observe its dedicated cancellation task correctly while the test witness never
completes.

## Predicted observable failure

Under a scheduling interleaving that resumes the executor before the
cancellation callback walk reaches the earlier test registration,
`canceled.Task.WaitAsync(TimeSpan.FromSeconds(5))` throws `TimeoutException`.
Product deadline cancellation, terminal timeout framing, and responsive-worker
containment remain correct.

## Required repair

Remove the auxiliary token registration. Catch `OperationCanceledException`
from the infinite delay when `cancellationToken.IsCancellationRequested`, set
`canceled` on that unwind path, and rethrow with bare `throw;`. Preserve the
pre-terminal bounded witness wait so cleanup cannot satisfy the assertion.

## Required guard

Temporarily suppress only the deadline path's
`RequestCancellation(CancellationReason.Deadline)` call and prove the repaired
focused test fails at the witness wait. Restore production, prove the focused
test green across fresh processes, then run the full server solution. The
production mutation must not be committed.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`
(`max`, no-tool, session-only); bounded adjudication at `2e63e1b`.

- Verdict: `accept`; severity `LOW`; confidence `high`.
- `guard_confirmed=false`; the guard above is required before repair closure.
- No product defect was accepted; this finding is limited to test observation.
