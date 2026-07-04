# rel1-1: Release tags forwarded raw to -p:Version fail MSBuild

**Severity**: MEDIUM — the slice-3 release workflow would fail on its first
rc tag, before producing any asset.
**Status**: Verified (re-grade accepted 2026-07-04)
**Branch**: master (direct, per this repo's recorded codex-loop precedent)
**Commit**: `b11eb66`

## Evidence
`scripts/dev-install.ps1` passed `$PayloadVersion` straight to
`-p:Version=`; the plan's release workflow triggers on `v*` tags and passes
the tag verbatim. Reproduced: `dotnet build -p:Version=v0.2.0-rc.1` →
"error : 'v0.2.0-rc.1' is not a valid version string."

## Predicted observable failure
Slice 3's tag build calling `-LayoutOnly -Version v0.2.0-rc.1` fails in
publish before producing the RID layout or draft release assets.

## What
Tag-shaped versions (leading `v`) are not valid MSBuild/NuGet version
strings; the layout generator accepted them unnormalized.

## Approach
`Get-PtkVersion` strips a leading `v`/`V` from a provided `-Version`
(`-replace '^[vV]', ''`), so CI can pass the git tag verbatim and the
normalization lives in the single layout generator rather than in each
workflow.

## Files changed
- `scripts/dev-install.ps1` — Get-PtkVersion normalizes tag-shaped input

## Guard proof
Red before fix: the reproduced MSBuild error above. Green after:
`-LayoutOnly -OutputDir <tmp> -Version v0.2.0-rc.1` publishes successfully
with `0.2.0-rc.1` stamped into the layout's VERSION file.

## Coder dispute (if any)
None.

## Known gaps
None.

## Reviewer comments
codex (codex-cli 0.142.5), reviewed 10d4a1a against base d0e34d6,
2026-07-04 (UTC): finding returned. Re-grade at head 719fd85: resolution
ACCEPTED, zero new findings (no_findings=true).
