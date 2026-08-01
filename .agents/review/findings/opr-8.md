# opr-8: Windows child-stdin guard can install a non-inheritable handle

**Severity**: MEDIUM — a partial native setup failure can make every later native child fail stdin access instead of receiving clean EOF.
**Status**: Accepted; unplanned. Product change blocked until an approved plan covers Windows child-stdin setup failure handling.
**Source**: Bounded Claude Opus 5 review of current production code, validated against repo tests and existing findings.

## Evidence

`server/PtkMcpServer/ChildStdinGuard.cs` opens `NUL`, calls `SetHandleInformation(..., HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)`, then calls `SetStdHandle(STD_INPUT_HANDLE, handle)`. Both native calls return `bool`, but neither result is checked.

If `SetHandleInformation` returns false, execution still publishes that non-inheritable handle as standard input. A subsequently spawned child receives a handle value absent from its handle table and stdin access fails with `ERROR_INVALID_HANDLE`. `StdioChildStdinTests.Stdin_reading_native_reads_clean_EOF_under_idle_stdio` establishes that successful child stdin access, not merely prompt return, is the contract.

The outer best-effort catch does not cover this path because Win32 `BOOL` failure is a return value rather than a managed exception.

## Predicted observable failure

On a Windows host where inheritance marking fails, native tools launched by a warm session fail when they inspect or duplicate stdin, commonly reporting that the handle is invalid. The guard has partially replaced the original stdin handle, so this is worse than a clean best-effort no-op.

## Required repair

Check `SetHandleInformation` before publishing the handle. On failure, dispose the candidate `NUL` handle and preserve the original process standard-input handle. Check `SetStdHandle` as well and clean up the unpublished candidate on failure. Preserve the repo's explicit best-effort startup boundary unless an approved plan changes it. Add injectable native-call seams or a bounded Windows subprocess fixture that proves a failed inheritance-mark step never publishes the candidate handle.

## Review disposition

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`max`, no-tool session), exact head `722c4ca66943906faac1e50877ee7645e1e0f093`. Verdict: `finding`.

- Rejected candidate: overwriting POSIX descriptor 0 does not destroy the already-captured MCP stream in the supported runtime; real-process server tests and stdio handshakes pass on Ubuntu and macOS while exchanging requests after detachment.
- De-duplicated candidate: unchecked POSIX `dup2` is already explicitly included in `opr-1`'s required repair.
- No product-change guard claim; no commands or file changes were available to the review transport.
