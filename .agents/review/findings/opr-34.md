# opr-34: Crash-left allocation temp wedges pre-writer reconciliation

**Severity**: LOW — one recoverable audit-segment allocation temporary can indefinitely block out-of-band audit administration and pin awaiting script evidence until an operator removes the file.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan orders canonical crash-temporary recovery before the pre-writer evidence scan without weakening unknown-entry refusal or evidence-to-spool lock ordering.

**Source**: Complete bounded no-tool Claude Opus 5 review of `server/PtkMcpServer/Audit/AuditEvidenceSpoolScanner.cs` at `0499aa7fc6fec65f76a3979cbef06515b9bd83e9`, followed by focused startup-order and final candidate adjudication.

## Evidence

`FileAuditJournalSink.CreateSegment` creates a canonical `.<segment>.<uuid>.allocating` file before publishing it atomically. Its ordinary exception path deletes the temporary, but a hard kill or power loss between creation and publish leaves it behind. The writer recovery routine explicitly recognizes and removes that protected canonical shape, and an existing direct writer-preparation test proves the recovery.

Production startup orders the owners incorrectly for an awaiting artifact. `AuditEvidenceOrphanReconciler.RequireCompleteBeforeWriter` creates and releases only the spool quota control, then invokes pre-writer evidence reconciliation before `AuditAdminOperations` calls `FileAuditJournalSink.PrepareAnchored`. `AuditEvidenceSpoolScanner.Inventory` accepts only the quota control and canonical segment names, so it rejects the allocation temporary as an unknown entry. `CaptureBeforeWriter` converts that failure to an incomplete scan, reconciliation returns false, and audit administration refuses before the later writer recovery can run. Every restart repeats the same order.

This is distinct from `opr-6`. That finding can falsely succeed after evidence-store state loss. This finding safely refuses deletion but never reaches the existing recovery owner, causing a stable availability and over-retention wedge. The impact is limited to the current out-of-band audit administration path; MCP execution admission is unaffected.

## Predicted observable failure

An audit append leaves its script evidence in `AwaitingAnchor` and the process hard-crashes while rotating through segment allocation. Every later audit-administration open returns unavailable because the retained allocation temporary makes the absence proof incomplete. Awaiting evidence remains pinned and the canonical temporary is never automatically recovered.

## Required guard

Add an anchored startup test that seeds one protected awaiting evidence artifact and one canonical crash-left allocation temporary, then opens the real audit-administration journal path. Assert startup recovers only the canonical temporary, completes pre-writer reconciliation, preserves the evidence bytes, and opens the writer. Add malformed and noncanonical unknown-entry controls that remain fail-closed. Temporarily revert only the recovery-order repair, prove the full-path test fails before writer preparation, restore it, then run focused evidence/orphan reconciliation tests and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `LOW`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- Fully retired-chain release, V1 request compatibility, leaf-type validation, record-boundary, handle-identity, and disposal candidates were rejected.
