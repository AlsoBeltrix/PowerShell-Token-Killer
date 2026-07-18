# rbc-3: AuditRuntimeGate TryCreateCallContext bypasses the lifecycle gate

**Severity**: MAJOR
**Status**: Open (intake, awaiting owner triage)
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `server/PtkMcpServer/Audit/AuditRuntimeGate.cs:107-146`

## Evidence

`TryCreateCallContext` returns an `AuditCallContext` after only checking
`_disposed || _stopping`, never requiring `_lifecycle?.IsStarted == true`.
This differs from `TryBeginCall` (line 217) and `CanConstructRuntimeLocked`
(line 383), both of which enforce the lifecycle gate.

Callers of `TryCreateCallContext` can therefore obtain a context and
append audit records before `server.started` is durable, bypassing the
lifecycle invariant the rest of the gate is built around.

## Predicted observable failure

Audit records are appended out of lifecycle order — before
`server.started` — making the journal chain inconsistent for any
consumer that treats `server.started` as the anchor for the session's
audited activity.

## What

Either enforce the lifecycle gate in `TryCreateCallContext` (add the
`_lifecycle?.IsStarted != true` check that `TryBeginCall` enforces), or
document at the method and every call site that this is a diagnostic-only
path that must never be used for effectful audit records. If it is only
used by `ptk_state`, enforce that at the call site.

## Scope of fix

One method in `AuditRuntimeGate.cs`, plus call-site verification. No
architectural change.

## Guard proof

Not yet written. A guard should call `TryCreateCallContext` before
`server.started` is durable and assert it refuses (or returns a context
that cannot append), matching `TryBeginCall`'s behavior.

## Reviewer comments

Read-only review by Hermes subagent (audit subsystem pass). No external
fixed-SHA review has been dispatched.