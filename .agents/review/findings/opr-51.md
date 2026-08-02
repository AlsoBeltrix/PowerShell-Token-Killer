# opr-51: Windows target matching treats path casing as identity

**Severity**: LOW — a casing-only PATH or PATHEXT change can spuriously reject a byte-identical cold RTK target on Windows.

**Status**: Accepted; unplanned. Product and test changes are blocked until an approved plan covers platform-aware executable identity comparison.

**Source**: Whole-file Claude Opus 5 integration review of `server/PtkMcpServer/Execution/ColdCommandResolution.cs` against `ExecutableFileIdentity`, followed by a built-assembly reflection probe.

## Evidence

`ColdCommandTargetIdentity.Matches` at `server/PtkMcpServer/Execution/ColdCommandResolution.cs:265-266` captures the current executable identity and compares it to the frozen identity with compiler-generated record equality. `ExecutableFileIdentity` contains `ExecutablePath`, `BinaryDigest`, and `UnixFileMode`; generated equality compares the path string case-sensitively.

That conflicts with the explicit Windows path policy in `server/PtkMcpServer/Execution/ExecutableFileIdentity.cs:11-13`, which selects `StringComparison.OrdinalIgnoreCase`, and with `MatchesCurrentFile` at `:99-103`, which uses that policy before comparing digest and Unix mode. `ColdCommandTargetIdentity.Matches` bypasses the platform-aware comparison.

A reflection probe against the current built `PtkMcpServer.dll` captured the same existing assembly through all-lowercase and all-uppercase absolute paths on Windows. Both captures succeeded, both paths named the same file, and `MatchesCurrentFile` returned true; record equality returned false. The identities retained their differently cased path strings, confirming the production comparison behavior.

`MatchesCurrentResolution` re-resolves live PATH and PATHEXT. A casing-only change can therefore produce the same Windows file with the same digest but a differently cased `ExecutablePath`, causing a false `RtkTargetResolutionChanged` no-start. This is availability-only and does not weaken the integrity gate.

## Required repair

Compare `ExecutableFileIdentity` values with platform-aware path semantics plus ordinal digest and exact Unix-mode equality. Keep the comparison narrow to identity matching or expose a dedicated semantic comparison helper; do not weaken digest, symlink-target, mode, or settled live-PATH checks.

Add a Windows guard that captures one target, re-resolves the same file through differently cased PATH and command-extension text, and asserts `MatchesCurrentResolution` remains true. Include a control proving a content or target change remains false. Temporarily revert only the repair, prove the casing guard fails, restore it, then run the repository verification entry point and fixed-model Claude Opus 5 review.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 268 lines in two bounded passes and a whole-file integration pass. `ColdCommandResolutionTests` passed 13/13 and `ExecutionPlannerTests` passed 82/82 before review. Existing `opr-2` and refuted-as-defect `rbc-13` were excluded. No product or test file changed in this finding slice.
