# hcc-7: Consent prompt pauses before its text is visible (buffered pipe)

**Severity**: MEDIUM — the installer's one interactive moment presents as a hang: the pause happens with no prompt on screen, and the prompt appears only after the user answers blind.
**Status**: In progress
**Branch**: —
**Commit**: `4fa2d30` (first fix — insufficient, reopened); repair (pending)

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
FIRST FIX (4fa2d30): `Invoke-PtkHarnessInitialization` dropped its
`| Out-Host` pipe and ptk_init flushed its prompt explicitly. REOPENED by
the reviewer: the transaction module's `& $RegistrationCutover | Out-Null`
(scripts/ptk_install_transaction.psm1:360) is an outer pipeline in the same
path — with the inner pipe gone, the outer one captured the child instead
(the reviewer pty-tested the actual composition: Found list AND prompt
invisible). REPAIR: the cutover invocation is no longer piped; the chain
terminal → install.ps1 → cutover → init child inherits the console end to
end, so the prompt renders before the read blocks. The cutover emits
nothing else material (audited: Install-PtkRtk, harness init, ARP entry).
The InstalledValidation pipe stays — that child never prompts (reviewer
confirmed). Exit-code semantics unchanged.

## Files changed
- `scripts/ptk_install_transaction.psm1` — `& $RegistrationCutover` unpiped (repair)
- `scripts/install.ps1` — `Invoke-PtkHarnessInitialization` drops `| Out-Host`
- `scripts/ptk_init.ps1` — prompt is written and flushed before the read
- `tests/PwshTokenCompressor.Tests.ps1` — structural guard over both pipeline sites

## Guard proof
- Module-level pty probe (pty required; Pester cannot allocate one):
  `/tmp/hcc7-module-probe.ps1` invokes the real `Invoke-PtkInstallTransaction`
  with a stub cutover child printing a newline-less prompt; driven by
  `/tmp/ptyprobe2.py` under a pty. Reverted module: prompt never visible
  before input (30s). Repaired: visible at 0.54s. Run 2026-08-04.
- `tests/PwshTokenCompressor.Tests.ps1::'install.ps1 does not pipe the init child through Out-Host (hcc-7)'` — pins BOTH pipeline sites structurally; each half FAILS with its site restored (verified by stash-revert of each file, 2026-08-04).

## Coder dispute (if any)
—

## Known gaps
stdin interactive while stdout is piped (`pwsh install.ps1 | tee log`)
still line-holds the prompt — same limitation class, accepted; interactive
installs have a console for both.

## Reviewer comments
(intake) Owner field report 2026-08-04, immediately after the hcc loop
closed; mechanism confirmed by code read and by the earlier manual smoke
(piped stdin hides it).

(verification, first fix) Reviewer: codex / gpt-5.6-sol / high / standard — codex-cli 0.146.0. reviewed_sha 4fa2d307481b9bc689b7c12c8951bf3de4aaf78b, base_sha 33806a06c77f437bd879d58f94a7b8ad462f86e0, guard_confirmed true, capability_ok true, verdict **reopened**, 2026-08-04. Key comment: "scripts/ptk_install_transaction.psm1:360 still invokes the entire RegistrationCutover scriptblock through `| Out-Null`. After hcc-7 removes the inner `| Out-Host`, the init child's output flows into that outer pipeline instead of the terminal. A PTY test of this actual composition showed neither the Found list nor the prompt before input... The install-driven observable failure therefore remains." Also confirmed: exit-code rollback path intact; the package-smoke pipe correctly left unchanged. Coder acted: transaction module unpiped (repair commit pending), module-level pty probe proves both directions; escalates to frontier on redispatch (T5).
