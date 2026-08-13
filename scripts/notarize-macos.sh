#!/usr/bin/env bash

set -euo pipefail

archive=${1:-}
if [[ -z "$archive" || ! -f "$archive" ]]; then
  echo "usage: $0 <notarization-archive>" >&2
  exit 2
fi

if [[ -z "${APPLE_ID:-}" || -z "${APPLE_APP_SPECIFIC_PASSWORD:-}" || -z "${APPLE_TEAM_ID:-}" ]]; then
  echo "APPLE_ID, APPLE_APP_SPECIFIC_PASSWORD, and APPLE_TEAM_ID are required" >&2
  exit 1
fi

timeout_seconds=${PTK_NOTARY_TIMEOUT_SECONDS:-1800}
retry_delay_seconds=${PTK_NOTARY_RETRY_DELAY_SECONDS:-5}
if [[ ! "$timeout_seconds" =~ ^[1-9][0-9]*$ ]]; then
  echo "PTK_NOTARY_TIMEOUT_SECONDS must be a positive integer" >&2
  exit 2
fi
if [[ ! "$retry_delay_seconds" =~ ^[0-9]+$ ]]; then
  echo "PTK_NOTARY_RETRY_DELAY_SECONDS must be a non-negative integer" >&2
  exit 2
fi

tmp_base=${RUNNER_TEMP:-${TMPDIR:-/tmp}}
work_dir=$(mktemp -d "$tmp_base/ptk-notary.XXXXXX")
cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

auth_args=(
  --apple-id "$APPLE_ID"
  --password "$APPLE_APP_SPECIFIC_PASSWORD"
  --team-id "$APPLE_TEAM_ID"
)

plist_value() {
  local file=$1
  local key=$2
  /usr/bin/plutil -extract "$key" raw -o - "$file" 2>/dev/null || true
}

show_result() {
  local output_file=$1
  local error_file=$2
  [[ ! -s "$output_file" ]] || cat "$output_file"
  [[ ! -s "$error_file" ]] || cat "$error_file" >&2
}

fetch_log() {
  local submission_id=$1
  xcrun notarytool log "$submission_id" "${auth_args[@]}" || true
}

fail_terminal() {
  local submission_id=$1
  local message=$2
  fetch_log "$submission_id"
  echo "$message" >&2
  exit 1
}

started_at=$(date +%s)
deadline=$((started_at + timeout_seconds))
submit_output="$work_dir/submit.plist"
submit_error="$work_dir/submit.stderr"
submit_rc=0
xcrun notarytool submit "$archive" "${auth_args[@]}" \
  --output-format plist --no-progress >"$submit_output" 2>"$submit_error" || submit_rc=$?
show_result "$submit_output" "$submit_error"

submission_id=$(plist_value "$submit_output" id)
if [[ ! "$submission_id" =~ ^[[:xdigit:]]{8}-[[:xdigit:]]{4}-[[:xdigit:]]{4}-[[:xdigit:]]{4}-[[:xdigit:]]{12}$ ]]; then
  echo "notary submission did not return a canonical UUID submission id" >&2
  exit 1
fi

submit_status=$(plist_value "$submit_output" status)
case "$submit_status" in
  Accepted)
    echo "Notarization accepted: $submission_id"
    exit 0
    ;;
  Invalid|Rejected)
    fail_terminal "$submission_id" "notarization failed with terminal status: $submit_status"
    ;;
  "In Progress"|"")
    ;;
  *)
    fail_terminal "$submission_id" "notarization returned unknown status: $submit_status"
    ;;
esac

if ((submit_rc != 0)); then
  echo "submission transport exited $submit_rc after returning id $submission_id; resuming by id" >&2
fi

while true; do
  remaining=$((deadline - $(date +%s)))
  if ((remaining <= 0)); then
    fail_terminal "$submission_id" "notarization did not reach Accepted within ${timeout_seconds}s"
  fi

  wait_output="$work_dir/wait.plist"
  wait_error="$work_dir/wait.stderr"
  wait_rc=0
  xcrun notarytool wait "$submission_id" "${auth_args[@]}" \
    --timeout "${remaining}s" --output-format plist --no-progress \
    >"$wait_output" 2>"$wait_error" || wait_rc=$?
  show_result "$wait_output" "$wait_error"

  wait_status=$(plist_value "$wait_output" status)
  case "$wait_status" in
    Accepted)
      echo "Notarization accepted: $submission_id"
      exit 0
      ;;
    Invalid|Rejected)
      fail_terminal "$submission_id" "notarization failed with terminal status: $wait_status"
      ;;
    "In Progress"|"")
      ;;
    *)
      fail_terminal "$submission_id" "notarization returned unknown status: $wait_status"
      ;;
  esac

  if ((wait_rc != 0)); then
    echo "notary wait transport exited $wait_rc; checking authoritative status for $submission_id" >&2
  fi

  info_output="$work_dir/info.plist"
  info_error="$work_dir/info.stderr"
  info_rc=0
  xcrun notarytool info "$submission_id" "${auth_args[@]}" \
    --output-format plist >"$info_output" 2>"$info_error" || info_rc=$?
  show_result "$info_output" "$info_error"

  info_status=$(plist_value "$info_output" status)
  case "$info_status" in
    Accepted)
      echo "Notarization accepted: $submission_id"
      exit 0
      ;;
    Invalid|Rejected)
      fail_terminal "$submission_id" "notarization failed with terminal status: $info_status"
      ;;
    "In Progress")
      echo "Notarization remains In Progress; resuming wait for $submission_id"
      ;;
    "")
      if ((info_rc != 0)); then
        echo "notary info transport exited $info_rc; retrying while the bounded allowance remains" >&2
      else
        echo "notary info returned no status; retrying while the bounded allowance remains" >&2
      fi
      ;;
    *)
      fail_terminal "$submission_id" "notarization returned unknown status: $info_status"
      ;;
  esac

  remaining=$((deadline - $(date +%s)))
  if ((remaining <= 0)); then
    fail_terminal "$submission_id" "notarization did not reach Accepted within ${timeout_seconds}s"
  fi
  if ((retry_delay_seconds > 0)); then
    delay=$retry_delay_seconds
    ((delay <= remaining)) || delay=$remaining
    sleep "$delay"
  fi
done
