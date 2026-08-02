# opr-32: Scope-qualified local function names cause false shell-dialect refusals

**Severity**: HIGH — a valid, parse-clean PowerShell script that defines a collision-named function with an explicit local or private scope is hard-refused before execution.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan defines which explicit function scopes count as script-local without broadening the existing nonlocal-definition exemption boundary.

**Source**: Method-sized no-tool Claude Opus 5 review of `server/PtkMcpServer/TrustedPreflightClassifier.cs` at `d437d49f5e3429a2427f4d42660690b2f23d8827`, followed by exact-head PowerShell AST, runtime, and built-assembly reflection probes and final contract adjudication.

## Evidence

`CollectLocalDefinitions` stores `FunctionDefinitionAst.Name` verbatim. PowerShell includes an explicit scope prefix in that value, so `function local:export` is recorded as `local:export` and `function private:export` as `private:export`. `IsLocallyDefined` later compares that stored value directly with the invoked command name `export`. The lexical definition therefore never exempts the subsequent use even though both scopes make the function visible at that use site.

Exact-head probes parsed both scripts without errors. The parser reported the scope-qualified names, direct execution invoked the functions successfully, and reflection against the rebuilt classifier returned `the bash 'export' builtin`. `RunspaceHost` treats that parse-clean non-null finding as a hard dialect refusal and does not start the pipeline.

This is distinct from `opr-17`: that finding begins after a literal `Set-Alias` or `New-Alias` command has been recognized and binds its arguments incorrectly. This finding loses the identity of a supported `FunctionDefinitionAst` because the declaration name is not normalized for explicit local scope.

## Predicted observable failure

`function local:export { param($value) "local:$value" }; export X=1` and its `private:` equivalent are returned as not-started Bash-dialect guidance even though the submitted PowerShell is parse-clean and executes the preceding local function.

## Required guard

Add classifier rows for explicit `local:` and `private:` function definitions lexically preceding collision-named uses and assert no finding. Add explicit nonlocal-scope controls so the repair cannot silently widen the approved script-local boundary. Temporarily revert only the name-normalization repair, prove both new local rows fail with a Bash-dialect finding, restore it, then run the focused classifier suite and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `HIGH`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- Dynamic function-provider definitions and broader execution-order modeling were rejected as outside the approved supported-form boundary.
