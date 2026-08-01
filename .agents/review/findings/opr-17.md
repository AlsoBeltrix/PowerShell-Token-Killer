# opr-17: Alias parameter ordering causes false shell-dialect refusals

**Severity**: HIGH — valid, parse-clean PowerShell is hard-refused before its
pipeline starts.

**Status**: Accepted; unplanned. Product and test changes are blocked until an
approved plan defines the alias-binding boundary and deterministic guards.

**Source**: Bounded no-tool Claude Opus 5 review of
`TrustedPreflightClassifier`, grounded by direct PowerShell execution and
classifier reflection probes run by the working session at `95b22a6`.

## Evidence

`TrustedPreflightClassifier.CollectLocalDefinitions` recognizes `Set-Alias`
and `New-Alias`, but its element scan stops at the first
`CommandParameterAst` unless that parameter is `-Name`. PowerShell permits
named parameters in arbitrary order and permits switches before positional
arguments.

Four current-head probes execute successfully under `pwsh -NoProfile`, exit
zero, and print `X=1`, while the classifier returns
`the bash 'export' builtin` for each:

- `Set-Alias -Force export Write-Output; export X=1`
- `Set-Alias -Value Write-Output -Name export; export X=1`
- `Set-Alias -Scope Local -Name export -Value Write-Output; export X=1`
- `New-Alias -Force export Write-Output; export X=1`

Code inspection shows that, for a parse-clean non-null finding,
`RunspaceHost` returns `FormatDialectRefusal` and never starts the user
pipeline. The defect therefore reaches the normal trusted preflight path as a
hard false refusal, not a diagnostic nudge.

## Predicted observable failure

An auto-routed call containing one of the valid preceding alias definitions is
returned as not started with bash-dialect recovery guidance. Reordering the
same valid PowerShell parameters into one of the narrow forms currently
recognized makes the refusal disappear.

## Required repair boundary

Bind literal `Set-Alias` and `New-Alias` definitions independently of named
parameter order, named/positional mixing, attached values, and intervening
switches. Preserve lexical preceding-definition ordering and the recorded
non-exemption of explicit global scope in
`TrustedPreflightClassifierTests.Later_or_unsupported_local_definitions_do_not_exempt_the_use`.
The plan must audit that existing theory for rows whose only unsupported trait
is parameter ordering; an intentional flip to exemption is not a regression,
but each flip must be recorded in the theory with its reason.
Dynamic, ambiguous, or splatted alias definitions must follow an explicitly
planned conservative outcome consistent
with the high-precision rule in `.agents/plans/shell-dialect.md` that false
refusal is worse than a missed bash shape. Those dynamic forms are in scope
only for this conservative-default decision, not for behavioral modeling by
the finding.

The implementation mechanism is not settled by this finding. In particular,
whether binding uses bounded metadata captured by trusted host code or a local
fixed model must be decided and proved in the plan; classification must remain
side-effect-free and must not re-enter user code.

## Required guard

Add the four reproduced `Set-Alias` and `New-Alias` scripts as no-refusal
cases, plus `New-Alias -Value Write-Output -Name export; export X=1` to cover
a non-switch-leading ordering on the second cmdlet. Add order-independent
negative guards that explicit global scope remains non-exempt and that a
different alias name does not exempt `export`. Keep later-definition and true
bash-positive cases unchanged. Temporarily revert only the alias-binding repair
and prove each new local-alias no-refusal row fails for the expected false
refusal. The negative rows must pass both before and after the repair. Restore
it, run the focused classifier suite, then run the full server solution.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`
(`frontier`, `max`, no-tool, session-only); bounded finding and severity
adjudication at `95b22a6`.

- Verdict: `accepted`; final severity `HIGH`; confidence `high`.
- `guard_confirmed=false`; no repair has been implemented or tested.
- The finding is limited to literal alias-definition binding and the resulting
  false refusal; broader execution-order modeling remains outside its accepted
  scope, and dynamic definitions require only the planned conservative default
  stated above.
