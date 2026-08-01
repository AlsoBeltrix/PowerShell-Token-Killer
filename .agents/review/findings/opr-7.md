# opr-7: Quota control is published before its format marker is durable

**Severity**: MEDIUM — one interrupted first initialization can leave a persistent malformed control file that blocks every later audit spool acquisition until manual repair.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan covers atomic audit quota-control publication and crash recovery.

**Source**: Bounded Claude Opus 5 review of current production code.

## Evidence

`server/PtkMcpServer/Audit/AuditSpoolQuotaLease.cs:181-203` creates `.ptk-audit-quota.lock` at its final path with `SecureAuditStorage.CreateExclusiveFile`, then writes the one-byte `0x50` format marker and flushes it. A process termination or write/flush failure between creation and durable marker publication leaves the final control entry at zero length; no rollback or incomplete-publication recovery exists.

Every later `CreateControlAndAcquire` sees the entry at lines 183-184 and skips initialization. `AcquireExisting` and `TryAcquireExisting` open it under the exclusive lease and `VerifyRetainedControl` rejects any length other than one at lines 222-228. `AuditSpoolQuotaLeaseTests.MalformedControls` deliberately confirms that an arbitrary zero-length retained control must fail closed, so weakening existing-control validation is not a safe repair.

## Predicted observable failure

On a fresh spool, the process creates the quota control and is terminated, loses the volume, or receives a write/flush error before the marker is durable. All later writer and administration attempts against that spool throw `IOException` reporting an invalid quota control. The product has no automatic recovery path, so one interrupted initialization permanently makes the protected spool unavailable until an operator identifies and removes the partial file.

## Required repair

Publish a fully written, flushed, protected one-byte control atomically at the final path. Concurrent creators must still converge on one valid persistent control; existing `AcquireExisting` and `TryAcquireExisting` behavior must continue rejecting arbitrary missing, malformed, linked, or misprotected controls. Add a deterministic fault hook around pre-publication initialization and guards proving interruption never leaves a final malformed control, concurrent creators converge, and malformed externally retained controls remain rejected. Prove the interruption guard fails against current code, restore the repair, run the repository verification entry point, and obtain fixed-SHA Claude Opus 5 review before integration.

## Reviewer

Claude Code 2.1.220 using owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, frontier, read-only review of `server/PtkMcpServer/Audit/AuditSpoolQuotaLease.cs` at `09b3c6ab62d27b966a42f9ab138011b6403e3844`. Verdict: `finding`.
