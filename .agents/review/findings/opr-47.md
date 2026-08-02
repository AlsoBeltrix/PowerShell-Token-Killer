# opr-47: Slow validator-start audit flush fabricates Bash timeout

**Severity**: MEDIUM — a fast, determinate `bash -n` result can be discarded as a validator timeout solely because the durable validator-start audit record finishes after the validator's fixed process budget.

**Status**: Accepted; unplanned. Product and test changes are blocked until an approved plan separates the validator process deadline from the noncancelable audit-flush wait while preserving fail-closed audit admission.

**Source**: Three bounded exact-source Claude Opus 5 passes over `server/PtkMcpServer/Execution/BashProcessRunner.cs` at `94ff698`, followed by whole-file integration against process-tree containment, RTK parity, invoke models, dispatch, and the production `RunspaceHost` caller.

## Evidence

`BashProcessRunner.ValidateAsync` starts `recordStarted` as an independently running task at `:174-178`. After the validator process starts and its output drains are active, `:252-255` passes that audit task to `WaitForDrainsAsync` using `validationDeadline`, the fixed `bash -n` process budget. If the audit task is the only unfinished task when that deadline expires, `:256-273` kills/drains the validator, then awaits the same audit task without a deadline. When the audit task ultimately succeeds, the method still returns `TimedOut` or `Canceled` without checking the already-available process exit code. The normal exit-code mapping at `:340-346` is therefore unreachable on this path.

The production caller at `server/PtkMcpServer/RunspaceHost.cs:3440-3449` supplies `RecordValidatorStartedAsync` with `CancellationToken.None`; a slow durable flush can therefore outlast the validator process budget and still succeed. `:3464-3468` rejects every non-`Valid` result through `BashValidationFailureResult`, so a script whose `bash -n` process already exited zero is denied by the timeout result. This is distinct from `opr-4`: no later caller cancellation overwrites the cause that interrupted a running process. Here audit latency consumes the child-process deadline and discards a determinate verdict.

## Predicted observable failure

On a slow but eventually successful audit sink, `bash -n` exits zero promptly while the validator-start record remains in its durable flush. Once the validator's fixed validation limit at `BashProcessRunner.cs:114-121` passes, PTK waits for that flush to finish but reports validation timed out and does not execute the valid Bash script. The audit record and response falsely attribute the refusal to validator execution rather than audit latency.

## Required repair

Keep durable validator-start admission fail closed, but do not use the child-process deadline to erase a determinate validator exit. Preserve the process exit result independently of the noncancelable audit flush and classify audit failure separately. Add a deterministic guard with an immediately successful validator and a delayed `recordStarted` task that eventually returns true after the validation limit; assert the completed exit verdict remains `Valid` rather than `TimedOut`. Add syntax-invalid and genuinely running-past-deadline controls. Temporarily revert only the repair, prove the new guard fails, restore it, run the repository verification entry point, and obtain a fixed-SHA Claude Opus 5 review.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 803 lines of `BashProcessRunner.cs` in three bounded exact-source passes plus whole-file production integration. Focused Bash, RTK, and process-tree containment tests passed 27/27. Integration accepted this distinct MEDIUM finding, merged Bash pre-start cause classification into `opr-4`, merged Bash eager capture allocation into `opr-40`, and resolved all other candidates. No product or test file changed in this finding slice.
