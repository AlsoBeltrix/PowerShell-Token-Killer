# hcc-4: Oversized consent numbers crash instead of re-prompting

**Severity**: LOW — malformed input kills the run (and rolls back a full install) instead of re-asking.
**Status**: In progress
**Branch**: —
**Commit**: (pending)

## Evidence
`scripts/ptk_init.ps1:302-307` — the skip-selection parser casts digit tokens
and range endpoints directly to `[int]`. `2147483648` matches `\d+` but
overflows Int32; with `$ErrorActionPreference = 'Stop'` the script
terminates, bypassing the invalid-selection re-ask.

## Predicted observable failure
Pasting an oversized number at the consent prompt terminates ptk_init — and
a full installer run with it — instead of printing "Unrecognized selection"
and asking again.

## What
Unchecked numeric conversion in interactive input parsing.

## Approach
(pending)

## Files changed
(pending)

## Guard proof
(pending)

## Coder dispute (if any)
—

## Known gaps
—

## Reviewer comments
(intake) Reviewer: codex / gpt-5.6-sol / xhigh (inline, session-only) / standard — generation pass over 19201a1..092df3b, codex-cli 0.146.0, verdict findings (5), capability_ok true, 2026-08-04.
