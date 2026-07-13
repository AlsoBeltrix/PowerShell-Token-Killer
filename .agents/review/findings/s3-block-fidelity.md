# s3-block-fidelity: clean and dynamicparam blocks can be dropped by RTK routing

**Severity**: MEDIUM — an eligible-looking end block can cause other submitted
PowerShell blocks, including cleanup, to be silently omitted.
**Status**: Verified
**Branch**: `fix/s3-block-fidelity`
**Commit**: `561c56136bfb895d7278ad1a320cfcc3c8cb9dcc`

## Evidence

`server/PtkMcpServer/Execution/ExecutionPlanner.cs:260` and
`server/PtkMcpServer/Execution/ExecutionPlanner.cs:416` reject
`ParamBlock`/`BeginBlock`/`ProcessBlock`, but not `CleanBlock` or
`DynamicParamBlock`. The same file's mixed-guidance classifier already rejects
both omitted blocks.

Claude parsed and executed
`clean { ... } end { git status }`, confirmed both blocks normally run, then
confirmed the planner at `669ce6e` constructs only the RTK-wrapped `git status`
command. The clean block is absent from the execution script.

## Predicted observable failure

Cleanup or dynamic-parameter logic can be skipped without a label, including
lock release, temporary-file deletion, or state restoration. Audit records the
submission as native/RTK even though the submitted PowerShell semantics were
not executed.

## What

The new structured planner does not include all named PowerShell script-block
forms in its fidelity exclusions, so its end-block extraction can discard
other executable blocks.

## Approach

`ExecutionPlanner.GetEligibleCommand` now rejects `CleanBlock` and
`DynamicParamBlock`, while `ClassifyDomain` returns `MixedDataflow` for either.
The exact original script therefore stays on `PowerShellDirect`; the end block
is never extracted away from its sibling blocks.

## Files changed

- `server/PtkMcpServer/Execution/ExecutionPlanner.cs` — complete the
  supplemental-block fidelity exclusions in eligibility and domain metadata.
- `server/PtkMcpServer.Tests/ExecutionPlannerTests.cs` — guard clean and
  dynamicparam submissions independently.

## Guard proof

- `Keeps_clean_and_dynamicparam_blocks_on_the_exact_PowerShell_path` failed
  both cases before the production correction and passed 2/2 after it.
- Claude independently removed the eligibility exclusions and observed both
  exact-text guards fail, then separately removed the domain exclusions and
  observed both `MixedDataflow` guards fail. Each restoration passed focused
  38/38.

## Coder dispute (if any)

None. The finding is independently admitted.

## Known gaps

The accepted review found the distinct top-level using-statement omission;
that is tracked separately as `s3-using-statement-fidelity`.

## Reviewer comments

Claude Code 2.1.207 (`claude-opus-4-8`) reviewed
`0c08379a02c796b8ea0e1779c196840c6a9b1269..669ce6ea47c520a9c3bb73411192630d56ed519b`
with `guard_confirmed=true` and verdict `reopened`, recorded
2026-07-13T01:48:24Z.

Claude Code 2.1.207 (`claude-opus-4-8`) reviewed
`7758c8cf5864ffcaebab9dad70a1ecbd5ccf0df4..561c56136bfb895d7278ad1a320cfcc3c8cb9dcc`
with `guard_confirmed=true` and verdict `accepted`, recorded
2026-07-13T02:50:35Z. The restored exact head passed 1,012/1,012 .NET, 139
Pester with two platform skips, and the zero-warning handshake; its detached
worktree was clean and removed.
