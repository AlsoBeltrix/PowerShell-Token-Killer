# ssu-3: Registration rewrite crosses known unsafe harness surfaces

**Severity**: HIGH — the planned Codex and Grok remove/add migration can damage
or lose a working registration before the replacement is proven.

**Status**: Closed 2026-07-30 — plan abandoned; no implementation remains

**Branch**: `master`

**Commit**: Plan decision recorded in `.agents/decisions.md`; product not started

## Evidence

- `.agents/plans/mcp-side-by-side-upgrade.md:47-50` requires every managed
  registration to be rewritten to the stable launcher.
- `scripts/ptk_init.ps1:363-373` carries the `mhi-12` Codex orphan-subtable
  repair because `codex mcp remove ptk` does not remove tool-approval subtables
  and can leave the entire Codex config unloadable.
- `scripts/ptk_init.ps1:474-479` currently probes a working registration first
  and deliberately leaves it unchanged.
- `scripts/ptk_init.ps1:537-545` documents that the Grok removal path is a
  mirrored shape without live verification.
- `docs/harness-support.md:15` records the corresponding support boundary.

## Predicted observable failure

Run the planned migration against a working Codex registration with persisted
PTK tool-approval subtables, or against an unsupported Grok CLI shape. The old
entry is removed before the replacement handshake succeeds; Codex can become
unable to load its config, and either harness can be left without working PTK.

## What

The plan turns a known hazardous removal primitive and an unverified mirrored
primitive into mandatory upgrade steps without specifying a safe mutation or
rollback protocol.

## Approach

Owner approved the cautious migration on 2026-07-29. The revised plan snapshots
all affected harness files, uses fixture-backed mutation specific to Claude,
Codex, Grok, and Agy, and requires an immediate registered-command handshake.
Codex is updated without its unsafe remove command. Grok live state is untouched
unless the installed CLI first passes a disposable-config add/remove proof. Any
failure restores every changed harness snapshot byte-for-byte.

## Files changed

- `.agents/decisions.md` — durable owner-approved per-harness transaction.
- `.agents/plans/mcp-side-by-side-upgrade.md` — common protocol, exact
  harness-specific mutation, rollback, and guard requirements.
- Review/state records — finding progression only.
- No product file changed.

## Guard proof

Pending a fix. The guard must cover Codex tool-approval subtables, unrelated
Codex configuration, the supported Grok shape, migration failure rollback, and a
successful five-tool handshake through the replacement command.

## Coder dispute (if any)

None.

## Known gaps

Implementation must still prove each installed CLI/config shape and rollback
seam. An unrecognized shape fails closed before activation.

## Reviewer comments

Reviewer: claude /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier
(owner-selected inline). Claude Code `2.1.220`, reviewed
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7..caf467e423105a621b1431302575b242f77791ac`,
verdict `findings`; admitted 2026-07-29.
