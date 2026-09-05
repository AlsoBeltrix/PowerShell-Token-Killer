#!/usr/bin/env bash
set -euo pipefail

# Deterministic exported-function stub. It shadows any installed GitHub CLI in
# every child shell, so the test cannot reach a network or repository.
gh() {
  printf '%s\n' "$*" >> "$PTK_GH_LOG"
  if [[ "$1" == "api" ]]; then
    [[ "$PTK_GH_MODE" == "query-fail" ]] && return 2
    [[ "$PTK_GH_MODE" == "existing" ]] && printf '%s\n' '369626463'
    return 0
  fi
  if [[ "$1 $2" == "release create" ]]; then
    [[ "$PTK_GH_MODE" == "create-fail" ]] && return 2
    return 0
  fi
  return 3
}
export -f gh

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

# Each available standard consumer must verify all eleven files, not silently
# skip malformed lines. GNU sha256sum and Perl shasum differ on one-space input.
verify_checksum_consumer() {
  local checksum_consumer
  local consumer_found=false
  for checksum_consumer in sha256sum shasum; do
    command -v "$checksum_consumer" >/dev/null 2>&1 || continue
    consumer_found=true
    if ! (
      cd "$work/assets"
      if [[ "$checksum_consumer" == sha256sum ]]; then
        LC_ALL=C sha256sum -c SHA256SUMS
      else
        LC_ALL=C shasum -a 256 -c SHA256SUMS
      fi
    ) > "$work/checksum-consumer.log" 2>&1; then
      cat "$work/checksum-consumer.log" >&2
      echo "$checksum_consumer rejected the assembled manifest" >&2
      return 1
    fi
    if [[ $(grep -c ': OK$' "$work/checksum-consumer.log") -ne 11 ]]; then
      cat "$work/checksum-consumer.log" >&2
      echo "$checksum_consumer did not verify all eleven assets" >&2
      return 1
    fi
  done
  [[ "$consumer_found" == true ]] || {
    echo 'no standard checksum consumer available' >&2
    return 1
  }
}

repo_root=$(cd "$(dirname "$0")/.." && pwd)
workflow="$repo_root/.github/workflows/release.yml"
helper="$repo_root/scripts/assemble-draft-release.sh"

if grep -Eq 'gh release upload.*--clobber' "$workflow"; then
  echo 'release workflow can overwrite assets on an existing release' >&2
  exit 1
fi
if ! grep -Fq 'ptk-installer.zip' "$workflow"; then
  echo 'release workflow does not publish the standalone installer bundle' >&2
  exit 1
fi
if ! grep -Fq 'build-installer-bundle.sh' "$workflow"; then
  echo 'release workflow does not use the tested installer-bundle builder' >&2
  exit 1
fi
if ! grep -Fq 'server/test-staged-install.ps1' "$workflow"; then
  echo 'release workflow does not exercise the packaged install transaction' >&2
  exit 1
fi

[[ -f "$helper" ]] || { echo 'draft-release assembler is missing' >&2; exit 1; }

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/assets"
: > "$work/assets/SHA256SUMS"
for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-arm64; do
  extension=tar.gz
  [[ "$rid" == win-* ]] && extension=zip
  printf '%s\n' 'proof asset' > "$work/assets/ptk-9.8.7-test-$rid.$extension"
  printf '%s\n' 'proof asset' > "$work/assets/ptk-siem-receiver-9.8.7-test-$rid.$extension"
done
bash "$repo_root/scripts/build-installer-bundle.sh" \
  "$repo_root" "$work/assets/ptk-installer.zip" "$work/assets/SHA256SUMS"
if bash "$repo_root/scripts/build-installer-bundle.sh" \
    "$repo_root" "$work/assets/ptk-installer.zip" "$work/assets/SHA256SUMS"; then
  echo 'installer-bundle builder replaced an existing bundle' >&2
  exit 1
fi
expected_entries=$'install.ps1\nptk_build_provenance.psm1\nptk_install_transaction.psm1'
actual_entries=$(unzip -Z1 "$work/assets/ptk-installer.zip" | LC_ALL=C sort)
[[ "$actual_entries" == "$expected_entries" ]] || {
  echo "installer bundle has unexpected contents: $actual_entries" >&2
  exit 1
}

actual_bundle_hash=$(sha256_file "$work/assets/ptk-installer.zip")
recorded_bundle_hash=$(awk '$2 == "ptk-installer.zip" { print $1 }' \
  "$work/assets/SHA256SUMS")
[[ "$recorded_bundle_hash" == "$actual_bundle_hash" ]] || {
  echo 'installer bundle checksum does not match the archive' >&2
  exit 1
}

[[ $(grep -c ' ptk-installer\.zip$' "$work/assets/SHA256SUMS") -eq 1 ]]

mkdir -p "$work/duplicate"
printf '%064d ptk-installer.zip\n' 0 > "$work/duplicate/SHA256SUMS"
if bash "$repo_root/scripts/build-installer-bundle.sh" \
    "$repo_root" "$work/duplicate/ptk-installer.zip" \
    "$work/duplicate/SHA256SUMS"; then
  echo 'installer-bundle builder accepted a pre-existing checksum entry' >&2
  exit 1
fi
[[ ! -e "$work/duplicate/ptk-installer.zip" ]] || {
  echo 'duplicate-checksum refusal left an installer archive behind' >&2
  exit 1
}
[[ $(grep -c ' ptk-installer\.zip$' "$work/duplicate/SHA256SUMS") -eq 1 ]]

mkdir -p "$work/shasum-only/path" "$work/shasum-only/output"
: > "$work/shasum-only/output/SHA256SUMS"
for command_name in awk cut rm shasum sort unzip zip; do
  ln -s "$(command -v "$command_name")" "$work/shasum-only/path/$command_name"
done
PATH="$work/shasum-only/path" /bin/bash \
  "$repo_root/scripts/build-installer-bundle.sh" \
  "$repo_root" "$work/shasum-only/output/ptk-installer.zip" \
  "$work/shasum-only/output/SHA256SUMS" >/dev/null
fallback_hash=$(awk '$2 == "ptk-installer.zip" { print $1 }' \
  "$work/shasum-only/output/SHA256SUMS")
[[ "$fallback_hash" == "$(shasum -a 256 \
  "$work/shasum-only/output/ptk-installer.zip" | cut -d ' ' -f 1)" ]] || {
  echo 'shasum fallback recorded an incorrect installer checksum' >&2
  exit 1
}

log="$work/gh.log"
sha=0123456789abcdef0123456789abcdef01234567

if PTK_GH_MODE=absent PTK_GH_LOG="$log" \
    bash "$helper" AlsoBeltrix/PowerShell-Token-Killer \
      v9.8.7-test 9.8.7-test "$sha" "$work/assets"; then
  echo 'draft assembler accepted a checksum manifest missing native assets' >&2
  exit 1
fi
[[ ! -s "$log" ]] || {
  echo 'invalid checksum manifest reached the GitHub CLI boundary' >&2
  exit 1
}

for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-arm64; do
  extension=tar.gz
  [[ "$rid" == win-* ]] && extension=zip
  for product in ptk ptk-siem-receiver; do
    name="$product-9.8.7-test-$rid.$extension"
    printf '%s %s\n' "$(sha256_file "$work/assets/$name")" "$name" \
      >> "$work/assets/SHA256SUMS"
  done
done

cp "$work/assets/SHA256SUMS" "$work/SHA256SUMS.good"
awk '$2 == "ptk-9.8.7-test-win-x64.zip" { $1 = sprintf("%064d", 0) } { print }' \
  "$work/SHA256SUMS.good" > "$work/assets/SHA256SUMS"
if PTK_GH_MODE=absent PTK_GH_LOG="$log" \
    bash "$helper" AlsoBeltrix/PowerShell-Token-Killer \
      v9.8.7-test 9.8.7-test "$sha" "$work/assets"; then
  echo 'draft assembler accepted an incorrect native-asset checksum' >&2
  exit 1
fi
[[ ! -s "$log" ]] || {
  echo 'incorrect native checksum reached the GitHub CLI boundary' >&2
  exit 1
}
cp "$work/SHA256SUMS.good" "$work/assets/SHA256SUMS"
printf '%s\n' 'must not be uploaded' > "$work/assets/unexpected.txt"

PTK_GH_MODE=absent PTK_GH_LOG="$log" \
  bash "$helper" AlsoBeltrix/PowerShell-Token-Killer \
    v9.8.7-test 9.8.7-test "$sha" "$work/assets"
grep -Fq 'api --paginate repos/AlsoBeltrix/PowerShell-Token-Killer/releases?per_page=100' "$log"
grep -Fq 'release create v9.8.7-test' "$log"
verify_checksum_consumer
if grep -Fq 'unexpected.txt' "$log"; then
  echo 'draft assembler included an unvalidated extra asset' >&2
  exit 1
fi
if grep -Fq 'release upload' "$log"; then
  echo 'new draft path unexpectedly uploaded into an existing release' >&2
  exit 1
fi

: > "$log"
if PTK_GH_MODE=existing PTK_GH_LOG="$log" \
    bash "$helper" AlsoBeltrix/PowerShell-Token-Killer \
      v9.8.7-test 9.8.7-test "$sha" "$work/assets"; then
  echo 'existing release was not refused' >&2
  exit 1
fi
grep -Fq 'api --paginate repos/AlsoBeltrix/PowerShell-Token-Killer/releases?per_page=100' "$log"
if grep -Eq 'release (create|upload)' "$log"; then
  echo 'existing release path attempted a mutation' >&2
  exit 1
fi

: > "$log"
if PTK_GH_MODE=query-fail PTK_GH_LOG="$log" \
    bash "$helper" AlsoBeltrix/PowerShell-Token-Killer \
      v9.8.7-test 9.8.7-test "$sha" "$work/assets"; then
  echo 'release-list query failure did not fail closed' >&2
  exit 1
fi
if grep -Eq 'release (create|upload)' "$log"; then
  echo 'release-list query failure attempted a mutation' >&2
  exit 1
fi

# Native Windows checksum files use CRLF; the assembled manifest mixes them
# with Unix LF entries. Only line endings are normalized, never names or hashes.
awk '/-win-(x64|arm64)\.zip$/ { printf "%s\r\n", $0; next } { print }' \
  "$work/SHA256SUMS.good" > "$work/assets/SHA256SUMS"
: > "$log"
PTK_GH_MODE=absent PTK_GH_LOG="$log" \
  bash "$helper" AlsoBeltrix/PowerShell-Token-Killer \
  v9.8.7-test 9.8.7-test "$sha" "$work/assets"
grep -Fq 'release create v9.8.7-test' "$log"
verify_checksum_consumer

for invalid_case in wrong-hash duplicate-name embedded-cr; do
  case "$invalid_case" in
    wrong-hash)
      awk '$2 == "ptk-9.8.7-test-win-x64.zip" {
        printf "%064d  %s\r\n", 0, $2; next
      } { print }' "$work/SHA256SUMS.good" > "$work/assets/SHA256SUMS"
      ;;
    duplicate-name)
      awk '$2 == "ptk-siem-receiver-9.8.7-test-win-x64.zip" {
        printf "%s  ptk-9.8.7-test-win-x64.zip\r\n", $1; next
      } { print }' "$work/SHA256SUMS.good" > "$work/assets/SHA256SUMS"
      ;;
    embedded-cr)
      awk '$2 == "ptk-9.8.7-test-win-x64.zip" {
        printf "%s  ptk-9.8.7-test-win-\rx64.zip\r\n", $1; next
      } { print }' "$work/SHA256SUMS.good" > "$work/assets/SHA256SUMS"
      ;;
  esac
  : > "$log"
  if PTK_GH_MODE=absent PTK_GH_LOG="$log" \
    bash "$helper" AlsoBeltrix/PowerShell-Token-Killer \
    v9.8.7-test 9.8.7-test "$sha" "$work/assets"; then
    echo "draft assembler accepted invalid Windows checksum: $invalid_case" >&2
    exit 1
  fi
  [[ ! -s "$log" ]] || {
    echo "invalid Windows checksum reached GitHub CLI: $invalid_case" >&2
    exit 1
  }
done

echo 'Draft release immutability tests passed.'
