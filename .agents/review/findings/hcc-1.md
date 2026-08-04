# hcc-1: Kimi uninstall deletes a pre-existing custom registration

**Severity**: MEDIUM — uninstall destroys user state ptk never created (data loss of a hand-maintained registration).
**Status**: In progress
**Branch**: —
**Commit**: (pending)

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
