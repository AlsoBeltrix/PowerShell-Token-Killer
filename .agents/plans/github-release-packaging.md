# Plan: GitHub release — five-RID packaging, installers, direct proof

**Status:** DRAFT 2026-08-03. Decision 2 is RULED: five RIDs (owner,
2026-08-03 — "packaging for Windows x64 & ARM64, macOS ARM64, Linux x64 &
ARM64 ... GH CIs should cover it"). Decisions A–D below and Decisions 3–5
carried from `.agents/plans/minimum-viable-release.md` are UNRULED and gate
the slices that name them. No code, tag, or publication is authorized by
this draft.

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
| `win-arm64` | `windows-11-arm` | none — x64 under emulation (Decision A) |
| `linux-x64` | `ubuntu-latest` | `rtk-x86_64-unknown-linux-musl.tar.gz` |
| `linux-arm64` | `ubuntu-24.04-arm` | `rtk-aarch64-unknown-linux-gnu.tar.gz` |
| `osx-arm64` | `macos-latest` | `rtk-aarch64-apple-darwin.tar.gz` |

No `osx-x64`. Verified against `rtk-ai/rtk` v0.44.2 release assets
(2026-08-03): the only Windows asset is x86_64-msvc.

Selecting Linux and macOS activates the platform gate in
`.agents/plans/minimum-viable-release.md` §Decision 2 and the
`.agents/review/dispositions.md` §"deferred to platform selection" bucket.
Two HIGH findings stop being deferrable — see Slice 7.1.

## Decision A — win-arm64 RTK strategy (UNRULED)

RTK publishes no aarch64 Windows binary. PTK refuses to start without a
capturable RTK (`server/PtkMcpServer/Execution/RtkDependency.cs:26`), so a
win-arm64 install with no RTK is a dead install.

Options: (a) ship win-arm64 with the x64 rtk under Windows ARM64 x64
emulation, documented; (b) drop win-arm64 until upstream ships aarch64.

Under (a), Slice 7.3 must add a win-arm64 CI step proving the emulated rtk
answers `hook check` — not merely that `rtk --version` runs. Emulation
failure must surface at install time, not first invocation.

Under (b), delete the `win-arm64` row everywhere in this plan.

## Decision B — license (UNRULED)

The repository ships no `LICENSE` file and the GitHub API reports no license
for it (checked 2026-08-03). A public release requires one. Candidates: MIT,
Apache-2.0. Whichever is chosen, Slice 7.2 adds `LICENSE` at the repo root
and packages it into every artifact.

## Decision C — release version (Decision 4, UNRULED)

`src/PwshTokenCompressor.psd1:3` says `ModuleVersion = '0.1.0'`;
`Get-PtkVersion` (`scripts/dev-install.ps1:114`) defaults dev builds to
`0.2.0-dev.g<sha>`; `.agents/plans/release-distribution.md` recorded
`v0.2.0` as the intended release. One value must reach every user-visible
surface. Do not invent a second build-number system.

## Decision D — how RTK reaches the user (UNRULED)

RTK is Apache-2.0, so all three options are legally available.

(a) **Fetch-on-install.** The installer detects rtk; when absent it
downloads the RID's asset from `rtk-ai/rtk` releases, verifies it against
that release's `checksums.txt`, and lays it into `~/.ptk/bin`. PTK's
registration then points `PTK_RTK_PATH` at that copy. One command still
works; PTK's release assets vendor no third-party binary.

(b) **Bundle.** Each PTK asset carries rtk. Larger assets; PTK owns rtk
staleness and its license/attribution surface.

(c) **Hard prerequisite.** Installer refuses and prints rtk's installer.

Whichever is ruled, the invariant from `.agents/state.md` holds: **do not
ship an installer that completes successfully onto a machine with no RTK.**
Under (a) and (b), the installed layout must record which rtk PTK pinned so
uninstall removes only a PTK-placed copy and never a user's own rtk.

## Decision 3 — Outlook/COM boundary (UNRULED)

Carried unchanged from `.agents/plans/minimum-viable-release.md:141`.
Recommended contract: materialized, selected, and deserialized values are
supported; PTK does not invoke active/lazy/COM getters to enrich output; the
limitation is documented and GitHub #8 stays open. Making active COM values
mandatory instead is a separate product slice that blocks packaging.

## Decision 5 — publish (UNRULED, terminal)

Tagging and publishing are owner actions. CI assembles a **draft** release
only. Nothing in this plan authorizes a tag, a public release, or a push of
a `v*` ref.

## Slice 7.1 — repair the two Unix HIGH findings

Decision 2 selected Linux and macOS, so these leave the deferred bucket.
Both are containment-correctness defects in the supported release contract;
both meet the release-blocking rule's "breaks a named session" clause.

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

1. Rule Decisions A–D and Decision 3.
2. Slice 7.1 (Unix HIGH repairs) — required before any Unix RID packages.
3. Slice 7.2, then 7.3, then 7.4.
4. Slice 7.5.
5. Decision 5, then tag and publish only on an explicit separate go.
