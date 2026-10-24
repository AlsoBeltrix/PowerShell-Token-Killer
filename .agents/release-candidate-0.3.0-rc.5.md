# Release candidate 0.3.0-rc.5

## Authority and identity

The owner directed "fix everything properly" on 2026-10-24 after rc.4's
public bootstrap was found broken, following the rc.4 publication go. This
candidate exists to ship that installer fix (`f24c3ce`) and the package
validation fixes (`06f9074`).

- Version: `0.3.0-rc.5`; tag `v0.3.0-rc.5` (carried by the draft).
- Product source: `74aaeafac383d966d436d22ceb6e9c11f6c16529`.
- Exact-source CI: `40057807529`, all six jobs passed.
- Native release workflow: `40058634879`, all five native legs and draft
  assembly passed.
- Draft release: twelve assets, created as a prerelease draft by the
  workflow itself (first confirmation of `ac5c6a6`).
- Build branch on canonical `origin`: `release/0.3.0-rc.5`, pointing at the source above. <!-- lint: allow (Git branch, not a repository path) -->

## Current evidence

- `ptk-installer.zip` lists `install.ps1`, `ptk_install_transaction.psm1`,
  `ptk_build_provenance.psm1`, `test-handshake.ps1`; its hash matches
  `SHA256SUMS`.
- Downloaded-release verification `40059087732` (workflow ref `master`)
  passed all six jobs on the first attempt.
- Windows x64 and arm64: 891 binaries each, 875 valid carried (Microsoft)
  signatures, 16 unsigned PTK binaries; Defender scan completed and left the
  payload intact on both.
- macOS: 36 Mach-O files with valid Developer ID signatures and online
  notarization.
- Release notes are prepared outside the repository (operator scratchpad);
  they are attached at publication.

## Publication (2026-10-24)

- Published by the owner (the agent's call was refused by the auto-mode
  classifier) at 2026-10-24T04:19:13Z as a prerelease; tag → `74aaeaf`.
- Public bootstrap proof passed: the README snippet, run unchanged in an
  isolated home on `NETWATCH-01`, installed `0.3.0-rc.5+74aaeaf`; the
  installed `scripts/` carries `test-handshake.ps1`. Recorded in the notes.

## Package channels

The release-event run `40061870699` (workflow at the rc.5 tag) failed
validation, so nothing was published. Fixes on `master`, each found by a
validation-only run against the published rc.5:

- `b19fd29`: Homebrew `brew audit --strict` rejected the redundant formula
  `version`; winget runner images need the latest winget for 1.12.0 schema
  headers (which winget-pkgs uses).
- `bf1ad83`: install that winget for the current user (`-AllUsers` fails on
  the Server image, 0x80073CF1).
- `a2a5094`: **real defect** — a winget-installed `ptk` could not start
  through the `WinGet\Links` symlink (the .NET apphost looks for
  `PtkMcpServer.dll` beside the link). The manifest now sets
  `ArchiveBinariesDependOnPath`, which puts `bin\` on PATH (winget-cli
  `PortableInstaller.cpp`: `symlinkTargetPath.parent_path()`).
- `b707272`: uninstall the local-manifest validation install by manifest.

Validation-only run `40063229735` at `b707272` passed all seven jobs
(Homebrew ×3, Windows Scoop+winget ×2, AUR, manifests).

Owner-dispatched publication run `40064123377` (from `master`): all seven
validation jobs passed, then:

- **Homebrew** `roethlar/homebrew-tap` `Formula/ptk.rb` → rc.5 assets.
- **Scoop** `roethlar/scoop-bucket` `bucket/ptk.json` → version `0.3.0-rc.5`.
- **AUR** `ptk-bin` created (`c36f8e6`, `pkgver = 0.3.0rc5`, depends on
  `rtk`); the AUR RPC had not yet indexed the new package minutes later,
  though the git repo and package page exist.
- **winget** stopped as designed (no `WINGET_TOKEN`). The validated
  manifests (artifact `package-manifests` of that run) match rc.5's
  `SHA256SUMS` and pass local `winget validate`. `f0b92cd` removed the
  "signed" claim from the PR body.

- **winget**: the owner ran the publisher, opening
  [microsoft/winget-pkgs#460717](https://github.com/microsoft/winget-pkgs/pull/460717).
  Its files landed under the owner's existing `manifests/r/Roethlar/`
  folder with the lowercase `roethlar.PowerShellTokenKiller` identifier;
  the bot labeled it `Manifest-Validation-Error` ("The path and
  PackageIdentifier are case sensitive"). `6317f79` switched the
  identifier to `Roethlar.PowerShellTokenKiller`; validation-only run
  `40066034251` passed all seven jobs with it.

## Still required

- Update #460717's branch with run `40066034251`'s manifests and fix its
  title (operator action; scratchpad script `update-winget-pr.ps1`), then
  watch the winget-pkgs pipeline. The PR is pending until merged.
- Confirm AUR RPC indexing.
