# opr-3: Output-root disposal removes its marker before residual artifacts

**Severity**: MEDIUM — a failed or incomplete artifact unlink can leave retained output in a root that no later startup can authenticate and reclaim.

**Status**: Accepted; implementation authorized by `.agents/plans/production-reliability-salvage.md` (`ptk_output` teardown and stale-root reclamation invariant).

**Source**: Bounded Claude Opus 5 review of current production code.

## Evidence

`OutputStore.Dispose` treats retained-artifact deletion as best effort and then disposes its `OutputRootLease`. `server/PtkMcpServer/Execution/OutputRootLease.cs:149-157` closes the marker handle, deletes `owner.v1.json`, and only then attempts a non-recursive root deletion. If a recognized `artifact-<guid>.out` file remains, root deletion fails and is swallowed.

Startup reclamation requires a valid `owner.v1.json`. A markerless non-empty `server-<pid>-<guid>` root returns before reclamation and is preserved forever, so the deletion order destroys the only proof that could authorize later cleanup.

## Predicted observable failure

A graceful teardown with recognized residue leaves a markerless output root. Every later supervisor sees the artifact but refuses to delete it because the ownership marker is gone.

## Repair

After releasing the live marker and removing the in-process live-root registration, route disposal through the same fail-closed `TryReclaim` path used for stale siblings. It deletes only a validated marker plus recognized artifact names; any failure leaves the marker in place for a later retry.

Add a cross-platform guard that creates a valid owned root plus one recognized artifact, disposes the lease, and requires the root to be removed. Prove the guard fails against the current marker-first disposal, restore the repair, then run full verification and hosted CI.

## Reviewer

Claude Code 2.1.220 using owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, frontier; reviewed `b424dc990f169bbdb3dc894bebbf97824860479e` read-only. Verdict: `finding`.
