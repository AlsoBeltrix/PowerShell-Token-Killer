You are the independent senior architecture reviewer for PowerShell Token
Killer (PTK), returning for fixed-SHA closure of the multi-session salvage
plan. Review exact commit
253667519e82dcd8a1b075fdc0558740b5c943ab and exact plan blob
ce9b3192fa5128f95f2d5c1a596e2709ed377611 in this detached clean checkout.
This is READ-ONLY. Do not edit files, commit, install, push, or mutate external
systems.

The owner-settled topology remains:

- one MCP stdio connection and public supervisor per unrelated agent;
- several explicitly named warm sessions may exist inside that connection;
- every session is a separate long-lived PowerShell worker process with one
  runspace;
- session names are explicit routing labels, not identity or authorization;
- unrelated agents sharing one connection remain unsupported.

Round 4 is recorded at:

- `.agents/review/production-reliability-salvage-opus5-r4.prompt.md`
- `.agents/review/production-reliability-salvage-opus5-r4.md`

The corrected plan is:

- `.agents/plans/production-reliability-salvage.md`

Read `AGENTS.md`, `.agents/repo-guidance.md`, the corrected plan, the round-4
record, and the exact repository files needed to verify every correction.
Treat `feature/mcp-resilience-r1` only as a parts bin.

Verify closure of all round-4 findings:

1. Slice 1 now deletes the real fake guardian/private-host fixture and preserves
   the already separate containment fixture without a false rename.
2. Slice 4 explicitly converts the Unix guardian-broker test and C fixture to
   worker-only parent-death/group-leadership coverage.
3. Audit removal retains `SecureAuditStorage` for `OutputStore`, retires only
   the runtime producer-to-SIEM conformance project/CI step, and preserves the
   standalone receiver tests.
4. Slice 6 atomically changes the live schemas, handshake guard, frozen public
   contract, and digest.
5. `open` on `Faulted`, concurrent open, recovery/closing open, and late-frame
   behavior are deterministic.
6. Worker startup/replacement has a bounded deadline, containment grace, and
   no-overlap behavior without adding a new public parameter or recovery loop.
7. Explicit reset is idle-only, close/output-handle semantics are frozen, and
   close/reopen quota attribution is bounded.
8. The shared output-store foreground lane is explicitly adjudicated.
9. Real Exchange acceptance now faults one Exchange session and proves the
   sibling remote connection survives without reauthentication.

Round 4 recommended replacing the single `OutputStore` foreground lane with a
per-session lane. The corrected plan rejects that remedy: the lane exists to
cap potentially uninterruptible filesystem work at one task per supervisor.
A wedged lane makes later capture fail promptly as `recovery=unavailable`, but
ordinary commands, warm runspace state, and state queries continue. Per-session
byte quotas remain independent. Judge whether this is the safer production
contract; do not equate optional recovery availability with PowerShell session
state isolation.

Also verify the over-engineering closure: unbrokered Unix containment remains
only for the still-live direct in-process path through Slice 5, then Slice 6
removes that path and its process-global fallback atomically. Final Unix
workers require the broker and fail startup closed without it.

Return one self-contained plain-English review with this exact structure:

1. VERDICT: ACCEPT or REVISE
2. BOTTOM LINE: five sentences maximum
3. R4 FINDINGS 1-9: one CLOSED or OPEN entry per finding with exact evidence
4. GLOBAL OUTPUT LANE: ACCEPTED or UNSAFE, with exact evidence
5. REGRESSION CHECK: reopened prior findings; write None if none
6. NEW BLOCKING OR MAJOR FINDINGS: numbered with evidence and smallest
   correction; write None if none
7. OVER-ENGINEERING CHECK: identify anything still safe to remove; write None
   if every remaining mechanism protects a concrete invariant
8. OWNER DECISIONS: confirm topology is settled and give the remaining ordered
   list
9. PRODUCTION CONFIDENCE: enforceable guarantees, explicit non-guarantees, and
   remaining evidence required before cutover

Do not return ACCEPT merely because text changed. Do not carry round 4's output
lane recommendation forward if the corrected single-lane contract better
bounds unkillable work without impairing ordinary execution. Do not demand
persistence, templates, shared sessions, a daemon, caller identity, or
guardian/private-host recovery.
