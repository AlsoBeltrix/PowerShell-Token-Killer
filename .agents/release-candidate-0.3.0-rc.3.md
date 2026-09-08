# Release candidate 0.3.0-rc.3

## Authority and identity

The owner's 2026-09-08 instruction authorizes completing the new GitHub and
package-manager releases. Existing running sessions must survive. See
`.agents/plans/release-0.3.0-rc.3.md` for scope and external prerequisites.

- Version: `0.3.0-rc.3`; intended tag `v0.3.0-rc.3`.
- Product source: `e823781b2127c7f3b7c2e01eb5daf9da19e34a38`.
- Exact-source CI: `34275489232`, all six jobs passed.
- Native release workflow: `34276660442`, in progress.
- Build branch on canonical `origin`: `release/0.3.0-rc.3`, pointing at that exact source. <!-- lint: allow (Git branch, not a repository path) -->
  GitHub rejected a raw-SHA workflow dispatch, so this named build branch was created;
  no release tag was created by the operator.
- rc.2 is unchanged. No existing installed runtime was replaced or stopped.

## Current evidence

Both native Linux release legs passed. macOS signed/notarized successfully and
is finishing packaged SIEM workflows. Windows signing is in progress. No new
draft or public release is claimed yet; asset IDs, hashes, identities, and
downloaded native checks will be recorded after assembly.

The downloaded verification harness landed separately at `128cdf8`. It tests
published/downloaded product bytes against the source above and never rebuilds
the product. Five checksum/inventory guards passed; removing the archive hash
comparison made the tampered-download test fail, and restoring it passed.
Read-only fresh downloads of rc.2's Mac/receiver/installer artifacts exercised
the downloader and digest/provenance checks successfully without altering rc.2.

## Still required

- Complete native draft assembly and all twelve-asset integrity checks.
- Native downloaded install, signatures/notarization, CLI, runtime, SIEM
  workflows, and opt-in uninstall on all five RIDs.
- Security reporting access/contact, final support policy and release notes,
  publication, and public bootstrap proof.
- Publish validated Homebrew and Scoop manifests; complete winget upstream
  acceptance; publish AUR using the owner's registered account/key.
- Verify actual availability in each channel. Submission alone is not release.
