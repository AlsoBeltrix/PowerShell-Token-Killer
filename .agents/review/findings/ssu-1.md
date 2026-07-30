# ssu-1: Stable launcher makes pwsh a hard server prerequisite

**Severity**: HIGH — a supported installation without `pwsh` on `PATH` cannot
start PTK through any managed registration.
**Status**: Closed 2026-07-30 — plan abandoned; no implementation remains
**Branch**: `master`
**Commit**: Plan decision recorded in `.agents/decisions.md`; product not started

## Evidence

- `.agents/plans/mcp-side-by-side-upgrade.md:47-50` mandates a managed
  registration beginning with `pwsh`.
- `README.md:268-269` states that the matched self-contained payload does not
  require installed PowerShell.
- `.agents/plans/release-distribution.md:258-260` repeats that only the optional
  hook requires `pwsh`; the matched payload never does.
- `scripts/dev-install.ps1:191-192` publishes the server self-contained.

## Predicted observable failure

Remove `pwsh` from `PATH` on a supported packaged install and start PTK through
any managed harness registration. The client fails before the MCP server starts,
where the current direct self-contained apphost registration succeeds.

## What

The proposed stable PowerShell launcher reverses a documented distribution
contract without naming or accepting that new prerequisite. It also adds a
PowerShell startup to every MCP connection.

## Approach

Owner approved the native boundary on 2026-07-29. The revised plan uses a
packaged per-RID native launcher below `~/.ptk/launcher/`. It requires no
separately installed PowerShell or .NET runtime, replaces itself with the
selected runtime on Unix, and uses pre-resume kill-on-close Job Object
containment on Windows.

## Files changed

- `.agents/decisions.md` — durable owner-approved native-launcher boundary.
- `.agents/plans/mcp-side-by-side-upgrade.md` — native command, layout, Slice 0
  implementation inventory, and mandatory no-`pwsh` guard.
- Review/state records — finding progression only.
- No product file changed.

## Guard proof

Pending fix. The guard must start the registered command from packaged output
with `pwsh` unavailable and complete the five-tool handshake.

## Coder dispute (if any)

None.

## Known gaps

The native implementation and its Windows/Unix feasibility proof remain Slice 0
work. Any orphan, protocol mutation, or unbounded teardown still stops the plan.

## Reviewer comments

Reviewer: claude /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier
(owner-selected inline). Claude Code `2.1.220`; openreview base
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7`, head
`caf467e423105a621b1431302575b242f77791ac`, verdict `findings`. Candidate
ADMITTED at intake on 2026-07-29.
