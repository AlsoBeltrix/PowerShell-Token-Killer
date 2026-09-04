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

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | cut -d ' ' -f 1
  else
    echo 'neither sha256sum nor shasum is available' >&2
    exit 69
  fi
}

required_assets=()
for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-arm64; do
  extension=tar.gz
  [[ "$rid" == win-* ]] && extension=zip
  for product in ptk ptk-siem-receiver; do
    archive="$asset_dir/$product-$version-$rid.$extension"
    [[ -f "$archive" ]] || {
      echo "required release archive is missing: '$archive'" >&2
      exit 66
    }
    required_assets+=("$archive")
  done
done
installer_bundle="$asset_dir/ptk-installer.zip"
[[ -f "$installer_bundle" ]] || {
  echo "standalone installer bundle is missing: '$installer_bundle'" >&2
  exit 66
}
required_assets+=("$installer_bundle")

manifest_entry_count=$(awk 'NF { count += 1 } END { print count + 0 }' \
  "$asset_dir/SHA256SUMS")
[[ "$manifest_entry_count" == "${#required_assets[@]}" ]] || {
  echo "SHA256SUMS must contain exactly ${#required_assets[@]} entries; found $manifest_entry_count" >&2
  exit 66
}

for asset in "${required_assets[@]}"; do
  name=${asset##*/}
  recorded_hashes=$(awk -v name="$name" 'NF == 2 && $2 == name { print $1 }' \
    "$asset_dir/SHA256SUMS")
  recorded_count=$(printf '%s\n' "$recorded_hashes" | \
    awk 'NF { count += 1 } END { print count + 0 }')
  [[ "$recorded_count" == 1 && "$recorded_hashes" =~ ^[0-9a-fA-F]{64}$ ]] || {
    echo "SHA256SUMS must identify '$name' exactly once" >&2
    exit 66
  }
  recorded_hash=$(printf '%s' "$recorded_hashes" | tr '[:upper:]' '[:lower:]')
  actual_hash=$(sha256_file "$asset" | tr '[:upper:]' '[:lower:]')
  [[ "$recorded_hash" == "$actual_hash" ]] || {
    echo "SHA256SUMS hash does not match '$name'" >&2
    exit 65
  }
done

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

assets=("${required_assets[@]}" "$asset_dir/SHA256SUMS")

gh release create "$tag" \
  -R "$repository" \
  --draft \
  --target "$target_sha" \
  --title "ptk $version" \
  --notes "Draft build for $version. Every asset was built and smoke-tested on its own native runner. Verify downloads against SHA256SUMS." \
  "${assets[@]}"
