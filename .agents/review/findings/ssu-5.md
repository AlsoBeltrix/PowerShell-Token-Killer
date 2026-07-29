# ssu-5: Activation replacement lacks a defined Windows-atomic primitive

**Severity**: MEDIUM — a concurrent launcher can observe activation failure or
an absent record if the unspecified replacement is not atomic under Windows file
sharing and retry behavior.

**Status**: Plan decision resolved 2026-07-29; implementation and guard not
started

**Branch**: `master`

**Commit**: Plan decision recorded in `.agents/decisions.md`; product not started

## Evidence

- At reviewed head `caf467e423105a621b1431302575b242f77791ac`,
  `.agents/plans/mcp-side-by-side-upgrade.md:129-132` required a flushed
  temporary file and "same-directory atomic rename" but named no Windows API,
  replacement flags, sharing contract, retry policy, or failure semantics.
- `server/PtkMcpServer/Audit/SecureAuditStorage.cs:286,1310` already uses
  `SetFileInformationByHandle(FileRenameInfoEx)` for protected Windows
  replacement, demonstrating that the repository treats this as a
  platform-specific primitive rather than a generic file move.

## Predicted observable failure

Hold or concurrently read `active.json` while activation replaces it on Windows.
An implementation using remove-then-move or a default move without replacement
semantics can expose a missing record or fail activation after the new runtime is
installed, preventing new MCP connections from starting.

## What

The plan asserts an atomicity property without specifying the primitive and
observable failure contract needed to implement and verify it on Windows.

## Approach

Owner approved the named OS replacement contract on 2026-07-29. Windows mirrors
the existing handle-based `FileRenameInfoEx` implementation with replace and
POSIX-semantics flags, delete-sharing readers, and no retry or delete-first
fallback. Unix uses same-directory `rename(2)` followed by parent-directory
flush. Kernel replacement success is the commit point.

## Files changed

- `.agents/decisions.md` — durable OS primitive and failure contract.
- `.agents/plans/mcp-side-by-side-upgrade.md` — canonical helper, reader sharing,
  commit point, failure behavior, and concurrency/fault guards.
- Review/state records — finding progression only.
- No product file changed.

## Guard proof

Pending a fix. The guard must race readers with repeated activation on Windows,
inject replacement failures, and prove readers see only the complete old or new
record while a failed activation preserves the old record.

## Coder dispute (if any)

None.

## Known gaps

Implementation must still prove Windows running-image contention and cross-host
Unix behavior. Arbitrary power-loss durability remains explicitly unclaimed.

## Reviewer comments

Reviewer: claude /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier
(owner-selected inline). Claude Code `2.1.220`, reviewed
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7..caf467e423105a621b1431302575b242f77791ac`,
verdict `findings`; admitted 2026-07-29.
