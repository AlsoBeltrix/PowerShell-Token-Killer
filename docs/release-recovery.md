# Release recovery and withdrawal

Release assets are immutable. Never replace an archive or checksum under an
existing version or tag, even when the replacement was rebuilt from the same
source commit. Every rebuild has a different build identity and must remain
distinguishable.

All GitHub mutations, workflow dispatches, tag changes, release edits,
artifact removals, and package-index actions in this procedure require the
repository owner's explicit outward-action authorization.

## 1. Freeze and identify

1. Stop publication, announcement, and package-index submission for the
   affected version. Cancel a still-running release workflow if it could create
   more affected artifacts.
2. Record the release URL and state, tag, source commit, workflow run, every
   asset name and SHA-256, signing/notarization evidence, and each artifact's
   `BUILD-PROVENANCE.json` before removing anything.
3. Determine whether the problem affects one build identity, one RID, one
   product, or the source contract shared by every artifact.
4. State user impact and whether continued download or execution creates an
   immediate security or data-integrity risk.

## 2. Draft release

A draft is not repaired in place. Preserve it while diagnosing. After explicit
authorization, delete the entire failed draft and rerun the workflow from the
chosen clean commit. The workflow refuses an existing draft or published tag,
so partial output cannot be silently mixed with a new build.

The replacement draft must again pass all native-RID tests, package provenance,
signature/notarization checks, `SHA256SUMS` verification, and independent
artifact inspection. Its fresh build identities must be recorded.

## 3. Published release

1. Mark the release prominently as withdrawn and identify the affected build
   identities and hashes. Do not move or reuse its Git tag.
2. Publish a public notice with the affected versions/RIDs, observable impact,
   containment steps, uninstall command, and fixed-version status. Do not put
   exploit details or sensitive user evidence in a public notice.
3. If leaving the files downloadable creates material harm, remove the release
   assets or release record only after explicit authorization and after the
   evidence in step 1 is preserved. A removed tag or asset name must never be
   reused for different bytes.
4. Build the repair as a new version. Never upload corrected bytes under the
   withdrawn version, even if no package index cached the original.
5. Withdraw or supersede every package-index entry and announcement that points
   to the affected release. Verify each external index separately; GitHub state
   alone is not proof.

For a signing-key or certificate incident, also follow the issuer's revocation
procedure and record the affected signatures. Deleting GitHub assets does not
revoke already downloaded binaries.

## 4. Replacement verification

Before recommending the replacement for publication:

- run the complete repository battery on its exact clean commit;
- build and execute all five supported RIDs on matching native runners;
- verify all eleven artifact hashes against `SHA256SUMS` after download;
- verify Windows signatures and macOS signing/notarization on the downloaded
  archives;
- run clean install, upgrade/repair, refusal, first-invoke, restart, and
  uninstall checks required by the release-readiness plan;
- confirm the withdrawn version remains clearly identified and its tag was not
  repointed; and
- issue a final owner recommendation naming the exact commit, version, workflow
  run, hashes, and build identities.

Use the [release-notes template](release-notes-template.md) so the replacement
and any withdrawn predecessor remain identifiable from a cold start.
