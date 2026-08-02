# opr-43: Fatal parse errors bypass trusted Bash command evidence

**Severity**: HIGH — common valid Bash scripts can lose automatic Bash delegation and fall through to PowerShell execution solely because the PowerShell recovery parser also reported an error.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan defines safe command-evidence scanning over PowerShell's partial recovery AST without weakening trusted resolution or local-definition gates.

**Source**: Bounded and whole-file Claude Opus 5 review of `TrustedPreflightClassifier` at `864cfe2`, confirmed by exact built-assembly reflection probes.

## Evidence

`server/PtkMcpServer/TrustedPreflightClassifier.cs:118-121` parses the script and enters a dedicated branch when PowerShell reports any parse error. That branch checks five error-adjacent shapes at `:123-175` and then returns `null` at `:176`. The normal command-evidence scan at `:179-263` is therefore unreachable for every fatal parse. The exact probe below proves that the recovery AST retains a complete Bash `set` command in this case. Whether the other allowlisted command-evidence shapes survive representative fatal parses remains unverified and must be enumerated before repair.

An exact reflection probe against the built assembly used the valid Bash script `set -euo pipefail; case "$1" in start) echo starting ;; *) echo usage ;; esac`. `AssessShellDialect` returned `PowerShellParseFatal=true` and `Finding=null`: the PowerShell parser recovered the leading `set` command, but the later Bash `case` syntax triggered errors not covered by the five shape checks.

`server/PtkMcpServer/RunspaceHost.cs:3186` obtains the assessment, and `:3191-3206` creates the Bash plan only when `Finding` is non-null. With this result it skips that branch and falls through to normal planning at `:3224`, so the valid Bash script reaches PowerShell and surfaces the engine parse failure instead of running through the available Bash path.

## Predicted observable failure

A selected shell route submits a valid Bash script containing a recognized command signal plus an unsupported Bash construct such as `case ... esac`. Bash and RTK are available, but the classifier discards the recovered command evidence because PowerShell also emitted a parse error. The invocation fails under PowerShell rather than executing through Bash.

## Required repair

After the existing error-shape checks, run the same trusted command-evidence scan over the recovered `CommandAst` nodes before concluding there is no dialect finding. Preserve shape-label precedence, `TrustedCommandSnapshot` resolution, local-definition exemptions, and `PowerShellParseFatal=true`; do not treat arbitrary partial-AST commands as Bash evidence beyond the existing allowlisted shapes.

Add a direct classifier guard for the exact `set` plus `case` script that requires `PowerShellParseFatal=true` and the established Bash `set` finding. Add an active `RunspaceHost` wiring guard proving an available Bash path is selected and PowerShell does not execute the script. Add negative partial-AST cases that contain no allowlisted Bash evidence and remain unclassified. Prove the positive guards red against the current early return and green after repair, run the repository verification entry point, and obtain fixed-SHA Claude Opus 5 review.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 448 lines of `TrustedPreflightClassifier.cs` at `864cfe2` in two bounded source/caller/test passes plus one whole-file AST/runtime integration pass. Focused classifier and shell-dialect wiring tests passed 88/88. Exact built-assembly reflection and PowerShell runtime probes confirmed the candidate. Independent adjudication and integration accepted it at HIGH, distinct from `opr-17`, `opr-32`, `opr-33`, the named `set -o` option gap, and nested-definition scope flattening. No product or test file changed in this finding slice.
