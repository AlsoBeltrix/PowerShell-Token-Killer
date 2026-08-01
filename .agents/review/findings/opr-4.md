# opr-4: Cleanup-time caller cancellation overwrites an elapsed process timeout

**Severity**: MEDIUM — a command that exhausted its execution budget can be reported as caller-canceled, suppressing the timeout and remote-effects warning.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan covers process-runner timeout-cause classification.

**Source**: Bounded Claude Opus 5 review of current production code, with the same root cause confirmed in the Bash runner and syntax validator.

## Evidence

`server/PtkMcpServer/Execution/RtkProcessRunner.cs:124-155` catches cancellation of a token linked from the caller and execution timeout, awaits `KillAndDrainAsync`, then reads the live caller token to choose canceled versus timed out. If the timeout fires first and the caller cancels during the bounded kill/drain wait, the later caller state overwrites the actual cause that interrupted `WaitForExitAsync`.

`server/PtkMcpServer/Execution/BashProcessRunner.cs:287-311` and `server/PtkMcpServer/Execution/BashProcessRunner.cs:432-463` use the same post-cleanup live-token classification for syntax validation and Bash execution.

## Predicted observable failure

A post-start RTK or Bash command exceeds its deadline. While PTK is terminating and draining the process, the caller cancels. PTK returns `Canceled` with `TimedOut=false` instead of `OutcomeUnknown` with `TimedOut=true`; the audit detail attributes the result to the caller and the timeout warning about possible remote effects is omitted. Bash syntax validation likewise returns `Canceled` instead of `TimedOut`.

## Required repair

Snapshot the cancellation cause immediately when the linked wait is interrupted, before any kill/drain await, and use that immutable cause for the result. Apply the same rule to RTK execution, Bash execution, and Bash syntax validation. Add deterministic guards that trigger the execution/validation timeout first, cancel the caller only after cleanup begins, and assert timeout remains authoritative. Prove each guard fails against current code, restore the repair, run the repository verification entry point, and obtain fixed-SHA Claude Opus 5 review before integration.

## Reviewer

Claude Code 2.1.220 using owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, frontier, read-only review of `server/PtkMcpServer/Execution/RtkProcessRunner.cs` at `29c54d285952e9703b86c15987cc0773210eb2ff`. Verdict: `finding`. Bash parity was confirmed by direct source tracing at the same SHA.
