# rr-7: Draft assembly rejects Windows checksum line endings

**Severity:** HIGH — all five native candidates pass, but no draft can assemble.
**Status:** FIXED LOCALLY; canonical candidate rerun pending.
**Scope:** `scripts/assemble-draft-release.sh` and its existing shell tests.

Release run `33933424682`, source `a57ae57`, passed all five native jobs,
including both signed Windows install, 32-check product/Defender, and packaged
SIEM workflow gates. Draft job `101221277126` failed before GitHub mutation:
`SHA256SUMS must identify 'ptk-0.3.0-rc.2-win-x64.zip' exactly once`.

The downloaded win-x64 checksum file has the correct archive name/hash and
ends in bytes `0D-0A`. The workflow concatenates native checksum files, while
the assembler's awk filename comparison retains the terminal carriage return.
This is a line-ending parsing defect, not a failed hash or production guard.

Under standing known-broken repair authority, teach checksum lookup to strip
only a terminal carriage return before parsing each line. Keep exact inventory,
one-entry-per-asset, hash recomputation, canonical-repository, immutable-release,
and fail-closed API guards. Add mixed Windows/Unix checksum fixtures that fail
before the fix and pass after it; also require malformed/duplicate/tampered
Windows entries to fail before any GitHub call. Rebuild through the canonical
workflow from the committed repair; do not publish or replace the live install.

The mixed-CRLF/LF regression failed with the exact exit-66 filename refusal
before the fix, passed after the two awk lookups stripped only terminal CR,
failed again when the fix was temporarily reverted, and passed after restore.
Wrong hashes, duplicate Windows names, and embedded carriage returns all fail
before any GitHub call. Existing immutability, missing-checksum, extra-asset,
installer-bundle, and fail-closed query checks remain green. ShellCheck,
actionlint, and `git diff --check` pass. Signed candidate assembly remains the
native integration proof; no guard was bypassed and no asset was replaced.
