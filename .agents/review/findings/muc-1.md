# muc-1: Guardian option silently reopens a discarded architecture

**Severity**: MEDIUM — an implementer could revive an expressly superseded
architecture without the owner first reopening that decision.

**Status**: Open — review input only; no fix or implementation is authorized.

**Branch**: Not started.

**Commit**: Not started.

## Evidence

- `.agents/review/mcp-upgrade-continuity-options.md:84` presents the stable
  guardian/private-runtime topology as an ordinary candidate.
- `.agents/plans/mcp-resilience.md:3` marks the prior guardian/private-host plan
  superseded, and line 5 says not to restore that architecture.
- `.agents/state.md:133` records the owner direction to pause that delivery line,
  and line 136 forbids implementing or cutting it over.

## Predicted observable failure

A cold implementer treats the guardian as unexplored work, repeats the discarded
design cycle, or starts the forbidden topology without an explicit owner ruling
that reopens it.

## What

The options brief omits the existing guardian design, its review history, and
the durable owner decision that superseded it.

## Approach

If the owner asks to continue the options document, cite the superseded plan
instead of restating it and label the guardian option as requiring an explicit
decision reversal plus a materially new justification.

## Files changed

None. Intake record only.

## Guard proof

Manual evidence check against the cited tracked files. No shipped behavior is
changed by this finding record.

## Coder dispute

None.

## Known gaps

The owner has not ruled whether the prior guardian decision may be reopened.

## Reviewer comments

Source openreview: Claude Code `2.1.220` /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / `max` /
frontier, owner-selected inline for this session. Reviewed
`d32f2a06e451df02cfa9b63d5c0dda570d1262ec..a308bdadbbda954340f48d63772e72787e9f6990`.
Intake verdict: **ADMITTED**.
