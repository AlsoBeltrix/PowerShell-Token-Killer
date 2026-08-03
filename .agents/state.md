# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- **RTK router delegation plan drafted (2026-08-03):** `.agents/plans/rtk-router-delegation.md` is DRAFT and authorizes no code. It supersedes `.agents/plans/minimum-viable-release.md` on approval, retaining that document's release-blocking rule, non-goals, and packaging/proof slices by reference. Product definition restated by the owner: PTK is a warm PowerShell runspace **and a compression router** — PTK compresses PowerShell objects itself, everything else routes to RTK. The plan replaces PTK's own AST/PATH routing with RTK's `rtk hook check` rewriter (validated against rtk 0.44.2 and source `../rtk`), deletes shell inference and post-success advice, and closes ~18 findings as removed code. Await Decision 1 on routing authority. Process constraints in that plan retire the "accepted and plan-gated" finding state and prohibit unattended review and file-by-file source review.

- **Minimum viable release reset (2026-08-03):** `.agents/plans/minimum-viable-release.md` is DRAFT and authorizes no code. It limits release work to the five-tool PowerShell product, removes automatic Bash inference and post-success command advice, fixes only core session reliability, reuses existing packaging, and requires direct product checks only. No review is authorized without a separate explicit owner approval for that invocation. Await Decision 1 on the proposed product contract. Any later state entry directing unattended review is stale and superseded.

- **Output formatter `opr-59` intake (2026-08-03):** LOW accepted and plan-gated: reading a nonempty Available or Incomplete artifact exactly at EOF returns a correct zero-byte page, but `FormatRead` then falsely appends `(no captured bytes)` despite its nonzero artifact-byte header. Repair is formatter-only and must preserve the true empty-artifact marker. No product or test file changed.

- **macOS CI reopened `ci-slow-seal-2` (2026-08-02):** run `30784201961` failed unchanged slow-seal test code at `3.1263559s` against the three-second bound; the stopwatch includes variable work before witnessed seal entry. Unchanged-code run `30786526767` passed all six jobs. Opus classified the LOW recurrence as the same test-guard defect, not a product regression; further tolerance widening is prohibited and a fresh approved plan is required before re-anchoring the test-only measurement. No product or test change was made.
- **Windows CI confirmed `opr-7` concurrent-publication arm (2026-08-02):** run `30784201961` observed a concurrent initializer see `.ptk-audit-quota.lock` after final-name creation but before Windows owner-only DACL protection, causing non-retryable pre-open verification failure. Unchanged-code run `30786526767` passed all six jobs and the focused Windows test passed 20/20 locally. Opus classified `OPR7_EXTENSION`, MEDIUM; existing atomic quota-control publication plan gate remains, with no product or test change.

- **OutputStore review in progress (2026-08-03):** exact-source review is complete through line 1214 on blob `7ca10b70` (line 694 is blank). The store core remains clean through list/status/read/search after production-state integration; `OutputTool.FormatRead` integration admitted LOW plan-gated `opr-59` for a false `(no captured bytes)` marker on a nonempty artifact's EOF page. The abstract failed-delete retry loop remains test-seam-only in shipped production; `rbc-7`/`rbc-14` remain closed. Prior focused evidence is 99/99. Full checkpoint: `.agents/review/index.md`. Next review covers lines 1215–1450 of the 2,195-line file; lines 1215–1433 were previously consulted only for atomic-publication visibility, not complete source-defect review.

- **Active unattended GitHub remediation (2026-08-01):** GitHub issues #3,
  #9, #10, #11, #12, #14, #15, #16, and #28 are closed. Issue #16's two-slice
  retained-output
  discovery
  landed through
  PRs #22 and #23; public recovery is `ptk_output action=list`, optionally by
  named session, with a fixed ten-item bound. Both Opus reviews accepted with
  `guard_confirmed=true`; the final PR head passed all six hosted jobs and merged
  as `9ad7765`. Issue #15's worker-lifetime clarification merged through PR #24
  as `d140f13`; its active-member report remains tracked in issue #8. Live
  recheck as of `e22d619` (2026-08-03): no open PR; five open issues — #7, #8,
  #13, #30, and new #32 (add a kimi-code leg to `ptk_init.ps1`), which is
  unclassified and unqueued. PR #25 (`e522923`) removed the
  issue #16 regression's xUnit2031 annotation; Opus accepted the exact repair
  and all six hosted jobs passed without that warning.
- **GitHub issue #28 design evaluation is closed; E1 is tracked in #30:** the
  accepted Opus-reviewed report recommends the existing five-tool surface
  provisionally, distinguishes on-prem Exchange WSMan/implicit remoting from
  Exchange Online modern auth, and preserves no-replay/remote-containment and
  secret-custody boundaries. Issue #30 carries the separately authorized-host
  acceptance run; its creation does not authorize connecting to a host.
- **GitHub issue #13 remains open after current-master triage:** two real-server
  MCP stress runs produced up to 100,002 interleaved stdout/stderr ANSI/CR-heavy
  objects without transport loss. Capture bounded itself, returned a recovery
  handle, and left the same worker ready. The worker protocol emits bounded
  artifact chunks only after execution, so the filed write-flood hypothesis is
  not a demonstrated transport path. Do not make a speculative fix; a recurrence
  needs exact build identity plus worker/server crash diagnostics or an OS exit
  record. Evidence is posted on issue #13.
- **Release readiness was parked by the owner on 2026-07-31.** PTK is not close
  to release. Do not ask for release licensing, hook-default, signing, tagging,
  or publication decisions now. Work the product and GitHub issue backlog.
  `.agents/plans/release-readiness.md` is the future reactivation plan. Before
  release readiness can activate, every build must receive a new visible build
  identity; rebuilding one commit may not report the same version.
- **Product scope changed 2026-07-30: PTK targets a global public release.**
  Personal/team-only framing is superseded. Release work must serve unaffiliated
  users on supported platforms; no tag, push, or public release is implicitly
  authorized by this target.
- **Upgrade policy settled 2026-07-30: stop every PTK process before installing
  a new version.** Session disruption and client restart are accepted. The
  side-by-side launcher/version-retention plan is abandoned; no continuity,
  activation-record, rollback, or prune implementation is queued. The installer
  guard now covers installed `PtkMcpServer` and Unix `PtkWorkerBroker` processes
  before both install and uninstall. Its focused mutation guard, 144-test Pester
  suite (1 platform skip), and 1,215-test server suite passed.
- **Exact head `12e1ff5` is installed user-scoped on `ASHBIAMWEB1` as
  `0.2.0-dev.g12e1ff5` (2026-07-29).** The owner authorized installation and
  targeted process termination. The old installed supervisor/worker at
  `0.2.0-dev.g7d66273` were path-verified and stopped; the transactional
  installer refreshed Claude/Codex registrations, installed detected-harness
  guidance/hooks, and wrote the user Add/Remove Programs entry. Staged,
  activated, independent registered-command, and installed-property smokes
  passed: exactly five tools, STA on Windows, external PowerShell 7 module path,
  and ExchangeOnlineManagement discovery. New Claude-owned supervisors launched
  from the installed path. This Codex session's deliberately severed old MCP
  transport remains closed and requires a fresh client session. Detailed hashes
  and process evidence are in `.agents/machines.md`. Re-verified 2026-08-03 on
  `ASHBIAMWEB1`: `~/.ptk/VERSION` still reads `0.2.0-dev.g12e1ff5`.
- **Owner direction changed on 2026-07-26: pause the three-process resilience
  delivery line and use the reviewed production-reliability salvage instead.**
  PTK's product is reliable token-compressed PowerShell execution with warm
  state and isolated runspaces. Do not implement, merge, install, or cut over
  `feature/mcp-resilience-r1`; retain its pushed head `93e7992` as evidence and
  a source of individually reviewed worker/containment code. The replacement
  plan is `.agents/plans/production-reliability-salvage.md` on
  `plan/production-reliability-salvage`. The owner settled topology decision 1:
  one agent-owned MCP connection may own several explicitly named warm
  sessions, with one long-lived PowerShell worker process/runspace per session;
  this is required to isolate on-prem Exchange and Exchange Online modules,
  overlapping cmdlets, and connection state. Unrelated agents still require
  separate MCP connections. The prior Claude Opus 5 acceptance of exact plan
  commit `4d7f6b3` and blob `9c4938d` is superseded. Claude Opus 5 reviewed the
  corrected topology at exact commit `9c50c38` and plan blob `bc12299`; it
  accepted process-per-named-session but returned `REVISE` for concrete fixture,
  guard, audit-coupling, lifecycle, output-contract, and Exchange fault-proof
  gaps. Canonical evidence is
  `.agents/review/production-reliability-salvage-opus5-r4.md`. Every admitted
  finding is now incorporated in the plan, including the explicit adjudication
  that one fail-fast output-storage lane is safer than one potentially wedged
  lane per session. Claude Opus 5 reviewed exact corrected commit `2536675` and
  plan blob `ce9b319`; it closed every R4 finding and accepted the global lane,
  but returned `REVISE` for three local omissions: Slice 2's handshake/OTLP
  consumer inventory, close while old containment is unconfirmed, and bounded
  waiting for healthy output-lane contention. Canonical evidence is
  `.agents/review/production-reliability-salvage-opus5-r5.md`. Claude Opus 5
  reviewed exact commit `0ca5fbc` and plan blob `bed6177` in round 6. It closed
  the containment and output-lane findings but returned `REVISE` because
  Slice 2 still omitted consumers of the OTLP transport interface that its
  listed deletion removes. Canonical evidence is
  `.agents/review/production-reliability-salvage-opus5-r6.md`. Claude Opus 5
  reviewed exact commit `d1b883a` and plan blob `6a13e1f` in round 7. It closed
  the round-6 finding, accepted the local evidence/admin boundary, and found
  five mechanical gaps: preserve the retained SIEM receiver's proto, name
  three remaining test consumers, delete dead export identity while retaining
  live checkpoint code, remove linked conformance residue, and disposition the
  mixed operator document. Canonical evidence is
  `.agents/review/production-reliability-salvage-opus5-r7.md`. All five
  corrections and the explicit legacy-checkpoint disposition are now
  incorporated. Claude Opus 5 reviewed exact commit `bf47d60` and plan blob
  `431aecf` in round 8, closed all five findings, confirmed the legacy-state
  disposition, found no new blocking or major issue, and returned `ACCEPT`.
  Canonical evidence is
  `.agents/review/production-reliability-salvage-opus5-r8.md`. The owner
  approved decision 2 on 2026-07-27:
  retire the obsolete guardian-era R0 public-contract artifacts and
  guardian-only fake fixture while preserving the real containment fixture and
  its Windows tests unchanged. The owner approved the test-only Slice 1a and
  later directed agents to stop asking low-level implementation questions:
  choose the engineering details that best advance a reliable product, and
  escalate only product intent or real external risk. No further Claude Opus
  reviews are available because the owner's Claude credits are exhausted. The
  same direction delegates decisions 3-4 to the implementing agent: remove
  cold jobs from the first production surface and remove mandatory exact-script
  audit/OTLP export from the runtime, matching the owner's stated warm
  PowerShell plus token-compression product. This is the go for continued local
  plan implementation; outward actions remain gated. Git workspace mechanics
  remain agent-owned; never ask the owner to operate an intermediate workspace.
- **Local branch management is delegated for the remaining audited-harness
  implementation** (owner, 2026-07-11): create, switch, merge, and delete local
  implementation/review branches without per-merge confirmation.
- **Prior security/routing shapes remain evidence, not implementation
  authority.** The declarative policy gate and secret redaction are rejected;
  `.agents/plans/security-layer.md` is prior-art context. The closed review
  findings in `.agents/plans/rtk-rewrite-routing.md` remain regression
  evidence, while its broad rewrite implementation is not approved.
- **`.agents/decisions.md` is UNDER HOLD** (owner, 2026-07-10: do not
  update it until the discussion is complete). The security reframe and RTK
  routing direction still need durable entries after the owner releases the
  hold. The owner explicitly authorized two narrow mini-SIEM exceptions: the
  open receiver question on 2026-07-14 and its S0 Option 1 implementation
  decision on 2026-07-15. Neither releases the broader hold.
- **Release distribution remains approved work.** Slices 0-2 are landed;
  slice 3 is blocked behind the replacement runtime's production gates and
  the GitHub release workflow file is still absent. The guardian/R7 package
  mechanics are explicitly superseded at the top of
  `.agents/plans/release-distribution.md`; do not restore them. The old
  2026-07-25 calendar is superseded with no replacement date approved. The
  deliberately open hook-default choice also blocks slice 4.
- **Standing GitHub authority:** the owner granted persistent permission on
  2026-07-10 to comment, close, and triage issues in this repository as
  appropriate without per-action asks.
- **Mini-SIEM S3H is landed on `master` (no-ff merge, 2026-07-18).** Code head
  `c726a33` (record head `3bacbc4`) merged from
  `plan/mini-siem-storage-hardening`. Re-verified 2026-08-03 in this clone:
  the branch survives only as `origin/plan/mini-siem-storage-hardening` (no
  local ref), and worktree `.claude/worktrees/siem-storage-hardening` no longer
  exists here; the retention note is satisfied remotely. The receiver now
  applies one SIEM-local, fail-closed
  protected-path boundary before parsing or use: retained identity-stable
  config/TLS reads, exact numeric-UID POSIX modes plus macOS ACL rejection,
  exact protected one-ACE Windows DACLs, lexical link/reparse rejection,
  mutable-storage identity-collision checks, and eager atomic owner-only
  DB/WAL/SHM startup with live identity revalidation before Kestrel can bind.
  Existing insecure objects are never repaired. The cross-platform matrix,
  guard mutations, independent audit, and exact host evidence are recorded in
  `.agents/machines.md`. This closes current config/TLS/SQLite enforcement;
  full acceptance row 7 still waits for the later slice that introduces and
  protects the currently absent custody checkpoint/anchor path.
- **rbc review-loop batch is in progress on `master` (2026-07-18).** The
  Hermes baseline review (rbc-1 through rbc-13 under `.agents/review/findings/`,
  plus `.agents/review/index.md`) is committed. Owner triaged all 11 open findings
  to FIX with **batch merge pre-approval** (commit `3d7f2c1`): any fix whose
  external fixed-SHA review is accepted with `guard_confirmed=true` and a green
  full suite may be merged to `master` without a per-item prompt. Merged so
  far: rbc-1 (`a445038`), rbc-2 (`a6c4a17`), rbc-3 refuted (`41d3257`), rbc-4
  (`685d34c`), rbc-6 refuted (`315b9db`, merge record `749815b`), out-of-band
  hotfix hf-1 ptk_output draft-2020-12 schema (`b7ac20b`), rbc-7 (`a9b0476`),
  rbc-9/rbc-10/rbc-12 (`6452945`; fix `27511b1`, external-review hardening
  `90b97b3`, remedy verification VERDICT: ACCEPT 2026-07-20), and rbc-14
  (`897bdbc`; fix `5fc84ad` + stale-pulse remedy `f624796`, codex turn-3
  ACCEPT). rbc-13 is refuted as a defect (fail-closed by design, documented at
  `MatchesCurrentResolution`). Dispositions without product change: rbc-5
  deferred to resilience R7 (owner disposition 2026-07-19), rbc-8 downgraded
  at triage 2026-07-19 with a targeted drain-replay guard queued to the worker
  pass, rbc-11 gated on the owner's S3H land/park decision with an interim
  deployment warning landed. rbc-15 (process-tree containment for background
  jobs) is closed and merged to local master at `f0d17f6`: remedies
  `b4432dc` → `c17c1f9` → `08da8f5` → `a216734` → `3634fe7`, codex review
  closed at turn 4 (diff-scoped findings: none; sole MAJOR labeled
  PRE-EXISTING by the reviewer, adjudicated deferred to the recycled-PID
  incarnation-hardening follow-up task), full server suite 1587/1587 green at
  `3634fe7` and again on master post-merge (2 m 17 s). Per-item ledger:
  `.agents/review/index.md`;
  records: `.agents/review/findings/rbc-*.md`. External reviewer was codex
  (standard = gpt-5.6-sol @ high, owner-confirmed in
  `.agents/review/harnesses.local.json`; frontier unconfirmed — escalation on
  codex blocks to owner).
- **GitHub #7 (Defender FP: `Trojan:MSIL/AsyncRAT.AB!MTB` on `PtkMcpServer.dll`)
  carries a landed interim mitigation and is gated on Microsoft's verdict.**
  Interim mitigation `51ce880` on `master`: the install path detects a
  Defender-quarantined/missing payload after install and fails loudly with
  remediation guidance instead of leaving a silently broken install; README and
  the runbook document the FP and submission status. The owner submitted the
  detected DLL to Microsoft via the WDSI file-submission portal on 2026-07-20.
  On verdict: update security intelligence, rescan the artifact, remove any
  incident-specific exclusions, retire the quarantine detection if superseded,
  and close #7 per `.agents/plans/defender-fp-submission.md`.
  GitHub was rechecked on 2026-08-01: the issue remains open with no comments,
  Microsoft verdict, or newer detection evidence. Do not repeat the submission.
- **rbc-5/rbc-6 containment WIP: the recorded carrier is falsified in this
  clone (verified 2026-08-03).** Branch `fix/rbc-6-unix-sigkill-escalation`
  exists neither locally nor on `origin`, and the cited `2b3ce1a` is an
  ordinary ancestor of `master` (a handoff commit), not a WIP tip — so the
  "uncommitted and preserved" claim cannot be confirmed here. Treat the WIP as
  unlocated pending owner direction; see `## Blockers`. rbc-6's filed premise
  was false: .NET 10 Unix
  `Process.Kill(entireProcessTree: true)` already uses SIGKILL. Its WIP instead
  addresses a different daemonized/reparented-descendant condition and was not
  accepted. rbc-5 is valid in the current in-process Windows runtime, but its
  saved spawn-then-assign Job Object WIP has an admitted escape race and
  conflicts with the approved creation-time containment contract. No rbc-5
  product change is accepted or committed. The recommended proportional
  resolution is to close rbc-5 through the already-planned resilience R7
  worker cutover, adding a Windows guard that a background descendant dies on
  hard supervisor termination; the owner approved this deferral on 2026-07-19
  (recorded in `.agents/review/index.md`).

## Next

- **Review intake:** `opr-11` MEDIUM remains plan-gated: add client schema guidance plus authoritative active-runtime route validation and defensive parser refusal; real stdio guards must prove unknown routes never execute as `auto`. No product or test change.

- **Review intake:** `opr-54` LOW accepted and plan-gated: enforce canonical session/name values at the runtime operation boundary and never reflect rejected raw input into PTK control lines; guard real stdio calls across all session-bearing tools. No product or test change.

- **Review intake:** `opr-53` MEDIUM accepted and plan-gated: make supervisor status/retry/recovery directives unforgeable relative to worker text, preserving arbitrary output and bounding responses; guard invocation and state through the real public boundary. No product or test change.

- **Review intake:** `opr-52` LOW accepted and plan-gated: add four exact bounded allowlist mappings and producer-code terminal guards while preserving generic fallback, exit classes, graceful silence, and the 256-byte ASCII limit. No product or test change.

- **Review intake:** `opr-51` LOW accepted and plan-gated: compare executable paths with Windows ordinal-ignore-case semantics while retaining digest and Unix-mode equality; add casing and changed-content guards. No product or test change.

- **Review intake:** `opr-50` MEDIUM accepted and plan-gated: exclude Windows volume-qualified command forms from cold RTK routing and guard exact PowerShell fallback. No product or test change.

- **Review intake:** `opr-49` MEDIUM accepted and plan-gated: normalize Windows non-fully-qualified PATH entries with the audited working directory as the explicit base and add cross-drive parity guards. No product or test change.

- **Review intake:** `opr-48` MEDIUM accepted and plan-gated: replace raw Unix execute-bit union with real-identity access semantics and add a two-directory PATH guard. No product or test change.

- **Review intake:** `opr-47` MEDIUM accepted plan-gated: separate the validator process deadline from noncancelable audit-flush admission without erasing a determinate `bash -n` exit result; guard successful, syntax-invalid, and genuinely timed-out validators.

- **Review intake:** `opr-46` LOW accepted and plan-gated: make identity/group observation incarnation-coherent while preserving opr-31 fail-closed tracking on indeterminate samples. No product or test change.

- **Review intake:** `opr-45` LOW accepted and plan-gated: make local-definition exemptions scope-aware while preserving top-level prior-definition and containing-recursion behavior. No product or test change.

- **Review intake:** `opr-44` MEDIUM accepted plan-gated: add a conservative named Bash `set -o` option allowlist without classifying valid PowerShell `set +e` or short `Set-Variable` forms. No product or test change.

- **Review intake:** `opr-43` HIGH accepted plan-gated: fatal-parse handling must preserve existing trusted command evidence before returning no dialect finding. No product or test change.

- **Review intake:** `opr-42` MEDIUM accepted plan-gated: state needs one compound pre-release result carrying worker state, named-session identity, and registry count; the wrapper must perform no post-lease registry lookup. No product or test change.

- **Review intake:** `opr-10` now covers the full finite integral 1–86,400-second parser contract; the new candidate merged into the existing MEDIUM plan gate. No product or test change.

- **Review intake:** `opr-41` MEDIUM accepted plan-gated: the no-fallback `NotStarted` RTK path omits `$LASTEXITCODE` restoration after pre-launch reset. No product or test change.

- **Review intake:** `opr-4` gained LOW pre-start classification scope under the existing MEDIUM plan gate: a no-start RTK or Bash result can combine a timeout outcome with cancellation audit detail. No product or test change.

- **Immediate work:** no unattended review. Await owner Decision 1 on `.agents/plans/rtk-router-delegation.md` (routing authority); implementation starts only after that approval. The 59-item `opr-*` intake queue below is superseded by that plan's Slice 6 dispositions and is retained only until it runs.

- **Review intake:** `opr-39` LOW accepted and plan-gated: `TryReclaim` can snapshot artifacts before marker ownership, remove the marker, then fail final directory deletion, leaving recognized residue without durable proof for any later retry.

- **Review intake:** `opr-40` LOW accepted and plan-gated: direct RTK execution and Bash execution use the 4 MiB stdout/stderr cap as each stream's eager initial allocation, producing avoidable 8 MiB large-object-heap churn per invocation.

- **Review intake:** `opr-36` MEDIUM accepted plan-gated: reserved-byte accounting uses a 32-bit product, so a supported multi-gigabyte high-concurrency state throws after reservation mutation and leaks admission capacity.

- **Review intake:** `opr-35` HIGH accepted plan-gated: a poisoned journal can falsely complete an ambiguous retained-evidence scan and make evidence retention-eligible before its preserved audit record is recovered.

- **Review intake:** `opr-34` LOW accepted plan-gated: recoverable canonical audit allocation temporary is scanned before its recovery owner runs, indefinitely blocking out-of-band audit administration and pinning awaiting evidence.

- **Review intake:** `opr-33` HIGH accepted plan-gated: literal alias-cmdlet spelling checks miss module-qualified and proven stock-alias invocations, causing a hard false shell-dialect refusal.

- **Review intake:** `opr-32` HIGH accepted plan-gated: explicit local/private function scope prefixes prevent a supported lexical definition from matching its collision-named use, causing a hard false shell-dialect refusal.

- **Review intake:** `opr-31` MEDIUM accepted and plan-gated: a transient Unix descendant probe failure can permanently discard a live reparented descendant before its later process-group escape.

- **Review intake:** `opr-30` MEDIUM accepted and plan-gated: Unix containment healthy-observation evidence is not bound to the released, live worker interval, permitting false empty-domain proof.

- **Review intake:** `opr-29` MEDIUM accepted and plan-gated: case-insensitive worker-environment validation and Unix launcher re-materialization reject case-distinct host variables or collide with case-distinct bootstrap-like names before spawn.

- **Review intake:** `opr-28` LOW accepted and plan-gated: the Unix launcher's own broker-handshake timeout is misclassified as caller cancellation when the overall startup deadline is later.

- **Review intake:** `opr-27` LOW accepted and plan-gated: structured Unix broker startup failures are mislabeled protocol corruption and lose their stage/native-error diagnostic.

- **Review intake:** `opr-26` MEDIUM accepted and plan-gated: a cached pre-arm Unix process snapshot can satisfy the registry's healthy-observation gate and falsely confirm an escaped domain empty.

- **Review intake:** `opr-25` MEDIUM accepted and plan-gated: Unix exit observation converts identity-query exceptions or a faulted broker wait into worker death, poisoning a healthy warm session.

- **Review intake:** `opr-24` LOW accepted and plan-gated: confirmed-empty Unix launch cleanup drops the containment task, so a created broker/worker domain is reported as never launched.

**Review intake:** `opr-23` MEDIUM accepted and plan gated: after Windows process creation, fast containment proof can erase launch provenance, causing cancellation to remove/cool the slot while slower proof leaves the same postlaunch failure `Faulted`.

**Review intake:** `opr-22` LOW accepted and plan gated: startup cancellation begins before first-use factory construction, while the worker factory receives a later deadline and uses wall-clock comparison to distinguish timeout from shutdown, so a real timeout can be mislabeled `worker_start_canceled`.

**Review intake:** `opr-21` LOW accepted and plan gated: if worker initialization and its containment cleanup both fail, the cleanup wrapper overwrites the primary timeout/cancellation detail with generic `worker_initialize_failed`.

**Review intake:** `opr-20` HIGH accepted and plan gated: cancellation of `StateAsync` in the proved pre-write window can poison and replace a healthy worker, losing warm named-session state even though no request reached the worker.

**Review intake:** `opr-19` HIGH accepted and plan gated: every client stop self-rejects its graceful shutdown request after completing `_fatal`, so `shutdown` / `stopped` is unreachable and all stops fall through to forced containment.

**CI remediation complete:** `.agents/plans/ci-macos-process-snapshot-guard.md`
closed at isolated-probe head `0499aa7`. The dedicated fixture-process guard
passed 1,221/1,221 local server tests, all six jobs in run `30727607324`, and two
additional exact-head macOS attempts (`91446210698`, `91446785138`). Production
remained unchanged; direct macOS mutation proof was unavailable and is not
claimed.

**Immediate next:** obtain Decision 1 on routing authority in `.agents/plans/rtk-router-delegation.md`. Do not invoke a reviewer unless the owner separately approves that exact review.
master. `opr-3` closed through PR #27 as `f5da911`; `opr-1`, `opr-2`, and
`opr-4` through `opr-18` are accepted and plan gated. `opr-14` is HIGH:
fixed-signature `fcntl` P/Invokes mispass variadic arguments on Apple arm64;
`opr-15` is HIGH: Unix identity-probe errors can falsely clear a live observed
escape. `opr-16` is LOW: a deadline-cancellation test uses the same losable
callback witness as the repaired Windows CI recurrence. `opr-17` is HIGH:
valid alias-definition parameter orderings are hard-refused as bash before
execution. `opr-18` is LOW: the first clean available-module inventory silently
persists after later session module-search-path or module-file changes. All five remediations
await approved plans. The latest review queue also added
Windows stdin partial-publication, timeout parsing, invoke validation, and
Unix worker-environment identity findings; lifecycle callback candidates
were adjudicated invalid. Keep periodic GitHub scans active for the four open,
externally gated issues.

**Then:** continue periodic live issue scans. Build identity and release
readiness remain parked while product defects are active.

1. Continue only an acceptance gate when its actual environment becomes
   available; do not conflate them. Rerun the remaining SIEM symlink-protection
   cases only under a Windows identity allowed to create test symlinks. Retry
   the exact-account Graph `/me` read only when the owner is ready to complete
   device authentication inside its 120-second window; retry Outlook metadata
   only after that exact account is configured as the profile's current user.
   On-prem Exchange still needs a different EMS-capable host. Run the ARM64 gate
   only on matching real Linux. The candidate is installed; the next
   intended-harness gate is a fresh Claude or Codex session proving the new
   five-tool schema and one ordinary invoke without cached removed-tool
   references. Do not reinstall merely to recover this deliberately severed
   client connection. No further ungated code change is queued.
2. Preserve `feature/mcp-resilience-r1` and every other work-carrying branch.
   Do not merge, install, delete, or continue the guardian/private-host line;
   the production-reliability salvage plan supersedes it.
3. Keep the rbc remainders parked until the replacement plan decides which
   runtime survives. rbc-15 remains closed; its residual recycled-PID
   incarnation hardening stays a follow-up. Do not continue the saved rbc-5
   post-start attach WIP.
4. Hold mini-SIEM at the S4 fixture gate recorded under `## Open / Parked`.
   When producer-owned v3 request bytes land, execute S4 from the complete
   producer corpus; do not substitute receiver-authored fixtures. Do not begin
   S4–S6 or modify PTK runtime for SIEM work.
5. Release distribution is blocked until the replacement production topology
   is approved, implemented, and directly validated. Do not build a guardian
   layout or reuse the old R7 cutover assumptions.
6. When the owner releases the decisions hold, reconcile the rejected
   security mechanism, retired durable/shared staging, and PTK→RTK routing
   direction in `.agents/decisions.md`.
7. On Microsoft's #7 verdict, execute the on-verdict steps in
   `.agents/plans/defender-fp-submission.md`. Meanwhile the unblocked CI
   remainders are the Windows kill-path test diagnosis (2/1587 failures) and
   the pre-existing `tls_protection` SIEM conformance-host TLS-material
   hardening.

## Open / Parked

- Mini-SIEM S4's fixture gate remains intentionally closed: producer-owned
  exact v1/v2/v3 OTLP byte corpora must all exist before S4 begins. The
  producer-side serializer for current v1/v2 records is landed at `1f6d485`,
  but R0 supplies only JSONL v3 contract vectors, not a producer-owned
  serialized v3 OTLP request; do not synthesize one in the receiver or treat
  its hand-authored S2 structural test as a golden producer fixture.
- Warm-backend slice 7 is unblocked open work, currently unscheduled, and
  remains owner-run Windows validation: AD native import/warm reuse; Exchange
  implicit remoting with first-vs-repeated `Get-Queue` latency; EXO/Graph
  unattended certificate auth. Its plan status still needs correction; see
  `## Blockers`.
- Durable checkout and shared runspaces are removed from the candidate build
  scope by the owner's 2026-07-11 direction. Their older open-decision entry
  remains stale while `.agents/decisions.md` is under hold; the idea plan is
  retained as history/evidence, not current implementation direction.
- GitHub #8 is an owner field report from 2026-07-23. Its older
  installed runtime dropped script/lazy/COM values. The replacement server now
  preserves synthetic EXO-style selected/deserialized values, evaluates an
  explicitly selected script property exactly once in the user pipeline, and
  surfaces the tested terminating-error message; it still truthfully labels
  uninspected active type data as incomplete. Real EXO selected values now pass.
  Keep #8 open until a real Outlook item retains selected values without extra
  shaping-time getter execution and a real non-core EXO/Outlook terminating
  exception returns its exact message. The verified progress and gate are posted
  on issue #8; Opus accepted holding without a speculative active-getter change.
- Fable's accepted R0 review noted one non-blocking test-fixture hygiene risk:
  if the testhost dies before its `finally`, the Unix guardian broker fixture's
  TERM-immune paused process group can remain until its fixture guardian is
  killed. This cannot make the guard falsely pass or affect an R0 product
  contract; consider an stdin-EOF guardian watch in a later scoped test-hygiene
  slice.

## Blockers

- **The invalid same-testhost cross-collection contention is repaired locally,
  but its residual signals remain open.** Before Slice 1a, six default-parallel
  runs exposed five intermittent fixed-watchdog/PATH failures while a serialized
  control passed. Slice 1a disables default collection parallelism without
  changing explicit concurrency tests, product deadlines, or assertions; three
  consecutive ordinary runs then passed 1,557/1,557 under starting load averages
  as high as 60.86. This closes the scheduling artifact, not every underlying
  risk. A recurrence of the anchored-evidence publication/removal ordering race
  or `JobManager.Dispose` bounded-observer failure in a serialized run is still
  a real signal. Fixed watchdog sensitivity remains. The two recorded Windows
  containment failures are repaired and the exact Slice 4 head passed direct
  Mac, Linux, and Windows batteries; current hosted-CI evidence is still
  absent, and later plan slices plus the deployment gates remain before any
  production-ready claim.
- **Direct ARM64 Linux build/execution validation needs a matching real host.**
  The prior UTM VM is not in use by owner direction. Cross-publishing from
  macOS is now correctly refused because it produced a Mach-O worker broker
  inside a `linux-arm64` layout. Do not claim this gate from cross-build output;
  run it on real ARM64 Linux when such a host is available. Historical UTM/
  `Grpc.Tools` evidence remains in `.agents/machines.md`, not as the current
  execution path.

- **Current Slice 10 generic Windows validation is pending while
  `NETWATCH-01` is unavailable.** Unix production containment and timeout
  replacement now pass direct macOS ARM64 and Linux x86_64 acceptance; prior
  retained Windows Job Object evidence does not substitute for an
  exact-current-head Windows run. When the host returns, run only Windows
  packaging/process/Job Object/timeout/crash/cleanup acceptance there. Exact
  host evidence is in `.agents/machines.md`.
- **The real AD/Exchange/EXO/Outlook workflow gate needs a company-connected
  supported Windows admin host.** `NETWATCH-01` is a personal gaming machine
  without company AD, on-prem Exchange, Exchange Online, or Outlook
  administration access and cannot close this gate. The replacement server's
  synthetic EXO-style projection remains useful evidence but is not a
  substitute for the required modules, network access, authentication, and
  real enterprise objects.
- **Decision-log conflict, correction blocked by the owner hold:**
  `.agents/decisions.md` still describes the policy-file gate as the open
  response after its criterion fires, while the later explicit owner call in
  `.agents/plans/security-layer.md` rejects that response. Its shared-host
  entry stages durable GUID sessions followed by sharing, while the owner's
  later direction removes both from the candidate build. Do not implement
  either stale direction. Its audit-export evidence at line 312 also points to
  producer behavior that the corrected plan removes; treat that as known stale
  evidence after audit decision 4, never as authority to restore the producer.
  Preserve these decision-log conflicts until the hold is released.
- **GitHub #7 closure is gated on Microsoft's WDSI verdict** on the submitted
  `PtkMcpServer.dll` (owner-submitted 2026-07-20). Interim quarantine-detection
  mitigation is landed (`51ce880`); no further local action on #7 until the
  verdict lands.
- **Plan-record drift, reported but not edited in this narrow state pass:**
  the warm-runspace plan still says slice 7 is paused behind the already
  decided GO, and the shared-runspace idea still assumes the rejected policy
  gate. Explicit owner calls, uncontested decisions, and live repo evidence
  named above control. Both re-confirmed still stale 2026-08-03.
- **rbc-5/rbc-6 containment WIP is unlocated in this clone (2026-08-03).** The
  recorded carrier `fix/rbc-6-unix-sigkill-escalation` @ `2b3ce1a` does not
  resolve: no such branch locally or on `origin`, and `2b3ce1a` is an ordinary
  `master` ancestor. Either the branch lives only on another remote/clone or it
  was deleted. Do not recreate or re-derive the WIP; an owner ruling is needed
  on whether it still exists anywhere before the `## Now` preservation
  instruction can be acted on.

## Verification

- Automated verification entry point: `.agents/repo-guidance.md`
  (Verification). Review-loop evidence lives in `.agents/review/index.md`;
  do not duplicate volatile counts here.
- Audited-session Slices 0-6, Slices 7a-7h, and the Windows wait-ownership
  prerequisite are complete locally. Canonical fixed-head acceptance evidence
  lives in `.agents/review/index.md`; host-specific verification records live
  in `.agents/machines.md`.

## Active Sources

- `.agents/plans/rtk-router-delegation.md` (draft; no implementation authority;
  supersedes the minimum-viable-release plan on approval)
- `.agents/plans/minimum-viable-release.md` (draft; no implementation authority;
  its release-blocking rule, non-goals, and packaging/proof slices are retained
  by reference from the router-delegation plan)

- `AGENTS.md`
- `.agents/repo-guidance.md`
- `.agents/plans/production-reliability-salvage.md`
- `.agents/plans/release-distribution.md` (release outcomes remain active; its
  guardian/R7 mechanics are superseded at the top of the file)
- `.agents/plans/defender-fp-submission.md`
- `.agents/plans/mini-siem-discovery.md`
- `.agents/plans/mini-siem-implementation.md`
- `.agents/review/index.md`
- `.agents/machines.md`
- `.agents/decisions.md` (under owner hold; named stale entries are evidence,
  not current implementation authority)

The security-layer, broad RTK-rewrite, audited-harness, guardian-resilience,
warm-runspace, and shared-persistent plans are retained prior-art/history, not
active implementation pointers. The production-reliability plan controls where
their text conflicts with the implemented replacement.

## Unrecorded Repo Memory

- None known.

History: rotated entries live verbatim in `docs/history/state-archive.md`.
