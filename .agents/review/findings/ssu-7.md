# ssu-7: Prune repeats the broad no-running-server disruption

**Severity**: MEDIUM — reclaiming any inactive runtime requires stopping every
PTK server below the install root, recreating the session disruption the upgrade
plan is intended to remove.

**Status**: Open

**Branch**: Not started

**Commit**: Not started

## Evidence

- `.agents/plans/mcp-side-by-side-upgrade.md:238-245` retains all prior
  runtimes during ordinary installs.
- `.agents/plans/mcp-side-by-side-upgrade.md:247-254` makes
  `-PruneInactive` fail closed if any `PtkMcpServer` process has an executable
  path below `~/.ptk`, even when that process uses a directory not targeted for
  deletion.
- `scripts/dev-install.ps1:128-138` contains the same broad
  `Assert-PtkServerNotRunning` predicate that contributes to the current
  disruptive upgrade behavior.
- `.agents/plans/release-distribution.md:313` records approximately 129 MB per
  representative runtime.

## Predicted observable failure

Keep the active runtime connected and attempt to prune only an older inactive
runtime that no process uses. The command refuses until every PTK-backed client
session is restarted, so retained runtimes accumulate indefinitely on the normal
always-connected workflow.

## What

Deletion safety is checked against the whole PTK home rather than the exact
version directories selected for removal.

## Approach

Pending owner-approved plan revision. Resolve live PTK executable paths to
canonical version directories and block only deletion of a directory that a live
process occupies. Define a bounded retention policy separately from the safety
predicate.

## Files changed

- Review records only; no plan or product change.

## Guard proof

Pending a fix. With runtime A active and inactive runtimes B and C present, the
guard must prune B/C without affecting A, refuse an attempt to delete A, and
leave all non-target directories unchanged.

## Coder dispute (if any)

None.

## Known gaps

Default retention count and treatment of uninspectable processes remain plan
decisions.

## Reviewer comments

Reviewer: claude /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier
(owner-selected inline). Claude Code `2.1.220`, reviewed
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7..caf467e423105a621b1431302575b242f77791ac`,
verdict `findings`; admitted 2026-07-29.
