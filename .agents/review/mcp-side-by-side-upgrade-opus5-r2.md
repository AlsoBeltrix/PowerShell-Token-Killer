# MCP side-by-side upgrade plan — Claude Opus 5 openreview round 2

**Status:** Valid findings verdict; intake in progress. No implementation is
authorized.

## Review identity

- Base:
  `c4bd2af884faecda81af6eeb9bb3b698d5141bb7`
- Reviewed head:
  `caf467e423105a621b1431302575b242f77791ac`
- Reviewer: Claude Code `2.1.220`
- Model: transcript model `claude-opus-5`; owner-selected inline model maps to
  `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`
- Effort/tier: `max` / frontier owner-selected; no fallback configured
- Invocation: direct Claude Code `--safe-mode`, strict empty MCP configuration,
  detached clean worktree, `Read`, `Glob`, `Grep`, `StructuredOutput`, and
  allowlisted `rtk git` Bash access
- Valid result: exit `0`, 73 turns, 3,521,276 API milliseconds, 3,532,936 wall
  milliseconds, 203 captured JSONL events, zero parse errors, one result event
- Session:
  `13a8f68b-04d7-4a37-9149-5d747e9bb325`

The structured payload matched the required schema, returned
`verdict=findings`, echoed both pinned SHAs exactly, and contained ten
non-empty candidate findings.

## Candidate intake

| ID | Reviewer severity | Candidate | Intake |
|----|-------------------|-----------|--------|
| ssu-1 | HIGH | Stable launcher makes `pwsh` a hard prerequisite | ADMITTED |
| ssu-2 | HIGH | Launcher interposition lacks a designed containment edge | DECLINED |
| ssu-3 | HIGH | Codex/Grok registration rewrite crosses known unsafe surfaces | ADMITTED |
| ssu-4 | MEDIUM | Current transaction can remove the stable launcher path | ADMITTED |
| ssu-5 | MEDIUM | `active.json` replacement lacks a named Windows-atomic primitive | Pending |
| ssu-6 | MEDIUM | Launch-time manifest verification is unbounded | Pending |
| ssu-7 | MEDIUM | Prune repeats the broad no-running-server disruption | Pending |
| ssu-8 | MEDIUM | Retained versions have no operator activation command | Pending |
| ssu-9 | LOW | `muc-7` carries a stale state-file line citation | Pending |
| ssu-10 | LOW | `muc-7` index row lacks reviewer-column identity | Pending |

Candidate findings do not authorize fixes. Every row must pass the codereview
intake gate before work begins.
