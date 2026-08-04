# hcc-5: Kimi skip blurb ignores KIMI_CODE_HOME

**Severity**: LOW — manual-setup instructions point at the wrong file for relocated kimi homes.
**Status**: Verified
**Branch**: —
**Commit**: `a3d0d17`

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
The kimi blurb now builds its mcp.json path from `Get-PtkKimiHome`, the same
resolution the leg uses; the consent test asserts the configured (temp)
home rather than the hardcoded path.

## Files changed
- `scripts/ptk_init.ps1` — `Get-PtkManualBlurb` kimi case
- `tests/PwshTokenCompressor.Tests.ps1` — assertion now derives the home

## Guard proof
- `tests/PwshTokenCompressor.Tests.ps1::'skips the numbered and ranged selections and prints their blurbs'` — the home-aware assertion FAILS with the fix stashed (blurb names `~/.kimi-code`), PASSES restored (verified 2026-08-04).

## Coder dispute (if any)
—

## Known gaps
—

## Reviewer comments
(intake) Reviewer: codex / gpt-5.6-sol / xhigh (inline, session-only) / standard — generation pass over 19201a1..092df3b, codex-cli 0.146.0, verdict findings (5), capability_ok true, 2026-08-04.

(verification) Reviewer: codex / gpt-5.6-sol / high / standard — codex-cli 0.146.0 (model from the -m pin; the JSONL stream emits no model id). reviewed_sha a3d0d1776e26b40d10d5f1caa376e3ba42c272dd, base_sha 9fbd00f36e8d35fc319dca1612d49f15439bfc48, guard_confirmed true, capability_ok true, verdict **accepted**, 2026-08-04T23:38Z. Comments: "Guard proof behaved exactly as required: PASS at the reviewed fix, FAIL with the base script naming ~/.kimi-code/mcp.json, then PASS after restoration." / "The focused consent set passed 9/9. The fix derives the blurb path from Get-PtkKimiHome, matching the KIMI_CODE_HOME-aware installation leg, with no observed adjacent regression." Record committed as part of the verification history.
