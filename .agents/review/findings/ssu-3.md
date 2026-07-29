# ssu-3: Registration rewrite crosses known unsafe harness surfaces

**Severity**: HIGH — the planned Codex and Grok remove/add migration can damage
or lose a working registration before the replacement is proven.

**Status**: Open

**Branch**: Not started

**Commit**: Not started

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

Pending owner-approved plan revision. Require harness-specific, fixture-backed
registration mutation that preserves unrelated configuration and the old working
entry until the stable target completes a five-tool handshake. Failure in one
harness must not alter another harness registration.

## Files changed

- Review records only; no plan or product change.

## Guard proof

Pending a fix. The guard must cover Codex tool-approval subtables, unrelated
Codex configuration, the supported Grok shape, migration failure rollback, and a
successful five-tool handshake through the replacement command.

## Coder dispute (if any)

None.

## Known gaps

The safe mutation primitive and exact rollback boundary remain plan decisions.

## Reviewer comments

Reviewer: claude /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier
(owner-selected inline). Claude Code `2.1.220`, reviewed
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7..caf467e423105a621b1431302575b242f77791ac`,
verdict `findings`; admitted 2026-07-29.
