# Plan: release 0.3.0-rc.6 — `install.ps1 -FromRelease` with the rtk fix

**Status:** DRAFT 2026-10-26. rr-10 fixed on `master` (verdict pending);
rr-9 and rr-11 open as finding records. Both codex dispatches failed before
any review (see the dispatch record below): the owner must run `codex login`,
then both dispatches are repeated unchanged. The rr-9 route proof needs an
owner go (auto mode refuses to execute the external installer script). Owner
rulings D1–D3 pending. Steps 1–4 precede tagging; steps 5–6 need the owner's
publish go, since publishing is an owner action.

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
- **Verified clean (no finding):** the csproj revision guard yields `unknown`
  for empty output and for a missing git (MSBuild probe, 2026-10-26); nothing
  pins the dev version's hash length; the winget and Homebrew manifest
  changes carry tests and passed validation run `40066034251`; the installer's
  rtk marker, uninstall-by-hash, PATH advice and checksum selection carry
  Pester coverage that passes (130/130 on this host).

## Steps

1. **Review.** Codex Change review over `74aaeaf..eb25ccb0` (unprimed) and
   codex openreview of this plan. Intake per the codereview playbook: each
   admitted finding gets `.agents/review/findings/<id>.md` and a row in
   `.agents/review/index.md`; one finding per commit; codex verdict per fix.
2. **rr-10.** Implement atomic control publication with the regression guard;
   full server suite; codex verdict.
3. **rr-9.** Apply D2; exercise the install.sh route locally; add the CI leg.
4. **rr-11.** Record the reopened diagnosis; apply D3.
5. **Release.** From the reviewed head: exact-source CI green; branch
   `release/0.3.0-rc.6`; dispatch `Release` (`version=0.3.0-rc.6`), expect a
   prerelease draft with twelve assets; dispatch `Verify downloaded release`
   (`version`, `source`); owner publishes; dispatch `Validate published
   package manifests`, then `Publish package channels`; move the open winget
   PR (microsoft/winget-pkgs#460717) to rc.6 or open its successor; write
   `.agents/release-candidate-0.3.0-rc.6.md`; prove `-FromRelease` with no
   rtk on `PATH` on one Linux or macOS host and on `NETWATCH-01`.
6. **Installed text.** Re-run the dev install (or `ptk init`) on magneto and
   on this host so the installed hook and nudge carry the new wording.

## Verification

- The full battery in `.agents/repo-guidance.md` §Verification on the
  reviewed head before tagging. On this Mac the Pester hook publish needs the
  pack override recorded in `.agents/machines.md`.
- Release gates are the workflows in step 5; the release workflow runs
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

## Pending owner decisions

- **D1 — version.** `0.3.0-rc.6` (assumed here) or `0.3.0` final.
- **D2 — rtk install.sh source.** Pin to a release tag with a recorded hash,
  or keep following `master`.
- **D3 — rr-11.** Whether the macOS timing recurrence gates this release.

## Non-goals

- Re-reviewing code older than rc.5.
- Widening the slow-seal bound (forbidden by the closed plan).
- Any change to what the release workflow builds or signs.
