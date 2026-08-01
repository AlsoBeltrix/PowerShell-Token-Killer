# opr-2: Unix PATH de-duplication collapses distinct case-sensitive directories

**Severity**: MEDIUM — cold command revalidation can report not found for an executable the child would resolve and run.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan covers platform-correct PATH de-duplication.

**Source**: Bounded Claude Opus 5 review of current production code.

## Evidence

`server/PtkMcpServer/Execution/ColdCommandResolution.cs:56` constructs `seenDirectories` with `StringComparer.OrdinalIgnoreCase` on every platform. On a case-sensitive Unix filesystem, PATH entries such as `/opt/Bin` and `/opt/bin` are distinct. If the first directory lacks `tool` and the second contains executable `tool`, the first entry is probed and the second is silently skipped as a duplicate.

`ColdCommandTargetIdentity.MatchesCurrentResolution` uses this resolver at cold-plan commit. It therefore rejects the target even though a cold PowerShell child would resolve the executable from the later PATH entry. Existing resolver tests cover platform tokenization and command ordering but not case-differing Unix PATH directories.

## Predicted observable failure

On Linux or a case-sensitive macOS volume, a cold RTK-eligible command whose resolving PATH entry differs only by case from an earlier non-resolving entry is treated as unavailable at commit. The command fails closed instead of following the child process's actual PATH semantics.

## Required repair

Use case-insensitive directory de-duplication only for Windows and ordinal de-duplication for Unix. Do not change the settled live-PATH re-resolution or executable-identity rules.

Add a Unix integration guard on a verified case-sensitive temporary filesystem: create distinct case-differing PATH directories, place an executable only in the later directory, and assert the resolver selects it. Prove the guard fails against the current comparer, restore the repair, then run the repo verification entry point and hosted cross-platform CI.

## Reviewer

Claude Code 2.1.220 using owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, frontier; reviewed `9de8e6c88229e7cd36ed207c6e45602234035679` read-only. Verdict: `finding`.
