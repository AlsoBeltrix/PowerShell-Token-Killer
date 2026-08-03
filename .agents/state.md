# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- **RTK router delegation plan is executed through Slice 6 (2026-08-03).** Plan: `.agents/plans/rtk-router-delegation.md`. Slices 0-6 landed and pushed; Slice 7 (version, package, direct proof) is unstarted and gated on Decisions 2-5. Verification at each slice: full server suite, Pester, stdio handshake.

- **Implementation reviewed (2026-08-03):** openreview codex over `87d03d8..076626f` returned `acceptable_with_changes` and endorsed the architecture unchanged, including the pinned-path binding. Three material changes and four findings, all adopted and fixed with mutation-proved guards: the startup RTK gate used `File.Exists` while the runtime pins via `TryCapture` (HIGH — a path passing the weaker check let the server start and then run native commands unfiltered); the module still exported the pre-Slice-2 `Resolve-PtcInvokeScript` AST rewriter with no caller and a manifest entry for a deleted function; rewrite acceptance normalized whitespace, so a rewrite altering text inside a quoted argument was accepted; and `server/README.md` documented deleted Bash and post-success behavior. Record: `.agents/review/openreview-rtk-router-codex-r2.md`.

- **RTK is a required dependency (owner, 2026-08-03):** "rtk was never optional. rtk was always stated requirement when I asked for this." PTK is a compression router; the thing it routes to is not optional. A missing RTK is a startup error (exit 78) with an actionable message, never a silent degraded mode. Do not build a without-RTK product tier, capability matrix, or per-call degradation reporting. CI installs rtk on all three platforms.

- **Routing authority: RTK decides (owner, 2026-08-03).** PTK submits the exact submitted text to `rtk hook check --agent ptk`; a rewrite executes, a decline runs the original unchanged. PTK no longer judges eligibility from the PowerShell AST and no longer resolves executables against PATH. Three guards protect the boundary, each mutation-proved: the accepted rewrite binds the startup-pinned executable (a bare `rtk` head would resolve through PATH at run time and could execute a different binary than the one PTK hashed); a rewrite must reduce to the submitted text once `rtk ` prefixes are stripped; and every wrapped name must bind to an `Application` in the session.

- **The agent session is a clean PowerShell 7 (owner, 2026-08-03).** `PSModuleAutoloadingPreference = None` in the initial session state. `PSModulePath` is retained so AD/Exchange stay discoverable and explicit `Import-Module` is unaffected. Previously, referencing any name a user module exported autoloaded that whole module — one reference pulled in 44 commands — and because autoload is lazy, two sessions on one machine diverged by whatever was invoked first. Scope correction on the record: an autoloaded function does *not* override a built-in alias, so the earlier claim that autoload rebinds `ls` was wrong.

- **PTK infers no shell (2026-08-03).** Automatic Bash detection, refusal, validation, and delegation are deleted. Bash reaches RTK the way every other native command does: the user writes `bash -lc '...'`. The `ptk_invoke` description states the dialect is PowerShell 7 and names that escape hatch.

- **Every `opr-*` finding is dispositioned (2026-08-03).** `.agents/review/dispositions.md` is the disposition of record for all 59: 5 fixed, 16 closed-removed (verified by symbol search, not assumed), 11 closed-out-of-scope behind disabled audit/SIEM, 12 deferred to platform selection, 9 remaining-not-blocking, 0 open blockers. The "accepted and plan-gated" state is retired — it let a finding be recorded without ever being resolved. A defect found during implementation is now fixed in its slice or dispositioned.

- **Known gap, `opr-20`:** its fail-closed half is guarded; its pre-write half is argued from the code path and unguarded. No available test seam reaches the pre-write window (each client owns its writer, the operation lease observes cancellation before the try block, and the stream seam sits downstream of the first-write callback). A vacuous guard was removed rather than shipped.

- **Two non-blocking findings worth a look before release:** `opr-10` terminates the supervisor before the MCP handshake on a malformed timeout value; `opr-53` lets worker output forge PTK-authored control lines. Both cheap, both outside this plan's scope.

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
