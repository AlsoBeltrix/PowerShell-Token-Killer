# Production reliability salvage — Claude Opus 5 review round 2

**Status:** REOPENED — the reviewer returned `REVISE`. Every round-1 blocker,
major finding, minor finding, and over-engineering cut closed. One new blocker
and one new major finding are admitted below. No product implementation is
authorized.

## Review identity

- Reviewed commit:
  `c5124c4f19b199a9907100ccf428cc0df5c96a41`
- Reviewed plan blob:
  `f8a5b082d54c36d0b25eeeb5d1cd483077af3874`
- Parts-bin branch:
  `feature/mcp-resilience-r1` at
  `93e79922a77bd5aab8e2959c69958dd165ea5087`
- Reviewer: Claude Code `2.1.220`, canonical model `claude-opus-5`,
  effort `max`
- Prompt:
  `.agents/review/production-reliability-salvage-opus5-r2.prompt.md`
- Prompt SHA-256:
  `66d9643ef15b286e24fa8bbab1a24bd4dfb10f42c2d0330f932ad6bf5e111220`
- Invocation: read-only, headless, no session persistence, strict empty MCP
  configuration, detached clean worktree
- Result: exit 0, 75 turns, 990,803 API milliseconds, no repository edit
- Preflight and postflight independently confirmed the exact reviewed SHA and
  a clean main and review worktree.

## Verdict

`REVISE`

The reviewer accepted the two-process architecture and its answer to the
owner's isolation question: under the supported first-cut topology, unrelated
agents do not share a runspace because each owns a different MCP connection,
supervisor process, worker process, and PowerShell runspace. A client that
multiplexes agents over one connection is explicitly unsupported and cannot be
identified by PTK.

## Round-1 closure

The reviewer marked every round-1 item closed:

- `PRS-B1` through `PRS-B5`: `CLOSED`
- `PRS-M1` through `PRS-M8`: `CLOSED`
- all six round-1 minor findings: `CLOSED`
- every admitted over-engineering cut: `CLOSED`

The four remaining owner decisions were accepted as genuine product choices in
the correct order: topology, R0 guard retirement, cold job/background removal,
and audit/protobuf removal.

## New blocking finding

### PRS2-B1 — do not delete retained containment tests with the R0 fixture

The plan's Slice 1 removed `PtkResilienceTestFixture` from the solution.
Repository evidence shows that project also owns useful cross-platform tests
and linked production sources for retained worker containment, bootstrap, and
native bindings. Deleting the project wholesale would remove the only current
guard for some code the salvage plan explicitly keeps.

Correction: retire the R0/guardian contract assertions and guardian-only
fixtures inside that project, but retain and rename the project as a focused
worker-containment fixture until every retained test and linked source has
migrated to an equivalent cross-platform test owner. Remove its old solution
identity only atomically with that coverage migration; never create a
coverage gap.

## New major finding

### PRS2-M1 — artifact transfer needs explicit nonblocking backpressure

The plan reserved output quota and defined chunks, seals, and discard-on-write
failure, but did not say how the supervisor keeps draining the worker pipe when
artifact storage is slow. If the pipe reader waits on a disk write, the pipe
can fill, block the worker before it emits the result terminal, and turn an
output-recovery slowdown into a command timeout and `outcome_unknown`.

Correction: the protocol reader never awaits artifact storage. It copies a
chunk into a fixed, preallocated in-memory buffer/queue and continues reading
control frames. If the bounded queue is full or the sink fails, it atomically
switches that artifact to discard-and-drain and reports
`recovery=unavailable`. The sink publishes a handle only after all queued bytes
are written and the length/digest seal verifies. Add a deliberately stalled
sink test proving the ordinary result still arrives without worker replacement
or replay.

## Minor findings

None.

## Over-engineering check

No additional machinery should be cut. The reviewer accepted the removal of
named sessions, `ptk_session`, cold jobs/background mode, prepared operations,
guardian/private host, automatic replay, public incarnation, mandatory audit,
and the real-time idle soak. The remaining supervisor, worker protocol,
platform containment, state split, output transfer, and lifecycle service each
own a concrete production invariant.

## Intake

Both new findings are admitted. They are plan corrections only:

1. preserve and narrow the cross-platform containment fixture instead of
   deleting it;
2. make artifact transfer nonblocking with a fixed preallocated queue and
   discard-and-drain fallback.

The owner decision list does not change.
