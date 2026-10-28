# Release candidate 0.3.0-rc.6

## Authority and identity

The owner's "go" on 2026-10-27 after ruling the review decisions one at a
time (`.agents/decisions.md`, 2026-10-27 amendment): the SIEM-integration
removal first, then the next release carrying it with the installer work of
the reviewed rc.6 plan; the version delegated and named `0.3.0-rc.6`.
Publishing stays an owner action.

- Version: `0.3.0-rc.6`; tag `v0.3.0-rc.6` (carried by the draft).
- Product source: `4c9b2198f56a726de456cf5b908cf7ae8279124a`.
- Exact-source CI: `40573611949`, all three jobs passed (Pester 136/134/134
  on windows/ubuntu/macos, server tests 1,288 on each, handshake passed on
  each; the former `siem` jobs no longer exist).
- Native release workflow: `40575011312`, all five native legs and the draft
  assembly passed (dispatched on `release/0.3.0-rc.6`, `version=0.3.0-rc.6`).
- Draft release: `v0.3.0-rc.6`, draft and prerelease, target `4c9b2198`,
  seven assets (`ptk-0.3.0-rc.6-{win-x64,win-arm64}.zip`,
  `ptk-0.3.0-rc.6-{linux-x64,linux-arm64,osx-arm64}.tar.gz`,
  `ptk-installer.zip`, `SHA256SUMS`); the receiver archives are gone.
- Build branch on canonical `origin`: `release/0.3.0-rc.6`, pointing at the source above. <!-- lint: allow (Git branch, not a repository path) -->
  Deleted on the owner's word 2026-10-27, with `release/0.3.0-rc.3`
  through `rc.5`, from GitHub and the Gitea mirror: every head was an
  ancestor of `master`, the rc.4–rc.6 heads equal their `v*` tags, and
  rc.3 (never published) keeps its source as its draft's target SHA
  `e823781b`.
- Release notes: `.agents/release-notes-0.3.0-rc.6.md`.

## Current evidence

- Downloaded-release verification `40575768473` (workflow ref `master`,
  `version=0.3.0-rc.6`, the source SHA above), run against the draft: all
  six jobs passed on the first attempt. Inventory: seven assets, five build
  identities, `SHA256SUMS` and the GitHub digests agree.
- Windows x64 and arm64: 551 binaries each, 541 valid carried (Microsoft)
  signatures, 10 unsigned PTK binaries; staged install, four handshakes and
  the product proof passed on each.
- macOS: 20 Mach-O files with valid Developer ID signatures and online
  notarization; staged install, handshakes and the product proof passed.
- Linux x64 and arm64: staged install, handshakes, the product proof and
  uninstall passed.
- The draft carries the final notes (`.agents/release-notes-0.3.0-rc.6.md`),
  title `ptk 0.3.0-rc.6`, prerelease, still a draft.
- Live bootstrap route proof, owner-run on `nagatha` (macOS arm64,
  2026-10-27 16:43 EDT) with this source's installer and the published rc.5
  payload, in a throwaway home with an empty `PATH`: run A (rtk
  unreachable) downloaded rtk's release archive, placed `~/.local/bin/rtk`,
  recorded the marker with its hash, and stopped before staging with the
  PATH advice (exit 1); run B (that directory on `PATH`) found it, installed
  the payload, and passed the staged and installed handshakes ("Installed
  ptk 0.3.0-rc.5"). The same script with `0.3.0-rc.6` proves the public
  bootstrap of this release once its assets are public (D4, after
  publication).

## Post-publication (owner publishes, channels publish on release)

- Public bootstrap proof (`scripts/install.ps1 -FromRelease -Version
  0.3.0-rc.6 -InstallPrerequisites`, isolated home, no rtk on `PATH`):
  passed, owner-run on `nagatha` 2026-10-27 20:19 EDT against the
  published release. Run A downloaded rtk, placed `~/.local/bin/rtk` with
  its marker and hash, and stopped with the PATH advice (exit 1); run B
  found it on `PATH`, installed the rc.6 payload, and passed the staged and
  installed handshakes ("Installed ptk 0.3.0-rc.6", exit 0).
- Package channels (`Publish package channels` on `release: published`):
  the owner published the draft 2026-10-27 20:42 UTC. The automatic run
  `40580462561` failed in the Homebrew validation on all three runners and
  published nothing: `brew audit` refused the throwaway tap, because
  Homebrew 7.0.11 (released 2026-10-26, Homebrew/brew#24352) limits
  implicit tap trust to install commands; the local 7.0.10 audit still
  passes without trust, so the proof is the CI run. Fixed by `brew trust
  ptk-validation/local` after `tap-new` in `validate-packages.yml`, then
  re-dispatched from master with `version=0.3.0-rc.6`. CI `40581777053` at
  the fix `18ebd87c` passed on all three platforms. The owner-dispatched
  run `40607922559` passed all eight validation jobs (Homebrew on
  ubuntu-latest, macos-latest and ubuntu-24.04-arm with the trust fix,
  Windows ×2, AUR, manifests), then published:
  - **Homebrew** `roethlar/homebrew-tap`: "ptk: publish 0.3.0-rc.6",
    2026-10-28 00:21 UTC.
  - **Scoop** `roethlar/scoop-bucket`: "ptk: publish 0.3.0-rc.6",
    2026-10-28 00:21 UTC.
  - **AUR** `ptk-bin`: the git `.SRCINFO` carries `pkgver = 0.3.0rc6`,
    `pkgrel = 1`; the AUR RPC still reported `0.3.0rc5-1` minutes later,
    the same indexing lag rc.5 showed.
  - **winget** stopped as designed (no `WINGET_TOKEN`), as for rc.5.
- winget: move microsoft/winget-pkgs#460717 to rc.6 or open its successor:
  the owner chose 2026-10-27 to swap in an rc.6 request and close #460717
  with a link to it. Auto mode refuses the submission, so the owner runs
  `scratchpad/winget-rc6/swap.sh` of session `315c7d81` (the publish
  script's winget path with the validated manifests, hashes checked against
  rc.6's `SHA256SUMS`). Done 2026-10-27: the owner's run opened
  microsoft/winget-pkgs#463601 ("New package: Roethlar.PowerShellTokenKiller
  version 0.3.0-rc.6"); its follow-up lookup then found nothing, so I
  closed #460717 with a link to #463601. Cause, a real defect: `gh pr list
  --head` matches only the bare branch name, and the publish script and the
  `publish-packages.yml` winget job both passed `roethlar:ptk-<version>`, so
  their existing-request check never matched. Fixed with the bare branch
  and an owner filter, guarded in `scripts/test-publish-package-manifests.py`
  (red on the old code, green on the fix; the fixed lookup finds #463601
  live). #463601 now waits on a moderator, like #460717 did. #460717 is the rc.5 new-package request: its
  checks 01–07 and 09–10 pass, installation validation (08) is neutral,
  and it carries `Validation-No-Executables` ("No application executables
  were discovered after installation; review is required"), so it waits on
  a moderator. The validated rc.6 manifests are the `package-manifests`
  artifact of run `40607922559`.
