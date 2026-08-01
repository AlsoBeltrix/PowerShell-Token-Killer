# opr-13: Worker environment identity is always case-insensitive

**Severity**: MEDIUM — a valid Unix process environment can prevent every contained worker from launching.
**Status**: Accepted; unplanned. Product change blocked until an approved plan covers platform-correct worker environment identity.
**Source**: Bounded Claude Opus 5 review of `WorkerLaunchCommand`, validated against capture and constructor code.

## Evidence

`server/PtkMcpServer/Worker/WorkerLaunchCommand.cs` constructs its frozen environment with `StringComparer.OrdinalIgnoreCase` on every platform and rejects a second key when `TryAdd` reports a duplicate.

`SessionWorkerLaunchCommand.CaptureEnvironment` enumerates the supervisor's complete ambient environment and passes it to this constructor. Windows environment names are case-insensitive, but POSIX names are case-sensitive: `PATH`, `Path`, and `path` are distinct variables and may coexist.

The reserved bootstrap-variable set also uses unconditional case-insensitive identity. On Unix, that is over-restrictive because a differently cased name is not the reserved variable read by the worker.

## Predicted observable failure

A Linux or macOS supervisor launched with any pair of environment variables that differs only by case reaches lazy worker creation, and `WorkerLaunchCommand` throws `ArgumentException` for duplicate names. No contained session can start until the ambient environment is changed.

## Required repair

Use `StringComparer.OrdinalIgnoreCase` on Windows and `StringComparer.Ordinal` on Unix for both frozen environment identity and reserved-name filtering. Add a platform-conditioned constructor test with two case-differing variables and a real Unix worker subprocess probe proving both values arrive distinctly. Prove the constructor test red against current behavior before retaining the repair.

## Review disposition

Reviewer: owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, no-tool transport; exact head `e9ef575d8626d3ca93027d11900181a597713177`. Verdict: `finding`. No product-change guard claim.

- Rejected candidate: arguments are defensively copied with `ToArray` before `Array.AsReadOnly`, so the caller cannot mutate the validated backing array.
- Rejected candidate: executable and working-directory validation already uses `Path.IsPathFullyQualified`, not `IsPathRooted`.
- Distinct from `opr-2`, which concerns case-sensitive directory entries within one Unix PATH value.
