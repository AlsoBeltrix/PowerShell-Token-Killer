# MCP side-by-side upgrade plan — Claude Opus 5 openreview round 1

**Status:** Contested — no verdict accepted and no finding admitted.

## Dispatch

- Base:
  `c4bd2af884faecda81af6eeb9bb3b698d5141bb7`
- Reviewed head:
  `caf467e423105a621b1431302575b242f77791ac`
- Reviewer: Claude Code `2.1.220`
- Model:
  `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`
- Effort/tier: `max`, frontier owner-selected inline for this session
- Transport: direct Claude Code `--safe-mode`, with MCP disabled and only
  `Read`, `Glob`, `Grep`, and allowlisted `rtk git` Bash access

The matching capability probe completed with the exact reviewed head and
reported the resolved model above. The whole-change review then exited zero
after 1,895.7 seconds and 137 streamed events.

## Verdict-contract failure

The orchestrator's bounded shell capture truncated the stream before exposing
the final verdict envelope. The nonpersistent reviewer session could not be
resumed for the playbook's one schema-only re-emission attempt:

`No conversation found with session ID: 0906963d-b74e-4f1d-b147-9183c0f00953`

The result therefore fails closed under `.agents/playbooks/openreview.md`.
Neither `clean` nor `findings` is accepted, no candidate finding enters intake,
and the draft authorizes no implementation.
