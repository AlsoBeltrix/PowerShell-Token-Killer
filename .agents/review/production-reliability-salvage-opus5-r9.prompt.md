You are the independent senior architecture and verification reviewer for PowerShell Token Killer (PTK). Review exact commit 17469d9b4ff9e9eae6a2dad13903c7294d09724d and exact plan blob 0b7e7a2966c02b38685ee2dbc2fe7a459b438ab5 in this detached clean checkout. This is READ-ONLY. Do not edit files, commit, install, push, invoke external services, or mutate any system.

Claude Opus 5 round 8 accepted the broader production-reliability salvage plan before implementation began. Slice 1 then retired only obsolete guardian-era R0 contracts and fake fixtures, without changing runtime behavior. Six default-parallel full server-suite runs across the unchanged base and Slice 1 produced five different intermittent failures; every failing test passed immediately alone. The new plan amendment diagnoses the verification gate and proposes Slice 1a: one assembly-level xUnit CollectionBehavior attribute with DisableTestParallelization=true, no production changes, no widened watchdogs, and no weakened assertions.

Read:
- AGENTS.md
- .agents/repo-guidance.md
- .agents/plans/production-reliability-salvage.md
- .agents/state.md
- the final diagnostic section of .agents/machines.md
- .agents/review/production-reliability-salvage-opus5-r8.md
- only the runtime/test files needed to assess the five named failures and xUnit scheduling

Review only the new diagnosis and Slice 1a amendment while checking that it does not reopen the accepted topology or add product scope. Evaluate these questions with repository evidence:

1. Does the evidence distinguish an R0 product regression from testhost load/shared-process scheduling strongly enough?
2. Is assembly-wide collection serialization aligned with the settled topology of one worker process/runspace per named session, or could it mask a product concurrency defect that the ordinary suite should expose?
3. Is one assembly attribute genuinely smaller and safer than raising timeouts, retrying tests, adding per-class collection annotations, or choosing an arbitrary thread cap?
4. Do explicit within-test concurrency and the later cross-session/process acceptance gates preserve the concurrency coverage that matters?
5. Is the proposed proof sufficient: mechanical runner on/off evidence, three consecutive ordinary full runs, and the existing cross-platform CI matrix?
6. Is any plan wording overclaimed, under-evidenced, or missing a necessary stop condition?

Do not demand new runtime machinery, a test scheduler abstraction, generalized stress infrastructure, or unrelated product fixes. Do not re-review settled plan mechanisms except where Slice 1a would invalidate them.

Return one self-contained plain-English review with this exact structure:

1. VERDICT: ACCEPT or REVISE
2. BOTTOM LINE: five sentences maximum
3. CAUSATION: SUPPORTED or UNSUPPORTED, with exact evidence and limits
4. SCOPE: TEST-ONLY or PRODUCT-RISK, with exact evidence
5. SMALLEST FIX: ACCEPTED or REJECTED, comparing the named alternatives
6. CONCURRENCY COVERAGE: PRESERVED or WEAKENED, with exact evidence
7. VERIFICATION GATE: SUFFICIENT or INSUFFICIENT, with the smallest correction if needed
8. PRIOR PLAN REGRESSION: reopened round-8 finding; write None if none
9. NEW BLOCKING OR MAJOR FINDINGS: numbered with evidence and smallest correction; write None if none
10. OVER-ENGINEERING CHECK: identify anything safe to remove; write None if the amendment is already minimal
11. PRODUCTION CONFIDENCE: what this fixes, what it does not prove, and the next concrete action
