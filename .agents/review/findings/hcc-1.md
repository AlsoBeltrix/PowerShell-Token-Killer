# hcc-1: Kimi uninstall deletes a pre-existing custom registration

**Severity**: MEDIUM — uninstall destroys user state ptk never created (data loss of a hand-maintained registration).
**Status**: Verified
**Branch**: —
**Commit**: `11df290`

## Evidence
`scripts/ptk_init.ps1` kimi leg: install leaves an existing `mcpServers.ptk`
entry as-is (probe-first, mhi-8), but uninstall (`$servers.Remove('ptk')`,
line 941) removes any such entry unconditionally — including a custom one
pointing somewhere other than `~/.ptk`.

## Predicted observable failure
A user with a hand-rolled kimi ptk registration runs install (entry
untouched) then uninstall and permanently loses their custom registration.

## What
Asymmetric ownership: the leg refuses to touch a custom entry on install but
deletes it on uninstall.

## Approach
Uninstall now removes the `mcpServers.ptk` entry only when its `command`
equals the payload binary this leg installs (`$binary`); any other entry is
custom user state and gets a warning naming the manual removal instead of
deletion (install's leave-as-is probe, made symmetric).

## Files changed
- `scripts/ptk_init.ps1` (kimi leg uninstall branch) — ownership check before removal
- `tests/PwshTokenCompressor.Tests.ps1` — hcc-1 guard test

## Guard proof
- `tests/PwshTokenCompressor.Tests.ps1::'kimi leg uninstall leaves a custom registration untouched (hcc-1)'` — with the fix stashed the custom entry is deleted and the test FAILS; restored it PASSES (verified 2026-08-04).

## Coder dispute (if any)
—

## Known gaps
codex/grok uninstalls have the same shape via their CLIs; out of scope here
(the CLI owns those removals).

## Reviewer comments
(intake) Reviewer: codex / gpt-5.6-sol / xhigh (inline, session-only) / standard — generation pass over 19201a1..092df3b, codex-cli 0.146.0, verdict findings (5), capability_ok true, 2026-08-04.

(verification) Reviewer: codex / gpt-5.6-sol / high / standard — codex-cli 0.146.0 (model from the -m pin; the JSONL stream emits no model id). reviewed_sha 11df2909c97021df1bdcdca964b8aea88ebffd62, base_sha c81715bef6d079af9ad28e0de6422f8511c5f84a, guard_confirmed true, capability_ok true, verdict **accepted**, 2026-08-04T22:59Z. Comments: "Guard behaved PASS → reverted FAIL → restored PASS. sandbox blocked git checkout because shared git index outside writable worktree, so exact HEAD blob restored with git show; worktree clean." / "The ownership check preserves custom Kimi registrations while retaining removal payload-owned entry. All 8 focused Kimi-leg tests passed; no adjacent regression found." Record committed as part of the verification history.
