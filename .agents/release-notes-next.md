# Next PTK prerelease — working release notes

**Status:** NOT PUBLISHABLE. This is the factual content draft for
`v0.3.0-rc.2`; security/support rulings and exact candidate evidence remain
unset. Populate the public release from `docs/release-notes-template.md` only
after every pending field below is proved.

## Candidate identity

- Version: `0.3.0-rc.2`, ruled by the owner on 2026-09-04.
- Supersedes: `0.3.0-rc.1` (`0c8ed87635ef37db548d086ada78a2020c4b390f`).
- Product delta summarized through: `0c9328a`.
- Exact final source commit, workflow run, publication time: pending.

## What changed

### PowerShell execution and recovery

- `Write-Host`/information and verbose records now appear in labeled response
  sections and remain recoverable from the immutable `ptk_output` artifact.
  Progress remains intentionally transient and uncaptured. Unsafe information
  payloads are omitted explicitly without invoking user-defined formatting.
- Agent sessions retain normal PowerShell module autoloading, so an available
  module can autoload on first use and remain warm in that session. User profiles
  still do not run.
- Unix worker launch now preserves case-distinct environment-variable names.
  Timeout, containment, exact-once dispatch, and the five-tool public surface
  remain unchanged.

### Audit export and SIEM receiver

- Exported records now carry full-fidelity exact command, caller response, and
  captured output/error evidence with truthful optional per-call agent/model/task
  attribution and requested/effective execution context.
- Operators explicitly configure destinations. Each enabled destination has
  independent delivery cursors, obligations, gaps, health, retry state, and
  confirmed historical backfill. Adding a destination is prospective; removing
  one with undelivered evidence requires a recorded abandonment action.
- The standalone receiver now indexes attributable activity, tracks evidence
  manifest completeness, supports authorized exact evidence reassembly, and
  exposes operator investigation APIs and dashboard flows. It remains a separate
  opt-in deployment; PTK never installs or selects it automatically.
- Packaged receiver lifecycle tooling validates checksums, version, RID,
  ownership, path separation, and native service configuration across supported
  hosts. Windows deployment overlap and service ACL handling were repaired.

### Installation, provenance, and release integrity

- Every PTK and SIEM package build receives a fresh 32-hex build identity.
  `BUILD-PROVENANCE.json`, binary informational versions, MCP initialize,
  `ptk_state`, audit records, receiver logs, and receiver health agree on the
  exact version, source commit, clean/dirty state, UTC build time, RID, and build
  identity.
- Public no-clone installation now begins with one checksum-verified
  `ptk-installer.zip` containing the installer and both required modules. The
  bootstrap selects the newest published release, including prereleases, then
  pins all subsequent downloads to that exact version.
- Source-install fallback no longer hard-codes a stale development version or
  leaks a handled native exit status. Codex initialization repairs an orphaned
  PTK tool-policy table while preserving valid registrations and policies.
- Release assembly refuses to overwrite any existing draft or published version,
  requires the exact ten native archives plus installer bundle, and verifies the
  eleven-entry SHA-256 manifest before creating a draft. Every native RID now
  exercises the packaged install transaction before its archive is uploaded.

### Public operations

- Added factual privacy and known-limitations documents, contribution guidance,
  bug/feature/PR templates, release-note requirements, and an immutable-version
  withdrawal/recovery procedure.
- Security reporting and support boundaries are intentionally absent until the
  owner adopts exact policies; do not publish this candidate without them.

## Install and upgrade

- This release is prerelease `0.3.0-rc.2`.
- New users follow the checksum-verified public bootstrap in `README.md`.
  Existing users use the same transactional installer. PTK processes must be
  stopped before upgrade; activation failure restores the prior installer-owned
  payload and registrations while retaining user-owned data.
- Existing single-destination export configuration migrates once into the
  destination registry. Review destination status after upgrade.
- The receiver database migrates automatically and transactionally forward to
  schema 11. Back up and integrity-check it first. Binary downgrade after schema
  migration is unsupported; follow the receiver upgrade/recovery procedure.
- No public MCP tool was added or removed; the surface remains exactly
  `ptk_invoke`, `ptk_session`, `ptk_state`, `ptk_reset`, and `ptk_output`.

## Supported artifacts

The candidate must contain PTK and standalone SIEM receiver archives for
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, and `osx-arm64`, plus
`ptk-installer.zip` and `SHA256SUMS`. Windows assets are publisher-signed; macOS
ARM64 assets are Developer ID signed and notarized; Linux assets make only the
published SHA-256 integrity claim.

## Known limitations

- Apply all current boundaries in `docs/known-limitations.md`.
- Real external-SIEM product query-back acceptance remains unverified; backend,
  adapter, and packaged operator-workflow proofs do not replace it.
- Current registration adapters do not inject per-call agent/model/task/run
  identity. Missing client-supplied attribution remains explicitly labeled.
- No package-manager distribution is included. The supported public path is the
  GitHub release installer bundle.

## Pending candidate evidence

- adopted version, security-reporting channel, and support boundary;
- exact clean source commit and six canonical CI job results;
- five native RID plus draft-assembly release workflow results;
- exact draft metadata, twelve-asset inventory, eleven downloaded hashes, and
  all PTK/SIEM build identities;
- Windows Authenticode and Defender results;
- macOS Developer ID, notarization, and Gatekeeper results;
- downloaded-artifact install, upgrade/repair, first invoke, restart, recovery,
  SIEM workflow, and uninstall results on every RID;
- exact open limitations, reviewer/finding disposition, withdrawal procedure,
  and final owner release ruling.

The unauthenticated bootstrap gets its final live smoke immediately after a
separately authorized publication. Failure invokes `docs/release-recovery.md`.
