# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

**o53-3 (HIGH, 2026-08-07): the opr-53 structured verdict hid all output in
Claude Code.** Field-reported ("returns output only via handles now") and
reproduced firsthand: CC renders `structuredContent` INSTEAD of text, so
every completed `ptk_invoke` showed only the verdict JSON — no output, no
recovery handles. Fixed same day under the owner's explicitly delegated
design call ("This is consumed by agents. You're an agent. What do YOU
need?"): completed responses are bare text again and absence is the
completed verdict; non-completed responses keep the structured verdict plus
a full `text` mirror; the call filter's text matcher is gated to
`ptk_output` by tool identity so worker text cannot forge `isError` back.
Guard-proved by two sabotages; finding record
`.agents/review/findings/o53-3.md`. **This host's `~/.ptk` was repaired by
the owner the same day** (killed all ptk processes, reinstalled) — the old
nested-payload caveat is history. **CONFIRMED live 2026-08-07 after the
owner reinstalled the fixed build and reconnected:** completed calls show
full output and recovery handles again; a refusal renders as a flagged
error with its complete reason. The rc.3 draft predates this fix — a fresh
draft (or the tag itself, which builds from its own commit) picks it up.

**Handoff 2026-08-06, head `46f8254`. Owner ruled the next release is
`v0.2.0` (not 1.0).**

In flight: **one owner decision, nothing half-done.** The tree is clean, all
work is committed and pushed, CI was green on all six jobs at `7cd4972`.

**rc.3 is GREEN: the re-run (`31184671731`, head `9e1790e`) passed all
five RIDs and assembled the `v0.2.0-rc.3` draft.** Slice 7.5 is closed on
every leg; the per-RID table in
`.agents/plans/github-release-packaging.md` owns the record. The first
dispatch (`31184268679`, head `587b6e5`) proved the gate real: both
Windows legs passed — including the hardened Defender scan-completes
check, so hosted runners can scan — and the three POSIX legs correctly
blocked the draft over the proof's check 13, which asserted the
Windows-only `ls` alias unconditionally (POSIX ships none; a clean session
binds the native `/usr/bin/ls`). Check defect, not product defect; fixed
platform-conditionally at `ccd6d1d`, proved locally against both observed
shapes. An earlier dispatch mistakenly landed on the `roethlar` fork
because `gh` defaults to the `github` remote — run canceled, `-R` rule
recorded in repo-guidance §Remotes.

**Decision 5 is EXECUTED IN FULL. PTK v0.2.0 was PUBLISHED 2026-08-07
16:35 UTC on the owner's explicit two-step go ("ship" → tag; "go" →
publish):**
https://github.com/AlsoBeltrix/PowerShell-Token-Killer/releases/tag/v0.2.0
— tagged at `3a9cbeb`, tag run `31197966252` green on all five RIDs, five
artifacts + SHA256SUMS, marked Latest. The stale `v0.2.0-rc.2`/`rc.3`
drafts were deleted on the same go. First post-release slice, queued but
not started: macOS Developer ID signing + notarization (the owner now has
an Apple developer account; blocked on their cert + CI secrets, then the
osx-arm64 release job gains the signing step — recommended, not required
for function: script installs never quarantine, and .NET ad-hoc signing
already satisfies Apple Silicon). #43 is CLOSED 2026-08-07 on the owner's
verification: the install completes on the reporting Server 2019 host
from master at head.**

**#43 (filed by the owner 2026-08-07, verified against the code the same
day): install fails on Windows Server 2019 because the handshake's
`Assert-LiveOutputRoot` counts a delete-pending artifact name.** Classic
NTFS keeps a deleted-but-open name enumerable; the product primitive
documents exactly that (`SecureAuditStorage.cs:176-179`) and gates its own
namespace check off Windows, but the test helper asserts raw counts
(`server/test-handshake.ps1:185`, used at `:601`/`:728`/`:822`). A
test-gate portability defect, not a product regression — the issue itself
proves containment holds on that host. Secondary: the installer threw away
the handshake's failing line. **Both halves FIXED 2026-08-07 under the
owner's blanket fix authorization** (recorded in repo-guidance §Earned
Practices): `4de1c89` (the live-root assertion probes artifact names on
Windows — access-denied means delete-pending and is excluded, anything
else still counts; POSIX and all post-exit checks unchanged) and `b811f05`
(the installer tees the handshake and carries its last 25 lines in the
thrown error). Proved here: probe classification for all three outcome
classes, both scripts parse, tee preserves the exit code, and the full
handshake passes on macOS. **The issue stays open pending the one proof
this repo cannot run: an install re-run on the reporting Server 2019 host
(owner action) — classic delete-pending semantics exist nowhere else we
can reach** (CI's `windows-latest` is Server 2022, POSIX-delete default).
Trap: the `v0.2.0-rc.3` draft was assembled at `9e1790e`, before these
fixes — a `-FromRelease` install of rc.3 on that host still carries the
old gate and will still fail. Re-test from the repo at head (or cut a
fresh rc if a release-artifact test is wanted there). The eventual
`v0.2.0` tag builds from its own commit, so tagging at/after `a0a2517`
picks the fixes up automatically.
Round 2 (2026-08-07): the owner's re-run from head passed every
previously failing gate — first fix confirmed on real classic-delete
NTFS — then failed the hard-kill leg's OWN raw live artifact count,
a fourth site the first sweep missed. Fixed in `a0a2517`: the probe is
now one helper (`Test-LiveArtifactEntry`) used by both live-phase
counts; a full-script sweep shows no other artifact-name counts except
the two post-exit ones, raw on purpose. Still open pending one more
Server 2019 install run from `a0a2517`+.

**What closed this session (release-gate work, after `opr-53`):**

- Slice 7.5's three hand-run checks are automated into
  `server/direct-product-proof.ps1`: check 10 uninstall (opt-in
  `-UninstallHome`, refuses unless the home contains the server under proof),
  check 13 `ls` alias + `PSModuleAutoloadingPreference=None`, and the Windows
  Defender payload scan. All three guard-proved by sabotage.
- **win-x64 Slice 7.5 re-run PASSED 22/22** at `935b8b2` against a real
  install into an isolated home, plus the full battery.
- `release.yml` now runs the product proof on every packaged RID before
  archiving (`46f8254`), so a release cannot be drafted from an artifact that
  fails its own contract. **Untested until the workflow actually runs** —
  that is what the dispatch above would prove.
- Recorded that the plan's "all slices executed" was stale by ~50 commits;
  `.agents/plans/github-release-packaging.md` now carries a per-RID table.

**Do not tag or push a `v*` ref without an explicit go.** The owner named the
version, not the tag.

**Caution, this host:** an EICAR control file written during a #7 scan check
tripped Defender and left a "Threat blocked" entry in the owner's Protection
history on a domain-joined machine (2026-08-06 10:07). It was the standard
harmless AV test string, removed automatically, nothing else touched — but it
may draw a security review. **Never write scanner-triggering test files
here.** The release gate's Defender check deliberately writes no EICAR and
judges by payload survival instead.


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

**`opr-53` is FIXED (owner ruled option (b), 2026-08-06).** PTK's own
verdict now travels in the protocol's `structuredContent` (`disposition`,
`executed`, `safe_to_resubmit`, `detail`) and `isError`, which worker output
cannot reach; the response text is unchanged byte-for-byte. The four session
tools return `CallToolResult`; `ptk_output` still returns a string and keeps
the text matcher. This also closed the deferred refusal→`isError` follow-up.
Commits `11eafee`, `c40404a`, `18d76e8`. Two follow-on defects were found in
the same effort, one by self-review and one by codex — both were PTK stating
a *false* verdict in the new trusted channel, which is the same mistake as
the original finding one layer in. `.agents/review/index.md` §o53 owns that
lesson.

**Review loop r806 is CLOSED (2026-08-07): all five fixes landed,
battery-verified, and verification-accepted.** One commit per finding, each
sabotage-proved: r806-3 `adb9f7a`, r806-4 `49a5cc7`, r806-1 `024fa66`,
r806-2 `9d8aec7`, r806-5 `5862041`. Battery green at `5862041` (server
1,191/1,191, Pester 112+3 skip, SIEM 247/247, audit clean, handshake
passed). Verification was one owner-directed batch dispatch (frontier,
esc:T2) — all five accepted, guard_confirmed true;
`.agents/review/index.md` §r806 owns the record. The r806-4 fix has a
consequence for the rc: a hosted Windows runner that cannot complete a
Defender scan now fails the per-RID gate visibly (by design); adapting the
workflow or runner is an owner call if it fires.

Otherwise every open GitHub issue is gated on hardware (#40), the owner
(#30), or Microsoft (#7). Decision 5 — tag `v0.2.0` and publish — is
terminal and owner-only.

**Next action:** get the owner's go for the `workflow_dispatch`
(`0.2.0-rc.3`) described in `## Now`. If it passes, four of the five Slice
7.5 legs close and only the tag and publish remain. If it fails, the failure
is in the new per-RID gate step (`46f8254`) or in a platform the win-x64 leg
could not exercise — read that job's log first, not the product.

**Historical, for the record:**

1. **`opr-53` — needed an owner ruling before implementation.** The finding was
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
