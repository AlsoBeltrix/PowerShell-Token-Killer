# Plan: release 0.3.0-rc.6 — `install.ps1 -FromRelease` with the rtk fix

**Status:** REVIEWED 2026-10-27. Codex reviewed the delta and this plan
(dispatch records below). rr-10 closed (codex accepted the repair); rr-12
and rr-13 (from the codex change review) fixed on `master`, verdicts
pending; rr-9 and rr-11 open. The rr-9 route proof still needs an owner go
(auto mode refuses to execute the external installer script). Owner rulings
D1–D4 pending, each with the reviewer's recommendation. Steps 1–5 precede
tagging; step 6 needs the owner's publish go, since publishing is an owner
action; step 7 is post-release maintenance.

## Goal

Publish the next release so that `scripts/install.ps1 -FromRelease` delivers
what shipped on `master` since rc.5: the installer places rtk where `PATH`
reaches it and before staging (rc.5's known issue), the hook and nudge text
lead with native PowerShell, checkout builds stamp a deterministic
seven-character revision, the two cryptography packages are lifted past their
advisories, Grpc.Tools 2.85.0 gives macOS arm64 a native protoc, and the
package-channel fixes. Resolve the review findings below before tagging.

## Base facts

- Last published release: `v0.3.0-rc.5` at `74aaeaf` (2026-10-24, prerelease;
  record `.agents/release-candidate-0.3.0-rc.5.md`).
- Delta to `master` `eb25ccb0`: 20 commits, 26 files, +724/−175.
- CI at `eb25ccb0`: all six jobs green (run `40379342927`), including the
  Linux and Windows protoc legs on Grpc.Tools 2.85.0. The two preceding
  `master` runs each failed one timing-sensitive server test once (rr-10,
  rr-11).
- Review scope: the delta plus the `-FromRelease` path (installer, release
  and verify workflows, package channels). Reviewer: codex, cached standard
  pair, for an unprimed defect pass over `74aaeaf..eb25ccb0` and an approach
  review of this plan. The full product is not re-reviewed; everything older
  than rc.5 carries rc.5's verification.

## Findings from the caller's review (candidates; intake per codereview)

- **rr-9 — rtk's installer script is unpinned.** `Install-PtkRtkWithScript`
  fetches `https://raw.githubusercontent.com/rtk-ai/rtk/refs/heads/master/install.sh`
  and runs it with `sh`. Verified 2026-10-26: the URL answers 200, the script
  honors `RTK_INSTALL_DIR` and defaults to `~/.local/bin` (matching ours) and
  verifies its own download against `checksums.txt`; but rtk's default
  branch is `develop`, so `master` may lag or vanish, and PTK runs whatever
  that mutable ref serves. The rc.5 record states the Linux/macOS route has
  never been exercised live. Options for D2: pin to a release tag with a
  recorded SHA-256, or keep following `master`. In either case, exercise the
  route once locally (throwaway `HOME`, `PATH` without rtk,
  `-FromRelease -Version 0.3.0-rc.5 -InstallPrerequisites`: expect the PATH
  advice without `~/.local/bin` on `PATH`, a full install with it) and add the
  same to CI on ubuntu and macos.
- **rr-10 — zero-length audit quota control under concurrent start.**
  `FileAuditJournalSinkTests.Concurrent_factories_converge_on_one_host_identity_without_temporary_files`
  failed on ubuntu (run `40377656597`, head `812bc46c`) with
  `IOException: The audit spool quota control is invalid` thrown from
  `AuditSpoolQuotaLease.VerifyRetainedControl` during
  `CreateControlAndAcquire`. Reading: `EnsureControlFile` creates the control
  with `CreateNew`/`FileShare.None` and writes the one-byte marker only
  afterwards; on Unix .NET emulates `FileShare` with `flock` taken after
  `open(2)`, so a concurrent acquirer can take the exclusive lock between the
  creator's open and its lock, verify a zero-length control, fail closed, and
  leave a zero-byte control behind that `EnsureControlFile` never repairs
  (it returns when the entry exists). Product impact: two PTK servers
  starting at once on one audit root can fail startup, and the root stays
  unusable until the control is removed by hand. Proposed fix: publish the
  control atomically (marker written and flushed to an exclusive temp file,
  then renamed into place without overwrite), so no reader ever observes a
  zero-length control; guard: a test that races acquisition against a
  zero-length control and against concurrent creators. Local flake-rate
  measurement on macOS: in progress at the time of writing.
- **rr-11 — macOS slow-seal elapsed recurrence.**
  `InvokeToolTests.Slow_output_store_seal_is_bounded_and_never_reruns` failed
  on macos (run `40151885345`, head `1f36356d`): elapsed 3.18 s against the
  3 s bound with a 2 s seal limit. The closed plan
  `.agents/plans/ci-slow-seal-elapsed-headroom.md` rules that a recurrence
  reopens diagnosis and forbids widening the bound again. Action: reopen the
  diagnosis in a finding and collect elapsed samples from macOS runs. Not a
  release blocker by itself; D3 decides whether it gates.
- **rr-12 — payload-resident rtk accepted, then deleted by the upgrade**
  (codex change review, 2026-10-27, admitted). Installers up to rc.5 put
  rtk in `~/.ptk/bin`; with that directory on `PATH`, or `PTK_RTK_PATH`
  pointing into it, the rc.6 installer accepted the copy, activation
  deleted it with the old payload, the smoke test's server exited 78, and
  the install rolled back on every attempt. Fixed in `23e11dbc`:
  `.agents/review/findings/rr-12.md`.
- **rr-13 — a user's rtk at the install target overwritten and adopted**
  (codex change review, 2026-10-27, admitted). With no rtk on `PATH`, the
  fetch overwrote `$RTK_INSTALL_DIR/rtk[.exe]` and the marker claimed it,
  so uninstall later removed the user's own copy. Fixed in `3cfc06a1`:
  `.agents/review/findings/rr-13.md`.
- Codex's remaining candidate (the unpinned `install.sh`) duplicates rr-9
  and is folded into it; its supply-chain framing is carried into D2.
- **Verified clean (no finding):** the csproj revision guard yields `unknown`
  for empty output and for a missing git (MSBuild probe, 2026-10-26); nothing
  pins the dev version's hash length; the winget and Homebrew manifest
  changes carry tests and passed validation run `40066034251`; the installer's
  rtk marker, uninstall-by-hash, PATH advice and checksum selection carry
  Pester coverage that passes (130/130 on this host).

## Steps

1. **Review.** Done 2026-10-27: codex Change review over
   `74aaeaf..eb25ccb0` (three candidates: rr-12, rr-13 and a duplicate of
   rr-9) and codex openreview of this plan (endorsed with the corrections
   now folded into steps 6–7 and D1–D4). Intake per the codereview
   playbook; one finding per commit; codex verdict per fix.
2. **rr-10.** `0d498d7d` (atomic publish) and `dc85fcf5` (exclusive
   handle held across the POSIX publish, after the codex reopen; accepted),
   then reopened by CI run `40386199045` (a creator's temporary name is an
   unknown spool entry) and reworked in `f29784c5`: in-place creation
   again, an empty control means creation in progress; codex reopened the
   rework (Windows unprotected-create instant; write before the identity
   proof), repaired. Verdict on the repair pending.
3. **rr-12 and rr-13.** Fixed (`23e11dbc`, `3cfc06a1`); codex verdicts
   pending, one dispatch at a time.
4. **rr-9.** Apply D2; exercise the install.sh route locally in a throwaway
   home with no rtk on `PATH` (owner go needed to execute the external
   script under auto mode); add the same to CI on ubuntu and macos.
5. **rr-11.** Record the reopened diagnosis; apply D3.
6. **Release (owner publish go).** From the reviewed head, in this order:
   - exact-source CI green on `master`, and the full local battery
     (§Verification) on the same head;
   - final release notes written: the draft is created with placeholder
     notes only (`scripts/assemble-draft-release.sh` passes a generic
     `--notes`), and rc.5's notes were kept outside the repository;
   - branch `release/<version>`; dispatch `Release` (`version`), expect a
     prerelease draft with twelve assets; dispatch `Verify downloaded
     release` (`version`, `source`). That proof supplies rtk itself
     (`verify-release.yml` downloads rtk and passes `-RtkPath`), so it does
     not exercise the installer's rtk bootstrap;
   - write `.agents/release-candidate-<version>.md` now, with the source
     SHA and the draft evidence; finalize it after publication;
   - the owner publishes the draft with the final notes. Publication starts
     `Publish package channels` by itself (`publish-packages.yml` runs on
     `release: published` and calls the manifest validation first); a
     manual dispatch of either workflow is only for a re-run after a fix,
     as rc.5 needed. D4 rules on the ordering against the bootstrap proof;
   - immediately after publication, prove the public bootstrap:
     `scripts/install.ps1 -FromRelease -Version <version>
     -InstallPrerequisites` in an isolated home with no rtk on `PATH`, on
     one Linux or macOS host and on `NETWATCH-01`. On failure: stop the
     channels (close the winget PR; revert the tap, bucket and AUR commits)
     and mark the release superseded in its notes;
   - winget: move microsoft/winget-pkgs#460717 to the new version or open
     its successor, and make sure the rc.5 PR cannot merge afterwards;
   - record channel results in the candidate record; verify the tag points
     at the reviewed source; delete `release/<version>` once the tag exists
     (branch closeout rule in AGENTS.md).
7. **Installed text (post-release maintenance, not a gate).** Re-run the
   dev install or `ptk init` on magneto and on this host so the installed
   hook and nudge carry the new wording; the installed hook on this host
   still emits the old `bash -lc '...'` advice (seen in the codex runs'
   stderr, 2026-10-27).

## Verification

- The full battery in `.agents/repo-guidance.md` §Verification on the
  reviewed head before tagging. On this Mac the Pester hook publish needs the
  pack override recorded in `.agents/machines.md`.
- Release gates are the workflows in step 6; the release workflow runs
  `direct-product-proof.ps1` per RID itself.

## Review dispatch record (2026-10-26)

- Harness: codex-cli 0.164.0, CLI transport, `codex exec` with the prompt on
  stdin, `-s read-only`, MCP servers `ptk`, `node_repl` and `computer-use`
  disabled for the run (the cached `headroom` entry no longer exists in the
  owner's config). Pair: the cached standard tier, `-m gpt-5.6-sol`,
  `model_reasoning_effort="high"`; the owner's config default (gpt-6-sol,
  xhigh) was not adopted, per the codereview playbook.
- Pins: base `74aaeafac383d966d436d22ceb6e9c11f6c16529`, head
  `eb25ccb0319d597c982e8c7c1349cd707aa65717`.
- Dispatch 1, Change review (unprimed defect pass over the delta): exit 1,
  no review. Dispatch 2, openreview of this plan: exit 1, no review. Both
  stderr logs show `codex_login` `401 Unauthorized`: "your refresh token was
  revoked. Please log out and sign in again." No model identity was resolved
  and no verdict exists. Next action: owner runs `codex login`; repeat both
  dispatches unchanged. Findings rr-9 to rr-11 link here for provenance.

## Review dispatch record (2026-10-27)

Same harness and pair as above (codex-cli 0.164.0, `codex exec`, prompt on
stdin, `-s read-only`, the three MCP servers disabled, `-m gpt-5.6-sol`,
`model_reasoning_effort="high"`); the JSONL envelope emits no model id, so
the dispatched pair is what is recorded. Every run exited 0 after the
owner's `codex login`. Codex's shell commands ran under the installed PTK
hook's `PTK_DIRECT` escape (ptk disabled for the run), read-only git only.

- Change review, pins `74aaeaf..eb25ccb0`, prompt unchanged from
  2026-10-26: three candidates — a legacy rtk under `~/.ptk/bin` deleted by
  the upgrade (admitted as rr-12), the unpinned `install.sh` (duplicate of
  rr-9), and the Windows overwrite of a user-owned `rtk.exe` (admitted as
  rr-13, which also covers rtk's own overwrite on Unix). No defect found in
  the workflow or package-channel changes.
- Openreview of this plan, same pins, prompt changed only to say the plan is
  committed at `fc240a2e`: endorsed as rc.6 after tightening the rtk supply
  chain and correcting the publication sequence. Adopted: publication
  auto-triggers the package channels (step 6), final release notes, the
  candidate record before publication, the bootstrap proof after
  publication with a stop-channel action, step 7 out of the critical path,
  release-branch deletion. Recommendations carried into D1 (rc.6), D2 (pin
  and hash; drop "keep following `master`"), D3 (rr-11 gates until
  diagnosed or classified test-only on repeated macOS evidence) and D4.
- rr-10 verdict, pins `eb25ccb0..0d498d7d`: reopened (POSIX two-link
  interval); repair verdict, pins `fc240a2e..dc85fcf5`: accepted; CI then
  reopened it on evidence neither verdict saw (`rr-10.md`); rework
  verdict, pins `1302b4be..f29784c5`: reopened (Windows create instant;
  write before identity proof); the repair's verdict is pending (pins in
  `rr-10.md`).
- rr-12 verdict, pins `dc85fcf5..23e11dbc`: reopened (configured-path
  precedence; link following); repair verdict, pins `e44b3df6..b2db2dab`:
  reopened (filesystem case sensitivity, whitespace-only value, resolver
  exhaustion); third delta, pins `b2db2dab..1302b4be`: reopened (the case
  probe proved existence, not identity; an unguarded loop in the
  stale-copy scan); the fourth delta decides containment by identity and
  awaits its verdict (pins in `rr-12.md`). rr-13 verdict, pins `22862834..3cfc06a1`:
  pending, dispatched after rr-12 resolves.

## Pending owner decisions

- **D1 — version.** `0.3.0-rc.6` (assumed here) or `0.3.0` final. Reviewer
  recommendation: rc.6, because the Unix rtk bootstrap has never been
  exercised live and three installer findings landed in this delta.
- **D2 — rtk install.sh source.** Pin to a release tag with a recorded
  SHA-256 (reviewer recommendation; the earlier "keep following `master`"
  option is withdrawn as mutable remote-code execution). If the 2026-10-24
  decision that rtk installs itself on Unix were reopened, downloading and
  checksum-verifying the platform archive directly, as the pre-rc.6
  installer did on every platform, would be smaller and safer (reviewer).
  Either way the route gets a live proof and a CI leg (step 4).
- **D3 — rr-11.** Whether the macOS timing recurrence gates this release.
  Reviewer recommendation: gate until it is diagnosed or classified as
  test-only on repeated macOS evidence; one later green run is not enough,
  and the bound stays.
- **D4 — channel publication versus the bootstrap proof.** Publishing the
  release starts `Publish package channels` automatically, before the
  public `-FromRelease` bootstrap can be proved (release assets are public
  only after publication). Options: (a) accept that order, run the
  bootstrap proof at once and withdraw the channels on failure (step 6), or
  (b) change `publish-packages.yml` to manual dispatch only so the proof
  gates the channels (a workflow change). Caller's recommendation: (a) for
  rc.6, since the channels deliver the packaged archives rather than the
  installer and AUR already declares rtk as a dependency, and settle (b)
  before the final 0.3.0.

## Non-goals

- Re-reviewing code older than rc.5.
- Widening the slow-seal bound (forbidden by the closed plan).
- Any change to what the release workflow builds or signs.
- Changing the package-channel trigger before D4 is ruled.
