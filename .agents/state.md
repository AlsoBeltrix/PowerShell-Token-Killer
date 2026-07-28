# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

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
- **Production-reliability salvage Slice 0 is recorded on
  `impl/production-reliability-salvage`.** The unchanged product base is exact
  `origin/master` commit `c9b11bc`; only the reviewed plan/review records sit
  between that product tree and the implementation branch head. The Pester
  suite passed 141 tests with two platform skips, the SIEM suite passed
  247/247, the handshake passed, and the third full server run passed
  1,587/1,587. The first two full server runs exposed three different
  intermittent failures that each passed immediately in isolation; they remain
  recorded production blockers rather than being normalized away. Two
  independent ephemeral Codex agents received distinct PTK server PIDs `9595`
  and `9596`, proving the production harness gives them separate MCP server
  processes/connections. No product file changed during the baseline.
- **The owner-approved Slice 1 R0 retirement is implemented on
  `impl/production-reliability-salvage`.** The frozen guardian/private-host
  contract, recovery/package schemas and examples, native acquisition
  inventory, `PtkResilienceTestFixture`, and its two consuming test classes are
  removed with their project references. The retained audit/SIEM vectors now
  have a smaller `AuditInteropContractTests` guard, and
  `ToolSchemaConformanceTests` freezes the exact live five-tool direct-server
  surface. The real `PtkContainmentTestFixture` and both Windows containment
  test files are byte-identical to the base. Focused contract/schema tests,
  Pester, SIEM, formatting, and the stdio handshake pass. Three bounded
  post-change server-suite attempts each passed 1,556/1,557 but hit a different
  already-isolated timing failure. Diagnosis then proved that none of the five
  implicated runtime or test files differs from the product base, all five
  implicated classes pass together under default parallelism, and the complete
  suite passes 1,557/1,557 when xUnit collections are serialized. A paired
  default-parallel full run also passed 1,557/1,557, confirming intermittent
  load sensitivity. The 16-logical-CPU Mac was under unrelated load averages
  around 24/45/47, including a long-running Headroom proxy using about two
  cores; no PTK, pwsh, testhost, or sleep-process leak remained. Slice 1a is
  now implemented with test-runner JSON and no timeout or product change. The
  reviewed assembly attribute was discarded after the exact runner and an
  isolated reproduction both ignored it; the runner JSON setting proved
  effective and remains explicitly overrideable. Three consecutive ordinary
  server runs passed 1,557/1,557, followed by green Pester, SIEM, and stdio
  handshake checks. This removes invalid cross-collection testhost contention;
  it does not erase the anchored-evidence ordering race, fixed watchdog residue,
  carried Windows failures, or the lack of current cross-platform CI evidence.
  Product decisions 3-4 and any `ci/**` push or PR remain independently gated.
  Exact machine evidence is in `.agents/machines.md`.
- **Production-reliability salvage Slice 2 is complete in the local
  checkout.** Ordinary invoke no longer opens or depends on audit storage;
  `ptk_state` reports audit disabled. `WorkerSupervisor`,
  `SupervisorLifecycle`, and `SupervisorCallFilter` now own admission,
  cancellation/drain, and ordered runtime shutdown without the idle watchdog.
  The anchored OTLP producer, its runtime protobuf dependencies, and its
  producer-to-SIEM conformance surface are removed; the relocated receiver
  proto, standalone SIEM, legacy `PtkAuditAdmin`, and local legacy-state
  primitives remain. The runtime installer excludes `PtkAuditAdmin`. Exact
  candidate tree `faf8c2338a1a729fc5c46d87376475265148a5b0` passed the full
  macOS ARM64 battery and a clean Linux x86_64 battery over SSH on `magneto`.
  A deliberate startup regression made the unwritable-audit-root integration
  guard fail, byte-exact restoration made it pass, and the runtime-package
  boundary guard was separately proved red-to-green. ARM64 Linux is untested;
  UTM is not to be used. Exact evidence is in `.agents/machines.md`.
- **Production-reliability salvage Slice 3 is complete locally at code head
  `bfa335ec223609df9ce0dbfcfd9efe99382203d4`.** The prepared
  prepare/descriptor/commit/abort protocol, cold-job worker codec, and their
  staging-only tests are removed. One strict v2 worker protocol now has only
  initialize/ready, foreground invoke, nonblocking state, targeted cancel,
  bounded artifact transfer, one terminal, and shutdown/stopped messages.
  Request IDs are monotonic, stale session/incarnation frames and unsolicited
  terminals are fatal, frames and logical fields are bounded, and invalid
  worker-owned output is classified as a runtime failure rather than blaming
  supervisor input. A real unwired fixture proves warm variables, prompt busy
  state, cancellation with one terminal, and isolation between two differently
  identified worker servers. Deliberate mutations proved the message union,
  no-background boundary, artifact length/digest, capacity, shutdown
  correlation, warm-state, cross-routing, and unsolicited-terminal guards.
  The exact commit passed 1,223/1,223 server tests, Pester 141 with two platform
  skips, SIEM 247/247, formatting, and the public handshake on macOS ARM64, then
  the same behavior battery in a clean disposable checkout on Linux x86_64
  `magneto`. Public MCP behavior is unchanged. The slice is not pushed or
  installed; exact evidence is in `.agents/machines.md`.
- **Production-reliability salvage Slice 4 is complete locally at code head
  `75505b98021229efebc338597d38450059da9294`.** Disposable workers now use
  the worker-only Unix broker or Windows creation-time Job Object containment,
  with independent per-worker teardown and unchanged public MCP behavior.
  Direct Windows validation found and repaired four concrete issues: Job Object
  termination now retains the handle until emptiness is proved; route-consent
  testing no longer depends on host aliases; a failed kill request cannot kill
  the root through descendants-only cleanup; and a wedged first containment
  request cannot race away the bounded observer's independent retry. The last
  retry guard passed 30 consecutive Windows runs. Exact-head full batteries
  passed on macOS ARM64, Linux x86_64 `magneto`, and Windows x64 `NETWATCH-01`:
  server 1,240/1,240, Pester 141 passed with two platform skips, SIEM 247/247,
  and the full stdio handshake. No test process survived and every disposable
  remote checkout was removed. The slice is not pushed or installed; exact
  evidence is in `.agents/machines.md`.
- **Production-reliability salvage Slice 5 is complete locally at code head
  `001c4ebe23d48e1804f2bb169f09a8f0e0c0dd2a`.** One MCP connection now owns
  a fixed maximum of eight internal named-session slots, including lazy
  `default`, with one isolated contained worker process and warm runspace per
  session. Lifecycle admission, shared startup, per-session foreground
  serialization, automatic timeout/crash/transport recovery, confirmed-empty
  replacement, cached PID health, sealed-output survival, close/reset, and
  concurrent connection shutdown are covered behind the internal fixture.
  The real-process guard proves overlapping commands, variables, modules,
  environment, directories, and processes remain isolated and that workers
  inherit the supervisor's launch directory. Deliberate mutations proved the
  launch-directory, containment-proof, stopped-worker PID, late-start
  shutdown, and invoke/state transport-recovery guards. Exact-commit batteries
  passed on macOS ARM64, Linux x86_64 `magneto`, and Windows x64
  `NETWATCH-01`: server 1,264/1,264, SIEM 247/247, platform-appropriate Pester
  counts, and the public stdio handshake. No candidate process or disposable
  remote checkout survived. The public MCP surface remains unchanged; the
  slice is not pushed or installed. Exact evidence is in
  `.agents/machines.md`.
- **Production-reliability salvage Slice 6 is locally implemented and committed
  at code head `6c9bdacc84685af54055b22627666bfb8231c2d1`; Windows validation
  remains pending.** Production now exposes exactly `ptk_invoke`,
  `ptk_output`, `ptk_reset`, `ptk_session`, and `ptk_state`. Cold jobs and the
  in-process runspace path are gone; every command executes only in the
  explicitly resolved named session's contained worker, while reset, state,
  lifecycle, and sealed output remain supervisor-owned. Twelve deliberate
  guard mutations failed independently and were restored byte-for-byte. The
  exact commit passed 1,167/1,167 server tests, Pester 141 with two platform
  skips, SIEM 247/247, the public handshake, formatting, and package smoke on
  macOS ARM64; the same server/Pester/SIEM/handshake battery passed from the
  verified archive on Linux x86_64 `magneto`. `NETWATCH-01` was unreachable at
  both its current DNS address and its previously used address, so Windows x64
  remains pending. No candidate Linux process survived. The slice is not
  pushed or installed; exact evidence is in `.agents/machines.md`.
- **Production-reliability salvage Slice 7 is locally implemented and committed
  at code head `51594735e40c6d50a2aaf94c84a9e55a70f63b50`; macOS ARM64 and
  Linux x86_64 are verified, and Windows x64 remains pending.** It classifies
  failure before the first invoke pipe-write call as `not_started` and every
  failure after write-call entry and before a complete terminal as
  `outcome_unknown`. A complete terminal remains authoritative; recovery makes
  one automatic replacement attempt; a failed replacement projects
  `reset_required=true`; and sibling and separate-supervisor isolation remain
  intact. Six deliberate regressions failed for their intended reason and
  every source was restored byte-for-byte. Exact-commit batteries passed on
  macOS and Linux: server 1,180/1,180, SIEM 247/247, platform-appropriate
  Pester counts, and the public stdio handshake; macOS additionally passed
  scoped formatting and self-contained publish smoke. No candidate process or
  disposable Linux checkout survived. `NETWATCH-01` timed out again after the
  Linux run. The local Slice 6 and Slice 7 archives remain available for
  Windows validation. The owner directed continued local implementation
  without waiving either Windows gate. The slice is not pushed or installed;
  exact evidence is in `.agents/machines.md`.
- **mini-SIEM S1-S3 are complete and incorporated on local `master`; the S3 durable
  store head is `eb51f2e` and its producer-conformance compatibility head is
  `9f53831`.** S1 supplies the solution skeleton and strict startup config; S2
  supplies the independently compiled canonical proto, bounded exact
  one-record validator, real Kestrel mTLS, and frozen response table. S3 replaces
  the fail-closed placeholder with a startup-migrated SQLite store: one
  serialized nondeferred transaction re-reads and conditionally advances the
  producer chain, stores exact raw request evidence, and atomically appends the
  versioned custody ledger under asserted WAL/FULL writer policy. Byte-identical
  replay is idempotent even after head advance; duplicate mismatch, chain
  failure, and strict-validator failure commit quarantine evidence before a
  permanent response; any commit/quarantine failure remains retryable and can
  never false-ack. The SQLite package's vulnerable native minimum is overridden
  to its patched bundle, and the dependency audit is clean. The isolated
  producer conformance project remains source-compatible and its ordinary fake
  receiver path remains unchanged. Producer-owned exact v1/v2 fixtures exist at
  `1f6d485`; the serialized v3 OTLP request fixture remains absent and is never
  invented from R0's JSONL vector. Local evidence and guard proofs are recorded
  in `.agents/machines.md`. Combined local verification of merge `374f164`
  passed on macOS, exact integration tip `1ad195e` passed direct Windows
  checkout validation, and exact snapshot `a473ca3` passed the direct Linux
  behavior battery after manual generation around an ARM64 MSBuild-only
  `protoc` crash; the exact commands, contexts, counts, and clean-build caveat
  are recorded there. Hosted run `29520427103` at `72c6103` passed all three
  SIEM jobs and the complete Ubuntu/macOS product batteries; its Windows
  server failure is addressed by the CI portability follow-up below.
- **Audited-harness Slices 7a-7f and the Windows wait-ownership prerequisite
  are complete and landed on local `master`; Slice 7f code head is
  `a9e757e`.**
  The strict bounded v1 worker protocol freezes all nine wire kinds, enforces
  strict UTF-8 NDJSON with duplicate/unknown/version rejection, caps encoded
  frames at 1 MiB and JSON depth at 32, preserves fragmented and coalesced
  input, serializes writes, latches ambiguous writer failure, and clears
  pooled script-bearing buffers. Claude Code 2.1.209 accepted exact range
  `a88d605..f86de26` with `guard_confirmed=true` after six independent
  mutations and the full battery. Code head `e70089f` then adds the
  behavior-preserving tool-to-provider operations seam and the separately
  disposable ordered lifetime seam; Claude accepted exact range
  `2eca287..e70089f` with `guard_confirmed=true` after four mutations and the
  full battery. Code head `56734e3` then adds the platform-neutral worker
  lifecycle core with strict boot/phase/correlation checks, deadline and host
  cancellation races, and exactly-once lifetime cleanup; Claude accepted
  exact range `cfaee5f..56734e3` with `guard_confirmed=true` after seven
  independent mutations and the full battery. Code head `bbc2a0e` then adds
  the deliberately unwired Windows creation-time containment primitive:
  exact `KILL_ON_JOB_CLOSE` Job Object configuration, one five-handle
  `CreateProcessW`/`STARTUPINFOEX` launch with `JOB_LIST` and `HANDLE_LIST`,
  closed Unicode environment, proof-only suspension, job-first rollback, and
  direct runnable/pre-resume/tree-death Windows fixtures. Claude accepted exact
  range `3348167..bbc2a0e` with `guard_confirmed=true` after seven mutations
  and the full battery; the exact tree also passed direct `NETWATCH-01`
  validation. Code head `d1cca1b` closes cancellable-wait ownership using a
  per-wait noninheritable duplicated process handle, with guards covering both
  the owning constructor and active async call path. The preliminary `5ea7d60`
  review was reopened after independent preflight found its active-path guard
  vacuous; only the corrected fixed-SHA acceptance is final. Code head
  `12617cc` then adds the Windows-only managed `--worker` lifecycle entry,
  exact bootstrap-variable removal and noninheritable pipe ownership, stable
  managed exit mapping, and bounded allow-listed abnormal diagnostics while
  leaving default MCP operations in-process. Independent preflight reopened
  preliminary head `e2cbfb5` for two guard vacuities; test-only commits
  `e9421cc` and `12617cc` close the concrete environment-removal and global
  diagnostic-destination gaps. Claude accepted exact range
  `eec7ed1..12617cc` with `guard_confirmed=true` after eleven independent
  mutations and the full battery; the exact tree also passed direct
  `NETWATCH-01` lifecycle and containment validation. Code head `a9e757e` then
  adds strict private request/cancel parsing, response encoding, and a bounded
  standalone scheduler with targeted cancellation, exactly-one terminal
  ownership, and fatal peer cleanup while remaining deliberately unwired from
  production.
  Claude accepted exact range `3580e67..a9e757e` with
  `guard_confirmed=true` after independent mutation proof and the full battery.
  Completion records are committed at `83ca3b1`; the accepted feature history
  was fast-forwarded to local `master`, content arrival was verified by direct
  branch diff, and the feature branch was removed. Canonical review and Windows
  evidence is in `.agents/review/index.md` and `.agents/machines.md`.
- **Audited-harness Slice 7g is complete and landed on local `master` at code
  head `eef38cb`.** It adds only strict transport-neutral value codecs for
  foreground invoke, job controls, and state, with strict logical UTF-8
  script/result limits. It does not bind invoke to ordinary request transport
  or add runtime execution, prepare/commit, background start, audit/output
  transfer, job-ID allocation,
  reset, proxy wiring, or MCP behavior. Claude Code 2.1.210 accepted exact
  range `a83e2e6..eef38cb` with `guard_confirmed=true` after ten independent
  mutations and the full battery. Completion records are committed at
  `8428f17`; the accepted feature history was fast-forwarded to local `master`,
  content arrival was verified by direct branch diff, and the feature branch
  was removed. Canonical evidence is in the audited-harness plan and
  `.agents/review/index.md`.
- **Audited-harness Slice 7h is complete and landed on local `master` at code
  head `8f5c57c`.** It adds only strict unwired prepare,
  prepared-correlation, commit, and abort values for foreground invoke. It
  freezes canonical UUIDv4 plan ID, exact strict-UTF-8 script digest, worker
  generation, and original absolute deadline correlation without adding
  reservation behavior, execution, the final prepared descriptor, audit/output
  transfer, background IDs, server wiring, or MCP behavior. Claude Code
  2.1.210 accepted exact range
  `1179ed0..8f5c57c` with `guard_confirmed=true` after eleven independent
  mutations and the full battery. Completion records are committed at
  `c07a958`; the accepted feature history was fast-forwarded to local `master`,
  content arrival was verified by direct branch diff, and the feature branch
  was removed. Canonical evidence is in the audited-harness plan and
  `.agents/review/index.md`.
- **The owner-approved two-layer MCP resilience sequence is implemented
  through every locally actionable R5 Windows barrier; the exact verified
  code/test head is `195e7e6`. R0-R4 are complete.** R5 now owns real Windows
  private-host launch, containment, monitoring, exact-generation restart,
  lifecycle audit, delivery truth, declared-state restoration, job/output
  tombstones, and recovery-aware state. Real apphost tests cover startup and
  replacement crashes, request/effect/response barriers, native descendant
  cleanup, idle persistence, lost jobs, and ambiguous reset requiring explicit
  repair. The complete repository battery and stdio handshake pass at that
  head; exact machine evidence is in `.agents/machines.md`. R5 is not
  cross-platform complete: production Unix still deliberately refuses startup
  until the approved native outer broker exists. The target keeps one public
  stdio guardian alive while it
  restarts an exact-version private host, and makes a healthy host replace an
  unexpectedly lost session worker. It never replays ambiguous work, changes
  generation on every replacement, recreates only frozen declared bootstrap
  state, and keeps guardian-local health/output surfaces usable. The canonical
  draft is `.agents/plans/mcp-resilience.md`; it narrowly supersedes the older
  explicit-restart target without weakening audit, containment, or session
  isolation. The owner additionally accepted the guardian crash boundary and
  fail-fast model-retry contract: no server queue or saved-state authoring tool
  in this stage; only proved-no-start errors direct the model to poll state and
  submit a new request, while `outcome_unknown` is never retried. The owner
  requested a fresh Claude Fable 5 maximum-effort review of that update.
  Claude Code 2.1.210 accepted the complete exact range
  `5ae154c..b4a2c0c` with `guard_confirmed=true` and no comments; its result
  metadata reported `claude-fable-5` plus CLI helper usage and no Opus model.
  Canonical review evidence is in `.agents/review/index.md`. For packaging,
  the owner confirmed nothing
  has shipped and only this development environment is in use, then delegated
  the choice: keep the current registration usable through R6, perform one R7
  cutover to the matched guardian package, and preserve no direct-server
  migration or compatibility layer. An eligible alias also recovers
  automatically to its fresh declared baseline after an execution-timeout
  terminal and confirmed old-tree death; the timed-out call is never replayed.
  Retryable recovery refusals carry phase, attempt, poll delay, and an exact
  readiness gate: delay expiry permits only `ptk_state`, and a fresh operation
  waits for readiness plus pre-write dispatch revalidation. After private
  write begins, the existing `outcome_unknown`/no-replay boundary still wins.
  Exact R0 verification and direct macOS/Windows evidence are recorded in
  `.agents/machines.md`. The in-repo independent audit found no R0 blocker.
  After the compression proxy was removed, Claude Code 2.1.211 routed directly
  to `claude-fable-5` at maximum effort and accepted exact range
  `215e10f..c1d809f` with matching SHAs and `guard_confirmed=true`. Its model
  metadata reported Fable plus the Haiku helper and no Opus model. It proved
  the Sentinel projection, post-write `outcome_unknown`, and Unix hard-
  containment guards red-to-green, restored its clean detached tree, and
  reported only one non-blocking fixture-hygiene advisory. Canonical review
  evidence is in `.agents/review/index.md`; the earlier fail-closed attempts
  and their resolution remain in
  `.agents/review/mcp-resilience-r0-review.contested.md`.
- **CI portability Slices 11-16 are complete at final test-only head
  `5642376`; hosted run `29541559607` passed all six jobs at exact pushed
  descendant `7bc08aa`.** Run
  `29536074900` proved Slices 11-12 closed their R0 checkout and marker races,
  then exposed four older scheduler, reprime-setup, Unix deadline, and Windows
  retry-notification assumptions. Each repair changes only its test; the
  review closure additionally freezes the native deadline helper without
  restoring a scheduler-latency ceiling. Focused red/green proofs, the complete
  macOS battery, direct Linux/Windows batteries at the runtime-equivalent
  preceding head, exact final-head platform guards, and independent review all
  passed, followed by green Ubuntu/macOS/Windows product and SIEM jobs. Slices
  1-10 remain complete with green hosted evidence. Canonical details are in
  `.agents/plans/ci-portability-repair.md` and
  `.agents/machines.md`; RTK distribution remains a separate decision.
- **Audited-harness Slice 6 is complete locally.** Code
  head `7999328` moves invoke/job/state/reset behavior and session-lifetime
  caches behind one owning `SessionRuntime`, leaves audit and output
  capabilities per operation, keeps MCP adapters thin and schema-compatible,
  and preserves jobs-before-runspace audited shutdown. Claude Code 2.1.208
  accepted exact range `aca20a6..7999328` with `guard_confirmed=true` in a
  clean detached worktree after independent cache-isolation, ownership,
  adapter, and reset-lifetime mutation proofs plus the full battery. Completion
  records are committed at `67b900d`. The accepted feature history was
  fast-forwarded to local `master`, content arrival was verified independently,
  and the feature branch was removed. Canonical evidence is in
  `.agents/review/index.md`.
- **Audited-harness Slice 5 is complete locally.** Final code head
  `fc61be6` completes audited cold-background planning, typed exactly-once
  dispatch/fallback, provenance-
  aware polling, and path-free output recovery. Eligible direct-text jobs
  reserve output capacity before start, seal one immutable bounded supervisor
  artifact, and publish its opaque handle before terminal notification;
  seam-absent RTK jobs remain explicitly unrecoverable, and model-facing job
  surfaces expose no internal path. Claude Code 2.1.208 and Grok 0.2.93 each
  accepted exact range `ee21f16..fc61be6` with `guard_confirmed=true` in clean
  detached worktrees after independent guard proofs and the full verification
  battery. Completion records are committed at `bbb1742`. The accepted
  feature history was fast-forwarded through handoff tip `de8dc53` to local
  `master`, content arrival was verified independently, and the feature branch
  was removed. Canonical evidence is in `.agents/review/index.md`.
- **Audited-harness Slice 4 is complete locally.** Final product
  head `76d4f0c` integrates the supervisor-owned output store and audited
  `ptk_output`, bounded two-stage same-invocation capture/recovery, anonymous
  retained artifacts, truthful recovery hints, behaviorally inert legacy
  `raw`, explicit `route=pwsh` consent, and the narrow unshaped state probe.
  Claude accepted exact integrated range `9c89abf..76d4f0c` with
  `guard_confirmed=true` after eight independent cross-slice red-to-green
  proofs and the full local battery. The accepted feature history was
  fast-forwarded to local `master`, content arrival was verified independently,
  and the feature branch was removed. Canonical review evidence is in
  `.agents/review/index.md`; host-specific evidence is in `.agents/machines.md`.
- **Audited-harness Slice 3 is complete locally.** Final
  integrated code head `b78d9c6` completes structured foreground routing,
  audited RTK/Bash dispatch, bounded preference-independent RTK capture,
  exact-original pre-start fallback, and post-success mixed-domain guidance.
  Claude accepted the exact fixed head with `guard_confirmed=true`; all six
  admitted material findings are closed. The accepted feature history was
  fast-forwarded to local `master`, content arrival was verified independently,
  and the feature branch was removed. Canonical review and platform evidence
  is in `.agents/review/index.md` and `.agents/machines.md`.
- **Audited-harness Slice 2 is complete locally.** The final
  integrated code head `3d3739a` completes job/control audit, local-only and
  anchored OTLP export, evidence administration and retention, permanent
  operator disposition, and the strict `ptk.audit/2` extension. Claude
  accepted the exact fixed head with `guard_confirmed=true`; all four material
  integrated-review findings are closed, and the accepted feature history was
  fast-forwarded to local `master`. Canonical review and platform evidence are
  in `.agents/review/index.md` and `.agents/machines.md`.
- **Local branch management is delegated for the remaining audited-harness
  implementation** (owner, 2026-07-11): create, switch, merge, and delete local
  implementation/review branches without per-merge confirmation.
- **Audited-harness slices 0-4 are complete locally.** Slice 1 commit
  `460c106` adds the mandatory current-server audit foundation, exact-script
  evidence, capacity reservation, protected local storage, and fail-closed
  pre-effect guards. Claude accepted the fixed-SHA implementation after an
  independent red→green guard proof; canonical review evidence is in
  `.agents/review/index.md` and platform evidence is in `.agents/machines.md`.
  `.agents/plans/audited-harness-sessions.md` combines
  mandatory PTK-owned/SIEM-exportable audit, private harness-scoped
  process-per-session workers, internal PTK→RTK routing, same-invocation
  output recovery, and no-retry mixed-domain handling. Its frozen slice-0
  results record the absent RTK capture seam, local-only/anchored OTLP
  contracts, exact profile/protocol bounds, carried routing guards, and live
  passing Windows/Unix parent-death probes. Both plan reviewers accepted the
  same approved pre-implementation content. Canonical pre-implementation
  review evidence and fixed-head guards live in `.agents/review/index.md`.
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
  slice 3 is blocked behind resilience R7 and `.github/workflows/release.yml`
  is still absent. The old 2026-07-25 calendar is superseded with no replacement
  date approved. The deliberately open hook-default choice also blocks slice 4.
- **Standing GitHub authority:** the owner granted persistent permission on
  2026-07-10 to comment, close, and triage issues in this repository as
  appropriate without per-action asks.
- **Mini-SIEM S3H is landed on `master` (no-ff merge, 2026-07-18).** Code head
  `c726a33` (record head `3bacbc4`) merged from
  `plan/mini-siem-storage-hardening`; the branch and worktree
  `.claude/worktrees/siem-storage-hardening` are retained pending owner
  cleanup direction. The receiver now applies one SIEM-local, fail-closed
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
  Hermes baseline review (`.agents/review/findings/rbc-1..13.md`,
  `.agents/review/index.md`) is committed. Owner triaged all 11 open findings
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
- **rbc-5/rbc-6 containment WIP remains uncommitted and preserved on
  `fix/rbc-6-unix-sigkill-escalation` at `2b3ce1a`; do not discard it without
  owner direction.** rbc-6's filed premise was false: .NET 10 Unix
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

1. Implement Slice 8 locally: keep output recovery optional, add the bounded
   per-invocation queue/sink path and session quotas from the approved plan,
   and prove a stalled or failed output path cannot delay, replay, or replace
   ordinary execution. When `NETWATCH-01` returns, first validate the exact
   Slice 6 and Slice 7 archives before claiming either cross-platform complete.
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
- GitHub issue #3 remains open (verified 2026-07-11): item 1 landed; items
  2-4 are an unplanned follow-up candidate, while its permission-bypass
  concern belongs to the open security track.
- GitHub #8, #9, and #10 are new owner field reports from 2026-07-23,
  unplanned and recorded here for durable tracking. #8: the object shaper
  emits `[active member not evaluated]` for script/lazy/COM members, making
  shaping unusable for EXO cmdlets and Outlook COM (Windows Server 2022);
  presentation-layer only. #9: calls hung 120 s on a dead MCP transport
  instead of failing fast, then the installed payload wedged on
  "audit persistence unavailable" across a full session restart. #10: the
  same wedge from a cold boot — `failure_class=journal.startup` every start
  on this machine, with fail-closed `ptk_reset` leaving no in-band recovery.
  #9/#10's root cause is undiagnosed and will not be diagnosed: the owner
  disabled the affected installed server and expects the post-R7 guardian
  reinstall to supersede it (owner, 2026-07-24).
- GitHub #11 (Codex keeps a stale ptk transport after the direct-server
  cutover) is open; its explicit product/client boundary is already carried
  into the resilience R7 real-Codex cutover validation.
- A pre-existing `AuditAnchoredRuntimeTests` assertion can observe the short
  interval between the final evidence-file publication and removal of its
  `.anchoring.*.script` temporary. It passed an isolated 10/10 and a clean
  complete rerun; repair the test synchronization in a separate scoped slice,
  not by weakening R0.
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
- **The server dependency graph now reports five high-severity advisories for
  transitive `System.Security.Cryptography.Xml` 10.0.6.** The path is
  `Microsoft.PowerShell.SDK` 7.6.3 through
  `Microsoft.Windows.Compatibility`/`System.ServiceModel`. This was discovered
  during Slice 1 restore/audit and is outside the R0-retirement scope; do not
  suppress it or claim the server dependency audit is clean.
- **Direct ARM64 Linux clean-build validation is blocked by a host-specific
  `Grpc.Tools` launch failure.** On the Ubuntu 26.04 ARM64 VM, the bundled
  `Grpc.Tools` 2.82.0 `protoc` succeeds when invoked directly with the exact
  generated command but exits 139 only when MSBuild launches it. Manually
  generating the identical intermediate files allowed every Linux behavior
  suite to pass. This does not invalidate that behavior evidence, but a clean
  ARM64 build must not be claimed until the launch failure is resolved or
  independently disproved; see `.agents/machines.md`.

- **R5 Unix production containment cannot be implemented or directly validated
  on the current Windows host.** Production composition deliberately fails
  closed on non-Windows until the approved native outer broker exists. Resume
  on direct Linux and macOS hosts with native toolchains; no enterprise network
  access or identity-system credentials are required. Exact host evidence is
  in the active workspace's `.agents/machines.md`.
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
  named above control.

## Verification

- Automated verification entry point: `.agents/repo-guidance.md`
  (Verification). Review-loop evidence lives in `.agents/review/index.md`;
  do not duplicate volatile counts here.
- Audited-session Slices 0-6, Slices 7a-7h, and the Windows wait-ownership
  prerequisite are complete locally. Canonical fixed-head acceptance evidence
  lives in `.agents/review/index.md`; host-specific verification records live
  in `.agents/machines.md`.

## Active Sources

- `AGENTS.md`
- `.agents/repo-guidance.md`
- `.agents/decisions.md`
- `.agents/plans/security-layer.md`
- `.agents/plans/rtk-rewrite-routing.md`
- `.agents/plans/audited-harness-sessions.md`
- `.agents/plans/mcp-resilience.md`
- `.agents/plans/production-reliability-salvage.md`
- `.agents/plans/release-distribution.md`
- `.agents/plans/warm-runspace-mcp-server.md`
- `.agents/plans/shared-persistent-runspace.md`
- `.agents/plans/mini-siem-discovery.md`
- `.agents/plans/mini-siem-implementation.md`
- `.agents/review/index.md`
- `.agents/machines.md`

## Unrecorded Repo Memory

- None known.

History: rotated entries live verbatim in `docs/history/state-archive.md`.
