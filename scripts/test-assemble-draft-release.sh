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

repo_root=$(cd "$(dirname "$0")/.." && pwd)
workflow="$repo_root/.github/workflows/release.yml"
helper="$repo_root/scripts/assemble-draft-release.sh"

if grep -Eq 'gh release upload.*--clobber' "$workflow"; then
  echo 'release workflow can overwrite assets on an existing release' >&2
  exit 1
fi
[[ -f "$helper" ]] || { echo 'draft-release assembler is missing' >&2; exit 1; }

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/assets"
printf '%s\n' 'proof SHA256SUMS' > "$work/assets/SHA256SUMS"
for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-arm64; do
  printf '%s\n' 'proof asset' > "$work/assets/ptk-9.8.7-test-$rid.zip"
  printf '%s\n' 'proof asset' > "$work/assets/ptk-siem-receiver-9.8.7-test-$rid.zip"
done
log="$work/gh.log"
sha=0123456789abcdef0123456789abcdef01234567

PTK_GH_MODE=absent PTK_GH_LOG="$log" \
  bash "$helper" AlsoBeltrix/PowerShell-Token-Killer \
    v9.8.7-test 9.8.7-test "$sha" "$work/assets"
grep -Fq 'api --paginate repos/AlsoBeltrix/PowerShell-Token-Killer/releases?per_page=100' "$log"
grep -Fq 'release create v9.8.7-test' "$log"
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

echo 'Draft release immutability tests passed.'
