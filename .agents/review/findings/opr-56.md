# opr-56: RTK planning drops top-level trap handlers

**Severity**: MEDIUM — a native command can be routed through RTK without the script's top-level `trap`, changing error handling and control flow solely because the route changed.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan makes trap-bearing script blocks ineligible for exact native routing and records their domain truthfully.

**Source**: Complete-source Claude Opus 5 review of `server/PtkMcpServer/Execution/ExecutionPlanner.cs` at `3628487` (blob `234c3833b6be6a22d6d984058090fd252f7244ba`), integrated with `RunspaceHost`, PowerShell AST behavior, focused tests, and a real warm PowerShell error-handling probe.

## Evidence

For `trap { 'caught'; continue }; where.exe definitely_missing`, PowerShell's parser reports one `EndBlock.Statements` entry containing the native `PipelineAst` and one separate `EndBlock.Traps` entry. `ExecutionPlanner.GetEligibleCommand`, `TryCreateMixedDataflowGuidance`, and `ClassifyDomain` inspect using statements and named blocks but never inspect `EndBlock.Traps`. With `where.exe` captured as an application, the planner can therefore classify the script `NativeTerminal`, construct only the native argv, and dispatch it through RTK; the trap text is not present in that dispatch.

A warm PowerShell probe with `$PSNativeCommandUseErrorActionPreference = $true` and `$ErrorActionPreference = 'Stop'` showed `where.exe` exit 1 enters the trap with `ProgramExitedWithNonZeroCode`, emits the handler output, continues, and preserves the handler's control flow. The RTK process path can only return the native process result and cannot execute that trap. Current shape tests cover using, clean, dynamicparam, and background constructs but no `EndBlock.Traps` case.

## Predicted observable failure

A user establishes native-error promotion in a warm session and invokes a trap-bearing one-command script on auto or forced RTK route. Direct PowerShell runs the declared handler; RTK routing silently drops it, so handler output, recovery actions, continuation behavior, and error disposition differ.

## Required repair

Treat any `EndBlock.Traps` entry as non-exact native shape in `GetEligibleCommand`, as `MixedDataflow` in `ClassifyDomain`, and as ineligible for post-success rewrite guidance. Add auto and forced-route tests proving the original script remains on PowerShellDirect, forced RTK records `RtkIneligibleShape`, domain is truthful, and no trap-dropping guidance is produced. Add a warm integration guard with native error promotion enabled and a failing application so the trap's observable handler behavior is preserved.

Temporarily revert only the trap checks, prove the new guards fail, restore them, then run focused planner, routing, audit-context, and shell-wiring tests plus the repository verification entry point.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) accepted this as a distinct current MEDIUM finding after bounded source review and focused adjudication. It is in the repaired `s3-block-fidelity` family, but `EndBlock.Traps` is a separate AST attachment not covered by that clean/dynamicparam repair or its guards. No product or test file changed in this finding slice.
