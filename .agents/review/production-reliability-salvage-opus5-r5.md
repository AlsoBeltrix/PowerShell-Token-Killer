# Production reliability salvage — Claude Opus 5 closure review

**Status:** `REVISE` — all nine round-4 findings closed and the global output
lane accepted; three new local plan corrections remain.

## Review identity

- Reviewed commit:
  `253667519e82dcd8a1b075fdc0558740b5c943ab`
- Reviewed plan blob:
  `ce9b3192fa5128f95f2d5c1a596e2709ed377611`
- Parts-bin branch:
  `feature/mcp-resilience-r1` at
  `93e79922a77bd5aab8e2959c69958dd165ea5087`
- Reviewer: Claude Code `2.1.220`, canonical model `claude-opus-5`,
  effort `max`
- Prompt:
  `.agents/review/production-reliability-salvage-opus5-r5.prompt.md`
- Prompt SHA-256:
  `7327e41130ab61bfc58752470b6285d12f588f305b5092bc3bb13d50ed463c0e`
- Invocation: read-only tools, headless, no session persistence, safe mode,
  strict empty MCP configuration, detached clean worktree
- Result: exit 0, 86 turns, 851,209 API milliseconds, no repository edit
- Model metadata reported only `claude-opus-5`; no fallback model was used.
- Preflight and postflight independently confirmed the exact reviewed SHA and
  a clean review worktree. Denied compound shell inspections were nonessential;
  the reviewer obtained the cited evidence through permitted reads.

## Verdict

`REVISE`

Every round-4 finding is closed. The reviewer independently accepted
process-per-named-session, the simplified lifecycle contract, the corrected
fixture/guard/audit ownership, the real Exchange sibling-fault proof, and the
broker-only final Unix runtime.

## Global output-lane adjudication

`ACCEPTED`

One connection-wide foreground storage lane correctly caps potentially
uninterruptible filesystem work at one task per supervisor. A per-session lane
would permit up to eight stuck tasks against the same output root without
improving PowerShell state isolation. Per-session quotas remain independent,
and output-capture degradation never reruns or fails the user command.

## New findings

1. **Slice 2 guard/source inventory is incomplete.** Audit removal must update
   the audit-evidence and fail-closed scenarios in
   `server/test-handshake.ps1` and remove the runtime OTLP mapper/exporter,
   protobuf, receiver fixture, and their main-project tests in the same commit.
2. **Close can bypass unconfirmed containment.** A faulted session whose old
   containment domain is still unconfirmed has zero active leases; the current
   text could allow close, remove its reserved alias, then reopen a replacement
   over the old domain. Close must refuse until that exact old domain is
   confirmed empty, and the alias must remain reserved.
3. **Healthy output-lane contention drops capture immediately.** Keep the
   single-task cap, but let a contender wait only the existing bounded
   prepare/seal capture interval before returning `recovery=unavailable`.
   Prove two healthy concurrent sessions both publish handles and a wedged lane
   still times out without starting a second storage task or failing a command.

## Regression and over-engineering check

- Reopened prior findings: none.
- Additional safe cuts: none.
- Topology decision 1 remains settled.
- Pending owner decisions remain, in order: R0 contract retirement, cold-job
  removal, mandatory-audit removal.
