# Release candidate 0.3.0-rc.4

## Authority and identity

On 2026-10-23 the owner approved building rc.4 from current code ("yes to
all"), the `rtk rewrite` contract repair, and unsigned Windows assets (both
recorded in `.agents/decisions.md`). Publication is not authorized by those
words; the 2026-09-08 publication authority in
`.agents/plans/release-0.3.0-rc.3.md` is a prior-session record and must be
reconfirmed before any outward release action.

- Version: `0.3.0-rc.4`; intended tag `v0.3.0-rc.4` (no tag exists yet; the
  draft carries it).
- Product source: `836fb108e09b60288dce9333bb99d8a7d2088cc4`.
- Exact-source CI: `40051736355`, all six jobs passed.
- Native release workflow: `40052372861`, all five native legs and draft
  assembly passed.
- Draft release: `422458581`, twelve assets. Marked prerelease by
  `gh release edit --prerelease` after creation; `ac5c6a6` makes future rc
  drafts prerelease automatically.
- Build branch on canonical `origin`: `release/0.3.0-rc.4`, pointing at the source above. <!-- lint: allow (Git branch, not a repository path) -->
- rc.2 and rc.3 drafts are unchanged.

## Changes since rc.3 that shaped this build

- Earlier release attempts: `40047540862` (all legs failed: rtk 1.0.1 rejects
  `hook check --agent ptk`) and `40050237185` (Windows signing 403; account
  gone). Neither produced a draft.
- PTK now asks RTK through `rtk rewrite` and accepts exit 0/3.
- Windows assets are not publisher-signed; integrity rests on `SHA256SUMS`
  and the Defender gate.

## Current evidence

Downloaded-release verification `40052902047` (workflow ref `master`, inputs
version and source above) passed all six jobs: the inventory and the native
downloaded-product job on every RID. The osx-arm64 job's first attempt ran
its proof step for ~40 minutes with no retained log and was cancelled at the
job timeout; its rerun passed in 2½ minutes. This matches rc.3's one-off osx
hang and is recorded as an intermittent macOS runner stall, not resolved.

- macOS: 36 Mach-O files with valid Developer ID signatures and online
  notarization.
- Windows x64 and arm64: 891 binaries each; 875 carry valid (Microsoft)
  signatures, 16 PTK binaries are unsigned as decided; Defender scan
  completed and left the payload intact on both.

## Publication (2026-10-24, owner go)

Published as a prerelease at 2026-10-24T03:11:06Z with the notes in the
release (not Latest; `v0.2.2` stays Latest). Tag `v0.3.0-rc.4` →
`836fb10`.

- **Public bootstrap is broken in this release.** Running the README
  bootstrap against it in an isolated home on `NETWATCH-01` failed after
  checksum verification: `install.ps1` looked for its smoke test at
  `<checkout>/server/test-handshake.ps1`, which neither
  `ptk-installer.zip` nor an installed payload contains. Every
  `-FromRelease` install outside a checkout fails the same way. Fixed in
  `f24c3ce`; a bundle built from it installed published rc.4 end to end
  in an isolated home. Assets are immutable, so the fix ships in the next
  release; package-manager installs do not use this installer.
- Package publication `40057264358` (release event): AUR validation
  passed; winget (schema-header warnings exit non-zero) and Homebrew (no
  git identity for `brew tap-new`) failed, so no channel was published.
  Fixed in `06f9074`; rerun from `master` by `workflow_dispatch`.

## Still required

- Owner go for publication, then final factual release notes from
  `docs/release-notes-template.md` (state unsigned Windows and SmartScreen).
- Public bootstrap proof after publication.
- Package channels (Homebrew, Scoop, winget, AUR) per the rc.3 plan, each
  verified for actual availability.
