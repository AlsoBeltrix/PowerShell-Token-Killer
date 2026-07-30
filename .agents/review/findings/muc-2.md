# muc-2: Guardian replacement does not cover guardian upgrades

**Severity**: MEDIUM — the highest-cost option can still break the public MCP
connection when its stable component or tool contract changes.

**Status**: Closed 2026-07-30 — continuity architecture abandoned by owner;
no fix or implementation remains.

**Branch**: Not started.

**Commit**: Not started.

## Evidence

- `.agents/review/mcp-upgrade-continuity-options.md:84` presents the guardian as
  the transparent private-runtime cutover option without stating its outer
  upgrade boundary.
- `.agents/plans/mcp-resilience.md:158` states that the prior guardian design was
  crash recovery rather than hot upgrade.
- `.agents/plans/mcp-resilience.md:810` states that guardian, configuration, and
  binary upgrades require a new public MCP connection.

## Predicted observable failure

PTK gains private-runtime hot replacement, but a later guardian binary or public
tool-contract upgrade closes the original stdio transport and again requires a
client-session restart.

## What

The option does not distinguish private-runtime replacement from replacement of
the process that owns the client pipes and public protocol state.

## Approach

If this option is reopened, state that only compatible private-runtime changes
can be transparent. Treat guardian or public-contract replacement as the
unsolved outer boundary.

## Files changed

None. Intake record only.

## Guard proof

Manual evidence check against the superseded plan's explicit upgrade boundary.

## Coder dispute

None.

## Known gaps

No target MCP client has a proven way to replace the guardian connection in
place.

## Reviewer comments

Source openreview: Claude Code `2.1.220` /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / `max` /
frontier, owner-selected inline for this session. Reviewed
`d32f2a06e451df02cfa9b63d5c0dda570d1262ec..a308bdadbbda954340f48d63772e72787e9f6990`.
Intake verdict: **ADMITTED**.
