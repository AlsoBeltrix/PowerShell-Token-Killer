# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

**rtk 1.0 contract repair and rc.4, 2026-10-23 (owner go):** the first
`0.3.0-rc.4` release run `40047540862` (branch `release/0.3.0-rc.4` at
`deee603`) failed on all five legs: rtk 1.0.1 rejects
`hook check --agent ptk` with exit 2, which also silently disabled routing for
any user on rtk 1.x and broke the installer probe. PTK now uses the documented
`rtk rewrite` contract and accepts exit 0/3 (decision in `.agents/decisions.md`).
Verified on `NETWATCH-01` against real rtk 1.0.1 and 0.48.0 (both answer 3 for
`git status --short`; a live server call reported `effective=rtk` on both). CI
now probes the real rtk. CI `40049412169` at `061a3b1` passed all six jobs.
Second rc.4 release run `40050237185` (branch moved to `061a3b1`): linux-x64,
linux-arm64, and osx-arm64 (signing and notarization) passed; both Windows
legs failed in Azure Trusted Signing with `403 (Forbidden)` from
`CertificateProfileRestClient.SignAsync` after authentication succeeded
(account `roethlar-app-signing`, profile `public-trust`, app
`github-signing`). No draft was created. Owner-side Azure check needed (role
assignment, profile/identity-validation state, subscription); then rerun the
failed legs with `gh run rerun 40050237185 --failed`.

**Installer prerequisite consent, 2026-10-23 (owner go):** `scripts/install.ps1`
now asks before installing a missing rtk (previously fetched silently) and,
for source installs, checks the NativeAOT toolchain (refuses with the install
command; it needs elevation) and offers a per-user .NET SDK install into
`~/.dotnet`. `-InstallPrerequisites` agrees in advance; a non-interactive
session without it refuses. An existing `.ptk-installed-rtk` marker counts as
prior agreement. Pester guards in the `prerequisite consent` context. Not yet
exercised: a real accepted rtk download or a real `dotnet-install` run (the
`NETWATCH-01` Windows host has no SDK or VS C++ tools; the module suite's
`redirect hook and installer` block fails setup there, same as baseline).

**Release publication authorized, 2026-09-08:** the owner directed completion
of the new GitHub and package-manager releases. Execution, version, package
choices, external account prerequisites, and evidence are canonical in
`.agents/plans/release-0.3.0-rc.3.md`. This supersedes earlier no-publication
scope for the new release; rc.2 remains frozen and existing sessions must
survive. Implementation is underway; no new release is published yet.
The native CLI is committed at `e823781` with all six CI jobs passing in
`34275489232`. That exact source is building as rc.3 in release run
`34276660442`; the candidate record is `.agents/release-candidate-0.3.0-rc.3.md`.
Draft `385067589` exists with twelve assets, and downloaded-release
verification `34280500750` passed its inventory plus the native
signature/install/CLI/runtime/SIEM/uninstall proof on all five RIDs. CI at
current head is red; see Blockers. Nothing is published.

**Native redirect hook, 2026-09-08:** implemented and deployed; all six CI
jobs passed at `2247aae`. The owner's subsequent go enabled and trusted the
native hook. A fresh Codex session in `roon-controller` proved normal shell
denial and PTK_DIRECT execution; PTK command execution and a client-issued
health read passed. All 29 existing PTK processes and this connection's warm
state survived. Scope, timing, and verification limits are canonical in
`.agents/plans/native-redirect-hook.md`; local activation and backup are in
`.agents/machines.md`. The frozen rc.2 candidate is unchanged.

**Buffered audit startup repair, 2026-09-08:** owner-approved implementation
and verification are complete. The owner then required a solution without
stopping existing sessions: new Codex connections now launch a separately
packaged repaired build, which initialized in 4.925 seconds against the
existing history from `roon-controller`. All existing PTK PIDs and this
connection's warm state survived the configuration switch. The original
installed directory remains unchanged. Scope, exact builds, validation,
and limitations are canonical in `.agents/plans/buffered-audit-startup.md`;
host paths and configuration backup are in `.agents/machines.md`.
The earlier PowerShell child-launch failures and subsequent successful
verification with the normal PowerShell 7.6.5 installation are
recorded in `.agents/machines.md`. This repair does not alter the installed
PTK build or the frozen rc.2 candidate.

**Handoff checkpoint as of `2fa49d9`:** the checksum-only correction is complete,
verified, committed, and pushed. No candidate build or local proof remains in
flight; the working tree was clean before this bookkeeping snapshot. Resume
from the candidate evidence linked below, without rebuilding rc.2 or repeating
completed Mac/download checks. The draft remains unpublished; this is not a
public-release or live-install go.

**Unpublished rc.2 checksum correction is complete and verified.**
The owner explicitly approved correcting only `SHA256SUMS` on the existing
draft. Version `0.3.0-rc.2`, draft `383097364`, source `6ab7040`, and all
eleven archive asset IDs/hashes remain unchanged. Fresh downloads passed GNU
and Perl checksum tools, all twelve GitHub digests, eleven manifest hashes,
and ten exact clean build identities. `rr-8` is closed, as are `rr-5`–`rr-7`.
The original checksum file and metadata are preserved locally.

The candidate's six-job CI `33935459185` and six-job release run `33935468032`
passed. The future formatter repair at `ac90719` also passed all six CI jobs
in `33937538564`. Downloaded Mac signatures/notarization, isolated installation,
32 runtime checks, SIEM workflows, and uninstall remain valid because the
archives are byte-for-byte unchanged.

The one-off owner exception and exact correction history are canonical in
`.agents/release-candidate-0.3.0-rc.2.md` and its linked assets JSON. The
standing immutability rule remains unchanged. Nothing was published, no tag ref
was created, and this task did not replace or restart the live installation.
The separate newer `eff24a5` harness fix is still outside the frozen candidate;
do not silently downgrade the installed development copy. Remaining native
download/public-launch checks and owner policy gates are recorded in the
candidate record; this correction is not a public-release signoff.

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
and their policies (`mhi-13`). Codex redirect enforcement now has an
owner-approved installer repair (`mhi-14`): the user-level hook matches unified
`exec_command` as `Bash`, preserves Headroom and other hooks, and leaves trust
to Codex's next-session review. The full PowerShell suite passes 116 tests with
3 platform skips; owner install plus fresh-session live deny/reissue remains
pending.

The policy-independent public operations baseline is committed at `c55169f`:
privacy, known limitations, contribution and community templates, release
notes, and immutable-version withdrawal recovery. The requirement-by-
requirement activation audit is current in `.agents/plans/release-readiness.md`.
That plan now also defines the exact uploaded/downloaded candidate evidence set
for the five-RID final gate.
The factual next-release content draft is `.agents/release-notes-next.md`; it is
not publishable until the owner policies and exact candidate evidence land.

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

- Startup repair: new Codex connections use the separate repaired runtime.
  Leave existing sessions running and preserve that runtime directory. A
  previously failed connection needs a fresh connection to read the new
  registration; do not stop other sessions to make this happen. See
  `.agents/plans/buffered-audit-startup.md`.
- Resolve the current-head CI failure below, then continue rc.3 through
  `.agents/plans/release-0.3.0-rc.3.md`: final factual release notes,
  publication, public bootstrap proof, then package-channel publication and
  actual per-channel availability. The five-RID downloaded-product gate is
  already satisfied. Keep rc.2 frozen and unpublished; live installation
  still replaces nothing without its own go. The publication authorization
  above is a prior-session record from 2026-09-08, not a live instruction:
  reconfirm it with the owner before any outward action.
- Close canonical GitHub #30 only under its separate outward-action gate.
  Unrelated Sentinel and package-manager feature decisions remain
  outside the release candidate.
- Before a future public launch, settle security reporting and support as
  separate owner decisions; they do not block this build/test pass.
  `v0.3.0-rc.1` cannot be reused because both a published
  prerelease and a stale draft already use that tag.

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

- **Current-head CI red, 2026-09-08 — diagnosed and fixed 2026-10-23:** run
  `34281077684` failed only `test (windows-latest)`, in
  `AuditDestinationS3Tests.Coordinator_applies_live_changes_and_keeps_partial_failure_independent`
  at line 569. Cause: the test inherited the production 5 s doubling retry
  backoff; on a slow runner the failing secondary reached a 20 s retry before
  it was switched to Delivered, outlasting the 15 s wait. Reproduced on
  `NETWATCH-01` by a 16 s pause before the switch (fails in 34 s, CI took
  32 s); the coordinator now accepts optional internal retry delays the test
  shortens, and the same pause then passes. Product backoff is unchanged.
  CI run `40046250981` at `4840f11`: all six jobs green after one rerun.
  The first attempt failed `siem (windows-latest)` once in
  `OperatorApiTests.The_chains_list_is_bounded_and_newest_first` (15 s
  `HttpClient` timeout on the 200-boot ingest at line 304; ~0.7 s locally on
  `NETWATCH-01`, 3/3); the rerun passed. First recorded occurrence, so
  treat it as a runner stall and watch for recurrence, not as fixed.
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
- `.agents/release-notes-next.md` (working draft; not publishable)
- `.agents/review/index.md`
- `.agents/machines.md`
- `.agents/decisions.md` (under owner hold for the named stale entries)
- `.agents/repo-guidance.md`
- `AGENTS.md`

Older plans remain prior art or history, not active implementation authority.
Rotated state history lives in `docs/history/state-archive.md`.

## Unrecorded Repo Memory

- None known.
