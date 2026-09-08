# Release candidate 0.3.0-rc.3

## Authority and identity

The owner's 2026-09-08 instruction authorizes completing the new GitHub and
package-manager releases. Existing running sessions must survive. See
`.agents/plans/release-0.3.0-rc.3.md` for scope and external prerequisites.

- Version: `0.3.0-rc.3`; intended tag `v0.3.0-rc.3`.
- Product source: `e823781b2127c7f3b7c2e01eb5daf9da19e34a38`.
- Exact-source CI: `34275489232`, all six jobs passed.
- Native release workflow: `34276660442`, all five native legs and draft
  assembly passed.
- Draft release: `385067589`, marked prerelease, twelve assets.
- Build branch on canonical `origin`: `release/0.3.0-rc.3`, pointing at that exact source. <!-- lint: allow (Git branch, not a repository path) -->
  GitHub rejected a raw-SHA workflow dispatch, so this named build branch was created;
  no release tag was created by the operator.
- rc.2 is unchanged. No existing installed runtime was replaced or stopped.

## Current evidence

All release legs passed, including native CLI/package, signing, Windows
Defender, macOS notarization, MCP/product, and SIEM workflow gates. Fresh local
downloads independently passed all twelve GitHub digests, eleven archive
hashes, and ten unique clean identities. The Mac workflow archive also passed
the new local downloaded-product harness: every Mach-O signature/online ticket,
staged/installed handshakes, CLI setup/removal, SIEM workflows, all 32 product
checks, and actual disposable uninstall. It was byte-identical to the Mac
release asset. The candidate is still a draft.

The first uploaded-download workflow, `34279698794`, failed before product
execution: its contents-read token could not see the unpublished draft.
The local authenticated download succeeded against the same metadata. The
workflow now grants the token the push visibility GitHub requires for drafts;
its operations remain downloads/verification. Run `34279949935` then passed
the full inventory and all three Unix native product jobs. Both Windows jobs
validated all 891 signatures but the disposable-account test inherited the
runner's inaccessible TEMP directory. The test launcher now initializes the
new account's profile/temp environment before launching the proof. The
Windows native rerun remains required; release assets are unchanged.

The downloaded verification harness landed separately at `128cdf8`. It tests
published/downloaded product bytes against the source above and never rebuilds
the product. Five checksum/inventory guards passed; removing the archive hash
comparison made the tampered-download test fail, and restoring it passed.
Read-only fresh downloads of rc.2's Mac/receiver/installer artifacts exercised
the downloader and digest/provenance checks successfully without altering rc.2.

## Still required

- Native downloaded install, signatures/notarization, CLI, runtime, SIEM
  workflows, and opt-in uninstall on all five RIDs.
- Final factual release notes, publication, and public bootstrap proof.
  Existing publishing credentials and the owner's correction to the earlier
  setup assumptions are recorded in the execution plan.
- Publish validated Homebrew and Scoop manifests; complete winget upstream
  acceptance; publish AUR using the owner's registered account/key.
- Verify actual availability in each channel. Submission alone is not release.
