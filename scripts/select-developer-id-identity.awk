/^[[:space:]]*[0-9]+\)[[:space:]]+[[:xdigit:]]{40}[[:space:]]+"Developer ID Application:/ {
  fingerprint = $2
  if (fingerprint ~ /^[[:xdigit:]]{40}$/) {
    print fingerprint
    exit
  }
}
