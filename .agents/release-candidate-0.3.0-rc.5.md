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

## Still required

- Publication. The agent's publish call was refused by the Claude Code auto
  mode classifier ("Create Public Surface"); the owner publishes or approves
  the call.
- Public bootstrap proof against the published release, in an isolated home.
- Package channels: publishing fires `publish-packages.yml` at the rc.5 tag,
  which includes both validation fixes. Then open the winget PR with the
  operator `gh` session and confirm each channel's real availability.
