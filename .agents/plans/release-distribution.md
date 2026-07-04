# Plan: Release distribution — prebuilt binaries + one-line installer

**Status:** DRAFT 2026-07-04 — awaiting owner approval before any code.
**Decision basis:** owner direction 2026-07-04 (recorded as an amendment to the
continuation decision in `.agents/decisions.md`): the current
run-from-the-repo-checkout install story (`dotnet run --project ...`) is not
acceptable for a release. First public release target **2026-07-25**, and it
must ship as prebuilt per-platform binaries with a one-line installer
(tier 3). The publish-and-register script (tier 1) and .NET tool packaging
(tier 2) are **dev-only** paths, never the public install story.

## Goal

By 2026-07-25, a user on Windows, macOS, or Linux installs ptk without cloning
the repo or having the .NET SDK:

```powershell
# Windows
irm https://raw.githubusercontent.com/AlsoBeltrix/PowerShell-Token-Killer/master/install.ps1 | iex
```

```sh
# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/AlsoBeltrix/PowerShell-Token-Killer/master/install.sh | sh
```

The installer downloads the release asset for the platform from GitHub
Releases, verifies its checksum, lays it out under a stable install dir,
registers the MCP server with Claude Code when the `claude` CLI is present
(prints the command otherwise, plus a Codex `config.toml` snippet), and can
optionally install the redirect hook. `--uninstall` reverses all of it.

Dev workflows keep two non-public paths: a publish-and-register script that
installs the current checkout's HEAD (tier 1 — also the exact layout logic the
release CI reuses), and local .NET tool packaging (tier 2, cuttable).

## Grounded facts this design stands on

- **Self-contained publish works.** Recorded 2026-07-03 (`.agents/state.md`):
  a self-contained publish of the server xcopy-deploys to boxes with no .NET
  installed; the embedded engine is PowerShell 7.6 (SDK 7.6.3, net10.0,
  ModelContextProtocol 1.4.0 per `server/PtkMcpServer/PtkMcpServer.csproj`).
- **Module discovery already supports an installed layout.**
  `RunspaceHost.ResolveModulePath` probes upward from cwd and
  `AppContext.BaseDirectory` for `src/PwshTokenCompressor.psd1`, and
  `PTK_MODULE_PATH` is an explicit override. The installer registers with an
  explicit `PTK_MODULE_PATH` (deterministic) rather than relying on the probe,
  which could match a checkout's module when a session runs inside a repo that
  has one.
- **The hook installer is layout-portable.** `scripts/ptk_init.ps1` resolves
  `ptk-hook.ps1` from its own `$PSScriptRoot`, so shipping `scripts/` inside
  the release artifact lets the installed copy register the installed hook
  path. (Verify in slice 1; expected zero code change.)
- **The handshake script is the artifact smoke test** but currently launches
  only via `dotnet run` / `dotnet exec` of a Debug dll. It needs a third mode
  that drives an arbitrary server binary (slice 0 tooling change).
- **Repo is PUBLIC** (verified 2026-07-04 via `gh repo view`): anonymous
  release-asset downloads and raw.githubusercontent.com installer URLs work.
- **No CI exists** (recorded in `.agents/repo-guidance.md`); release
  automation requires building it, and workflow iteration only runs on pushed
  refs — see Owner logistics.

## Design commitments

- **Directory-layout publish, not single-file, no trimming.** The PowerShell
  SDK is reflection-heavy and does not support trimmed or single-file publish
  reliably; the artifact is a plain publish directory in an archive. Size
  (~70–120 MB per RID) is acceptable for GitHub Releases; slice 0 measures it.
- **Every shipped artifact is smoke-tested on its own OS** by the extended
  handshake script against the actual published binary, in CI, before it can
  land in a release. No untested asset ships (an untestable RID is dropped,
  not shipped blind — no silent caps).
- **Install layout** (one root, everything inside it):
  - Windows: `%LOCALAPPDATA%\ptk`; macOS/Linux: `~/.ptk`
  - `bin/` (publish output, `PtkMcpServer(.exe)`), `src/` (module),
    `scripts/` (`ptk-hook.ps1`, `ptk_init.ps1`), `VERSION`
- **Registration** is `claude mcp add ptk --scope user` pointing at the
  absolute binary path with `PTK_MODULE_PATH` set in the env block;
  remove-then-add so re-installs and dev→release switches never collide.
- **rtk is recommended, never bundled.** It is a separate product with its own
  installers; ptk already degrades visibly without it. The installer detects
  rtk on PATH and prints an install pointer when absent.
- **The release tag and the published release are owner actions.** CI builds
  and smoke-tests everything into a **draft** release; publishing it (and
  pushing the `v0.1.0` tag that triggers it) needs an explicit owner go, per
  `.agents/push-policy.md`. This also lets the ~2026-07-20 go/no-go outcome
  abort the release without public residue.
- **No new server features ride along.** This plan is distribution only;
  server/module behavior changes are out of scope except the handshake-script
  launch mode (test tooling).

## Slices

0. **Probe, then freeze (evidence + test tooling only).**
   - Extend `server/test-handshake.ps1` with a `-ServerCommand <exe> [args]`
     mode (guard: both existing modes still pass; new mode passes against a
     local publish). The only code in this slice.
   - Local publish probe on this Mac (osx-arm64): publish → canonical layout →
     handshake against the binary via the new mode; measure archive size and
     cold-start latency vs `dotnet run`. Confirm the macOS apphost is ad-hoc
     signed (it is by default from dotnet publish) and runs from a
     curl-downloaded archive without Gatekeeper interference.
   - `claude mcp add --env` syntax verified against the installed CLI.
   - CI probe on a side branch: minimal workflow proving runner facts —
     .NET 10 SDK via setup action, Pester 5 availability/pinning per OS,
     macos-latest is arm64, Rosetta presence for an osx-x64 smoke, artifact
     upload. Results recorded here before slices 2–4 are built.
1. **Canonical layout + dev install script (tier 1, dev-only).**
   `scripts/dev-install.ps1`: publish the current checkout self-contained for
   the local RID, produce the install layout, register with `claude mcp` (and
   print the Codex snippet), `-Hook` optional, `-Uninstall`, `-LayoutOnly
   -OutputDir` mode that release CI reuses so dev and release artifacts are
   the same layout by construction. Guard: handshake `-ServerCommand` against
   the installed binary; uninstall removes the registration and the dir;
   existing repo-based `.mcp.json` flow untouched.
2. **CI workflow (tests).** `.github/workflows/ci.yml` on push/PR:
   ubuntu/windows/macos matrix running the Pester suite, `dotnet test`, and
   the handshake. Guard: green on all three OSes on the PR/branch.
3. **Release workflow.** `.github/workflows/release.yml` on `v*` tags:
   per-RID publish using the slice-1 layout mode on the RID's native runner
   (win-x64 on windows, linux-x64 on ubuntu, osx-arm64 on macos; osx-x64 also
   on macos with a Rosetta smoke), handshake against each artifact, archive as
   `ptk-<version>-<rid>.zip|.tar.gz`, generate `SHA256SUMS`, assemble a
   **draft** GitHub Release. Version stamped from the tag via `-p:Version`.
   Guard: an rc pre-release tag (e.g. `v0.1.0-rc.1`) yields a complete draft
   with every asset smoke-tested and checksummed.
4. **Installers.** `install.ps1` (Windows PowerShell one-liner) and
   `install.sh` (POSIX sh for macOS/Linux): detect OS/arch, download the
   pinned-or-latest release asset, verify against `SHA256SUMS`, extract to the
   install root, register (remove-then-add) when `claude` is present else
   print the command, print the Codex snippet and an rtk recommendation when
   rtk is absent, `--hook` optional (requires `pwsh`; skipped with a message
   when missing — the server itself never needs an installed PowerShell),
   `--uninstall`. Guard: from the rc draft release, a one-liner install on
   each supported OS ends with the handshake passing against the installed
   binary, and uninstall leaves no registration or files.
5. **.NET tool packaging (tier 2, dev-only, CUTTABLE).** `PackAsTool` with the
   module shipped inside the package so the BaseDirectory probe finds it;
   installable only from a local source (`dotnet tool install -g
   --add-source`); explicitly NOT published to NuGet.org for v0.1.0. Guard:
   local tool install passes the handshake via the tool command. First thing
   dropped if 2026-07-25 is at risk — it is off the release critical path.
6. **Docs + release prep.** README "Setup" becomes the one-liner install
   (repo-checkout and dev paths move to a dev/contributor doc);
   `server/README.md` documents the installed layout and `PTK_MODULE_PATH`
   registration; release-notes draft; `ModuleVersion`/release version
   reconciled; `.agents/repo-guidance.md` "No CI" statement and
   `.agents/repo-map.json` verification entries updated (drift fix rides in
   the same slice that creates the drift).
7. **RC rehearsal + release (owner-gated).** End-to-end dry run in week 3 with
   the owner back (~2026-07-20): rc tag → draft release → one-liner installs
   exercised on the real Windows box and this Mac (Linux via container or CI),
   friction fixed, then owner pushes `v0.1.0` and publishes the release by
   2026-07-25.

Process note: the codex review loop per code slice (owner-set precedent,
2026-07-04) applies to slices 0–5; workflow YAML counts as code here.

## Timeline (target 2026-07-25)

- **Week 1 (Jul 6–11):** slices 0–2. Probe results frozen into this plan; dev
  install working locally; CI green on three OSes.
- **Week 2 (Jul 13–18):** slices 3–4, rc.1 draft release exercised end to end
  from CI; slice 5 only if slack remains.
- **Week 3 (Jul 20–24):** slice 6 docs, slice 7 rehearsal on the real boxes
  (owner back ~Jul 20), fixes, owner go, tag + publish 2026-07-25.
- **Buffer:** slice 5 is cuttable; osx-x64 is droppable (osx-arm64 covers the
  owner's Mac); the schedule holds the release even if week 1 slips into
  week 2, because slices 3–4 are the only hard-path items after slice 1.

## Owner logistics (needed to execute, not design questions)

- **Pushes:** CI/workflow iteration only runs on pushed refs, and the policy
  is ask-first. Requested: a standing go, scoped to this plan, for pushes to a
  `ci/*` side branch and `v0.1.0-rc.*` pre-release tags. `master` pushes and
  the final `v0.1.0` tag stay per-explicit-go.
- **Real-box installer verification** needs the Windows box, which returns
  with the owner ~Jul 20 — hence slice 7's placement; CI's windows runner
  covers the risk until then.

## Open questions (recommendations inline)

1. **RID set for v0.1.0.** Recommend `win-x64`, `linux-x64`, `osx-arm64`,
   plus `osx-x64` if the Rosetta smoke works on the CI runner; defer
   `win-arm64`/`linux-arm64` until someone asks.
2. **Version.** Recommend `v0.1.0` (matches the module's `ModuleVersion`).
3. **Hook in the public installer.** Recommend off by default behind
   `--hook`/`-Hook`: it is the adoption device for the owner's own test, but
   opinionated (deny-redirects every shell call) for a first-time public user.
4. **Install root names.** Recommend `~/.ptk` and `%LOCALAPPDATA%\ptk`.

## Risks

- **PowerShell SDK publish quirks** (the reason single-file/trimming are ruled
  out) could still surface per-RID at runtime; the every-artifact-smoke-tested
  commitment is the containment.
- **GitHub runner drift** (net10 SDK availability, Pester versions, macos
  arch) — slice 0's CI probe exists to catch this before the workflows are
  designed around wrong facts.
- **Unsigned binaries.** No code signing or notarization in v0.1.0. The
  curl/irm install paths avoid browser quarantine/MOTW, and dotnet ad-hoc
  signs macOS apphosts, but a user who downloads the archive via a browser may
  hit Gatekeeper/SmartScreen friction. Documented limitation; signing is a
  post-v0.1.0 track if it bites.
- **Go/no-go interaction.** The ~2026-07-20 adoption test may conclude
  "archive the project" days before the release date. The draft-until-owner-go
  release design means nothing public ships in that case; the distribution
  work is then the well-packaged end state of an archived project, not waste
  pressure to release anyway.
- **Timeline compression in week 3:** owner returns ~Jul 20 and the release is
  Jul 25, so real-box rehearsal, go/no-go, and release decision share five
  days. Mitigation: everything through rc draft releases is done and CI-proven
  before Jul 20.

## Non-goals

- Publishing to NuGet.org, winget, Homebrew, or any package manager (v0.1.0 is
  GitHub Releases + installer scripts only; managers are a later track).
- Code signing / notarization.
- Bundling or installing rtk.
- Any server/module behavior change beyond the handshake launch mode.
- Auto-update mechanics (re-running the installer is the update path).
- The universal wrapper CLI and destructive-cmdlet gate (still paused).
