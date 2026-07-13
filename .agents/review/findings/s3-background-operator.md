# s3-background-operator: RTK routing drops the PowerShell background operator

**Severity**: MEDIUM — an asynchronous native submission is silently changed
into synchronous RTK execution with different timing, output, job, and timeout
semantics.
**Status**: Verified
**Branch**: `fix/s3-background-operator`
**Commit**: `923f8a522b4e662c83ad3fa8351cb1f88e2dbd6f`

## Evidence

`server/PtkMcpServer/Execution/ExecutionPlanner.cs:256` accepts a single-command
`PipelineAst` without checking `pipeline.Background`. The RTK execution script
at line 147 uses `command.Extent.Text`; PowerShell excludes the trailing `&`
from both the command and pipeline extents. `ClassifyDomain` at line 410 also
reports this shape as `NativeTerminal`.

The coder audit reproduced `bash -c 'sleep 1; printf done' &` at `669ce6e`.
Auto route took 1,085 ms and returned `done`, proving synchronous RTK
execution. `route=pwsh` returned in 123 ms with a running `PSRemotingJob`.

## Predicted observable failure

A command intended to run asynchronously blocks the tool call, returns the
wrong output/job state, and can consume the foreground deadline. Audit also
records a native/RTK route rather than the exact mixed/control-flow semantics.

## What

The structured planner omits the AST's background flag from its fidelity gate,
then constructs routed text from an extent that cannot preserve that flag.

## Approach

`GetEligibleCommand` now rejects `pipeline.Background`, while
`ClassifyDomain` labels the shape `MixedDataflow`. Auto and forced RTK requests
therefore execute the exact original text, with forced routing returning the
truthful `RtkIneligibleShape` label.

## Files changed

- `server/PtkMcpServer/Execution/ExecutionPlanner.cs` — exclude background
  pipelines from RTK and label their dataflow honestly.
- `server/PtkMcpServer.Tests/ExecutionPlannerTests.cs` — guard auto and forced
  route behavior, exact text, and fallback reason.

## Guard proof

- `Keeps_a_background_native_pipeline_on_the_exact_PowerShell_path` failed
  before the production correction and passed afterward.
- Claude independently removed the eligibility check and observed the exact
  route/text assertion fail, then removed the domain check and observed the
  `MixedDataflow` assertion fail. Both restorations passed.

## Coder dispute (if any)

None. The finding was independently reproduced and admitted by the coder.

## Known gaps

None currently.

## Reviewer comments

Coder integrated audit against fixed head `669ce6e`, recorded
2026-07-13T01:48:24Z. This finding is independent of Claude's two integrated
findings and blocks Slice 3 acceptance under the same review loop.

Claude Code 2.1.207 (`claude-opus-4-8`) reviewed
`66f22fac482f242e1d70c24763ef8c01eb49d97d..923f8a522b4e662c83ad3fa8351cb1f88e2dbd6f`
with `guard_confirmed=true` and verdict `accepted`, recorded
2026-07-13T03:21:14Z. The exact head passed 1,014/1,014 .NET, 139 Pester with
two skips, and the handshake; its worktree was clean and removed.
