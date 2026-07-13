# s3-using-statement-fidelity: top-level using statements can be dropped by RTK routing

**Severity**: MEDIUM — a submitted module/assembly import can be silently
omitted when the end block looks like an eligible native command.
**Status**: Open
**Branch**: `fix/s3-using-statement-fidelity`
**Commit**: pending

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

Pending implementation. Reject any nonempty `UsingStatements` collection from
RTK eligibility and classify it as `MixedDataflow`, preserving the byte-exact
PowerShell submission.

## Files changed

- Pending.

## Guard proof

- Pending: a parse-valid using-module submission with an Application-backed
  end command must plan `PowerShellDirect`, `MixedDataflow`, and exact original
  execution text. Removing either production check must fail its assertion.

## Coder dispute (if any)

None. The accepted `s3-block-fidelity` reviewer provided a concrete
non-duplicate reproduction and the candidate is admitted.

## Known gaps

None currently.

## Reviewer comments

Claude Code 2.1.207 (`claude-opus-4-8`) identified this separate material
case while accepting `s3-block-fidelity` at fixed head `561c561`, recorded
2026-07-13T02:50:35Z. It does not reopen the accepted clean/dynamicparam fix.
