# ssu-5: Activation replacement lacks a defined Windows-atomic primitive

**Severity**: MEDIUM — a concurrent launcher can observe activation failure or
an absent record if the unspecified replacement is not atomic under Windows file
sharing and retry behavior.

**Status**: Open

**Branch**: Not started

**Commit**: Not started

## Evidence

- `.agents/plans/mcp-side-by-side-upgrade.md:129-132` requires a flushed
  temporary file and "same-directory atomic rename" but names no Windows API,
  replacement flags, sharing contract, retry policy, or failure semantics.
- `server/PtkMcpServer/Audit/SecureAuditStorage.cs:212,286` already distinguishes
  Windows `MoveFileEx` and atomic replacement behavior, demonstrating that the
  repository treats this as a platform-specific primitive rather than a generic
  file move.

## Predicted observable failure

Hold or concurrently read `active.json` while activation replaces it on Windows.
An implementation using remove-then-move or a default move without replacement
semantics can expose a missing record or fail activation after the new runtime is
installed, preventing new MCP connections from starting.

## What

The plan asserts an atomicity property without specifying the primitive and
observable failure contract needed to implement and verify it on Windows.

## Approach

Pending owner-approved plan revision. Name the supported per-platform replacement
primitive, file-sharing expectations, bounded retry behavior, directory flush
limits, and the invariant that failure leaves the old complete record active.

## Files changed

- Review records only; no plan or product change.

## Guard proof

Pending a fix. The guard must race readers with repeated activation on Windows,
inject replacement failures, and prove readers see only the complete old or new
record while a failed activation preserves the old record.

## Coder dispute (if any)

None.

## Known gaps

The exact Windows API and acceptable durability boundary remain plan decisions.

## Reviewer comments

Reviewer: claude /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier
(owner-selected inline). Claude Code `2.1.220`, reviewed
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7..caf467e423105a621b1431302575b242f77791ac`,
verdict `findings`; admitted 2026-07-29.
