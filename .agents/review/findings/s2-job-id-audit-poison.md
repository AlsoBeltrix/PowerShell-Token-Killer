# s2-job-id-audit-poison: reject missing job identifiers before audit admission

**Severity**: HIGH — one model-facing call can permanently disable every audited
operation until the server restarts.
**Status**: Open
**Branch**: `fix/s2-job-id-audit-poison`
**Commit**: pending

## Evidence

`server/PtkMcpServer/Audit/AuditCallMetadata.cs` accepts `ptk_job` status,
output, or kill calls when `id` is absent. `server/PtkMcpServer/Tools/JobTool.cs`
then receives its default `id=0` and passes that value into audit appends.
`server/PtkMcpServer/Audit/AuditEvent.cs` rejects non-positive job identifiers;
the resulting schema failure marks audit health unavailable and has no in-process
recovery path. `server/PtkMcpServer/Audit/AuditCallFilter.cs` can also derive its
fallback terminal before replacing the client response with the audit-persistence
refusal.

## Predicted observable failure

A model calls `ptk_job` with `action=status`, `output`, or `kill` and omits `id`.
The call is admitted and then poisons audit health with `journal.schema`; every
later audited call is refused until process restart. The terminal record can say
completed even though the client receives a refusal.

## What

Require a positive job identifier for every job-specific action at the metadata
boundary, before reservation or audit admission, and derive fallback terminal
state after any audit-persistence refusal has replaced the response.

## Approach

Pending implementation. Keep `list` as the only action that does not require an
identifier. Add an end-to-end omitted-ID guard that proves the malformed call
cannot poison health and a later valid audited call still succeeds.

## Files changed

- Pending implementation.

## Guard proof

- Pending red-to-green proof for omitted `ptk_job` identifiers and fallback
  terminal truthfulness.

## Coder dispute (if any)

None.

## Known gaps

None identified.

## Reviewer comments

Claude Code 2.1.207 (`claude-fable-5`) reviewed fixed head
`6cbd1d3061985f06bb0a5da8bcf2faa84a5bb826` against
`78e256ca0f3b1253aa97dd984f1d913429ea452a`, `guard_confirmed=true`, verdict
`reopened`, recorded 2026-07-12T14:33:24Z. The reviewer verified the complete
omitted-ID failure chain and classified it as a model-triggerable, fail-closed
process-lifetime availability loss with a companion false completed terminal.
