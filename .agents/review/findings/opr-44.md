# opr-44: Named Bash `set -o` options evade dialect detection

**Severity**: MEDIUM — valid Bash scripts using common named shell options can be planned as PowerShell and fail under the stock `set` alias instead of taking the Bash path.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan defines a conservative named-option allowlist that preserves valid PowerShell `Set-Variable` forms.

**Source**: Bounded and whole-file Claude Opus 5 review of `TrustedPreflightClassifier` at `864cfe2`, confirmed by exact built-assembly reflection and PowerShell runtime probes.

## Evidence

`server/PtkMcpServer/TrustedPreflightClassifier.cs:221-250` recognizes Bash `set` options only when the first argument is a `CommandParameterAst` and every later element is either an allowlisted flag parameter or the literal `pipefail`. That predicate therefore recognizes `set -o pipefail` but rejects other named Bash options.

An exact built-assembly reflection probe for `set -o errexit; cp -r src out` returned `PowerShellParseFatal=false` and `Finding=null`. The script is valid Bash. Under PowerShell, the stock `set` alias resolves to `Set-Variable`; an exact runtime probe bound `-o` to `-Option` and failed because `errexit` is not a `ScopedItemOptions` value. With no dialect finding, the active `RunspaceHost` caller falls through to normal PowerShell planning.

The ambiguity is narrow. An exact PowerShell probe showed that `set +e` is valid PowerShell, so a blanket rule for every `+flag` spelling would introduce false Bash classification. The accepted candidate covers named Bash values following `-o`, not all plus-prefixed arguments.

## Predicted observable failure

A valid Bash script begins with `set -o errexit`, `set -o nounset`, or another named Bash shell option not currently allowlisted. Bash is available, but the classifier reports no finding. The `set -o errexit` statement fails when `-o` binds to `Set-Variable -Option` and `errexit` is not a `ScopedItemOptions` value, so the requested shell option is never applied. Behavior of the remainder of the script after that failure was not established.

## Required repair

Recognize an explicit, case-insensitive set of named Bash options only when the name immediately follows the `-o` parameter. Inventory the supported Bash option names and exclude spellings that can represent valid PowerShell `ScopedItemOptions` or other accepted `Set-Variable` syntax. Preserve the existing trusted command-resolution check, local-definition exemption, and the conservative treatment of ambiguous `+flag` forms.

Add direct classifier and active wiring guards for `set -o errexit` and at least one second named Bash option. Add negative guards proving `set +e`, a valid short PowerShell `Set-Variable` form such as `set foo -o ReadOnly`, and an unknown named option remain unclassified. Prove the positive guards red against the current `pipefail`-only predicate and green after repair, run the repository verification entry point, and obtain fixed-SHA Claude Opus 5 review.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 448 lines of `TrustedPreflightClassifier.cs` at `864cfe2` in two bounded source/caller/test passes plus one whole-file AST/runtime integration pass. Focused classifier and shell-dialect wiring tests passed 88/88. Exact built-assembly reflection and PowerShell runtime probes confirmed the narrow named-option gap and disproved blanket `+flag` classification. Independent adjudication integration accepted MEDIUM, distinct `opr-43`; separate nested-definition-scope candidate remains pending record. No product or test file changed in finding slice.
