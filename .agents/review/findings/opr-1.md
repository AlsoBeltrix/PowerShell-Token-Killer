# opr-1: POSIX child-stdin detachment leaks the source `/dev/null` descriptor

**Severity**: LOW — one descriptor remains open for each guard call and is inherited by child processes.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan covers POSIX descriptor ownership.

**Source**: Bounded Claude Opus 5 review of current production code.

## Evidence

`server/PtkMcpServer/ChildStdinGuard.cs:58-61` opens `/dev/null`, duplicates it onto descriptor 0, and never closes the source descriptor. The production call site at `server/PtkMcpServer/Program.cs:41` runs once per supervisor, so current impact is one process-lifetime descriptor rather than an unbounded request-path leak. The descriptor is opened without close-on-exec and therefore remains available to subsequently launched children.

The existing stdin integration guard proves children receive clean EOF. It does not assert custody of the source descriptor or idempotence when detachment is invoked more than once.

## Predicted observable failure

On POSIX, a supervisor retains one extra open `/dev/null` descriptor after startup. Any additional `DetachChildStdin` call retains another descriptor. Children inherit those otherwise unnecessary descriptors.

## Required repair

Close the source descriptor after successful or failed `dup2`, while preserving descriptor 0 when `open` itself returns 0. Check native return values without weakening the best-effort startup boundary.

Add a disposable subprocess guard that invokes detachment more than once and counts valid descriptors with a portable native probe. Prove the guard fails against the current implementation, restore the repair, then run the repo verification entry point and hosted cross-platform CI.

## Reviewer

Claude Code 2.1.220 using owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, frontier; reviewed `fe4bb1a3fb2b35c080a43e4bbd9513ed4a0a9f02` read-only. Verdict: `finding`.
