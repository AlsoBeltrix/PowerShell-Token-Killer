# ci-rollback-2: Installer transaction fixture inherits the wrong Windows owner

**Severity**: LOW — the hosted Windows server suite fails before exercising
the installer transaction assertions.
**Status**: In progress
**Branch**: `fix/ci-rollback-owner-fixture`
**Commit**: pending

## Evidence

Draft PR 17, GitHub Actions run `30658534883`, exposed the caught exception:
`The PTK payload root must be owned by the current Windows user.` The failure
originates in `Set-PtkInstallRootAccess` before the intended transaction fault.
`server/test-install-transaction.ps1:25` creates each payload root without
setting its owner, so the Windows 2025 hosted runner can leave the inherited
owner as a different principal.

## Predicted observable failure

On a Windows runner whose temporary-directory owner is not the current user,
the fixture fails the installer ownership precondition and the entire Windows
server suite remains red without testing rollback behavior.

## What

The fixture assumes that `New-Item` makes the current Windows user the payload
root owner. That assumption is false on the hosted Windows runner.

## Approach

On Windows only, read the new payload directory's existing security descriptor,
set its owner to the current user SID, and write it back before constructing the
fixture contents. Production ownership validation and ACL normalization remain
unchanged.

## Files changed

- `server/test-install-transaction.ps1:27` — explicitly set the Windows
  fixture payload owner to the current user.

## Guard proof

- `InstallerTransactionTests.Activation_and_registration_faults_restore_exact_prior_state`
  — hosted run `30658534883` fails before the fix with the exact owner error;
  the replacement Windows PR run must pass. The focused test and full local
  server suite must also remain green.

## Coder dispute (if any)

None.

## Known gaps

The local host already creates a current-user-owned fixture, so the revert side
of the guard is supplied by the exact hosted run rather than a local failure.
The unrelated macOS slow-seal failure remains separately scoped.

## Reviewer comments

Pending Claude Opus 5 review.
