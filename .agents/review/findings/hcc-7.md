# hcc-7: Consent prompt pauses before its text is visible (buffered pipe)

**Severity**: MEDIUM — the installer's one interactive moment presents as a hang: the pause happens with no prompt on screen, and the prompt appears only after the user answers blind.
**Status**: In progress
**Branch**: —
**Commit**: (pending)

## Evidence
Owner field report 2026-08-04 (first real install of the consent feature):
run stopped after `Found: 1 claude, ...`; the `Skip (1,3; ...)` prompt
appeared only after Enter was pressed. Mechanism, confirmed by pty probe:
`install.ps1` ran the ptk_init child as `& pwsh -File ... | Out-Host`;
piping a native command makes pwsh assemble its stdout into pipeline
objects LINE BY LINE, so a partial line (the prompt) is held until a
newline or child exit. The newline-terminated `Found:` line streamed
through; the prompt did not. A first fix attempt (flush before read in
ptk_init) changed nothing — the buffering is in the parent's line
splitter, not the child — which the pty probe proved both ways.

## Predicted observable failure
An interactive install shows `Found: ...` then appears to stop; the skip
prompt is invisible until after the user answers it.

## What
Composition defect: install.ps1 piped an interactive child through the
pipeline, and pwsh line-assembles piped native output.

## Approach
`Invoke-PtkHarnessInitialization` no longer pipes the child through
`Out-Host`; direct invocation lets the child inherit the console, so the
prompt renders before the read blocks. ptk_init keeps the explicit
write-then-flush prompt (correct for raw-piped parents). Exit-code
semantics unchanged ($LASTEXITCODE).

## Files changed
- `scripts/install.ps1` — `Invoke-PtkHarnessInitialization` drops `| Out-Host`
- `scripts/ptk_init.ps1` — prompt is written and flushed before the read
- `tests/PwshTokenCompressor.Tests.ps1` — structural guard test

## Guard proof
- Manual (pty required; Pester cannot allocate one): `/tmp/ptyprobe.py`
  drives `pwsh -Command "& pwsh -File ptk_init.ps1 -DryRun [| Out-Host]"`
  under a pty. With the pipe: prompt never visible before input (30s);
  without: visible at 0.5s. Run 2026-08-04 on this repo.
- `tests/PwshTokenCompressor.Tests.ps1::'install.ps1 does not pipe the init child through Out-Host (hcc-7)'` — pins the mechanism structurally; FAILS with the pipe restored (verified by stash-revert 2026-08-04).

## Coder dispute (if any)
—

## Known gaps
—

## Reviewer comments
(intake) Owner field report 2026-08-04, immediately after the hcc loop
closed; mechanism confirmed by code read and by the earlier manual smoke
(piped stdin hides it).
