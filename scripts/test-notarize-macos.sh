#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
test_root=$(mktemp -d "${TMPDIR:-/tmp}/ptk-notary-test.XXXXXX")
cleanup() {
  rm -rf -- "$test_root"
}
trap cleanup EXIT

mock_dir="$test_root/mock-bin"
mkdir -p "$mock_dir"
touch "$test_root/payload.zip"

cat >"$mock_dir/xcrun" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail

[[ ${1:-} == notarytool ]] || exit 90
command_name=${2:-}
submission_id=12345678-1234-4abc-8def-123456789abc
state_dir=${PTK_NOTARY_TEST_STATE_DIR:?}
scenario=${PTK_NOTARY_TEST_SCENARIO:?}

emit_plist() {
  local id=${1:-}
  local status=${2:-}
  cat <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
PLIST
  [[ -z "$id" ]] || printf '<key>id</key><string>%s</string>\n' "$id"
  [[ -z "$status" ]] || printf '<key>status</key><string>%s</string>\n' "$status"
  echo '</dict></plist>'
}

next_count() {
  local name=$1
  local file="$state_dir/$name"
  local count=0
  [[ ! -f "$file" ]] || count=$(<"$file")
  count=$((count + 1))
  echo "$count" >"$file"
  echo "$count"
}

case "$command_name" in
  submit)
    case "$scenario" in
      missing-id) emit_plist '' 'In Progress' ;;
      malformed-id) emit_plist 'not-a-uuid' 'In Progress' ;;
      *) emit_plist "$submission_id" 'In Progress' ;;
    esac
    ;;
  wait)
    wait_count=$(next_count wait-count)
    case "$scenario" in
      recover)
        if ((wait_count == 1)); then
          echo 'simulated transport drop' >&2
          exit 69
        fi
        emit_plist "$submission_id" 'Accepted'
        ;;
      rejected)
        emit_plist "$submission_id" 'Invalid'
        ;;
      *) exit 91 ;;
    esac
    ;;
  info)
    next_count info-count >/dev/null
    [[ "$scenario" == recover ]] || exit 92
    emit_plist "$submission_id" 'In Progress'
    ;;
  log)
    echo "simulated notary log for $scenario"
    ;;
  *)
    exit 93
    ;;
esac
MOCK
chmod +x "$mock_dir/xcrun"

run_case() {
  local scenario=$1
  local expected_rc=$2
  local expected_text=$3
  local state_dir="$test_root/state-$scenario"
  local output
  local rc=0
  mkdir -p "$state_dir"
  output=$(PATH="$mock_dir:$PATH" \
    PTK_NOTARY_TEST_SCENARIO="$scenario" \
    PTK_NOTARY_TEST_STATE_DIR="$state_dir" \
    PTK_NOTARY_TIMEOUT_SECONDS=5 \
    PTK_NOTARY_RETRY_DELAY_SECONDS=0 \
    APPLE_ID=test@example.invalid \
    APPLE_APP_SPECIFIC_PASSWORD=test-password \
    APPLE_TEAM_ID=TESTTEAM \
    bash "$script_dir/notarize-macos.sh" "$test_root/payload.zip" 2>&1) || rc=$?
  if [[ "$rc" -ne "$expected_rc" ]]; then
    echo "$output" >&2
    echo "$scenario: expected exit $expected_rc, got $rc" >&2
    exit 1
  fi
  if ! grep -Fq "$expected_text" <<<"$output"; then
    echo "$output" >&2
    echo "$scenario: missing expected text: $expected_text" >&2
    exit 1
  fi
}

run_case recover 0 'Notarization accepted:'
[[ $(<"$test_root/state-recover/wait-count") == 2 ]]
[[ $(<"$test_root/state-recover/info-count") == 1 ]]
run_case rejected 1 'notarization failed with terminal status: Invalid'
run_case missing-id 1 'notary submission did not return a canonical UUID submission id'
run_case malformed-id 1 'notary submission did not return a canonical UUID submission id'

echo 'Notarization recovery tests passed.'
