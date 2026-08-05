# State Archive

Rotated verbatim from `.agents/state.md` by the `handoff` and `drift`
operators; current state lives there. Entries below are historical record,
newest rotation first.

## Rotated 2026-08-05 (drift based on 78b2dbb)

### From `## Next`

Fixed: #37 (shadowed-name routing), the version gap, the elision/recovery
contradiction, the missing effective-route label, refusals arriving as MCP
success, the misapplied uncertainty rider, the passive object-shaping loss
(the headline complaint), the lossy-recovery overclaim, a hint for the
`1>&2` dialect trap, and the bounded-capture prefix claim (#35 F4).

**Object shaping (`b3604f1`, `72a864e`).** A trusted type collapsed to one
`ToString()`; `Get-Culture` returned `en-US` and nothing else. Scalar
properties of trusted types are now projected by name. Codex round 1 caught
a HIGH in that first cut and it was real, reproduced before fixing: the
getter was called *before* the value was judged, so `[Lazy[string]]` — which
is `System.Private.CoreLib`, hence trusted — ran the caller's factory
delegate during capture. Gating now happens on the property's **declared**
type and declaring container, never on what came back. `Task<T>.Result` is
the same shape and is denied for the same reason.

Guard rails worth keeping in mind for future work here: the no-user-code
invariant cannot be enforced after a getter runs, and an over-strict rule is
its own defect — rejecting all virtual getters removed `CultureInfo.Name`
and `DisplayName`, the exact values the fix exists to restore.

**Investigated and closed without a code change:** Linux `2>&1` ordering
(#36). Reproduced correct order on Windows; the reported inversion comes
from bash block-buffering stdout while stderr is unbuffered, before PTK sees
either byte. Recorded on the issue.

**Filed, not fixed — needs the matching hardware (#40):** macOS long-pipeline
worker loss (#35 F2, not reproducible on Linux) and Windows ARM64 MSIX module
imports denied inside workers (#34 F3).

**Every issue from the four platform test reports is now fixed, or filed with
the investigation that could not finish here.** Open issues carrying that
work:

- **#40** — macOS long-pipeline worker loss and Windows ARM64 MSIX module
  imports. Both need the matching hardware; neither reproduces here.
- **#41 fixed and closed (2026-08-05).** The recorded instrumentation probe
  settled it in one observation: `CopyPassiveInstanceNotes` reaches the
  nested note, and `TryRenderTrustedText` claims the hollow
  `System.Management.Automation.PSCustomObject` base object — trusted
  assembly, empty `ToString()` — returning `''` and discarding the value
  before any later logic runs. That short-circuit also explains why both
  earlier compose variants "stayed empty on a verified-current build": a
  compose branch placed after the trusted-text render is unreachable for
  this shape. The fix composes a nested custom object's exact
  `PSNoteProperty` members as `Name=value; Name=value` text **before** the
  trusted-text render, bounded to 3 object levels (which also terminates
  self-referencing graphs) and the existing 2048-character render cap. The
  composed string survives the shaping module end-to-end — the downstream
  fear recorded in the issue does not reproduce on a fresh build. Guards:
  the #41 repro (`MARKER` must surface) and the depth bound, both proved
  failing with the compose branch disabled.

Recurring lesson worth carrying: on this shaper, a stale build tree produces
convincing wrong answers. Several investigations here chased behaviour that
had already been fixed. Kill build-tree `PtkMcpServer` processes and rebuild
before trusting a live probe.

The test-report backlog is complete: every issue it raised is fixed and
closed (#33–#38, #41) or filed and blocked on matching hardware (#40). (#32,
the kimi leg, was closed 2026-08-05: it landed at `ad1665e` and the tracker
was out of sync. The close comment records one deviation from the issue's
design sketch — no `startupTimeoutMs` is written, and live verification
passed without it.) One new machine observation from this pass (ptk-session
`PSModulePath` truncation failing four StateToolTests) is recorded in
`.agents/machines.md`, not filed as an issue — owner's call whether it is
product-visible.

Session-close fact (2026-08-05): the #38 and #41 fixes landed **without a
reviewer dispatch** — codex was undispatchable at the time (its configured
headroom gateway was not running and the owner was out of frontier-model
credits), and the owner declined an automatic dispatch for these fixes. Suite
counts belong to `.agents/repo-guidance.md` §Verification, which also records
the plain-shell caveat.

Decision 5 — tag `v0.2.0` and publish — remains owner-only, untouched, and is
now downstream of this backlog. Do not tag or push a `v*` ref without an
explicit go.

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

**Per-harness consent landed (2026-08-04, owner-directed: pacman-style).** A
detection-mode `ptk_init` install asks once: a numbered `Found:` list of
detected harnesses and a single skip selection (`1,3`; `2-4`; `0`=skip all;
Enter=install all). Declines and `-SkipAgent` exclusions print a
manual-setup blurb (the harness's `mcp add` command or config snippet plus
the re-run command). `-AllAgents` answers yes to everything; a
non-interactive session wires all with a notice. `-Uninstall`/`-Show` never
ask. Claude's registration moved from `install.ps1` into the claude leg, so
one consent covers registration + hook + nudge and leg failure fails the
install into rollback as before. `install.ps1` gained `-Agent`/`-SkipAgent`/
`-AllAgents` pass-throughs. Upgrades re-prompt every time — no consent
store; add one only on evidence. Pester 99 passed, 1 platform skip; the
skip-filter mutation is caught by the new tests.

**Kimi harness leg landed (2026-08-04).** `ptk_init.ps1` now covers claude,
codex, grok, agy, and kimi. The kimi leg merges `mcpServers.ptk` into
`~/.kimi-code/mcp.json` (no scriptable CLI surface exists), installs the same
`ptk-hook.ps1` as a `[[hooks]]` PreToolUse `matcher = "Bash"` entry in
`config.toml` (owner-approved in the mandate; kimi's protocol accepts the
script's stdout deny JSON verbatim), and writes the shared nudge block to
`~/.kimi-code/AGENTS.md`. Live-verified on this box (docs/harness-support.md):
a fresh `kimi -p` session was denied on Bash with the ptk guidance and got an
answer from `ptk_state`.

**Fixture sessions renamed `exchange-*` → `sample-*` (`e2d31db`).** The
install-time smoke test printed `named worker topology ok: exchange-online
pid=..., exchange-onprem pid=...` — two processes named after an admin's mail
infrastructure, during a tool install. Nothing Exchange-related ran; the
names were flavour resembling ptk's target workflows. The installer now also
announces the smoke test before running it (local workers, no network).

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

## Rotated 2026-08-05 (drift based on f99a92b)

### From `## Now`

- **#38 fixed and closed (2026-08-05, owner ruling).** PowerShell's engine
  invokes an exception's `Message` override while building the error record,
  upstream of capture — ruled **outside** PTK's boundary. The invariant in
  force promises only that the capture itself executes no user code. An
  untrusted exception now surfaces its type name and base-constructor
  message via a field read of `System.Exception`'s backing field (executes
  nothing); the `Message` override is never invoked by capture and its
  computed text is never reported. Ruling recorded in `.agents/decisions.md`;
  invocation-counting guards in `RunspaceHostTests.cs`, proved failing
  against the old behavior.

## Rotated 2026-08-05 (drift based on a2a8a9a)

### From `## Now`

- **hcc review loop closed (2026-08-04):** the codex generation pass over
  the kimi/consent range produced five findings; those plus owner-reported
  hcc-6 (**install rolled back on claude-less machines** — release-relevant,
  fixed at `553450c`) are all fixed one-commit-each, guard-proved, and
  reviewer-verified accepted (hcc-6 at frontier via owner-named codex pair;
  the claude frontier is undispatchable on this machine — org subscription
  disabled). Details: `.agents/review/index.md`.

- **Four platform test reports landed 2026-08-04/05** (#33 Windows x64, #34
  Windows ARM64, #35 macOS arm64, #36 Arch Linux x64), run against
  `docs/testplan.md`. All four are now closed: ten findings fixed, three
  filed as #38/#40/#41. Verified at `008172e`: server 1,129/1,129 (from
  1,068), Pester 107 with 1 platform skip, working tree clean, master in
  sync with origin.

  Every fix was mutation-proved and reviewed by codex at defaults, two rounds
  maximum per the owner's instruction. The reviews found 14 real defects
  across the rounds — each reproduced before acting — including two the
  reports never saw: `Lazy<T>` and `ThreadLocal<T>` are trusted-assembly
  types whose getters run a caller's delegate, and capture was invoking them.
  The last review returned clean.

- **#37 fixed and closed (`963195d`, `0056128`, `16638e0`).** The rtk rewrite
  ran the real binary when the submitted script defined a shadowing function
  in that same submission — the preflight command snapshot is captured before
  the worker runs, so the function did not exist yet and its name still read
  as `Application`. The submitted AST is now the authority. macOS caught it;
  #33 and #34 marked the same test passing because they tested a
  *pre-existing* function, which the snapshot does see.

  Two codex rounds found **eight** further bypasses of the first fix, all
  closed: switch parameters consuming the alias name, scope- and
  module-qualified names, `Import-Alias` recording a path, dot-sourcing and
  `Import-Module`, `Function:`/`Alias:` provider writes, and unreadable alias
  names. One predated #37: the binder emitted a double call operator for any
  rewritten `& git status`, a parse error rather than the command submitted.

- **`ptk_state` reports the build version (`a7624fa`).** Every report hit
  this: the engine version was shown, nothing identifying ptk, and the Unix
  binaries carry blank file-version fields, so testers named inferred git
  SHAs instead. First line now reads `ptk <version>: pid ...`.

### From `## Open / Parked`

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

  _Rotation note: falsified — `gh issue view 8` confirms #8 was closed
  2026-08-05 06:58:47 UTC. The condition this entry was waiting on was met and
  the issue closed; the parked entry above is preserved verbatim as the
  evidence trail, not as current status._

## Rotated 2026-08-04 (drift based on 19201a1)

### From `## Now`

- **RTK router delegation plan is executed through Slice 6 (2026-08-03).** Plan: `.agents/plans/rtk-router-delegation.md`. Slices 0-6 landed; Slice 7 (version, package, direct proof) is unstarted and gated on Decisions 2-5. Per-slice commit table is in the plan's status block.

  Re-verified as of `a3112f3` (docs-only over code head `f637ad0`) on `ASHBIAMWEB1`: the whole battery passes. Counts live in `.agents/repo-guidance.md` §Verification, refreshed in the same pass; they moved during this work because ~6,500 lines and their tests were deleted. Local SIEM is 226/247 on this host only — the 21 pre-existing symlink-privilege cases recorded in `.agents/machines.md`, not a product failure.

  **Hosted CI is green at `a3112f3`** — all six jobs, and the RTK install step succeeded on ubuntu, windows, and macos, so the previously unexercised install path is now proven. SIEM is 247/247 in CI, where symlink creation is permitted.

  _Rotation note: superseded — Slice 7 was subsequently executed by the release-packaging plan (Slices 7.0-7.5 landed, `v0.2.0-rc.2` draft proved), which current `state.md` records; the commit-pinned CI/verification claims above stand as historical evidence of that pass._

## Rotated 2026-08-03 (drift based on a3112f3)

Landed `## Now` and falsified `## Blockers` entries, preserved verbatim.
Current state lives in `.agents/state.md`; canonical detail lives in
`.agents/review/dispositions.md`, `.agents/review/findings/rbc-5.md`, and
`.agents/machines.md`.

### From `## Now`

- **Implementation reviewed (2026-08-03):** openreview codex over `87d03d8..076626f` returned `acceptable_with_changes` and endorsed the architecture unchanged, including the pinned-path binding. Three material changes and four findings, all adopted and fixed with mutation-proved guards: the startup RTK gate used `File.Exists` while the runtime pins via `TryCapture` (HIGH — a path passing the weaker check let the server start and then run native commands unfiltered); the module still exported the pre-Slice-2 `Resolve-PtcInvokeScript` AST rewriter with no caller and a manifest entry for a deleted function; rewrite acceptance normalized whitespace, so a rewrite altering text inside a quoted argument was accepted; and `server/README.md` documented deleted Bash and post-success behavior. Record: `.agents/review/openreview-rtk-router-codex-r2.md`.

### From `## Blockers`

- **rbc-5/rbc-6 containment WIP is unlocated in this clone (2026-08-03).** The
  recorded carrier `fix/rbc-6-unix-sigkill-escalation` @ `2b3ce1a` does not
  resolve: no such branch locally or on `origin`, and `2b3ce1a` is an ordinary
  `master` ancestor. Either the branch lives only on another remote/clone or it
  was deleted. Do not recreate or re-derive the WIP; an owner ruling is needed
  on whether it still exists anywhere before the `## Now` preservation
  instruction can be acted on.

  _Rotation note: re-checked at `a3112f3` — the branch is still absent, and the
  premise is falsified. `.agents/review/findings/rbc-5.md` §Disposition (owner,
  2026-07-19) already rejected that WIP ("Do not continue or commit it"), so
  nothing was lost and no owner ruling is outstanding._

- **Plan-record drift, reported but not edited in this narrow state pass:**
  the warm-runspace plan still says slice 7 is paused behind the already
  decided GO, and the shared-runspace idea still assumes the rejected policy
  gate. Explicit owner calls, uncontested decisions, and live repo evidence
  named above control. Both re-confirmed still stale 2026-08-03.

  _Rotation note: both source documents were corrected in this drift pass
  (`.agents/plans/warm-runspace-mcp-server.md` status block;
  `.agents/plans/shared-persistent-runspace.md` gotcha 3), so the drift this
  entry tracked no longer exists._

## Rotated 2026-08-03 (drift based on e22d619)

Landed or self-labelled-superseded `## Now` entries, preserved verbatim.
Canonical detail for these records lives in `.agents/review/index.md`,
`.agents/machines.md`, and the named plans; current state lives in
`.agents/state.md`.

### From `## Now`

- **Historical first closure of `ci-slow-seal-2` (2026-08-01; superseded by recurrence):** test-only commit `5180d0b` retained the two-second seal limit with independent three-second elapsed bound below the five-second caller budget. Mutation proof failed at `3.1491933s`; restored focused and 1,221/1,221 full server tests passed. Exact-head run `30748054339` passed all six jobs plus macOS-only rerun jobs `91497781569` and `91498323805`. Historical plan: `.agents/plans/ci-slow-seal-elapsed-headroom.md`.

- **Program complete-source review closed (2026-08-02):** all 49 lines reviewed at blob `ba01d79` in one complete Opus composition pass with worker entry, stdin guard, lifecycle/filter, DI aliases, disposal, and real stdio behavior; focused tests passed 68/68 and exact-head CI `30782965551` passed all six jobs including three-platform handshakes. Existing `opr-1`, `opr-8` through `opr-10`, and `opr-42` remained excluded. Opus found no current defect; no product or test change.

- **ExecutionPlanner complete-source review closed (2026-08-02):** all 675 lines reviewed at blob `234c3833` in three bounded passes plus whole-file Opus integration; focused tests passed 103/103. Accepted HIGH `opr-58`, MEDIUM `opr-55`/`opr-56`, LOW `opr-57`; prior repaired and adjacent findings remained excluded. Exact target-visible probes resolved the sole integration dispute in favor of `opr-55`. Prior limited record expanded; no product or test change.

- **ExecutionPlanner `opr-58` intake (2026-08-01):** HIGH accepted plan-gated at `a3b7994`: successful mixed-dataflow guidance can recommend redirecting a fully buffering producer into its own input. Disposable-file proof preserved 13 characters under the completed pipeline but the suggested shape truncated the target to zero. No product or test change.

- **ExecutionPlanner `opr-57` intake (2026-08-01):** LOW accepted plan-gated at `1de0286`: redirected `CommandExpressionAst` pipelines return `PowerShell` before redirections are checked, so audit routing metadata mislabels file-writing expression forms. Execution remains PowerShellDirect. No product or test change.

- **ExecutionPlanner `opr-56` intake (2026-08-01):** MEDIUM accepted plan-gated at `3628487`: eligibility, domain classification, and guidance ignore `EndBlock.Traps`; RTK dispatch drops the handler. A warm native-error probe proved direct PowerShell enters the trap while the constructed RTK argv cannot. No product or test change.

- **ExecutionPlanner `opr-55` intake (2026-08-01):** MEDIUM accepted plan-gated at `3548eb8`: constant `CommandParameterAst` arguments, including whitespace-separated colon syntax, are merged into one RTK argv element while real PowerShell Standard and Windows native probes send separate prefix and operand elements. Existing test pins the wrong merged vector. No product or test change.

- **ExecutionPlan complete-source record revalidated (2026-08-01):** all 590 byte-unchanged lines rechecked at `6a66e20` in two bounded passes plus whole-file Opus integration; focused planner, dispatch, and shell-dialect tests passed 103/103. Existing `s3-rtk-preference-isolation`, `opr-4`, and `opr-48` through `opr-51` excluded. Three production-unreachable candidates retained only as reactivation notes; Bash original-script provenance verified explicit and guarded. Prior limited record expanded; no product or test change.

- **AuditExportCheckpoint complete-source record revalidated (2026-08-01):** all 527 byte-unchanged lines rechecked at `497e0b2` in two bounded passes plus whole-file production-reachability integration; focused tests passed 103/103. Existing `opr-37`, `opr-38`, and repaired Windows durability were excluded. The only caller candidate belongs to removed exporter completion and remains a reactivation review note, not a current finding. Prior limited record expanded; no product or test change.

- **WorkerSupervisor whole-file review closed (2026-08-01):** all 381 lines reviewed at source-equivalent `c9a7f51` in two bounded passes plus whole-file Opus integration against named sessions, protocol, tools, and real stdio behavior; focused tests passed 29/29. Accepted MEDIUM `opr-53`, LOW `opr-54`, and extended existing MEDIUM `opr-11`; existing MEDIUM `opr-42` remained excluded. Prior limited `3cd2482` record expanded. No product or test change.

- **`opr-11` runtime-boundary extension (2026-08-01):** real shipped-server stdio evidence proves generated DataAnnotations are advisory schema rather than enforced input validation. The existing MEDIUM route-fallback repair must validate in the active runtime path and make `WorkerSupervisor.ParseRoute` refuse explicit unknown values; schema-only repair is insufficient. No product or test change.

- **WorkerSupervisor `opr-54` intake (2026-08-01):** LOW accepted and plan-gated at `c9a7f51`: generated session-name schema constraints are not enforced by the shipped server, and rejected raw names are echoed into PTK directive lines. A real stdio probe injected a forged status line and started no worker. No product or test change.

- **WorkerSupervisor `opr-53` intake (2026-08-01):** MEDIUM accepted and plan-gated at `c9a7f51`: worker-controlled invocation and state text can forge PTK-authored retry, status, and recovery directives because both share one unframed response channel. A real stdio invoke preserved forged status and recovery lines beside the genuine recovery line. No product or test change.

- **AuditEvidenceRetentionAudit whole-file review closed (2026-08-01):** all 188 lines reviewed at `2c6bb7a` with evidence-store deletion, exact content/identity proof, event validation, and focused-test integration; tests passed 15/15. Existing HIGH `opr-35` was excluded as pre-selection scope. Prior limited `735000e` review expanded; no additional distinct finding and no product or test change.

- **WorkerProcessExit whole-file review closed (2026-08-01):** all 179 lines reviewed at `c82d804` with process entry, server exit producers, both bootstrap implementations, and focused-test integration; tests passed 78/78. Accepted LOW `opr-52`; prior limited `a930e27` review expanded. No product or test change.

- **WorkerProcessExit `opr-52` intake (2026-08-01):** LOW accepted and plan-gated at `e13bb8a`: four bounded production protocol/bootstrap detail codes are absent from terminal normalization allowlists, so correct exit classes lose actionable identity, incarnation, containment-group, or handle-direction detail. Focused tests pass 78/78 but do not guard the four mappings. No product or test change.

- **SupervisorLifecycle complete-source record revalidated (2026-08-01):** all 139 byte-unchanged lines rechecked at `4306716` as the named subject with filter, registration, shutdown, lifetime, and recent focused-test integration; tests passed 21/21. Prior limited `2ac1cd4` plus dependency-level filter coverage promoted to explicit complete-source coverage. Opus found no current defect; no product or test change.

- **AuditSpoolRecordCodec whole-file review closed (2026-08-01):** all 127 lines reviewed at `9ac4960` with live/closed readers, sink recovery, scanner, envelope shape, and focused-test integration; tests passed 42/42. Prior limited `4c39b9f` review expanded; Opus found no current defect and no product or test change.

- **WorkerSession whole-file review closed (2026-08-01):** all 125 lines reviewed at `6e2c1d4` with runtime, worker server, construction, artifact capture/codec, and focused-test integration; tests passed 38/38. Existing MEDIUM `opr-4` was excluded without extension. Prior limited `5f2e1fb` review expanded; no additional distinct finding and no product or test change.

- **AuditSpoolSegmentIdentity whole-file review closed (2026-08-01):** all 116 lines reviewed at `1a0d80f` with scanner, checkpoint, writer, retirement, event-validation, and focused-test integration; tests passed 41/41. Production parse consumers gate output before use. Prior limited `b4ffe87` review expanded; Opus found no current defect and no product or test change.

- **AuditEvidenceOrphanReconciler whole-file review closed (2026-08-01):** all 101 lines reviewed at `a352bc2` with both active static startup callers, provider, protected spool, and focused-test integration; tests passed 11/11. Existing MEDIUM `opr-6` and LOW `opr-34` were excluded. Instance cadence API is dormant; static pre-writer proof is live. Prior limited `77a324e` review expanded; no additional current finding and no product or test change.

- **AuditAdminDispositionFailure whole-file review closed (2026-08-01):** all 89 lines reviewed at `cea2ff8` with active disposition administration, typed stage/effect classification, and focused-test integration; tests passed 22/22. Verified `s2-admin-disposition-failures` remained intact and was excluded. Prior limited `888914d` review expanded; no current finding and no product or test change.

- **WorkerLaunchCommand whole-file review closed (2026-08-01):** all 68 lines reviewed at `a565184` with command factory, both platform launchers, and focused-test integration; tests passed 14/14. Existing MEDIUM `opr-29` was excluded after its Unix launcher repair boundary was recorded separately. Prior limited `d847df2` review expanded; no additional distinct finding and no product or test change.

- **AuditOutputRequestProtector whole-file review closed (2026-08-01):** all 67 lines reviewed at `6b349b8` with metadata capture, output-handle generation, and focused-test integration; tests passed 14/14. Repo-wide C# inventory found no production construction or capture caller, correcting the queued active-caller wording. Prior limited `a2c343f` review expanded. Opus found no current defect; no product or test change.

- **InvokeTool whole-file review closed (2026-08-01):** all 65 lines reviewed at `77aa9a9` with assembly registration, session seam, production adapter, schema, and runtime integration; focused tests passed 90/90. Prior limited `f0418b0` review expanded. Opus found no current defect; no product or test change.

- **AuditAdminFailure whole-file review closed (2026-08-01):** all 58 lines reviewed at `11bad9d` with evidence-admin classification, failure publication, and fault-injection integration; focused tests passed 20/20. Prior limited `618f007` review expanded. Opus found no current defect; no product or test change.

- **SupervisorCallFilter whole-file review closed (2026-08-01):** all 55 lines reviewed at `f1cf11d` with server registration, lifecycle/lease, shutdown, and cross-platform integration; focused tests passed 21/21. Prior limited `6675e37` review expanded. Opus found no current defect; no product or test change.

- **AuditEffectiveIdentity whole-file review closed (2026-08-01):** all 30 lines reviewed at `92a60aa` with audit-admin construction, event schema/serialization, and cross-platform integration; focused tests passed 55/55. Prior limited `c1d83e1` review expanded. Opus found no current defect; no product or test change.

- **BashExecutableIdentity whole-file review closed (2026-08-01):** all 26 lines reviewed at `9d5a443` with executable identity, production startup resolution, planner/runner, and focused-test integration; sequential tests passed 102/102. Prior limited `385db4c` review expanded to complete-source coverage. Opus found no current defect; no product or test change.

- **RawUsageCounter complete-source record revalidated (2026-08-01):** all 17 byte-unchanged lines rechecked at `10a3547` against both increment boundaries, state reporting, lifecycle, and focused tests; 6/6 passed. Prior `469959c` no-findings result remains valid, including atomicity and inert signed-wrap adjudication. Semantic inventory now accepts explicit complete-source wording, not only `whole-file` tokens. No product or test change.

- **AuditStartupConfiguration whole-file review closed (2026-08-01):** all 71 lines reviewed at `8648f37` with `PtkAuditAdmin`, options, checkpoint-reader, and test integration; focused tests passed 3/3. Existing MEDIUM `opr-5` remains the only finding. Post-record filename inventory has no missing production `.cs` basename, so coverage work now audits abbreviated records rather than trusting filename mentions. No product or test change.

- **ColdCommandResolution whole-file review closed (2026-08-01):** all 268 lines reviewed at `ca5384f` in two bounded passes and whole-file integration against planner, plan invariants, executable identity, focused tests, upstream source, and platform probes; tests passed 95/95. Accepted MEDIUM `opr-48`, `opr-49`, `opr-50`, and LOW `opr-51`; existing `opr-2` and refuted-as-defect `rbc-13` remained excluded. No product or test change.

- **ColdCommandResolution `opr-51` intake (2026-08-01):** LOW accepted and plan-gated: Windows target matching uses case-sensitive record equality despite platform-aware identity policy, so casing-only resolution changes spuriously no-start. No product or test change.

- **ColdCommandResolution `opr-50` intake (2026-08-01):** MEDIUM accepted and plan-gated: Windows drive-relative command names bypass the bare-name guard and resolve against server drive state instead of child location semantics. No product or test change.

- **ColdCommandResolution `opr-49` intake (2026-08-01):** MEDIUM accepted and plan-gated: Windows rooted or drive-relative PATH entries bind server process drive state instead of the audited child working directory. No product or test change.

- **ColdCommandResolution `opr-48` intake (2026-08-01):** MEDIUM accepted and plan-gated: Unix resolver tests the union of raw execute bits rather than real-identity `X_OK`, so cold prepare and commit can bind a PATH file PowerShell would skip. No product or test change.

- **BashProcessRunner whole-file review closed (2026-08-01):** all 803 lines reviewed at `94ff698` in three bounded passes plus whole-file integration against containment, RTK parity, invoke models, dispatch, and production callers; focused tests passed 27/27. Accepted MEDIUM `opr-47`; extended existing `opr-4` and `opr-40`. No product or test change.

- **BashProcessRunner `opr-40` extension (2026-08-01):** Bash execution uses the same two eager 4 MiB capture allocations as direct RTK, roughly 8 MiB of large-object-heap storage before output. Accepted scope of the existing LOW plan-gated allocation-shape defect. No product or test change.

- **BashProcessRunner `opr-4` extension (2026-08-01):** Bash pre-start budget classification can combine `TimedOut=true` with cancellation audit detail because the guard snapshots cancellation but `BudgetFailure` re-reads the deadline. Accepted LOW scope of the existing MEDIUM plan-gated immutable-cause defect. No product or test change.

- **BashProcessRunner `opr-47` intake (2026-08-01):** MEDIUM accepted plan-gated: a slow successful validator-start audit flush can consume the fixed `bash -n` process budget and replace an already-determinate exit verdict with `TimedOut`. No product or test change.

- **AuditJournal whole-file review closed (2026-08-01):** all 897 lines reviewed at `54822ee` in three bounded passes plus whole-file integration against serializer, health, factory, live-reader, and production caller evidence; focused tests passed 59/59. No additional distinct finding; direct `opr-35` and `opr-36` remain unchanged. No product or test change.

- **WindowsProcessTreeSupervisor whole-file review closed (2026-08-01):** all 621 lines reviewed at `32c5748` in three bounded passes plus whole-file integration against native implementation, authority, and production callers; focused tests passed 85/85. No additional distinct finding; existing `opr-23` and the separate `rbc-5` boundary remain unchanged. No product or test change.

- **UnixWorkerProcessLauncher re-review closed (2026-08-01):** all 1,207 lines reviewed at `431183f` in four bounded passes plus whole-file integration against broker, registry, authority, and production callers; focused tests passed 20/20. No additional distinct finding; existing `opr-14`, `opr-24` through `opr-29`, and `opr-46` remain unchanged. No product or test change.

- **UnixWorkerContainmentRegistry re-review closed (2026-08-01):** all 515 lines reviewed at `afbf64f` in two bounded passes plus whole-file integration; focused tests passed 12/12. One current LOW finding, `opr-46`, was accepted; lifecycle/API candidates were rejected as reference-safe or production-unreachable. No product or test change.

- **UnixWorkerContainmentRegistry `opr-46` intake (2026-08-01):** LOW accepted and plan-gated: PID reuse between identity and group probes can latch false escape and transiently report `descendants_unknown`; exact background proof still releases registry. No product or test change.

- **SessionWorkerClient re-review closed (2026-08-01):** all 888 lines reviewed at `c8e6c4e` in three bounded passes plus whole-file integration; focused tests passed 80/80. No additional distinct finding; existing HIGH `opr-19` and LOW `opr-22` gained scoped repair/guard extensions. No product or test change.

- **SessionWorkerClient `opr-22` scope extension (2026-08-01):** existing LOW finding now covers both directions of late wall-clock classification: first-use delay can label timeout canceled, while successful cleanup crossing deadline can label caller cancellation timed out. No product or test change.

- **SessionWorkerClient `opr-19` scope extension (2026-08-01):** existing HIGH finding now also requires stop repair to coordinate prompt worker exit with `_stopping` and preserve containment on every failed graceful exchange. No product or test change.

- **TrustedPreflightClassifier re-review closed (2026-08-01):** all 448 lines reviewed at `864cfe2` in two bounded passes plus whole-file integration; focused tests passed 88/88. Exact runtime probes admitted HIGH `opr-43`, MEDIUM `opr-44`, LOW `opr-45`; one invalid-dual-dialect candidate was rejected. No product or test change.

- **TrustedPreflightClassifier `opr-45` intake (2026-08-01):** LOW accepted and plan-gated: nested local definitions are flattened across script-block scopes, suppressing valid top-level Bash builtin evidence that PowerShell cannot resolve. No product or test change.

- **TrustedPreflightClassifier `opr-44` intake (2026-08-01):** MEDIUM accepted plan-gated: named Bash options after `set -o` are missed unless the literal is `pipefail`, causing valid Bash to fail under PowerShell `Set-Variable`. No product or test change.

- **TrustedPreflightClassifier `opr-43` intake (2026-08-01):** HIGH accepted plan-gated: fatal PowerShell parse handling returns before recovered trusted Bash command evidence is scanned, so valid Bash can fall through to PowerShell failure. No product or test change.

- **NamedSessionSupervisor re-review closed (2026-08-01):** all 1,231 lines reviewed at `ca7fe85` in three bounded passes plus whole-file integration; focused tests passed 36/36. One current finding, MEDIUM `opr-42`, was accepted in the active `WorkerSupervisor` caller; all supervisor-local candidates were rejected. No product or test change.

- **NamedSessionSupervisor integration intake `opr-42` (2026-08-01):** MEDIUM accepted plan-gated: `WorkerSupervisor.StateAsync` releases the session lease, then uses two registry snapshots; concurrent removal can fault, same-name replacement can mix incarnations, and registry change can tear the count. No product or test change.

- **DefaultSessionRuntimeFactory re-review closed (2026-08-01):** all 44 lines reviewed at `c5f9536` with active callers and protocol validation; focused tests passed 26/26. One candidate merged into existing MEDIUM `opr-10`; no distinct new finding and no product or test change.

- **DefaultSessionRuntimeFactory `opr-10` extension (2026-08-01):** finite fractional or sub-millisecond positive timeout values violate the downstream whole-second protocol contract and abort startup. Merged into existing MEDIUM `opr-10`; no new finding ID and no product or test change.

- **RtkProcessRunner re-review closed (2026-08-01):** all 442 lines reviewed at `2debaf6` in two bounded passes plus whole-file dispatch integration; focused tests passed 76/76. Outcomes: LOW `opr-40`, MEDIUM `opr-41`, LOW extension to existing MEDIUM `opr-4`, and Opus-clean comment correction `e83c209`. No runtime behavior or test change.

- **RtkProcessRunner `opr-41` intake (2026-08-01):** MEDIUM accepted plan-gated: a canceled or expired RTK dispatch can start no process yet persist a fabricated zero `$LASTEXITCODE`; the reset pipeline can also replace prior `$?` with success. No product or test change.

- **RtkProcessRunner `opr-4` extension (2026-08-01):** pre-start budget classification can combine `TimedOut=true` with a cancellation audit detail because the decision snapshots cancellation but re-reads the deadline. Accepted LOW as the existing immutable-cause defect; `opr-4` remains MEDIUM and plan-gated. No product or test change.

- **RtkProcessRunner review intake (2026-08-01):** `opr-40` LOW accepted and plan-gated: every direct RTK execution eagerly allocates two 4 MiB capture buffers, roughly 8 MiB of large-object-heap storage before any output is read. No product or test change.

- **OutputRootLease current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 433 lines in two bounded passes plus one whole-file active-caller integration pass; focused tests passed 27/27. One distinct LOW finding, `opr-39`, was accepted; verified `opr-3` was excluded and a closed-descriptor candidate was rejected. No product or test change.

- **ChildStdinGuard current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 68 lines in one complete source/startup-caller/test pass; focused tests passed 22/22. Outcome `no_additional_current_findings`; existing `opr-1`, `opr-8`, and verified `rbc-1` overlap were excluded. No product or test change.

- **ScriptEvidenceStore current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 1,613 lines in eight bounded passes plus three thematic integration passes; focused tests passed 89/89. Outcome `no_additional_current_findings`; verified `s2-admin-evidence-failures` and existing `opr-35` overlap were excluded, and nine pass candidates were independently rejected after current-call-path adjudication. No product or test change.

- **ScriptEvidenceStoreProvider current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 243 lines in three bounded source/contract/test passes plus one current-production call-graph pass; focused tests passed 50/50. Outcome `no_additional_current_findings`; existing `opr-6` was excluded, and four pass candidates were independently rejected as current-inert or production-unreachable. No product or test change.

- **AuditSpoolQuotaLease current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 254 lines in two bounded passes plus one active-caller integration pass; focused tests passed 10/10. Outcome `no_additional_current_findings`; existing `opr-7` was excluded. No product or test change.

- **AuditEvidenceSpoolScanner current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 731 lines in four bounded passes plus one active-caller integration pass; focused tests passed 31/31. Outcome `no_additional_current_findings`; existing `opr-34` and `opr-35` overlap was excluded. No product or test change.

- **AuditCallMetadata current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 710 lines in four bounded passes plus one production-reachability pass; focused tests passed 14/14. Outcome `no_additional_current_findings`; `AuditCallMetadataCapture.TryCapture` has no production caller after intentional runtime-audit removal — only tests and its own definition remain — and existing `opr-11`, `opr-12`, and verified `s2-job-id-audit-poison` were excluded. No product or test change from this review.


- **SessionRuntime current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 491 lines in four bounded passes plus one production-call-graph pass; focused tests passed 109/109. Outcome `no_additional_current_findings`; existing `opr-18` was excluded and a factory-only `opr-10` rediscovery was rejected. No product or test change.

- **FileAuditJournalSink full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,707 lines in eleven bounded passes plus four thematic cross-boundary passes; focused tests passed 50/50. Outcome `no_current_findings`; two same-user substitution candidates and one Linux raw-descriptor lifetime candidate were rejected against the quota call graph, threat boundary, and sole caller lifetime. No product or test change.

- **SecureAuditStorage full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,619 lines in seven bounded passes plus two split cross-boundary passes; focused tests passed 15/15. Outcome `no_current_findings`; Windows durability-seam and macOS ACL candidates were rejected on exact semantics, caller ordering, and threat boundary. No product or test change.

- **AuditEvent full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,227 lines in five bounded passes plus two split cross-boundary passes; focused tests passed 13/13. Active admin and journal event validation/serialization is clean; dormant call-context paths were excluded. No product or test change.

- **AuditOperatorDispositionOutcome full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,110 lines in five bounded passes plus two split cross-boundary passes; focused tests passed 22/22 and 13/13. Outcome `no_current_findings`; no product or test change.

- **AuditOperatorDispositionIntent full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,057 lines in five bounded passes plus cross-boundary adjudication; focused tests passed 22/22. Outcome: one current finding, `opr-38` LOW, already recorded plan-gated; no product or test change.

- **Review intake:** `opr-38` LOW accepted plan-gated: `AuditOperatorDispositionProof` accepts one trailing LF in an acknowledged-gap reason, durably creating an out-of-grammar proof whose clean spelling conflicts on retry.

- **AuditOptions full review complete (2026-08-01):** Claude Opus 5 reviewed all 208 lines plus the complete production call graph; focused baselines passed 10/10 and 3/3. Outcome `no_current_findings` beyond existing `opr-5`; the newline-permissive options regex is unreachable behind the checkpoint codec's strict identity validation. No product or test change.

- **AuditCompletedChainRetirement full review complete (2026-08-01):** Claude Opus 5 reviewed all 933 lines in five bounded passes plus cross-boundary adjudication; focused retirement tests passed 13/13 and preparation tests 22/22. Active `RecoverUnderQuota` is clean; removed exporter initiation remains dormant. No product or test change.

- **AuditExportCheckpointStore full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,659 lines in seven bounded passes plus cross-boundary adjudication; focused tests passed 40/40. Outcome `no_current_findings`; removed exporter-only block/retry paths require re-review if reactivated. No product or test change.

- **AuditAnchoredWriterPreparation full review complete (2026-08-01):** Claude Opus 5 reviewed all 493 lines in three bounded passes plus cross-boundary adjudication; focused tests passed 22/22 and 13/13. Outcome: one current finding, `opr-37` HIGH, already recorded plan-gated; no product or test change.

- **Review intake:** `opr-37` HIGH accepted plan-gated: crash-truncated canonical initial-checkpoint temporary is non-authoritative but rejected by exact-byte-only recovery, permanently denying anchored and administration startup.

- **AuditAdminOperations full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,124 lines in five bounded passes plus cross-boundary integration at `bfd571e`; evidence-access tests passed 20/20 and disposition tests 22/22. Two candidates were rejected by exact ownership and mutation-tested publication semantics. Outcome `no_current_findings`; no product or test change.

- **Windows scheduler cancellation recurrence is closed:** the earlier
  drain-based repair `8588374` remained vulnerable because the test's disposable
  token callback could be removed during cancellation unwind. Test-only repair
  `9e66a35` witnesses cancellation in the operation's own unwind path before
  drain; Opus accepted the exact SHA with `guard_confirmed=true`. PR #31 run
  `30703723050` passed all six jobs and merged as `c9e5a44`.
- **GitHub issue #12 is closed as superseded by the per-connection worker
  topology:** bounded Windows probes against both the source-built server and a
  self-contained `win-x64` apphost completed the full outer-worker → child
  client → inner-supervisor/worker → nested-`ptk_invoke` path in 6.4/6.5
  seconds. Four distinct process identities were observed, nested output was
  `42`, and the outer session remained ready. Opus accepted closure; no code
  change or regression guard was warranted. Reopen only with exact current
  build identity and server/worker exit diagnostics from a recurrence.
- **GitHub issue #11 is closed after real-Codex transport-boundary validation:**
  installed `0.2.0-dev.g12e1ff5` recovered a path-verified killed worker on the
  same MCP connection, reported `warm_state_lost`, and invoked through a new
  worker. Killing the path-verified public supervisor made the old client fail
  immediately with `Transport closed`; a fresh Codex session launched a new
  supervisor and invoked successfully. Opus accepted the qualified worker/public
  boundary docs; PR #26 passed all six hosted jobs and merged as `3b570ff`.
- **GitHub issue #10 is closed as resolved by retiring the audit startup gate:**
  installed `0.2.0-dev.g12e1ff5` initialized with `PTK_AUDIT_ROOT` beneath a
  regular file, reported audit disabled before and after reset, invoked, reset,
  created no audit root, and exited gracefully. The current startup guard passed
  1/1 and the hosted handshake diagnostic remains green. Opus accepted closure
  with `guard_confirmed=true`; reopen only on current-build audit gating.
- **GitHub issue #9 is closed after exact-client validation of both reported
  stages:** current Claude Code path-verified and killed its installed public
  supervisor, then transparently restarted PTK and returned `ptk_state` promptly
  on one post-kill call. Current Codex instead failed immediately with
  `Transport closed` and succeeded from a fresh session; neither client hung.
  The audit wedge is the retired gate closed under #10. Opus accepted closure;
  the original older-client observation remains valid history.
- **GitHub issue #3 is closed after current-product reconciliation:** shell
  dialect guidance, cross-platform warm-state isolation, 300-second default
  timeout/recovery, and an actionable live missing-command diagnostic cover its
  ergonomics items. The permission proposal is resolved by the documented
  non-authorization-boundary/client-prompt posture and the owner's explicit
  rejection of the bypassable policy-file gate. Opus accepted closure.
- **HISTORICAL — superseded 2026-07-30: Non-disruptive side-by-side upgrade plan
  drafted at `5f5d00d`; valid Claude
  Opus 5 openreview intake is complete.** `.agents/plans/mcp-side-by-side-upgrade.md`
  records the captured retry over `c4bd2af..caf467e`: ten candidates, eight
  admitted and two declined, with one admitted metadata finding already
  resolved. The owner settled `ssu-1` on 2026-07-29: managed registrations use a
  native self-contained stable launcher and retain the no-`pwsh` distribution
  contract. The owner also settled `ssu-3`: migration is transactional and
  harness-specific; Codex avoids its unsafe remove command, Grok requires a
  disposable-config CLI proof, and every changed harness rolls back
  byte-for-byte on failure. The owner settled `ssu-4`: install-home `~/.ptk/launcher/` is never a
  wholesale payload entry or removed after registration; launcher changes use
  validated sibling-file replacement and fail closed if the stable path cannot
  remain continuously present. The owner settled `ssu-5`: Windows uses
  handle-based `FileRenameInfoEx` replacement with delete-sharing readers and no
  delete-first fallback; Unix uses `rename(2)` plus a parent-directory flush.
  The owner settled `ssu-6`: install/reuse performs full runtime hashing, while
  normal launch reads at most 266240 control-file bytes and never enumerates or
  hashes the payload tree. One other plan/product finding and one citation
  correction remain open at
  `.agents/review/mcp-side-by-side-upgrade-opus5-r2.md`. The next owner gate is
  `ssu-7`. Implementation is not authorized. Codereview remains deferred until
  Slice 0 has an implemented fix and deterministic revert-fails/restore-passes
  guard proof.

- **HISTORICAL — superseded 2026-07-30: MCP upgrade-continuity openreview
  completed on 2026-07-29; owner ruling
  pending.** Claude Opus 5 at max effort reviewed
  `d32f2a06e451df02cfa9b63d5c0dda570d1262ec..a308bdadbbda954340f48d63772e72787e9f6990`.
  Intake admitted five findings and declined one overstatement; see
  `.agents/review/mcp-upgrade-continuity-opus5-r1.md` and `muc-1` through
  `muc-6` in `.agents/review/index.md`. Current recommendation is
  side-by-side immutable versions below the single `~/.ptk` home, with active
  connections left on their pinned old version until natural exit. Same-client
  transparent adoption remains unsolved and would require an explicit owner
  reversal before reopening the discarded guardian or daemon architectures.

- **Windows enterprise validation landed local fixes `36146a1` and `56b562c`
  on `ASHBIAMWEB1` (2026-07-29).** Hosted workers now discover modules beside
  installed `pwsh`, and Windows runspaces now use STA for COM automation. The
  real exact-account EXO metadata read and warm-session reuse passed; its
  explicitly selected values were retained and the remaining incomplete marker
  truthfully denotes uninspected type data. Graph never authenticated or read
  API data. STA removed the Outlook COM hang, but the local Outlook profile has
  no current user, so no Inbox item was read. No candidate was installed during
  those field probes and no probe process was left running; detailed host
  evidence is in `.agents/machines.md`.
- **Exact-head Windows x64 PTK runtime/package acceptance passed at
  `7eaf8a0cbe391abda7185e23e621fe7b01028886` on `ASHBIAMWEB1`
  (2026-07-28).** Direct Windows execution found and separately committed
  acceptance witness startup/CRLF defect `6ccdaa7` and real worker
  initialization deadline-classification race `7eaf8a0`. The final head
  passed 1,212/1,212 server tests, 144 Pester tests with one platform skip,
  direct and packaged handshakes, disposable staged activation, and packaged
  100-cycle worker replacement/Job Object timeout/crash/cleanup acceptance.
  The ordinary token still cannot create Windows symlinks, so the independent
  SIEM suite remains 226/247 with all 21 failures during privileged link
  setup before product assertions. No candidate was installed or registered;
  host evidence and package hashes are in `.agents/machines.md`.
- **Windows installer ACL repair landed locally at `bb2349a` (2026-07-28).**
  The install transaction now normalizes its Windows payload root before staged
  validation to a protected, current-user-only, inheritable full-control ACL.
  This prevents a sandbox-created non-inheriting `~/.ptk` DACL from making
  installed `~/.ptk/bin/` unreadable after activation. The regression failed with the
  exact access denial when the fix was removed and passed after restoration;
  the live ACL-only repair restored `ptk_invoke`. Installed payload bytes were
  not replaced and nothing was pushed. Host evidence is in
  `.agents/machines.md`.
- **Handoff checkpoint (2026-07-28): all currently available Linux x86_64
  acceptance is complete; the remaining Windows and enterprise gates are
  separate.** Exact committed head
  `37b7d94dacdfd7fb03b52ce95d4409031e6e6699` passed Pester on `gabrielle`
  and the full server, SIEM, dependency-audit, native-package, handshake, and
  100-cycle staged-package production-acceptance battery on `magneto`.
  Evidence is committed at `14daad6`; the read-only installed-payload rollback
  baseline is committed at `3f5dc11`. All disposable validation roots were
  removed, and nothing was installed, registered, deployed, or pushed. The
  current Codex installation remains the old `0.2.0-dev.g6db333c` job-API
  build, not the validated five-tool named-session candidate. `NETWATCH-01`
  can close only generic Windows packaging/process/Job Object acceptance; it
  is a personal gaming machine with no company AD, on-prem Exchange, Exchange
  Online, or Outlook administration access. Those real workflow gates require
  a separate company-connected supported Windows admin host with the required
  modules, network access, and authentication.
- **Exact-head staged-package acceptance now also passes on macOS ARM64.**
  Committed head `920944ef3a4491edbf1d2c6a3915a5e39106a8fb`
  produced genuine Mach-O ARM64 `PtkMcpServer` and `PtkWorkerBroker`
  executables, passed the five-tool staged handshake, and passed the complete
  100-cycle production-acceptance matrix from the packaged executable. Process
  and file-descriptor counts remained 5/5 and 531/531 across all replacements;
  timeout, direct worker death, process-group escape, and supervisor hard-kill
  containment all passed. No process remained, the disposable root was
  removed, and nothing was installed or registered.
- **One immutable Linux x86_64 package now passes on all three available Linux
  hosts, including both hosts without a .NET SDK.** `magneto` built and
  validated the exact `d243c82d135b390b69fd93b25d01135f4d791a58`
  package; `gabrielle` and `altiera` independently verified the same source
  and layout hashes, confirmed the SDK was absent, and passed both the complete
  public handshake and 100-cycle production acceptance from those package
  bytes. Each host contained timeout and direct-kill descendants, refused the
  process-group escape, preserved the sibling, and removed all descendants
  after supervisor hard-kill. No package process remained, all disposable
  roots were removed, and nothing was installed or registered.
- **Production-reliability Slice 11 is complete and integrated on local
  `master`.** Exact documentation/integration commit
  `2c96e842618d73ac59fa37d5492a5ae92a0d163d` publishes the implemented
  one-supervisor/many-session-workers contract, reduces audit documentation to
  retained administration/receiver boundaries, marks the discarded guardian
  plan as historical, and reconciles the release-plan pointer. Verification
  found that build-on-launch `dotnet run` wrote NuGet warnings onto MCP stdout;
  the checkout path now builds first and launches with
  `--no-build --no-launch-profile`. The original registration handshake failed
  on that non-JSON stdout and the corrected exact command passed the complete
  five-tool/named-session handshake. Pester passed 141 with two platform skips,
  the server suite passed 1,212/1,212, and the retained SIEM receiver passed
  247/247. Current `origin/master`
  `c9b11bcb0b4e41a11110c5870562b4980c0b86b3` was the exact merge base; local
  `master` was fast-forwarded and its content diff against the verified salvage
  branch was empty. Nothing was pushed, installed, registered, or deployed.
- **The five high-severity XML-crypto dependency advisories are closed locally
  at exact commit `b6fcbcdd9a81bdd5ce9e9ba8dde087a2adc02ff3`.**
  `Microsoft.PowerShell.SDK` 7.6.3 still requests vulnerable
  `System.Security.Cryptography.Xml` 10.0.6, so the server now directly pins
  patched 10.0.10. The transitive vulnerability audit changed from five
  advisories on every server project to no vulnerable packages; the complete
  Pester/server/SIEM/handshake battery and 100-cycle production acceptance
  passed with zero build warnings. A validated self-contained layout resolved
  only 10.0.10 and passed the complete public handshake. A replacement-server
  diagnostic also preserved all values on an EXO-style selected/deserialized
  object, evaluated an explicitly selected script property exactly once, and
  surfaced a terminating error's message. This is useful synthetic evidence,
  not a substitute for the pending real EXO/Outlook workflow on Windows.
- **Cross-RID package generation now fails closed at exact commit
  `e2beda3c35b6bc4d1a6b1d0191e43ed9957a19f0`.** A macOS probe of the advertised
  `linux-arm64` layout path found a Mach-O ARM64 `PtkWorkerBroker` inside the
  package rather than a Linux ELF binary. PTK has no proved cross-target native
  compiler contract, so `dev-install.ps1 -LayoutOnly` now refuses a target RID
  that differs from the build host before creating the output directory. The
  new guard failed when the check was removed and passed when restored; Pester
  passed 142 with two platform skips, server 1,212/1,212, SIEM 247/247, the
  direct-checkout handshake, and a same-RID self-contained package handshake.
  Exact committed head `37b7d94dacdfd7fb03b52ce95d4409031e6e6699`
  subsequently passed the complete Linux x86_64 battery on real hosts: Pester
  passed 142 with two platform skips, server passed 1,212/1,212, SIEM passed
  247/247, the transitive vulnerability audit was clean, direct and
  staged-package handshakes passed, both public executables were genuine x86-64
  ELF binaries, and the 100-cycle production-acceptance matrix passed. This
  prevents a mislabeled release artifact and proves the matching-host
  `linux-x64` path; it does not replace the pending build and execution on a
  real ARM64 Linux host. UTM is not used.
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
- **Production-reliability salvage Slice 8 is locally implemented and committed
  at product code head `4270487c891d3c2cb0976f25eb3082f90c7ac630`;
  test-only guard descendant `4779cb2f7000306cc11f24fe017a4671b5b16cbf`
  closes a mutation-discovered late-publication test gap. macOS ARM64 and Linux
  x86_64 are verified; Windows x64 remains pending.** Optional recovery now
  reserves full per-invocation quota against immutable session identity before
  dispatch, uses one fixed capture buffer and one bounded connection-wide
  storage lane, validates the complete worker artifact protocol while
  discarding, publishes only immutable complete handles, and safely reclaims
  abandoned owned output roots without touching live roots. Fifteen deliberate
  production regressions failed for their intended reasons and were restored;
  the restored focused battery passed 82/82 and the complete server suite
  passed 1,197/1,197 on the test-hardened macOS head. The product commit also
  passed Pester 141 with two platform skips, SIEM 247/247, formatting, public
  built and published handshakes, and ARM64 self-contained publish smoke on
  macOS; its verified archive passed the full server/Pester/SIEM/handshake
  battery on Linux x86_64 `magneto`. No candidate process or disposable Linux
  checkout survived. The slice is not pushed or installed; exact evidence is
  in `.agents/machines.md`.
- **Production-reliability salvage Slice 9 is locally implemented and committed
  at code head `46710afd2e90911f2402019ff9ab4ddc10695845`; macOS ARM64 and
  Linux x86_64 are verified, and Windows x64 remains pending.** The development
  installer now publishes only the single public supervisor/internal-worker
  package and required Unix broker, runs the complete public handshake before
  and after activation, snapshots the installer-owned payload and known harness
  registrations, changes registrations last, and restores and verifies exact
  bytes plus Unix modes after activation or registration failure. Registration
  initialization runs as a checked child, so its deliberate `exit 1` cannot
  bypass rollback. Unconfirmed rollback retains a readable recovery manifest.
  Six deliberate regressions proved file restoration, recovery-snapshot
  retention and manifest, checked child registration, current named-session
  guidance, and same-named function isolation. The exact commit passed the
  complete Linux x86_64 battery on `magneto`: server 1,199/1,199, Pester 141
  with two platform skips, SIEM 247/247, built handshake, transaction matrix,
  local-RID layout publish/validation, and staged plus activated package
  handshakes. The matching macOS package smokes and behavior batteries passed;
  one exact-final full-suite repeat hit the already-recorded unchanged 500 ms
  Bash pipe-drain fixture under host load and passed immediately in isolation.
  No installed payload or real registration changed, and no candidate process
  or disposable Linux checkout survived. Exact evidence is in
  `.agents/machines.md`.
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
- **Slice 10 production acceptance is in progress on
  `impl/production-reliability-salvage`.** `c2f67d3` added the unshipped
  production-acceptance stress harness and package-boundary guards; `9151e57`
  made selected-session state and the supervisor-local session list prove
  prompt, truthful behavior while three real calls remain active across two
  servers. Linux acceptance then reproduced an intermittent
  `descendants_unknown` reset refusal even though broker and registry
  containment were confirmed. Root cause was an asynchronous forwarding race:
  the Unix registry's exact empty-domain proof was complete, but the public
  worker proof task could still be pending when the supervisor checked it.
  `156aac2` publishes that already-completed exact proof before returning
  `ConfirmedEmpty`; the supervisor's fail-closed rule is unchanged. The guard
  failed with the synchronization removed and passed after restoration.
  Verification at `156aac2`: 1,204 server tests, 141 Pester tests (2 skipped),
  247 SIEM tests, handshake, macOS 100 replacement cycles, and the Linux
  diagnostic reproducer's 100 replacement cycles all passed.
- **Slice 10 timeout containment is committed and directly verified at
  `ef1657b22b9847f24dc991e6a9c0a58aa8624281`.** One absolute admission
  deadline now reaches the runspace; a dedicated watchdog stays live while
  PowerShell cancellation or terminal transport is blocked; timed-out
  incomplete output remains recoverable under the supervisor's single recovery
  handle; and production fail-fast replaces only the unresponsive worker. Five
  targeted regressions failed for their intended reason and passed after
  byte-exact restoration. Complete macOS ARM64 and exact-archive Linux x86_64
  batteries passed, including 100 replacements, timeout child/grandchild death,
  sibling survival, resource return, and supervisor hard-kill containment.
  Exact host identities, commands, counts, archive/log hashes, and cleanup are
  recorded in `.agents/machines.md`. Nothing was installed, deployed, or
  pushed.
- **Slice 10 Unix process-group escape acceptance is committed at
  `a626c3b8e993575ad916058bc33959c02b4daae3` and verified on macOS ARM64 and
  exact-archive Linux x86_64.** The public harness proves a real `setsid`
  escape reports `descendants_unknown`, retains the escaped PID as unconfirmed,
  blocks worker reuse, does not replay the timed-out command, and permits a
  different worker only after exact escaped-PID cleanup. Making the registry
  ignore process-group changes produced a false `ready` replacement and made
  the new guard fail; byte-exact restoration passed reduced and full
  acceptance. Full host evidence and cleanup hashes are in
  `.agents/machines.md`.
- **Slice 10 direct-worker-death containment is committed at
  `8b9644ae8937b33c9bd9de1498dbef6ee7cd88d2` and verified on macOS ARM64 and
  exact-archive Linux x86_64.** The Unix broker now treats reaping the worker as
  the immediate trigger to contain any remaining process-group members;
  descendants can no longer keep the broker waiting indefinitely by retaining
  inherited pipes. The new low-level guard failed against the old broker with
  both descendants still live and passed after the fix. Public acceptance
  directly kills a worker during an in-flight command, proves both descendants
  exit, reports `outcome_unknown` without replay, replaces only that worker,
  and preserves the sibling PID and warm variable. Complete macOS and Linux
  batteries passed; exact counts, log hashes, host split, and cleanup are in
  `.agents/machines.md`. A focused nine-case pass makes the full hard-kill
  boundary mapping explicit: deterministic transport-death guards cover before
  write, write-call entry, during result, and after terminal decode; real
  worker/process guards cover during execution and after an observable effect.

## Rotated 2026-07-11 (drift based on 78779b0)

The previous current-work sections are preserved verbatim below. Live
reverification found the clone and canonical remote synchronized at
`78779b0`, issues #5/#6 closed, and the Mac install current for all product
files; the replacement state records the remaining held conflicts.

### From `## Now`

- **SECURITY: question OPEN, owner-led consultation in progress — read
  `.agents/plans/security-layer.md` top section BEFORE any security
  work.** Two distinct things, do not conflate: (1) **DECIDED** — the
  declarative policy-file gate is rejected as the answer (owner: "brittle
  nonsense — alias it, use python, edit the rules file"); its slices are
  prior art, do not implement. (2) **NOT a decision** — the "ptk gets
  zero consideration / low-friction bypass gated on one careless yes /
  missing the `Remove-Mailbox CEO@company.com Y/n?` check" statement is
  the owner's FRAMING for an outreach consultation he is running
  himself, recorded verbatim in the plan. It is the problem any shape
  must answer, not a verdict already delivered. Do not cite it as a
  settled call. **Live candidate, UNVERIFIED: MCP elicitation**
  (server-initiated user confirmation mid-call) — verify spec support,
  per-harness client support, headless behavior (a prompt no client
  renders fails OPEN — worse than none). From the neutral cross-harness
  consultation, still open as candidates: ConstrainedLanguage profile,
  authenticated-session TTL/process-teardown, control-action gating
  placement above `ptk_reset`/`ptk_job kill` (reset kills jobs before
  any check could fire). **Secret redaction was raised by all three and
  REJECTED** (owner's delta test: `bash -c 'cat secret.txt'` leaks the
  same secret today; verified ptk_state emits env NAMES only, never
  values). Process lessons recorded in the plan: (a) shape review BEFORE
  plan review — a review loop cannot question its own premise; (b) apply
  the DELTA TEST to every proposed control — what does routing through
  ptk make worse than the path that already exists? Three models
  agreeing is not evidence; they pattern-match the same way.
- **Two DRAFT plans, codex-reviewed, NEITHER APPROVED — no code written
  for either.** (a) `.agents/plans/rtk-rewrite-routing.md` — route all
  native work through `rtk rewrite` (owner direction 2026-07-10:
  "everything that isn't powershell should route through rtk"); loop
  CLOSED CONVERGED after 7 rounds, 15 findings, rounds 20→7→6→2→1→1→1.
  One item (rrp-15) closed by coder disposition, not reviewer grade —
  **flagged for owner ratification**: routing by substitution means
  name-keyed session hooks (a `git` breakpoint) never fire on a routed
  segment; inherent to substitution, already true of shipped
  single-command routing, disclosed in docs with `route=pwsh` as escape.
  (b) `.agents/plans/security-layer.md` — see the rejection above; its
  12 findings were all resolved before the premise fell.
- **`.agents/decisions.md` is UNDER HOLD** (owner, in-session 2026-07-10:
  "don't update decisions until we're done talking"; a premature entry
  was reverted at `6ee6f9e` — reverted, never rewritten). The rtk-routing
  direction and the security reframe both still need decision entries
  when the owner releases the hold.
- **~30 commits local-ahead of `origin/master`** (both plan drafts, all
  loop fixes, the loop records). Owner pushes himself.
- **issue-5/6 batch COMPLETE, loop CLOSED (2026-07-10)**: the
  implementation review ran three rounds (10 findings → 3 reopened + 5
  new → clean NO-FINDINGS close at `9a894e1`); all 25 findings fixed one
  per commit, record in `.agents/review/index.md`. Battery at head:
  dotnet 100/100, Pester 133/1 skipped, handshake PASSED, live stdio
  issue-repro checks 11/11 (canonical counts). Owner PUSHED 2026-07-10
  (`master` == `origin/master` at `f12dd46`); issues #5 and #6 got fix
  references and were CLOSED the same day. GitHub reports the remote
  renamed to `PowerShell-Token-Killer` (capital W) — the configured URL
  still works via redirect. Owner grant (2026-07-10, in-session):
  persistent approval to handle GitHub issues on this repo as
  appropriate (comment/close/triage without per-action asks).
  Machine-local note: this session's ptk server died during the slice-0
  incident and the installed payload predates the whole batch — a
  dev-install re-run is needed for live sessions to pick it up.
- Batch details (`.agents/plans/issue-5-6-invoke-semantics.md`, APPROVED
  in-session): slice 0 root cause — the "900s call never answered"
  incident was system sleep vs monotonic timers (harness MCP log +
  pmset evidence, recorded in the plan), no code defect; slice 1 neutral
  `[stderr]` by invocation provenance; slice 2 total wall-clock budget
  (queue + preflight + execution, sleep-safe deadline re-checks, fast
  queue expiry without executing); slice 3 never-queueing ptk_state.
  Plan loop i56p-1..11 CLOSED CONVERGED the same day. Also banked:
  codex calls ptk_invoke from inside its read-only sandbox (issue-3
  permission-surface datum). Process note (owner, in-session): approval
  requests now go to the owner as ≤50-word plain-English summaries; the
  broader plan-length fix will come via the governance toolkit, not this
  repo.
- **shell-dialect plan COMPLETE 2026-07-10**
  (`.agents/plans/shell-dialect.md`; decision entry + the sd1-4 and
  sd3-1 amendments in `.agents/decisions.md`). All slices done and
  codex-loop-closed: slice 0 (probes frozen), slice 1 (detector;
  sd1-1..7, owner-ratified), slice 2 (server wiring `8c234e8`;
  sd2-1..6), slice 3 (raw posture `fa1b23c` + elision-hint redesign
  `0840d13`; sd3-1 owner-adjudicated, sd3-2..4 RESOLVED), slice 4 (D3
  texts `8bb96b1`; sd4-1..2). The plan's live end-to-end Verification
  pass ran over real MCP stdio against the built server: **11/11**
  (refusals verbatim on both paths, bash -lc recovery with compression,
  raw counter + log line positive and negative, rtk-absent seam).
  Battery as of `e576962`: dotnet 80/80, Pester 133 passed / 1 skipped
  (canonical counts), handshake PASSED. Loop records:
  `.agents/review/index.md`; findings: `.agents/review/findings/sd*-*.md`.
  Plan tail EXECUTED 2026-07-10: the owner pushed mid-session (remote
  `master` reached `8bb96b1`), so the approved fix references were
  posted — issue #3 got the item-1 comment (stays open for items 2-4),
  issue #4 got its comment and was CLOSED. Only the sd4-fix tail
  (`428ac82`, `e576962`, the loop-close records) remains local-ahead.
- **Owner decisions recorded 2026-07-09 (in-session, post-handoff):**
  (a) slice-1 convergence close RATIFIED (above); (b) the push
  happened — `master` == `origin/master` == remote HEAD at `c71ea70`,
  so the "29 commits local-ahead / push go" item is CLOSED; (c) issues
  **#5** and **#6** triaged **after current work** — finish shell-dialect
  slices 2-4 first, then take #5/#6 as the next batch (not parked, not
  preempting); (d) the release-plan hook-default question stays
  DELIBERATELY OPEN by owner choice ("decide later") — re-present it
  before release slice 4 (installers) starts; (e) owner approved posting
  fix references on issue #3 (item 1) and issue #4 — and closing #4 —
  but execution is DEFERRED until the fixing slices land and are pushed
  (per the plan's Verification section: #3 item 1 needs slice 2; #4
  needs slices 3-4). Do not post before then.
- GitHub status as of 2026-07-10: issues #1, #2, and #4 CLOSED (#4
  closed this date with its fix reference); #3 open with the item-1 fix
  comment posted (items 2-4 remain a candidate small follow-up batch;
  the MCP permission-bypass ask is its own future owner-gated plan);
  #5/#6 open, triaged after-current-work.
- Standing flags carried forward: the release-distribution plan's
  slice 3 (`release.yml`) is now unblocked (shell-dialect complete), and
  its hook-default decision must close before its slice 4
  (`.agents/plans/release-distribution.md`); the remote `ci/slice-2`
  branch was DELETED 2026-07-09 (owner go in-session) — flag retired;
  the machine-local dev-install note lives in `## Next`.

### From `## Next`

1. **Security (highest, owner-driven — but the owner is running his own
   outreach on the framing; do not pre-empt his consultation with a
   plan).** Useful agent work while that runs: verify the
   MCP-elicitation candidate as FACT-FINDING only (spec support; which
   harnesses render server-initiated prompts; headless failure mode — a
   prompt nobody renders fails OPEN and is worse than none). Report
   findings; do not draft a plan or a shape until the owner's
   consultation returns. Do NOT re-derive the policy file.
2. **rtk-routing plan:** owner approval + ratification of the rrp-15
   disposition (see `## Now`). No code until then.
3. Owner push go for the local-ahead tail (~30 commits).
4. Owner releases the decisions.md hold, then record: the rtk-routing
   direction and the security reframe.
- Owner push go for the local-ahead docs tail (shared-runspace idea
  plan + its two review loops' records + drift fixes; ask-first
  policy). The issue-5/6 approval/push bullet that lived here is DONE —
  approved, built, pushed, issues closed (see `## Now`); caught stale by
  the grok reviewloop pass 2026-07-10.
- Remaining owner decision: hook-default (blocks release slice 4 only;
  owner chose "decide later" 2026-07-09 — re-present before that slice).
- Machine-local (owner's Mac + Windows box): the installed `~/.ptk`
  payload and the ptk_init-written nudges/hook predate the whole
  shell-dialect plan — a dev-install re-run (+ ptk_init) is needed for
  live sessions to pick up slices 1-4 and the new texts.
- Slice-7 test matrix (proposed, not yet in the decision entry): (1) AD module
  native import inside ptk_invoke + warm reuse across calls; (2) build and HOLD an
  Exchange implicit-remoting session in the warm runspace, Get-Queue latency call
  1 vs call N; (3) EXO/Graph via unattended cert auth (plan constraint: no
  interactive Connect-*). Server knobs for these tests (Program.cs): per-call
  timeout default 300s (`PTK_CALL_TIMEOUT_SECONDS`), idle self-exit default 4h
  orphan backstop (`PTK_IDLE_EXIT_SECONDS`).
- ~~go/no-go test (~2026-07-20)~~ — DECIDED: unqualified GO 2026-07-08,
  ahead of the window (`docs/history/decisions-archive.md`); this bullet
  was stale (caught by the shared-runspace plan review, spr-1). The
  Windows-box real-usage evaluation intent lives on only as the slice-7
  test matrix above and the shared-host measured-pain criterion in
  `.agents/decisions.md`.
- ~~Interim security posture: keep ptk_invoke on ask-per-call in the
  harness; build the policy-file gate if blanket-allow pressure
  appears.~~ — SUPERSEDED 2026-07-11: the criterion fired AND the
  response was rejected. Current posture: ask-per-call remains the only
  control, the owner considers that insufficient (`## Now`), and the
  replacement shape is unresolved. The decision entry stays OPEN until
  the hold lifts.

### From `## Blockers`

- None. (Handoff re-verification note: the 2026-07-09 governance-refresh
  flag "`.agents/repo-map.json` / `.agents/artifact-manifest.json` are
  retired-but-locally-modified, remove by hand" is now stale — the tree
  is clean and both files are tracked unmodified; deleting them remains
  an owner option, not a blocker.)

## Rotated 2026-07-09 (handoff at d352e66)

### From `## Now`

- **2026-07-09 (night, latest): shell-dialect slice-1 codex re-grade
  round 1 RECORDED.** At head `acb0f39` (codex, Codex v0.144.0,
  gpt-5.6-sol, read-only): sd1-2 RESOLVED; sd1-1 and sd1-3 held NOT
  RESOLVED — each fix covered one half of its finding (ambient-only
  resolution; comment-only blanking). 4 new findings, every claim
  independently verified in-session before triage (repros re-run at
  head): sd1-6 (MEDIUM, space-filler blanking SYNTHESIZES bash shapes —
  new FP class, disproves sd1-3's "never an over-match" claim) and
  sd1-7 (LOW, error IDs pair with shape evidence globally) ADMITTED;
  sd1-5 DECLINED (miss inside sd1-3's recorded known gap); sd1-4
  CONTESTED (alias-shadowed `set` — contests the frozen slice-0 `set`
  exemption, OWNER CALL). Reviewer battery at head: Pester 119/1
  skipped, clean tree. FIX ROUND 2 LANDED same night: `bc5638d`
  (sd1-1 script-local definitions), `4e6a223` (sd1-3 Generic-fragment
  blanking), `5f4b3fa` (sd1-6 non-bridging blank filler), `ef9f3ed`
  (sd1-7 error/evidence locality) — one commit + red/green guard each;
  battery at head: Pester 123/1 skipped, dotnet 59/59. RE-GRADE ROUND 2
  (head `293eda6`): sd1-6 RESOLVED; sd1-1/sd1-3/sd1-7 held on residual
  tails (no new IDs; every tail master-verified at head). FIX ROUND 3
  LANDED: `9b5e326` (sd1-1 Set-Alias tracking + lexical ordering),
  `20ba7fd` (sd1-3 escape-aware fragments), `f30ddde` (sd1-7
  command-position keywords). Battery: Pester 127/1 skipped, dotnet
  59/59. RE-GRADE ROUND 3 (head `374666b`): all three held on strictly
  more crafted tails (no new IDs, third consecutive round; all
  master-verified). FIX ROUND 4 LANDED: `eb5e193` (recursion counts),
  `f5229a7` (fragments span newlines), `0c43b05` (defined/resolving
  keywords are not evidence). **LOOP CLOSED CONVERGED** per the ccc9686
  contrived-tail precedent — every named repro across four rounds is
  guarded. **Canonical counts: Pester 130 (+1 skip), dotnet 59.**
  SLICE 1 DONE. NEXT: slice 2 (server wiring — labeled refusal result on
  both execution paths). OWNER: (a) ~~sd1-4 adjudication~~ DONE — owner
  unparked in-session, fixed at `c43360c` (set -e flags only while set
  still means stock Set-Variable; Pester 132/1 skip, dotnet 59/59;
  decision amendment recorded), (b) ratify the convergence close or
  order re-grade round 4, (c) push go. Records:
  `.agents/review/index.md`, `.agents/review/findings/sd1-{1..7}.md`.
- **2026-07-09 (night): shell-dialect plan APPROVED — owner,
  in-session.** D1 = (a) refuse-fast with platform-aware guidance; D2 =
  non-breaking raw-posture subset; D3 = dialect line. The #4 comment's 4
  acceptance suggestions reconciled into D2 at approval (adopted:
  no-preemptive-raw rewording, `route=pwsh`+`raw=false` taught as "exact
  execution, shaped output", `ptk_state` raw telemetry; declined:
  reason/cost gate — attribution recorded in the plan). Decision entry
  in `.agents/decisions.md`. Slice 0 probes RAN same night, results
  frozen into the plan: the #3 repro does not reproduce on this build
  (the resolver never rtk-wraps a `&&` chain — pwsh leg, runs fine);
  detection list pinned at 12 constructs IN, trailing-`\` OUT
  (Windows-path false-positive risk); `bash -lc` recovery verified end
  to end (cwd anchor, `[exit] N`, `[ptk:log via rtk]` compression, both
  legs); D2/D3 wording baseline snapshotted in the plan. NEXT: slice 1
  (token-aware detector in the module), then 2-4 — one commit + battery
  + codex loop each. #5/#6 remain UNTRIAGED (owner call).
- **2026-07-09 (evening refresh): OWNER PUSHED master through
  `5e3cd70`; GitHub issues #1 and #2 CLOSED (~15:35Z). THREE NEW GitHub
  items since (all `roethlar`, ~19:28-19:33Z):** issue **#5** (ptk_invoke
  labels successful exit-0 native stderr as `[errors]` — medium; asks
  for a neutral stderr label, PowerShell Write-Error kept
  distinguishable, 4-case regression matrix), issue **#6**
  (`timeoutSeconds` excludes queue wait behind the serialized runspace —
  a 1s-budget call can wait arbitrarily and still run; `ptk_state`
  blocks behind the busy runspace it should diagnose — medium; asks for
  wall-clock budget semantics + prompt busy/active-call-age/waiter-count
  reporting), and a **comment on issue #4**: cross-model confirmation
  (GPT-5.6-Sol/Codex governance audit, 2026-07-09) of the
  `raw=true`-as-habit problem — raw set preemptively on most inspection
  calls, bought nothing (README byte-identical raw vs shaped; 86%/95.6%
  reduction forfeited) — with 4 acceptance suggestions (no preemptive
  raw; teach route=pwsh+raw=false as "exact execution, shaped output";
  reason/cost gate on unjustified raw; raw-usage telemetry in
  ptk_state). **#5/#6 are UNTRIAGED — no plan, no code; owner
  prioritization needed.** The #4 comment bears directly on the DRAFT
  shell-dialect plan's raw-posture leg — reconcile before approval.
  Local state: working tree clean, 4 unpushed commits, all
  shell-dialect plan text (`3227607` draft, sources = issue #3 item 1 +
  issue #4; `1d7f38b` sd-1..sd-10 fixes; `13599a6` sd2-1..sd2-5;
  `809e0d0` sd3-1). Plan status: DRAFT awaiting owner approval; slice 0
  (probes) runs first; no code before approval.
- **2026-07-09: slices 3-6 review loop CLOSED (converged).**
  Re-grade round 1 (codex read-only, head `3ec608b`) cleared mhi-9 and
  mhi-11 and held mhi-10; round 2 (head `d58be68`, base `3ec608b`)
  graded the mhi-10 completion and mhi-12 RESOLVED, guard_confirmed,
  NO NEW FINDINGS (codex-cli 0.142.5). Verdicts recorded in
  `.agents/review/findings/mhi-{9,10,11,12}.md`; index updated. ~~All
  slice 3-6 + review-loop commits remain unpushed pending the owner's
  master push go.~~ RESOLVED same day: owner pushed master through
  `5e3cd70` (origin confirmed at that head, 2026-07-09 evening).
- **2026-07-09: slices 3-6 review loop — fixes landed; re-grade closed
  (see latest bullet).** Codex loop over `86b51ae..6134a2f` produced
  mhi-9/10/11 (fixed: `ce0caf2`, `fa3620a`, `6c1d025`); re-grade round 1
  held mhi-10 NOT RESOLVED — completion `e8363f3` (no-CLI codex uninstall
  now reads the config and names the manual removal). mhi-12 self-found
  live (HIGH): `codex mcp remove ptk` orphans `[mcp_servers.ptk.tools.*]`
  subtables and bricks the codex CLI — this box's config repaired
  in-session; sweep fix `9d00c6e`. **Canonical counts: Pester 85, dotnet
  59.** Master is local-ahead of origin; push is the owner's call. NEXT:
  codex re-grade round 2 at `e8363f3` (mhi-10 completion + mhi-12), then
  record the verdicts.
- **2026-07-09 (latest): revert miscommunication resolved; end-state
  build resumed.** The harness-file writes stay undone; the
  nudge-standard-layer script change (60cd9f3) is RESTORED by reverting
  82b8c51. Operative decisions live in the plan's two 2026-07-09
  amendments (`.agents/plans/multi-harness-init.md`): nudge is a
  standard layer, machine changes only through the complete owner-run
  install process, slice 5 = default dev-install chaining, agy hook
  deferred, no live installer runs during development. Building slices
  3-6 to completion. Owner pushed through a0dceb8; everything after is
  local. **Canonical counts: Pester 75, dotnet 59.**
- **2026-07-09 (latest): GITHUB ISSUE #2 FIXED and codex-closed** (plan:
  `.agents/plans/issue-2-stale-hook-registration.md`; owner mid-session
  go). ptk_init's claude leg registers the INSTALLED hook copy
  (`~/.ptk/scripts/ptk-hook.ps1`) so checkout moves cannot strand
  registrations; `-Show` flags STALE entries (missing file OR directory);
  installs name what they replaced; dev-install refreshes an existing
  hook entry when it also registered the server. Loop: i2-1 (MEDIUM,
  consent via parsed PreToolUse entry, 665d99a), i2-2 (LOW docs,
  ea95d48), i2-3 (LOW -PathType Leaf, 69a2b13) — all re-graded RESOLVED.
  **This box's live stale entry (src\ path — fail-open since unknown) was
  HEALED; hook effective next Claude Code session.** NOTE: ~/.ptk's
  payload is the owner's 2026-07-08 install — a dev-install re-run picks
  up the new hook text (neutral naming + liveness) and installer.
  **Canonical counts: Pester 75, dotnet 59.** ~~Comment/close issues
  #1+#2 after the owner pushes.~~ DONE 2026-07-09: both CLOSED on
  GitHub (~15:35Z). NEXT: multi-harness slice 3 (grok leg).
- **2026-07-09 (later): GITHUB ISSUE #1 FIXED and codex-closed** (plan:
  `.agents/plans/issue-1-mixed-stream-shaping.md`, owner-approved "go";
  taken ahead of multi-harness slice 3 by the same go). Shipped, one
  commit each: caa7714 (string-bearing mixed streams render as text —
  the repro class), 66a53df (heterogeneous header + ToString samples),
  c2d8a4a (i1-1: header type-list bounded), 86f990e (Select-Object
  -First stamps Selected.* into live TypeNames — generic path de-mutated,
  self-caught), d3f4569 (i1-3: MaxItems 0 wraparound), 1e1ab99 (i1-2:
  Select-PtcFirst helper, no object-row Select-Object anywhere — the
  deserialized/remoting exposure). Loop record: `.agents/review/index.md`
  (two rounds, final NO NEW FINDINGS). **Canonical counts: Pester 71,
  dotnet 59; handshake PASSED.** Comment/close issue #1 after the owner
  pushes. NEXT (owner instruction mid-session): GitHub issue #2 — stale
  global hook registration fails open silently — then multi-harness
  slice 3 (grok leg).
- **2026-07-09: CONCURRENT GOVERNANCE REFRESH interleaved with the
  slice-2 build session** (602ee45/03d9162/719c200/bd6ff02, toolkit
  ce0db15, owner identity, ~00:17-00:18): AGENTS.md + skills + CLAUDE.md
  + repo .claude/settings.json refreshed mid-build. bd6ff02 ("reset
  governance") swept one in-flight, comment-only `scripts/ptk_init.ps1`
  edit from the build session's working tree into its commit — content
  verified intact at HEAD (Pester 66/66 at 3caa78f; no governance file
  touched by the build commits, no build file damaged by the refresh).
  REFRESH FLAGS awaiting owner: `.agents/repo-map.json` and
  `.agents/artifact-manifest.json` are retired-but-locally-modified
  ("remove by hand if intended"). Process hazard worth remembering: a
  refresh that commits working-tree sweeps must not run while a build
  session has in-flight edits — a guard-proof sabotage state could get
  committed under a refresh message.
- **2026-07-09: MULTI-HARNESS SLICE 2 EXECUTED — codex leg.**
  `ptk_init.ps1 -Agent codex`: idempotent registration (existing entry
  left as-is via `codex mcp get`; else `codex mcp add ptk -- <installed
  exe>`, payload-gated), nudge block in `~/.codex/AGENTS.md`
  (`-NudgePath` now binds to the single selected leg). LIVE ON THIS BOX:
  real run hit the already-registered short-circuit, nudge installed
  after the owner's `@RTK.md` include, and a fresh `codex exec` quoted
  the block verbatim — codex nudge home VERIFIED
  (`docs/harness-support.md`). Codex loop on 7a068b9 CLOSED 2026-07-09:
  mhi-8 (MEDIUM — registration probe now runs before the payload gate,
  preserving leave-as-is for existing/custom entries; guard test uses a
  fake codex shim, 3caa78f) fixed and re-graded RESOLVED, no new
  findings. **Canonical counts: Pester 66, dotnet 59.** Details in the
  plan. NEXT: slice 3 (grok leg).
- **2026-07-08 (later night): MULTI-HARNESS SLICE 1 EXECUTED — installer
  framework + Claude leg.** `ptk_init.ps1` is the per-agent framework
  (`-Agent claude|codex|grok|agy|all`, detected-set default, stub legs for
  2-4), user-level by default with loud flip note, `-Local` warned opt-in,
  `-Nudge` block in `~/.claude/CLAUDE.md`, registration gate (refuses the
  hook without a `~/.ptk` payload), harness-neutral hook text, and the
  mhi-2 liveness check (down-server wording in the deny; wording only).
  Details + in-slice decisions in the plan
  (`.agents/plans/multi-harness-init.md`). **Canonical counts now: Pester
  62, dotnet 59** (11 new Pester tests, all guard-proven). README +
  server/README hook sections updated. OWNER NOTE: the flip means a bare
  `ptk_init.ps1` run now patches `~/.claude/settings.json`, and
  `dev-install.ps1 -Hook` prints a Claude-leg-only note. Codex loop on
  057a5ee CLOSED 2026-07-09: mhi-6 (MEDIUM, dev-install -Hook now gated
  on actual registration, 1e06351) and mhi-7 (LOW, surgical byte-exact
  nudge strip, ec6e094) both fixed and re-graded RESOLVED, no new
  findings (`.agents/review/index.md`). NEXT: slice 2 (codex leg).
- **2026-07-08 (night): v2 LIVE ON THIS BOX; MULTI-HARNESS PLAN APPROVED;
  SLICE 0 EXECUTED — ALL PROBES GREEN.** Owner completed dev-install +
  global hook + push; the session's own tool calls then hit the redirect
  and re-issued via ptk_invoke (both matchers verified live). Fresh
  headless Claude session: deny quoted verbatim → unprompted re-issue →
  task done — the standing hooked-check gate is CLOSED. grok and agy
  registered and live-verified (grok: ptk__ naming, no Claude-hook
  spillover, nudge home = ~/.claude/CLAUDE.md VERIFIED by marker probe;
  agy: mcp_config.json entry, unprefixed names, headless auth worked);
  codex entry was missing, re-added, tools list, headless auto-deny
  re-confirmed (interactive is codex's path). Durable table:
  `docs/harness-support.md`. Slice-0 probes ran THROUGH ptk background
  jobs. NEXT: slice 1 (installer framework + Claude leg), codex loop per
  slice. **Dogfood findings for the backlog:** (1) mixed string/object
  streams hit the generic table and LOSE the string lines (twice in live
  use — real shaping gap); (2) same class: string+MatchInfo mix rendered
  a Length-only table.
- **2026-07-08 (evening): SETUP DOCS UPDATED; MULTI-HARNESS INIT PLAN
  DRAFTED, codex-closed, AWAITING owner approval + the manual agy
  interview.** `.agents/plans/multi-harness-init.md`: per-harness
  registration/enforcement/nudge legs modeled on rtk init; evidence
  frozen (codex/grok CLI surfaces verified, self-reports marked as
  probe targets; agy headless interview failed — owner is asking it
  interactively with a prompt that demands VERIFIED vs RECALLED
  labeling). Slice 0 = live probes, headlined by the STANDING GATE both
  repos want: the live Claude hooked deny-and-reissue check. README/
  server-README now carry the cross-repo findings (global-first hook,
  content-tracking warning, truthful hook failure semantics — a down
  server still denies, PTK_DIRECT escapes; liveness-aware hook is a
  recorded slice-1 candidate). Review loop mhi-1..5 closed
  (`.agents/review/index.md`; two HIGH docs claims corrected). Commits
  local from 419503c onward; push owner-gated as always.
  in-session).** ptk continues as an active product; the continuation-gate
  entry is archived (`docs/history/decisions-archive.md`), the
  destructive-cmdlet parking survives as its own Open entry, and
  warm-runspace slice 7 (AD/EMS/EXO validation) plus greenfield D5
  (CLI-face retirement) are unblocked by the gate's closure. The shared
  multi-client host stays parked on its own measured-pain criterion (not
  part of this go).
- **2026-07-08 (post-GO build): v2-FEEDBACK FIXES + D5 RETIREMENT BUILT
  and codex-closed** (`.agents/plans/v2-feedback-fixes.md`; loop record
  in `.agents/review/index.md`). What shipped:
  - **Slice 1 (56b1af3): the os-error-6 class is FIXED.** Probe-diagnosed
    root cause: ChildStdinGuard's NUL handle was NON-INHERITABLE
    (File.OpenHandle default), so children got a stdin handle value
    absent from their handle table — rustup shims (cargo/rustc/codex)
    died duplicating it. SetHandleInformation(HANDLE_FLAG_INHERIT) fixes
    it; live-verified: cargo/rustc work on route=pwsh, auto, raw, jobs.
    The e2e now asserts stdin-reading natives SUCCEED (the missing
    assertion that hid the bug) and spawns CreateNoWindow.
  - **Slice 2 (9cc74de): UTF-8 native output decoding** — Console
    OutputEncoding pinned BOM-less UTF-8 in RunspaceHost (mojibake class
    dead; OEM-emitting tools now mojibake instead, escape hatch = job
    logs, NOT raw=true).
  - **Slice 3 (4f957ab): timeout message warns resolution can differ
    after recycle and points at ptk_state; rtk install nag filtered at
    error collection (specific banner only); stderr-swallow report
    probed NULL** (both routes return identical real stderr — details
    frozen in the plan).
  - **Slice 4 / greenfield D5 (bfc6323): CLI face RETIRED.** Module =
    server shaping library (exports: Compress-PtcObject,
    Compress-PtcOutput, Resolve-PtcInvokeScript; 1374→622 lines);
    ptk.ps1, docs/usage.md, CLI tests and fixtures removed; README
    single-surface story + setup corrected (d5-1: .mcp.json is empty,
    dev-install or explicit registration are the paths).
  - Codex loops: v2fb-1, v2fb-2, d5-1 (all LOW) fixed one commit each;
    v2fb re-grades RESOLVED; loop closed converged.
  - **Canonical counts now: Pester 51, dotnet 59.** Owner pushed master
    through d881d37 on 2026-07-08 and CI run 28985350456 is GREEN on all
    three OSes at that head. The installed 0.2.0 binary still serves the
    OLD surface — stop the ~/.ptk servers, rerun scripts/dev-install.ps1
    (it refuses while one runs, by design), then /mcp reconnect to go
    live on v2. Owner fixed the codex config (now points at the
    installed binary), closing the repo-bin exe-lock annoyance.
- **2026-07-08 (after the build): SECOND LIVE-USE FEEDBACK BATCH recorded
  (owner-shared notes from heavy real use of the CURRENT installed v1,
  `F:\notes\PTK\vela_session_notes.md` — machine-local, essentials
  captured here). Assessment against the just-built v2, candidate work
  awaiting owner prioritization (no code authorized):**
  1. **HIGH — "The handle is invalid (os error 6)" for rustup-shimmed
     binaries (cargo, rustc, codex) via route=auto; the session's single
     biggest time sink.** Workaround used live: `cmd /c "... < nul"` under
     route=pwsh. Code fact: `ChildStdinGuard` NUL-backs STDIN only
     (handle -10); stdout/stderr (-11/-12) of the console-less server
     were never guarded, so console-handle-probing shims can still hit
     invalid handles. NEEDS A PROBE on this box (which handle, foreground
     vs rtk-routed vs jobs — job children get a closed-pipe stdin, not
     NUL) before any fix. This failure class is invisible to the current
     test suite.
  2. **HIGH value/effort — mojibake in native output (`ΓÇö` for em-dash):
     OEM-codepage (cp437/850) vs UTF-8 mismatch on the capture side.**
     Candidate: pin `[Console]::OutputEncoding`/native decoding to UTF-8
     in the server/runspace. Pollutes all native tool output today.
  3. **Timeout recycle surprised live use with changed command/PATH
     resolution in the fresh runspace.** v2 already improves this
     (ptk_state drift; teach-at-timeout), and env restore was
     deliberately reset-only — but the timeout message should also say
     command resolution may differ and point at ptk_state. Small.
  4. **Minor:** rtk's "no hook installed" nag rides along in routed
     output (candidate strip); route=auto sometimes swallowed a failed
     native's stderr where route=pwsh showed it (probe).
  5. **Validation, not work:** the note's #3 ("long work has no story",
     pattern hand-rolled 6-7 times) is exactly what D3 built; warm-state
     reliability and shaping fidelity got explicit praise.
- **2026-07-08 (latest): GREENFIELD SLICES D1/D2/D4/D3 BUILT and
  codex-closed.** `.agents/plans/greenfield-design.md` (approved same day,
  adoption entry in `.agents/decisions.md`) executed in full except D5
  (CLI-face retirement — deferred until after the go/no-go window by the
  plan's own call). What shipped:
  - **D1** (dfcc4f0): ANSI/control sequences stripped at text ingest in
    `Compress-PtcOutput`, before log-shape classification.
  - **D2** (c573a08): every text leg bounded — `Limit-PtcPassthrough`,
    400 lines / 40 KB head+tail with explicit elision markers naming
    raw=true; the old never-truncate contract test reconciled under the
    adoption decision.
  - **D4** (247fe72): `ptk_state` (engine/PID/uptime/cwd/modules/jobs +
    env DRIFT vs post-priming baseline, PATH as entry diff, variable
    count; `listAvailable` cached only on clean probes) SUBSUMES
    `ptk_modules` + `ptk_ping`, which are REMOVED. `ptk_reset` now
    restores the process environment to its server-start baseline
    (factory-state semantics; timeout recycles deliberately do not).
  - **D3** (d3efc2d): background jobs — `ptk_invoke background=true`
    (child pwsh, self-redirected log under `~/.ptk/jobs/`, session cwd,
    -ExecutionPolicy Bypass, parse errors land in the log, exit 64),
    `ptk_job` (status/output/kill/list; shaped bounded offset-paged
    polls), per-call `timeoutSeconds` capped by new
    `PTK_MAX_CALL_TIMEOUT_SECONDS` (default 3600), teach-at-timeout
    error naming both recovery paths, reset/graceful-shutdown kill jobs.
  - Codex loops: 7 findings (d1-1, d2-1, d2-2, d4-1(+b), d3-1..d3-3 —
    two MEDIUM), all fixed one commit each with guard proofs, final
    re-grade RESOLVED x4 / NO NEW FINDINGS (`.agents/review/index.md`).
  - **Canonical counts now: Pester 76, dotnet 57.** Handshake asserts the
    four-tool surface (ptk_invoke, ptk_job, ptk_state, ptk_reset) and
    calls ptk_state instead of the removed ptk_ping.
  - **Tool-surface break, owner action on next release/install:** the
    installed 0.2.0 binary still serves the old tools; a rebuild/
    dev-install is needed for the new surface. CI ci.yml runs the same
    battery unchanged.
  - **Environment finding (owner action):** `~/.codex/config.toml` still
    registers ptk as `dotnet run` on this repo — every `codex exec`
    (including this session's review loops) spawns/builds it and can
    leave a repo-bin `PtkMcpServer.exe` running, locking the build. The
    Claude Code registration already points at `~/.ptk/bin`; recommend
    updating codex's to match. Recovery that worked all session:
    `Get-Process PtkMcpServer | Where Path -like '*Powershell-Token-Killer*' | Stop-Process`.
  - All commits local/unpushed (master push stays owner-gated).
  Release-plan slice 3 unaffected, still queued. D5 execution note for
  the future session: closes `ptk` CLI verbs, `docs/usage.md`, their
  tests; README single-surface rewrite.
- **2026-07-08 (later): release-plan SLICE 2 DONE and codex-closed.**
  `.github/workflows/ci.yml` landed on master (74a2604): ubuntu/windows/
  macos matrix, current action majors (checkout@v7, setup-dotnet@v5),
  Pester `-CI` + `dotnet test` + default-mode handshake. Its first run
  caught a REAL product bug: the hosted runspace honors Windows execution
  policy and the server never set one, so any Windows box with no policy
  configured (CI runners, fresh user installs) got the hosted default
  (Restricted), which blocked the module import and silently degraded
  shaping/routing to Out-String — owner boxes had always passed only
  because they have a policy configured; Linux/macOS unaffected (SMA
  hardcodes Unrestricted off-Windows). Owner-approved fix (dddbb6b, a
  recorded scope addition to the release plan): pin
  `InitialSessionState.ExecutionPolicy = Bypass` on Windows in
  `RunspaceHost.CreateRunspace` — rationale: ptk_invoke runs script text,
  it replaces a harness tool that itself runs `pwsh -ExecutionPolicy
  Bypass`, and ptk is not a security boundary. Guard proven: the new
  regression test (forces Restricted process policy) fails without the
  fix, 37/37 with it, including under the Restricted repro
  (`$env:PSExecutionPolicyPreference='Restricted'; dotnet test ...`).
  Server suite canonical count is now 37. CI green on all three OSes at
  the branch head (run 28971482704); codex loop closed NO FINDINGS first
  pass on both commits (`.agents/review/index.md`). Branch bookkeeping:
  commits were cherry-picked from `ci/slice-2` (831bcc3/30f283d) to
  master; local branch deleted; REMOTE `ci/slice-2` deletion was blocked
  by the harness permission classifier and awaits the owner confirming
  (`git push origin --delete ci/slice-2`) — the no-lingering-branches
  condition is not yet satisfied for the remote. Master push (now 7 local
  commits) stays owner-gated. Next: slice 3 (release workflow).
- **2026-07-08: Windows battery GREEN; dev-install verified on this box
  (handoff items 1-2).** Pester 70/70 (0 skipped — the shim test runs here,
  ls stays unrouted), dotnet test 36/36, handshake passes in all three modes
  (default, `-UseRegistrationCommand`, and `-ServerCommand` against
  `~\.ptk\bin\PtkMcpServer.exe` from a neutral cwd; installed binary reports
  0.2.0.0). dev-install had already been run on this box (VERSION
  `0.2.0-dev.g9ec73fe`): ARP entry present and `winget list` surfaces it
  (`ARP\User\X64\ptk`), user-scope registration live. NOT verified:
  `-Uninstall` round-trip and the elevated/running-server refusals.
  Notes: `claude mcp list` flagged ptk defined in BOTH user scope (installed
  exe) and project scope (`.mcp.json` dotnet run) with different endpoints —
  both servers were live. RESOLVED 2026-07-08: owner removed the
  project-scope registration and the emptied `.mcp.json` is committed; the
  installed user-scope binary is the endpoint, and a checkout needs
  `scripts/dev-install.ps1` to get a server (the Mac has no install after
  the slice-1 round-trip test). Both instances were killed to unblock
  `dotnet test` (precedented exe lock); `/mcp` respawned on the installed
  binary.
- **2026-07-08: ptk MCP server live-use feedback recorded.**
  After ~10 calls in a real session, the owner reported the MCP server was the
  right tool and that warm runspace/state persistence is the standout feature,
  but also the main isolation hazard: variables and `$env:PATH` persist across
  calls, including test shims such as a fake `npm` prepended to PATH. Long work
  should use the background process + redirected output + polling pattern so
  each MCP call stays under timeout and preserves the live server. Output shaping
  preserved useful signal, including compile warnings, final artifact lines, and
  stderr as `[errors]`; minor polish gap: raw ANSI color sequences from tools
  such as vite surfaced unfiltered. Native command routing through rtk was
  transparent. Treat this as adoption evidence and open feedback items.
- **HANDOFF 2026-07-04 (end of day): owner moving to the WINDOWS box for
  testing; master pushed through the handoff commit (explicit owner go).**
  Everything below in this entry's sibling bullets is the day's context;
  the Windows session starts with `git pull`, then:
  1. **Battery:** Pester suite (70 tests; the 1 Unix skip — the .cmd/.bat
     shim test — RUNS on Windows, and the new ls platform test takes its
     `$IsWindows` branch: `ls` must stay unrouted there), `dotnet test`
     (36/36 expected), handshake default + `-UseRegistrationCommand`.
  2. **dev-install on Windows (the paths this Mac could not verify):**
     `pwsh -File scripts/dev-install.ps1` → check the Add/Remove Programs
     entry appears (HKCU uninstall key; `winget list` should surface it),
     user-scope registration works, handshake `-ServerCommand
     "$HOME\.ptk\bin\PtkMcpServer.exe"` passes from a neutral cwd, then
     `-Uninstall` removes payload+registration+ARP and leaves user files.
     The install refuses elevated shells and refuses while a `~/.ptk`
     server is running (clear message with the PID) — both by design.
  3. **Owner items for the go/no-go window:** install the hook
     (`scripts/ptk_init.ps1 -Global` or `dev-install.ps1 -Hook`), run the
     live hooked check in a fresh session (Bash + PowerShell tool calls
     should come back denied with ptk guidance), start the friction log.
  4. **Next build item:** slice 2 (`.github/workflows/ci.yml`, three-OS
     matrix; iterate on a `ci/*` branch — granted scope, delete after —
     master push per-go). Slice 3 after. Hook-default decision still open,
     needed before slice 4.
- **2026-07-04 (latest): release-plan SLICE 1 DONE and codex-closed; slice
  0 fully closed (CI probe ran — see the plan's probe results).** Slice 1:
  module discovery flipped to binary-dir-first (35fd472 + dc26c30, guard
  tests prove both the order and the cwd-fallback through the real
  composition) and `scripts/dev-install.ps1` landed (10d4a1a + b11eb66 +
  719fd85): publish→`~/.ptk` install, user-scope registration
  (remove-then-add), Codex snippet, Windows ARP entry, `-Hook`,
  `-Uninstall`, `-LayoutOnly -OutputDir` for release CI. Process: 3-lens
  pre-commit subagent review (10 findings fixed in-tree) + codex loop
  (rel1-1 tag-version normalization, rel1-2 TOML escaping — both fixed,
  re-grade NO FINDINGS; `.agents/review/index.md`). Full battery green on
  this Mac: Pester 69/0/1, dotnet 36/36, handshake all modes, install/
  uninstall round-trip leaves the machine in its pre-test state
  (`~/.ptk` removed, no user-scope registration, settings.json md5
  unchanged). NOT verified here: Windows ARP paths and a live `-Hook`
  install (slice 7 / owner's Windows box). Next slice: 2 (CI workflow) —
  iterate on `ci/*` (granted scope), the workflow file itself lands on
  master locally; the master push stays owner-gated.
- **2026-07-04 (later): release-plan slice 0 is DONE except the CI probe.**
  `-ServerCommand` mode landed in `server/test-handshake.ps1` (a8553dc,
  pre-commit multi-lens review fixes folded in) and the osx-arm64 probe
  results are frozen into the plan (8161af2): 45 MB tar.gz asset (129 MB
  unpacked), apphost ad-hoc signed, curl download quarantine-free and runs
  clean, quarantined-fresh-copy contrast SIGKILLed + `spctl` rejected
  (this box), published binary removes the dotnet-run build check from
  session start, module loads position-independently from the canonical
  layout via the BaseDirectory upward probe, `claude mcp add`/`remove`
  syntax confirmed on Claude Code CLI 2.1.201. Codex review loop on the
  slice: see `.agents/review/index.md`. PUSH GO GRANTED 2026-07-04 for
  `ci/*` + `v0.2.0-rc.*`, with the owner's hard condition: NO branches
  may linger once the coding is done — the agent deletes every `ci/*`
  branch (local and remote) as soon as its facts/workflows land on
  master; the owner never has to handle branches. Probe branches fork
  from origin/master (last pushed commit), NOT local HEAD, so unpushed
  master work is not published through a side branch. `master` pushes and
  the final `v0.2.0` tag stay per-explicit-go.
- **2026-07-04 (later): master's Pester battery was RED on this Mac —
  pre-existing, NOT slice 0 — now FIXED (owner go same day).** Clean
  master had 65 passed / 3 failed / 1 skipped: two "read README"
  assertions expected `pwsh_token_compressor` in the LIVE README (removed
  by the a43897a docs-pass rewrite; broke on every platform), and "leaves
  aliases and cmdlets on the PowerShell path" asserted `ls` stays
  unrouted, which only holds on Windows — on macOS/Linux `ls` is the
  native Application and the resolver routes it to rtk BY DESIGN
  (owner-ratified 2026-07-04: ls IS the shell command where a native one
  exists; Get-ChildItem is the PowerShell way; models shouldn't lean on
  aliases). Fixes: 849081d (deterministic temp fixtures) and d0e34d6
  (gci for the cross-platform alias case + a new test pinning the ls
  platform split via $IsWindows). Codex loop: NO FINDINGS first pass.
  Suite on this Mac: 69 passed / 0 failed / 1 skipped (70 tests — the
  canonical count going forward; the earlier "69/69" phrasing predates
  the added test). Lesson recorded: the old 69/69 record had not
  reproduced on identical code — suite greenness was machine-dependent
  until these fixtures were made deterministic.
- **2026-07-04 (late): RELEASE-DISTRIBUTION PLAN APPROVED — next action is
  slice 0.** `.agents/plans/release-distribution.md`, approved by owner
  in-session 2026-07-04 after question resolution and a codex review loop on
  the plan text (3 LOW doc fixes, all accepted — `.agents/review/index.md`).
  All commits from 7494edf onward are UNPUSHED (push needs owner go), and
  the plan's requested standing push scope (`ci/*` + `v0.2.0-rc.*`) was NOT
  separately confirmed — ask explicitly before the first CI push. Slice 0 =
  local osx-arm64 publish probe, `test-handshake.ps1 -ServerCommand` mode,
  CI runner probe (needs that push go). Owner set a first public release
  target of **2026-07-25**: prebuilt self-contained per-RID binaries on
  GitHub Releases + `install.ps1`/`install.sh` one-liners (tier 3);
  publish-and-register script and .NET-tool packaging are dev-only. Decision
  amendment recorded in `.agents/decisions.md` (continuation entry). Owner
  resolved the plan's open questions in-session the same day: **5 RIDs**
  (win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64 — owner has
  hardware for all five), **v0.2.0**, **one `~/.ptk` home** (payload+config,
  every platform and install method, no `--dir`), **winget = eventual
  primary Windows path** (installer-type only; v0.2.0 builds readiness: ARP
  uninstall entry, binary-hostable install logic, binary-relative module
  discovery — probe-order flip is an approved scope addition). STILL OPEN
  (owner: "decision for later", must close before slice 4): the public
  installer's hook default — tension recorded in the plan's Resolutions.
  Resume point: formal plan approval + the scoped push go (`ci/*` branch,
  `v0.2.0-rc.*` tags), then slice 0 (local publish probe, handshake
  `-ServerCommand` mode, CI runner probe incl. ARM runners). No code before
  approval.
- **2026-07-04 (earlier): docs pass PUSHED through a43897a.** README now
  leads with the MCP server as the primary use and documents rtk routing
  (four shaping legs, install-rtk encouragement, credits); server/README
  introduces rtk with the in-runspace rewrite detail; usage.md documents the
  `[exit] N` and PTC_TEMP-concurrency facts of `Invoke-PtcRun`. App name
  corrected everywhere: **PowerShell Token Killer** (`ptk`), named after
  rtk (Rust Token Killer); `PwshTokenCompressor` is only the module's
  on-disk name (repo-guidance mission line aligned).
- **UNIFIED SHELL ROUTING: BUILT 2026-07-04** — all five slices of the
  approved plan are committed and verified, each through the codex review
  loop (details in the plan and the commit messages):
  - Slice 0 probe results are recorded in the plan (rtk fidelity, cwd,
    chains, the Windows `rtk ls` gap, hook mechanics, latency).
  - Slice 1 (aa0ff12 + fixes): `Resolve-PtcInvokeScript` rewrites a single
    bare native-Application command with constant args to run through rtk;
    everything else runs as PowerShell unchanged; `route=auto|pwsh|rtk`
    argument on ptk_invoke; `raw=true` skips routing. Codex loop closed NO
    FINDINGS after fixes: $Error pollution (twice — resolver and
    Get-PtcRtkCommand), .cmd/.bat shim exclusion, Unix test stub, NUL handle
    lifetime.
  - **Load-bearing discovery (0a31364): natives that read stdin hung forever
    over stdio.** PowerShell hands a native command with no pipeline input
    the process stdin — the idle-but-open MCP JSON-RPC pipe — so bare git
    (any MSYS binary, sort, ssh) blocked until session end. Predates
    routing; never seen because no one had run a bare MSYS binary through
    ptk_invoke over stdio (live checks used cmdlets; rtk-routed calls mask
    it by wiring their own child stdio). Fix: ChildStdinGuard captures the
    transport streams, then points process stdin at NUL so children inherit
    EOF. Regression e2e spawns the real server over idle pipes and runs a
    stdin-reading native (60s hang without the guard, ~600ms with).
  - Slices 2-3 (e8ff3d7, 0f84988 + fixes): ptk_invoke repositioned as the
    single shell tool; `scripts/ptk-hook.ps1` (PreToolUse deny-with-guidance
    redirect for Bash+PowerShell, PTK_DIRECT escape hatch, fail-open,
    cwd-anchoring advice with apostrophe escaping) and `scripts/ptk_init.ps1`
    (rtk-init-style installer: local default/-Global/-Show/-Uninstall/
    -DryRun, idempotent, preserves foreign hooks surgically). Codex loop:
    cwd drop (High), shared-entry deletion, param-description mismatch,
    apostrophe escaping — all fixed with guard proofs; final one-line escape
    fix declared converged (replicates a codex-cleared pattern).
  - Verified 2026-07-04 (final battery): Pester 69/69, dotnet 34/34, both
    handshake variants, live stdio spot-check with real rtk (`[ptk:log via
    rtk]` + `[exit] 7` together), live in-harness routed `git status`.
  - **NOT DONE / OWNER ACTIONS:** (a) the hook is NOT installed anywhere —
    install with `pwsh -File scripts/ptk_init.ps1 -Global` (next session
    start), then run the live hooked check: a Bash and a PowerShell tool
    call should come back denied with ptk guidance and the model should
    re-issue via ptk_invoke; start the friction log the amended go/no-go
    needs. (b) ~~28 local commits unpushed~~ RESOLVED 2026-07-04: owner
    pushed everything through a43897a; only the plan commit 7494edf remains
    unpushed (see the release-plan entry above). (c) `/mcp` restart to
    respawn the live server on the final build (the last live instance was
    killed for the final rebuild) — not verifiable from the 2026-07-04 docs
    session; check whether it happened before reading a quiet ptk day as
    non-adoption.
  - Process notes for the record: two review-fix tests rode along in
    earlier commits instead of their own (5202756 carried the shim-test
    skip; 58990b1 carried the shared-entry test) — content correct, history
    not rewritten. Claude Code auto-respawns the ptk server after kills and
    each /mcp reconnect can leave an extra instance; every server rebuild
    this session needed a `Stop-Process -Name PtkMcpServer` first.
    PROPOSED (no go yet): a shadow-copy launcher so builds never collide
    with live servers.
- (COMPLETE — superseded by the BUILT entry above; kept as the approval
  record) Unified shell routing was the active work item:
  Owner-approved plan `.agents/plans/unified-shell-routing.md` (2026-07-04):
  ptk becomes the single shell tool — PowerShell → warm runspace, ANY
  non-PowerShell command line → rtk unconditionally (rtk passes through what
  it doesn't filter), log-shaped output → rtk log (exists) — plus a PreToolUse
  redirect hook on the harness Bash AND PowerShell tools, shipped via a
  `ptk_init.ps1` installer mirroring `rtk init` semantics. Decision basis: the
  2026-07-04 amendment in `.agents/decisions.md` (go/no-go now evaluates this
  product with the hook installed; operative criterion is experienced benefit
  + owner not disabling the hook). All plan open questions are resolved; the
  next action is slice 0 (rtk fidelity + hook mechanics probe, which freezes
  the design). Process: codex review loop after each slice (owner-set,
  2026-07-04): `codex exec --sandbox read-only` reviews each commit, real
  findings get fixed one-commit-each with guard proofs, iterate to NO
  FINDINGS or convergence (contrived-only Lows). Verified rtk facts for slice
  0: rtk's own global hook is installed on this box (`~/.claude/settings.json`
  PreToolUse, matcher `Bash` only — the PowerShell tool is uncovered),
  `rtk hook claude` is a native binary reading tool-call JSON from stdin,
  `rtk init --show` / `--dry-run` are safe read-only probes.
- 2026-07-04: the round-2 review (two findings on a8d3d02..HEAD) was FIXED
  under the approved plan `.agents/plans/review-fixes-2026-07-round2.md` with
  the codex-review loop per slice: (1) High — `Invoke-PtcRtkLog` snapshots and
  restores the caller's `$LASTEXITCODE` around the native rtk leg (the rtk
  invocation was clobbering the user script's exit code before the server read
  it; snapshot the VALUE, not the live PSVariable) — commit a798094, codex:
  NO FINDINGS; (2) Medium — dispatch guards completed in three codex rounds:
  every route guards all properties its compressor dereferences (ae1b9d6),
  files must have a KNOWN Length (null value or missing → generic; only
  directories legitimately lack it) (f86da5a), and every item must match the
  route's type name — one genuine FileInfo can no longer drag look-alike
  shapes of other types onto the fs route (ccc9686). All fixes guard-proven
  (revert → predicted failure → restore). Verified 2026-07-04: Pester 49/49,
  dotnet 30/30, both handshake variants pass, and a live stdio spot-check
  against the real winget rtk shows a log-shaped `exit 7` script rendering
  BOTH `[ptk:log via rtk]` and `[exit] 7`. The final codex pass on ccc9686
  landed after the handoff commit: one Low, self-labeled adversarial
  (calculated property returning a non-numeric `Length`/`WorkingSet64` value
  passes the guards, and direct `Compress-PtcObject` throws on the numeric
  conversion; the server path is already contained by `Compress-PtcOutput`'s
  never-throw shape-error fallback). Per the convergence rule the loop is
  CONVERGED and closed with this finding consciously not fixed — value-TYPE
  validation of every guarded property is out of proportion for a display
  heuristic with a raw escape hatch. Reopen only if a real (non-crafted)
  stream ever hits it.
- NOTE (2026-07-04): the live ptk MCP server was killed again this session to
  unblock `dotnet test` (PID 7696 held the exe lock — same precedented
  recovery); the owner needs an `/mcp` restart to respawn it on the current
  build. ~~Local commits through efe94c1 UNPUSHED~~ RESOLVED: owner pushed
  through a43897a on 2026-07-04.
- Owner pushed the day's work (a0a4819..4f943ea, including Phase 2) to origin on
  2026-07-03; only docs commits after 4f943ea may be local. On the push, the remote
  reported the repo MOVED to `AlsoBeltrix/PowerShell-Token-Killer` (capital S — the
  URL already recorded in `.agents/repo-guidance.md`); owner updated the local
  `origin` URL to match the same day. Pester: 31/31 on the Mac (2026-07-02); 29/31
  on the Windows box (2026-07-03) — the 2 failures were a pre-existing test-fixture
  sensitivity, FIXED later the same day (see the review-fixes bullet below).
- 2026-07-03 (night): five findings from an external (GPT-5.5) review of the Phase 2
  build were verified and FIXED under the approved plan
  `.agents/plans/review-fixes-2026-07.md`, one commit per finding: (1) ptk_invoke
  now surfaces nonzero native exit codes as an `[exit] N` block (with the CLI
  path's stale-guard, reset-before/read-after, mirrored into the server);
  (2) `Compress-PtcObject` dispatches to a specialized compressor only when EVERY
  item passes its property check, so mixed streams (FileInfo + string) compress
  via the generic path instead of degrading to `[ptk:shape ERROR]` raw fallback;
  (3) caller cancellation (user Esc) is now distinguished from timeout — the
  pipeline is stopped with a 5s grace and the warm runspace SURVIVES a clean
  stop (only a truly wedged pipeline still recycles), and the error says
  "canceled", not "timeout"; (4) `-MaxItems` + `+N more` now apply to
  property-less rows (scalar streams); (5) the two repo-root-sensitive Pester
  tests use deterministic temp-dir fixtures. Verified: Pester 43/43 (first fully
  clean run on this box), dotnet test 29/29, both handshake variants pass, plus
  an end-to-end stdio check of `[exit] 7` + stale-guard; guard proofs done for
  every behavior fix. Warm round-trip re-measured with the exit-code
  bookkeeping: avg 1.7 ms / max 3.5 ms over 20 stdio calls — no regression vs
  the ~3 ms baseline. Note: the live ptk servers were killed to unblock the
  rebuild (the exe was file-locked), so the session's MCP tools are down until
  an `/mcp` restart, which will respawn on the fixed build; a live Esc-abort
  spot-check in a real session is the one check not run headlessly (the
  mechanism is unit-tested).
- A 2026-06-27 design session explored a "universal PowerShell wrapper" rearchitecture
  (triggered by `ptk Get-ChildItem` printing help instead of running). No product code
  was written; the owner deferred the build decision. Recorded as an Open decision
  (b1e0550, docs-only). See `.agents/decisions.md`.
- A follow-on 2026-06-27 exploration looked at giving ptk a session-persistent
  warm-runspace backend (a stdio MCP server owning a `Runspace` that loads heavy
  modules / authenticated connections once). Recorded as a second Open decision in
  `.agents/decisions.md`. The core requirement is warm module load with no reload
  tax; unattended (cert-based) auth is the pattern for connection-bearing modules
  like EXO, not itself the requirement (owner correction 2026-07-02).
- 2026-07-02: owner selected the warm-runspace MCP server as the active work item and
  approved `.agents/plans/warm-runspace-mcp-server.md`. Slices 1-6 are built and
  verified: `server/` holds a net10.0 stdio MCP server (ModelContextProtocol 1.4.0 +
  Microsoft.PowerShell.SDK 7.6.3) with ptk_ping / ptk_invoke / ptk_modules /
  ptk_reset over a single warm runspace (serialized calls, timeout recycle, idle
  self-exit), registered in `.mcp.json` as `dotnet run -v q --project
  server/PtkMcpServer` (verified byte-clean stdout on cold build).
- Measured reload tax on this machine (Pester as the heavy module): cold per-call
  pwsh ≈ 460-500 ms every call; warm server pays 402 ms once, then re-import and
  module use round-trip at ~3 ms per tool call.
- Owner intent that frames future work: ptk is a personal/team tool complementing the
  owner's `headroom` PoC on Windows/PowerShell work, not an org-wide tool. The build
  trigger is measured benefit on real daily Windows usage, not faith. See
  `.agents/repo-guidance.md` for the generalized framing.
- 2026-07-02: governance refreshed from the AgentGovernanceBootstrap toolkit
  (`AGENTS.md` reconciled to the current template; repo-specific content carved into
  the new `.agents/repo-guidance.md` and `.agents/push-policy.md`).

- 2026-07-02 (late): owner stepped back and PAUSED all further building pending a
  go/no-go test. Evidence: headroom stopped (its context rewrites caused prompt-cache
  re-billing, net negative); rtk not adopted reliably via AGENTS.md instructions.
  Full reasoning and the test definition live in `.agents/decisions.md` ("Whether ptk
  continues at all"). Phase 2 compression, the universal wrapper, and the
  destructive-cmdlet gate are all behind that gate. No new plans until the test runs.
- 2026-07-03: owner UNPAUSED Phase 2 compression (amendment recorded in the
  continuation decision entry): build compression on ptk_invoke before the go/no-go
  so the test evaluates the full product. Scope: objects → Compress-PtcObject;
  log-shaped text → rtk when an rtk binary is present; all other text full
  passthrough; ollama leg dropped. ACTIVE plan (approved 2026-07-03):
  `.agents/plans/phase2-compression.md`. The universal wrapper and the
  destructive-cmdlet gate stay paused. Owner back at work ~2026-07-20 (was ~07-16).
- 2026-07-03 (later): Phase 2 slices 0-2 BUILT and committed. Module:
  `Compress-PtcOutput` (objects → Compress-PtcObject; log-shaped text → rtk with
  labeled raw fallback; all other text verbatim passthrough, never truncated;
  never-throw contract). Server: every runspace (create/reset/recycle) is primed
  with the module (`PTK_MODULE_PATH` override, else upward probe; import failure →
  stderr log + Out-String fallback) and ptk_invoke output is shaped unless the new
  `raw=true` argument is set. Verified: Pester 38 passing (+ the 2 known
  repo-root-fixture failures), dotnet test 25/25, both handshake variants pass;
  guard proofs done for module and server tests. Measured savings (chars/4
  estimate, TOOL-REPORTED — explicitly not the go/no-go benefit metric):
  Get-Process 2096→492 tok (76.5%), Get-Service 3689→861 (76.7%), Get-ChildItem
  -Recurse repo 47139→530 (98.9%), Get-Command 1737→1034 (40.5%); README.md text
  passthrough byte-identical. Object savings are summary-by-truncation (top-N +
  count) by design; `raw=true` is the escape hatch.
- Slice 3 (rtk binary) DONE 2026-07-03: owner installed rtk 0.43.0 via winget
  (resolves to `%LOCALAPPDATA%\Microsoft\WinGet\Links\rtk.exe` on the user PATH;
  an earlier agent-downloaded copy was refused by the permission classifier and
  discarded — owner install superseded it). Verified: `rtk log <file>` matches the
  interface the module uses (dedup summary + errors/warnings); rtk's "no hook
  installed" nag goes to stderr, which `Invoke-PtcRtkLog` already suppresses; the
  module log leg verified end-to-end with real rtk (`[ptk:log via rtk]` output).
  The SERVER discovers rtk via `Get-Command rtk` on its own PATH — true once
  Claude Code restarts from an environment that has the post-install user PATH;
  if it ever isn't, the leg degrades to its labeled raw fallback (visible, not
  silent), and `PTK_RTK_PATH` is the explicit override.
- Failure-mode drill result (observed during the rebuild): killing the server
  process mid-session bricks the ptk tools for the rest of the session — the
  harness marks them disconnected and does NOT respawn the server; only a session
  restart brings them back. Interpretation rule for the go/no-go weeks: a quiet
  ptk day needs a server-alive check before it is read as non-adoption. This
  session's live server was killed for the rebuild; the owner's `/mcp` restart
  respawned it on the new build without a full session restart (a second, gentler
  recovery path worth remembering alongside the drill finding above).
- 2026-07-03 (evening): Phase 2 LIVE CHECK PASSED in a real Claude Code session
  on the new build: objects → compact `fs:` summary; strings → verbatim
  passthrough; log-shaped output → `[ptk:log via rtk]` through the real winget
  rtk (the live server's PATH sees it — no PTK_RTK_PATH override needed);
  `raw=true` → plain Out-String table. Phase 2 is fully operational end to end.

- 2026-07-03 session findings (recorded here so they survive without chat):
  - PowerShell 5.1 compatibility, measured on this box: the module BODY imports and
    runs under Windows PowerShell 5.1.26100 — zero parse errors; fs, text, and
    MatchInfo paths work; only the process-object path fails ("Exception getting
    CPU": 5.1's ETS computes CPU from TotalProcessorTime, which throws on protected
    processes where 7 returns null). The manifest floor (`PowerShellVersion = '7.2'`)
    is the only import gate. So a 5.1 CLI backport is bounded (lower floor + one
    defensive access + dual-engine test runs; Pester 5.8.0 already sits in this
    box's 5.1 user scope). A 5.1 WARM SUBSTRATE would be new architecture — no
    NuGet package embeds 5.1; it would need a .NET Framework host or a persistent
    powershell.exe child. Parked, and capped by the decom horizon below. Note: a
    self-contained publish of the existing server xcopy-deploys to boxes with no
    .NET installed (engine is still 7.6).
  - Module-compat map for the go/no-go environment: on-prem Exchange management
    tools/EMS are 5.1-only through 2019/SE and will never load in the 7.6 runspace
    (owner-confirmed); the viable route is implicit remoting held inside the warm
    runspace (`New-PSSession -ConfigurationName Microsoft.Exchange` +
    `Import-PSSession` — plain WSMan/Kerberos, unofficial on 7, UNTESTED in this
    environment and the single most valuable slice-7 check). ActiveDirectory module:
    assumed PS7-native with current RSAT — unverified, no RSAT on this box. EXO and
    MSGraph: 7-native (owner-confirmed).
  - Decom horizon (owner, 2026-07-03): Exchange 2019 decommissions next year; the
    on-prem need lasts roughly 6-12 more months. This caps any 5.1-specific build —
    post-decom the workload is EXO+Graph, fully 7-native.


### From `## Next`

- **NEXT ACTION (2026-07-10): shell-dialect slice 2 — server wiring.**
  Slice 1 (detector) is DONE and its codex loop CLOSED CONVERGED
  (`.agents/review/index.md`). Slices 2-4 land one commit + battery +
  codex loop each.
- Review loop: shell-dialect slice-1 loop CLOSED CONVERGED 2026-07-09
  after four rounds (`.agents/review/index.md`); sd1-4 owner-unparked
  and fixed (`c43360c`) — all seven sd1 findings closed.
  Owner decisions still open: (a) prioritize new issues **#5**
  (`[errors]` mislabels exit-0 native stderr) and **#6** (queue-wait
  excluded from `timeoutSeconds`; `ptk_state` blocks behind a busy
  runspace) — both medium, untriaged, adjacent to but outside the
  shell-dialect plan's scope, (b) push go for the local commits
  (`3227607..809e0d0` plus the approval-recording commit).
- Slice 2 DONE (top Now entry). Next: slice 3 (release workflow,
  `.github/workflows/release.yml` on `v*` tags — per-RID publish on native
  runners, draft release; iterate on `ci/*`, granted scope). Still needed
  along the way: the hook-default decision before slice 4. Loose end from
  slice 2: the REMOTE `ci/slice-2` branch still exists pending owner
  confirmation of the delete. ~~test fixes~~ DONE 2026-07-04
  (owner go; battery green). The other questions (RIDs, version, install
  root, winget posture) are RESOLVED — do not re-raise them.
- ~~Execute unified-shell-routing slices~~ DONE 2026-07-04 (see Now). Next
  actions are the OWNER items in the routing entry: install the hook
  (`scripts/ptk_init.ps1 -Global`), run the live hooked check in a fresh
  session, start the friction log, push on go, `/mcp` restart.
- ~~Codex verdict on ccc9686~~ CLOSED 2026-07-04 (converged; disposition
  recorded in the round-2 entry above).
- 2026-07-02 headless adoption dry-run on this Mac (Sonnet, 19 trials, neutral cwd,
  ptk pre-approved via --allowedTools, tasks PowerShell-shaped but never mentioning
  ptk): **0/13 unprompted ptk usage**. The model used the harness's native PowerShell
  tool every time; the 6 control trials correctly used Bash. Even when the permission
  system blocked `Import-Module` in the native tool, it retried rather than discover
  ptk. Structural finding: the harness defers MCP tools behind ToolSearch (they are
  not in the upfront tool list — confirmed reachable when explicitly requested), so
  ptk's descriptions are invisible unless the model actively searches; against a
  native PowerShell tool it never does. This reproduces the rtk non-adoption pattern
  and is directly relevant to the 7/16 go/no-go: on the Windows box, expect adoption
  only if the native tool path is painful (cold EXO/AD auth per call) or the tools
  are surfaced/allowlisted prominently. Harness artifacts (runner + transcripts) were
  session-scratch, not kept in-repo.
- 2026-07-02 Codex condition: ptk registered in `~/.codex/config.toml` (dotnet run,
  absolute project path; Headroom proxy config removed the same day). Unlike Claude
  Code, Codex loads MCP tool descriptions upfront — gpt-5.5 found and called ptk_ping
  correctly. Headless `codex exec` auto-denies MCP tool calls ("user cancelled", ~1ms
  pre-flight; not overridable via --full-auto or approval_policy=never), so automated
  trials were not possible; interactive verification by owner succeeded (pong, after
  one-time allow). Asked directly, gpt-5.5 self-reported it would prefer ptk for
  multi-step/heavy-module PowerShell work (75-90%) but not one-offs (30-40%) —
  recorded as a self-report only: per the continuation decision, self-reported
  benefit is explicitly not evidence; observed unprompted usage on the Windows box
  remains the go/no-go criterion. No fresh-session behavioral trial run on Codex yet.
- 2026-07-03: Windows box brought up and the server validated there (the
  prerequisite for the go/no-go, NOT slice 7 itself — no AD/EMS/EXO modules were
  exercised). Two machine gaps found and fixed: the user-level NuGet.Config had an
  EMPTY packageSources list, so every restore failed NU1100 instantly — registered
  the default nuget.org feed; Pester 5 was not visible to pwsh (only inbox 3.4.0) —
  installed 5.8.0 CurrentUser via Install-PSResource. With dotnet SDK 10.0.301 and
  pwsh 7.6.3: `dotnet test` 19/19; stdio handshake PASSED both via the built dll and
  via the exact `.mcp.json` registration command from a cold build (stdout
  byte-clean); warm cross-call state verified. Module Pester suite 29/31: the two
  failures ("compresses filesystem objects before formatting" and "still routes real
  (deserialized) filesystem objects to the fs compressor") assert README.md survives
  `Get-ChildItem <repo root> | Compress-PtcObject -MaxItems 10`, but this box's root
  has 12 entries (a machine-local `.claude/` dir among them), so README.md falls
  past the cap. The fixture is the live repo root — environment-sensitive by design,
  pre-existing, not a Windows defect. FIXED 2026-07-03 (review-fixes plan, slice
  5): both tests now use deterministic temp-dir fixtures; suite 43/43 on this box.
- 2026-07-03 (later): owner enabled the MCP server on this box and the live-session
  check PASSED in Claude Code on Windows — full parity with the Mac check:
  ptk_ping → pong; ptk_invoke shares state across calls (same PID, variable set in
  call 1 read back in call 2, engine PS 7.6.3); warm module load confirmed (Pester
  cold import 545.7 ms, warm re-import 2 ms); ptk_modules lists loaded modules;
  script errors surface in an `[errors]` block; ptk_reset clears variables and
  modules without restarting the process. This box is now a fully working ptk
  environment; the remaining pre-07-16 items are the AWAITING OWNER GO list above.
- AWAITING OWNER GO, proposed 2026-07-03: ~~(a) push the local commits~~ DONE —
  owner pushed a8d3d02..9804eed (the review fixes and docs) to origin
  2026-07-03; ~~(b) deterministic temp-dir fixture~~ DONE 2026-07-03 via the
  review-fixes plan (slice 5); (c) fold the slice-7 test matrix below into the
  continuation decision entry; (d) pre-07-16 tests on this box (bring-up + warm-load
  measurement DONE 2026-07-03 — see the live-check bullet below): ToolSearch
  discoverability probe (do the ptk tool descriptions rank for
  "powershell"-shaped queries?); failure-mode drills (kill
  the server mid-session — do the tools respawn or brick? wedge a call past a
  short `PTK_CALL_TIMEOUT_SECONDS` — does the recycle keep the session usable?);
  and a headless "nudge ladder" adoption experiment (bare registration →
  allowlisted → one neutral CLAUDE.md line → explicit rule; small N,
  PowerShell-shaped tasks that never name ptk; burns API tokens). Related protocol
  proposal: run the go/no-go two-phase — week 1 pure/unprompted (the recorded
  criterion), then if adoption is zero add the minimal effective nudge and watch
  PERSISTENCE (this tests the experienced-benefit hypothesis); adopting it means
  amending the continuation decision entry, not quietly moving the goalpost.
- ~~Live-session check~~ DONE 2026-07-02: all four tools appeared in a live Claude
  Code session on this Mac. Verified: ptk_ping → pong; ptk_invoke shares state across
  calls (same PID, variable set in call 1 read back in call 2); module warmth matches
  the recorded reload tax (Pester cold import 448 ms, warm re-import 6.9 ms);
  ptk_modules lists loaded modules; script errors surface in an `[errors]` block;
  ptk_reset clears variables and modules without restarting the process. The go/no-go
  test on the real Windows box (below) is still the open item.
