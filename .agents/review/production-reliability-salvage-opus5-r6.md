# Production reliability salvage — Claude Opus 5 closure review, round 6

**Status:** `REVISE` — the containment and output-lane findings are closed;
one bounded Slice 2 consumer-inventory correction remains.

## Review identity

- Reviewed commit:
  `0ca5fbcc4c3ffe9c1fefb5080970d2d0c80e7ddb`
- Reviewed plan blob:
  `bed6177d38b7d291c9392b323d57933d73fe174d`
- Parts-bin branch:
  `feature/mcp-resilience-r1` at
  `93e79922a77bd5aab8e2959c69958dd165ea5087`
- Reviewer: Claude Code `2.1.220`, canonical model `claude-opus-5`,
  effort `max`
- Prompt:
  `.agents/review/production-reliability-salvage-opus5-r6.prompt.md`
- Prompt SHA-256:
  `abb65e7dfca7af8b0964b8e665948f94122e5a13f42d4ca4e96a0df65812c0f3`
- Invocation: read-only tools, headless, no session persistence, safe mode,
  strict empty MCP configuration, detached clean worktree
- Result: exit 0, 40 turns, 641,910 API milliseconds and 639,764 wall
  milliseconds, no repository edit
- Model metadata reported only `claude-opus-5`; no fallback model was used.
- Preflight and postflight independently confirmed the exact reviewed SHA,
  exact plan blob, and a clean review worktree.
- One denied compound Bash inspection was nonessential. The reviewer obtained
  the cited evidence through permitted reads, and the coder independently
  confirmed the interface-consumer inventory with `rg`.

## Verdict

`REVISE`

Round-5 findings 2 and 3 are closed. The plan now prevents close/reopen while
an old containment domain is unconfirmed, keeps the alias reserved, and guards
that no replacement can overlap it. The one connection-wide output lane now
retains its one-task cap while healthy contention waits only the existing
bounded capture interval.

No round-4 closure was reopened. The final Unix runtime remains broker-only,
the protocol reader remains nonblocking and never awaits storage, and no
recovery loop, second containment mode, caller identity, persistence,
templates, daemon, or shared session reappeared.

## Remaining finding

**Slice 2's runtime OTLP consumer inventory is still incomplete.**

The plan deletes `AuditOtlpHttpExporter.cs`, which also declares
`IAuditOtlpExportTransport`, but it does not name every production and test
consumer of that interface. Deleting only the seven files currently listed in
Slice 2 cannot compile and risks preserving a dead exporter abstraction merely
to satisfy those consumers.

The correction must remove the complete runtime OTLP export path:

- production consumers `AuditExportCoordinator.cs`,
  `AuditBootExportSource.cs`, `AuditClosedSpoolExportPump.cs`, and the anchored
  export construction in `AuditRuntimeResources.cs` and `Program.cs`;
- exporter-specific tests `AuditOtlpExportCompositionTests.cs`,
  `AuditClosedSpoolExportPumpTests.cs`, and
  `AuditExportCoordinatorTests.cs`;
- transport stubs, without deleting unrelated coverage, in
  `AuditLiveSpoolReaderTests.cs`, `AuditAnchoredRuntimeTests.cs`,
  `AuditEvidenceRetentionTests.cs`, and
  `AuditEvidenceOrphanReconcilerTests.cs`;
- the already named mapper, exporter, protobuf, receiver fixture, and their
  direct tests.

Slice 2 must exit with no `IAuditOtlpExportTransport`, `Grpc.Tools`, or
`Google.Protobuf` reference in the runtime project. Owner decision 4 must name
the whole anchored OTLP export path rather than only the mapper/exporter,
protobuf, and their main-project tests.

## Regression and over-engineering check

- Reopened prior findings: none.
- New blocking or major findings: none beyond the remaining Slice 2 inventory
  defect.
- Additional safe cut: remove the otherwise unreachable anchored OTLP export
  pipeline as part of the same correction; do not leave compiled dead code.
- Topology decision 1 remains settled.
- Pending owner decisions remain, in order: R0 contract retirement, cold-job
  removal, mandatory-audit removal.
