# Production reliability salvage — Claude Opus 5 final review

**Status:** `ACCEPT` — final fixed-SHA review closed. No product implementation
is authorized; four owner decisions remain.

## Review identity

- Reviewed commit:
  `4d7f6b379768b6953cdd0ccddc994333b2fa4ad5`
- Reviewed plan blob:
  `9c4938d398035754636bffeac05ee54666cb4ebb`
- Parts-bin branch:
  `feature/mcp-resilience-r1` at
  `93e79922a77bd5aab8e2959c69958dd165ea5087`
- Reviewer: Claude Code `2.1.220`, canonical model `claude-opus-5`,
  effort `max`
- Prompt:
  `.agents/review/production-reliability-salvage-opus5-r3.prompt.md`
- Prompt SHA-256:
  `7a539c1615657bd03ab938eceb0fc785e93cdf0fa2e9b9f71615f03a73fa9108`
- Invocation: read-only, headless, no session persistence, strict empty MCP
  configuration, detached clean worktree
- Result: exit 0, 44 turns, 515,462 API milliseconds, no repository edit
- Preflight and postflight independently confirmed the exact reviewed SHA and
  a clean main and review worktree.

## Verdict

`ACCEPT`

Under the supported first-cut topology, unrelated agents cannot share one
PowerShell runspace: each owns a different MCP connection, supervisor process,
worker process, and runspace. A client that multiplexes agents over one
connection remains explicitly unsupported because PTK receives no caller
identity it can enforce.

## Round-2 closure

### PRS2-B1 — `CLOSED`

Slice 1 now removes the R0/guardian assertions while retaining the fixture's
cross-platform containment, bootstrap, native-binding, and parent-death
coverage. The old project identity disappears only atomically with a renamed
fixture that builds and runs every retained test, so no guard gap is permitted.

### PRS2-M1 — `CLOSED`

The protocol reader now never awaits artifact storage. Quota reservation also
reserves a fixed in-memory queue; chunk admission is nonblocking; a separate
sink owns storage; queue refusal, sink failure, or sink lag at result delivery
switches to discard-and-drain and `recovery=unavailable`. The plan requires a
stalled-sink test proving the ordinary result arrives without replay or worker
replacement.

## Regression and new-finding check

- Reopened prior findings: none.
- New blocking findings: none.
- New major findings: none.
- Additional safe over-engineering cuts: none.

The reviewer found that every remaining component protects a concrete
invariant: public-pipe continuity, warm-state isolation, bounded framing,
truthful failure, process containment, prompt state, or recoverable output.

## Owner decisions confirmed

The four remaining decisions are genuine product choices and remain correctly
ordered:

1. one agent-owned connection/supervisor/worker as the supported topology;
2. retirement of the frozen R0 guardian contract identity while preserving
   retained containment coverage;
3. removal of cold `ptk_job` and invoke's background option;
4. removal of mandatory audit and the runtime OTLP/protobuf build dependency.

## Production confidence

The accepted plan can enforce process/runspace isolation within the supported
topology, at-most-once PTK dispatch, conservative write-attempt outcome
classification, no automatic replay, containment of PTK-owned process domains,
prompt split state, bounded nonblocking artifact transfer, and ordered
shutdown.

It does not promise agent isolation on a multiplexed connection, termination of
escaped Unix or remote effects, client-side non-resubmission, external service
availability, credential validity, or a universal client reconnect.

Production cutover still requires the plan's recorded evidence: the exact full
verification battery, cross-platform containment and fault matrices, resource
soak thresholds, ARM64 clean build, real EXO/Outlook shaping proof, Windows
kill-path closure, intended-harness connection/restart proof, installed-package
canary and rollback, toolkit guidance refresh, and closure or explicit
exclusion of every known blocker.
