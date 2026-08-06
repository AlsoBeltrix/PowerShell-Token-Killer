# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- **RTK is a required dependency (owner, 2026-08-03):** "rtk was never optional. rtk was always stated requirement when I asked for this." PTK is a compression router; the thing it routes to is not optional. A missing RTK is a startup error (exit 78) with an actionable message, never a silent degraded mode. Do not build a without-RTK product tier, capability matrix, or per-call degradation reporting. CI installs rtk on all three platforms.

- **Routing authority: RTK decides (owner, 2026-08-03).** PTK submits the exact submitted text to `rtk hook check --agent ptk`; a rewrite executes, a decline runs the original unchanged. PTK no longer judges eligibility from the PowerShell AST and no longer resolves executables against PATH. Three guards protect the boundary, each mutation-proved: the accepted rewrite binds the startup-pinned executable (a bare `rtk` head would resolve through PATH at run time and could execute a different binary than the one PTK hashed); a rewrite must reduce to the submitted text once `rtk ` prefixes are stripped; and every wrapped name must bind to an `Application` in the session.

- **The agent session is a clean PowerShell 7 (owner, 2026-08-03).** `PSModuleAutoloadingPreference = None` in the initial session state. `PSModulePath` is retained so AD/Exchange stay discoverable and explicit `Import-Module` is unaffected. Previously, referencing any name a user module exported autoloaded that whole module — one reference pulled in 44 commands — and because autoload is lazy, two sessions on one machine diverged by whatever was invoked first. Scope correction on the record: an autoloaded function does *not* override a built-in alias, so the earlier claim that autoload rebinds `ls` was wrong.

- **PTK infers no shell (2026-08-03).** Automatic Bash detection, refusal, validation, and delegation are deleted. Bash reaches RTK the way every other native command does: the user writes `bash -lc '...'`. The `ptk_invoke` description states the dialect is PowerShell 7 and names that escape hatch.

- **Every `opr-*` finding is dispositioned (2026-08-03).** `.agents/review/dispositions.md` is the disposition of record and owns the per-bucket counts; it reports zero open blockers. Do not restate its buckets here. The "accepted and plan-gated" state is retired — it let a finding be recorded without ever being resolved. A defect found during implementation is now fixed in its slice or dispositioned.

- **Known gap, `opr-20`:** its fail-closed half is guarded; its pre-write half is argued from the code path and unguarded. No available test seam reaches the pre-write window (each client owns its writer, the operation lease observes cancellation before the try block, and the stream seam sits downstream of the first-write callback). A vacuous guard was removed rather than shipped.

- **Two non-blocking findings are worth a look before release** — named and explained at the end of `.agents/review/dispositions.md` §"remaining, not blocking", which owns that call. Both are cheap and outside the router plan's scope. Both were **re-confirmed live at `c5a0bb2`** on 2026-08-05 and are planned in `.agents/plans/pre-release-opr-10-opr-53.md`: the timeout predicate accepts `1e400`, `1.5`, `0.5` and `86401` where all four should fall back, and a script that prints PTK-shaped lines has its forged `[ptk worker] status=` and `recovery=` handle preserved verbatim beside the genuine ones. `opr-53` carries an owner decision (escape the grammar, or move control information to structured content) and cannot be implemented until it is ruled.

## Next

**Goal in force (owner, 2026-08-05): finish the app to a 1.0-ready state.**
Review significant code changes with codex (default settings), work GH issues
periodically. Under it, #13 was closed and #42 filed and planned.

**Landed under the goal, 2026-08-05.** #13 closed (round-3 verdict
`accepted`). #42 filed, planned, and its code slices 1, 2, 3, 3b landed with
guards, plus both codex findings (i42-1 HIGH, i42-2 LOW) fixed. `opr-10`
fixed and guard-proved. Battery at `eca0891`: server 1,164/1,164, Pester 111
+ 1 skip, dependency audit clean, handshake passed, direct product proof
17/17 against a complete payload.

**#42 is CLOSED (2026-08-05).** Slice 4 did not need this host's install
repaired: an install into an isolated `HOME` (`USERPROFILE`/`HOME`/`HOMEPATH`
set on the child process, since `$HOME` itself is read-only) exercises the
real transaction, activation, validation, and registration without touching
`~/.ptk`. `Assert-PtkRuntimeNotRunning` filters by path prefix, so it scopes
to the sandbox and does not see the live servers. **Use this technique for
any future install-path work** — it is the difference between simulating the
defect and reproducing it.

**Two durable lessons from this issue, both earned the hard way:**

- **`Copy-Item -Recurse` and `Move-Item -Force` both put a directory *inside*
  an existing same-named directory.** Neither is a safe way to replace a
  directory. Only an explicit recursive merge, or a rename onto a
  proven-absent path, is. This single mistake produced the original bug and
  then reappeared inside two successive fixes for it (i42-1, i42b-1).
- **A guard for a merge must supply a destination that already contains a
  same-named child.** The first guard written for i42b-1 passed against the
  broken code, because driving the merge through activation meets an empty
  destination. Test the helper the way the failing caller calls it.
- **File-lock behaviour is Windows-only.** POSIX renames and unlinks open
  files happily, so a test that asserts a throw under an open handle fails on
  ubuntu/macOS. CI caught two such tests that passed locally; they are now
  `-Skip:(-not $IsWindows)`, with the platform-independent half split out so
  it still runs everywhere.

Doing so found three further defects the unit tests could not see, all fixed
in `1ff20c8`: activation deleted in place (destroying the payload before
discovering a lock), the undo used `Move-Item -Force` (which nests a
directory into a same-named directory, the very defect being fixed), and
rollback aborted before restoring because its own delete threw. Proof:
baseline 387 files, locked reinstall, still 387, no `bin/bin`, rescued
install passes the release gate 17/17. Before that commit the same run left
208.

**This host's own `~/.ptk` is still nested** and still runs the stale
111-file payload. It is not a code problem any more — the installer now
refuses to install over it and says how to repair. Repair means closing every
ptk process (including the server the working agent runs through) and
reinstalling: owner action.

**What remains:**

1. **`opr-53` — needs an owner ruling before implementation.** The finding is
   re-confirmed live (a script that prints PTK-shaped lines has its forged
   `[ptk worker] status=` and `recovery=` handle preserved verbatim beside
   the genuine ones). The choice is (a) escape the reserved grammar inside
   the text channel — smallest change, but mutates legitimate user output —
   or (b) return supervisor control information as structured content —
   larger, touches the tool surface, and also fixes the deferred
   refusal→`isError` mapping. Recommendation: (b). Plan:
   `.agents/plans/pre-release-opr-10-opr-53.md`.

   Not taken unilaterally, for two recorded reasons rather than caution:
   `.agents/review/dispositions.md` dispositions it **not release-blocking**,
   and option (b) changes all five tools from `Task<string>` to a structured
   return — a design fork with user-visible consequences either way, which
   the plan explicitly gates on the owner.

**Working the test-report backlog** (owner, 2026-08-05): fix the reported
issues, one codex review per completed fix, maximum two rounds. The backlog
itself is closed — every issue it raised is fixed and closed (#33–#38, #41)
or filed and blocked on matching hardware (#40); the landed detail is
rotated to `docs/history/state-archive.md`. The review cadence it set still
governs the loops that follow it.

**Reviewer dispatch is fixed on this host.** codex is API-only via Portkey;
there is no `codex login`. Dispatch with
`codex exec --cd <repo> -s read-only --color never -c 'mcp_servers={}' - < <prompt>`
— without the MCP override, `codex exec` auto-denies its own configured ptk
tools and the reviewer burns its budget retrying them. The recurring
`refresh token was revoked` log line is noise.

**Known follow-up, deliberately deferred:** the refusal → `isError` mapping
reads the response text, because the tools return `Task<string>` and the
structured `InvokeDisposition` is flattened before the filter sees it. Codex
round 2 argued for carrying the outcome as data instead, and it is right —
text matching cannot be made airtight, only well-pinned. Every marker the
matcher accepts is covered by a test, including the false-positive shapes.
Threading a structured result through the tool surface is the real fix and
is a larger change than this batch.

### The queue

Open work, ordered. An open issue is queued work: "backlog complete" is not
the same as "nothing queued", and this section exists so the two are never
conflated again.

1. **#42 — CLOSED 2026-08-05.** Every slice landed and the issue carries the
   close-out. Commits: `8801281`, `b0573ee`, `210f865`, `8a1bf2a`, the two
   codex findings `044d53b` and `7761b75`, and `1ff20c8` for the three
   defects only a real locked install exposed. The detail is above and in the
   issue; do not restate it here. Original filing: filed
   2026-08-05 from this host's own broken install. `Move-Item` of a directory
   onto a surviving directory nests instead of replacing
   (`scripts/ptk_install_transaction.psm1:279`), so a failed or raced
   `Remove-PtkInstallPath` silently produces `bin/bin/` and leaves the old
   payload registered. Here that is 111 files against a correct publish's 296
   — 185 missing, including `System.Collections.NonGeneric.dll` and
   `Microsoft.PowerShell.SDK.dll`. `Assert-PtkPayloadIntact`
   (`scripts/install.ps1:199`) name-checks five paths, all present in the
   stale payload, so it passes; the package smoke test passes too, because
   the server starts and handshakes fine. The issue owns the detail and the
   three-part repair; do not restate it here.

   This is the root cause of the previously uninvestigated live defect: an
   ordinary read-only `Get-Service`/`Get-Process` is refused with "Trusted
   pre-execution isolation failed" **and the warm runspace is recycled**,
   because ETS member access throws `FileNotFoundException` for the missing
   assembly. `ConvertFrom-Json` is broken in the same worker for the same
   reason. Release-blocking for 1.0: an install can look healthy and then
   destroy warm session state on a read-only command.

2. **#13 — worker death diagnostics — CLOSED 2026-08-05.** Slice 4 executed:
   the issue carries the close-out comment and the design record (nothing on
   the worker-death path is forgery-proof, so PTK asserts only the
   unrequested exit and labels the exit code and stderr tail untrusted).
   Round-3 verification over `4f9284f..e2c2902` returned `accepted` /
   `guard_confirmed: true`, SHAs matched; `.agents/review/index.md` owns the
   verdict and the one accepted residual risk. Historical detail below is
   retained until the next rotation.

   The
   owner's 2026-08-04 comment closed the speculative-fix path and named what
   the next recurrence needs: the worker stderr diagnostic, or the crash
   event/exit code. PTK currently discards both, so a recurrence is
   structurally guaranteed to dead-end again. Evidence: worker stdout and
   stderr are drained into a discard loop
   (`server/PtkMcpServer/Worker/SessionWorkerClient.cs:311`), and
   `IWorkerContainedProcess`
   (`server/PtkMcpServer/Worker/WorkerProcessAuthority.cs:29`) exposes no exit
   code — while the worker already writes a bounded
   `ptk_worker_exit kind=… detail=…` line and a distinct exit code per failure
   class (`server/PtkMcpServer/Worker/WorkerProcessExit.cs:13`). The work is
   to retain that last bounded stderr line plus the exit code and surface them
   on the `outcome_unknown` path
   (`server/PtkMcpServer/Sessions/WorkerSupervisor.cs:314`) instead of
   dropping them. Plan:
   `.agents/plans/issue-13-worker-death-diagnostics.md` (the plan owns the
   detail, do not restate it here). Slices 1-3 landed; codex found three
   defects, all fixed, and its verification pass reopened two of the fixes,
   both repaired at `e2c2902` — still the last code commit as of `78b2dbb`;
   everything after it is docs and `.gitignore`.

   **Reviewer dispatch on this host — settled 2026-08-05.** The two dead
   round-2 dispatches were not a transport problem and not the work. codex's
   `~/.codex/config.toml` registers the `ptk` MCP server, and under
   `codex exec` (non-interactive, `approval: never`) every `ptk_invoke` /
   `ptk_session` call is auto-denied with `user cancelled MCP tool call`;
   the reviewer burned its whole budget retrying a tool it could never call.
   Adding `-c 'mcp_servers={}'` fixed it — round 3 returned a verdict in
   168s on 51k tokens. codex here is API-only through Portkey; there is no
   `codex login` to run, and the `refresh token was revoked` line it logs
   continuously is confirmed noise (a `pong` probe answered in ~3s while
   logging it). The dispatch recipe lives in `.agents/review/index.md`.

   **Durable lesson from this loop:** on the worker-death path nothing is
   forgery-proof, because the caller's script runs *inside* the worker — it
   controls both the worker's standard error and its exit code. PTK
   therefore asserts only that the process exited unrequested, and presents
   the exit code and retained stderr line as explicitly untrusted evidence.
   Do not reintroduce a per-kind classification here without a
   supervisor-owned authenticated channel; two successive attempts to
   classify (from text, then from the exit code) were both forgeable.
2. **#7 — Defender false positive.** Gated on Microsoft's WDSI verdict
   (submitted 2026-07-20). No local action until it lands.
3. **#40 — macOS long-pipeline worker loss; Windows ARM64 MSIX module
   imports.** Gated on matching hardware; neither reproduces here.
4. **#30 — on-prem Windows remoting acceptance.** Owner-gated by the issue's
   own terms: it does not authorize a probe, and needs the owner to name host,
   auth path, identity, command surface, and cleanup authority first.
5. **Decision 5 — tag `v0.2.0` and publish.** Terminal and owner-only.
6. Unqueued candidates below (POSIX bootstrap, smoke-test narrowing, the
   refused read-only pipeline) are not on this list until the owner puts them
   there.

Review dispatch remains owner-gated throughout and happens only on the
owner's explicit word.

Unqueued candidates — not on the queue above until the owner puts them there,
in no particular order:

- A POSIX bootstrap so macOS/Linux can install without `pwsh` already
  present.
- Narrowing the install-time smoke test. It is a full product handshake run
  twice per install, opening worker sessions and writing under `~/.ptk`; a
  failure of the second one rolls back an otherwise-good install, so a flake
  reverts a working installation. Initialize plus `tools/list` would catch a
  broken payload without that exposure.
- ~~One live defect seen during this session and not investigated~~ —
  investigated and root-caused 2026-08-05; it is **#42**, the nested-payload
  install, not a router or classifier bug. See queue item 1. The refusal
  reproduces on any command whose objects need a trimmed-away assembly
  (`Get-Process`, `Get-Service`), so it will disappear on a repaired install.

**Release plan:** `.agents/plans/github-release-packaging.md`, which executes
Slice 7 of the router plan and supersedes the packaging mechanics of
`.agents/plans/release-distribution.md`. The release-packaging plan owns the
ruled decisions (2, and A–D), the executed slice status, and the proved
`v0.2.0-rc.2` draft release; do not restate them here. Every slice of it is
executed. Decision 5 — tag
and publish — is terminal, owner-only, and the last thing left *in that
plan*; the queue above, not this line, is the repo's open work. Do not tag or
push a `v*` ref without an explicit go.

Landed release-slice detail (Slices 7.0–7.5, the one-installer
consolidation, per-harness consent, the kimi leg, the fixture rename, and
the closed routing investigation) is rotated verbatim to
`docs/history/state-archive.md`.

**Agent test plan: `docs/testplan.md` (`c73b434`).** ~70 numbered stress
tests an agent runs against a ptk it is already connected to, filing one
GitHub issue. Written for a cold reader on any machine: no install, no
checkout, no session context, no chat residue. It is a document only — **do
not run it and do not spawn agents against it** without an explicit owner go.
Earlier drafts that put this in `.github/workflows/` and a `.agents/` plan
were removed as wrong-shaped.

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
  `parallelizeTestCollections: false`, re-confirmed present as of `78b2dbb`)
  without changing explicit concurrency tests, product deadlines, or
  assertions. This closes the scheduling artifact, not every underlying risk. A
  recurrence of the anchored-evidence publication/removal ordering race or
  `JobManager.Dispose` bounded-observer failure in a serialized run is still a
  real signal, and fixed watchdog sensitivity remains.

  The "hosted-CI evidence is absent" half of this item is falsified, and
  stays falsified as of `78b2dbb`: all six CI jobs are green on
  ubuntu/windows/macos. Suite counts
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

- `.agents/plans/github-release-packaging.md` (DRAFT; executes router Slice 7
  behind Decisions A-D, 3, and 5) — supersedes the packaging mechanics of
  `.agents/plans/release-distribution.md` slices 3, 4, 6, 7
- `.agents/plans/rtk-router-delegation.md` (APPROVED; Slices 0-6 executed,
  Slice 7 handed to the release-packaging plan) — supersedes the
  minimum-viable-release plan
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
