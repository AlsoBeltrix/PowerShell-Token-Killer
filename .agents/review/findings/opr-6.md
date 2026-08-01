# opr-6: Evidence-store faults re-enable the never-opened reconciliation shortcut

**Severity**: MEDIUM — pre-writer or periodic reconciliation can report success after a previously used evidence root disappears, allowing lost awaiting evidence to go undetected.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan covers evidence-store recovery-state and reconciliation semantics.

**Source**: Bounded Claude Opus 5 review of current production code.

## Evidence

`server/PtkMcpServer/Audit/ScriptEvidenceStoreProvider.cs:155-161`, `:180-188`, and `:203-212` treat `_store is null` plus an absent evidence-directory entry as proof that no artifact root ever existed, returning successful reconciliation without opening protected storage.

The proof is not monotonic. Storage failures in `Store`, `Publish`, `Probe`, `MarkAnchored`, reconciliation, and retention clear `_store` at lines 41, 51, 94, 103, 120, 137, 142, 169, 174, 191, 197, 220, and 225. A provider that already published awaiting evidence can therefore become indistinguishable from a never-opened provider after any recoverable storage fault.

`AuditEvidenceOrphanReconciler.RequireCompleteBeforeWriter` documents that protected evidence-storage failure closes admission, but it trusts `ReconcileExistingAwaitingBeforeWriter` returning true. Existing tests prove the legitimate never-opened/absent-root shortcut and ordinary reconciliation, not the used-store → fault-reset → absent-root transition.

## Predicted observable failure

The provider publishes an awaiting evidence artifact, then a later storage operation faults and clears `_store`. The evidence directory is removed or becomes indistinguishable from a missing entry before the next reconciliation. `ReconcileExistingAwaiting`, `ReconcileExistingAwaitingBeforeWriter`, or `RetainEligible` returns success from the shortcut. In the pre-writer path, audit administration opens a new journal writer even though previously durable records may reference missing awaiting evidence; periodic reconciliation likewise does not mark evidence storage unavailable.

## Required repair

Separate “this provider has never observed or opened evidence storage” from the retryable `_store` reference. Once storage has been opened, its disappearance must fail closed instead of re-entering the never-created shortcut; a transient fault may still discard the store object for explicit recovery. Add a deterministic guard using the existing evidence-store fault injector: publish once, induce a later storage fault that clears the cached store, remove the evidence root, then require pre-writer and periodic reconciliation to report storage unavailability rather than success. Prove the guard fails against current code, restore the repair, run the repository verification entry point, and obtain fixed-SHA Claude Opus 5 review before integration.

## Reviewer

Claude Code 2.1.220 using owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, frontier, read-only review of `server/PtkMcpServer/Audit/ScriptEvidenceStoreProvider.cs` at `a87baacd7e3dba7a5070187b829dac64bd05e2c0`. Verdict: `finding`.
