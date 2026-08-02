# opr-35: Poisoned journal falsely clears ambiguous evidence reference

**Severity**: HIGH — an ambiguous audit flush can make script evidence retention-eligible even though the corresponding audit record is preserved and recovered later.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan makes poisoned-journal evidence scans incomplete without weakening complete retained-spool reconciliation.

**Source**: Complete bounded no-tool Claude Opus 5 review of `server/PtkMcpServer/Audit/AuditJournal.cs` at `5840ce8392c4cdeca471435e6d3b3c61a64d9537`, followed by exact caller, sink, and evidence-lifecycle adjudication.

## Evidence

`AuditJournal.Append` writes a serialized record before it calls `FlushToDisk`; a nonfatal append or flush exception poisons the journal. `FileAuditJournalSink` advances its live committed watermark only after the physical flush returns, so a flush that fails after the write can leave a complete logical record beyond that watermark.

`AuditCallContext` immediately calls `ReconcileAfterAmbiguousAuditAppend` for an unaccepted evidence-bearing call whose audit append was attempted. `AuditJournal.ScanRetainedEvidenceReferences` rejects disposed or unsupported sinks but not `_poisoned`, and `AuditEvidenceSpoolScanner` can therefore return a complete scan that excludes the record beyond the committed watermark. `ScriptEvidenceStore` interprets a complete scan with no reference as proof and renames the `AwaitingAnchor` artifact to `Unreferenced`.

`FileAuditJournalSink.CloseAndTrim` later flushes and preserves the stream's full logical length, including the record excluded from the earlier live scan. A restart can consequently retain and recover an audit record after its referenced script evidence was made retention-eligible.

## Impact

Retention can delete script evidence still referenced by a recovered audit record, breaking the audit-to-evidence integrity and retention guarantee precisely on an ambiguous durable-write boundary.

## Required guard

Use the real file sink with a failure after record write but before its committed watermark advances. Publish script evidence, trigger the ambiguous append, and prove reconciliation returns false and leaves the artifact pinned while the journal is poisoned; then close and reopen the spool and prove the record remains recoverable. Temporarily revert only the poisoned-scan repair, prove the guard releases the artifact incorrectly, restore it, and run focused evidence-retention, journal, file-sink, and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `HIGH`; confidence `high`.
- `guard_confirmed=false`; no repair implemented or tested.
- Post-flush durable ambiguity, automatic-transition poisoning, external-recovery guards, disposed committed-spool reads, and test-double fidelity candidates were rejected as intentional, guarded, unreachable, or test-only.
