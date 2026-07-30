# muc-7: Ordinary installed upgrade strands active MCP conversations

**Severity**: MEDIUM — an upgrade requires terminating the process that owns the
client transport, permanently removing PTK from that conversation until the
whole client session restarts.

**Status**: Closed 2026-07-30 — accepted operational behavior. All PTK processes
must stop before installation, and affected MCP client sessions restart
afterward. No continuity fix remains.

**Branch**: Not started.

**Commit**: Plan `5f5d00d`; product fix not started.

## Evidence

- `scripts/dev-install.ps1:128` defines `Assert-PtkServerNotRunning` because the
  current installer replaces the live root payload.
- `scripts/dev-install.ps1:481` invokes that guard on every ordinary install.
- `scripts/dev-install.ps1:490` activates `bin/`, `src/`, `scripts/`, and
  `VERSION` in place through the current transaction.
- `.agents/state.md:830` records GitHub #11, where Codex retained a stale PTK
  transport after the direct-server cutover.

## Predicted observable failure

On Windows, upgrading an installed PTK requires the old server to exit so its
locked payload can be replaced. The connected client keeps a closed or stale
stdio transport; starting the new binary does not attach it to the original
pipes, and all PTK tools remain unavailable in that conversation.

## What

The installer couples payload replacement to the lifetime of the client-owned
stdio server process.

## Approach

Implement the owner-approved form of
`.agents/plans/mcp-side-by-side-upgrade.md`. The current draft proposes
immutable versioned payloads, a stable per-client launcher, one atomic
activation record for future launches, and no process termination during
ordinary upgrade.

## Files changed

- `.agents/plans/mcp-side-by-side-upgrade.md` — draft implementation and
  verification plan only.

## Guard proof

Not yet run. The plan requires a real packaged Windows acceptance guard that
keeps runtime A and a warm in-flight session alive while runtime B installs,
then proves A remains usable and a new client selects B. Reverting each
implementation slice must make its new guard fail for the intended reason.

## Coder dispute

None.

## Known gaps

The stable PowerShell launcher has not yet proved byte-transparent stdio,
hard-kill teardown, or non-orphaning behavior. Slice 0 is a mandatory
architecture gate.

## Reviewer comments

Owner approved Claude Opus 5 plan openreview on 2026-07-29. Codereview was not
dispatched: `.agents/playbooks/codereview.md` T3 is a pre-dispatch blocker when
guard proof is missing, and a documentation plan cannot truthfully satisfy the
required `guard_confirmed=true` verdict contract.

The owner-approved openreview ran over `c4bd2af..caf467e` with Claude Code
`2.1.220`,
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, effort `max`,
frontier owner-selected inline for the session. Its final verdict envelope was
not recoverable from the bounded capture, and the nonpersistent session could
not perform the one allowed schema-only re-emission. The attempt is fail-closed
and contested; see
`.agents/review/mcp-side-by-side-upgrade-opus5-r1.md`.
