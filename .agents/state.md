# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

**Canonical CI is green at `b2253a9`; local release-readiness work is ahead.**
Run `33910227193` passed all six Ubuntu, macOS, and Windows product/SIEM jobs.
The 2026-09-04 unique-build-identity slice now gives every PTK and SIEM build a
fresh exact identity across package provenance, binaries, MCP initialize,
runtime diagnostics, audit records, receiver logs, and receiver health. Its
same-commit rebuild/dirty-source guard and full local macOS battery pass. The
release workflow also now refuses pre-existing release tags instead of
clobbering assets (`rr-1`). The live GitHub backlog contains only #30; remoting
acceptance passed, but `i30-1` still needs the one owner stream-retention
ruling.

The public no-clone path now ships one checksum-verified installer bundle with
its two required modules (`rr-2`).

## Next

- Finish the release-readiness activation audit and freeze the candidate
  contract/version after the `i30-1` ruling; then build the current-head
  five-RID draft under its separate outward-action gate.
- Obtain the one required owner ruling for `i30-1` without coupling unrelated
  Sentinel or package-manager feature decisions to the release candidate.
  Outward release actions remain separately gated.

## Open / Parked

- Warm-backend field validation still lacks AD native import/warm reuse, exact
  first-versus-repeated `Get-Queue` latency, Graph unattended certificate
  authentication, and useful Outlook data. The broader warm on-prem Exchange
  remoting workflow passed on 2026-08-31, and EXO app-only warm reuse passed on
  2026-07-29; host evidence lives in `.agents/machines.md`.
- Durable checkout shared runspaces were removed from candidate scope by the
  owner on 2026-07-11. The older open-decision entry remains stale while
  `.agents/decisions.md` is under hold; the idea plan is history/evidence, not
  current implementation direction.
- Fable R0 noted one non-blocking fixture-hygiene risk: if the testhost dies
  before its `finally`, a Unix guardian fixture process group can remain until
  its guardian is killed. This cannot make the guard falsely pass or affect the
  product contract; an stdin-EOF guardian watch remains a later test-hygiene
  candidate.

## Blockers

- **GitHub #30 stream-capture contract:** remoting acceptance passed, but
  `.agents/review/findings/i30-1.md` confirms current source drops verbose,
  information, and `Write-Host` records from both response and recovery
  artifact. The owner must choose the contract before repair.
- **Sentinel Decision D:** `.agents/plans/siem-sentinel-validation.md` is
  planning-only. S0 read-only Azure feasibility discovery needs a separate
  owner go; no Azure inspection or mutation is authorized.
- **Package-manager Decision D1:** `.agents/plans/package-manager-distribution.md`
  is draft-only. The owner must choose the `ptk` CLI entry-point shape before
  any slice is approved.
- **Residual serialized-suite signals:** default xUnit collection parallelism
  remains disabled at `c215515`, and the full local server suite passed. A
  recurrence of the anchored-evidence publication/removal race or
  `JobManager.Dispose` bounded-observer failure in a serialized run remains a
  real signal; fixed-watchdog sensitivity also remains.
- **Current-head generic Windows acceptance:** `ASHBIAMWEB1` passed the
  Windows x64 package, Job Object, and 100-cycle acceptance at `7eaf8a0`, but
  that is not current head. Its ordinary token still cannot execute the SIEM
  symlink-protection cases. Machine evidence owns the detail.
- **Decision-log conflicts remain on owner hold:** the policy-file gate and
  shared-host entries conflict with later owner direction, and a mini-SIEM
  evidence paragraph describes superseded producer behavior. Do not implement
  those stale directions until the hold is released.

## Verification

- Automated verification entry point and current exact-head results:
  `.agents/repo-guidance.md` (Verification).
- Unique-build-identity commit `4c636fe`, 2026-09-04: repeated PTK and SIEM builds
  received four distinct identities; dirty-source detection passed; packaged
  initialize, cold `ptk_state`, audit producer identity, layout validation,
  and 26-check direct product proof passed. Post-commit PTK and SIEM
  `-RequireCleanSource` package gates passed with `source_dirty=false`. Full
  Pester passed 112/3 skipped;
  server passed 1,354/1,354 with two known analyzer warnings; SIEM passed
  357/357 clean; lifecycle, registration handshake, actionlint, release helper,
  signing-helper, and both dependency-vulnerability gates passed.
- Installer-bundle release-readiness slice after `fa3d476`, 2026-09-04:
  red/green helper proof passed for exact bundle contents, portable SHA-256,
  duplicate refusal, all eleven release artifact hashes, exact upload set,
  existing-tag immutability, and fail-closed release queries; ShellCheck,
  actionlint, README PowerShell parse, and `git diff --check` passed.
- Version-fallback repair worktree, 2026-09-04: focused exit-state guard passed;
  Pester 112 passed/3 skipped; server and SIEM solutions passed (SIEM 357/357,
  no warnings); mini-SIEM lifecycle, registration handshake, release selection,
  signing documentation, notarization recovery, and Developer ID selection
  checks passed; all server and SIEM projects reported no vulnerable packages.
- Local head `696de29`, `nagatha-2.local`, 2026-09-04: provisional osx-arm64
  layout validation and the full packaged direct-product proof passed from an
  isolated home, including uninstall. This is current local package evidence,
  not the still-required final-version five-RID candidate run.
- Review-loop evidence lives in `.agents/review/index.md`; host-specific
  evidence lives in `.agents/machines.md`. Do not duplicate volatile counts
  here.

## Active Sources

- `.agents/plans/siem-sentinel-validation.md` (DRAFT; Decision D blocks S0)
- `.agents/plans/package-manager-distribution.md` (DRAFT; D1 blocks all slices)
- `.agents/review/findings/i30-1.md` (CONFIRMED; owner ruling needed)
- `.agents/plans/github-release-packaging.md` (COMPLETE 2026-08-12)
- `.agents/plans/rtk-router-delegation.md` (Slices 0-6 executed; Slice 7
  completed through the release-packaging plan)
- `.agents/plans/production-reliability-salvage.md`
- `.agents/review/index.md`
- `.agents/machines.md`
- `.agents/decisions.md` (under owner hold for the named stale entries)
- `.agents/repo-guidance.md`
- `AGENTS.md`

Older plans remain prior art or history, not active implementation authority.
Rotated state history lives in `docs/history/state-archive.md`.

## Unrecorded Repo Memory

- None known.
