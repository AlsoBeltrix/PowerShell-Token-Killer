# Plan: GitHub release — five-RID packaging, installers, direct proof

**Status:** DRAFT 2026-08-03. Decision 2 is RULED: five RIDs (owner,
2026-08-03 — "packaging for Windows x64 & ARM64, macOS ARM64, Linux x64 &
ARM64 ... GH CIs should cover it"). Decisions A–D are all RULED 2026-08-04:
emulated x64 rtk on win-arm64, Apache-2.0, `v0.2.0`, fetch-on-install.
Decision 3 is WITHDRAWN as mis-scoped and replaced by Slice 7.0.

Slices 7.0 and 7.1 are EXECUTED. Slices 7.2–7.5 are authorized and
unblocked. **Decision 5 (tag and publish) remains UNRULED and is terminal**:
CI assembles a draft release only; no tag, no publication, no `v*` ref push
without a separate explicit owner go.

| Slice | Status |
| --- | --- |
| 7.0 shaper fallback | `bf1fc0b` |
| 7.1 `opr-15` | `cd00276` |
| 7.1 `opr-14` | `67d37dd` (proved on macos-latest arm64 CI) |
| 7.2 version + license packaging | `5b19260`, `fb6d951` |
| 7.3 `release.yml` | `ecc5df4` |
| 7.4 installers | `141793d`, `eec2ccd` |
| 7.5 direct proof | `db5601c` |

**All slices are executed, but the recorded proof below is against
`fb6d951` and is STALE.** As of `935b8b2` there are ~50 code commits since
that head, including the whole install rewrite (#42) and the tool-surface
change (`opr-53`). Slice 7.5 must be re-run per RID before publishing.

**Slice 7.5 re-run status (all legs CLOSED at `9e1790e`, rc.3 run
`31184671731`, 2026-08-07):**

| RID | Status |
|-----|--------|
| win-x64 | **PASSED 2026-08-06 — 22/22** by hand at `935b8b2` (real install into an isolated home, Defender + uninstall checks, full battery green), and again in CI at `9e1790e` including the hardened scan-completes check (r806-4). |
| win-arm64 | **PASSED 2026-08-07 in CI** (run `31184671731`, head `9e1790e`) — full per-RID gate on the native runner, Defender legs included. |
| linux-x64 | **PASSED 2026-08-07 in CI** (same run). First POSIX run of the gate; see note below on check 13. |
| linux-arm64 | **PASSED 2026-08-07 in CI** (same run). |
| osx-arm64 | **PASSED 2026-08-07 in CI** (same run). |

The uninstall leg (`-UninstallHome`) stays outside CI by design — it needs an
installed home, and the workflow stage has a layout. It was exercised on the
win-x64 hand-run above.

First rc.3 dispatch (run `31184229338`→canceled-fork, then `31184268679` on
canonical, head `587b6e5`): both Windows legs passed; all three POSIX legs
failed on one check — the proof's check 13 asserted the Windows-only `ls`
alias unconditionally, and POSIX PowerShell ships none. The gate skipped the
draft, which is the gate working. Fixed platform-conditionally at `ccd6d1d`;
the re-run above went five-for-five and assembled the `v0.2.0-rc.3` draft.

Three of the plan's 13 checks were hand-run and are now automated into
`server/direct-product-proof.ps1` (`bc2091c`, `c9598d2`, `935b8b2`): check 10
uninstall (opt-in via `-UninstallHome`), check 13 `ls` alias plus
`PSModuleAutoloadingPreference`, and the Windows Defender payload scan. A
platform leg is now one command, not a manual checklist.

Evidence, `v0.2.0-rc.2` draft (run `30940515893`, head `fb6d951`) — historical:

- Five RIDs built on their own native runners, all six jobs green. Each
  artifact was handshake-smoke-tested and had its RTK startup gate proved on
  its own platform before archiving.
- Assets ~50–58 MB each plus `SHA256SUMS`; the published win-x64 checksum
  was independently re-verified after download.
- **Decision A is proved, not assumed:** on `windows-11-arm` the emulated
  x64 rtk answered `hook check` with `rtk git status --short`, the packaged
  server passed the handshake, and the gate returned exit 78.
- `server/direct-product-proof.ps1` against the installed win-x64
  candidate: 16/16 checks, including warm named sessions, `ptk_output`
  recovery, timeout recovery, compound native routing, and the trusted-type
  rendering from Slice 7.0.
- Windows Defender custom scan of the packaged payload: clean, payload
  intact. Issue #7's false positive does not reproduce on this artifact.

Version surfaces agree: `VERSION` `0.2.0-rc.2`, module `0.2.0` +
prerelease `rc2`, assembly `0.2.0-rc.2+fb6d951`.

This plan executes Slice 7 of `.agents/plans/rtk-router-delegation.md`. It
supersedes the packaging mechanics of `.agents/plans/release-distribution.md`
slices 3, 4, 6, and 7; that document's design commitments (one `~/.ptk` home,
directory-layout publish, no elevation, binary-relative module discovery,
remove-then-add registration, winget-ready ARP entry) remain authoritative
except where a numbered decision here overrides them. Its "rtk is
recommended, never bundled" commitment is **dead text** — superseded by the
owner's 2026-08-03 ruling that RTK is required.

## Decision 2 as ruled — the RID set

Five RIDs, each built and smoke-tested on its own native runner because
`Assert-PtkNativeBuildRid` (`scripts/dev-install.ps1:85`) refuses cross-RID
layout builds: `PtkWorkerBroker` is native and PTK has no proved
cross-target contract.

| RID | Runner | RTK upstream asset |
| --- | --- | --- |
| `win-x64` | `windows-latest` | `rtk-x86_64-pc-windows-msvc.zip` |
| `win-arm64` | `windows-11-arm` | `rtk-x86_64-pc-windows-msvc.zip`, emulated (Decision A) |
| `linux-x64` | `ubuntu-latest` | `rtk-x86_64-unknown-linux-musl.tar.gz` |
| `linux-arm64` | `ubuntu-24.04-arm` | `rtk-aarch64-unknown-linux-gnu.tar.gz` |
| `osx-arm64` | `macos-latest` | `rtk-aarch64-apple-darwin.tar.gz` |

No `osx-x64`. Verified against `rtk-ai/rtk` v0.44.2 release assets
(2026-08-03): the only Windows asset is x86_64-msvc.

Both ARM runner labels exist and are free and unlimited on public
repositories (GitHub hosted-runner reference, checked 2026-08-03); this repo
is public. The five-RID matrix therefore costs nothing in Actions minutes,
and no self-hosted runner is needed.

Selecting Linux and macOS activates the platform gate in
`.agents/plans/minimum-viable-release.md` §Decision 2 and the
`.agents/review/dispositions.md` §"deferred to platform selection" bucket.
Two HIGH findings stop being deferrable — see Slice 7.1.

## Decision A — win-arm64 RTK strategy (RULED: emulated x64)

**Ruled 2026-08-04 (owner, y):** ship win-arm64 using the x86_64 rtk under
Windows ARM64's x64 emulation. Do not drop the RID and do not wait for an
upstream aarch64 Windows build.

RTK publishes no aarch64 Windows binary. PTK refuses to start without a
capturable RTK (`server/PtkMcpServer/Execution/RtkDependency.cs:26`), so a
win-arm64 install with no RTK is a dead install.

Consequences that bind later slices:

- Slice 7.3 adds a win-arm64 CI step proving the emulated rtk answers
  `hook check` — not merely that `rtk --version` runs. A version banner
  proves the loader started the image; it does not prove the rewriter
  returns usable stdout under emulation, which is the contract PTK depends
  on (`.agents/plans/rtk-router-delegation.md` §Upstream contract).
- Slice 7.4's installer surfaces an emulation failure at install time, not
  at first invocation. On win-arm64 the installer must execute a real
  `hook check` probe against the rtk it just placed and fail the install if
  the probe does not answer.
- Slice 7.2 documents the emulation in the win-arm64 release notes.

## Decision B — license (RULED: Apache-2.0)

**Ruled 2026-08-04 (owner):** Apache-2.0. Chosen over MIT for its explicit
patent grant and retaliation clause; it also matches rtk, PTK's required
dependency.

The repository shipped no `LICENSE` and the GitHub API reported no license
(checked 2026-08-03). `LICENSE` now exists at the repo root, verbatim
Apache-2.0.

Slice 7.2 packages it into every artifact. Do not add a NOTICE file unless
PTK actually redistributes third-party Apache-2.0 material — under
Decision D option (a) the installer fetches rtk at install time rather than
redistributing it, which carries no NOTICE obligation; option (b) bundling
does, and would require attribution for rtk in the artifact.

## Decision C — release version (RULED: v0.2.0)

**Ruled 2026-08-04 (owner, y).** The release is `v0.2.0`. Dev builds already
stamp `0.2.0-dev.g<sha>`, so numbering the release below its own prereleases
was never an option. Nothing has shipped, so no upgrade path is owed.

`src/PwshTokenCompressor.psd1:3` says `ModuleVersion = '0.1.0'`;
`Get-PtkVersion` (`scripts/dev-install.ps1:114`) defaults dev builds to
`0.2.0-dev.g<sha>`; `.agents/plans/release-distribution.md` recorded
`v0.2.0` as the intended release. One value must reach every user-visible
surface. Do not invent a second build-number system.

## Decision D — how RTK reaches the user (RULED: fetch-on-install)

**Ruled 2026-08-04 (owner, a).** The installer detects rtk; when absent it
downloads the RID's asset from `rtk-ai/rtk` releases, verifies it against
that release's `checksums.txt`, and lays it into `~/.ptk/bin`. PTK's
registration points `PTK_RTK_PATH` at that copy. PTK's own release assets
vendor no third-party binary, and PTK does not own rtk's staleness.

Rejected: bundling rtk in each asset (PTK would own its staleness and
attribution surface), and refusing to install (worst first-run experience
for no safety gain, since the fetch is checksum-verified).

Binding consequences:

- **Never complete an install onto a machine where PTK will refuse to
  start.** A failed or unverifiable rtk fetch fails the install, with the
  message naming both `PTK_RTK_PATH` and rtk's own installer.
- **Record which rtk PTK placed** in the installed layout, so uninstall
  removes only a PTK-placed copy and never a user's own rtk. An rtk already
  on PATH is used as-is and never touched.
- Verify the download against the release's `checksums.txt`, not merely
  HTTPS. An unverified binary that PTK then pins and hashes at startup is
  exactly the substitution the pinning exists to prevent.
- On win-arm64 the fetched asset is the x64 build (Decision A), and the
  installer must run a real `hook check` probe against it before declaring
  success.
- rtk is Apache-2.0, so distributing it would have been permitted; this
  choice avoids the obligation rather than being forced by it.

RID → asset map (rtk v0.44.2 assets, verified 2026-08-03):

| RID | rtk asset |
| --- | --- |
| `win-x64` | `rtk-x86_64-pc-windows-msvc.zip` |
| `win-arm64` | `rtk-x86_64-pc-windows-msvc.zip` (emulated) |
| `linux-x64` | `rtk-x86_64-unknown-linux-musl.tar.gz` |
| `linux-arm64` | `rtk-aarch64-unknown-linux-gnu.tar.gz` |
| `osx-arm64` | `rtk-aarch64-apple-darwin.tar.gz` |

## Decision 3 — superseded by Slice 7.0

**Withdrawn 2026-08-04.** This decision was mis-scoped and must not be
ruled as written. It asked whether to accept a documented "Outlook/COM
boundary", implying the only unrendered values were live COM and lazy
getters on Outlook and EXO objects.

That is false, and it was verified false on an ordinary Windows host with
no Outlook and no Exchange present (2026-08-04): `Get-Culture` renders as
`[active member not evaluated]`, and so does `git push` output. The defect
is not a COM boundary — it is that `ProjectOutput`
(`server/PtkMcpServer/RunspaceHost.cs:1776`) recognizes six types and
returns no data for everything else. Outlook and EXO are merely where the
owner first noticed it (GitHub #8).

Accepting the old wording would have shipped a compression tool that
silently drops values for most .NET types under a note describing a
narrower problem. Slice 7.0 replaces this decision. The remaining genuine
question — whether PTK should execute active COM/lazy getters — is answered
NO by Slice 7.0 and needs no separate ruling: the fallback renders text
without invoking user getters, so the safety posture is unchanged.

Original text, retained so the supersession is auditable, from
`.agents/plans/minimum-viable-release.md:141`.
Recommended contract: materialized, selected, and deserialized values are
supported; PTK does not invoke active/lazy/COM getters to enrich output; the
limitation is documented and GitHub #8 stays open. Making active COM values
mandatory instead is a separate product slice that blocks packaging.

## Decision 5 — publish (UNRULED, terminal)

Tagging and publishing are owner actions. CI assembles a **draft** release
only. Nothing in this plan authorizes a tag, a public release, or a push of
a `v*` ref.

## Slice 7.0 — the shaper renders unknown types instead of dropping them

**Approved 2026-08-04 (owner: "yeah fix it").** Release blocker under
`.agents/plans/minimum-viable-release.md` §Release-blocking rule: `ptk_invoke`
returns materially wrong output — no data at all — for most object types.

### The defect

`ProjectOutput` (`server/PtkMcpServer/RunspaceHost.cs:1776`) handles
scalars via `TryPassiveScalar`, then branches on an allowlist of exactly
six shapes: `ErrorRecord`, `FileInfo`, `DirectoryInfo`, `MatchInfo`,
`Process`, `PSCustomObject`. Its final `else` (line 1859) calls
`AddActiveMemberPlaceholder(detached, "Value")`, which emits
`[active member not evaluated]` and sets `_activeMemberOmitted`, flagging
the whole capture incomplete.

Every other type lands there: `CultureInfo`, `TimeZoneInfo`,
`X509Certificate2`, `ServiceController`, AD and Exchange objects, and any
type from any module. Reproduced 2026-08-04 with `Get-Culture` on a host
with no Outlook and no Exchange.

Reproduce it with a bare cmdlet, never with a native command. A native
command that RTK rewrites returns RTK-filtered text and never reaches this
projection at all; one PTK runs directly (RTK declined it, or the script is
a compound whose other segments are cmdlets) does. An earlier draft of this
plan cited `git push` output as evidence — that output was PowerShell-direct
only because the submitted script was prefixed with `Set-Location`, which
is a property of how the call was made, not of the shaper. `Get-Culture`
reproduces the defect with nothing else in play.

`PassiveNoteValue` (line 2001) has the same hole one level down: a
non-scalar note value becomes `[nested <type> not expanded ...]`.

### Why the current behavior exists, and what must not change

Active PowerShell members are executable user code. Reading one during
capture runs that code on the producer callback — arbitrary execution at
shaping time, which PTK refuses. Two guards enforce this and **must keep
passing unchanged**:

- `A_spoofed_service_controller_name_never_authorizes_live_getters`
  (`RunspaceHostTests.cs:451`) — a user type named `ServiceController`
  with counting getters; asserts `Reads == 0`.
- `Passive_capture_never_enumerates_a_user_property_adapter`
  (`RunspaceHostTests.cs:510`) — a user `PSPropertyAdapter`; asserts zero
  `GetProperties`/`GetProperty` calls.

Both build their types in-memory with `Add-Type`. That is the
discriminator this slice turns on: **assembly trust**, the same mechanism
`TryFreezeErrorRecord` (line 1738) already uses to decide whether an
exception's `Message` is safe to read. `ErrorRecord` is the precedent —
it hit this exact dead end and Slice 4 fixed it by projecting text
instead of shape.

### The repair

Add a final fallback ahead of the `else` placeholder: when the base
object's type comes from a trusted assembly, render it with a bounded
`ToString()` and project that text; otherwise keep the existing
placeholder.

Trust test — reuse and widen `IsTrustedPowerShellAssembly` (line 1767)
into a shared predicate that accepts a type whose assembly is:

- loaded from the runtime/framework directory
  (`AppContext.BaseDirectory` or `RuntimeEnvironment.GetRuntimeDirectory()`),
  covering `System.*` and `Microsoft.*` BCL types; or
- one of the two already-trusted PowerShell assemblies with the matching
  public key token.

An assembly that is dynamic (`Assembly.IsDynamic`) or has no on-disk
`Location` is never trusted — that is precisely what `Add-Type` produces,
so both guards keep their placeholder and their zero read counts.

Constraints on the rendering itself:

- Call `ToString()` only. Never enumerate properties, never touch ETS,
  never consult a `PSPropertyAdapter`.
- A type that overrides `ToString()` in an untrusted assembly is excluded
  by the trust test before the call is reached.
- Wrap in try/catch; on throw, fall back to the placeholder and set
  `_captureFailed` as the existing catch paths do.
- Charge the result through `TryChargeProjection` like every other
  retained string, and bound it — cap the rendered text and truncate with
  a marker rather than retaining an unbounded `ToString()`.
- A rendered value is a lossy projection, not an omission: set
  `_lossyProjection`, do **not** set `_activeMemberOmitted`. This matters
  for the user-visible marker — the capture reports
  `passive_projection_lossy`, not `active_member_not_evaluated`, because
  no active member was skipped.

Apply the same fallback to `PassiveNoteValue` so nested trusted values
render rather than reporting `[nested ... not expanded]`.

### Issue #8's secondary complaint

`TryFreezeErrorRecord` returns
`"[PowerShell error text omitted because its exception type was not safe to
inspect]"` for any exception outside the two trusted PowerShell assemblies
(line 1751), so a module's own exception loses its message entirely. Widen
it to the same shared trust predicate, so a framework exception surfaces
its `Message`. An exception type from a genuinely untrusted assembly keeps
the omission text. Confirm against #8's reported `route: pwsh` case before
claiming it fixed; if the reported case involves an untrusted module
assembly, say so plainly rather than reporting a fix the report would not
observe.

### Guards (each mutation-proved)

1. `Get-Culture` returns text containing the culture name, and the capture
   is not flagged `active_member_not_evaluated`.
2. A `[System.TimeZoneInfo]` (a second trusted type, from a different
   assembly than `CultureInfo`) renders its text rather than a placeholder
   row.
3. Both existing no-live-getter guards still pass **unchanged** — not
   edited to accommodate the fallback. If either needs editing, the trust
   test is wrong; fix the trust test.
4. A trusted type whose `ToString()` is enormous is truncated to the cap
   with a marker.
5. A trusted type whose `ToString()` throws yields the placeholder and a
   failed-capture flag, not an escaped exception.
6. An exception from a trusted framework assembly surfaces its `Message`;
   one from an `Add-Type` assembly keeps the omission text.

**Complete when:** guards 1, 2, and 6 fail against the current build and
pass after; guards 3–5 pass; the full battery passes; and a real
`ptk_invoke` of `Get-Culture` on this host shows the value.

Do not attempt to realize active COM or lazy getters. That was issue #8's
suggestion 1 and it stays rejected: it executes user code at shaping time.
Suggestion 2 (fall back to a text rendering) is what this slice implements.

## Slice 7.1 — repair the two Unix HIGH findings

**Status: both repaired 2026-08-04.** `opr-15` at `cd00276` (master);
`opr-14` on branch `ci/opr-14-cloexec`, held there until the Linux and macOS
runners prove it, because its guards skip on Windows and this development
host cannot execute them. Dispositions updated in
`.agents/review/dispositions.md`.

Decision 2 selected Linux and macOS, so these leave the deferred bucket.
Both are containment-correctness defects in the supported release contract;
both meet the release-blocking rule's "breaks a named session" clause.

Implementation note for `opr-14` that the finding did not anticipate: the
repair replaced the variadic `fcntl(F_SETFD)` with `ioctl(FIOCLEX)`, which
passes no variadic argument at all and is additionally atomic. `FIOCLEX` is
**not** the same constant on both platforms — Darwin `_IO('f', 1)` is
`0x20006601`, Linux is `0x5451`. A single shared constant would have broken
Linux, the platform that already worked. Both values were verified against
the xnu and Linux uapi headers rather than assumed.

**`opr-15` (blocks linux-x64, linux-arm64, osx-arm64).** Detail in
`.agents/review/findings/opr-15.md`. `IsIdentityLive` in
`server/PtkMcpServer/Worker/UnixWorkerContainmentRegistry.cs` catches every
nonfatal exception from `_native.QueryIdentity` and returns `false`, so a
transient `/proc/<pid>/stat` or `proc_pidinfo` failure is indistinguishable
from process absence. A transient failure on an observed escaped descendant
lets `CanConfirmEmpty` complete the registry emptiness task and release the
session alias while that process still runs.

Repair: make the native identity boundary tri-state — exact identity,
confirmed absence, indeterminate. Only confirmed absence or an observed
different incarnation satisfies liveness; indeterminate keeps containment
unconfirmed and retries through the existing observer.

Guard (deterministic fault injection): `CompleteAsync` returns
`descendants_unknown` for an observed escaped descendant, then the worker
group empties while one escaped-descendant identity query fails
transiently. Current code completes the emptiness task in the background;
the repair must leave it incomplete through both the transient failure and
a later exact live observation, completing only after an exact dead or
different-incarnation observation. Mutation-prove before retaining.

**`opr-14` (blocks osx-arm64).** Detail in
`.agents/review/findings/opr-14.md`. `UnixWorkerBootstrap.cs` and
`UnixWorkerProcessLauncher.cs` each declare libc's variadic `fcntl` as the
fixed P/Invoke `Fcntl(int, int, int)` and use it for `F_SETFD`. Apple's
arm64 ABI passes variadic arguments on the stack while a fixed third
argument follows the ordinary register convention, so the callee does not
reliably receive `flags | FD_CLOEXEC`. A user's command child can then
inherit the worker's duplicated request reader and event writer.

Repair: an ABI-correct non-variadic native shim on Apple arm64, preserving
Linux behavior.

The shim has an existing home. `server/PtkMcpServer/Native/ptk_worker_broker.c`
is already compiled per-RID by the `BuildPtkWorkerBroker` target
(`server/PtkMcpServer/PtkMcpServer.csproj:35`) on every non-Windows build,
already includes `fcntl.h`, and already carries `__APPLE__` conditionals.
Adding a `ptk_set_cloexec(int fd)` wrapper there — where the C compiler
emits the correct variadic call sequence — avoids inventing a second native
build path. Note the target currently produces an executable, not a shared
library, so exposing a callable symbol needs either a second output or a
small dedicated source; choose whichever keeps `-Werror -Wpedantic` clean.
Both call sites (`UnixWorkerBootstrap.cs:304`,
`UnixWorkerProcessLauncher.cs:1094`) declare the identical bad P/Invoke and
must both be replaced.

Guards on real Apple arm64: `FD_CLOEXEC` is actually set; an exec-created
command child cannot observe duplicated bootstrap descriptors; overlapping
worker launches cannot inherit one another's temporary mapping descriptors.
Existing macOS suites exercise worker startup without asserting descriptor
flags, so their passing is not evidence — the guards must fail against the
current implementation first.

The remaining platform-deferred findings (`opr-13`, `opr-23`, `opr-24`–
`opr-31`, `opr-46`) are MEDIUM/LOW and do not meet the release-blocking
rule. Do not repair them in this plan.

**Complete when:** both guards fail against current code, pass after the
repair, and the full battery passes on Linux and macOS in hosted CI.

## Slice 7.2 — one coherent version, license, packaged layout

Behind Decisions B and C.

1. Set the ruled version in the module manifest, the server assembly and
   package metadata, the installed `VERSION` file, and every user-visible
   diagnostic. `Get-PtkVersion` already strips a leading `v` from a tag, so
   release CI passes the tag verbatim.
2. Put the source commit in the assembly informational version. No
   per-rebuild uniqueness scheme, no provenance system.
3. Add `LICENSE` at the repo root and copy it into the packaged layout via
   `New-PtkLayout` (`scripts/dev-install.ps1:187`) alongside a trimmed
   README. Package only: the MCP server and its runtime/native files, the
   shaping module, `scripts/`, license/readme, and the registration
   command. Never SIEM, `PtkAuditAdmin`, tests, review records, or `.agents/`.
4. Reuse the existing generator. `-LayoutOnly -OutputDir -Rid -Version`
   already exists and release CI drives exactly it, so dev and release
   artifacts stay identical by construction. Do not fork a second layout
   path.

**Complete when:** every user-visible version surface agrees, `LICENSE` is
present in the repo and in a built layout, and a layout built on each RID's
native runner passes `Assert-PtkPayloadIntact`.

## Slice 7.3 — `.github/workflows/release.yml`

Triggered on `v*` tags. Per-RID job on the RID's native runner from the
Decision 2 table:

1. `dotnet publish` through `scripts/dev-install.ps1 -LayoutOnly` with
   `-Rid` and `-Version` from the tag.
2. Install RTK for that RID (Decision D decides whether it is also staged
   into the artifact).
3. Launch and handshake the packaged binary via
   `server/test-handshake.ps1 -ServerCommand`, which already supports
   driving an arbitrary server binary.
4. Prove the RTK startup gate: with `PTK_RTK_PATH` pointed at nothing and
   `rtk` off PATH, the packaged binary exits 78 and prints the message from
   `RtkDependency.UnavailableMessage()`.
5. Archive as `ptk-<version>-<rid>.zip` (Windows) or `.tar.gz` (Unix),
   emit `SHA256SUMS`, upload.

A final job assembles a **draft** GitHub Release from the five artifacts.
It never publishes.

Reuse `.github/workflows/ci.yml`'s RTK install steps rather than writing
new ones; extend them for the two ARM runners and, under Decision A(a), for
emulated x64 rtk on `windows-11-arm`.

Any RID whose runner smoke cannot run is verified on owner hardware in
Slice 7.5 or dropped with a logged reason. No untested asset ships.

**Complete when:** an rc tag (`v<version>-rc.1`) produces a complete draft
with five smoke-tested, checksummed assets.

## Slice 7.4 — public installers

Behind Decision D. `install.ps1` (Windows) and `install.sh` (POSIX sh for
macOS/Linux), both currently absent from the repo. Each: detects OS and
arch, refuses to run elevated, downloads the pinned-or-latest asset,
verifies it against `SHA256SUMS`, stages and validates the complete payload,
refuses to mutate an in-use installation, snapshots the prior payload and
registrations, activates as one unit, then registers as the final mutation.
Any failure restores the snapshot before returning failure. `--uninstall`
removes payload, registrations, and the Windows ARP entry while leaving user
config; `--purge` removes everything.

The RTK requirement is enforced here per Decision D. The installer must not
report success on a machine where PTK will refuse to start.

**Complete when:** from the rc draft, a one-line install on each supported
OS ends with the handshake passing through the installed binary; injected
failure at every activation and registration boundary leaves the prior
payload and registrations byte-identical; uninstall leaves nothing behind.

## Slice 7.5 — direct product proof

Once the candidate is assembled, run the full battery once
(`.agents/repo-guidance.md` §Verification), then this direct check on one
clean host per RID — the owner has hardware for all five:

1. install the candidate;
2. launch it through the printed registration command;
3. list exactly the five supported tools;
4. open a named session and prove state survives a second invocation;
5. compress a representative PowerShell object;
6. preserve representative plain text;
7. recover bounded large output through `ptk_output`;
8. time out one invocation and prove the next succeeds;
9. reset and close a named session;
10. uninstall and prove the installed launch path is gone;
11. a compound native command (`git status && cargo test`) routes through
    RTK and returns compressed output;
12. with RTK absent, startup fails with the actionable message naming
    `PTK_RTK_PATH`;
13. a fresh session exposes the shipped `ls` alias and loads no user module,
    on a host whose user module directory contains one.

On Windows, also scan the exact packaged bits with current Defender —
GitHub #7's false positive on `PtkMcpServer.dll` is unresolved pending
Microsoft's WDSI verdict, and quarantine of the supported Windows artifact
is a release blocker under the release-blocking rule.

Record commands and outcomes. No scores, no derived metrics, no new test
framework, no soak run.

## Process constraints

Carried from `.agents/plans/rtk-router-delegation.md` §Process constraints
and still binding:

- **No reviewer invocation** — no `codereview`, `openreview`, or unattended
  review until the release ships. Each requires a separate explicit owner
  approval naming that exact invocation.
- **Fix or dispose, never record.** A defect found during implementation is
  fixed in its slice or given a disposition in
  `.agents/review/dispositions.md`. Do not create a new gated finding.
- **No file-by-file source review.**
- Focused tests during implementation; the full battery once per slice
  before commit.
- One slice per commit, pushed under `.agents/push-policy.md`.
- `Audit/`, `siem/`, and `PtkAuditAdmin` are untouched and excluded from the
  release. Exclusion is not deletion.

## Ordered execution

1. Slice 7.0 (shaper fallback) — approved; a release blocker in the
   supported contract, independent of platform.
2. Rule Decisions C and D. (A and B are ruled; 3 is withdrawn.)
3. Slice 7.1 (Unix HIGH repairs) — required before any Unix RID packages.
4. Slice 7.2, then 7.3, then 7.4.
5. Slice 7.5.
6. Decision 5, then tag and publish only on an explicit separate go.
