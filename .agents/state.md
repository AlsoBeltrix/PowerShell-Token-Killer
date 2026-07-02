# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- Module is in a clean, reviewed state. HEAD is 9ae82a3 and `origin/master` matches it
  — nothing is committed-but-unpushed. 31/31 Pester tests passing (last run 2026-07-02).
- A 2026-06-27 design session explored a "universal PowerShell wrapper" rearchitecture
  (triggered by `ptk Get-ChildItem` printing help instead of running). No product code
  was written; the owner deferred the build decision. Recorded as an Open decision
  (b1e0550, docs-only). See `.agents/decisions.md`.
- A follow-on 2026-06-27 exploration looked at giving ptk a session-persistent
  warm-runspace backend (a stdio MCP server owning a `Runspace` that loads heavy
  modules / authenticated connections once). Recorded as a second Open decision in
  `.agents/decisions.md`. The core requirement is warm module load with no reload
  tax; unattended (cert-based) auth is the pattern for connection-bearing modules
  like EXO, not itself the requirement (owner correction 2026-07-02).
- 2026-07-02: owner selected the warm-runspace MCP server as the active work item:
  get the server with a persistent runspace working first, then object compression
  via ptk on top. A durable plan is still required before code.
- Owner intent that frames future work: ptk is a personal/team tool complementing the
  owner's `headroom` PoC on Windows/PowerShell work, not an org-wide tool. The build
  trigger is measured benefit on real daily Windows usage, not faith. See
  `.agents/repo-guidance.md` for the generalized framing.
- 2026-07-02: governance refreshed from the AgentGovernanceBootstrap toolkit
  (`AGENTS.md` reconciled to the current template; repo-specific content carved into
  the new `.agents/repo-guidance.md` and `.agents/push-policy.md`).

## Next

- Draft the durable plan for the warm-runspace stdio MCP server (owner's chosen work
  item, 2026-07-02) and get it approved before any code. Settled sub-decisions live
  in `.agents/decisions.md` under that Open Decision - resume there, do not re-derive.
- The "universal PowerShell wrapper" decision (the surface) stays open/deferred; its
  run-anything surface largely falls out of the server's `ptk_invoke` tool anyway.

## Blockers

- None.

## Verification

- `pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"`
  (Pester). See `.agents/repo-map.json` for the recorded command.

## Active Sources

- `AGENTS.md`
- `.agents/repo-guidance.md`
- `.agents/repo-map.json`
- `.agents/decisions.md`

## Unrecorded Repo Memory

- None known.
