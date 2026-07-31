# ci-rollback-2: Installer transaction fixture inherits the wrong Windows owner

**Severity**: LOW — the hosted Windows server suite fails before exercising
the installer transaction assertions.
**Status**: Accepted; pending integration
**Branch**: `fix/ci-rollback-owner-fixture`
**Commit**: `79c0fd8`

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

- `server/test-install-transaction.ps1:29-38` — explicitly set the Windows
  fixture payload owner to the current user.

## Guard proof

- `InstallerTransactionTests.Activation_and_registration_faults_restore_exact_prior_state`
  — hosted run `30658534883` fails before the fix with the exact owner error;
  replacement run `30660093533` on draft PR 18 passes the complete Windows
  server suite and handshake. The focused test and full local 1,215-test
  server suite also pass.

## Guard attribution

Runs `30658534883` and `30660093533` are sibling-branch runs rather than an
isolated one-commit pair. The guard therefore rests on the failure text's
unique origin in `Set-PtkInstallRootAccess` and the fixture fix being the only
change that satisfies that precondition.

## Coder dispute (if any)

None.

## Known gaps

The local host already creates a current-user-owned fixture, so the revert side
of the guard is supplied by the exact hosted run rather than a local failure.
The unrelated macOS slow-seal failure remains separately scoped.

Production's mismatched-owner refusal lacks a direct negative test.
`server/test-staged-install.ps1` independently creates an owner-sensitive
payload fixture and is not exercised by CI; both are separate follow-ups.

## Reviewer comments

- Redispatch: Claude Code `2.1.220`; reviewed head `02f5a0c`, base `a261a94`;
  `guard_confirmed=true`; verdict `accepted`; UTC
  `2026-07-31T19:59:22Z`.
- Reviewer accepted the hosted evidence as the manual guard because the exact
  failure text has one repository origin and the fixture fix alone satisfies
  that precondition. Final orchestrator outcome: accepted.

Reviewer: claude / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`
/ max / frontier — escalated: owner (inline, session-only)

- Claude Code `2.1.220`; reviewed head `79c0fd8`, base `a261a94`;
  `guard_confirmed=false`; verdict `accepted`; UTC `2026-07-31T19:49:40Z`.
- The fix is correctly scoped to the sole payload root production validates,
  preserves the inherited DACL, runs before child creation, and does not alter
  production ownership rejection or ACL normalization.
- The reviewer identified the corrected line citation above and the commit
  field update.
- Separate follow-up: production's mismatched-owner refusal lacks its own
  negative test; do not widen this finding.
- First dispatch was not accepted because the reviewer could not run
  Bash and therefore returned `guard_confirmed=false`. Hosted CI supplies the
  manual red/green guard but does not rewrite the transcript field.
