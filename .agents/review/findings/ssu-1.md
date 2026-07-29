# ssu-1: Stable launcher makes pwsh a hard server prerequisite

**Severity**: HIGH — a supported installation without `pwsh` on `PATH` cannot
start PTK through any managed registration.
**Status**: Open
**Branch**: Not started
**Commit**: Not started

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

Pending owner-approved plan revision. Prefer a stable self-contained native
launcher that resolves `active.json` and starts or replaces itself with the
selected runtime. If the PowerShell launcher remains, the plan must explicitly
accept the prerequisite and update the release contract in the same decision.

## Files changed

- Review records only; no plan or product change.

## Guard proof

Pending fix. The guard must start the registered command from packaged output
with `pwsh` unavailable and complete the five-tool handshake.

## Coder dispute (if any)

None.

## Known gaps

The native launcher mechanism and its containment semantics remain a separate
plan decision.

## Reviewer comments

Reviewer: claude /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier
(owner-selected inline). Claude Code `2.1.220`; openreview base
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7`, head
`caf467e423105a621b1431302575b242f77791ac`, verdict `findings`. Candidate
ADMITTED at intake on 2026-07-29.
