# MCP upgrade continuity — Claude Opus 5 openreview round 1

**Status:** Complete with findings; continuity architecture declined by owner
direction on 2026-07-30. Retained as historical review evidence only.

## Review identity

- Base:
  `d32f2a06e451df02cfa9b63d5c0dda570d1262ec`
- Reviewed head:
  `a308bdadbbda954340f48d63772e72787e9f6990`
- Reviewer: Claude Code `2.1.220`
- Model: configured `opus` alias resolved by the recorded Headroom dispatch to
  `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`
  (`claude-opus-5` inline, session-only owner selection)
- Effort/tier: `max` / frontier; no fallback configured
- Invocation: headless JSON, strict empty MCP configuration, four inspection
  tools, detached clean worktree
- Valid result: exit 0, 63 turns, 1,050,126 API milliseconds, 1,082,601 wall
  milliseconds
- Postflight: exact reviewed SHA and clean review worktree confirmed

Two earlier invocations produced no verdict: one exceeded the local process
timeout and one stopped at its 32-turn guard during tool use. Neither is counted
as a review round.

## Verdict

`findings`

The reviewer returned six candidates. Intake admitted five and declined one:

- `muc-1` — admitted: the guardian option silently reopens a superseded
  architecture.
- `muc-2` — admitted: private-runtime replacement does not solve replacement of
  the guardian or public tool contract.
- `muc-3` — admitted: the daemon option conflicts with the settled stdio,
  no-reattachment topology.
- `muc-4` — declined: versioned subdirectories do not conflict with the single
  `~/.ptk` home when they remain below that root.
- `muc-5` — admitted: Codex dead/stale-transport evidence already contradicts
  client-managed reconnect as a current mitigation.
- `muc-6` — admitted: the review was not discoverable from current state. The
  state pointer is now present; independent finding-fix verification remains
  open.

Per-finding evidence and predicted failures are in
`.agents/review/findings/muc-*.md`; the declined candidate is recorded in
`.agents/review/muc-4.contested.md`.

## Recommendation pending owner ruling

Use immutable side-by-side runtime versions under the single `~/.ptk` home and
an activation pointer for future launches. Never terminate an active installed
supervisor during ordinary upgrade. Existing conversations and warm sessions
stay pinned to their old runtime until they end; new conversations launch the
newly activated version. Retain old payloads until no live process owns them,
then collect them under an explicit retention rule.

Do not commit to the discarded guardian or daemon architectures to obtain
same-conversation hot adoption. A guardian only moves the unsolved boundary
outward: changing the guardian or public contract still requires a new client
connection. Client-managed reconnect is not a PTK mitigation for Codex while
the recorded dead/stale-transport defects remain open.

If transparent adoption of new runtime code inside an already connected
conversation becomes a hard product requirement, reopen it as a separate owner
decision with new evidence and a new approved plan. It is not a consequence of
the non-disruptive installer change.

No further whole-change round is warranted on the unchanged reviewed head:
known findings now enter their recorded downstream flow, and redispatching the
same unprimed question would shop for duplicate findings rather than test a new
delta.
