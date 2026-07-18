# rbc-3: AuditRuntimeGate TryCreateCallContext bypasses the lifecycle gate

**Severity**: MAJOR
**Status**: RESOLVED — refuted at triage 2026-07-18: the claimed missing check
exists at the reviewed head `f6a2caa` and has existed since the file's first
commit (`460c106`). No code change.
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

Already present in the suite (verified at triage, no new test needed):

- `AuditRuntimeGateTests.cs:393`
  (`Diagnostic_only_shutdown_is_idempotent_and_never_reopens_storage`) —
  `TryCreateCallContext` refuses after failed startup + stop, i.e. with no
  durable `server.started`.
- `AuditEvidenceOrphanReconcilerTests.cs:175` and `:238` — refusal while
  startup reconciliation holds the runtime `Unavailable` (no durable
  `server.started` yet); admission at `:180`/`:245` only after the blocker
  clears and the full serialized startup — including the durable
  `server.started` append — completes inside the same call.

## Triage resolution (2026-07-18, head `a4cff13`)

The Evidence section is a misread. `TryCreateCallContext` performs **two**
`_gate` checks: the early fast-path (`_disposed || _stopping` only, lines
116–124) and a second check after `TryInitializeSerialized` (lines 133–140)
that enforces exactly the clause the finding says is absent:

    if (_disposed || _stopping ||
        (!_testOperational && _lifecycle?.IsStarted != true))

- `git show f6a2caa:…/AuditRuntimeGate.cs` confirms the clause at the
  reviewed head; `git log -L` attributes it to `460c106`
  ("feat: add fail-closed audit foundation") — it was never missing.
- The invariant is also structural: `_lifecycle`/`_journal` are published
  together (under `_gate`) only after `candidateLifecycle.EnsureStarted()`
  returns, and `AuditServerLifecycle` sets `_state = Started` only after the
  `server.started` append succeeds. There is no reachable state with a
  non-null `_journal` and an un-started lifecycle.
- `_testOperational` is only set by the `internal static`
  `CreateOperationalForTests` factory, referenced solely from
  `PtkMcpServer.Tests` (`AuditCallFilterTests.cs:375`,
  `AuditPreEffectGuardTests.cs:2049`). No production path can bypass the
  gate.

The likely source of the misread: reviewing the method's opening block
(lines 107–124) and stopping at the first `return`, missing the post-init
re-check.

## Reviewer comments

Read-only review by Hermes subagent (audit subsystem pass). No external
fixed-SHA review has been dispatched. Triage refutation performed in-session
against both the reviewed head and current `master`.