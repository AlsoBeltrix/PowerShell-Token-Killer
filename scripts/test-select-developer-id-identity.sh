#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
selector="$script_dir/select-developer-id-identity.sh"
valid_fingerprint=0123456789ABCDEF0123456789ABCDEF01234567
developer_fingerprint=89ABCDEF0123456789ABCDEF0123456789ABCDEF

assert_success() {
  local name=$1
  local expected=$2
  local input=$3
  local actual
  actual=$(bash "$selector" <<<"$input")
  if [[ "$actual" != "$expected" ]]; then
    echo "$name: expected '$expected', got '$actual'" >&2
    exit 1
  fi
}

assert_failure() {
  local name=$1
  local input=$2
  local output
  local rc=0
  output=$(bash "$selector" <<<"$input" 2>&1) || rc=$?
  if [[ "$rc" -ne 1 ]]; then
    echo "$name: expected exit 1, got $rc" >&2
    exit 1
  fi
  if [[ "$output" != 'no valid Developer ID Application signing identity found in the imported certificate' ]]; then
    echo "$name: intended diagnostic missing: $output" >&2
    exit 1
  fi
}

assert_failure zero-identities '     0 valid identities found'

assert_failure unrelated-only "  1) $valid_fingerprint \"Apple Development: Example (TEAM123456)\"
     1 valid identities found"

assert_success unrelated-first "$developer_fingerprint" "  1) $valid_fingerprint \"Apple Development: Example (TEAM123456)\"
  2) $developer_fingerprint \"Developer ID Application: Example, Inc. (TEAM123456)\"
     2 valid identities found"

assert_success developer-id "$valid_fingerprint" "  1) $valid_fingerprint \"Developer ID Application: Example, Inc. (TEAM123456)\"
     1 valid identities found"

assert_failure malformed-fingerprint '  1) NOT-A-FINGERPRINT "Developer ID Application: Example, Inc. (TEAM123456)"'
assert_failure short-fingerprint '  1) 0123456789ABCDEF "Developer ID Application: Example, Inc. (TEAM123456)"'

echo 'Developer ID identity selection tests passed.'
