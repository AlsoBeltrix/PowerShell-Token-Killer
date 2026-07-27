# Production reliability salvage — Claude Opus 5 closure review, round 7

**Status:** `REVISE` — the round-6 finding is closed and the local
evidence/admin boundary is safe; five mechanical plan gaps remain.

## Review identity

- Reviewed commit:
  `d1b883a0e2b2fe049ff6650bc6b7685d4c4f6a7b`
- Reviewed plan blob:
  `6a13e1f8b860cfa150cc2d6521cf829d8a82f98b`
- Parts-bin branch:
  `feature/mcp-resilience-r1` at
  `93e79922a77bd5aab8e2959c69958dd165ea5087`
- Reviewer: Claude Code `2.1.220`, canonical model `claude-opus-5`,
  effort `max`
- Prompt:
  `.agents/review/production-reliability-salvage-opus5-r7.prompt.md`
- Prompt SHA-256:
  `3b3279039c8d50286087f30348c8f534c3b6b18dbc785b7f788f3428016cbb64`
- Invocation: read-only tools, headless, no session persistence, safe mode,
  strict empty MCP configuration, detached clean worktree
- Result: exit 0, 46 turns, 650,647 API milliseconds and 650,529 wall
  milliseconds, no repository edit
- Model metadata reported only `claude-opus-5`; no fallback model was used.
- Preflight and postflight independently confirmed the exact reviewed SHA,
  exact plan blob, and a clean review worktree.
- One denied compound Bash inspection was nonessential. The reviewer obtained
  the cited evidence through permitted reads, and the coder independently
  confirmed every finding with `rg`.

## Verdict

`REVISE`

The round-6 interface-consumer finding is closed. Every direct
`IAuditOtlpExportTransport` consumer is named and split correctly between
delete and edit, moving or stubbing the interface is forbidden, and the exit
criteria reject a retained dead export loop.

The local evidence/admin boundary is also sound. `SecureAuditStorage` remains
required by `OutputStore`; checkpoint, journal, evidence, reconciliation, and
administration code have non-OTLP callers and remain available.

No round-4 or round-5 closure was reopened. Topology decision 1, the
broker-only final Unix runtime, the nonblocking protocol reader, and the
single bounded connection-wide output lane are unchanged.

## Remaining findings

1. **The retained SIEM receiver consumes the vendored proto that Slice 2
   deletes.** Relocate `audit_otlp.proto` and its OpenTelemetry license into
   the SIEM tree, repoint `PtkSiemReceiver.csproj`, remove protobuf tooling
   only from the runtime project, and keep `dotnet test siem/PtkSiem.slnx`
   green. Owner decision 4 must authorize removal of the runtime build
   dependency, not deletion of the shared wire contract.
2. **Three test consumers of deleted export-loop/coordinator types are
   unnamed.** Edit `AuditRuntimeGateTests.cs`, `AuditCallFilterTests.cs`, and
   `AuditOptionsHealthTests.cs` to remove only exporter cases and fakes, and
   forbid test-local stubs of the deleted loop/coordinator types.
3. **Export identity and checkpoint code have opposite dispositions.** Delete
   `ExportConfigurationIdentity.cs` and
   `ExportConfigurationIdentityTests.cs`; retain checkpoint code and tests
   because they have cited local administration, journal, evidence, writer,
   and retirement callers.
4. **The conformance project owns linked sources and main-project
   exclusions.** Delete the project directory and
   `AuditOtlpSiemConformanceTests.cs`, remove both obsolete
   `PtkMcpServer.Tests.csproj` exclusions, retain
   `AuditCoreSchemaTestRecords.cs`, and remove only the producer-conformance CI
   step.
5. **Operator documentation still advertises the deleted producer.** Reduce
   `server/AUDIT-EXPORT.md` to retained local administration and receiver
   wire/ack material, remove producer-enablement guidance, and fix links and
   wording in both READMEs. Leave the held decision-log entry untouched and
   record it as known residue.

## Over-engineering and legacy-state disposition

- `ExportConfigurationIdentity.cs` and its tests are safe to remove because
  their only production caller is deleted.
- Retain the operator-disposition path explicitly as legacy-state
  administration: old checkpoints may still contain a permanent export block,
  no new block can be created after Slice 2, and `PtkAuditAdmin` remains the
  supported way to clear old state.
- No other safe cut was found.

## Owner decisions

- Topology decision 1 remains settled and unchanged.
- Pending decisions remain, in order: R0 contract retirement, cold-job
  removal, mandatory-audit removal.
- Decision 4 must not be presented until its protobuf wording is corrected.
