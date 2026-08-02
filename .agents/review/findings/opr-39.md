# opr-39: Output-root reclaimer can remove ownership proof before root removal completes

**Severity**: LOW — a narrow live-owner publish-and-die or Windows delete-pending window can leave a markerless output root that later supervisors permanently refuse to reclaim.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan defines a marker-preserving final-removal protocol and cross-platform guard.

**Source**: Bounded and whole-file Claude Opus 5 review of current production code.

## Evidence

`server/PtkMcpServer/Execution/OutputRootLease.cs:200-224` snapshots the recognized artifact paths before it opens and exclusively locks the ownership marker at `:233-241`. After validating the retained marker, `:258-262` deletes only those snapshotted artifacts, `:263-266` unlocks and removes `owner.v1.json`, and only then `:267` attempts non-recursive root deletion. The catch at `:269` suppresses any failure.

A legitimate live owner can publish another recognized artifact after the snapshot and then die before the reclaimer acquires the marker. The reclaimer gains ownership, deletes only the older snapshot, removes the marker, and fails to delete the now-nonempty directory. A Windows delete-pending artifact can likewise make final directory removal fail after its directory entry was selected for deletion. Future `TryReclaim` calls stop at `:231` because the marker is absent, so no retry can authenticate the root.

This is distinct from verified `opr-3`: that repair routed disposal through `TryReclaim` so known residue is processed before the marker. This finding is inside `TryReclaim` itself; its pre-lock artifact snapshot and marker-before-directory tail can still destroy the only durable ownership proof before final removal succeeds.

## Predicted observable failure

One supervisor-startup or disposal race leaves a `server-<pid>-<creation-id>` output root containing a recognized artifact without `owner.v1.json`. On Windows, a delete-pending entry can make the one directory-deletion attempt fail; after the external handle closes, its entry disappears but the now-empty markerless root is never retried. Later supervisors preserve either root indefinitely. Retained output bytes can therefore survive every automatic cleanup pass in the artifact-snapshot case.

## Required repair

Define a cross-platform final-removal protocol that revalidates the root after marker ownership is acquired and preserves or durably restores authenticated retry state whenever the directory cannot be removed. Add a deterministic guard for an artifact appearing between the initial snapshot and marker acquisition, plus the applicable Windows delete-pending case. Prove the guard fails at current head and passes after the repair, then run the repository verification entry point and fixed-SHA Opus review.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 433 lines at `810ed95` in two bounded source/caller/test passes plus one whole-file active-caller integration pass. Focused lease and output-store tests passed 27/27. The initial MEDIUM proposal was independently accepted at LOW because the live-owner publish-and-die interval is narrow; a separate closed-descriptor unlock candidate was rejected as a compound hypothetical. No product or test file changed.
