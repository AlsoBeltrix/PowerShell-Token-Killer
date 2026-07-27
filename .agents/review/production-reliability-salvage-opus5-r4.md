# Production reliability salvage — Claude Opus 5 multi-session review

**Status:** `REVISE` — process-per-named-session topology accepted; plan
corrections required before owner decision 2 or product implementation.

## Review identity

- Reviewed commit:
  `9c50c38efe1e55f1e08df531ccd2392e4fe498b5`
- Reviewed plan blob:
  `bc122998de0868cc4aa95fefc354c7a6d3e950a4`
- Parts-bin branch:
  `feature/mcp-resilience-r1` at
  `93e79922a77bd5aab8e2959c69958dd165ea5087`
- Reviewer: Claude Code `2.1.220`, canonical model `claude-opus-5`,
  effort `max`
- Prompt:
  `.agents/review/production-reliability-salvage-opus5-r4.prompt.md`
- Prompt SHA-256:
  `e2714b9cbb290b5d9b1e03e1d7dab109f0a51c4750e71aaa2443761bdf476407`
- Invocation: read-only tools, headless, no session persistence, safe mode,
  strict empty MCP configuration, detached clean worktree
- Result: exit 0, 97 turns, 1,110,386 API milliseconds, no repository edit
- Model metadata reported only `claude-opus-5`; no fallback model was used.
- Preflight and postflight independently confirmed the exact reviewed SHA and
  a clean review worktree. Four nonessential compound shell inspections were
  denied by the read-only permission boundary; the reviewer obtained the cited
  evidence through permitted repository reads.

## Verdict

`REVISE`

The reviewer accepted a separate PowerShell process per named session as the
smallest reliable boundary for independently containable Exchange module,
assembly, command-table, connection, environment, and failure state. It also
accepted the explicit connection-local `ptk_session list|open|close` surface,
optional `session` selection, default-session compatibility, fixed session
bound, absence of mutable `select`, and removal of templates/durability.

## Admitted findings

1. **Wrong fixture identity.** `PtkResilienceTestFixture` contains the fake
   guardian/private host, not the retained containment coverage. The latter is
   already in `PtkContainmentTestFixture`. Slice 1 and owner decision 2 must
   delete the former and preserve the latter without a false rename.
2. **Unnamed Unix guard conversion.**
   `UnixGuardianBrokerIntegrationTests.cs` and
   `Native/ptk_guardian_broker_fixture.c` contain the only current Unix
   parent-death/group-leadership coverage. Slice 4 must convert them
   atomically to the worker-broker topology instead of deleting or silently
   leaving guardian assertions.
3. **Audit-removal consumers omitted.** `OutputStore` uses
   `SecureAuditStorage`, and the CI SIEM conformance project consumes the
   runtime OTLP/protobuf types. Slice 2 and owner decision 4 must state what is
   retained and what is retired when the runtime protobuf dependency is
   removed.
4. **Atomic schema guard list incomplete.** Slice 6 must change
   `server/test-handshake.ps1` and the frozen
   `public-tool-contract.json`/digest in the same commit as the live tool
   surface.
5. **Faulted-open behavior undefined.** `ptk_session open` must not silently
   succeed or accidentally choose recovery semantics for a faulted session.
6. **Startup deadline undefined.** Opening or replacing a worker needs one
   bounded startup deadline plus containment cleanup and a deterministic
   terminal state.
7. **Lifecycle/output edges undefined.** The plan must freeze reset-while-busy
   behavior and whether sealed handles survive session close.
8. **Shared output-store lane unacknowledged.** The reviewer recommended a
   per-session foreground storage lane. The repository evidence instead shows
   the global lane intentionally caps potentially uninterruptible filesystem
   work at one task while later capture fails fast and ordinary execution
   continues. The material finding is admitted as a missing explicit
   cross-session output contract; the per-session-lane remedy is contested as
   a reliability regression and must be adjudicated by the next fixed-SHA
   review.
9. **Real Exchange fault proof missing.** The staged on-prem/EXO acceptance run
   must reset or hard-kill one Exchange worker and prove the sibling remote
   connection stays usable without reauthentication.

## Over-engineering adjudication

Final Unix production workers are broker-required. The current unbrokered
process-global containment fallback remains only while the still-live direct
in-process server exists through Slice 5; Slice 6 removes that runtime and the
fallback atomically. An unbrokered final production mode is unsupported and
must fail closed.

## Owner decisions

Topology decision 1 remains settled and independently supported. Decision 2
must be corrected for the real fixture ownership before presentation.
Decisions 3 and 4 remain pending in their existing order. The conservative
session lifecycle semantics above are plan corrections within the settled
topology, not new product scope; the next review must verify them before the
owner receives decision 2.
