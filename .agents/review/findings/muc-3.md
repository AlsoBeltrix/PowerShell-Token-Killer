# muc-3: Daemon option conflicts with the settled transport topology

**Severity**: MEDIUM — selecting it without a decision reversal would authorize
work that contradicts the current product and active-plan boundary.

**Status**: Closed 2026-07-30 — continuity architecture abandoned by owner;
no fix or implementation remains.

**Branch**: Not started.

**Commit**: Not started.

## Evidence

- `.agents/review/mcp-upgrade-continuity-options.md:96` presents a per-user daemon
  as an ordinary candidate.
- `.agents/decisions.md:244` settles stdio MCP instead of a spawned daemon.
- `README.md:74` states that the design has no daemon or reattachment.
- `.agents/plans/production-reliability-salvage.md:199` excludes a shared daemon,
  and line 261 excludes daemon and cross-connection attachment.

## Predicted observable failure

An options ruling appears to approve the daemon while the durable product
decision and active plan still forbid it, leaving implementation blocked or
silently violating the higher-authority sources.

## What

The options brief does not flag that the daemon alternative requires reversing
settled topology decisions before any implementation plan can be approved.

## Approach

If the owner wants to reconsider the daemon, first rule explicitly on the
existing decision and active-plan boundary. Otherwise remove it from the viable
set and retain it only as a rejected alternative.

## Files changed

None. Intake record only.

## Guard proof

Manual evidence check against the cited decision, product documentation, and
active plan.

## Coder dispute

None.

## Known gaps

The decisions log is under hold, so its normal amendment path is not presently
available.

## Reviewer comments

Source openreview: Claude Code `2.1.220` /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / `max` /
frontier, owner-selected inline for this session. Reviewed
`d32f2a06e451df02cfa9b63d5c0dda570d1262ec..a308bdadbbda954340f48d63772e72787e9f6990`.
Intake verdict: **ADMITTED**.
