# Production reliability salvage — Claude Opus 5 closure review, round 8

**Status:** `ACCEPT` — all five round-7 findings are closed, the legacy-state
disposition is safe, and no blocking or major finding remains.

## Review identity

- Reviewed commit:
  `bf47d60a2ce6f5bfaa17029d78f72e36014b7b90`
- Reviewed plan blob:
  `431aecfebf4c756001db4df85459a650c20e8594`
- Parts-bin branch:
  `feature/mcp-resilience-r1` at
  `93e79922a77bd5aab8e2959c69958dd165ea5087`
- Reviewer: Claude Code `2.1.220`, canonical model `claude-opus-5`,
  effort `max`
- Prompt:
  `.agents/review/production-reliability-salvage-opus5-r8.prompt.md`
- Prompt SHA-256:
  `b8693d0df6d1b5646ed2b1f9e9af6ffbcdf2a497d8f3f0e7457dafe5b58d5657`
- Invocation: read-only tools, headless, no session persistence, safe mode,
  strict empty MCP configuration, detached clean worktree
- Result: exit 0, 68 turns, 754,088 API milliseconds and 752,097 wall
  milliseconds, no repository edit
- Model metadata reported only `claude-opus-5`; no fallback model was used.
- Preflight and postflight independently confirmed the exact reviewed SHA,
  exact plan blob, and a clean review worktree.
- Two denied compound Bash searches were nonessential. The reviewer obtained
  the cited evidence through permitted reads and searches. The coder then
  confirmed at the exact reviewed SHA both the export-type reference inventory
  and the tracked fixture-file inventory with read-only `git` queries.

## Verdict

`ACCEPT`

The corrected plan is dependency-closed for the two implementation slices the
review targeted. All five round-7 findings are closed by repository evidence,
not merely by changed wording. The retained legacy checkpoint administration is
one-way: it can clear a pre-upgrade permanent block, while the new runtime has
no surviving path that can create one.

No round-4 through round-6 closure was reopened. No new runtime, recovery path,
second containment mode, caller identity, persistence, template, daemon, or
shared session was introduced. The one bounded connection-wide output-storage
lane remains intentional; per-session output lanes remain rejected.

## Round-7 closures

1. **Receiver-owned proto:** closed. Slice 2 relocates the vendored proto and
   license into the retained SIEM receiver, repoints the receiver project and
   active source pointer, removes protobuf tooling only from the runtime
   project, and requires `dotnet test siem/PtkSiem.slnx`.
2. **Unnamed test consumers:** closed. All twelve test consumers of the deleted
   export loop, coordinator, transport, and related helper types are now
   dispositioned as deletes or bounded edits. Test-local replacement stubs are
   forbidden.
3. **Export identity versus checkpoint retention:** closed. The export identity
   file and its complete declaration family have no retained consumer and are
   deleted. Checkpoint code remains because retained journal, evidence,
   retirement, reader, and administration paths still use it.
4. **Linked conformance residue:** closed. The producer-conformance project,
   linked producer test, obsolete compile exclusions, and producer-only CI step
   are removed together. Shared local test records and the standalone receiver
   test path remain.
5. **Operator documentation:** closed. Slice 11 removes or marks superseded the
   runtime producer-enablement claims in `server/AUDIT-EXPORT.md` and both
   READMEs while preserving local administration and receiver wire/ack
   material. The held decisions log remains untouched.

## Non-blocking residue and safe cuts

- Two additional old proto-path references remain in the active mini-SIEM plan,
  and one retained receiver source comment cites a runtime producer file that
  Slice 2 deletes. They do not affect a build, acceptance gate, or operator
  instruction, so the reviewer did not make them findings.
- Once the producer is removed, several instance members on
  `AuditClosedSpoolChainReader` become unreachable. Removing them would widen
  the checkpoint-store edit and its operator-disposition proof, so the reviewer
  recommends recording the debt rather than forcing it into Slice 2.
- The receiver project's `InternalsVisibleTo` grant for
  `PtkMcpServer.Tests` becomes dead with the conformance project and is safe to
  remove in the same commit.

## Owner decisions

- Topology decision 1 remains settled: one supervisor connection per unrelated
  agent, several explicitly named sessions per connection, and one long-lived
  PowerShell worker process/runspace per session.
- Pending decisions remain, in order: guardian-era R0 public-contract
  retirement; removal of cold jobs from the first production surface; removal
  of mandatory exact-script audit and the runtime OTLP producer/build
  dependency while preserving the receiver-owned wire contract.

## Production confidence

This acceptance means the plan is coherent and executable; it does not mean the
current product is ready to cut over. Before cutover the plan still requires the
two-agent connection-isolation probe, the exact-SHA cross-platform acceptance
matrix, the 100-replacement resource-settling run with an unchanged sibling,
and staged real Exchange on-premises/Exchange Online sibling-fault proof.
Existing product blockers called out in the plan remain independent work.
