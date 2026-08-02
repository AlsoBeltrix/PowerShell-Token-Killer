# opr-46: Torn identity and process-group probe latches false escape

**Severity**: LOW — a rare PID-reuse race can report containment unconfirmed and fail a close or replacement even though exact background proof still releases the registry.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan defines a coherent identity/group observation compatible with the fail-closed opr-31 repair.

**Source**: Bounded and whole-file Claude Opus 5 review of `UnixWorkerContainmentRegistry` at `afbf64f`, confirmed against the native group-query contract and production launcher/supervisor call graph.

## Evidence

`server/PtkMcpServer/Worker/UnixWorkerContainmentRegistry.cs:267-280` calls `QueryIdentity(processId)` and then `GetProcessGroup(processId)` as separate native probes and records the pair after checking only that the first identity is valid. `server/PtkMcpServer/Worker/UnixWorkerProcessLauncher.cs:930-937` proves the group probe either returns a nonnegative group or throws; the defect is not an error sentinel, but a PID incarnation changing between two successful probes.

If an observed descendant exits and its PID is recycled between those calls, the registry can pair the old descendant identity with the replacement process's group. The comparisons at `UnixWorkerContainmentRegistry.cs:302-308` and `:321-326` then latch `EscapeObserved` and store a false escaped-descendant entry.

The consequence is bounded. `CanConfirmEmpty` at `:359-364` checks the stored escaped entry by exact identity, so the replacement PID does not keep the old identity live. `ConfirmEventuallyAsync` at `:210-237` can therefore complete `Empty` and clear the registration. However, `CompleteAsync` reads the sticky escape flag at `:152` and returns `descendants_unknown` at `:165-167`; if asynchronous empty proof has not completed by the supervisor's immediate check, the current close or replacement can surface a false containment-unconfirmed failure.

## Predicted observable failure

During containment observation, a tracked descendant exits and the host reuses its PID between the identity and process-group syscalls. No process actually escaped the worker group, but the current close, reset, or replacement path can report `descendants_unknown`. Background exact-identity proof still completes, so this finding does not claim a surviving orphan or permanent registry block.

## Required repair

Accept an observed identity/group pair only from one coherent PID incarnation. Use a native coherent observation or an identity sandwich that re-queries identity after the group probe and accepts the sample only when both valid identities match. A mismatched or indeterminate sample for an already tracked descendant must preserve fail-closed tracking under opr-31 rather than erase it.

Add a deterministic registry guard whose fake native returns the old identity, a foreign group, then a replacement identity for one PID; assert no false escape is latched and containment does not return `descendants_unknown` from that torn sample. Preserve a positive stable-identity group-escape guard and the opr-31 indeterminate-tracking behavior. Prove the new guard red against the current two-probe implementation and green after repair, run the repository verification entry point, and obtain fixed-SHA Claude Opus 5 review.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 515 lines of `UnixWorkerContainmentRegistry.cs` at `afbf64f` in two bounded exact-source passes plus one whole-file integration pass. Focused registry and launcher tests passed 12/12. Independent adjudication and integration accepted this distinct LOW diagnostic/availability finding, rejected permanent-hang and orphan claims, and excluded `opr-15`, `opr-26`, `opr-30`, and `opr-31`. No product or test file changed in this finding slice.
