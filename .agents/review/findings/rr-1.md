# rr-1: release rerun could overwrite published assets

**Severity:** HIGH  
**Status:** FIXED in the release-immutability slice after `ec3034b`  
**Scope:** `.github/workflows/release.yml`, `scripts/assemble-draft-release.sh`

## Finding

The draft-assembly step used this fallback:

```text
gh release create ... || gh release upload "$tag" staged/* --clobber
```

Any reason `release create` failed—including an already published release for
the tag—therefore led to a clobbering upload. GitHub already contains both a
published `v0.3.0-rc.1` and a stale draft with that nominal tag, demonstrating
that tag-only lookup is not enough to distinguish safe draft recovery. A
workflow rerun could replace public artifacts while leaving their names and
version unchanged.

## Repair and proof

Draft assembly now queries the canonical repository's complete paginated
release list before mutation and refuses if any draft or published record uses
the requested tag. Query failure is fail-closed. The assembler never invokes
`gh release upload` and never uses `--clobber`; replacing a stale draft requires
the draft's separately authorized deletion followed by a clean rerun.

`scripts/test-assemble-draft-release.sh` failed on the old workflow because the
clobber fallback was present. With the repair it proves a missing-tag path
creates one draft, an existing-tag path performs no mutation, and a release-list
query failure performs no mutation. The test uses an exported shell-function
`gh` stub and cannot reach GitHub.
