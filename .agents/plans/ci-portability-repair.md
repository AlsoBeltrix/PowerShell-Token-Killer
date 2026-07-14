# Plan: CI portability repair after audited-harness Slice 6

**Status:** MASTER-LANDING FOLLOW-UP APPROVED by the owner on 2026-07-14
(`go` after the exact `00e74d2` failure diagnosis and two-commit test-only
proposal). The original repair completed at code head `3c61886`; GitHub
Actions run `29313220388` passed Ubuntu, macOS, and Windows, including each
server suite and stdio handshake. Master run `29314404462` then exposed two
pre-existing harness flakes under identical server and workflow trees. Slices
7-8 below stabilize those tests without changing production runtime behavior,
installing RTK into ordinary unit-test jobs, or deciding whether a future PTK
release bundles a pinned RTK binary.

## Evidence and problem

GitHub Actions run `29310719880` tested `69bd0d5` on Ubuntu, macOS, and
Windows. Checkout, SDK setup, and Pester passed on every runner. The .NET
server suite failed, so the handshake step was skipped:

- Ubuntu and macOS each failed only the two data rows of
  `Forced_rtk_fallback_metadata_is_raw_invariant`. The test expected
  `RtkIneligibleShape`, but a clean runner with no RTK binary correctly
  planned `RtkExecutableUnavailable`. The same failure existed at the
  pre-Slice-6 head `aca20a6`; locally, forcing `PTK_RTK_PATH` to a missing
  file reproduces it.
- Windows also exposed five independent test-harness assumptions: an
  unassignable `BuiltinUsersSid` used as an alternate owner, a real module
  enumeration constrained to a test-only 60-second host budget, a
  20,000-line `cmd.exe` timing workload constrained to 15 seconds, a
  supposedly relative path that becomes absolute across Windows volumes,
  and a live `Get-Command` probe that does not select one result before
  serializing it.

The production code is not changed merely to satisfy these failures. Each
test must establish its own preconditions and retain the behavioral assertion
that originally guarded the accepted audited-harness slice.

## Master-landing follow-up evidence

Master run `29314404462` tested `00e74d2`: Ubuntu and macOS passed, while
Windows failed two tests. Exact tree comparison shows `3c61886..00e74d2`
changes only `.agents/` documentation; the entire `server/` tree and workflow
are identical to green run `29313220388`.

- `Private_output_stop_is_joined_before_disposal_and_guard_release` failed in
  its setup-only warm invocation before installing any stop/join hooks. The
  test gives cold runspace readiness, execution, and recovery-renderer startup
  one second. The same failure is already recorded in review history, followed
  by repeated isolated and full-suite passes.
- `Concurrent_startup_repair_opens_and_publishes_one_runtime` shares one
  singleton `AuditCallContextAccessor` across eight synthetic concurrent
  requests even though production registers that mutable holder per request.
  Overlap makes one request observe another's current context and return the
  exact `audit_boundary_invalid` seen in this run and an earlier Windows run.
  The failure reproduced immediately in the clean `NETWATCH-01` checkout at
  exact master head `00e74d2`.

## Slices

Each numbered slice is one finding and one commit.

1. **Hermetic forced-RTK fallback metadata.** Provision the existing native
   cross-platform RTK stub inside the theory, set `PTK_RTK_PATH` for the
   operation, and restore the environment and fixture in `finally`. Preserve
   both `raw` rows and every existing plan assertion. Guard proof: with the
   fixture change temporarily absent and RTK forced missing, both rows fail;
   restored, both pass under the same missing ambient RTK condition.
2. **Capability-faithful Windows owner setup.** Always verify that the audit
   factory leaves the root, spool, host identity, and segment owned by the
   current user with one protected explicit full-control ACE. For the
   alternate-owner precondition, use only a distinct owner SID exposed by the
   current token as assignable; do not assume `BuiltinUsersSid` is eligible.
   Keep the always-valid ACL assertion even when the token offers no distinct
   alternate owner.
3. **Production-faithful module-enumeration budget.** Run the real
   `listAvailable` integration check with the production default host budget
   instead of the test-only 60-second budget. Do not weaken the shipped-module
   assertion or alter the runtime-local cache.
4. **Bounded output-buffer reuse workload.** Retain the blocked first write,
   delayed foreign write, buffer overwrite opportunity, and canary
   assertions, but replace the 20,000-line shell loop with one delayed foreign
   canary. The test must exercise the ownership boundary without making
   success depend on hosted-runner console throughput.
5. **Actually relative cold target fixture.** Supply an explicit relative
   source string to `TryCapture`; do not derive one between paths that can live
   on different Windows volumes. Preserve the subsequent PATH re-resolution
   and content-identity assertions.
6. **Singular live PowerShell resolution probe.** Select the first live
   `Get-Command` result before serializing command type and source, and compare
   Windows source paths with Windows path-identity casing semantics. Preserve
   all three candidate-order comparisons against the cold resolver.
7. **Separate setup warm-up from the tested timeout.** Give only the setup
   warm invocation an explicit ten-second call budget. Keep the host default
   and the subject invocation at one second so the test still drives the
   timeout, blocked stop, joined cleanup, and guard-release path it names.
   Prove the old budget fails under a deterministic delayed private-output
   opening, the new setup reaches the subject, and removing the production
   stop join still makes the stabilized test fail.
8. **Use production-faithful request scopes in the startup race.** Register
   `AuditCallContextAccessor` as scoped, create and dispose one service scope
   per contender, and hold all eight handlers at a barrier so request overlap
   is deterministic. Preserve the exactly-two open attempts, single recovery
   and startup, eight handler calls, eight accepted/completed pairs, and final
   stop assertions. Mutating the accessor back to singleton must fail under
   the barrier; restoring scoped ownership must pass on Windows.

## Verification

After each slice, run its focused test and commit before beginning the next.
After all slices:

1. `dotnet test server/PtkMcpServer.slnx`
2. `pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"`
3. `pwsh -NoProfile -File server/test-handshake.ps1`
4. Review the exact repair range for weakened assertions, production changes,
   and unproved platform assumptions.
5. Push only after separate owner approval. The Windows branches remain
   provisionally verified until the pushed matrix is green; record the exact
   run rather than claiming local macOS proves Windows behavior.

Completed evidence: owner-approved GitHub Actions run `29313220388` passed all
three matrix jobs at exact head `3c618867adbe1c172f0b95fed53cc7425280a3f1`.

For slices 7-8, run each focused red/green proof on `NETWATCH-01` from a clean
exact checkout, then repeat both focused tests there before the complete local
battery and a new owner-approved hosted matrix. Commit the two findings
separately. A green rerun without the stabilizing changes is insufficient
because it merely resamples both races.

## Non-goals

- Bundling, vendoring, downloading, or compiling upstream RTK for users.
- Adding a real-RTK compatibility matrix. That is a separate distribution and
  integration-test design decision.
- Changing `ExecutionPlanner`, audit storage, session-runtime cache ownership,
  cold command resolution, or output-capture production behavior.
