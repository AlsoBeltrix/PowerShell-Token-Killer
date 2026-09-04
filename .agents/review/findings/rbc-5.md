# rbc-5: Background jobs lack Job Object containment on Windows

**Severity**: MAJOR
**Status**: Closed as inapplicable to the current product (2026-09-04)
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**Files**: `server/PtkMcpServer/JobManager.cs:1820, 1962`

## Evidence

Both `TryRequestContainment` (line 1820) and `RequestKill` (line 1962) use
`Process.Kill(entireProcessTree: true)`. On Windows, this relies on the
.NET runtime's tree-walk (`CreateToolhelp32Snapshot` + kill descendants),
not a Job Object.

The foreground worker path uses `WindowsProcessTreeSupervisor` with a
Job Object and `KillOnJobClose` (`WindowsProcessTreeSupervisor.cs:44, 101`),
which the OS enforces even if the supervisor is hard-killed. Background
`JobManager` jobs have no Job Object containment. If the supervisor
process is hard-killed, background job trees are orphaned and keep
running.

The code acknowledges this via `RootTerminationConfirmed` tracking
(line 1426) and `ShutdownCoreAsync` throwing if any root is unconfirmed
(line 2100), but these only help on graceful shutdown — a supervisor
crash leaves the tree running with no one to kill it.

## Predicted observable failure

A supervisor crash (or `kill -9`) leaves a cold background job's process
tree running on Windows indefinitely, consuming resources and
potentially holding locks or network connections. No OS-level mechanism
reaps the tree because no Job Object owns it.

## What

Apply the same `WindowsProcessTreeSupervisor` Job-Object containment to
background jobs, or document that background jobs accept weaker
containment than foreground workers by design. If the latter, record
the decision and the operational mitigation (e.g., the shutdown
unconfirmed-root throw) in `.agents/decisions.md`.

## Scope of fix

Either wire `WindowsProcessTreeSupervisor` into the `JobManager`
background start path (larger), or record the accepted risk (smaller).
Depends on the owner's containment posture decision.

## Guard proof

Not yet written. If wired, a guard should assert that a hard-killed
supervisor leaves no orphaned background job tree on Windows
(verified via a Job-Object close callback or a post-crash process
scan).

## Disposition

The 2026-07-19 ruling targeted the then-proposed resilience R7 surface. The
later approved production-reliability salvage plan superseded that topology:
its `ptk_job` section removes both cold `ptk_job` and
`ptk_invoke(background=true)`, and its do-not-port list explicitly excludes
the R7 guardian cutover and guardian-owned job registry. Current source has no
`JobManager`, background invocation parameter, or public job tool. There is
therefore no current background-job execution path to contain.

Close this finding as inapplicable; do not recreate the removed surface merely
to satisfy its historical guard. If a later owner-approved job plan adds
background execution, that plan must establish a fresh creation-time Windows
containment contract and guard. The saved spawn-then-assign WIP remains
rejected because its admitted escape race is still unsound.

## Reviewer comments

Read-only review by Hermes subagent (execution/worker subsystem pass).
No external fixed-SHA review has been dispatched.
