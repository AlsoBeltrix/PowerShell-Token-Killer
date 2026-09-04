# rr-5: packaged-install fixture has the wrong Windows owner

**Severity:** HIGH — both Windows candidate jobs stop before installation proof.
**Status:** FIXED LOCALLY; native candidate rerun pending.
**Scope:** `server/test-staged-install.ps1`; no shipped ownership policy change.

## Evidence and attribution

Candidate run `33928685705`, exact source
`c8b084fbb79c9d73965dbfc632163919c29e50dd`, successfully built and signed both
Windows PTK/SIEM payloads, then failed the packaged-install transaction on
both `win-x64` and `win-arm64` with:

> The PTK payload root must be owned by the current Windows user.

The message has one origin, `Set-PtkInstallRootAccess`, before transaction
validation. The fixture creates its disposable payload root with `New-Item`
and assumes it is owned by the current user. Hosted Windows can assign a group
owner instead. This is the same fixture defect repaired in
`server/test-install-transaction.ps1`; `ci-rollback-2.md` explicitly named
the staged-install script as a separate follow-up.

The security refusal is load-bearing and remains unchanged. This is not a
running-process/file-lock problem. The failed run created no release draft,
so `0.3.0-rc.2` remains available for a clean rebuilt candidate without replacing
an existing release or asset.

## Repair and verification

On Windows only, read the newly created disposable payload directory's existing
security descriptor, set its owner to the current test user's SID, and write
that descriptor back before populating child files. Preserve its DACL and leave
production code, package contents, and all install/handshake assertions intact.

The two native failed jobs above are the red proof. A fresh canonical five-RID
candidate run must supply the Windows green proof; macOS alone cannot verify
the Windows-specific fix. Record local non-Windows regression and native rerun
results here before closing this finding.

Local checks after the fix: PowerShell parse passed; the staged-install proof
passed both complete handshakes against the downloaded attempt-one Mac layout
in a disposable home; `server/test-install-transaction.ps1` passed; release
assembly/installer-bundle guards passed; Pester passed 113 with 3 platform skips;
SIEM passed 357/357; server passed 1,360/1,360 with the two known analyzer
warnings; `git diff --check` passed. Fix commit `eb3f999` reached canonical
`master`. Native rerun `33930714689` is testing that exact commit. Native Windows
verification is still required.
