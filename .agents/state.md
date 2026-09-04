# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

**Canonical CI is green at `b2253a9`.** Run `33910227193` passed all six
Ubuntu, macOS, and Windows product/SIEM jobs, including the repaired
version-fallback step and the final Windows handshake. The owner explicitly
reactivated global release-readiness work on 2026-09-04. Its activation-gate
audit is in progress: the live GitHub backlog contains only #30, whose remoting
acceptance passed but whose `i30-1` stream-retention contract still needs an
owner ruling; unique per-build identity and current exact-candidate artifact
proof are not yet established.

## Next

- Reconcile the stale `rbc-5` record against the superseding production plan
  and current five-tool surface; do not recreate removed background-job work.
- Continue the release-readiness activation audit, then execute each authorized
  local blocker. Outward release actions remain separately gated.

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
- **rbc-5 remains open:** resilience R7 must land creation-time worker
  containment plus the Windows hard-supervisor-death background-descendant
  guard and proof. `.agents/review/findings/rbc-5.md` owns the finding.

## Verification

- Automated verification entry point and current exact-head results:
  `.agents/repo-guidance.md` (Verification).
- Version-fallback repair worktree, 2026-09-04: focused exit-state guard passed;
  Pester 112 passed/3 skipped; server and SIEM solutions passed (SIEM 357/357,
  no warnings); mini-SIEM lifecycle, registration handshake, release selection,
  signing documentation, notarization recovery, and Developer ID selection
  checks passed; all server and SIEM projects reported no vulnerable packages.
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
- `.agents/review/findings/rbc-5.md`
- `.agents/review/index.md`
- `.agents/machines.md`
- `.agents/decisions.md` (under owner hold for the named stale entries)
- `.agents/repo-guidance.md`
- `AGENTS.md`

Older plans remain prior art or history, not active implementation authority.
Rotated state history lives in `docs/history/state-archive.md`.

## Unrecorded Repo Memory

- None known.
