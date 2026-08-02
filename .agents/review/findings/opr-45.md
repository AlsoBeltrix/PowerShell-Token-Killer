# opr-45: Nested local definitions suppress top-level Bash builtin detection

**Severity**: LOW — a narrow dual-syntax script can be misplanned as PowerShell because a definition confined to a nested scope is treated as globally visible.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan defines scope-aware local-definition visibility without weakening recursion exemptions.

**Source**: Bounded and whole-file Claude Opus 5 review of `TrustedPreflightClassifier` at `864cfe2`, confirmed by exact built-assembly reflection and PowerShell runtime probes.

## Evidence

`server/PtkMcpServer/TrustedPreflightClassifier.cs:66` records each `LocalDefinition` with only name and offsets. `CollectLocalDefinitions` at `:268-324` calls `Ast.FindAll(..., searchNestedScriptBlocks: true)` for function and alias definitions, flattening definitions from nested script blocks into one list. `IsLocallyDefined` at `:429-446` then accepts a same-name definition from that list by offsets alone, without checking the definition's enclosing script-block scope against the command's scope.

An exact built-assembly reflection probe for `function noop { function export { echo inner } }; export X=1` returned `PowerShellParseFatal=false` and `Finding=null`. In Bash, defining `noop` does not execute its body, so the top-level `export X=1` resolves the Bash builtin. An exact PowerShell runtime probe showed that the nested `export` function is not visible at top level and the final command fails with `CommandNotFoundException`. With no dialect finding, the active `RunspaceHost` caller falls through to PowerShell planning.

## Predicted observable failure

On the exact dual-syntax script, available Bash delegation is skipped because the classifier treats the nested function as a visible local definition for the later top-level command. PowerShell then reports `export` as an unknown command instead of Bash applying the environment assignment.

## Required repair

Record the script-block scope in which each function or alias definition becomes visible. Exempt a later command only when that definition is visible from the command's scope, such as the same script block or an ancestor scope, while preserving offset ordering and the existing containing-definition recursion exemption. A definition in a child or sibling script block must not suppress top-level Bash evidence.

Add a direct classifier guard for the exact nested-function script that requires an `export` Bash finding. Preserve negative guards for a prior top-level `function export`, a prior supported top-level alias, and recursive use inside the containing definition. Add scope-boundary guards for child and sibling definitions so they cannot become global exemptions. Prove the positive guard red against the current flattened list and green after repair, run the repository verification entry point, and obtain fixed-SHA Claude Opus 5 review.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 448 lines of `TrustedPreflightClassifier.cs` at `864cfe2` in two bounded source/caller/test passes plus one whole-file AST/runtime integration pass. Focused classifier and shell-dialect wiring tests passed 88/88. Exact built-assembly reflection and PowerShell runtime probes confirmed the scope-flattening gap. Independent adjudication and integration accepted it at LOW, distinct from `opr-43` and `opr-44`. No product or test file changed in this finding slice.
