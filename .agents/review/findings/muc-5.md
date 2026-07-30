# muc-5: Client-managed reconnect is already contradicted for Codex

**Severity**: LOW — treating reconnect as merely unmeasured can distort the
option ranking despite observed failures in a target client.

**Status**: Closed 2026-07-30 — continuity architecture abandoned by owner;
no fix or implementation remains.

**Branch**: Not started.

**Commit**: Not started.

## Evidence

- `.agents/review/mcp-upgrade-continuity-options.md:116` presents automatic
  client reconnect and schema refresh as viable when documented and verified.
- `.agents/state.md:821` records GitHub #9, where calls hung for 120 seconds on a
  dead MCP transport.
- `.agents/state.md:830` records GitHub #11, where Codex retained a stale PTK
  transport after direct-server cutover.
- `server/README.md:249` states that `PTK_DIRECT` remains the way through until
  the harness has replaced the dead transport, which PTK itself cannot restart.

## Predicted observable failure

The client-managed option is ranked as an unknown rather than as disproven for
Codex, so the cheapest-looking option may be selected even though the observed
target client remains permanently attached to the dead transport.

## What

The options brief omits existing target-client evidence and does not distinguish
clients that failed from clients that have not yet been measured.

## Approach

Treat client-managed recovery as contradicted for Codex until #9 and #11 close.
Maintain a per-client evidence matrix for any other supported harness.

## Files changed

None. Intake record only.

## Guard proof

Manual evidence check against the recorded issues and server operator guidance.

## Coder dispute

None.

## Known gaps

Reconnect behavior for other MCP clients remains unmeasured.

## Reviewer comments

Source openreview: Claude Code `2.1.220` /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / `max` /
frontier, owner-selected inline for this session. Reviewed
`d32f2a06e451df02cfa9b63d5c0dda570d1262ec..a308bdadbbda954340f48d63772e72787e9f6990`.
Intake verdict: **ADMITTED**.
