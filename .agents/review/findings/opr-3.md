# opr-3: Output-root disposal removes its marker before residual artifacts

**Severity**: MEDIUM — a failed or incomplete artifact unlink can leave retained output in a root that no later startup can authenticate and reclaim.

**Status**: Exact-SHA Opus review accepted with `guard_confirmed=true`; awaiting hosted CI and integration through PR #27.

**Source**: Bounded Claude Opus 5 review of current production code.

## Evidence

`OutputStore.Dispose` treats retained-artifact deletion as best effort and then disposes its `OutputRootLease`. `server/PtkMcpServer/Execution/OutputRootLease.cs:149-157` closes the marker handle, deletes `owner.v1.json`, and only then attempts a non-recursive root deletion. If a recognized `artifact-<guid>.out` file remains, root deletion fails and is swallowed.

Startup reclamation requires a valid `owner.v1.json`. A markerless non-empty `server-<pid>-<guid>` root returns before reclamation and is preserved forever, so the deletion order destroys the only proof that could authorize later cleanup.

## Predicted observable failure

A graceful teardown with recognized residue leaves a markerless output root. Every later supervisor sees the artifact but refuses to delete it because the ownership marker is gone.

## Repair

After releasing the live marker and removing the in-process live-root registration, route disposal through the same fail-closed `TryReclaim` path used for stale siblings. It deletes only a validated marker plus recognized artifact names; any failure leaves the marker in place for a later retry.

Add a cross-platform guard that creates a valid owned root plus one recognized artifact, disposes the lease, and requires the root to be removed. Prove the guard fails against the current marker-first disposal, restore the repair, then run full verification and hosted CI.

## Implementation and guard proof

- `OutputRootLease.Dispose` now releases the live lock and registry entry, then reuses the fail-closed stale-root reclaimer. Recognized residue is deleted only after marker and artifact identity validation; any failure preserves the marker for a later retry.
- `OutputRootLeaseTests.Dispose_reclaims_recognized_residue_before_removing_ownership_marker` creates one valid retained artifact and requires lease disposal to remove the root.
- Guard red: unchanged production left the root present (`Assert.False`, actual `true`), 0/1 passed.
- Guard green: repaired production passed 1/1.
- Full verification: server 1,221/1,221; Pester 145 passed and 1 platform skip; registered five-tool handshake passed; server dependency audit found no vulnerable packages. SIEM passed 226/247; the 21 failures are the recorded ordinary-Windows-token inability to create symlink fixtures before product assertions.

## Reviewer

Claude Code 2.1.220 using owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, frontier; reviewed `b424dc990f169bbdb3dc894bebbf97824860479e` read-only. Verdict: `finding`.

Fixed-SHA repair review: head `e72f2b65665cb087a8c7c5b01576e9f943ba4158` against base `5d2eb8ccd13863c12ff246a7ce34757c10cc335c`; verdict `accepted`, `guard_confirmed=true`. The reviewer confirmed marker preservation on any validation/unlink failure, safe self-reclamation after live-registry removal, unchanged sibling lock protection, and a non-vacuous red/green guard.
