You are the independent senior architecture reviewer for PowerShell Token
Killer (PTK), returning for fixed-SHA closure of the multi-session salvage
plan. Review exact commit
bf47d60a2ce6f5bfaa17029d78f72e36014b7b90 and exact plan blob
431aecfebf4c756001db4df85459a650c20e8594 in this detached clean checkout.
This is READ-ONLY. Do not edit files, commit, install, push, or mutate external
systems.

The owner-settled topology is unchanged: one MCP stdio connection and
supervisor per unrelated agent; several explicitly named sessions per
connection; one long-lived PowerShell worker process/runspace per session;
session names are routing labels, not identities; no persistence, templates,
daemon, shared session, or guardian/private-host recovery.

Review history:

- `.agents/review/production-reliability-salvage-opus5-r4.md`
- `.agents/review/production-reliability-salvage-opus5-r5.md`
- `.agents/review/production-reliability-salvage-opus5-r6.md`
- `.agents/review/production-reliability-salvage-opus5-r7.md`

The exact corrected plan is:

- `.agents/plans/production-reliability-salvage.md`

Read `AGENTS.md`, `.agents/repo-guidance.md`, the plan, the four review
records, and only the repository files needed to verify closure. Treat
`feature/mcp-resilience-r1` only as a parts bin.

Round 7 closed round 6's finding and accepted the local evidence/admin
boundary. It returned five mechanical findings. Verify that the exact plan now:

1. Relocates `audit_otlp.proto` and its license into the retained SIEM receiver,
   repoints its project and the active mini-SIEM source pointer, removes
   protobuf tooling only from the runtime project, and runs
   `dotnet test siem/PtkSiem.slnx`.
2. Names `AuditRuntimeGateTests.cs`, `AuditCallFilterTests.cs`, and
   `AuditOptionsHealthTests.cs` as edit targets, preserving unrelated coverage
   while forbidding test-local stubs of the deleted loop/coordinator types.
3. Deletes dead `ExportConfigurationIdentity` code and tests while retaining
   checkpoint code with its cited non-OTLP callers.
4. Removes the linked producer-conformance source and obsolete compile
   exclusions with its project and CI step, while retaining
   `AuditCoreSchemaTestRecords.cs`.
5. Dispositions `server/AUDIT-EXPORT.md` and both README link surfaces so no
   operator is told the deleted producer can be enabled, without editing the
   held decisions log.
6. Explicitly retains operator disposition only for legacy pre-upgrade
   checkpoint blocks that the new runtime cannot create.
7. Correctly narrows owner decision 4 to removal of the runtime producer/build
   dependency while preserving the receiver-owned wire contract.

Confirm that these corrections do not reopen any round-4 through round-6
closure and do not introduce a new runtime, recovery path, or dead retained
mechanism. Do not reintroduce the rejected per-session output lanes. Do not
demand persistence, templates, shared sessions, a daemon, caller identity, or
guardian/private-host recovery.

Return one self-contained plain-English review with this exact structure:

1. VERDICT: ACCEPT or REVISE
2. BOTTOM LINE: five sentences maximum
3. R7 FINDING 1: CLOSED or OPEN, with exact evidence
4. R7 FINDING 2: CLOSED or OPEN, with exact evidence
5. R7 FINDING 3: CLOSED or OPEN, with exact evidence
6. R7 FINDING 4: CLOSED or OPEN, with exact evidence
7. R7 FINDING 5: CLOSED or OPEN, with exact evidence
8. LEGACY-STATE DISPOSITION: SAFE or UNSAFE, with exact evidence
9. REGRESSION CHECK: reopened prior findings; write None if none
10. NEW BLOCKING OR MAJOR FINDINGS: numbered with evidence and smallest
    correction; write None if none
11. OVER-ENGINEERING CHECK: identify anything still safe to remove; write None
    if every remaining mechanism protects a concrete invariant
12. OWNER DECISIONS: confirm topology is settled and give the remaining
    ordered list
13. PRODUCTION CONFIDENCE: enforceable guarantees, explicit non-guarantees,
    and remaining evidence required before cutover

Do not return ACCEPT merely because text changed. Judge whether a cold
implementation agent can execute Slice 2 and Slice 11 as dependency-closed,
compiling commits without deleting the retained receiver or restoring the
deleted producer.
