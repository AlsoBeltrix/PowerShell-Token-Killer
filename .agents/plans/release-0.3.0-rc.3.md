# Release 0.3.0-rc.3 and package distribution

## Authority and scope

On 2026-09-08 the owner directed: "and the new version's GH release and
package manager releases are done? if not, do not stop until they are."
This authorizes the necessary implementation, verification, commits, pushes,
new release publication, and package submissions. It supersedes the earlier
rc.2 task's no-publication scope. Existing running PTK sessions must survive;
the frozen rc.2 draft and its archives remain immutable.

The new version continues the current prerelease series as 0.3.0-rc.3 (verify
the tag is unused before building). It includes buffered audit startup and
the native redirect hook, plus the entry point required for package installs.
Public limitations must remain explicit; a prerelease label does not waive
the signed-artifact and downloaded-product gates in release-readiness.md.

## Package plan amendment

- D1: use the recommended native CLI in the existing binary, dispatched after
  internal worker classification. Bare invocation and `serve` retain MCP
  stdio behavior. `version`, `doctor`, `init`, and `uninstall` run before audit
  and worker startup. Setup hosts the shipped registration script in the
  already-embedded PowerShell engine; hooks remain standalone native code.
  Keep PtkMcpServer for existing registrations and add a native `ptk` alias.
- D2: declare upstream RTK dependencies. Verified 2026-09-08: Homebrew core
  `rtk` 0.48.0, Scoop Main `rtk`, and winget `rtk-ai.rtk` exist. The original
  August inventory is stale. AUR RPC also confirms `rtk` and `rtk-bin` 0.48.0-1
  maintained by bbj; declare `rtk`, which the binary package provides.
- D3: publish through existing owner-controlled `roethlar/homebrew-tap` and
  `roethlar/scoop-bucket`, submit winget via `roethlar/winget-pkgs`, and
  publish `ptk-bin` to AUR when registered account access is available.
  No additional ecosystems are in this release.
- D4: the checksum-verified standalone installer bootstrap is already built;
  retain it and prove it against the newly published release.
- D5: native `init` and `uninstall` require explicit `--agent <names>` or
  `--all-agents`. Package installation only places files. Registration and
  unregistration happen when the user runs the command. Package managers own
  payload removal; `ptk uninstall` removes the selected harness integration.
- Package-managed registration paths must survive manager version-directory
  changes. A package-owned `PACKAGE-HOME` text file may name its stable
  absolute root (Homebrew opt, Scoop current). Ordinary archives use their
  own root. An explicit `--home` overrides this for inspection/testing.

## Execution and evidence

1. Implement and prove native CLI dispatch, hosted setup error propagation,
   explicit registration selection, and packaged alias. Run the repository
   verification entry points and preserve red/green evidence for new tests.
2. Add reproducible manifest generation and native downloaded-release
   verification. Test release metadata, checksums, provenance, signing,
   install/product/SIEM workflows, and isolated uninstall on all five RIDs.
3. Commit/push each slice; require green native CI at the release product
   tree. Build a new immutable draft from its exact clean source commit.
4. Finalize support/security policy and exact release notes from actual
   evidence; publish, then prove the public bootstrap.
5. Generate package manifests from that release's verified asset hashes,
   validate/install them, publish the tap/bucket/AUR updates and winget PR,
   and verify each channel's actual availability. A submitted PR is pending,
   not a completed winget release. Keep update automation with the manifests.

## External prerequisites (open)

- Canonical repository private vulnerability reporting is disabled. Current
  GitHub user roethlar has push but no admin/maintain permission. The owner
  has been asked to enable private reporting via an admin or provide a
  monitored private contact before SECURITY.md can promise that route.
- AUR rejected the available SSH key. The owner has been asked for the AUR
  account name and registered key path. No AUR publication is claimed.

## Progress

Initial inspection: canonical master is bb1f43e; GitHub's latest public
prerelease is rc.1, and the rc.2 draft predates the September startup/hook
repairs. Neither the CLI nor PTK package-manager manifests existed. No
release is complete at this checkpoint.

### Native CLI slice verified, 2026-09-08

Implemented the dispatch, embedded setup host, stable package-root marker,
native apphost alias, public command documentation, and packaged registration
proof. The internal worker branch remains the first executable action.
Setup requires explicit harness selection; missing RTK refuses registration
but does not prevent version inspection or unregistration.

Local macOS verification: server 1,392 passed; SIEM 357 passed; Pester 118
passed with 3 platform skips; SIEM lifecycle passed. A fresh disposable layout
passed the complete handshake. Packaged CLI proof passed with no shell on
PATH: exact identity, selection refusal, missing-RTK refusal, dry-run, real
Kimi registration/native hook, and removal preserving foreign MCP state and
package files. Staged and activated handshakes and the direct-checkout
registration handshake passed. Dependency scan reported no vulnerable server
packages; actionlint and git diff --check passed.

Red/green: temporarily removing CLI dispatch caused the selection/version
guards to fail; restoring it passed all seven CLI tests. Hosted `exit 7`
initially returned 1; preserving the script-boundary LASTEXITCODE repaired
that failure. The first packaged proof accidentally joined two discovered
RTK paths into one environment value; selecting the first application fixed
the test harness, and all packaged checks then passed. Existing sessions and
installed binaries were not replaced or stopped.

The new downloaded-release verification scripts are a separate unfinished
slice; they are not part of the CLI validation claim.

### Downloaded-release verification slice

Fresh rc.2 Mac/receiver/installer downloads exercised the new read-only
downloader and verifier: exact candidate source, twelve-asset inventory,
eleven canonical manifest entries, GitHub digests, all selected archive hashes,
and both clean native identities passed. Five integrity guards pass locally.
The new workflow runs the full inventory plus independent native downloads on
all five RIDs. Its scripts come from the workflow ref, while `source` pins the
product identity being verified; no checkout-built product is used. Windows
uses a disposable standard account to retain the real non-elevated uninstall
gate. Native downloaded-product execution remains pending the new draft.
