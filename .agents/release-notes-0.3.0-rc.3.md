# PTK 0.3.0-rc.3

This prerelease replaces per-call PowerShell redirect hooks with a standalone
native handler, improves startup while reading retained audit history, and
adds the native `ptk` command used by package-manager installations.

Source: [`e823781b2127c7f3b7c2e01eb5daf9da19e34a38`](https://github.com/AlsoBeltrix/PowerShell-Token-Killer/commit/e823781b2127c7f3b7c2e01eb5daf9da19e34a38).
It supersedes the published `0.3.0-rc.1` prerelease. The older rc.2 draft was
never published and its archives remain separate.

## What changed

- The redirect hook evaluates shell calls and returns deny guidance without
  launching `pwsh` or loading the MCP server. It preserves PTK_DIRECT consent,
  working-directory handling, and the existing hook protocol.
- Audit startup reads retained spool files through a buffered reader. Large
  histories no longer incur a separate file read for every byte. Startup time
  still depends on retained history and host load; this is not a fixed startup
  latency guarantee.
- The package includes `ptk version`, `ptk doctor`, `ptk init`, and
  `ptk uninstall`. Setup uses the embedded PowerShell engine and requires an
  explicit harness selection. Package managers own payload placement/removal.
- `Write-Host`/information and verbose output are retained in responses and
  recoverable output artifacts. Progress remains transient. Normal module
  autoloading is enabled in warm sessions; user profiles are not loaded.
- Codex setup repairs orphaned PTK tool-policy tables and installs the native
  redirect hook while retaining the harness's separate hook-trust review.
- Every PTK and receiver build carries a unique identity tied to its exact
  source, version, RID, and build time. Package provenance, MCP initialize,
  diagnostics, audit records, receiver logs, and health agree on that identity.
- The public bootstrap downloads one checksum-verified installer bundle with
  both required modules and pins subsequent downloads to the selected release.
- Audit export and the separate optional SIEM receiver retain full command and
  response evidence, explicit attribution, independent destination delivery
  state, and operator investigation flows. Windows private permissions and
  service-identity configuration were repaired.

## Install and upgrade

Use the [checksum-verified public installer](https://github.com/AlsoBeltrix/PowerShell-Token-Killer/blob/e823781b2127c7f3b7c2e01eb5daf9da19e34a38/README.md#public-install).
The package is self-contained; RTK is a required dependency. Windows ARM64
uses RTK's x64 binary under OS emulation.

After a package manager places PTK on PATH, select the harness to register:

```text
ptk doctor
ptk init --agent codex
```

Close sessions using an installed payload before upgrading or removing that
payload. The transactional installer preserves user-owned data and restores
the previous payload and registrations if activation fails. New configuration
takes effect in new harness sessions; existing warm sessions are not migrated.
The five MCP tools remain `ptk_invoke`, `ptk_session`, `ptk_state`, `ptk_reset`,
and `ptk_output`.

Existing audit destination configuration migrates into the destination
registry. The optional receiver database migrates forward to schema 11;
back up and integrity-check it first. Binary downgrade after database migration
is unsupported.

## Artifacts and evidence

The release contains PTK and separate SIEM receiver archives for `win-x64`,
`win-arm64`, `linux-x64`, `linux-arm64`, and `osx-arm64`, plus
`ptk-installer.zip` and `SHA256SUMS`.

The exact product source passed [all six CI jobs](https://github.com/AlsoBeltrix/PowerShell-Token-Killer/actions/runs/34275489232).
The native build is [release run 34276660442](https://github.com/AlsoBeltrix/PowerShell-Token-Killer/actions/runs/34276660442).

This file remains a working draft until downloaded native evidence, exact
asset inventory, package-channel availability, and publication facts are added.

## Known limitations and recovery

The [documented product boundaries](https://github.com/AlsoBeltrix/PowerShell-Token-Killer/blob/e823781b2127c7f3b7c2e01eb5daf9da19e34a38/docs/known-limitations.md)
apply. Real external-SIEM query-back acceptance and real cross-account service
handoff remain unverified; packaged adapter/workflow tests are narrower proof.
Current harness adapters do not inject per-call agent/model/task identity;
missing client attribution is labeled explicitly.

Release assets are immutable. Follow the [withdrawal and recovery procedure](https://github.com/AlsoBeltrix/PowerShell-Token-Killer/blob/e823781b2127c7f3b7c2e01eb5daf9da19e34a38/docs/release-recovery.md)
if an artifact is found unsafe or unusable; fixes receive a new version.
