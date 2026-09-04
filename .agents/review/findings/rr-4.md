# rr-4: public bootstrap skipped the newest prerelease

**Severity:** HIGH
**Status:** FIXED in the release-selection slice after `add2c2e`, with the
README path executed end to end in the follow-up after `d40228c`
**Scope:** `README.md`, `server/test-release-selection.ps1`

## Finding

The public bootstrap downloaded `ptk-installer.zip` through GitHub's
`/releases/latest/download` route. GitHub defines “latest” there as the latest
stable release and excludes prereleases. PTK's installer intentionally selects
the most recently published release including prereleases.

Live canonical state made the failure concrete: `v0.2.2` was the latest stable
release, `v0.3.0-rc.1` was the newer published prerelease, and the old stable
release had no standalone installer bundle. Publishing the recommended next
candidate as `v0.3.0-rc.2` would therefore leave the documented no-version
bootstrap pointing at an asset that does not exist.

## Repair proof

The bootstrap now enumerates the canonical repository's published releases,
rejects malformed or ambiguous selection data, chooses the newest
`published_at` value including prereleases, and downloads both
`ptk-installer.zip` and `SHA256SUMS` from that exact tag. It passes the selected
version explicitly to `install.ps1`, preventing a later release from changing
the payload between bootstrap selection and installer execution.

`server/test-release-selection.ps1` failed on the stable-only URL before the
repair. It now rejects any return to `/releases/latest/download`, requires the
paginated published-release selection endpoint, and requires the selected
version to be pinned through the installer call. The test, PowerShell parser,
and `git diff --check` passed after repair.

The initial guard inspected README text but did not execute it. An executable
bootstrap fixture then reproduced GitHub's non-enumerated REST-array shape and
failed against `d40228c`: the README's `@(...)` wrapper nested that array, so
the first selection predicate saw an array-valued `draft` property and refused
the response. Removing only that wrapper preserves a direct object array for
the pipeline. The executable proof now builds and checksum-validates a real
installer bundle, selects the newer published prerelease while ignoring a
newer draft, downloads both assets from its exact tag, pins `-Version`, runs
the extracted installer, and proves temporary extraction cleanup.
