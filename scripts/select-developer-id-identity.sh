#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
identity=$(awk -f "$script_dir/select-developer-id-identity.awk")
if [[ ! "$identity" =~ ^[[:xdigit:]]{40}$ ]]; then
  echo "no valid Developer ID Application signing identity found in the imported certificate" >&2
  exit 1
fi

printf '%s\n' "$identity"
