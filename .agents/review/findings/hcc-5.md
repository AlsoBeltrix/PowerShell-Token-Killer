# hcc-5: Kimi skip blurb ignores KIMI_CODE_HOME

**Severity**: LOW — manual-setup instructions point at the wrong file for relocated kimi homes.
**Status**: In progress
**Branch**: —
**Commit**: (pending)

## Evidence
`scripts/ptk_init.ps1:276` — the kimi manual blurb hardcodes
`~/.kimi-code/mcp.json` while the leg itself resolves the home via
`Get-PtkKimiHome` (`$KIMI_CODE_HOME`-aware, line 194). The consent test pins
the hardcoded string while running with a custom KIMI_CODE_HOME.

## Predicted observable failure
A user with a relocated kimi home who skips the kimi leg and follows the
printed instructions edits the wrong mcp.json; kimi never sees the
registration.

## What
Blurb text not derived from the same home resolution as the leg.

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
