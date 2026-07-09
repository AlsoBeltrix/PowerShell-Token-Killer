# mhi-11: agy leg leaves a pre-existing hooks.json active

**Severity**: MEDIUM — a stale, unverified *blocking* hook can survive install and deny agy command use while the leg reports "no hook shipped"; narrow trigger (requires a prior manual/earlier hooks.json), severe consequence.
**Status**: Fixed (awaiting reviewer re-review)
**Branch**: master (direct; repo precedent)
**Commit**: `6c1d025`
Reviewer intake id: `mhi-agy-stale-hook-survives` (codex, codex-cli 0.142.5).

## Evidence
`scripts/ptk_init.ps1:579-596` — the agy install path writes `plugin.json`, `rules/ptk.md`, and `mcp_config.json`, and even removes a stale plugin-level `mcp_config.json` when globally registered (line 589), but never removes an existing `hooks.json`. `docs/harness-support.md:16` and the plan record the decision that the shipped plugin carries **no** `hooks.json` (live firing unverified). `scripts/ptk_init.ps1:597-598` then reports enforcement deferred.

## Predicted observable failure
If `~/.gemini/config/plugins/ptk/hooks.json` exists from an earlier attempt, re-running `ptk_init.ps1 -Agent agy` leaves it active while printing that no hook is shipped: Antigravity can execute an unverified deny hook — blocked commands plus a false install report.

## What
The leg enforces its declared end-state for `mcp_config.json` but not for `hooks.json`; install is not idempotent toward the documented no-hook state.

## Approach
The agy install path now enforces the documented no-hook end-state, mirroring the existing stale-`mcp_config.json` cleanup precedent: after writing `plugin.json`/`rules/ptk.md`, a pre-existing `hooks.json` in the plugin dir is removed with a visible `[agy] pre-existing hooks.json removed` report, in both registration branches. `-DryRun` discloses the pending removal ("would remove the pre-existing hooks.json") without performing it, keeping the dry-run action list complete. The removal is deliberately unconditional (not marker-aware) because the shipped plugin never writes any `hooks.json`; the code comment flags that a future verified hook must make this ownership-aware (Known gaps).

## Files changed
- `scripts/ptk_init.ps1` — agy leg: install removes a pre-existing `hooks.json` (with report); `-DryRun` branch discloses the pending removal when one exists.
- `tests/PwshTokenCompressor.Tests.ps1` — guard test `agy leg removes a pre-existing hooks.json (no hook is shipped)`: seeds a deny-all `hooks.json` under a seamed `-AgyPluginRoot` with a registered `-AgyConfigPath` (no binary needed), asserts `-DryRun` discloses but leaves the file, then that the real install removes it and reports.

## Guard proof
Stashed `scripts/ptk_init.ps1`, re-ran the guard test: FailedCount=1 — the `-DryRun` output contained only the hook-less plugin summary with no removal disclosure ('would remove the pre-existing hooks.json' failed to match), exactly the predicted silent survival. Stash popped; test passes with the fix. Full battery: 83 tests, 82 passed, 0 failed, 1 skipped (pre-existing), ~14s.

## Coder dispute (if any)
None — admitted as graded.

## Known gaps
When a future slice ships a verified hooks.json, the removal must become marker/ownership-aware; out of scope here.

## Reviewer comments
(pending re-review)
