# Review status

## Active — cr10 (Claude review over S4b custody/retention, 2026-08-12)

Generation attempt: claude / claude-opus-5 / xhigh / frontier over the
intended `53bb4aa..196b4f7` range. Claude independently resolved the real
commits and returned two evidence-backed candidates, but the orchestrator had
supplied invalid fabricated full-SHA expansions; returned SHAs therefore did
not match the literal dispatch pins and the envelope is not accepted as a
range verdict. Both candidates passed intake independently and are admitted.
Fixes land one finding per commit; one verification batch is the section's
second and final review round.

| ID     | Severity | Impact (one line)                                      | Status | Reviewer |
|--------|----------|--------------------------------------------------------|--------|----------|
| cr10-1 | CRITICAL | v7 receipts brick the receiver during v8 upgrade       | `[~]` fixed, verification pending | pending |
| cr10-2 | HIGH     | startup and retention become quadratic at scale        | `[ ]` | pending |

## Closed — cr9 (codereview over R6, 2026-08-11)

Generation pass: codex / gpt-5.6-sol / high / standard over
`a43e4e4..c9b41c8` (R6: CI/docs/packaging/release gates — the final
audit-restoration slice). Verdict `findings` (5), capability_ok true,
SHAs matched. All five admitted; none declined. Fixes one commit each;
verification in ONE frontier batch (worktree `770dae5`) — verdict
`confirmed`, all five, guard_confirmed true (the cr9-1 bite proved
with a lifecycle-only root that satisfied the old substring check and
fails all three new pins; doc claims verified against the code; two
reviewer-environment blocks noted in the records, no product
assertion failed).

| ID    | Severity | Impact (one line)                                             | Status | Reviewer |
|-------|----------|---------------------------------------------------------------|--------|----------|
| cr9-1 | HIGH     | journaling gate passed on lifecycle records alone             | `[x]` `f38610c` | codex xhigh |
| cr9-2 | HIGH     | server README still told operators auditing is disabled       | `[x]` `db59531` | codex xhigh |
| cr9-3 | HIGH     | active exact-script evidence store documented as legacy-only  | `[x]` `de24c84` | codex xhigh |
| cr9-4 | MEDIUM   | receiver response contract omitted the 401 row                | `[x]` `953e2c2` | codex xhigh |
| cr9-5 | LOW      | proof's HOME-rooted audit dir not reliably cleaned up         | `[x]` `770dae5` | codex xhigh |

## Closed — cr8 (codereview over R5c, 2026-08-11)

Generation pass: codex / gpt-5.6-sol / high / standard over
`b093dea..782d345` (R5c: mini-SIEM S6 — gap-disposition state machine
+ alert pipeline). Verdict `findings` (7), capability_ok true, SHAs
matched. All seven admitted; none declined. Fixes landed one commit
each, every guard proved biting (stash-revert or faithful sabotage).
Verification in ONE frontier batch (four HIGHs → T2 ceiling), then
cr8-4 took FOUR frontier rounds — three real reopens, each a deeper
hole in the migration backfill: v3 gaps migrated NULL-linked (v5
backfill), MIN(attempt_id) ambiguous across resumed gaps (v6 instant
match), one JSON batch sharing a single instant (v7 custody-ledger
adjacency — the opener's quarantine receipt immediately precedes its
gap:opened receipt, exact and instant-independent). Round 4 confirmed
with the adjacency sabotage biting and the history-wide invariant
checked. cr8-1's fix records a deliberate design divergence
(heal-by-proof instead of freeze-and-wait), accepted at verification.

| ID    | Severity | Impact (one line)                                               | Status | Reviewer |
|-------|----------|-----------------------------------------------------------------|--------|----------|
| cr8-1 | HIGH     | late hole filling silently resumed past an open gap             | `[x]` `fc78886` | codex xhigh |
| cr8-2 | HIGH     | retention erased queued subjects, permanently suppressing alerts | `[x]` `9da3da5` | codex xhigh |
| cr8-3 | HIGH     | alert/gap custody receipts verified over forged evidence        | `[x]` `6648223` | codex xhigh |
| cr8-4 | HIGH     | an unresolved gap's opening attempt was sweepable               | `[x]` `c0b4889`+`675e44b`+`a28f458`+`1fe69b6` | codex xhigh ×4 |
| cr8-5 | MEDIUM   | queue rows and closed alerts grew without bound                 | `[x]` `2506a97` | codex xhigh |
| cr8-6 | MEDIUM   | newline-framed rule-config hashes collided                     | `[x]` `78c0579` | codex xhigh |
| cr8-7 | LOW      | detail JSON interpolated unescaped stored values               | `[x]` `0c2b2c6` | codex xhigh |

## Closed — cr7 (codereview over R5b, 2026-08-11)

Generation pass: codex / gpt-5.6-sol / high / standard over
`47fd8e2..1422bec` (R5b: read-only operator query API + dashboard,
mini-SIEM S5). Verdict `findings` (5), capability_ok true, SHAs
matched. All five admitted against the code at intake; none declined.
Fixes landed one commit each, every guard proved biting
(stash-revert, or faithful sabotage where the revert would be a
compile error). Verification in ONE frontier batch (three HIGHs → T2
ceiling covers all): codex / gpt-5.6-sol / xhigh, network-enabled
workspace-write worktree at `99c5c7f` — verdict `confirmed`,
guard_confirmed true, every guard independently re-proved
(fail-on-sabotage/revert, pass restored), suite 285/285.

| ID    | Severity | Impact (one line)                                              | Status | Reviewer |
|-------|----------|----------------------------------------------------------------|--------|----------|
| cr7-1 | HIGH     | query-string auth put the operator credential in logs/history  | `[x]` `89f5284` | codex xhigh |
| cr7-2 | HIGH     | equal ingest/operator tokens collapsed the two authorities     | `[x]` `47c33e6` | codex xhigh |
| cr7-3 | HIGH     | unbounded /api/chains + overlapping dashboard poll             | `[x]` `4011f44` | codex xhigh |
| cr7-4 | MEDIUM   | lexicographic time filters silently dropped same-second events | `[x]` `a7edc20` | codex xhigh |
| cr7-5 | LOW      | uppercase event ID 404'd its own stored event                  | `[x]` `99c5c7f` | codex xhigh |

## Closed — cr6 (codereview over R5a, 2026-08-11)

Generation pass: codex / gpt-5.6-sol / high / standard over
`b1bd4a7..be80e59` (R5a: producer-owned golden corpora + producer-to-
receiver conformance, the mini-SIEM S4 fixture gate). Verdict
`findings` (1), capability_ok true, SHAs matched. Verified same day.

| ID    | Severity | Impact (one line)                                            | Status | Reviewer |
|-------|----------|--------------------------------------------------------------|--------|----------|
| cr6-1 | MEDIUM   | fixture locator resolved an ancestor checkout's stale corpus | `[x]` `4d564c4` | codex/gpt-5.6-sol/high/standard |

## Closed — cr5 (codereview over R4, 2026-08-11)

Generation pass: codex / gpt-5.6-sol / high / standard over
`3b8dff8..3996e6a` (R4: journaled eviction event + allocation-path
floor, loopback web UI, alert webhook). Verdict `findings` (8),
capability_ok true, SHAs matched. All eight verified against the code
at intake and ADMITTED; none declined. Every finding sits in
`Audit/Web/` — the UI and webhook services, both new in R4.

ALL EIGHT VERIFIED. Fixes landed one commit each, every guard proved
fail-before/pass-after (stash-revert, checkout-revert, or targeted
sabotage where the surface is additive — per record): cr5-1 `ee0ac2f`,
cr5-2 `5a7d895` + repair `e253c12`, cr5-3 `0d208ac` + repair `459039a`,
cr5-4 `e526643` + repair `124ba4c`, cr5-5 `415c139`, cr5-6 `17440c5`,
cr5-7 `55ff41d`, cr5-8 `16e0c4d`. Verification ran as two batches
(orchestrator's call, cr2/r806 precedent): HIGHs at frontier (T2),
MEDIUMs at standard. THREE real reopens, each repaired and re-accepted
at frontier: cr5-2 (same-millisecond truncation between the
construction instant and filename stamps), cr5-3
(DirectoryNotFoundException classed as benign retention), cr5-4
(live-appended-last presented a quiet supervisor's stale records as
newest under the shared-root topology). Round 1 of the HIGH batch
returned guard_confirmed:false because the codex sandbox denied the
VSTest testhost socket — fixed for all later rounds by dispatching with
`-c 'sandbox_workspace_write.network_access=true'` (worth reusing).
Battery at `124ba4c`: server 1,301/1,301, SIEM 270/270, Pester 112+3
skip, handshake PASSED.

| ID    | Severity | Impact (one line)                                              | Status | Reviewer |
|-------|----------|----------------------------------------------------------------|--------|----------|
| cr5-1 | HIGH     | port squatter harvests the UI bearer token, reuses it later    | `[x]` `ee0ac2f` | codex/gpt-5.6-sol/xhigh/frontier esc:T2,T5 |
| cr5-2 | HIGH     | webhook ctor filesystem work gates startup (webhook or not)    | `[x]` `5a7d895`+`e253c12` | codex/gpt-5.6-sol/xhigh/frontier esc:T2,T5 |
| cr5-3 | HIGH     | /api/records returns 200 with evidence silently omitted        | `[x]` `0d208ac`+`459039a` | codex/gpt-5.6-sol/xhigh/frontier esc:T2,T5 |
| cr5-4 | MEDIUM   | populated closed spool hides the live tail from the UI         | `[x]` `e526643`+`124ba4c` | codex/gpt-5.6-sol/xhigh/frontier esc:T5 |
| cr5-5 | MEDIUM   | token race: serving UI may not recognize the published token   | `[x]` `415c139` | codex/gpt-5.6-sol/high/standard |
| cr5-6 | MEDIUM   | failed webhook edge lost if the condition heals before retry   | `[x]` `17440c5` | codex/gpt-5.6-sol/high/standard |
| cr5-7 | MEDIUM   | lineage alert fires at most once per process lifetime          | `[x]` `55ff41d` | codex/gpt-5.6-sol/high/standard |
| cr5-8 | MEDIUM   | webhook delivery failure invisible in every health surface     | `[x]` `16e0c4d` | codex/gpt-5.6-sol/high/standard |

## Closed — cr4 (codereview over R3d completion + R3c, 2026-08-11)

Generation pass: codex / gpt-5.6-sol / high / standard over
`abc3292..8ab189a` (boot lineage, coordinated live-tail read, receiver
token auth + JSON ingest). Verdict `findings` (5), capability_ok true,
SHAs matched. Dispatch note: codex-cli 0.147.0 ignored the recorded
`-c 'mcp_servers={}'` override (empty inline table deep-merges to a
no-op since 0.146.1); the first dispatch failed fail-closed on
`capability_ok:false` and the incantation was re-probed to per-server
`enabled=false` flags — recorded in `harnesses.local.json`.

ALL FIVE VERIFIED. Fixes landed one commit each, sabotage-proved:
cr4-1 `9acd89d`, cr4-2 `54dd79f`, cr4-3 `c0abafc`, cr4-4 `7c8fa96` +
repairs `ae7ca0a`/`97ee6d1`/`940dc3c`, cr4-5 `5ddbaef`. cr4-4 took
FOUR frontier rounds (T2 then T5-ceiling ×3): the reviewer twice found
real new paths in the repairs — a mutable file ordering under the
linear cursor, then drain-scoped attestations, then cursor-only
evidence — before accepting the per-boot-positions + ledger-mirrored
attestations design. Battery at close: server 1,281/1,281, SIEM
270/270, handshake PASSED.

| ID    | Severity | Impact (one line)                                            | Status | Reviewer |
|-------|----------|--------------------------------------------------------------|--------|----------|
| cr4-1 | HIGH     | non-v4 lineage id bypasses quarantine, disables execution    | `[x]`  | codex/gpt-5.6-sol/xhigh/frontier esc:T2 |
| cr4-2 | MEDIUM   | silent lineage publish failure leaves a boot unattested      | `[x]`  | codex/gpt-5.6-sol/high/standard |
| cr4-3 | HIGH     | concurrent supervisors overwrite each other's lineage        | `[x]`  | codex/gpt-5.6-sol/xhigh/frontier esc:T2 |
| cr4-4 | HIGH     | export leg unsafe under concurrent supervisors (skip/races/false gaps) | `[x]` | codex/gpt-5.6-sol/xhigh/frontier esc:T2,T5,T5,T5 |
| cr4-5 | MEDIUM   | honest cross-encoding replay quarantined as forgery          | `[x]`  | codex/gpt-5.6-sol/high/standard |

## Closed — cr2 (codereview over the R2 audit restoration, 2026-08-10)

Generation pass: codex-cli 0.146.1, `codex/gpt-5.6-sol/high/standard`
(owner said "codex default"), over `61fc838..a10ad50` — the whole
audit-restoration R2 range. Verdict `findings` (4), capability_ok true,
SHAs pinned and matched; ~170.6k tokens. Dispatched with the recorded
`-c 'mcp_servers={}'` recipe, read-only sandbox. All four candidates
verified against the code at intake and admitted; none declined.

| ID     | Severity | Impact (one line) | Status | Reviewer |
|--------|----------|-------------------|--------|----------|
| cr2-1 | HIGH | Windows repaired rather than validated a retained `host.id`, silently adopting a foreign/over-permissive identity instead of quarantining it | `[x]` fixed `8e913e7`, accepted | codex/gpt-5.6-sol/high/standard |
| cr2-2 | HIGH | every call journaled `session.name=default`; named-session activity was misattributed | `[x]` fixed `73464ab`, accepted | codex/gpt-5.6-sol/high/standard |
| cr2-3 | MEDIUM | audit admission rejected `ptk_output` requests carrying the schema's own defaults, narrowing the published MCP contract | `[x]` fixed `a5a8f76`, accepted | codex/gpt-5.6-sol/high/standard |
| cr2-4 | MEDIUM | quarantines were stderr-only — no durable journal record, so monitoring/export could not see them | `[x]` fixed `98083cc`, accepted | codex/gpt-5.6-sol/high/standard |

One commit per finding, each sabotage-proved locally (stash-revert →
named guard fails → restore → passes). Full battery at `98083cc`:
server 1,220/1,220 from a plain shell, Pester 112 + 3 skipped.

**Verification: one batch dispatch, accepted, guard_confirmed true.**
Batch shape was the orchestrator's call (one shared surface, four
sequential commits, one disposable worktree at `98083cc`) — a recorded
deviation from per-finding dispatch, mirroring the r806 precedent.
codex-cli 0.146.1, `codex/gpt-5.6-sol/high/standard`, workspace-write
inside an orchestrator-created disposable worktree, pins
`a10ad501..98083cc6` matched, capability_ok true; the reviewer
independently re-ran every guard proof (revert → fail → restore → pass)
for all four findings. ~97.7k tokens. Per-finding records carry the
verdict text.

## Closed — oar1 (openreview, audit-restoration plan, 2026-08-10)

**Rulings resolved same day, all three in the reviewer's favor with one
owner amendment:** (1) adopted — journal-backed local UI, receiver
optional ("fallback should act exactly like real SIEM"); (2) superseded
in the better direction — no pairing/enrollment machinery at all; the
receiver issues a token like a real SIEM and shares the one
endpoint+credential exporter contract; (3) adopted — rbc-11 retention
enforcement rides the blanket fix authorization before the receiver
ships. The plan's "Shape of record" section owns the adopted design.

openreview codex (gpt-5.6-sol @ high, standard-confirmed) over
`9163ea0..f4e1738`: **replace**. Owner named "codex (default)", a
recorded owner-directed deviation from openreview's fixed
frontier-at-max routing. codex-cli 0.146.1, `-c 'mcp_servers={}'`
recipe, read-only sandbox, ~100.6k tokens; capability_ok true, SHAs
pinned and matched; `material_changes` non-empty as the verdict
requires. Verdict artifact retained in the session scratchpad; the
substance is recorded here.

Reviewer's recommended approach (design judgment, owner rules): keep
the producer's append-only journal as the sole local authority and
execution gate; expose it through a loopback management UI that also
shows export lag; export asynchronously to an **optional** authenticated
remote receiver via endpoint-plus-pairing enrollment; the receiver
serves remote custody/query/alerts and requires proven bounded
retention before deployment. Material changes awaiting owner rulings:
(1) journal-backed local UI instead of a mandatory local receiver;
(2) R1 gains a secure endpoint-plus-enrollment design reconciling the
mTLS/separate-principal/separate-product contracts; (3) receiver
retention enforcement becomes a deployment/packaging prerequisite;
(4) state.md Q1 staleness — fixed at intake (LOW, blanket).

| ID | Severity | Impact (one line) | Status | Reviewer |
|----|----------|-------------------|--------|----------|
| oar1-1 | HIGH | plan makes the retention-unenforced receiver (rbc-11) the default destination in every install; unbounded growth ends in disk-full execution blocks | `[ ]` admitted; plan revision gated on owner ruling 1/3 | codex/gpt-5.6-sol/high/standard |
| oar1-2 | HIGH | "one endpoint, no certificate ceremony" conflicts with the receiver's frozen client-cert-required contract; naive implementation invites unauthenticated custody | `[ ]` admitted; plan revision gated on owner ruling 2 | codex/gpt-5.6-sol/high/standard |
| oar1-3 | MEDIUM | the promised log GUI reads the receiver DB, which goes blind exactly during exporter/receiver failure | `[ ]` admitted; resolved by ruling 1 if adopted | codex/gpt-5.6-sol/high/standard |
| oar1-4 | LOW | state.md still presented settled Q1 as the next open design question | `[x]` fixed at intake (same commit) | codex/gpt-5.6-sol/high/standard |

## Closed — r806 (owner-requested pass over the day's commits, 2026-08-06)

Generation pass: codex-cli 0.146.1, `codex/gpt-5.6-sol/high/standard`, over
`d440234..32c444d` (all eleven 2026-08-06 commits: the three opr-53
structured-verdict fixes, the release-gate automation, and the per-RID CI
gate). Verdict `findings` (4), capability_ok true, SHAs pinned and matched.
Dispatched with the recorded `-c 'mcp_servers={}'` recipe; 2.36M input tokens
(2.21M cached), 15.4k output. All four candidates verified against the code
at intake and admitted; none declined.

| ID     | Severity | Impact (one line)                                        | Status | Reviewer |
|--------|----------|-----------------------------------------------------------|--------|----------|
| r806-1 | MEDIUM   | a preflight containment refusal (nothing executed) reports `executed=true`/`outcome_unknown` | `[x]` fixed `024fa66`, accepted | codex/gpt-5.6-sol/xhigh/frontier esc:T2 |
| r806-2 | MEDIUM   | a failed replacement start reports a warm-state-destroying reset as `not_started`/`safe_to_resubmit=true` | `[x]` fixed `9d8aec7`, accepted | codex/gpt-5.6-sol/xhigh/frontier esc:T2 |
| r806-3 | HIGH     | the opt-in destructive uninstall's refusal passes a sibling-prefixed home and removes the wrong install | `[x]` fixed `adb9f7a`, accepted | codex/gpt-5.6-sol/xhigh/frontier esc:T2 |
| r806-4 | MEDIUM   | the Defender release gate passes when the scan never ran (absent or failing MpCmdRun) | `[x]` fixed `49a5cc7`, accepted | codex/gpt-5.6-sol/xhigh/frontier esc:T2 |
| r806-5 | MEDIUM   | invoke's Failed fallback reports scheduler/machinery failures as `completed` while its own text says `status=failed` | `[x]` fixed `5862041`, accepted | codex/gpt-5.6-sol/xhigh/frontier esc:T2 |
| r806-d1..d4 | — | declined at intake with reasons: `.agents/review/r806-self.contested.md` | `[-]` | claude/fable-5/self-review |

All five fixes landed 2026-08-06 (owner go), one commit per finding, each
guard-proved by sabotage (the exact old behavior restored temporarily fails
exactly the new assertions). Battery at `5862041`: server 1,191/1,191,
Pester 112 + 3 platform-skipped, SIEM 247/247, dependency audit clean,
handshake passed.

**Verification (2026-08-07 04:06 UTC): one batch dispatch, all five
accepted, guard_confirmed true on every finding.** The batch shape was the
owner's explicit instruction ("one total codex review, make it count",
2026-08-06) — a recorded owner-directed deviation from per-finding
dispatch. Routed frontier under T2 (r806-3 HIGH): codex-cli 0.146.1,
`codex/gpt-5.6-sol/xhigh/frontier`, workspace-write in an
orchestrator-created disposable worktree, pins `2756767..5862041` matched,
capability_ok true. The reviewer independently re-ran every guard proof
(revert → named guards fail → restore → 21/21 pass; predicate re-runs for
the two script findings). ~3.94M input tokens (3.78M cached), 14.9k output.
Environment notes, recorded not invalidating: `git checkout` restoration
was blocked inside the disposable worktree (index metadata under the
read-only canonical `.git`), so the reviewer restored via apply_patch and
proved byte-exactness with `git diff --exit-code` before each passing run;
github.com DNS was unreachable from the sandbox, so remote freshness was
not checked there (checked clean by the orchestrator).

Note for the rc: the r806-4 fix means the per-RID gate's Windows Defender
leg now fails visibly on a runner that cannot complete a scan; that is the
gate working, and whether to adapt the workflow or the runner is an owner
call recorded in the finding.

**Second pass, same range (owner-requested, 2026-08-06):** the working agent
re-reviewed the range itself (single-agent mode) after the codex dispatch —
exact sources at head, not the diff alone. It independently confirmed all
four codex findings (r806-4 is the *expected* state on GitHub's Windows
runners, where `MpCmdRun.exe` exists but Defender's service is typically
disabled), admitted one finding codex missed (r806-5, verified against the
producers in `WorkerOperationScheduler`/`RunspaceHost` and the pinned test
at `ToolOutcomeTests.cs:142`), and declined four candidates with recorded
reasons.

r806-1 and r806-2 are the o53 lesson recurring at a third and fourth site:
PTK stating a false verdict in the trusted structured channel because
execution stage is inferred from a shared detail string instead of carried as
data. r806-3 and r806-4 sit in the release gate itself and bear on the
pending `0.2.0-rc.3` dispatch: -3 is the destructive-refusal boundary, -4
means the new per-RID gate's Windows Defender leg can pass vacuously.

## Closed — o53 (opr-53 forgeable control lines, 2026-08-06; o53-3 added 2026-08-07)

**o53-3 (HIGH, field-reported + reproduced firsthand 2026-08-07):** clients
that prefer `structuredContent` over text — Claude Code, the primary
harness — rendered every completed call as the three-field verdict JSON,
hiding all output and recovery handles. Fixed same day under the owner's
delegated design call: completed responses are bare text again (absence is
the completed verdict), non-completed responses keep the verdict plus a
`text` mirror, and the call filter's text matcher is gated to `ptk_output`
by tool identity. Detail and guard proofs:
`.agents/review/findings/o53-3.md`. The durable lesson extends the o53
record: a trusted channel is only trustworthy if the primary consumer can
still see the product around it — verify new protocol surfaces against the
real harnesses that read them, not only against the wire.

Generation pass: codex-cli 0.146.0, `codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh`,
over `d440234..11eafee`. Verdict `findings` (2), capability_ok true, SHAs
pinned and matched.

| ID     | Severity | Impact (one line)                                        | Status |
|--------|----------|----------------------------------------------------------|--------|
| o53-1  | MEDIUM   | a worker-reported non-start was reported as `executed=true` | `[x]` (fixed `18d76e8`) |
| o53-2  | HIGH     | a close that had already acted was reported `safe_to_resubmit=true` | `[x]` (fixed `c40404a`) |

o53-2 was found independently by the working agent while the review was in
flight and fixed before the verdict arrived; codex reached the same
conclusion by a different route (`CloseAsync` starts the transition before
its completion task can fault).

**Durable lesson.** Both findings are the same mistake as the original
finding, one layer in: opr-53 was about a *script* being able to state a
false verdict, and both follow-ons were *PTK* stating a false verdict in the
new channel a client is told to trust. Moving a claim into a trusted channel
raises the cost of getting it wrong. When adding a structured
disposition, enumerate every site that produces it and ask of each: had the
work already begun? `NamedSessionException` is not uniformly a non-start,
and a normal return is not uniformly a completion.

## Closed — o10 (opr-10 timeout validation, 2026-08-05)

Generation pass: codex-cli 0.146.0, `codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh`,
over `7761b75..eca0891`. Verdict `findings` (1), capability_ok true, SHAs
pinned and matched.

| ID     | Severity | Impact (one line)                                        | Status |
|--------|----------|----------------------------------------------------------|--------|
| o10-1  | MEDIUM   | an individually valid maximum below the default still aborts startup | `[x]` (fixed `da62268`) |

o10-1 is the pair half of `opr-10`: each variable can be individually legal
while the pair is not, because `CreateLimits` rejects a default greater than
the maximum. The working agent found the same defect independently while
the review was in flight and had already fixed it; codex named a simpler
trigger (`PTK_MAX_CALL_TIMEOUT_SECONDS=100` with the call timeout unset,
so the 300 default exceeds it), which is now a pinned theory case. Guard
proved: three cases fail with the pair logic reverted.

## Closed — i42b (install payload survival, 2026-08-05)

Generation pass: codex-cli 0.146.0, `codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh`,
over `7761b75..1ff20c8`. Verdict `findings` (1), capability_ok true, SHAs
pinned and matched.

| ID      | Severity | Impact (one line)                                       | Status |
|---------|----------|----------------------------------------------------------|--------|
| i42b-1  | HIGH     | the merge helper nested subdirectories, and activation then claimed the payload was intact | `[x]` (fixed `5d866b1`) |

**Durable lesson, third occurrence in one issue.** Every finding on #42 was
the fix reintroducing the defect somewhere the previous test did not look:
i42-1 in rollback, i42b-1 inside the merge helper written to fix i42-1.
`Copy-Item -Recurse` and `Move-Item -Force` both put a directory *inside* an
existing same-named directory. Neither is a safe way to replace a directory;
only an explicit recursive merge, or a rename onto a proven-absent path, is.

The first guard written for i42b-1 **passed against the broken code**,
because driving the merge through activation meets an empty destination.
A guard for a merge must supply a destination that already contains a
same-named child, which is what rollback actually does.

## Closed — i42 (install payload activation, 2026-08-05)

Generation pass: codex-cli 0.146.0, `codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh`,
over `b1c9184..8a1bf2a` (GitHub #42). Verdict `findings` (2),
capability_ok true, SHAs pinned and matched. Both admitted at intake and
fixed one commit each.

| ID     | Severity | Impact (one line)                                        | Status |
|--------|----------|----------------------------------------------------------|--------|
| i42-1  | HIGH     | rollback repeats the nesting bug, so a failed install can half-restore the registered payload | `[x]` (fixed `044d53b`) |
| i42-2  | LOW      | the completeness wiring check passes even with the call deleted | `[x]` (fixed `7761b75`) |

i42-1 was the valuable one and was not visible from the diff alone:
`Restore-PtkInstallSnapshot` removed the target and copied the snapshot back
without checking the removal took effect, and `Copy-Item -Recurse` onto a
surviving directory nests exactly as `Move-Item` does. The activation guard
made it *more* reachable, since the condition that trips the guard is the
condition that defeats the removal. Both call sites now share
`Assert-PtkInstallPathRemoved`. Guard proved by reproducing the nested
restore red before the fix.

i42-2 was accurate: the assertion searched for a bare function name that
also appears in the declaration the test extracts, so deleting the call left
it passing. Proved by deleting only the call.

## Closed — i13 (worker death diagnostics, 2026-08-05)

Generation pass: codex-cli 0.146.0, `codex/gpt-5.6-sol/xhigh (inline,
session-only)/standard`, over `c1561ee..a2a713e` (GitHub #13). Verdict
`findings` (3), capability_ok true, SHAs pinned and matched. All three
admitted at intake and fixed one commit each. Plan:
`.agents/plans/issue-13-worker-death-diagnostics.md`.

Verification round 1 over `178acea..4f9284f` returned **reopened**
(guard_confirmed false) with two comments, both accepted without dispute:
i13-1's first fix only moved the forgery from the stderr text to the exit
code, and i13-2's guard was vacuous. Repaired in `e2c2902`.

**Verification round 3 over `4f9284f..e2c2902` returned `accepted`,
`guard_confirmed: true`, `capability_ok: true`, SHAs pinned and matched
(2026-08-05).** Both round-1 comments are confirmed closed:

- comment 1 (`SessionWorkerClient.cs:835`) — caller-controlled stderr and
  exit code no longer form the detail token; every death with evidence maps
  to the fixed `worker_exited_unexpectedly`, and the supervisor labels the
  reported facts as untrusted evidence.
- comment 2 (`UnixWorkerProcessLauncherTests.cs:112`) — the source-text
  guard would catch the named `ExitCode => _brokerExit.Result` regression.
  The reviewer names one residual hole, inherent to source-text guards: an
  indirect broker-exit alias or decoy text could evade it. Recorded as
  accepted residual risk, not an open finding.

| ID    | Severity | Impact (one line)                                          | Status |
|-------|----------|------------------------------------------------------------|--------|
| i13-1 | HIGH     | caller-controlled input reported as PTK's own classification of a worker death | `[x]` (fixed `178acea`, reopened, repaired `e2c2902`, accepted r3) |
| i13-2 | MEDIUM   | Unix reported the broker's constant as the worker's exit code | `[x]` (fixed `17a6a44`, guard replaced `e2c2902`, accepted r3) |
| i13-3 | MEDIUM   | the call after a death reported none of the known facts     | `[x]` (fixed `4f9284f`, accepted r3) |

**Why rounds 2a/2b died, resolved 2026-08-05.** Two earlier dispatches over
the same range died without emitting an envelope. The cause was not the
reviewer's transport and not the work: codex's `config.toml` registers the
`ptk` MCP server, and under `codex exec` (non-interactive, `approval:
never`) every `ptk_invoke`/`ptk_session` call is auto-denied with `user
cancelled MCP tool call`. The reviewer burned its budget retrying a tool it
could never call. Round 3 added `-c 'mcp_servers={}'` and returned a verdict
in 168s using 51,239 tokens.

The `refresh token was revoked` error codex logs continuously is confirmed
**noise** — a `pong` probe returned in ~3s while logging it, and round 3
completed while logging it. Do not treat it as a dispatch blocker.

**Dispatch recipe for this host** (codex is API-only via Portkey; there is
no `codex login` to run):

```
codex exec --cd <repo> -s read-only --color never -c 'mcp_servers={}' - < <prompt-file>
```

What is true without the reviewer: the full battery passes locally
(server 1,151/1,151, Pester 107 + 1 platform skip, SIEM 226/247 for this
host's symlink privilege, dependency audit clean), CI is green on all six
jobs at `e2c2902`, and every guard was proved by sabotage — including
re-running the reviewer's own sabotage for the replaced i13-2 guard.

Provenance note: codex's token was already revoked partway through the two
dispatches that *did* return envelopes (generation, and verification round
1). Both carried matching pins and `capability_ok: true`; recorded as a note
per the dispatch-provenance rule, not as an invalidation.

## Closed — hcc (harness-consent codereview, 2026-08-04)

Generation pass: codex-cli 0.146.0, `codex/gpt-5.6-sol/xhigh (inline,
session-only)/standard`, over `19201a1..092df3b` (kimi leg + rollback
inventory + consent feature). Verdict `findings` (5), capability_ok true,
SHAs pinned and matched. All five admitted at intake; hcc-6 joined as an
owner field report mid-loop. All six fixed (one commit each), guard-proved
(revert→fail→restore→pass, re-verified by the reviewer in its own
worktree), and verified **accepted**: hcc-1..5 at codex standard
(gpt-5.6-sol/high), hcc-6 at frontier (codex gpt-5.6-sol/xhigh,
escalated: T2 — the recorded claude frontier proved undispatchable: org
subscription access disabled; the owner named the codex frontier pair).
Per-finding detail: `.agents/review/findings/hcc-<n>.md`.

| ID    | Severity | Impact (one line)                                        | Status |
|-------|----------|----------------------------------------------------------|--------|
| hcc-1 | MEDIUM   | kimi uninstall deletes a pre-existing custom registration | `[x]` (codex/gpt-5.6-sol/high/std, accepted) |
| hcc-2 | MEDIUM   | claude payload gate accepts an empty bin dir; can register a dead binary + arm the hook | `[x]` (codex/gpt-5.6-sol/high/std, accepted) |
| hcc-3 | MEDIUM   | apostrophe in ptk path writes invalid kimi TOML           | `[x]` (codex/gpt-5.6-sol/high/std, accepted) |
| hcc-4 | LOW      | oversized consent number crashes instead of re-prompting  | `[x]` (codex/gpt-5.6-sol/high/std, accepted) |
| hcc-5 | LOW      | kimi skip blurb ignores KIMI_CODE_HOME                    | `[x]` (codex/gpt-5.6-sol/high/std, accepted) |
| hcc-6 | HIGH     | install rolls back when claude is detected without its CLI (owner field report) | `[x]` (codex/gpt-5.6-sol/xhigh/frontier, esc:T2, accepted) |

> **Dispositioned 2026-08-03.** Every accepted `opr-*` finding now carries
> exactly one disposition in `.agents/review/dispositions.md` — fixed,
> closed-removed, closed-out-of-scope, deferred to platform selection, or
> remaining-not-blocking. That file is the disposition of record.
>
> The "accepted and plan-gated" state is retired. It let a finding be recorded
> without ever being resolved, and produced 59 of them with zero repairs. Per
> `.agents/plans/rtk-router-delegation.md` §Process constraints, a defect found
> during implementation is now either fixed in the slice that found it or given
> a disposition — never a new gated entry.
>
> The narrative below is retained as the historical review record. In
> particular, every `**Open — opr-N**` bullet and every "Status: accepted; plan
> required" label below is frozen historical text from before that retirement —
> read the disposition file for an `opr-*` finding's current standing, never
> these bullets. The same applies to the push-state sentences in the older
> loop-closure entries: git owns push state, and those lines describe a moment,
> not the repo today.

**Open — opr-59:** LOW — a valid EOF read of a nonempty retained artifact reports nonzero `bytes` and zero `bytes_returned`, then falsely appends `(no captured bytes)`. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-59.md`.

**Checkpoint 2026-08-03 — OutputStore lines 1–1214 (one current formatter finding):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed exact source blob `7ca10b70` in bounded passes through line 1214 (line 694 is blank), integrating root ownership, disposal, publication, retention, delete-settling, capacity helpers, production callers, public list/status/read/search behavior, immutable off-gate reads, offset and UTF-8 validation, and model-facing pagination/search formatting. Earlier focused store/artifact evidence remains 99/99. An `IsReadableArtifact` candidate was invalid because the predicate requires a retained stream. A mechanically possible failed-delete retry loop was re-adjudicated `TEST_SEAM_ONLY`: every shipped artifact unlinks before publication and stores `Path=null`, so production retention cannot report delete failure. The core 865–1214 pass closed without a current finding after production-state integration rejected candidates involving public session-name filtering, visible capturing artifacts, the guarded 32-bit read cast, disposed retained streams, and live-growing search streams. `OutputTool.FormatRead` integration admitted LOW `opr-59`: a correct zero-byte EOF page for a nonempty artifact falsely appends `(no captured bytes)`. Existing `rbc-7`, `rbc-14`, `gh-16-1`, and `gh-16-2` remained excluded. No product or test file changed. The file has 2,195 lines; lines 1215–1433 were consulted only to establish atomic publication visibility, not completely reviewed for source defects. Review continues with lines 1215–1450: artifact creation, rendering, unlink-before-publication, flush/publication ordering, reservation claim, and capacity arithmetic; accepted `opr-36` remains excluded.

**Scope extension 2026-08-02 — opr-7:** Windows Actions run `30784201961` observed the quota control's final name between `FileMode.CreateNew` and owner-only DACL protection; a concurrent initializer treated the visible name as published and failed its pre-open DACL verification instead of retrying. Unchanged-code run `30786526767` and 20/20 focused local repetitions passed, confirming an intermittent race rather than clearing it. Claude Opus 5 classified this `OPR7_EXTENSION`, severity MEDIUM, within the existing atomic fully-written, flushed, protected control-publication repair; no duplicate finding or product change. Detail: `.agents/review/findings/opr-7.md`.

**Review closed 2026-08-02 — Program (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 49 lines of `server/PtkMcpServer/Program.cs` (blob `ba01d79bd07ec3fb80e540b1cd77ba64b31ddf9f`) in one complete exact-source composition pass integrated with worker classification/entry, `ChildStdinGuard`, `SupervisorLifecycle`, `SupervisorCallFilter`, `WorkerSupervisor` construction/disposal, DI boundary tests, and real stdio behavior. Focused worker-entry, startup, lifecycle, package-boundary, schema, and seam tests passed 68/68; exact-head CI `30782965551` passed server tests and the initialize/tools-list/tools-call handshake on Ubuntu, macOS, and Windows. Worker/supervisor selection, logging/stdout purity, timeout reads, singleton aliases, hosted-service identity, request scoping/filtering, transport stream custody, shutdown ownership, and disposal were traced. Existing `opr-1`, `opr-8`, `opr-9`, `opr-10`, and `opr-42` remained excluded. The Unix transport candidate was rejected because the pinned .NET 10 implementation duplicates fd 0 before `dup2`. The current cross-platform handshakes separately confirm shipped stdio transport behavior. Outcome: `no_current_findings`; no product or test file changed.

**Review closed 2026-08-02 — ExecutionPlanner (four current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 675 lines of `server/PtkMcpServer/Execution/ExecutionPlanner.cs` (blob `234c3833b6be6a22d6d984058090fd252f7244ba`) in three bounded exact-source/caller/test passes plus whole-file integration; focused planner, dispatch, and shell-dialect tests passed 103/103. Route normalization, direct/RTK/Bash construction, executable and cold-target identity binding, argument-mode fidelity, working-directory handling, wrapper exclusions, domain classification, guidance construction, snapshot resolution, and active `RunspaceHost`/`RtkProcessRunner` consumers were traced. Accepted HIGH `opr-58`, MEDIUM `opr-55` and `opr-56`, and LOW `opr-57`. Existing `s3-background-operator`, `s3-block-fidelity`, `s3-using-statement-fidelity`, `s3-wrapper-context`, `s3-rtk-preference-isolation`, `opr-4`, `opr-47`, and `opr-48` through `opr-51` remained excluded. A whole-file pass briefly disputed `opr-55`; target-visible UTF-8 Base64 probes under both supported native passing modes proved two argv elements while the planner emits one, and focused Opus re-adjudication corrected the dispute to `OPR55_CONFIRMED`. No additional current finding remained. The prior limited record below is expanded. Outcome: `four_current_findings`; no product or test file changed.

**Open — opr-58:** HIGH — post-success native-redirection guidance can recommend writing an application producer back into the same file it reads; following the PTK-authored suggestion truncates the input before launch. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-58.md`.

**Open — opr-57:** LOW — `ExecutionPlanner.ClassifyDomain` returns `PowerShell` for a redirected `CommandExpressionAst` before inspecting its redirections, persisting an incorrect `powershell` audit domain for file-writing expression pipelines. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-57.md`.

**Open — opr-56:** MEDIUM — `ExecutionPlanner` never checks `EndBlock.Traps`, so an otherwise eligible native script can route through RTK without its top-level trap handler and change warm-session error control flow. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-56.md`.

**Open — opr-55:** MEDIUM — `ExecutionPlanner.TryCreateRtkArgumentVector` merges a native parameter prefix and constant argument into one RTK argv element although PowerShell sends two, including attached and whitespace-separated colon forms, so routing changes target-visible argument boundaries. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-55.md`.

**Revalidation 2026-08-01 — ExecutionPlan (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) revalidated all 590 byte-unchanged lines of `server/PtkMcpServer/Execution/ExecutionPlan.cs` `6a66e20c4bc1f349b1314cc764728e0e6de17dd1` in two bounded exact-source/caller/test passes plus one whole-file integration adjudication; focused planner, dispatch, and shell-dialect tests passed 103/103. Construction lattice, immutable plan/dispatch state, route/path/fallback combinations, RTK/Bash identity provenance, working-directory binding, original-script dispatch truth, post-success guidance, machine codes, and active planner/runner consumers traced. Existing `s3-rtk-preference-isolation`, `opr-4`, and `opr-48` through `opr-51` excluded. Dormant candidates remain reactivation notes only: compare negotiated verified RTK digests if that constructor path returns; review fallback-less RTK dispatch if a production constructor gains it; review `NativeDirect` dispatch if it becomes constructible. Bash original-script provenance is explicitly special-cased, passed from `Plan.OriginalScript`, and integration-tested. Prior limited record below expanded. Outcome: `no_current_findings`; no product or test file changed.

**Revalidation 2026-08-01 — AuditExportCheckpoint (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) revalidated all 527 byte-unchanged lines of `server/PtkMcpServer/Audit/AuditExportCheckpoint.cs` at `497e0b2abee0522b26b976e65fb0c4d74df05157` in two bounded source/caller/test passes plus one whole-file production-reachability adjudication; focused codec, store, operator-disposition, and completed-chain-retirement tests passed 103/103. Immutable models, constructor invariants, canonical serialization/parsing, exact property sets, framing/BOM/byte/depth bounds, checkpoint/block cursor adjacency, UUID/timestamp/digest/detail/failure-class validation, overflow, exception projection, and current store/reader/admin/retirement/retention consumers were traced. Existing `opr-37`, `opr-38`, and repaired `s2-windows-checkpoint-durability` were excluded. A caller-pass candidate in `AuditExportCheckpointStore.CompleteClosedChain` was rejected as current: block creation/completion belongs to the removed exporter flow, `MarkChainComplete` has test-only callers, and the reader refuses a blocked cursor before producing an end proof. Re-review explicit blocked-completion refusal if exporter flow returns. The prior limited record below was expanded. Outcome: `no_current_findings`; no product or test file changed.

**Review 2026-08-01 — WorkerSupervisor (two current findings; one existing-finding extension):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 381 lines of `server/PtkMcpServer/Sessions/WorkerSupervisor.cs` at source-equivalent head `c9a7f51` in two bounded source/caller/test passes plus one whole-file integration adjudication; focused `WorkerSupervisorTests`, `SessionOperationsSeamTests`, and `NamedSessionSupervisorTests` passed 29/29. Construction, invoke/state/reset/session dispatch, lifecycle/disposal, cancellation and exception projection, route parsing, every response formatter, value provenance, protocol validation, named-session integration, current audit disablement, and public tool schema were traced. Two real shipped-server stdio probes proved worker text can forge exact PTK status/recovery lines (MEDIUM `opr-53`) and rejected raw session names can forge refusal lines because generated schema is not server-enforced (LOW `opr-54`). The latter proof also extended MEDIUM `opr-11` so its route repair requires active-runtime validation rather than schema-only enforcement. Existing MEDIUM `opr-42` remained excluded. The prior limited record at `3cd2482fdaed41349f327fe9ac22bd551218a5bb` was expanded. Outcome: `two_current_findings_one_existing_scope_extension`; no product or test file changed.

**Scope extension 2026-08-01 — opr-11:** a real shipped-server stdio probe established generated DataAnnotations are advisory schema, not an enforced input boundary. `opr-11`'s repair must therefore validate `auto|pwsh|rtk` in the active runtime path and refuse explicit unknown values in `WorkerSupervisor.ParseRoute`; `AllowedValues` remains client guidance only. Severity remains MEDIUM. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` at `09df6b8`. No product or test change.

**Open — opr-54:** LOW — at `c9a7f51`, generated session-name schema constraints are not enforced by the shipped server and `WorkerSupervisor.Refused` echoes rejected raw names into PTK directive lines; a real stdio probe injected a forged status line without starting a worker. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-54.md`.

**Open — opr-53:** MEDIUM — at `c9a7f51`, worker-controlled invocation and state text shares one unframed channel with PTK-authored retry, status, and recovery directives; a real stdio invoke preserved forged status and recovery lines beside the genuine recovery line. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-53.md`.

**Review 2026-08-01 — AuditEvidenceRetentionAudit (no additional distinct finding):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 188 lines of `server/PtkMcpServer/Audit/AuditEvidenceRetentionAudit.cs` at `2c6bb7a` in one complete exact-source pass integrated with the sole `ScriptEvidenceStore` deletion caller, exact content/identity revalidation, audit event validation, and focused tests; `AuditEvidenceRetentionTests` passed 15/15. Two-slot reservation, flushed intent before unlink, retained-versus-unknown failure proof, completion-append failure, original/audit exception precedence, hard-death states, context/sensitive fields, health projection, reason exhaustiveness, nulls, fatal classification, and concurrency were traced. Existing HIGH `opr-35` was excluded as a pre-selection eligibility defect. Prior limited review `735000e` was expanded. Outcome: `no_additional_distinct_finding`; no product or test file changed.

**Review 2026-08-01 — WorkerProcessExit (one current finding):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 179 lines of `server/PtkMcpServer/Worker/WorkerProcessExit.cs` at `c82d804` in one complete exact-source pass integrated with `WorkerProcessEntry`, every `WorkerServer` terminal producer, Windows/Unix bootstrap producers, and focused tests; sequential focused tests passed 78/78. Exit-kind/code parity, graceful zero/silence, producer-detail allowlists, hostile-detail sanitization, ASCII/byte bounds, single best-effort write, flush/lifetime, unknown enums, nulls, exceptions, and diagnostic-failure isolation were traced. The pass accepted LOW `opr-52` for four classified producer codes that collapse to generic terminal detail while preserving the correct exit class. Prior limited review `a930e27` was expanded. Outcome: `one_current_finding`; no product or test file changed.

**Open — opr-52:** LOW — at `e13bb8a`, four classified worker protocol/bootstrap failures retain correct exit codes but collapse to generic terminal diagnostic details because their producer codes are absent from `WorkerProcessExit` allowlists. Focused tests pass 78/78 but do not guard these four mappings. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-52.md`.

**Revalidation 2026-08-01 — SupervisorLifecycle (no findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) revalidated all 139 byte-unchanged lines of `server/PtkMcpServer/SupervisorLifecycle.cs` at `4306716` as the named complete-source subject integrated with `SupervisorCallFilter`, global registration, shutdown/lifetime ownership, and recent focused tests; sequential focused tests passed 21/21. Transactional admission, linked cancellation, active-call drain invariants, synchronous callbacks under the gate, stop idempotence, session shutdown/disposal ordering, lease exactly-once release, concurrent stop/dispose/admission, integer overflow, and the bounded ignored host stop token were traced. The prior limited record at `2ac1cd4` and recent dependency-level filter pass were expanded into explicit subject coverage. Outcome: `no_findings`; no product or test file changed.

**Review 2026-08-01 — AuditSpoolRecordCodec (no findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 127 lines of `server/PtkMcpServer/Audit/AuditSpoolRecordCodec.cs` at `9ac4960` in one complete exact-source pass integrated with live/closed readers, sink recovery, evidence scanning, exact envelope validation, and focused tests; codec-focused tests passed 42/42. Final hash-marker arithmetic, pre-hash reconstruction, exact-byte hashing, envelope shape, canonical boot UUIDv4 and event UUIDv7, sequence/previous-hash invariants, supported schemas, trailing bytes, exception normalization, allocation bounds, and corrupt/truncated behavior were traced. Prior limited review `4c39b9f` was expanded. Outcome: `no_findings`; no product or test file changed.

**Review 2026-08-01 — WorkerSession (no additional distinct finding):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 125 lines of `server/PtkMcpServer/Worker/WorkerSession.cs` at `6e2c1d4` in one complete exact-source pass integrated with `SessionRuntime`, `WorkerServer`, production construction, foreground artifact capture/codec, and focused tests; sequential focused tests passed 38/38. Request dispatch, deadline/token/route/timeout forwarding, capture ownership, artifact bounds and fallback, partial/error text retention, status/detail mapping, state truthfulness, shutdown/lifetime, nulls, exceptions, and disposal were traced. Existing MEDIUM `opr-4` was excluded without further repair-boundary extension. Prior limited review `5f2e1fb` was expanded. Outcome: `no_additional_distinct_finding`; no product or test file changed.

**Review 2026-08-01 — AuditSpoolSegmentIdentity (no findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 116 lines of `server/PtkMcpServer/Audit/AuditSpoolSegmentIdentity.cs` at `1a0d80f` in one complete exact-source pass integrated with scanner, checkpoint, writer-preparation, retirement, event-validation consumers, and focused tests; `AuditSpoolSegmentIdentityTests` passed 41/41. Exact filename length/layout, lowercase canonical UUIDv4 version/variant parsing, decimal index bounds and overflow, invariant formatting, fixed separators, path safety, failed-out reset, default-record reachability, equality, `ToString`, and maximum-index consistency were traced. Production `TryParse` consumers gate the out identity before use. Prior limited review `b4ffe87` was expanded. Outcome: `no_findings`; no product or test file changed.

**Review 2026-08-01 — AuditEvidenceOrphanReconciler (no additional current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 101 lines of `server/PtkMcpServer/Audit/AuditEvidenceOrphanReconciler.cs` at `a352bc2` in one complete exact-source pass integrated with both active static startup callers, `ScriptEvidenceStoreProvider`, protected spool topology, and focused tests; `AuditEvidenceOrphanReconcilerTests` passed 11/11. Root/control preparation, complete-versus-indeterminate proof, health and exception classification, writer admission, publication lock ordering, artifact mutation, cadence locks, retry scheduling, and overflow were traced. Existing MEDIUM `opr-6` and LOW `opr-34` were excluded without extension. Repo-wide C# inventory found the instance cadence API dormant; only static pre-writer reconciliation is production-reachable. Prior limited review `77a324e` was expanded. Outcome: `no_additional_current_findings`; no product or test file changed.

**Review 2026-08-01 — AuditAdminDispositionFailure (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 89 lines of `server/PtkMcpServer/Audit/AuditAdminDispositionFailure.cs` at `cea2ff8` in one complete exact-source pass integrated with active disposition administration, stage/effect classification, typed intent/outcome failures, and focused fault-injection tests; `AuditOperatorDispositionTests` passed 22/22. All eleven failure kinds and detail codes, durable-effect precedence, fixed nonsensitive exception messages, immutable typed kinds, preserved inner causes, reservation bounds, failure-audit ordering, and invalid-enum behavior were traced. Verified historical repair `s2-admin-disposition-failures` remained intact and was excluded; prior limited review `888914d` was expanded. Outcome: `no_current_findings`; no product or test file changed.

**Review 2026-08-01 — WorkerLaunchCommand (no additional distinct finding):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 68 lines of `server/PtkMcpServer/Worker/WorkerLaunchCommand.cs` at `a565184` in one complete exact-source pass integrated with `SessionWorkerLaunchCommand`, both platform launchers, argument/environment consumption, and focused tests; sequential focused tests passed 14/14. Null/NUL/fully-qualified validation, one-time enumerable snapshots, property immutability, argument boundaries, reserved bootstrap custody, environment rules, and prelaunch exception behavior were traced. Existing MEDIUM `opr-29` was excluded and its Unix launcher repair boundary was recorded separately at `a565184`; the prior limited review at `d847df2` was expanded. Outcome: `no_additional_distinct_finding`; no product or test file changed.

**Review 2026-08-01 — AuditOutputRequestProtector (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 67 lines of `server/PtkMcpServer/Audit/AuditOutputRequestProtector.cs` at `6b349b8` in one complete exact-source pass integrated with its `AuditCallMetadataCapture` consumer, 256-bit output-handle generation, and focused tests; `AuditCallMetadataTests` passed 14/14. Key generation/copying/zeroization, strict UTF-8, domain separation, SHA-256 handle digest, HMAC pattern fingerprint, allocation bounds, exceptions, and fail-closed absent-protector behavior were traced. A repo-wide C# reachability inventory found no production protector construction and no production metadata-capture call; the prior limited review at `a2c343f` was expanded. Concurrent disposal can yield a latent incorrect fingerprint, but is test-only/future hardening under current reachability, not a current product defect. Outcome: `no_current_findings`; no product or test file changed.

**Review 2026-08-01 — InvokeTool (no findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 65 lines of `server/PtkMcpServer/Tools/InvokeTool.cs` at `77aa9a9` in one complete exact-source pass integrated with assembly registration, `ISessionOperations`, `WorkerSupervisor`, schema separation, and focused runtime tests; sequential focused tests passed 90/90. MCP-visible parameters, defaults, annotations, service injection, exact seven-argument forwarding, cancellation, return/exception behavior, and every public behavior claim were traced. The prior limited review at `f0418b0` was expanded; runtime-labeled route/timeout validation is intentional at this boundary. Outcome: `no_findings`; no product or test file changed.

**Review 2026-08-01 — AuditAdminFailure (no findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 58 lines of `server/PtkMcpServer/Audit/AuditAdminFailure.cs` at `11bad9d` in one complete exact-source pass integrated with evidence administration, milestone-sensitive classification, failure-event publication, and focused fault-injection tests; `AuditAdminEvidenceAccessTests` passed 20/20. All twelve admin detail codes and four destination failure kinds map exhaustively and distinctly; disclosure/publication wording, immutable failure kind, fixed nonsensitive public message, preserved inner cause, and invalid-enum behavior were traced. The prior limited review at `618f007` was expanded. Outcome: `no_findings`; no product or test file changed.

**Review 2026-08-01 — SupervisorCallFilter (no findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 55 lines of `server/PtkMcpServer/SupervisorCallFilter.cs` at `f1cf11d` in one complete exact-source pass integrated with global tool registration, `SupervisorLifecycle`, `SupervisorCallLease`, shutdown/disposal, and current cross-platform CI; sequential focused tests passed 21/21. Dependency resolution, admission/refusal atomicity, linked cancellation, active-call draining, result/null/exception paths, lease idempotence, and refusal truthfulness were traced. The prior limited review at `6675e37` was expanded; advisory cancellation and unbounded-tool candidates lacked a violated contract or shown production trigger. Outcome: `no_findings`; no product or test file changed.

**Review 2026-08-01 — AuditEffectiveIdentity (no findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 30 lines of `server/PtkMcpServer/Audit/AuditEffectiveIdentity.cs` at `92a60aa` in one complete exact-source pass integrated with `AuditAdminOperations`, audit schema validation, serialization, and current cross-platform CI; sequential focused audit-admin tests passed 55/55. Windows current-token SID capture, Unix effective UID P/Invoke portability, invariant normalization, lifetime, disposal, schema bounds, fail-closed behavior, and attribution wording were traced. The prior limited review at `c1d83e1` was expanded; identity-mutation staleness lacked a production path. Outcome: `no_findings`; no product or test file changed.

**Review 2026-08-01 — BashExecutableIdentity (no findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 26 lines of `server/PtkMcpServer/Execution/BashExecutableIdentity.cs` at `9d5a443` in one complete exact-source pass integrated with `ExecutableFileIdentity`, production PowerShell application resolution, planner/runner launch checks, and focused tests; sequential `BashProcessRunnerTests` and `ExecutionPlannerTests` passed 102/102. Canonical final-target capture, path/digest/Unix-mode revalidation, typed fail-closed no-start behavior, and digest audit identity were traced. The prior limited review at `385db4c` was expanded; test-only override, unused record equality, and same-byte/different-path candidates were rejected as production-unreachable or non-impacting. Outcome: `no_findings`; no product or test file changed.

**Revalidation 2026-08-01 — RawUsageCounter (prior complete-source no-findings record remains valid):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) revalidated all 17 byte-unchanged lines of `server/PtkMcpServer/RawUsageCounter.cs` at `10a3547` against both active `SessionRuntime` increment boundaries, state reporting, lifecycle ownership, and focused tests; `RawUsageTests` passed 6/6. The prior complete-source review at `469959c` remains authoritative. Atomicity, visibility, exactly-once user-boundary increments, internal-probe exclusion, and signed counter wrap were traced. Wrap requires more than two billion real per-session invocations and can only make inert compatibility telemetry negative; no routing, execution, audit, shaping, or lifecycle decision consumes the value. Outcome: `no_findings`; no product or test file changed. This also corrected the semantic-inventory heuristic: wording that says complete source and bounded caller/test evidence is sufficient even without a literal `whole-file` token.

**Review 2026-08-01 — AuditStartupConfiguration (no additional distinct finding):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 71 lines of `server/PtkMcpServer/Audit/AuditStartupConfiguration.cs` at `8648f37` in one complete exact-source pass integrated with the separate `PtkAuditAdmin` caller, `AuditOptions`, checkpoint snapshot reader, and focused tests; `AuditStartupConfigurationTests` passed 3/3. Legacy root loading, permanent-block checkpoint/event binding, anchored option reconstruction, bounds/retention preservation, exception behavior, and disposal were traced. Existing MEDIUM `opr-5` was excluded. Dummy probe identity, anchored reconstruction, protected-checkpoint identity input, mismatch checks, stale-read/apply behavior, malformed paths, whitespace defaults, and no-op disposal candidates were rejected as path-only, validated, fail-closed, caller-rechecked, existing-boundary, or non-impacting. Outcome: `no_additional_distinct_finding`; no product or test file changed. The post-record filename-level inventory found no production `.cs` basename absent from this index; semantic whole-file quality auditing continues separately.

**Review 2026-08-01 — ColdCommandResolution (four additional current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 268 lines of `server/PtkMcpServer/Execution/ColdCommandResolution.cs` at `ca5384f` in two bounded exact-source passes plus one whole-file integration pass against `ExecutionPlanner`, `ExecutionPlan`, `ExecutableFileIdentity`, and resolver/planner tests; focused tests passed 95/95. PowerShell PATH tokenization, Windows/PATHEXT and Unix ordering, executable access, relative-path bases, target capture, live re-resolution, file identity, and plan invariants were traced. Upstream PowerShell/PowerShell-Native source, real Windows child-process probes, and a built-assembly identity probe adjudicated platform semantics. Existing `opr-2` and refuted-as-defect `rbc-13` were excluded. Integration accepted MEDIUM `opr-48`, `opr-49`, and `opr-50`, plus LOW `opr-51`; home expansion, invalid-path, redundant-scan, reserved-device, and test-only platform-override candidates were rejected as upstream parity, production-unreachable, conservative, or non-impacting. Outcome: `four_additional_current_findings`; no product or test file changed.

**Open — opr-51:** LOW — `ColdCommandTargetIdentity.Matches` uses case-sensitive record equality for Windows executable paths, so casing-only resolution changes spuriously produce no-start. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-51.md`.

**Open — opr-50:** MEDIUM — Windows drive-relative command forms such as `C:tool` bypass the cold resolver's bare-name guard and bind server drive state instead of child location semantics. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-50.md`.

**Open — opr-49:** MEDIUM — Windows rooted or drive-relative PATH entries are normalized against server process drive state instead of the audited child working directory, breaking cold resolver/child parity and weakening the target-identity contract. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-49.md`.

**Open — opr-48:** MEDIUM — Unix cold command resolution accepts any execute bit instead of testing real-identity `X_OK`, so prepare and commit can bind the same PATH file PowerShell would skip. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-48.md`.

**Review 2026-08-01 — BashProcessRunner (one additional distinct finding; two existing scope extensions):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 803 lines of `server/PtkMcpServer/Execution/BashProcessRunner.cs` at `94ff698` in three bounded exact-source passes plus one whole-file integration pass against `ProcessTreeContainment`, RTK parity, invoke models, dispatch, and the production `RunspaceHost` caller; focused Bash, RTK, and containment tests passed 27/27. Validation and execution startup, environment scrubbing, audit barriers, deadlines and cancellation, process tracking, bounded output capture, kill/drain escalation, result projection, and helper contracts were traced. Existing `opr-4`, `rbc-1`, resolved `rbc-6`, and `rbc-15` boundaries were excluded. Integration accepted slow validator-start audit flushing as MEDIUM `opr-47`, extended `opr-4` to Bash pre-start cause classification, and extended LOW `opr-40` to Bash execution's eager capture allocation. All other start ambiguity, containment, validation classification, stream lifetime, partial-output, environment, UTF-8, and cleanup hypotheses were rejected as guarded, conservative contract, no-throw, fail-closed, existing-boundary, or unsupported. Outcome: `one_additional_distinct_finding_two_existing_scope_extensions`; no product or test file changed.

**Scope extension 2026-08-01 — opr-40:** the `BashProcessRunner` execution path uses the same 4 MiB ceiling as the eager initial `MemoryStream` capacity for both redirected streams, allocating roughly 8 MiB before reading output. Independently accepted as the same LOW allocation-shape defect, not a new finding. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` at `94ff698`. Detail: `.agents/review/findings/opr-40.md`. No product or test change.

**Scope extension 2026-08-01 — opr-4:** current `BashProcessRunner` pre-start guards snapshot cancellation, but `BudgetFailure` independently re-reads the deadline, so a no-start Bash result can combine a timeout outcome with cancellation audit detail. Independently accepted as the same LOW extension of the existing MEDIUM immutable-cause defect, not a new finding. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` at `94ff698`. Detail: `.agents/review/findings/opr-4.md`. No product or test change.

**Open — opr-47:** MEDIUM — a fast, determinate `bash -n` result can be discarded as a validator timeout solely because the durable validator-start audit record finishes after the validator's fixed process budget. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` at `94ff698`. Detail: `.agents/review/findings/opr-47.md`.

**Review 2026-08-01 — AuditJournal (no additional distinct findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 897 lines of `server/PtkMcpServer/Audit/AuditJournal.cs` at `54822ee` in three bounded exact-source passes plus one whole-file integration pass against audit event serialization, health synchronization, journal construction, live spool reading, and production call-context recovery; focused journal, health, committed-spool, evidence-retention, and call-context tests passed 59/59. Sink contracts, reservation ownership and accounting, committed-spool reads, evidence scans, admission and external recovery, append/flush poisoning, automatic transitions, monotonic identity/time construction, health metrics, and disposal were traced. Existing `opr-35` and `opr-36` were direct exclusions; `opr-34`, `opr-37`, fixed `s2-anchored-temp-recovery`, and resolved `rbc-3` remained adjacent boundaries. Integration rejected all additional reservation, recovery, health-publication, identity, test-sink, automatic-transition, spool-read, and diagnostic hypotheses as guarded, synchronized, bounded, test-only, deliberate contract, or production-unreachable. Outcome: `no_additional_distinct_finding`; no product or test file changed.

**Review 2026-08-01 — WindowsProcessTreeSupervisor (no additional distinct findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 621 lines of `server/PtkMcpServer/Worker/WindowsProcessTreeSupervisor.cs` at `32c5748` in three bounded exact-source passes plus one whole-file integration pass against `WindowsWorkerNative`, the single-domain launch authority, and production session-client callers; focused supervisor, containment, lifecycle, nested-job, and bootstrap tests passed 85/85. Atomic job-list creation, job-policy verification, empty observation, launch rollback, suspended proof mode, containment, later proof forwarding, process/job/pipe ownership, disposal, native error projection, and concurrent entry points were traced. Existing `opr-23` was excluded; the separate `rbc-5` JobManager background-job boundary was not merged. Integration rejected all additional containment, ownership, race, diagnostic, and disposal hypotheses as atomic-contract-safe, one-shot, idempotent, deliberately fail-closed, fatal-only, or production-unreachable. Outcome: `no_additional_distinct_finding`; no product or test file changed.

**Re-review 2026-08-01 — UnixWorkerProcessLauncher (no additional distinct findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 1,207 lines of `server/PtkMcpServer/Worker/UnixWorkerProcessLauncher.cs` at `431183f` in four bounded exact-source passes plus one whole-file integration pass against the native broker, containment registry, launch authority, and production session-client callers; focused launcher, registry, bootstrap, and broker-adjacent tests passed 20/20. Spawn and descriptor ownership, broker handshake and failure projection, registration ordering, group arming and release, exit observation, shutdown containment, empty-domain forwarding, pipe disposal, protocol framing, Unix identity/process-group probes, `posix_spawn`, `waitpid`, and native allocation lifetimes were traced. Existing `opr-14`, `opr-24` through `opr-29`, and `opr-46` were exclusions. Integration rejected all additional lifecycle, race, diagnostic, value-domain, framing, and descriptor hypotheses as contract-safe, broker-contained, existing-boundary, or production-unreachable. Outcome: `no_additional_distinct_finding`; no product or test file changed.

**Open — opr-46:** LOW — a PID recycled between separate identity and group probes can latch a false containment escape and return `descendants_unknown`, although exact background proof still releases the registry. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` at `afbf64f`. Detail: `.agents/review/findings/opr-46.md`.

**Open — opr-45:** LOW — `TrustedPreflightClassifier` flattens nested local definitions, so a child-scope `function export` can suppress later top-level Bash `export` evidence even though PowerShell cannot resolve that function. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` at `864cfe2`. Detail: `.agents/review/findings/opr-45.md`.

**Open — opr-44:** MEDIUM — `TrustedPreflightClassifier` recognizes only the literal `pipefail` after Bash `set -o`; other named Bash options such as `errexit` produce `Finding=null`, then fail under PowerShell's stock `Set-Variable` alias. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` at `864cfe2`. Detail: `.agents/review/findings/opr-44.md`.

**Open — opr-43:** HIGH — the fatal-parse branch returns before the trusted command-evidence scan, so a valid Bash script with a recovered `set` command plus `case ... esac` gets `Finding=null` and falls through to PowerShell instead of available Bash delegation. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` at `864cfe2`. Detail: `.agents/review/findings/opr-43.md`.

**Re-review 2026-08-01 — UnixWorkerContainmentRegistry (one additional current finding):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 515 lines of `server/PtkMcpServer/Worker/UnixWorkerContainmentRegistry.cs` at `afbf64f` in two bounded exact-source passes plus one whole-file integration pass; focused registry and launcher tests passed 12/12. Pending/armed registration, observation and confirmation loops, snapshot and descendant closure, exact identities and process groups, escaped tracking, completion tasks, disposal, generation races, native contracts, and production launcher/supervisor callers were traced. Existing `opr-15`, `opr-26`, `opr-30`, and `opr-31` were exclusions. Independent adjudication rejected stale-generation completion, registry-disposal races, unresolved-on-dispose, and post-completion wait candidates as reference-safe or production-unreachable. Integration accepted the torn identity/group PID-reuse diagnostic race as LOW `opr-46`, explicitly without an orphan or permanent-block claim. Outcome: `one_additional_current_finding`; no product or test file changed.

**Re-review 2026-08-01 — SessionWorkerClient (no additional distinct findings; two existing scope extensions):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 888 lines of `server/PtkMcpServer/Worker/SessionWorkerClient.cs` at `c8e6c4e` in three bounded exact-source passes plus one whole-file integration pass; focused client, protocol, worker-server, and named-supervisor tests passed 80/80. Factory launch/initialization, request framing and correlation, artifact delivery, write-attempt boundaries, cancellation, state, graceful stop, containment, disposal, fatal/exit observation, and production callers were traced. Existing `opr-19` through `opr-22` were exclusions. Integration accepted prompt-exit/failed-handshake containment boundaries as an `opr-19` extension and successful-cleanup deadline crossing as an `opr-22` extension. Independent adjudication rejected initialization-detail loss, extra client deadlines, pre-initialization calls, never-initialized reuse, poison-before-cancel, cancel-ID drift, disposal orphaning, and confirmed-containment downgrade as unreachable, redundant, lease-invariant, existing-boundary, or current-inert through callers. Outcome: `no_additional_distinct_finding_two_existing_scope_extensions`; no product or test file changed.

**Re-review 2026-08-01 — TrustedPreflightClassifier (three current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 448 lines of `server/PtkMcpServer/TrustedPreflightClassifier.cs` at `864cfe2` in two bounded source/caller/test passes plus one whole-file AST/runtime integration pass; focused `TrustedPreflightClassifierTests` and `ShellDialectWiringTests` passed 88/88. Parsing and recovery ASTs, trusted command resolution, local-definition exemptions, shell-shape predicates, and active `RunspaceHost` planning were traced. Exact built-assembly reflection and PowerShell runtime probes covered fatal-parse recovery, named `set -o` values, nested definition visibility, and valid PowerShell controls. Existing `opr-17`, `opr-32`, and `opr-33` overlap was excluded. Outcomes: HIGH `opr-43`, MEDIUM `opr-44`, LOW `opr-45`; an error-adjacent recursive-keyword candidate was rejected because the affected script was invalid under both dialects and had no current observable divergence. No additional distinct finding; no product or test file changed.

**Re-review 2026-08-01 — NamedSessionSupervisor (one additional current finding):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 1,231 lines of `server/PtkMcpServer/Sessions/NamedSessionSupervisor.cs` at `ca7fe85` in three bounded source/caller/test passes plus one whole-file lifecycle/concurrency integration pass; focused supervisor, lifecycle, artifact-capture, and real-process tests passed 36/36. Public list/open/invoke/state/reset/close operations, slot start/replace/recover/fault/cool transitions, operation leases, output recovery, containment proof, stale callbacks, automatic recovery, shutdown/disposal, active `WorkerSupervisor` rendering, and concrete production worker behavior were traced. Existing `opr-19` through `opr-23`, `gh-16-1`, and `gh-16-2` were excluded. Independent cluster adjudication and integration rejected bounded output completion, semaphore-invariant, late-start cleanup, stale-close, start-proof overlap, containment-observer recovery, shutdown-hardening, and unused containment-reason candidates as bounded, invariant-safe, fail-closed, current-inert, or unreachable through the concrete worker. The active caller integration accepted the post-lease state-summary registry race as MEDIUM `opr-42`. Outcome: `one_additional_current_finding`; no product or test file changed.

**Open — opr-42:** MEDIUM — after a worker-state query releases its supervisor lease, concurrent close/shutdown can make `WorkerSupervisor.StateAsync` fault at `List().Single`; same-name replacement can mix incarnations and a second list snapshot can tear the rendered count. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` at `ca7fe85`. Detail: `.agents/review/findings/opr-42.md`.

**Re-review 2026-08-01 — DefaultSessionRuntimeFactory (no additional distinct finding; one existing-finding extension):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 44 lines of `server/PtkMcpServer/Sessions/DefaultSessionRuntimeFactory.cs` at `c5f9536` in one complete source/caller/protocol pass plus independent production-reachability adjudication; focused `WorkerProcessEntryTests` and `WorkerOperationProtocolTests` passed 26/26. Environment defaults, parsing/conversion, supervisor and worker parity, protocol-limit construction, cancellation checks, `RunspaceHost` ownership transfer, failure disposal, and current `Program`/worker entry paths were traced. Existing `opr-9` and `opr-10` were excluded. The only candidate—finite fractional or sub-millisecond values surviving the parser before failing the whole-second protocol contract—was independently merged into MEDIUM `opr-10` because it has the same weak predicate, production path, startup failure, and repair site. Construction ownership and cancellation candidates were clean. Outcome: `no_additional_distinct_finding_one_existing_scope_extension`; no product or test file changed.

**Scope extension 2026-08-01 — opr-10:** `DefaultSessionRuntimeFactory` also admits finite fractional and sub-millisecond positive values that later violate the worker protocol's integral 1–86,400-second contract and abort startup. Independently merged at unchanged MEDIUM severity because this is the same weak predicate, failure path, and repair site. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` at `c5f9536`. Detail: `.agents/review/findings/opr-10.md`. No product or test change.

**Re-review 2026-08-01 — RtkProcessRunner (two additional current findings, one existing-finding extension, one comment correction):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 442 lines of `server/PtkMcpServer/Execution/RtkProcessRunner.cs` at `2debaf6` in two bounded source/caller/test passes plus one whole-file dispatch integration pass; focused runner, containment, and invoke tests passed 76/76. Pinned launch identity, isolated environment, broker-established process-group inheritance, stdin closure, bounded full-stream draining, cancellation/deadline classification, kill/drain cleanup, result projection, and active `RunspaceHost` dispatch were traced. Existing `opr-4`, verified `s3-rtk-preference-isolation`, resolved `rbc-1`, `rbc-6`, `rbc-15`, and the documented executable identity/start race were excluded. One stale containment comment was corrected at `e83c209` after separate Opus review. Independent adjudication accepted eager capture allocation as LOW `opr-40`, accepted pre-start warm-state mutation as MEDIUM `opr-41`, and extended `opr-4` at LOW with the same immutable-cause root. Pre-start containment failure, kill/drain exit observation, bounded-reader lifetime, and fixed cleanup-delay candidates were rejected as intentional fail-fatal behavior, already re-observed state, bounded disposal, or bounded overhead. Outcome: `two_additional_current_findings_one_existing_scope_extension_one_comment_correction`; no runtime behavior or test file changed.

**Open — opr-41:** MEDIUM — an RTK cancellation or deadline can win after `$LASTEXITCODE` reset but before process start, and the no-fallback `NotStarted` path returns with a fabricated zero; the reset pipeline can also replace prior `$?` with success. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` at `2debaf6`. Detail: `.agents/review/findings/opr-41.md`.

**Scope extension 2026-08-01 — opr-4:** the current `RtkProcessRunner` pre-start budget guards snapshot cancellation but `BudgetFailure` re-reads the deadline, so one no-start result can combine a timeout outcome with a cancellation audit detail. Independently accepted at LOW as the same immutable-cause defect, not a new finding. The existing MEDIUM severity and plan gate remain. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` at `2debaf6`. Detail: `.agents/review/findings/opr-4.md`. No product or test change.

**Open — opr-40:** LOW — every direct RTK or Bash execution eagerly allocates two 4 MiB capture buffers before reading stdout or stderr, causing avoidable 8 MiB large-object-heap churn for even tiny commands. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-40.md`.

**Re-review 2026-08-01 — OutputRootLease (one additional current finding):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 433 lines of `server/PtkMcpServer/Execution/OutputRootLease.cs` at `810ed95` in two bounded source/caller/test passes plus one whole-file active-caller integration pass; focused lease and output-store tests passed 27/27. Root identity and creation, marker serialization/durability, local and cross-process exclusion, stale-root validation/reclamation, artifact grammar and retained identity, disposal, retry behavior, and Windows sharing versus Unix locking were traced. Verified `opr-3` was excluded. One markerless-root candidate was independently accepted at LOW as `opr-39`; a closed-descriptor unlock candidate was rejected as a compound hypothetical. Outcome: `one_additional_current_finding`; no product or test change and no guard claim.

**Open — opr-39:** LOW — stale pre-lock artifact snapshot plus marker-before-directory deletion can leave a markerless output root that every future reclaimer preserves. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-39.md`.

**Re-review 2026-08-01 — ChildStdinGuard (no additional current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 68 lines of `server/PtkMcpServer/ChildStdinGuard.cs` at `43d9ef2` in one complete source/startup-caller/test pass; focused stdin-guard and worker-entry tests passed 22/22. Unix descriptor replacement/ownership, Windows NUL handle lifetime/inheritance/publication ordering, native failure containment, the sole supervisor startup call after MCP stream capture, and child-process EOF behavior were traced. Existing `opr-1`, `opr-8`, and verified `rbc-1` overlap were excluded. Outcome: `no_additional_current_findings`; no detail file, product change, or guard claim.

**Re-review 2026-08-01 — ScriptEvidenceStore (no additional current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 1,613 lines of `server/PtkMcpServer/Audit/ScriptEvidenceStore.cs` at `1731698` in eight bounded source/caller/test passes plus three thematic publication/anchoring, retention/reconciliation, and protected-read/parser/quota-lock integration passes; the focused seven-class evidence suite passed 89/89. Construction, strict UTF-8 publication, quota and returned-lease lifetime, state transitions, anchoring, reconciliation, capacity/age retention, audited deletion, protected inventory/identity, exact reads, filename grammars, and current administration versus dormant audit-call reachability were traced. Verified `s2-admin-evidence-failures` and existing `opr-35` overlap were excluded. Nine pass candidates were independently rejected as production-unreachable, externally indistinguishable, covered by outer cleanup, or reachable only through the excluded hostile same-user post-open race. Outcome: `no_additional_current_findings`; no detail file, product change, or guard claim.

**Re-review 2026-08-01 — ScriptEvidenceStoreProvider (no additional current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 243 lines of `server/PtkMcpServer/Audit/ScriptEvidenceStoreProvider.cs` at `36040e7` in three bounded source/underlying-contract/test passes plus one whole-file current-production call-graph pass; focused publication, orphan-reconciliation, retention, and store tests passed 50/50. Lazy construction, synchronization, returned publication/anchor lifetime, exception/reset policy, absent-root shortcuts, reconciliation and before-writer admission, retention, current administration callers, and dormant `AuditCallContext` surfaces were traced. Existing `opr-6` was excluded. Independent adjudication rejected missing `AuditUnavailableException` passthrough candidates as current-inert and `MarkAnchored` classification/reset candidates as production-unreachable. Outcome: `no_additional_current_findings`; no detail file, product change, or guard claim.

**Re-review 2026-08-01 — AuditSpoolQuotaLease (no additional current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 254 lines of `server/PtkMcpServer/Audit/AuditSpoolQuotaLease.cs` at `2cfc232` in two bounded source/test passes plus one whole-file active-caller integration pass; focused quota tests passed 10/10. Canonical protected root/control creation and validation, exclusive create/acquire/try-acquire, bounded retry/sharing classification, retained control identity/marker verification, ownership/disposal, and current writer, scanner, retirement, and administration callers were traced. Existing `opr-7` was excluded. Outcome: `no_additional_current_findings`; no detail file, product change, or guard claim.

**Re-review 2026-08-01 — AuditEvidenceSpoolScanner (no additional current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 731 lines of `server/PtkMcpServer/Audit/AuditEvidenceSpoolScanner.cs` at `ff86132` in four bounded source/caller/test passes plus one whole-file active-caller integration pass; focused scanner/orphan/retention tests passed 31/31. Current `AuditJournal` journal-gated scanning and `ScriptEvidenceStore`/`AuditEvidenceOrphanReconciler` pre-writer flows were traced across protected bounded inventory, topology and retained identity, closed/live segment scanning, committed-tail semantics, bounded exact record reads, v1/v2 envelope shape, evidence-reference aggregation/completeness, and disposal. Existing `opr-34` and `opr-35` overlap was excluded. Outcome: `no_additional_current_findings`; no detail file, product change, or guard claim.

**Re-review 2026-08-01 — AuditCallMetadata (no additional current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 710 lines of `server/PtkMcpServer/Audit/AuditCallMetadata.cs` at `5180d0b` in four bounded source/test passes plus one whole-file production-reachability pass; focused metadata tests passed 14/14. Tool/action dispatch, reservation sizing, field admission, invoke/state/reset/session/output/job capture, actor/session context, UTF-8/scalar constraints, route/session grammar, timeouts, deadlines, and failure projection were covered. Repository-wide caller evidence confirms `AuditCallMetadataCapture.TryCapture` is dormant after intentional runtime-audit removal: only tests and its own definition remain. Existing `opr-11`, `opr-12`, and verified `s2-job-id-audit-poison` were excluded. Outcome: `no_additional_current_findings`; any reactivation must re-review the component against restored production callers. No detail file, product change, or guard claim.

**Reopened 2026-08-02 — ci-slow-seal-2:** LOW — macOS run `30784201961` failed unchanged test code at `3.1263559s` against the three-second bound because the stopwatch begins before invocation while the bounded seal delay begins only after execution and rendering. Unchanged-code run `30786526767` passed all six jobs. Claude Opus 5 classified this a recurrence of the same test-guard defect, not a product regression or new finding; further tolerance widening is prohibited and a fresh approved plan must re-anchor timing at the existing seal-entry witness. The completed historical plan is `.agents/plans/ci-slow-seal-elapsed-headroom.md`; no product or test change was made. Detail: `.agents/review/findings/ci-slow-seal-2.md`.

**Re-review 2026-08-01 — SessionRuntime (no additional current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 491 lines of `server/PtkMcpServer/Sessions/SessionRuntime.cs` at `a03a558` in four bounded source/caller/test passes plus one whole-file production-call-graph pass; focused runtime/invoke/state/reset/raw-usage/shell-routing tests passed 109/109. Current `WorkerProcessEntry` → `WorkerSession` construction and adapter paths were traced across ownership/disposal/shutdown, invocation/result/outcome/provenance mapping, route normalization, state probe/cache concurrency, environment drift, reset, cancellation, raw usage, and intentional audit-null routing; direct-runtime compatibility/test surfaces were distinguished from the active server path. Existing `opr-18` was excluded, and a factory-only rediscovery of existing `opr-10` was rejected as non-distinct. Outcome: `no_additional_current_findings`; no detail file, product change, or guard claim.

**Review 2026-08-01 — FileAuditJournalSink (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 1,707 lines of `server/PtkMcpServer/Audit/FileAuditJournalSink.cs` at `a78b539` in eleven bounded source/caller/test passes plus four thematic cross-boundary integration passes; focused tests passed 50/50. Current local and anchored paths were traced across protected construction, quota ownership/admission, reservation/rotation, append/flush/committed reads, staged checkpoint activation, segment allocation/publication, retained-prefix constraints, close/trim/macOS compaction, retention/deletion, crash-temporary recovery, canonical chain validation, path/link checks, and platform-native allocation helpers. Post-publication reopen and closed-delete substitution candidates were rejected because legitimate peers are quota-serialized and remaining substitution requires the explicitly excluded hostile same-user store owner; the Linux raw-descriptor candidate was rejected because its sole caller roots the stream through a subsequent `finally` disposal. Verified prior `s2-anchored-temp-recovery` was excluded. Outcome: `no_current_findings`; no detail file, product change, or guard claim.

**Review 2026-08-01 — SecureAuditStorage (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 1,619 lines of `server/PtkMcpServer/Audit/SecureAuditStorage.cs` at `867594b` in seven bounded source/caller/test passes plus two split cross-boundary integration passes; focused tests passed 15/15. Current Linux, macOS, and Windows paths were traced across protected root/file creation, direct-child/link refusal, owner/mode/DACL/extended-ACL enforcement, retained identities and unlink proof, bounded reads, atomic publish/replace/durability, alias deletion, stat/native ABI layouts, token SID/DACL construction, and file/security P/Invokes. The Windows post-rename flush candidate was rejected because the callback is the recovery-safe durability seam; macOS missing-path ACL success is closed by the immediate owner recheck, and symlink substitution requires an excluded hostile same-user race. Verified prior `s2-windows-checkpoint-durability` was excluded. Outcome: `no_current_findings`; no detail file, product change, or guard claim.

**Review 2026-08-01 — AuditEvent (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 1,227 lines of `server/PtkMcpServer/Audit/AuditEvent.cs` at `23093ff` in five bounded source/caller/test passes plus two split cross-boundary integration passes; focused tests passed 13/13. Current active `AuditAdminOperations` and journal lifecycle/retention producers were traced across the complete event model and vocabularies, semantic field applicability, disposition and evidence-retention facts, health/coverage invariants, normalized arrays, canonical pre-hash serialization, UTF-8 scalar/path limits, UUID/time/number rules, ASCII token grammars, enum membership, and lower-hex validation. Dormant `AuditCallContext` scenarios and verified prior `s2-job-id-audit-poison` were excluded. Outcome: `no_current_findings`; no detail file, product change, or guard claim.

**Review 2026-08-01 — AuditOperatorDispositionOutcome (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 1,110 lines of `server/PtkMcpServer/Audit/AuditOperatorDispositionOutcome.cs` at `1dbd980` in five bounded source/caller/test passes plus two split cross-boundary integration passes. Focused operator-disposition tests passed 22/22 and completed-chain retirement tests 13/13. Current-production adjudication covered outcome commit/open idempotence, exact completed-event and intent fact binding, atomic alias publication/recovery, canonical bytes/digests, bounded outcome/control inventory, typed failures, deletion, and completed-chain retirement eligibility/cleanup. Existing `s2-admin-disposition-failures` and the intent-only `opr-38` were excluded. Outcome: `no_current_findings`; no detail file, product change, or guard claim.

**Review 2026-08-01 — AuditOperatorDispositionIntent (one current finding):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 1,057 lines of `server/PtkMcpServer/Audit/AuditOperatorDispositionIntent.cs` at source commit `9d29281` in five bounded source/caller/test passes plus whole-file cross-boundary adjudication. Focused operator-disposition tests passed 22/22. Active `PtkAuditAdmin`, checkpoint application, outcome recovery, and startup retirement paths were traced across proof identity, exact target compatibility, bounded enumeration, canonical publication/read/recovery, consume/delete, strict parsing, and validation. Outcome: exactly one current finding, `opr-38` LOW for a trailing-LF acknowledged-gap reason that persists durably and conflicts with its clean retry spelling. A `FirstFailureUtc` offset candidate was rejected because intent validation delegates to the strict blocked-record constructor. No product or test change.

**Open — opr-38:** LOW — the acknowledged-gap reason regex uses .NET's newline-permissive `$` anchor, so the shipped administration CLI can publish a trailing-LF reason whose clean spelling conflicts on retry. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-38.md`.

**Review 2026-08-01 — AuditOptions (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 208 lines of `server/PtkMcpServer/Audit/AuditOptions.cs` at `e2cb3fe` in one complete source/caller/test pass plus production-call-graph adjudication. Focused baselines passed 10/10 options/health tests and 3/3 startup-configuration tests. Root/path, mode, capacity, retention, derived-path, and configuration-identity constraints yielded no distinct current finding beyond existing `opr-5`. A trailing-newline identity candidate from the options regex's `$` anchor was rejected as current-unreachable: the only anchored production call site receives the value through `AuditExportCheckpointCodec`'s strict `\z` validation, and no product surface supplies caller-created anchored options to internal consumers. Re-review that latent validator inconsistency if external anchored option construction becomes supported. Outcome: `no_current_findings`; no detail file, product change, or guard claim.

**Review 2026-08-01 — AuditCompletedChainRetirement (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 933 lines of `server/PtkMcpServer/Audit/AuditCompletedChainRetirement.cs` at `8ea3e54` in five bounded source/caller/test passes plus whole-file cross-boundary adjudication. Focused retirement tests passed 13/13 and anchored-preparation tests 22/22. Current-production adjudication was limited to `RecoverUnderQuota` from anchored writer startup preflight and covered bounded control inventory, published alias and intent replay, retained identities, exact topology and target validation, segment/control deletion order, strict intent serialization/parsing, and restart idempotence. `TryRetire` and `TryRetireObservedCompleted` remain removed-exporter initiation paths and were not promoted into current findings. Outcome: `no_current_findings`; no detail file, product change, or guard claim.

**Review 2026-08-01 — AuditExportCheckpointStore (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 1,659 lines of `server/PtkMcpServer/Audit/AuditExportCheckpointStore.cs` at source-equivalent head `fb0dbf9` in seven bounded source/caller/test passes plus whole-file cross-boundary adjudication; focused tests passed 40/40. Current production adjudication covered fresh prepared-writer creation, persistent lease and protected checkpoint custody, transition persistence and uncertain replacement, closed-reader capabilities, permanent disposition, completed-chain transition, root/control identity, canonical names, and transition validation across active anchored-admin and retirement callers. `s2-windows-checkpoint-durability` was excluded as an already-verified prior repair, and `opr-37` remains a caller-preflight cleanup defect rather than a second store finding. Removed exporter flows make ordinary acknowledge/configuration-retry/block methods dormant; re-review `BlockClosedRecord` configuration-identity validation if those flows return. Outcome: `no_current_findings`; no detail file, product change, or new guard claim.

**Review 2026-08-01 — AuditAnchoredWriterPreparation (one current finding):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 493 lines of `server/PtkMcpServer/Audit/AuditAnchoredWriterPreparation.cs` at source commit `07f6ca4f8ef27d623211b3bbace725b5ac6935b8` in three bounded source/caller/test passes plus whole-file cross-boundary adjudication. Focused baselines passed 22/22 preparation tests and 13/13 completed-chain tests. Active anchored-admin construction, checkpoint binding/activation ownership, startup preflight, retained cleanup, topology/control inventory, and canonical temporary classification yielded one distinct accepted finding: `opr-37` HIGH for permanent startup refusal after a crash leaves a proper-prefix initial-checkpoint temporary. An activated-sink leak candidate was rejected because `AuditJournalFactory.OpenActivatedAnchored` takes ownership on entry including failure. Outcome: `one_current_finding`; no product or test change.

**Open — opr-37:** HIGH — a hard crash can leave a canonical initial-checkpoint temporary as a proper prefix of deterministic bytes; exact-byte-only recovery then permanently blocks every anchored writer and out-of-band administration startup. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-37.md`.

**Review 2026-08-01 — AuditAdminOperations (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 1,124 lines of `server/PtkMcpServer/Audit/AuditAdminOperations.cs` at `bfd571e54eceb59b1f24ea9062a21e6056521410` in five bounded source/caller/test passes plus a whole-file cross-boundary pass. Focused baselines passed 20/20 evidence-access tests and 22/22 operator-disposition tests. Active `PtkAuditAdmin` reachability covered journal-session opening, evidence read/export publication and failure accounting, permanent-block checkpoint adoption/disposition, reservation, event construction, and closed failure mappings. Adjudication rejected an activated-sink leak because `AuditJournalFactory.OpenActivatedAnchored` takes ownership on entry including failure, and rejected reclassifying pre-durability final-name publication because `Export_failure_before_durability_return_records_published_effect_and_known_facts` and its recorded mutation proof intentionally define that boundary as published. Outcome: `no_current_findings`; no detail file, product change, or new guard claim.

**Review 2026-08-01 — AuditClosedSpoolChainReader (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 1,320 lines of `server/PtkMcpServer/Audit/AuditClosedSpoolChainReader.cs` at `5ac6cc22b0422a159e0f19d1ef09ff0a9177dbc4` in six bounded source/caller/test passes; focused tests passed 38/38. Final current-call-graph adjudication covered the sole active `AuditAdminOperations` path: fresh-reader checkpoint adoption, exact blocked-record validation, permanent disposition, and disposal. Repeated-resolution snapshot release, stale chain/prefix end capabilities, and consumed configuration-retry authorization candidates were rejected because their exporter/live-reader callers were removed; adoption quota disposal is idempotent and the sole caller performs no post-failure use. Any exporter/live-reader reactivation must re-review those dormant capability-lifetime hazards. Outcome: `no_current_findings`; no detail file, product change, or guard claim.

**Review 2026-08-01 — AuditCallContext (no current findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 932 lines of `server/PtkMcpServer/Audit/AuditCallContext.cs` at `1b6ee179c590ae444e7097c68f3030517df00cc0` in five bounded source/caller/test passes. Final reachability adjudication used the intentional runtime-audit removal at `ddbb908`, current `Program` registration, tool signatures, `ISessionOperations`, and `SessionRuntime` adapters: production constructs no `AuditCallContext`, calls no `TryBegin`, assigns no accessor, and passes `audit: null`. Internally valid failed-`TryBegin` publication-disposal, RTK output-provenance overwrite, and hard-coded default session/generation candidates were rejected as current findings because the component is dormant. Any runtime-audit reactivation must re-review and repair those three hazards before enablement. Outcome: `no_current_findings`; no detail file, product change, verification, or guard claim.

**Open — opr-36:** MEDIUM — 32-bit reserved-byte multiplication overflows at 32,768 maximum-sized slots under an allowed multi-gigabyte spool, throwing after admission state mutation and leaking capacity. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-36.md`.

**Open — opr-35:** HIGH — poisoned journal can report a false-complete retained-evidence scan after an ambiguous append, making evidence retention-eligible before the preserved audit record is recovered. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-35.md`.

**Open — opr-34:** LOW — a canonical crash-left audit allocation temporary is recoverable by writer preparation but is rejected by the earlier pre-writer evidence scan, indefinitely wedging out-of-band audit administration and pinning evidence. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-34.md`.

**Open — opr-33:** HIGH — alias-definition collection matches only literal `Set-Alias`/`New-Alias` spellings, so module-qualified or proven stock-alias invocations hard-refuse valid parse-clean PowerShell. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-33.md`.

**Open — opr-32:** HIGH — explicit `local:` or `private:` function definitions retain their scope prefix in classifier identity, so valid parse-clean PowerShell is hard-refused as Bash before execution. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-32.md`.

**Open — opr-31:** MEDIUM — one indeterminate Unix identity or group probe can permanently evict a live reparented descendant from tracking, allowing its later escape to survive false empty-domain proof. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-31.md`.

**Open — opr-30:** MEDIUM — Unix containment's healthy-observation gate accepts snapshots from before worker release or after worker death, so it need not cover the descendant-creation interval it is meant to prove. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-30.md`.

**Open — opr-29:** MEDIUM — `WorkerLaunchCommand` and the Unix launcher's environment re-materialization use case-insensitive identity, so valid case-distinct Unix host variables deterministically block launch or collide with bootstrap injection. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-29.md`.

**Open — opr-28:** LOW — the Unix launcher's private five-second broker-handshake timeout is reported as `worker_start_canceled` whenever the overall startup deadline is later. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-28.md`.

**Open — opr-27:** LOW — valid structured Unix broker `StartFailed` events are collapsed into `unix_worker_broker_protocol_invalid`, losing startup stage and native error diagnostics. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-27.md`.

**Open — opr-26:** MEDIUM — a shared Unix process-table snapshot captured before registration arming can count as healthy post-arm evidence and falsely confirm an escaped domain empty. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-26.md`.

**Open — opr-25:** MEDIUM — Unix worker-exit observation treats identity-query exceptions and a faulted broker wait as successful exit signals, poisoning a healthy warm session. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-25.md`.

**Open — opr-24:** LOW — confirmed-empty Unix launch cleanup bare-rethrows the handshake failure without a containment task, so a created worker domain is reported as never launched. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-24.md`.

**Review record 2026-08-01 — UnixWorkerBootstrap (no findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed `server/PtkMcpServer/Worker/UnixWorkerBootstrap.cs` at `53b96c91c1d77e5a8e9a9b4bb5b5026f1864c98b` in two exact-source candidate passes plus a final production-evidence adjudication, covering fixed descriptor validation, close-on-exec handling, duplication and stream ownership, direction/provenance checks, cleanup ordering, Unix P/Invoke/error projection, and bootstrap call timing. The final pass used the exact launcher anonymous-pipe construction, `posix_spawn` standard-descriptor setup, broker worker remap, first-action `WorkerProcessEntry` caller, and focused tests. It rejected descriptor-zero, socket/read-write provenance, original-close short-circuit, startup-hook, repeat-open, diagnostic, non-atomic duplication, `errno`, `libc`, Unix `Win32Exception`, and dispose-masking candidates. It also kept the `CreateStream` terminal double-close rejected as already adjudicated without material product effect and excluded the accepted Apple arm64 variadic-`fcntl` ABI defect already tracked as `opr-14`. Outcome: `no_findings`; no detail file, product change, or guard claim.

**Open — opr-23:** MEDIUM — post-creation Windows startup cancellation is labeled prelaunch when job-empty proof is already complete, so proof timing can remove/cool the slot instead of leaving it `Faulted`. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-23.md`.

**Review record 2026-08-01 — WindowsWorkerNative (no findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed `server/PtkMcpServer/Worker/WindowsWorkerNative.cs` at `cef398e6032734b83946681a9aa5af86f313956c` in six exact-source passes, covering job creation/configuration/observation, pipe inheritance and ownership, atomic process creation, containment-proof resume, owned exit waits, command/environment encoding, attribute-list allocation, SafeHandle leasing, Win32 structures, and P/Invoke ABI. Final split adjudication against the sole `WindowsProcessTreeSupervisor` owner rejected handle-transfer, concurrent-inheritance, raw-handle lifetime, attribute/environment, job-observer, process-wrapper, wait-registration, pipe-disposal, and native-signature candidates. A merge pass rejected pending `ContainmentEmpty` as the approved proof-only fail-closed state and rejected embedded-NUL input as unreachable through `WorkerLaunchCommand` validation. Outcome: `no_findings`; no detail file, product change, or guard claim.

**Open — opr-22:** LOW — startup timeout versus cancellation is inferred from a drifting wall-clock deadline: first-use work can label a real timeout canceled, while successful cleanup crossing the deadline can label caller cancellation timed out. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-22.md`.

**Open — opr-21:** LOW — a containment failure during failed worker initialization wraps the primary exception before classification, replacing timeout/cancellation detail with generic `worker_initialize_failed`. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-21.md`.

**Open — opr-20:** HIGH — `ProcessSessionWorker.StateAsync` marks its write attempted before the writer's first pipe-write boundary, so proved pre-write cancellation can poison and replace a healthy worker and lose warm session state. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-20.md`.

**Open — opr-19:** HIGH — normal worker shutdown is unreachable because `ProcessSessionWorker.StopAsync` self-rejects every graceful request and forces containment; repair must also treat prompt exit during stopping as expected and preserve containment on every failed handshake path. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-19.md`.

**Review record 2026-08-01 — WorkerOperationProtocol (no findings):** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed `server/PtkMcpServer/Worker/WorkerOperationProtocol.cs` at `76e2b4a4d701c992a30d3f20bf56a8a14563b7d0` in five exact-source passes, covering envelope identity, initialization and limits, invoke/state/cancel/result unions, artifact chunk/seal encoding, closed-object parsing, strict text/code validation, timeout semantics, and receiver order/bounds/digest ownership. Focused production-caller and protocol-layer adjudication rejected request dereference, deadline symmetry, state timeout projection, result/snapshot validation symmetry, empty-frame correlation, artifact-bound placement, and payload-kind candidates. It also rejected post-digest-failure receiver reuse because production treats that protocol failure as terminal and poisons/replaces the worker; re-review that candidate if a caller ever recovers and continues on the same receiver. Outcome: `no_findings`; no detail file, product change, or guard claim.

**Review record 2026-08-01 — WorkerOperationScheduler (no findings):** Claude
Opus 5
(`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`)
reviewed `server/PtkMcpServer/Worker/WorkerOperationScheduler.cs` at
`a46e065cc5e7d10798fef684022c3f082e555a59` in three exact-source passes,
covering admission and replay, capacity, scheduler hops, cancellation and
deadline grace, terminal result/artifact ordering, fatal latching, drain,
active-request ownership, and disposal. Focused server, protocol-limit,
production-callback, and test adjudication rejected post-drain admission,
idempotence, deadline-overflow, terminal-write, fail-stop, observer-lifetime,
and terminal-race candidates. Outcome: `no_findings`; no detail file, product
change, or guard claim.

**Review record 2026-08-01 — WorkerOutputArtifact (no findings):** Claude Opus 5
(`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`)
reviewed `server/PtkMcpServer/Worker/WorkerOutputArtifact.cs` at
`f46631961c651cfc888bb9c8b22923862b9d857a` in three exact-source passes,
covering supervisor capture binding, chunk/seal validation, reservation discard,
sink publication and timeout ownership, disposal and secret clearing, foreground
capture bounds and cloning, and strict artifact codec validation. Focused caller,
store, protocol-receiver, runspace-bound, and test adjudication rejected
reservation, buffer/CTS race, maximum-bound, detail-publication, seal/dispose,
codec-invariant, and decode-classification candidates. The serial-caller
invariant is the re-adjudication trigger if capture ownership later changes.
Outcome: `no_findings`; no detail file, product change, or guard claim.

**Review record 2026-08-01 — WorkerServer (no findings):** Claude Opus 5
(`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`)
reviewed `server/PtkMcpServer/Worker/WorkerServer.cs` at
`f46631961c651cfc888bb9c8b22923862b9d857a` in four exact-source passes,
covering run-once admission, initialization and deadline races, pending reads,
operation scheduling, cancellation/shutdown, protocol correlation, exception
mapping, factory/session/scheduler ownership, detached-task observation, and
cleanup. Focused caller, scheduler, runtime-delay, and test adjudication rejected
pre-ready loss, queued cancellation, fatal-task completion, deadline spin,
synchronous drain, cancellation cleanup, and late-factory masking candidates.
Outcome: `no_findings`; no detail file, product change, or guard claim.

**Review record 2026-08-01 — WorkerProtocol (no findings):** Claude Opus 5
(`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`)
reviewed `server/PtkMcpServer/Worker/WorkerProtocol.cs` at
`e72b67d9896934d18143259b9e60079d5aa8b711` in three exact-source passes,
covering decode ownership, envelope validation, encoding bounds, JSON depth and
duplicate rejection, wire-name mapping, pooled-buffer lifetime, incremental
framing, cancellation, EOF, concurrent writes, failure latching, and clearing.
Focused runtime, caller, and test adjudication rejected payload aliasing, frame
limit/depth, post-dispose/overflow, retry desynchronization, callback, and
diagnostic candidates on direct guards or terminal production ownership.
Outcome: `no_findings`; no detail file, product change, or guard claim.

**Current intake — opr-18:** LOW — `ptk_state listAvailable=true` silently
reuses the first available-module inventory after the warm session changes its
module search path or module files. Status: accepted; plan required. Reviewer:
`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`.
Detail: `.agents/review/findings/opr-18.md`.

**Review record 2026-08-01 — AuditLiveSpoolReader (no findings):** Claude Opus
5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`)
reviewed `server/PtkMcpServer/Audit/AuditLiveSpoolReader.cs` at
`6a33c3467ba38ad75ea18bd1e74bb28dd040e539` in three exact-source passes,
covering committed-prefix records and checkpoints, opaque rotation/closure
capabilities, closed-prefix cursor advancement, sequence/hash/byte bounds,
concurrency, and disposal. Candidate adjudication used the complete focused
test inventory and journal/checkpoint/closed-reader caller contracts. It
rejected gap rotation and unseen writer closure because retained recovery walks
every intermediate segment; rejected pending, block, byte-alias, bound, and
lifetime candidates on direct guards; and rejected wrong-reader prefix-proof
consumption as unreachable by current callers, opaque internal misuse, and
recoverable without checkpoint or live-cursor mutation. Outcome: `no_findings`;
no detail file or open item, and no product or test change.

**Open — opr-17:** HIGH — valid alias-definition parameter orderings are missed by local-definition collection, so trusted preflight hard-refuses parse-clean PowerShell before execution. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-17.md`.

**Review record 2026-08-01 — AuditExportCheckpoint (no findings):** Claude Opus 5
(`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`)
reviewed `server/PtkMcpServer/Audit/AuditExportCheckpoint.cs` at
`497e0b2abee0522b26b976e65fb0c4d74df05157`, limited to immutable checkpoint
and blocked-record invariants, strict canonical JSON, byte/newline/BOM bounds,
UUID and timestamp canonicality, cursor adjacency, overflow, and exception
projection. The invariant pass proposed that acknowledged and next-blocked
event IDs must differ; the reviewer rejected the sole candidate on evidence from the
reader and store consumers because both records are independently bound to
distinct spool positions and sequences, so equality cannot rewind or replay the
cursor. No owner ruling was sought or required. The codec/helper pass returned
`no_findings`. Outcome: no findings recorded, no detail file or open item, and
no product or test change.

**Open — opr-16:** LOW — the responsive deadline-cancellation test's disposable callback can unregister during delay unwind, causing a false timeout after correct product cancellation. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-16.md`.

**Open — opr-15:** HIGH — Unix containment treats every nonfatal identity-query exception as process death, so a transient probe failure can clear a live observed escape and release its session alias. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-15.md`.

**Openreview adjudication 2026-08-01:** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed `server/PtkMcpServer/Worker/WindowsWorkerBootstrap.cs` at `f70e6311ec65118b6bb6feca15b0fa9caeb6cc77`, limited to bootstrap-environment custody, pointer-width parsing, inherited-handle acquisition, noninheriting duplication, pipe and flag validation, stream ownership, cleanup ordering, and failure projection using the complete source and bounded launcher/exit/test evidence. An initial pass proposed three candidates; call-graph adjudication rejected unrelated-handle closure because the supervisor supplies an exact restricted handle list, rejected checking original rather than active duplicate inheritance, and rejected dual capture/removal failure aggregation as diagnostic-only on a terminal startup path. Final verdict: `no_findings`; no product change or guard claim.

**Openreview adjudication 2026-08-01:** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed `server/PtkMcpServer/Audit/AuditHealth.cs` at `5c89fd3d6fbe1bea512aa83c9a3afef2d1b8f019`, limited to availability transitions, recovery serialization, emergency-probe accounting, capacity metrics, exporter projection, thread safety, machine-code validation, and user-facing health text using the complete source and bounded caller/test evidence. An initial pass proposed three candidates; production-evidence adjudication rejected trailing-newline failure codes as unreachable from current internal callers, rejected frozen exporter state because the current exporter is deliberately out-of-band, and rejected retained `Recovered` outage fields because the sole production caller immediately closes that phase with `MarkHealthy`. Final verdict: `no_findings`; no product change or guard claim.

**Openreview 2026-08-01:** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed `server/PtkMcpServer/Audit/AuditAnchoredSpoolPrefixRetention.cs` at `3adc4b96ad16ff213d10f29b31807abef5502486`, limited to checkpoint-authorized prefix selection, protected-file identity retention, quota-lease ownership, age and headroom calculation, concurrent topology rejection, exact deletion, and post-delete inventory verification using the complete source and focused test suite. Verdict: `no_findings`; no product change or guard claim.

**Current intake — opr-14:** HIGH — Apple arm64 variadic `fcntl` receives its third argument through incompatible fixed P/Invoke declarations, so worker protocol and launch-mapping descriptors may remain inheritable. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-14.md`.

**Openreview adjudication 2026-08-01:** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed `server/PtkMcpServer/Audit/AuditJournalFactory.cs` at `5897078ab011d20d7e52b6af46bbc1e7e8ed0260`, limited to protected host identity creation and validation, concurrent publication, staged-sink ownership, journal construction, protection-mode routing, and failure cleanup using the complete source and bounded production caller/test evidence. The initial pass proposed two LOW candidates; evidence-bound adjudication rejected the short decoded host-id exception-type mismatch because startup remains fail-closed, and rejected pre-guard staged-sink leakage because no production caller can supply a transferred sink with guard-invalid arguments. Final verdict: `no_findings`; no product change or guard claim.

**Openreview 2026-08-01:** Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed `server/PtkMcpServer/Worker/WorkerProcessEntry.cs` at `c3225a0ccd649135f0e09624d224c5dde329b037`, limited to worker-invocation classification, bootstrap capture and cleanup, runtime construction, server-exit mapping, diagnostic fallback, and fatal-exception boundaries using the complete source, production entry caller, and focused-test inventory. Verdict: `no_findings`; no product change or guard claim.

**Openreview adjudication 2026-08-01:** Claude Opus 5 re-reviewed unchanged `server/PtkMcpServer/SupervisorLifecycle.cs` at `98608018450cb5b2021930405862d4ffdbb48482`. An initial no-tool pass proposed cancel-under-lock candidates conflicting with the prior clean review at `2ac1cd4168621f1e6b34b41d2fa62fdcf6ddea4c`; call-graph-bound adjudication rejected all three because current registrations are fixed, non-blocking/non-throwing BCL or asynchronous-TCS callbacks and the drain TCS already uses `RunContinuationsAsynchronously`. Final verdict: `no_findings`; no product-change guard claim.

**Current intake — opr-13:** MEDIUM — worker launch freezes environment names with unconditional case-insensitive identity, so valid case-differing Unix variables are rejected as duplicates and can block all workers. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-13.md`.

**Current intake — opr-12:** LOW — `ptk_invoke` accepts negative `timeoutSeconds` and silently selects the operator default even though the approved contract reserves that meaning for zero. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-12.md`.

**Current intake — opr-11:** MEDIUM — `ptk_invoke` silently maps every unknown route to `auto`, so a typo in the documented `pwsh` consent token changes routing policy without refusal or warning. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-11.md`.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Program.cs` at `18760119e98e6a1ff36e9443ebb8548acc9ae01b`, limited to worker/supervisor separation, stdout purity, DI alias lifetimes, startup and shutdown ordering, transport stream custody, and request filter/scoping setup using the complete startup sequence and bounded test evidence in a no-tool transport. Existing `opr-8`, `opr-9`, and `opr-10` were excluded. Verdict: `no_findings`; no product-change guard claim.

**Current intake — opr-10:** MEDIUM — timeout environment parsing accepts positive infinity and out-of-range values, allowing `TimeSpan.FromSeconds` to crash supervisor or worker startup instead of applying the fallback. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-10.md`.

**Current intake — opr-9:** MEDIUM — timeout environment variables use current-culture floating-point parsing, so the same text can silently resolve to a different duration across hosts. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-9.md`.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Tools/SessionTool.cs` at `dab8abbdb7a506bfb077d1a11d5bc94985dcdc20`, limited to the action/name validation matrix, audit alignment, nullable defaults, cancellation forwarding, routing, async exception behavior, and schema/description alignment using the complete adapter and bounded evidence in a no-tool transport. Verdict: `no_findings`; no product-change guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Tools/StateTool.cs` at `b9d862ef39a8c3b25518c5737f15535aaf1a3323`, limited to adapter validation/default alignment, session routing, cancellation and boolean forwarding, async exception behavior, and schema/description alignment using the complete adapter and bounded audit/seam evidence in a no-tool transport. Verdict: `no_findings`; no product-change guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Sessions/ISessionOperations.cs` at `79d9d128c30e612daa6b62cd753ca7ee85d15cba`, limited to the trusted tool-facing boundary, protocol exposure, output-store custody, argument routing, session defaults, and ordered lifetime ownership. A declaration-only pass raised five candidates; an evidence-bound contested pass rejected all five against actual DI, adapters, worker-surface tests, and lifecycle ordering. Final verdict: `no_findings`; no product-change guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Tools/ResetTool.cs` at `dfe0da7b4fc59ed90dc4f97cf3fa007a8d650240`, limited to validation, default-session selection, cancellation forwarding, session routing, async exception behavior, and schema/description alignment using the complete adapter and bounded caller/test evidence in a no-tool transport. Verdict: `no_findings`; no product-change guard claim.

**Current intake — opr-8:** MEDIUM — Windows child-stdin setup ignores inheritance-mark failure and can publish a non-inheritable `NUL` handle, making later native children fail stdin access with `ERROR_INVALID_HANDLE`. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-8.md`.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/RawUsageCounter.cs` at `469959c622d4ea2c6b6a150d570b05b6869f337c`, limited to raw-usage counter overflow, atomicity, concurrency, and contract truthfulness using the complete source and bounded caller/test evidence in a no-tool transport. Verdict: `no_findings`; no product-change guard claim.

**Closed repair — ci-worker-cancel-1:** LOW — the merged 10-second standalone cancellation-callback checkpoint recurred on Windows run `30692685449`; repair at `8588374f8d19b97a9c38d9606a6e331ba38b8452` synchronizes through scheduler drain, which already owns cancellation-task completion. Opus 5 exact-SHA verdict: `accepted`, `guard_confirmed=true`, no actionable findings. PR 29 run `30694440416` passed all six jobs and merged as `d7eefc5f7159469570135646a2667ca94b52d553`. Detail: `.agents/review/findings/ci-worker-cancel-1.md`.

**Current intake — opr-7:** MEDIUM — quota-control creation exposes the final filename before its one-byte format marker is durable, so interrupted first initialization can permanently brick the spool. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-7.md`.

**Current intake — opr-6:** MEDIUM — evidence-store faults clear the only state distinguishing a previously used root from a never-opened provider, so an absent root can make reconciliation report false success. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-6.md`.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Audit/AuditEvidenceRetentionAudit.cs` at `735000e2cfabb88c3a82b8d1b13fa9f984bc6c07`, limited to retention-event construction, ordering and bounds, count and byte truthfulness, exception handling, sensitive data, and fail-closed behavior. Verdict: `no_findings`; no product change or guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Audit/AuditEvidenceOrphanReconciler.cs` at `77a324ec78a816afb8672e7b93438342ec84293a`, limited to evidence ownership recognition, orphan detection, concurrent publication and retention races, path and link safety, deletion ordering, exception handling, and fail-closed filesystem behavior. Verdict: `no_findings`; no product change or guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Audit/AuditAdminDispositionFailure.cs` at `888914dc2674386fd202bc0a7a4d2d828c1418c9`, limited to administration failure and disposition classification, status truthfulness, sensitive-data handling, exception mapping, and fail-closed behavior. Verdict: `no_findings`; no product change or guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Audit/AuditSpoolRecordCodec.cs` at `4c39b9f51e8114999eeaa44d8c708e0f3b5d8a56`, limited to record framing, length and range validation, canonical serialization and decoding, truncation or corruption handling, allocation bounds, stream semantics, and fail-closed behavior. Verdict: `no_findings`; no product change or guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Audit/AuditSpoolSegmentIdentity.cs` at `b4ffe87d5b2364885dbf92ee00a4e23df0a1d786`, limited to segment filename construction and parsing, canonical identity, numeric range and overflow behavior, ordering, path safety, and fail-closed rejection. Verdict: `no_findings`; no product change or guard claim.

**Current intake — opr-5:** MEDIUM — `AuditStartupConfiguration` canonicalizes `PTK_AUDIT_ROOT` before `AuditOptions` can reject relative roots, silently binding legacy administration to a launcher-dependent directory. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-5.md`.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Audit/AuditOutputRequestProtector.cs` at `a2c343f4f0a143ee017d93e794269b532e8d4f6c`, limited to authorization binding, request validation, sensitive-output protection, fail-closed behavior, exception handling, and cross-platform semantics. Verdict: `no_findings`; no product change or guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Audit/AuditAdminFailure.cs` at `618f007b1c0f2cf8125f8635eb6174ad754d2101`, limited to failure classification and projection, sensitive-data handling, status truthfulness, exception behavior, and audit availability semantics. Verdict: `no_findings`; no product change or guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Audit/AuditEffectiveIdentity.cs` at `c1d83e10e1078a94b6b0d1559b1235163e5d8312`, limited to effective identity capture, platform semantics, normalization, unavailable-data behavior, and audit truthfulness. Verdict: `no_findings`; no product change or guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/SupervisorCallFilter.cs` at `6675e37d2509d40c88149912e3bb23b15077d1a6`, limited to call admission, shutdown interaction, cancellation propagation, lifetime accounting, exception safety, and observable MCP behavior. Verdict: `no_findings`; no product change or guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Execution/ExecutionPlanner.cs` at `0a0fcbb17c283c2dac8a7cc2b600233de0f31d83`, limited to classification, route eligibility and enforcement, executable identity binding, fallback provenance, validation ordering, working-directory handling, and fail-closed behavior. Verdict: `no_findings`; no product change or guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Execution/ExecutionPlan.cs` at `cb992e9f466de45720fba7fe9782c7e6f68b7f6b`, limited to construction invariants, immutable state, dispatch conversion, provenance metadata, fail-closed validation, and result contracts defined in that file. Verdict: `no_findings`; no product change or guard claim.

**Openreview 2026-08-01:** Claude Opus 5 reviewed `server/PtkMcpServer/Execution/BashExecutableIdentity.cs` at `385db4ce55de5abc2f6488166c72786a985f2ab2`, limited to executable identity capture, path handling, fail-closed behavior, and cross-platform semantics. Verdict: `no_findings`; no product change or guard claim.

**Current intake — opr-4:** MEDIUM — cleanup-time caller cancellation can overwrite an already-elapsed RTK/Bash process timeout and suppress timeout/remote-effects reporting. Status: accepted; plan required. Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`. Detail: `.agents/review/findings/opr-4.md`.

Workflow: see `.agents/playbooks/reviewloop.md`.
Per-finding detail: see `.agents/review/findings/<id>.md`.

## Legend
- `[ ]` Admitted, open (passed intake triage; not yet started)
- `[~]` In progress / pending review
- `[x]` Verified (awaiting owner-gated merge/push)
- `[!]` Contested — declined, disputed, or ruled invalid; awaiting owner adjudication
- `[-]` Declined at intake (kept for the record; no work)

**Closed loops before 2026-08-04 are archived.** They were rotated verbatim
to `docs/history/review-archive.md` on 2026-08-05, when this file reached
303KB and could no longer be read whole by a cold agent (the Read tool caps
at 256KB). Every loop from the 2026-07-04 release-distribution pass through
the read-only baseline codebase review lives there, including the legend and
the per-loop findings tables. This file keeps only the active loop and the
most recently closed one; `.agents/review/dispositions.md` remains the
disposition of record for every `opr-*`.

Loop run 2026-07-04 — reviewer: codex (codex-cli 0.142.5), scope: the
release-distribution plan commits `a43897a..e622cba` (docs/governance only).
Process note: fixes are committed directly to `master`, one finding per
commit, per this repo's recorded codex-loop precedent (`.agents/state.md`,
2026-07-04 routing entry) rather than the playbook's per-finding branches —
the scope is prose in governance files, and the owner-gated push boundary
still applies to the whole batch.
