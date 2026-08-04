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

**Nothing is queued.** The release plan is fully executed and the candidate
is proved. Decision 5 — tag `v0.2.0` and publish — is the only remaining
gate, and it is owner-only. Do not tag, publish, or push a `v*` ref without
an explicit go.

Unqueued work that exists if the owner wants it, in no particular order:

- A Kimi harness leg (none exists).
- A POSIX bootstrap so macOS/Linux can install without `pwsh` already
  present.
- Narrowing the install-time smoke test. It is a full product handshake run
  twice per install, opening worker sessions and writing under `~/.ptk`; a
  failure of the second one rolls back an otherwise-good install, so a flake
  reverts a working installation. Initialize plus `tools/list` would catch a
  broken payload without that exposure.
- One live defect seen during this session and not investigated:
  `Get-Process dotnet | Where-Object { $_.CommandLine -match 'testhost' }`
  was refused with `Trusted pre-execution isolation failed; the script was
  NOT executed and the runspace was recycled` — an ordinary read-only
  pipeline refused, and warm state lost, for a command that never ran.

**Release plan:** `.agents/plans/github-release-packaging.md`, which executes
Slice 7 of the router plan and supersedes the packaging mechanics of
`.agents/plans/release-distribution.md`.

**Decision 2 is RULED — five RIDs** (owner, 2026-08-03): `win-x64`,
`win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`. No `osx-x64`. GitHub
Actions covers the matrix; each RID builds on its own native runner because
`Assert-PtkNativeBuildRid` refuses cross-RID layout builds.

**Slice 7.1 is complete (2026-08-04): both Unix HIGH findings are repaired
and Unix packaging is unblocked.** Selecting Linux and macOS activated two
findings a Windows-only release would have dodged:

- `opr-15` at `cd00276` — identity observation is now tri-state (exact,
  confirmed-absent, indeterminate). Previously every query failure read as
  "process dead", so one transient `/proc` error could release a session
  alias while an escaped descendant still ran. Indeterminate now fails
  closed and retries, matching `ProcessGroupExists`.
- `opr-14` at `67d37dd` — `FD_CLOEXEC` is set with `ioctl(FIOCLEX)` instead
  of a fixed-signature P/Invoke to variadic `fcntl`, which is wrong on
  Apple arm64. Also atomic, closing the get/set inheritance window.
  **`FIOCLEX` differs per platform** (Darwin `0x20006601`, Linux `0x5451`,
  both verified against xnu and Linux uapi headers) — a shared constant
  would break Linux.

`opr-14`'s guards skip on Windows, so it was proved on branch
`ci/opr-14-cloexec`: all six CI jobs green including `macos-latest`, which
is Apple arm64 — the exact platform the ABI bug affects. Merged fast-forward
and the branch deleted. The rest of
`.agents/review/dispositions.md` §"deferred to platform selection" is
MEDIUM/LOW and does not block.

**Decision A RULED (owner, 2026-08-04): win-arm64 ships with the emulated
x64 rtk.** rtk v0.44.2 publishes only `rtk-x86_64-pc-windows-msvc.zip`, so
the RID runs it under Windows ARM64 x64 emulation. CI must prove the
emulated rtk answers `hook check`, not merely `--version`; the installer
must fail at install time if it does not.

**Decision B RULED (owner, 2026-08-04): Apache-2.0.** `LICENSE` now exists
at the repo root; it was previously absent. Slice 7.2 packages it into every
artifact.

**Slice 7.0 landed at `bf1fc0b` (owner: "yeah fix it").** The object shaper
recognized six types and returned `[active member not evaluated]` for every
other one — `Get-Culture` came back with no data on a host with no Outlook
and no Exchange. GitHub #8 framed this as an Outlook/COM problem; it was not,
and Decision 3 is **withdrawn as mis-scoped** rather than ruled. Types from
trusted assemblies (the framework directory plus the two PowerShell
assemblies) now render via `ToString()`; dynamic and location-less
assemblies — what `Add-Type` produces — are never trusted, so both
no-live-getter guards pass unedited. The exception-message gate is widened
the same way (#8's secondary complaint). Rendering is capped at 2048 chars
and marked `passive_projection_lossy`, not `active_member_not_evaluated`.
Do not reopen this as a COM-getter question: executing active getters at
shaping time stays rejected.

**Routing is not defective — a suspected bug was investigated and closed
(2026-08-04).** `git ... 2>&1` appeared to fall back to PowerShell. It does
not: RTK declines `git --version` (nothing to compress) with or without a
redirection, and rewrites `git status --short` either way. The regression
that would catch a real redirection gate is pinned at `24eff27`. When
reproducing shaper behavior, use a bare cmdlet — a native command PTK runs
directly because the script was prefixed with a cmdlet is a property of the
call, not of the shaper.

**Decisions C and D RULED (owner, 2026-08-04):** release version `v0.2.0`;
RTK reaches users by fetch-on-install (installer downloads the matching rtk,
checksum-verified against rtk's own `checksums.txt`, into `~/.ptk/bin`; an
rtk already on PATH is used as-is and never touched; a marker file records
what the installer placed so uninstall removes only that).

**Every slice of the release plan is executed.** 7.2 version/licence
packaging (`5b19260`, `fb6d951`), 7.3 `.github/workflows/release.yml`
(`ecc5df4`), 7.4 installers (`141793d`, `eec2ccd`, since consolidated), 7.5
`server/direct-product-proof.ps1` (`db5601c`).

**One installer: `scripts/install.ps1` (`3109ec1`, 2026-08-04).** Three
existed. The root `install.ps1`/`install.sh` registered claude only and never
called `ptk_init.ps1`, so anyone installing from a release never got the
codex, grok, or agy legs — those legs are written and live-verified, and ran
only from the dev script. Merged into the dev script, which already had the
transaction, rollback, ARP entry, harness init, and uninstall; it gained
`-FromRelease` (download the asset, verify against `SHA256SUMS`), rtk
resolution before registration, and `-Purge`. Root scripts deleted, net −491
lines. Modes: `-FromRelease`, bare (build checkout), `-Uninstall [-Purge]`,
`-LayoutOnly -OutputDir` (release CI). Verified at that commit: server
1,068/1,068, Pester 84 with 1 platform skip, layout build correct.

Consequence to keep in view: **macOS and Linux now need `pwsh` to install**,
because `install.sh` is gone. The installed payload still embeds its own
PowerShell and does not need one. No POSIX bootstrap was written — deliberate
under the owner's one-installer instruction, not an oversight.

**Kimi has no harness leg.** `ptk_init.ps1` covers claude, codex, grok, and
agy. Kimi is absent entirely; adding one needs its config location,
registration format, and tool-name prefix.

**Fixture sessions renamed `exchange-*` → `sample-*` (`e2d31db`).** The
install-time smoke test printed `named worker topology ok: exchange-online
pid=..., exchange-onprem pid=...` — two processes named after an admin's mail
infrastructure, during a tool install. Nothing Exchange-related ran; the
names were flavour resembling ptk's target workflows. The installer now also
announces the smoke test before running it (local workers, no network).

**Agent test plan: `docs/testplan.md` (`c73b434`).** ~70 numbered stress
tests an agent runs against a ptk it is already connected to, filing one
GitHub issue. Written for a cold reader on any machine: no install, no
checkout, no session context, no chat residue. It is a document only — **do
not run it and do not spawn agents against it** without an explicit owner go.
Earlier drafts that put this in `.github/workflows/` and a `.agents/` plan
were removed as wrong-shaped.

**A `v0.2.0-rc.2` draft release exists and is proved** (run `30940515893`,
head `fb6d951`): five RIDs each built, handshake-smoked, and RTK-gate-proved
on their own native runner; 16/16 direct product checks against the
installed win-x64 candidate; Defender scan clean with the payload intact.
The emulated x64 rtk on `windows-11-arm` answered `hook check`, so Decision
A is proved rather than assumed. Full evidence is in the plan's status
table — do not restate it here.

**The only thing left is Decision 5: tag and publish.** That is terminal and
owner-only. CI assembles drafts; nothing tags a `v*` ref or publishes
without an explicit separate go.

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
