# opr-57: Expression redirections are mislabeled as plain PowerShell

**Severity**: LOW — execution remains PowerShellDirect, but durable audit routing metadata incorrectly records file-writing expression pipelines as `powershell` instead of `mixed_dataflow`.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan makes domain classification inspect redirections before narrowing the pipeline element to `CommandAst`.

**Source**: Complete-source Claude Opus 5 review of `server/PtkMcpServer/Execution/ExecutionPlanner.cs` at `1de0286` (blob `234c3833b6be6a22d6d984058090fd252f7244ba`), integrated with PowerShell AST probes, `AuditCallContext`, audit schema, and focused planner/audit tests.

## Evidence

`ExecutionPlanner.ClassifyDomain` returns `ExecutionDomain.PowerShell` when the sole pipeline element is not `CommandAst`, then checks `command.Redirections.Count` only after that narrowing. PowerShell parser probes showed `'text' > out.txt`, `(Get-Date) >> out.txt`, and `1 + 2 > out.txt` are single `PipelineAst` statements whose sole `CommandExpressionAst` element carries one redirection. Each therefore exits through the early `PowerShell` branch even though the same classifier labels a redirected `CommandAst` as `MixedDataflow`.

`AuditCallContext.AuthorizePlanAsync` persists `plan.Domain?.ToMachineCode()` into routing metadata, and the audit schema accepts the resulting `powershell` value. Current classification tests cover command redirection but not a `CommandExpressionAst` redirection, so focused tests pass while the inconsistent durable label remains.

## Predicted observable failure

An invocation writes an expression result to a file. Execution succeeds on PowerShellDirect, but its audit event records `domain=powershell`; an equivalent command-form redirection records `domain=mixed_dataflow`. Operators and downstream audit analysis therefore undercount or misclassify file-writing mixed dataflow based only on AST element form.

## Required repair

In `ClassifyDomain`, inspect the sole `CommandBaseAst` pipeline element's redirections before returning `PowerShell` for a non-`CommandAst` element. Keep execution eligibility unchanged. Add independent literal, parenthesized-command, and binary-expression redirection guards proving `MixedDataflow`, plus an audit-context guard proving the durable routing domain. Retain a nonredirected expression guard proving it remains `PowerShell`.

Temporarily revert only the classification change, prove the new guards fail, restore it, then run focused planner and audit-context tests plus the repository verification entry point.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) accepted this as a distinct current LOW finding after bounded source review and focused adjudication. The impact is intentionally limited to audit-visible metadata; no execution-path or side-effect claim is made. No product or test file changed in this finding slice.
