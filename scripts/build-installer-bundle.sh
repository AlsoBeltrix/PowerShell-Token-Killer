#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo 'usage: build-installer-bundle.sh SOURCE_ROOT OUTPUT_ZIP CHECKSUM_FILE' >&2
  exit 64
fi

source_root=$1
output_zip=$2
checksum_file=$3
[[ -d "$source_root/scripts" ]] || {
  echo "source scripts directory is missing under '$source_root'" >&2
  exit 66
}
[[ "${output_zip##*/}" == 'ptk-installer.zip' ]] || {
  echo "installer bundle must be named ptk-installer.zip: '$output_zip'" >&2
  exit 64
}
[[ ! -e "$output_zip" ]] || {
  echo "refusing to replace existing installer bundle '$output_zip'" >&2
  exit 73
}
[[ -f "$checksum_file" ]] || {
  echo "checksum manifest is missing: '$checksum_file'" >&2
  exit 66
}

if awk '$2 == "ptk-installer.zip" { found = 1 } END { exit !found }' \
    "$checksum_file"; then
  echo "refusing duplicate ptk-installer.zip checksum in '$checksum_file'" >&2
  exit 73
fi

files=(
  "$source_root/scripts/install.ps1"
  "$source_root/scripts/ptk_install_transaction.psm1"
  "$source_root/scripts/ptk_build_provenance.psm1"
)
for file in "${files[@]}"; do
  [[ -f "$file" ]] || { echo "installer input is missing: '$file'" >&2; exit 66; }
done

cleanup_partial() {
  status=$?
  if [[ $status -ne 0 ]]; then
    rm -f -- "$output_zip"
  fi
  exit "$status"
}
trap cleanup_partial EXIT

zip -j -q "$output_zip" "${files[@]}"
expected_entries=$'install.ps1\nptk_build_provenance.psm1\nptk_install_transaction.psm1'
actual_entries=$(unzip -Z1 "$output_zip" | LC_ALL=C sort)
[[ "$actual_entries" == "$expected_entries" ]] || {
  echo "installer bundle contents are unexpected: $actual_entries" >&2
  exit 65
}

if command -v sha256sum >/dev/null 2>&1; then
  hash=$(sha256sum "$output_zip" | cut -d ' ' -f 1)
elif command -v shasum >/dev/null 2>&1; then
  hash=$(shasum -a 256 "$output_zip" | cut -d ' ' -f 1)
else
  echo 'neither sha256sum nor shasum is available' >&2
  exit 69
fi
printf '%s %s\n' "$hash" "${output_zip##*/}" >> "$checksum_file"
printf '%s %s\n' "$hash" "${output_zip##*/}"
trap - EXIT
