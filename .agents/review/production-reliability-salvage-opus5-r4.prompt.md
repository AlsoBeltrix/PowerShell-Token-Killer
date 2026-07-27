You are the independent senior architecture reviewer for PowerShell Token
Killer (PTK). Review exact commit
9c50c38efe1e55f1e08df531ccd2392e4fe498b5 and exact plan blob
bc122998de0868cc4aa95fefc354c7a6d3e950a4 in this detached clean checkout.
This is READ-ONLY. Do not edit files, commit, install, push, or mutate external
systems.

The owner needs PTK to be dependable in production workflows. PTK's core is
token-compressed shell execution with warm PowerShell state. One real workflow
requires an agent to keep on-prem Exchange and Exchange Online modules loaded
at the same time even when their cmdlet names overlap. The settled topology is:

- one MCP stdio connection and public supervisor per unrelated agent;
- several explicitly named warm sessions may exist inside that connection;
- every session is a separate long-lived PowerShell worker process with one
  runspace, analogous to one PowerShell process per terminal window;
- session names are explicit routing labels, not identities or authorization;
- unrelated agents sharing one connection remain unsupported.

The prior one-worker-per-connection draft was accepted in
`.agents/review/production-reliability-salvage-opus5-r3.md`, but that acceptance
is superseded because the owner rejected its topology as incompatible with the
Exchange workflow. This review must judge the corrected design afresh, not
preserve the old verdict.

Read:

- `AGENTS.md`
- `.agents/repo-guidance.md`
- `.agents/state.md`
- `.agents/plans/production-reliability-salvage.md`
- `.agents/review/production-reliability-salvage-opus5-r3.md`
- the relevant multi-session portions of
  `.agents/plans/audited-harness-sessions.md`
- current `master` worker/session/tool/output code and tests
- `feature/mcp-resilience-r1` only where needed as a parts bin

Review the complete corrected plan, with particular scrutiny on:

1. Whether a dedicated PowerShell process per named session is the smallest
   reliable boundary for conflicting Exchange modules, assemblies, cmdlets,
   remote connections, and failure state.
2. Whether the explicit `ptk_session list|open|close` surface plus optional
   `session` on invoke/state/reset is complete, unambiguous, backward
   compatible for `default`, and smaller than the discarded template/durable
   session design.
3. Whether the fixed eight-session bound, explicit-open/no-auto-create rule,
   idle-only close, lack of mutable `select`, handle-only `ptk_output`, and
   per-session reset semantics are justified or need correction.
4. Whether concurrency, timeout, crash, replacement, containment, state,
   shutdown, output backpressure/quota, and no-replay behavior are truly
   isolated per session while supervisor death still cleans up every worker.
5. Whether the implementation slices can land without a period where schemas,
   live behavior, or guards disagree, and without transplanting
   guardian/private-host complexity.
6. Whether the production acceptance matrix directly proves the actual
   on-prem Exchange plus Exchange Online overlapping-cmdlet workflow and
   sibling-session survival.
7. Whether any mechanism is still unnecessary, or any omitted invariant would
   make this unsafe or unreliable in production.

Return one self-contained plain-English review with this exact structure:

1. VERDICT: ACCEPT or REVISE
2. BOTTOM LINE: five sentences maximum
3. TOPOLOGY: sound or unsound, with exact evidence
4. SESSION CONTRACT: complete or incomplete, with exact evidence
5. FAILURE ISOLATION: sound or unsound, with exact evidence
6. IMPLEMENTATION/VERIFICATION: sound or unsound, with exact evidence
7. BLOCKING OR MAJOR FINDINGS: numbered with evidence and the smallest
   correction; write None if none
8. OVER-ENGINEERING CHECK: identify anything safe to remove; write None if
   every remaining piece protects a concrete invariant
9. OWNER DECISIONS: state which decisions are settled and which remain, in
   order
10. PRODUCTION CONFIDENCE: enforceable guarantees, explicit non-guarantees,
    and remaining evidence required before cutover

Do not return ACCEPT merely because process-per-session resembles interactive
PowerShell. Do not demand persistence, templates, shared sessions, a daemon,
caller identity, or guardian/private-host recovery unless repository evidence
shows the simpler connection-local design cannot meet the stated workflow.
