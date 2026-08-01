# opr-29: Case-insensitive environment validation rejects valid Unix hosts

**Severity**: MEDIUM — a valid Linux or macOS environment containing names that differ only by case deterministically prevents every worker launch before normal failure classification.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan makes worker-environment identity platform-correct while preserving reserved bootstrap-variable custody.

**Source**: Focused no-tool Claude Opus 5 adjudication of `server/PtkMcpServer/Worker/WorkerLaunchCommand.cs` and `SessionWorkerLaunchCommand.CaptureEnvironment` at `488e65354e1562c6b80f0adbdee8a07c53af08df`, discovered through the `UnixWorkerProcessLauncher` caller review and confirmed by final merge adjudication.

## Evidence

`SessionWorkerLaunchCommand.CaptureEnvironment` enumerates the supervisor's complete process environment except the two reserved bootstrap handle names. On Unix, environment-variable names are case-sensitive, so names such as `PATH` and `Path` are legal and distinct. `WorkerLaunchCommand` freezes every environment through a dictionary using `StringComparer.OrdinalIgnoreCase`; its `TryAdd` rejects the second case-distinct name as a duplicate. Both worker command-construction branches use this path. The exception occurs before `UnixWorkerProcessLauncher.LaunchAsync` and before `ProcessSessionWorkerFactory` can classify a launch failure.

## Predicted observable failure

A Linux or macOS service, container, CI runner, or shell wrapper supplies two valid variables differing only by case. PTK cannot create any worker command and every session start fails with a raw `ArgumentException` claiming caller-supplied duplicate environment names. Retrying is deterministic until the host environment changes.

## Required guard

Add platform-specific command/environment tests. On Unix, construct a worker environment containing two case-distinct names and assert both survive with exact spelling and values through the spawn boundary. On Windows, retain case-insensitive duplicate rejection. Add reserved bootstrap-name controls that prove the exact platform-appropriate custody rule. Temporarily revert only the repair, prove the Unix preservation assertion fails, restore it, then run focused command/launcher tests and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `MEDIUM`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- This finding is independent of launcher runtime behavior: construction fails before any launcher is called.
