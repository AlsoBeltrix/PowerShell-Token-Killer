# hcc-6: Install fails when claude is detected but its CLI is absent

**Severity**: HIGH — a supported machine shape (no claude CLI, possibly a ~/.claude remnant) cannot install the product: the whole install rolls back.
**Status**: In progress
**Branch**: —
**Commit**: (pending)

## Evidence
Owner field report 2026-08-04: "install fails if claude is not installed",
on `19201a1` (pre-dates the kimi/consent range). Mechanism:
`Test-PtkAgentPresent` counts a `~/.claude` directory as claude-present, so
the claude leg runs; with no CLI the leg returns `(-not $skipHook)` = false
(mhi-9); a failed leg exits `ptk_init` 1; `install.ps1`'s
`Invoke-PtkHarnessInitialization` throws on that and the transaction rolls
the whole install back.

## Predicted observable failure
`install.ps1` on a machine without the claude CLI aborts and restores the
prior payload — the product cannot be installed at all — because an
optional harness integration could not be fully wired.

## What
A degraded harness leg (guidance-only, no registration/hook possible) is
reported as a FAILED leg. mhi-9 pinned that nonzero exit when the concern
was the hook pointing at an invisible tool; nobody made the install arm
tolerate it.

## Approach
The claude leg now distinguishes "cannot wire this harness" from "wiring
failed". CLI-absent still skips the hook, but only an actual `claude mcp
add` error sets `$registrationFailed` and fails the leg; a missing CLI
degrades to guidance-only SUCCESS, so detection-mode installs on
claude-less machines complete. The mhi-9 test's nonzero-exit assertion was
the defect's own pin and is amended; a new detection-mode test reproduces
the owner-reported shape end to end (redirected HOME with `.claude`,
gutted PATH).

## Files changed
- `scripts/ptk_init.ps1` (claude leg) — `$registrationFailed` return semantics
- `tests/PwshTokenCompressor.Tests.ps1` — mhi-9 amendment + hcc-6 end-to-end test

## Guard proof
- `tests/PwshTokenCompressor.Tests.ps1::'claude leg skips the hook but keeps the nudge when the claude CLI is absent'` (amended) and `::'detection-mode run succeeds when claude is detected but its CLI is absent (hcc-6)'` — both FAIL with the fix stashed (exit 1), PASS restored (verified 2026-08-04).

## Coder dispute (if any)
—

## Known gaps
The mhi-9 test's `$LASTEXITCODE -Not -Be 0` assertion encodes the defective
behavior and is amended by the fix; its skip-hook/no-settings/nudge
assertions stand. Registration FAILURE (CLI present, `claude mcp add`
errors) stays a leg failure — that is a real error, not a missing optional
harness.

## Reviewer comments
(intake) Owner field report 2026-08-04 during the hcc loop; mechanism
confirmed by code read.
