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
- Not yet proved: the public `-FromRelease` bootstrap with no rtk on `PATH`,
  which needs the assets to be public (D4, after publication; the scripted
  proof is `scratchpad/rr9-route/run.sh` of session `315c7d81`, now set to
  rc.6, for the owner to run since auto mode refuses it).

## Post-publication (owner publishes, channels publish on release)

- Public bootstrap proof (`scripts/install.ps1 -FromRelease -Version
  0.3.0-rc.6 -InstallPrerequisites`, isolated home, no rtk on `PATH`):
  _pending_.
- Package channels (`Publish package channels` on `release: published`):
  _pending_.
- winget: move microsoft/winget-pkgs#460717 to rc.6 or open its successor:
  _pending_.
