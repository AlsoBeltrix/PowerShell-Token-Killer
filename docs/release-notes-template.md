# PTK `<version>`

<!-- Replace every angle-bracket placeholder before publication. -->

## Status

- Source commit: `<40-hex commit>`
- Release workflow: `<canonical GitHub Actions URL>`
- Published: `<UTC timestamp>`
- Supersedes: `<version or none>`
- Withdrawn versions relevant to this release: `<version and advisory, or none>`

## What changed

Describe user-visible behavior, repaired defects, and intentionally unchanged
contracts. Link each closed issue. Do not turn an unverified behavior into a
claim.

## Install and upgrade

Link the exact public-install instructions in `README.md`. State whether this
is a prerelease. Existing users use the same transactional installer; document
any version-specific migration or say explicitly that none is required.

## Supported artifacts

The release must contain `SHA256SUMS`, `ptk-installer.zip`, and both products
for every supported RID:

| RID | PTK | SIEM receiver |
| --- | --- | --- |
| `win-x64` | `ptk-<version>-win-x64.zip` | `ptk-siem-receiver-<version>-win-x64.zip` |
| `win-arm64` | `ptk-<version>-win-arm64.zip` | `ptk-siem-receiver-<version>-win-arm64.zip` |
| `linux-x64` | `ptk-<version>-linux-x64.tar.gz` | `ptk-siem-receiver-<version>-linux-x64.tar.gz` |
| `linux-arm64` | `ptk-<version>-linux-arm64.tar.gz` | `ptk-siem-receiver-<version>-linux-arm64.tar.gz` |
| `osx-arm64` | `ptk-<version>-osx-arm64.tar.gz` | `ptk-siem-receiver-<version>-osx-arm64.tar.gz` |

State that Windows assets are publisher-signed, macOS ARM64 assets are
Developer ID signed and notarized, and Linux assets rely on the published
SHA-256 manifest only after those facts have been independently verified on
the downloaded archives.

## Known limitations

Link [`docs/known-limitations.md`](known-limitations.md) and list only
version-specific additions. Name any deferred external validation clearly.

## Verification evidence

Record:

- exact clean candidate commit and version;
- all six canonical CI job results;
- release workflow run and five native RID job results;
- eleven downloaded artifact hashes and build identities;
- Windows signature and security-product checks;
- macOS signature, notarization, and Gatekeeper checks;
- install, upgrade/repair, first invoke, restart, and uninstall results; and
- reviewer verdicts or known open findings.

Do not publish the release notes until every placeholder is removed and the
final owner gate names the same commit, version, artifacts, and workflow run.

