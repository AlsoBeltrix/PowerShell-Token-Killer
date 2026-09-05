# rr-8: Uploaded checksum manifest is not portable

**Severity:** HIGH — standard checksum-tool verification fails on the draft.
**Status:** CLOSED — owner-authorized draft correction passes fresh download checks.
**Scope:** `scripts/assemble-draft-release.sh` and its shell regression suite.

All six release jobs in `33935468032` passed source `6ab7040` and created
unpublished draft `383097364`, `v0.3.0-rc.2`. Downloaded `SHA256SUMS` retains
CRLF on the four Windows archive entries and one separator space on the
installer entry. macOS `shasum -a 256 -c SHA256SUMS` exits 1: it treats CR as
part of each Windows filename and reports one improperly formatted line.

This differs from `rr-7`: that fix makes the assembler correctly read native
checksum files, but it does not normalize the manifest it subsequently uploads.
The PowerShell installer parses whitespace and removes line endings, so this
is not evidence of corrupt archives or a failed installer hash comparison.
Independent full byte/provenance verification remains separately recorded in
`.agents/release-candidate-0.3.0-rc.2.md`.

Under standing known-broken repair authority, after verifying the exact input
inventory and every archive hash, emit a canonical SHA256SUMS with LF endings
and two spaces between hash and filename. Preserve all existing hash, name,
repository, immutability, and API-failure guards. Add a real standard checksum
consumer assertion for both LF and mixed native manifests; prove red/green
and temporary-revert failure before closure.

The initial handoff withheld draft mutation pending explicit owner approval.
The owner subsequently approved correcting the unpublished rc.2 checksum list
without rebuilding or replacing its binaries. That one-off exception is
canonical in `.agents/release-candidate-0.3.0-rc.2.md`. It does not authorize
publication, a source/tag change, live installation, or inclusion of the newer
`eff24a5` harness fix, and does not relax the standing immutability rule.

## Repair and proof

The assembler now accumulates canonical entries only after each input hash
matches the actual archive. After all inventory/hash and existing-release/API
guards pass, it emits exactly eleven LF-terminated lines with lowercase hashes
and two separator spaces. No archived product bytes are changed.

Regression tests execute every available GNU `sha256sum` and Perl `shasum`
consumer and require eleven successful file checks, on both ordinary LF and
mixed native manifests. Before repair, GNU rejected CR-bearing Windows names;
Perl additionally skipped the malformed one-space installer entry. The guards
pass after repair, fail when it is temporarily reverted, and pass after
restoration. Existing wrong-hash, duplicate-name, embedded-CR, inventory,
extra-asset, immutable-release, and failed-API checks remain green. ShellCheck,
actionlint, and `git diff --check` pass.

Independent downloaded verification of rc.2 succeeded for all twelve GitHub
digests, eleven archive/installer hashes, safe extraction, exact bundle files,
ten unique clean build identities, and binary version agreement.

After explicit owner approval, only the draft's checksum asset was replaced
with LF/two-space canonical output. Its original bytes and metadata were
preserved. Fresh downloads passed GNU `sha256sum` and Perl `shasum`, each
checking all eleven files, plus the complete twelve-digest/ten-identity proof.
Every archive asset ID, size, and digest remained unchanged. Draft `383097364`
is still unpublished at the same source and tag name, and no tag ref was
created. Exact before/after checksum identities are in the canonical candidate
assets inventory. The formatter repair at `ac90719` also passed all six CI
jobs in `33937538564`. No packaging check remains open for this finding.
