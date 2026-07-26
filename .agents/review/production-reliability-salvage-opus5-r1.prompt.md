You are the independent senior architecture reviewer for PowerShell Token
Killer (PTK). Review the committed plan at exact SHA
2b6361a9864fcf35524d424ffd56ceef162e5eda in this detached, clean checkout.
This is a READ-ONLY review. Do not edit files, commit, install, push, or run
commands that mutate the checkout or external systems.

The owner's actual product requirement, in plain English:

- PTK is a token compressor and a warm PowerShell execution service.
- It must work reliably and correctly in production workflows.
- Unrelated agents/sessions must not share mutable PowerShell runspace state.
- The owner challenged the prior design as over-engineered and asked what can
  safely be salvaged from it.
- The crucial question is whether "one PowerShell worker" means multiple agents
  accidentally share one runspace. The answer and the plan must be explicit.
- Extra code is acceptable only when it materially improves an enforceable
  reliability property; otherwise it should be cut.

Read, at minimum:

1. AGENTS.md
2. .agents/repo-guidance.md
3. .agents/state.md (especially the first Now item and Next)
4. .agents/decisions.md where relevant
5. .agents/plans/production-reliability-salvage.md
6. README.md and server/README.md
7. Relevant current server source/tests on this exact SHA
8. Relevant source/tests available at feature/mcp-resilience-r1 head 93e7992,
   but treat that branch only as a parts bin, not as an implementation direction.

Judge the plan, not the prose style. Validate claims against code and tests.
Pay special attention to:

- topology and identity: exactly what owns a runspace, whether each MCP server
  instance/client session gets its own worker, and whether any daemon,
  singleton, static registry, job, reset, or output store can cross that
  boundary;
- whether the proposed minimum process boundary is actually simpler and safer
  than both today's in-process runspace and the paused guardian/private-host/
  worker design;
- which code from 93e7992 is safe to cherry-pick or port, which should be
  rewritten, and which should be abandoned;
- timeout/cancellation semantics, uncertain outcomes, crash recovery, process
  tree containment, warm-state loss, startup failure, stdio loss, shutdown,
  and never-replay guarantees;
- macOS, Linux, and Windows behavior, including descendant cleanup;
- whether background jobs have an honest and isolated lifecycle;
- whether the test and rollout plan can expose regressions before production;
- any claimed "100%" guarantee that PTK cannot honestly enforce;
- every part of the plan that adds complexity without buying a concrete,
  testable production guarantee.

Return one self-contained review in plain English with this exact structure:

1. VERDICT: ACCEPT or REVISE
2. BOTTOM LINE: five sentences maximum, including an explicit answer to
   "Do multiple agents share one runspace under this plan?"
3. BLOCKING FINDINGS: numbered. For each, cite exact file/line or symbol
   evidence, state the failure mode, and give the smallest plan correction.
   Write "None" if there are none.
4. MAJOR FINDINGS: same format.
5. MINOR FINDINGS: same format.
6. SALVAGE TABLE: Keep / Adapt / Drop, naming concrete code or concepts from
   93e7992 and why.
7. OVER-ENGINEERING CUTS: concrete plan elements to remove or defer.
8. OWNER DECISIONS: the smallest ordered set of choices that truly require the
   owner; do not turn implementation details into owner questions.
9. PRODUCTION CONFIDENCE: what the plan can guarantee, what remains outside
   PTK's control, and the minimum evidence required before cutover.

Do not accept aspirational wording as a guard. A finding is actionable only if
you can tie it to repository evidence or a missing enforceable contract. Do not
recommend the paused three-process design merely because it already exists.
