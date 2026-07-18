# rbc-2: AuditRuntimeGate StopCoreAsync does not guarantee server.stopped on session/exporter failure

**Severity**: MAJOR
**Status**: Open (intake, awaiting owner triage)
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `server/PtkMcpServer/Audit/AuditRuntimeGate.cs:350-362`

## Evidence

`StopCoreAsync` awaits `sessionLifetime.ShutdownAsync()` and
`resources.StopExporterAsync()` without any try/catch. If either throws
during graceful drain, the entire `StopAsync` task faults, the host
records a faulted shutdown, and `Dispose` (line 320) captures the same
exception and rethrows — but `lifecycle?.Stop()` on line 361 never runs,
so `server.stopped` is never appended to the journal.

This violates the lifecycle contract that the stop record is the final
record in the audit journal.

## Predicted observable failure

A session-lifetime or exporter drain failure during shutdown leaves
the journal without a terminal `server.stopped` event. An operator or a
later startup-recovery pass cannot distinguish a clean shutdown from a
crash, and the chain appears truncated.

## What

Wrap the `sessionLifetime.ShutdownAsync()` and
`resources.StopExporterAsync()` calls in try/catch (downgrade to logged
warnings or a health marker), so `lifecycle?.Stop()` always runs and
`server.stopped` is appended as the final record even when the
session/exporter drain faults.

## Scope of fix

One method in `AuditRuntimeGate.cs`. No architectural change.

## Guard proof

Not yet written. A guard should inject a throwing `ISessionLifetime`
and/or a throwing exporter and assert `lifecycle.Stop()` still runs and
`server.stopped` is the final journal record.

## Reviewer comments

Read-only review by Hermes subagent (audit subsystem pass). No external
fixed-SHA review has been dispatched.