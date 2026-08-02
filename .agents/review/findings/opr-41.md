# opr-41: Pre-start RTK budget failure leaves fabricated warm `$LASTEXITCODE`

**Severity**: MEDIUM — a canceled or expired RTK dispatch that starts no user process can persist a fabricated native exit code into the warm session and change later script behavior.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan separates no-start state restoration from fallback authorization.

**Source**: Whole-file dispatch integration and independent Claude Opus 5 adjudication of production code at `2debaf6`.

## Evidence

`server/PtkMcpServer/RunspaceHost.cs:3489-3495` captures the warm automatic state, calls `ResetExitCode`, and then awaits `RtkProcessRunner.ExecuteAsync`. `ResetExitCode` at `:990-1000` runs `$global:LASTEXITCODE = 0` as a PowerShell pipeline, changing the stored native exit code. Because the reset is an invoked pipeline rather than a `SessionStateProxy` write, it can also replace the session's prior `$?` status with success.

The runner's pre-start guards at `server/PtkMcpServer/Execution/RtkProcessRunner.cs:51-52` and `:73-74` return `BudgetFailure` when cancellation or deadline exhaustion wins before `Process.Start`. `BudgetFailure` at `:241-253` returns `Disposition=NotStarted` and `UserExecutionStarted=false`, but supplies no `ProvenPreStartFallbackReason`.

`RunspaceHost.cs:3496-3518` restores the captured state only when `ProvenPreStartFallbackReason` is present. All other runner results enter `:3539-3558`; that branch sets an exit code only for `Completed`, shapes the result, and returns without restoring the snapshot. A cancellation or deadline that becomes active after the reset but before either runner guard therefore executes no user command and still leaves the warm runspace with fabricated bookkeeping state. The existing snapshot/restoration helpers at `:2297-2325` cover `$LASTEXITCODE` only; `TryRestoreWarmAutomaticState` already restores it through `SessionStateProxy` without invoking another PowerShell pipeline.

## Predicted observable failure

A warm named session has a meaningful nonzero `$LASTEXITCODE`. An RTK-routed call reaches the reset, then cancellation or deadline exhaustion wins at the runner's pre-start guard. PTK correctly reports that user execution never started, but the next session call observes `$LASTEXITCODE` as zero rather than the prior value; the bookkeeping pipeline can likewise replace the prior `$?` status with success. Scripts that branch on either automatic value can take a path unsupported by any user execution.

This is distinct from `opr-4`, which records contradictory cause classification, and from verified `s3-rtk-preference-isolation`, which addressed ambient preference effects on a command that was actually routed. This finding is persistent warm-state mutation after a proven no-start result.

## Required repair

Separate `$LASTEXITCODE` restoration from fallback authorization. Every RTK result with `UserExecutionStarted=false` must use the existing non-pipeline restoration helper before either returning or entering an authorized fallback; budget and cancellation outcomes must remain non-fallback results. If the `$?` guard below reproduces, also reset `$LASTEXITCODE` through a non-pipeline mechanism such as `SessionStateProxy.SetVariable` so bookkeeping cannot disturb the read-only `$?` automatic variable. If restoration fails, follow the existing fallback-restoration failure pattern: recycle the runspace and return `WarmStateLost` without retrying the original command.

Started but non-`Completed` results also traverse the general return branch, but their correct exit-state semantics depend on an executed process and are outside this proven no-start finding. Do not apply no-start restoration when `UserExecutionStarted=true` or silently change those semantics in this repair.

Add a host-level guard that seeds a nondefault `$LASTEXITCODE`, makes cancellation or deadline win after reset but before process start, and proves the value is unchanged on the next warm-session call while the RTK stub records no execution. Prove that guard red against current code and green after repair. Also seed `$?` to false through a failing warm-session command and test whether the first expression in the next call still observes false. If that guard reproduces the qualified side effect, prove it green after repair and optionally supplement it with a structural assertion that reset invokes no PowerShell pipeline. If it is already green, record non-reproduction and remove `$?`, the reset implementation change, and the structural assertion from the repair scope. Run the repository verification entry point and obtain fixed-SHA Claude Opus 5 review.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 442 lines of `RtkProcessRunner.cs` at `2debaf6` in two bounded source/caller/test passes plus a whole-file dispatch integration pass. Focused runner, containment, and invoke tests passed 76/76. The integration pass found this cross-file branch defect; independent adjudication accepted it at MEDIUM and distinct from `opr-4` and verified `s3-rtk-preference-isolation`. No product or test file changed in this finding slice.
