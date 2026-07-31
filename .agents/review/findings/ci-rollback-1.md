# ci-rollback-1: Windows rollback failure hides the caught exception

**Severity**: LOW — hosted Windows CI remains opaque because the assertion
replaces the transaction failure with a generic message.
**Status**: In progress
**Branch**: `fix/ci-rollback-diagnostic`
**Commit**: pending

## Evidence

GitHub Actions runs `30649064747` and `30652816210` both fail
`InstallerTransactionTests.Activation_and_registration_faults_restore_exact_prior_state`
on Windows at `server/test-install-transaction.ps1:205`. The assertion reports
only that the caught exception did not contain the expected rollback phrase;
it does not print the caught exception.

## Predicted observable failure

When the hosted runner takes the unexpected transaction path, CI reports only
the generic assertion text and the root cause cannot be distinguished from an
ACL, snapshot, hook, or rollback failure.

## What

The failure-path assertion discards `$failure.Exception.Message`, which is the
only evidence identifying the unexpected transaction path on the hosted runner.

## Approach

Append the caught exception message to the existing assertion text. This does
not change the assertion condition, transaction behavior, or pass/fail result;
it makes the already-failing hosted path diagnostic.

## Files changed

- `server/test-install-transaction.ps1:205` — include the caught exception in
  the failure message.

## Guard proof

The failure path is hosted-Windows-specific and does not reproduce locally.
The manual guard is the next GitHub Actions Windows server-test run: reverting
this change yields the generic message recorded in runs `30649064747` and
`30652816210`; restoring it must add `Actual: ...` without changing the failed
test identity. The focused test and full local server suite must remain green.

## Coder dispute (if any)

None.

## Known gaps

This diagnostic does not repair the underlying Windows transaction failure.
Claude Opus 5 is reachable, but the required Bash guard command was denied on
both bounded capability probes, so no guard-confirmed codereview verdict is yet
possible on this machine.

## Reviewer comments

Pending Claude Opus 5 review.
