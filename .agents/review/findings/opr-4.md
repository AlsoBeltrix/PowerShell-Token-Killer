# opr-4: Cleanup-time caller cancellation overwrites an elapsed process timeout

**Severity**: MEDIUM — a command that exhausted its execution budget can be reported as caller-canceled, suppressing the timeout and remote-effects warning.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan covers process-runner timeout-cause classification.

**Source**: Bounded Claude Opus 5 review of current production code, with the same root cause confirmed in the Bash runner and syntax validator.

## Evidence

`server/PtkMcpServer/Execution/RtkProcessRunner.cs:124-155` catches cancellation of a token linked from the caller and execution timeout, awaits `KillAndDrainAsync`, then reads the live caller token to choose canceled versus timed out. If the timeout fires first and the caller cancels during the bounded kill/drain wait, the later caller state overwrites the actual cause that interrupted `WaitForExitAsync`.

`server/PtkMcpServer/Execution/BashProcessRunner.cs:287-311` and `server/PtkMcpServer/Execution/BashProcessRunner.cs:432-463` use the same post-cleanup live-token classification for syntax validation and Bash execution.

### Pre-start budget-classification extension (2026-08-01)

`server/PtkMcpServer/Execution/RtkProcessRunner.cs:51-52` and `:73-74` decide that cancellation or deadline exhaustion prevents process start, but pass only the cancellation value captured at the guard to `BudgetFailure`. `BudgetFailure` at `:241-253` independently re-reads `DateTimeOffset.UtcNow >= deadline` for `TimedOut` while choosing `AuditDetailCode` from the earlier `canceled` argument. The deadline can cross between the guard and result construction, producing `TimedOut=true` together with `rtk_execution_canceled_before_start`. `WorkerSession.MapInvokeResult` gives `TimedOut` precedence, so the public outcome and audit detail can describe different causes.

## Predicted observable failure

A post-start RTK or Bash command exceeds its deadline. While PTK is terminating and draining the process, the caller cancels. PTK returns `Canceled` with `TimedOut=false` instead of `OutcomeUnknown` with `TimedOut=true`; the audit detail attributes the result to the caller and the timeout warning about possible remote effects is omitted. Bash syntax validation likewise returns `Canceled` instead of `TimedOut`.

For a pre-start RTK call, the caller can already be canceled when the guard runs and the deadline can expire before `BudgetFailure` constructs the result. No process starts, but the returned timeout flag takes precedence while the audit detail still says cancellation. This extension is LOW in isolation because execution remains correctly suppressed; it shares the existing immutable-cause defect and therefore remains part of `opr-4` rather than a separate finding.

## Required repair

Snapshot the cancellation cause immediately when the linked wait is interrupted, before any kill/drain await, and use that immutable cause for the result. Apply the same rule to RTK execution, Bash execution, and Bash syntax validation. Add deterministic guards that trigger the execution/validation timeout first, cancel the caller only after cleanup begins, and assert timeout remains authoritative. Prove each guard fails against current code, restore the repair, run the repository verification entry point, and obtain fixed-SHA Claude Opus 5 review before integration.

For the pre-start guards, snapshot both cancellation and deadline-expiry state at the decision point and pass both values into `BudgetFailure`; result flags and audit detail must derive from that single snapshot. Add a deterministic guard that crosses the deadline after the pre-start decision and proves the result cannot combine a timeout outcome with a cancellation detail.

## Reviewer

Claude Code 2.1.220 using owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, frontier, read-only review of `server/PtkMcpServer/Execution/RtkProcessRunner.cs` at `29c54d285952e9703b86c15987cc0773210eb2ff`. Verdict: `finding`. Bash parity was confirmed by direct source tracing at the same SHA.

On 2026-08-01, Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 442 lines of `RtkProcessRunner.cs` at `2debaf6` in two bounded source/caller/test passes plus a whole-file dispatch integration pass. Focused runner, containment, and invoke tests passed 76/76. Independent adjudication accepted the pre-start contradiction at LOW as the same immutable-cause defect already recorded here. No product or test file changed in this extension.
