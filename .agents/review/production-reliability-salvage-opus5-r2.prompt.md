You are the independent senior architecture reviewer for PowerShell Token
Killer (PTK), returning for a fixed-SHA round-2 review. Review exact commit
c5124c4f19b199a9907100ccf428cc0df5c96a41 in this detached clean checkout.
This is READ-ONLY. Do not edit files, commit, install, push, or mutate external
systems.

The owner wants PTK returned to its actual product: reliable token-compressed
command execution and warm PowerShell state for production agent workflows,
without unrelated agents sharing mutable runspace state. Added machinery is
acceptable only where it buys an enforceable, tested reliability property.

Round 1 reviewed
2b6361a9864fcf35524d424ffd56ceef162e5eda and returned REVISE. The exact prompt,
review identity, findings, and intake are committed at:

- .agents/review/production-reliability-salvage-opus5-r1.prompt.md
- .agents/review/production-reliability-salvage-opus5-r1.md

The amended plan is:

- .agents/plans/production-reliability-salvage.md

Read AGENTS.md, .agents/repo-guidance.md, .agents/state.md, the two round-1
review files, the amended plan, README.md, server/README.md, and the relevant
current and feature/mcp-resilience-r1 source/tests needed to verify claims.
Treat feature head 93e79922a77bd5aab8e2959c69958dd165ea5087 only as a parts
bin. Judge the plan against repository evidence, not its prose or the prior
reviewer's authority.

Verify every round-1 blocker PRS-B1 through PRS-B5, every major PRS-M1 through
PRS-M8, and the six minor findings. Pay particular attention to:

- whether the new one-agent-owned-connection/one-worker boundary honestly
  answers the multiple-agent/runspace question and fails closed when a harness
  multiplexes agents;
- whether removing ptk_job and invoke background is coherent with the public
  contract and toolkit-owned guidance gate;
- whether supervisor/worker ownership is now unambiguous for lifecycle, state,
  output, cancellation, and containment;
- whether the output chunk/seal protocol is sufficient without reintroducing
  guardian capability machinery;
- whether the Unix broker, worker entry, process-group ownership, and
  descendants_unknown limit are implementable;
- whether frozen R0 contracts and audit/protobuf dependencies are retired only
  through explicit owner decisions;
- whether the plan still contains optional machinery that should be cut;
- whether known EXO shaping, Windows kill-path, stale-client, audit-startup,
  and distribution failures correctly remain production blockers;
- whether any reliability or "never" statement still exceeds what PTK can
  enforce.

Return one self-contained plain-English review with this exact structure:

1. VERDICT: ACCEPT or REVISE
2. BOTTOM LINE: five sentences maximum. Explicitly answer: "Can unrelated
   agents share one runspace under the supported first-cut topology?"
3. ROUND-1 CLOSURE: one row or bullet for each PRS-B1..B5 and PRS-M1..M8,
   CLOSED or OPEN, with exact file/line or symbol evidence.
4. NEW BLOCKING FINDINGS: numbered with evidence, failure mode, and smallest
   plan correction; write None if there are none.
5. NEW MAJOR FINDINGS: same format.
6. MINOR FINDINGS: only unresolved round-1 minors or genuinely new material
   issues; do not repeat closed wording suggestions.
7. OVER-ENGINEERING CHECK: concrete elements still safe to remove or defer.
8. OWNER DECISIONS: verify that only genuine product choices are left, ordered
   one at a time, with a recommendation.
9. PRODUCTION CONFIDENCE: enforceable guarantees, explicit non-guarantees, and
   minimum evidence before cutover.

Do not return ACCEPT if a cold implementation agent would still have to invent
an ownership boundary, failure semantic, protocol transfer, or guard-retirement
strategy. Do not reopen a closed issue without repository evidence.
