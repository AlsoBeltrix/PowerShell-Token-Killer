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
All numeric conversions in the skip-selection parser now go through
`[int]::TryParse` with bounds checked after parse; an oversized token is
simply invalid input and re-asks.

## Files changed
- `scripts/ptk_init.ps1` — `Read-PtkConsentSkips` token loop
- `tests/PwshTokenCompressor.Tests.ps1` — hcc-4 guard test

## Guard proof
- `tests/PwshTokenCompressor.Tests.ps1::'an oversized selection number re-asks instead of crashing (hcc-4)'` — with the fix stashed the run dies on the cast (nonzero exit) and the test FAILS; restored it PASSES (verified 2026-08-04).

## Coder dispute (if any)
—

## Known gaps
—

## Reviewer comments
(intake) Reviewer: codex / gpt-5.6-sol / xhigh (inline, session-only) / standard — generation pass over 19201a1..092df3b, codex-cli 0.146.0, verdict findings (5), capability_ok true, 2026-08-04.
