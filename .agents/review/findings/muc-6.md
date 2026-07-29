# muc-6: Upgrade-continuity review input is not discoverable from current state

**Severity**: LOW — a cold session can miss the open architecture question and
re-derive it without the review evidence.

**Status**: In progress — current-state pointer added; independent finding-fix
review has not been dispatched. No architecture implementation is authorized.

**Branch**: `master` (docs-only state maintenance).

**Commit**: Not started.

## Evidence

- `.agents/review/mcp-upgrade-continuity-options.md:3` marks the file as review
  input with unresolved questions.
- Before this intake, `.agents/review/index.md` did not register the review and
  `.agents/state.md` did not point to it.
- `AGENTS.md:25` requires `.agents/state.md` to be the immediately discoverable
  current-state entry point.

## Predicted observable failure

A cold session reads current state, never finds the continuity review, and
repeats the option analysis or starts from stale assumptions.

## What

The forward-looking review input was placed among review artifacts without a
ledger or state pointer.

## Approach

Register the review outcome in the review index and add a concise state pointer
that names the pending owner ruling. If the work becomes an approved plan, move
the canonical design into `.agents/plans/`.

## Files changed

The review index registers the intake and `.agents/state.md` now exposes the
review to a cold session.

## Guard proof

Manual cold-entry check from `.agents/state.md` and `.agents/review/index.md`.

## Coder dispute

None.

## Known gaps

The options document remains review input and is not an approved plan.

## Reviewer comments

Source openreview: Claude Code `2.1.220` /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / `max` /
frontier, owner-selected inline for this session. Reviewed
`d32f2a06e451df02cfa9b63d5c0dda570d1262ec..a308bdadbbda954340f48d63772e72787e9f6990`.
Intake verdict: **ADMITTED**.
