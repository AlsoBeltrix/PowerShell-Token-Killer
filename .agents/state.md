# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- **RTK router delegation plan is executed through Slice 6 (2026-08-03).** Plan: `.agents/plans/rtk-router-delegation.md`. Slices 0-6 landed; Slice 7 (version, package, direct proof) is unstarted and gated on Decisions 2-5. Per-slice commit table is in the plan's status block.

  Re-verified as of `a3112f3` (docs-only over code head `f637ad0`) on `ASHBIAMWEB1`: the whole battery passes. Counts live in `.agents/repo-guidance.md` §Verification, refreshed in the same pass; they moved during this work because ~6,500 lines and their tests were deleted. Local SIEM is 226/247 on this host only — the 21 pre-existing symlink-privilege cases recorded in `.agents/machines.md`, not a product failure.

  **Hosted CI is green at `a3112f3`** — all six jobs, and the RTK install step succeeded on ubuntu, windows, and macos, so the previously unexercised install path is now proven. SIEM is 247/247 in CI, where symlink creation is permitted.

- **RTK is a required dependency (owner, 2026-08-03):** "rtk was never optional. rtk was always stated requirement when I asked for this." PTK is a compression router; the thing it routes to is not optional. A missing RTK is a startup error (exit 78) with an actionable message, never a silent degraded mode. Do not build a without-RTK product tier, capability matrix, or per-call degradation reporting. CI installs rtk on all three platforms.

- **Routing authority: RTK decides (owner, 2026-08-03).** PTK submits the exact submitted text to `rtk hook check --agent ptk`; a rewrite executes, a decline runs the original unchanged. PTK no longer judges eligibility from the PowerShell AST and no longer resolves executables against PATH. Three guards protect the boundary, each mutation-proved: the accepted rewrite binds the startup-pinned executable (a bare `rtk` head would resolve through PATH at run time and could execute a different binary than the one PTK hashed); a rewrite must reduce to the submitted text once `rtk ` prefixes are stripped; and every wrapped name must bind to an `Application` in the session.

- **The agent session is a clean PowerShell 7 (owner, 2026-08-03).** `PSModuleAutoloadingPreference = None` in the initial session state. `PSModulePath` is retained so AD/Exchange stay discoverable and explicit `Import-Module` is unaffected. Previously, referencing any name a user module exported autoloaded that whole module — one reference pulled in 44 commands — and because autoload is lazy, two sessions on one machine diverged by whatever was invoked first. Scope correction on the record: an autoloaded function does *not* override a built-in alias, so the earlier claim that autoload rebinds `ls` was wrong.

- **PTK infers no shell (2026-08-03).** Automatic Bash detection, refusal, validation, and delegation are deleted. Bash reaches RTK the way every other native command does: the user writes `bash -lc '...'`. The `ptk_invoke` description states the dialect is PowerShell 7 and names that escape hatch.

- **Every `opr-*` finding is dispositioned (2026-08-03).** `.agents/review/dispositions.md` is the disposition of record and owns the per-bucket counts; it reports zero open blockers. Do not restate its buckets here. The "accepted and plan-gated" state is retired — it let a finding be recorded without ever being resolved. A defect found during implementation is now fixed in its slice or dispositioned.

- **Known gap, `opr-20`:** its fail-closed half is guarded; its pre-write half is argued from the code path and unguarded. No available test seam reaches the pre-write window (each client owns its writer, the operation lease observes cancellation before the try block, and the stream seam sits downstream of the first-write callback). A vacuous guard was removed rather than shipped.

- **Two non-blocking findings are worth a look before release** — named and explained at the end of `.agents/review/dispositions.md` §"remaining, not blocking", which owns that call. Both are cheap and outside the router plan's scope.

## Next

**Slice 7 — version, package, direct proof.** Gated on owner Decisions 2-5:
supported platforms (Decision 2), Outlook/COM boundary (3), release version
(4), publish (5). Decision 2 also decides which platform findings matter —
see `.agents/review/dispositions.md` §"deferred to platform selection". A
Windows-only first release is blocked by none of them.

Packaging must also resolve how RTK reaches the user: bundled, or a
prerequisite the installer checks for and refuses to proceed without. Do not
ship an installer that completes onto a machine with no RTK.

**Process constraints stay in force** (`.agents/plans/rtk-router-delegation.md`
§Process constraints): no reviewer invocation without a separate explicit
owner approval naming it; no file-by-file source review; fix-or-dispose, never
record a new gated finding.

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
  unattended certificate auth. Its plan status was corrected 2026-08-03.
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

- **The invalid same-testhost cross-collection contention is repaired, but its
  residual signals remain open.** Before Slice 1a, six default-parallel runs
  exposed five intermittent fixed-watchdog/PATH failures while a serialized
  control passed. Slice 1a disables default collection parallelism
  (`server/PtkMcpServer.Tests/xunit.runner.json`,
  `parallelizeTestCollections: false`, re-confirmed present as of `a3112f3`)
  without changing explicit concurrency tests, product deadlines, or
  assertions. This closes the scheduling artifact, not every underlying risk. A
  recurrence of the anchored-evidence publication/removal ordering race or
  `JobManager.Dispose` bounded-observer failure in a serialized run is still a
  real signal, and fixed watchdog sensitivity remains.

  The "hosted-CI evidence is absent" half of this item is falsified as of
  `a3112f3`: all six CI jobs are green on ubuntu/windows/macos. Suite counts
  belong to `.agents/repo-guidance.md` §Verification; the historical
  1,557-test figure this entry used to carry predates the router-delegation
  deletions and is dropped rather than refreshed. Later plan slices and the
  deployment gates still stand before any production-ready claim.
- **Direct ARM64 Linux build/execution validation needs a matching real host.**
  The prior UTM VM is not in use by owner direction. Cross-publishing from
  macOS is now correctly refused because it produced a Mach-O worker broker
  inside a `linux-arm64` layout. Do not claim this gate from cross-build output;
  run it on real ARM64 Linux when such a host is available. Historical UTM/
  `Grpc.Tools` evidence remains in `.agents/machines.md`, not as the current
  execution path.

- **Current Slice 10 generic Windows validation is pending — but no longer on
  `NETWATCH-01`'s return.** Basis corrected 2026-08-03: `ASHBIAMWEB1` ran the
  Windows leg on 2026-07-28 at head `7eaf8a0` — Windows x64 runtime/package,
  Job Object, and 100-cycle packaged production acceptance all passed
  (`.agents/machines.md` §`ASHBIAMWEB1`). What remains open is only that this
  is not the current head; rerun Windows packaging/process/Job Object/timeout/
  crash/cleanup acceptance at the head being shipped. That host's ordinary
  token still cannot close the SIEM symlink-protection cases.
- **The real AD/Exchange/EXO/Outlook workflow gate is partly closed; on-prem
  Exchange and Outlook remain open.** Basis corrected 2026-08-03. `ASHBIAMWEB1`
  is domain-joined to `ad.analog.com` and closed the EXO leg on 2026-07-29:
  app-only auth, an identity-bound `Get-EXOMailbox` read, warm-reuse latency,
  and retained selected properties (`.agents/machines.md` §Enterprise field
  validation). Still unclosed there: on-prem Exchange (no `ExchangeInstallPath`,
  no `RemoteExchange.ps1`, no `Get-Queue`), Graph (absent credential file, no
  parent window for WAM, device code expired), and Outlook (COM initializes
  after the STA fix but the namespace exposes no current user). The earlier
  `NETWATCH-01`-is-a-gaming-machine framing is retired — that host was never
  the only candidate.
- **Decision-log conflict, correction blocked by the owner hold:**
  `.agents/decisions.md` still describes the policy-file gate as the open
  response after its criterion fires, while the later explicit owner call in
  `.agents/plans/security-layer.md` rejects that response. Its shared-host
  entry stages durable GUID sessions followed by sharing, while the owner's
  later direction removes both from the candidate build. Do not implement
  either stale direction. Its mini-SIEM entry's **Current evidence** paragraph
  (the one citing `server/AUDIT-EXPORT.md` and the acknowledged OTLP export)
  also describes producer behavior that the corrected plan removes; treat that
  as known stale evidence after audit decision 4, never as authority to restore
  the producer. Preserve these decision-log conflicts until the hold is
  released. (A prior "line 312" pointer here was stale — line numbers drift;
  the anchor above does not.)
- **GitHub #7 closure is gated on Microsoft's WDSI verdict** on the submitted
  `PtkMcpServer.dll` (owner-submitted 2026-07-20). Interim quarantine-detection
  mitigation is landed (`51ce880`); no further local action on #7 until the
  verdict lands.
- **rbc-5 stays open, gated on resilience R7** landing its creation-time worker
  containment plus the Windows hard-supervisor-death background-descendant
  guard, with proof (`.agents/review/findings/rbc-5.md`). It is not gated on any
  local branch.

## Verification

- Automated verification entry point: `.agents/repo-guidance.md`
  (Verification). Review-loop evidence lives in `.agents/review/index.md`;
  do not duplicate volatile counts here.
- Audited-session Slices 0-6, Slices 7a-7h, and the Windows wait-ownership
  prerequisite are complete locally. Canonical fixed-head acceptance evidence
  lives in `.agents/review/index.md`; host-specific verification records live
  in `.agents/machines.md`.

## Active Sources

- `.agents/plans/rtk-router-delegation.md` (APPROVED; Slices 0-6 executed,
  Slice 7 gated on Decisions 2-5) — supersedes the minimum-viable-release plan
- `.agents/plans/minimum-viable-release.md` (superseded; its release-blocking
  rule, non-goals, and packaging/proof slices are retained by reference from
  the router-delegation plan)
- `.agents/review/dispositions.md` (disposition of record for every `opr-*`)

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
