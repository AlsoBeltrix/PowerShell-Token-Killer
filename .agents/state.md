# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

**Canonical `master` contains verified product commit `0c9328a`; its exact
product tree is green in CI.** Run `33924847924` at `72ccd90` passed all six
Ubuntu, macOS, and Windows product/SIEM jobs before docs-only record updates.
The 2026-09-04 unique-build-identity slice now gives every PTK and SIEM build a
fresh exact identity across package provenance, binaries, MCP initialize,
runtime diagnostics, audit records, receiver logs, and receiver health. Its
same-commit rebuild/dirty-source guard and full local macOS battery pass. The
release workflow also now refuses pre-existing release tags instead of
clobbering assets (`rr-1`). The live GitHub backlog contains only #30; remoting
acceptance passed, and `i30-1` is fixed on canonical `master` by rendering and
retaining `Write-Host`/information and verbose records while continuing to drop
progress.
The external issue remains open only because closing it is a separate outward
mutation.

The public no-clone path now ships one checksum-verified installer bundle with
its two required modules (`rr-2`).

The Codex init leg now repairs an orphaned PTK tool-policy table before
invoking the otherwise-bricked Codex CLI, while preserving valid registrations
and their policies (`mhi-13`). Codex redirect enforcement remains unshipped:
the current TUI exposes shell and MCP work through the same outer `exec` tool.

The policy-independent public operations baseline is committed at `c55169f`:
privacy, known limitations, contribution and community templates, release
notes, and immutable-version withdrawal recovery. The requirement-by-
requirement activation audit is current in `.agents/plans/release-readiness.md`.
That plan now also defines the exact uploaded/downloaded candidate evidence set
for the five-RID final gate.

The release workflow now runs the packaged transaction/activation proof on
every RID after signing and before archive creation (`rr-3`).

The no-version public bootstrap now selects and pins the newest published
stable or prerelease rather than GitHub's stable-only latest alias (`rr-4`).
An executable README fixture caught and repaired the initial static guard's
nested REST-array gap after `d40228c`; the proof now covers selection,
checksum, pinned installer invocation, and cleanup.

Local product head `0c9328a` now has a complete clean macOS verification
battery: Pester, server, SIEM, registration handshake, mini-SIEM lifecycle,
four-build identity, release/signing helpers, both vulnerability scans, and a
fresh `osx-arm64` package's staged activation plus 32-check installed-product
and uninstall proof.

## Next

- Settle the remaining release policy, freeze the candidate contract/version,
  and build the current-head five-RID draft under its separate outward-action
  gate.
- Close canonical GitHub #30 only under its separate outward-action gate.
  Unrelated Sentinel and package-manager feature decisions remain
  outside the release candidate.
- Settle the security-reporting channel, support expectations, and next version
  as separate owner decisions. `v0.3.0-rc.1` cannot be reused because both a
  published prerelease and a stale draft already use that tag.

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

- **Canonical GitHub #30 closure:** `i30-1` is fixed on `origin/master` and exact-
  head CI run `33924847924` passed all six jobs. The live issue remains open;
  issue mutation is a separate outward-action gate.
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
- Canonical CI run `33924847924`, exact head `72ccd90`, 2026-09-04: all six
  Ubuntu, macOS, and Windows product/SIEM jobs passed, including the server
  stream-retention tests and stdio handshakes.
- Clean local product head `0c9328a`, 2026-09-04: Pester passed 113 with 3
  platform skips; server passed 1,360/1,360 with the two known analyzer
  warnings; SIEM passed 357/357 without warnings; registration handshake,
  mini-SIEM lifecycle, four-build identity, release selection, signing docs,
  notarization recovery, Developer ID selection, and both transitive
  dependency vulnerability scans passed. Actionlint and release ShellCheck
  passed. A fresh provisional `0.0.0-stream-clean` `osx-arm64` layout passed
  validation, packaged activation, the 30-check direct proof, isolated public
  source install, and the 32-check installed-product/uninstall proof, including
  information/verbose response visibility and immutable recovery while progress
  stayed excluded. Both explicit throwaway roots were removed.
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
- Public-operations slice `c55169f`, 2026-09-04: issue-form YAML parsed, all
  added relative Markdown links resolved, actionlint passed, and
  `git diff --check` passed. Live community profile was 42% before these local
  files reach canonical GitHub; private vulnerability reporting was disabled.
- Packaged-install release gate after `6003273`, 2026-09-04: workflow guard
  failed before the step was added; helper proof, actionlint, ShellCheck, and
  `git diff --check` passed after repair. A fresh local `osx-arm64` package
  passed both complete handshakes around activation into a disposable home.
- Prerelease bootstrap selection after `add2c2e`, 2026-09-04: the static guard
  failed on GitHub's stable-only latest alias before repair; release-selection
  test, README PowerShell parse, and `git diff --check` passed after repair.
- Executable README bootstrap follow-up after `d40228c`, 2026-09-04: the new
  fixture failed first with `GitHub returned an invalid draft flag.` against
  the nested REST-array wrapper; after its removal the release-selection test
  and focused README public-install parse passed. `git diff --check` passed.
- Stream-retention commit `0c9328a`, 2026-09-04: the new direct-host and
  response/recovery tests failed before capture fields existed. Six new tests
  plus the extended timeout case passed; full server passed 1,360/1,360 with
  two known analyzer warnings; registration handshake passed; a fresh clean
  `osx-arm64` package passed staged activation plus the updated 30-check direct
  product proof, and a separate clean isolated source install passed all 32
  checks including uninstall. Both throwaway roots were removed.
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
- `.agents/review/findings/i30-1.md` (FIXED on canonical; issue open)
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
