# MCP resilience R0 implementation review: resolved

**Status:** Resolved 2026-07-16 — the later direct-route review returned a
valid accepted verdict. The failed attempts below remain historical evidence.

**Recorded:** 2026-07-16T04:15:54Z

## Fixed target

- Base: `215e10fcfede9cf200b21b3c0cda95d4fc712ddd`
- Head: `c1d809f51b74b97a04a13fe32a5b72afeb4d15af`
- Reviewer: Claude Code 2.1.211, model `claude-fable-5`, effort `max`

## Evidence

A bounded JSON smoke probe completed successfully before dispatch. Its
`modelUsage` named `claude-fable-5` and contained no Opus model, proving the
requested route was active. The implementation review used the same explicit
model and effort, no fallback model, exact fixed SHAs, the reviewloop verdict
schema, and a disposable detached-worktree instruction.

The first review attempt reached its 30-minute bound with exit 124 and no
stdout. The playbook's one permitted retry restated the verdict schema and
reached its 45-minute bound with exit 124 and no stdout. Because neither
attempt returned an envelope, neither has a verdict, matching reviewed/base
SHAs, or literal `guard_confirmed=true`. The orchestrator therefore rejected
both attempts. Their detached worktrees were porcelain-clean and removed; the
coder worktree stayed clean.

## Independent evidence that does not replace the gate

The exact implementation passed its full local battery and direct native
macOS/Windows R0 checks, recorded in `.agents/machines.md`. An independent
in-repo audit found no remaining R0 contract, schema, hash, mapping, or
platform-evidence blocker. Those are implementation evidence, not a substitute
for the explicitly required Claude Fable fixed-SHA review.

## Resolution

After the owner removed the compression proxy, a fresh bounded smoke probe
through `https://api.anthropic.com` completed successfully and its
`modelUsage` reported `claude-fable-5` plus the Haiku helper, with no Opus
model. Claude Code 2.1.211 then reviewed the same exact base/head at effort
`max` and returned exit zero with the schema-constrained verdict `accepted`,
literal `guard_confirmed=true`, and both full SHAs matching dispatch at
2026-07-16T06:15:48Z.

The reviewer independently proved three guards red then green: Sentinel static
projection under `HostGeneration` corruption; the post-write
`outcome_unknown` boundary under a false pre-dispatch phase mapping; and Unix
hard containment when worker-group `SIGKILL` was weakened to `SIGTERM`. It
restored every mutation byte-exactly, verified an empty porcelain status and
`git diff HEAD`, and removed its disposable worktree. The Ajv strictTypes
warnings were confirmed non-semantic by 17 targeted probes. One LOW,
non-blocking Unix fixture-cleanup advisory is recorded in
`.agents/review/index.md` and does not reopen R0.
