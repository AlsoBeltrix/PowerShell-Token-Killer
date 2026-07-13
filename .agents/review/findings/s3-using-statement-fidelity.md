# s3-using-statement-fidelity: top-level using statements can be dropped by RTK routing

**Severity**: MEDIUM — a submitted module/assembly import can be silently
omitted when the end block looks like an eligible native command.
**Status**: Verified
**Branch**: `fix/s3-using-statement-fidelity`
**Commit**: `b7ab1a3c164a5aaf8957fe7725d8c9bd113f53bc`

## Evidence

`server/PtkMcpServer/Execution/ExecutionPlanner.cs:258` and
`server/PtkMcpServer/Execution/ExecutionPlanner.cs:416` do not inspect
`ast.UsingStatements`, although the same file's mixed-guidance path does.
`using module Foo; git status` can therefore yield a single-command end block
that is reconstructed as only `& '<rtk>' git status`.

## Predicted observable failure

The declared module or assembly is never imported, its initialization side
effects do not occur, and audit labels the submission native/RTK even though
the exact submitted PowerShell text was not executed.

## What

The structured planner extracts an eligible end-block command without treating
top-level using statements as a fidelity exclusion.

## Approach

`GetEligibleCommand` rejects any nonempty `UsingStatements` collection, and
`ClassifyDomain` labels the shape `MixedDataflow`. Both routes retain the
byte-exact original PowerShell submission.

## Files changed

- `server/PtkMcpServer/Execution/ExecutionPlanner.cs` — exclude using
  statements from RTK eligibility and native-terminal classification.
- `server/PtkMcpServer.Tests/ExecutionPlannerTests.cs` — guard exact execution
  text and truthful domain metadata.

## Guard proof

- `Keeps_top_level_using_statements_on_the_exact_PowerShell_path` failed before
  the production correction and passed afterward.
- Claude independently removed the eligibility check and observed the
  byte-exact execution guard fail, then removed the domain check and observed
  the `MixedDataflow` assertion fail. Each restoration passed focused 39/39.

## Coder dispute (if any)

None. The accepted `s3-block-fidelity` reviewer provided a concrete
non-duplicate reproduction and the candidate is admitted.

## Known gaps

None currently.

## Reviewer comments

Claude Code 2.1.207 (`claude-opus-4-8`) identified this separate material
case while accepting `s3-block-fidelity` at fixed head `561c561`, recorded
2026-07-13T02:50:35Z. It does not reopen the accepted clean/dynamicparam fix.

Claude Code 2.1.207 (`claude-opus-4-8`) reviewed
`2b9b28cb4f187a72803811c252de2637bfa340ea..b7ab1a3c164a5aaf8957fe7725d8c9bd113f53bc`
with `guard_confirmed=true` and verdict `accepted`, recorded
2026-07-13T03:11:12Z. The restored exact head passed 1,013/1,013 .NET, 139
Pester with two platform skips, and the handshake; the worktree was clean and
removed.
