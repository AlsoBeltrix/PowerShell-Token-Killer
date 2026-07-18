# rbc-6: No SIGKILL escalation for Unix process trees after SIGTERM grace

**Severity**: MAJOR
**Status**: Open (intake, awaiting owner triage)
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**Files**: `server/PtkMcpServer/Execution/BashProcessRunner.cs:736-744`,
`server/PtkMcpServer/Execution/RtkProcessRunner.cs:403-430`

## Evidence

`TryKillProcessTree` (`BashProcessRunner.cs:736`) and `KillAndDrainAsync`
(both files) call `process.Kill(entireProcessTree: true)` followed by a
bounded `WaitForExitAsync` with `ProcessStopGrace = 2s`.

On Unix, `Kill(entireProcessTree: true)` sends `SIGTERM`, not `SIGKILL`.
A child that catches `SIGTERM` (a common pattern for graceful-shutdown
handlers that hang, or a malicious process) can survive the 2-second
grace. `stopped` is then set to `false` and the result reports
`InvokeDisposition.OutcomeUnknown` with "PTK will not retry it" —
correct for the audit boundary, but the process is still running.

There is no escalation to `SIGKILL` after the grace expires.

## Predicted observable failure

A Bash/RTK descendant that traps `SIGTERM` survives the containment
window, keeps running (holding resources, network connections, or
file locks), and is reported as `OutcomeUnknown`. The supervisor moves
on, but the orphaned process persists on the host.

## What

After the `SIGTERM` grace expires, escalate to `SIGKILL` for the process
tree (or for the process group if a negative PID is available). On
Unix, `kill(-pgid, SIGKILL)` reaches the whole group; the .NET
`Process.Kill` tree-walk does not escalate. A platform-specific
follow-up kill is needed.

## Scope of fix

One helper method shared by `BashProcessRunner` and `RtkProcessRunner`.
No architectural change; the `OutcomeUnknown` audit boundary is
preserved — the escalation is best-effort containment, not a retry.

## Guard proof

Not yet written. A guard should launch a child that traps `SIGTERM`
and sleeps, verify it survives the 2s grace, then verify the `SIGKILL`
escalation reaps it.

## Reviewer comments

Read-only review by Hermes subagent (execution/worker subsystem pass).
No external fixed-SHA review has been dispatched.