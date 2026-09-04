#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 5 ]]; then
  echo 'usage: assemble-draft-release.sh REPOSITORY TAG VERSION TARGET_SHA ASSET_DIR' >&2
  exit 64
fi

repository=$1
tag=$2
version=${3#v}
target_sha=$4
asset_dir=$5

[[ "$repository" == 'AlsoBeltrix/PowerShell-Token-Killer' ]] || {
  echo "refusing non-canonical release repository '$repository'" >&2
  exit 64
}
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]] || {
  echo "invalid release version '$version'" >&2
  exit 64
}
[[ "$tag" == "v$version" ]] || {
  echo "release tag '$tag' does not match version '$version'" >&2
  exit 64
}
[[ "$target_sha" =~ ^[0-9a-fA-F]{40}$ ]] || {
  echo "invalid release target '$target_sha'" >&2
  exit 64
}
[[ -d "$asset_dir" && -f "$asset_dir/SHA256SUMS" ]] || {
  echo "release assets or SHA256SUMS are missing under '$asset_dir'" >&2
  exit 66
}

archive_count=$(find "$asset_dir" -maxdepth 1 -type f \
  \( -name 'ptk-*.zip' -o -name 'ptk-*.tar.gz' \) -print | wc -l | tr -d ' ')
[[ "$archive_count" == '10' ]] || {
  echo "expected 10 release archives under '$asset_dir', found $archive_count" >&2
  exit 66
}

# A release record is the immutability boundary, including drafts. Query the
# complete release list because GitHub's tag endpoint can hide a draft when a
# published release uses the same nominal tag. Query failure stops under -e.
# Never use `upload --clobber`: an owner must approve deleting a stale draft
# before a clean workflow rerun creates a replacement.
existing_release_ids=$(gh api --paginate \
  "repos/$repository/releases?per_page=100" \
  --jq ".[] | select(.tag_name == \"$tag\") | .id")
if [[ -n "$existing_release_ids" ]]; then
  echo "release '$tag' already exists; refusing to replace or clobber its assets" >&2
  exit 73
fi

shopt -s nullglob
assets=("$asset_dir"/*)
[[ ${#assets[@]} -gt 0 ]] || {
  echo "no release assets found under '$asset_dir'" >&2
  exit 66
}

gh release create "$tag" \
  -R "$repository" \
  --draft \
  --target "$target_sha" \
  --title "ptk $version" \
  --notes "Draft build for $version. Every asset was built and smoke-tested on its own native runner. Verify downloads against SHA256SUMS." \
  "${assets[@]}"
