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

## Still required

- `publish-packages.yml` dispatch from `master` for `0.3.0-rc.5` (refused
  to the agent by the auto-mode classifier; owner runs or approves it).
- winget PR via the operator `gh` session; confirm each channel's real
  availability.
