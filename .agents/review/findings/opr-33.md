# opr-33: Alias-cmdlet command identity spellings cause false shell-dialect refusals

**Severity**: HIGH — valid, parse-clean PowerShell that creates a local collision-named alias through a module-qualified alias cmdlet or its resolved stock alias is hard-refused before execution.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan defines side-effect-free semantic command-identity recognition without trusting shadowable alias spellings by text alone.

**Source**: Method-sized no-tool Claude Opus 5 review of `server/PtkMcpServer/TrustedPreflightClassifier.cs` at `d437d49f5e3429a2427f4d42660690b2f23d8827`, followed by exact-head PowerShell AST, runtime, and built-assembly reflection probes and final contract adjudication.

## Evidence

`CollectLocalDefinitions` enters alias-definition binding only when the first `CommandAst` element is the literal string `Set-Alias` or `New-Alias`. PowerShell also invokes those same cmdlets through module-qualified names such as `Microsoft.PowerShell.Utility\Set-Alias` and through the stock `sal` and `nal` aliases. Those command identities fail the literal comparison, so the collector never records the local alias definition even when its name and value use the already-supported positional form.

Exact-head probes parsed the module-qualified and stock-alias scripts without errors. Their `CommandAst` names retained the alternate spelling, direct execution created `export` and printed `X=1`, and reflection against the rebuilt classifier returned `the bash 'export' builtin`. `RunspaceHost` turns that parse-clean non-null finding into a hard refusal before starting the pipeline.

This is distinct from `opr-17`: that finding recognizes the alias cmdlet and then binds otherwise valid argument orderings incorrectly. This finding never reaches argument binding because it compares invocation spelling instead of proven command identity. The module-qualified reproducer establishes the defect independently of session alias resolution; stock `sal` and `nal` are the same identity-recognition root, but must be honored only when trusted resolution proves what they invoke.

## Predicted observable failure

`Microsoft.PowerShell.Utility\Set-Alias export Write-Output; export X=1`, `sal export Write-Output; export X=1`, and the corresponding `New-Alias`/`nal` forms are returned as not-started Bash-dialect guidance even though each submitted PowerShell script is parse-clean and its preceding local alias definition executes successfully.

## Required guard

Add no-finding classifier rows for module-qualified `Set-Alias` and `New-Alias`, plus `sal` and `nal` when the trusted command snapshot proves their stock alias definitions. Add controls where functions or aliases shadow `sal` and `nal` so textual spelling alone cannot exempt a later collision-named use. Temporarily revert only the semantic command-identity repair, prove the supported alternate-spelling rows fail with Bash-dialect findings, restore it, then run the focused classifier suite and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `HIGH`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- Splatted alias definitions were rejected as dynamic and remain outside this finding's supported literal-binding boundary.
