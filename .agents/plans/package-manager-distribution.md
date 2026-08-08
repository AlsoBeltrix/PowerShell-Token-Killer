# Plan: package-manager distribution

**Status: DRAFT. No slice is approved. D1 is the blocking decision and is
with the owner; every slice below is gated behind it.** Nothing here may be
implemented before its decision lands. Written 2026-08-07 against head
`3670bd9` (published releases `v0.2.0`, `v0.2.1`).

Goal, in the owner's words: "a real binary release so people don't have to
clone the repo to install", plus publication "via winget, brew, aur, and any
other package managers that make sense."

## Verified starting facts

Re-verify anything here before relying on it; all of it was measured on
2026-08-07 at `3670bd9`.

1. **Release archives already contain everything an install needs.** The
   published `ptk-0.2.1-<rid>.{zip,tar.gz}` expands to `bin/`, `scripts/`,
   `src/`, `LICENSE`, `README.md`, `VERSION`. `scripts/` holds
   `install.ps1`, `ptk_init.ps1`, `ptk-hook.ps1`,
   `ptk_install_transaction.psm1`.
2. **There is no `ptk` command anywhere.** `scripts/install.ps1` creates no
   symlink, shim, or PATH entry. The installed layout lives at `~/.ptk`, and
   harness registrations point at the absolute path
   `~/.ptk/bin/PtkMcpServer[.exe]`.
3. **The server binary has no CLI verbs.** `Program.cs` classifies an
   internal worker invocation, then starts the MCP host on stdio. There is
   no `install`, `init`, `register`, or `--version` verb.
   `scripts/install.ps1`'s header already anticipates one ("a future
   `PtkMcpServer install` verb can host it in-process").
4. **The documented public install is circular.** README says it installs
   "without cloning this repository", then instructs
   `pwsh -File scripts/install.ps1 -FromRelease` — a file that only exists
   inside a clone or inside an already-downloaded release archive. There is
   no bootstrap one-liner. This is the concrete gap behind the owner's
   request.
5. **Release binaries are signed from v0.2.1**: Windows Authenticode via
   Azure Trusted Signing (timestamped), macOS Developer ID + notarized.
   Verified against published assets, not build output. Package manifests
   that reference release assets therefore reference signed bits.
6. **rtk is a hard runtime dependency** — the server exits 78 without it —
   and rtk's own distribution is uneven. Its latest release publishes:
   `rtk-{x86_64,aarch64}-apple-darwin.tar.gz`,
   `rtk-aarch64-unknown-linux-gnu.tar.gz`,
   `rtk-x86_64-unknown-linux-musl.tar.gz`,
   `rtk-x86_64-pc-windows-msvc.zip`, `.deb` and `.rpm` for **x86_64 only**,
   and `checksums.txt`. There is **no Windows arm64 rtk** (ptk runs the x64
   build under emulation) and **no arm64 deb/rpm**. rtk is not in
   Homebrew, AUR, winget, or Scoop.
7. **`install.ps1` does far more than place files**: stage, smoke-test
   (full handshake, twice), snapshot the prior payload, activate as a unit,
   ensure rtk, then per-agent registration behind an interactive
   pacman-style consent prompt, with byte-identical rollback on any failure.

## The architectural problem

A package manager does exactly one of the seven things `install.ps1` does:
place files. It will not run an interactive consent prompt, will not edit a
user's harness configuration, and (Homebrew explicitly, AUR by convention,
winget by sandbox) will not fetch a second binary from the network during
install.

So package-manager distribution is not a packaging exercise. It requires
separating the payload from the wiring:

- **payload placement** — the package manager's job, or the bootstrap's;
- **wiring** — a user-run command after install, which must therefore exist
  as a stable CLI entry point on `PATH`.

Fact 2 says that entry point does not exist. Everything else in this plan
depends on creating it, which is why D1 blocks the plan.

## Decisions

| id | question | status |
|----|----------|--------|
| D1 | Shape of the `ptk` CLI entry point | **open — blocking, with the owner** |
| D2 | How rtk is satisfied under package managers | open, gated behind D1 |
| D3 | Which channels ship, and in what order | open, gated behind D1 |
| D4 | Bootstrap one-liner hosting and URL | open, gated behind D1 |
| D5 | Whether `ptk init` may run non-interactively by default | open, gated behind D1 |

### D1 — the `ptk` CLI entry point (blocking)

Every channel needs one command on `PATH` that is not "start an MCP server
on stdio". Options:

- **(a) Verb the existing binary.** Rename to `ptk` (or ship it as `ptk`)
  and add verbs: bare/`serve` keeps today's stdio behavior (harness
  registrations and the worker-classification path must be unchanged),
  plus `init`, `uninstall`, `version`, `doctor`. The installer's own header
  already names this direction. Cost: touches `Program.cs` argument
  handling, which is load-bearing — `WorkerProcessEntry.IsWorkerInvocation`
  must still win first, and stdout must stay pure JSON-RPC. Benefit: one
  artifact, no script dependency, works identically under every channel.
- **(b) Ship a `ptk` launcher script** next to the binary that dispatches to
  `scripts/*.ps1`. Cheaper to build; but it reintroduces a PowerShell-script
  dependency at exactly the layer package managers touch, needs two
  variants (`.ps1`/`.cmd` and a POSIX `sh`), and leaves the binary unable to
  describe itself.
- **(c) Do neither**; keep `install.ps1` as the only entry point and ship
  packages that merely drop the payload plus a README telling users to run
  a script. Rejected in this draft's judgment: it makes every package a
  half-install, and the "no clone" goal is only half met.

Recommendation: **(a)**, with `serve` as the explicit verb and bare
invocation preserved byte-for-byte for existing registrations.

### D2 — rtk under package managers

Fact 6 makes this genuinely awkward. Options:

- **(a) `ptk init` fetches rtk** exactly as `install.ps1` does today. Works
  in every ecosystem; means `brew install ptk` alone leaves a non-working
  server until `ptk init` runs. Honest if `ptk serve` exits 78 with a
  message naming `ptk init`.
- **(b) Declare a dependency.** Impossible today — rtk exists in no
  ecosystem this plan targets. Would require packaging rtk too (owner does
  not own rtk; upstream coordination).
- **(c) Bundle rtk inside ptk's archives.** Removes the network step and
  makes packages self-sufficient; costs a licensing review, couples release
  cadence to rtk's, and leaves the Windows-arm64 hole (fact 6) needing the
  x64 build shipped under emulation, which is already what ptk does.
- **(d) Require rtk first, fail clearly.** Simplest packaging, worst first
  run.

No recommendation offered until D1 lands, because (a) and (d) both depend on
where the "install rtk" logic lives.

### D3 — channels and order

Assessed against: does it reach ptk's users, can it be automated from the
release workflow, and what is the ongoing cost per release?

**Recommended, in order:**

1. **Scoop** (Windows). A JSON manifest in an owner-controlled bucket repo.
   Self-service, no review queue, trivial automation (`autoupdate` +
   checkhash). Lowest cost, immediate value; a good first channel to prove
   the release-workflow automation pattern.
2. **Homebrew tap** (macOS + Linux) — `AlsoBeltrix/homebrew-ptk`, formula
   pulling the per-arch release tarball. A **tap**, not homebrew-core:
   core imposes notability requirements (stars/forks/watchers) ptk does not
   meet yet, and core forbids formulae whose install fetches more binaries,
   which collides with D2(a). A tap has neither constraint. `brew tap
   AlsoBeltrix/ptk && brew install ptk`.
3. **winget** (Windows). Manifest PR into `microsoft/winget-pkgs`.
   `InstallerType: zip` + `NestedInstallerType: portable`, mapping
   `PtkMcpServer.exe` to the `ptk` alias. Validation is friendlier to signed
   installers (fact 5 helps). Cost: a PR per release, automatable with
   `wingetcreate` from the release workflow.
4. **AUR** (Arch). A `ptk-bin` PKGBUILD; self-service publishing, but needs
   an AUR account and SSH key registered by the owner, and the repo is
   pushed to, not PR'd. Low ongoing cost once wired.

**Deferred, defensible later:**

5. **`.deb` / `.rpm`.** rtk ships these, so there is precedent; but useful
   distribution needs a hosted apt/yum repository, and without one the user
   experience is no better than downloading a tarball. Revisit if demand
   appears.
6. **nixpkgs**, **mise/asdf plugins** — real but niche; each is ongoing
   maintenance for a small audience.

**Rejected, with reasons (do not silently revisit):**

- **snap / flatpak** — confinement is fundamentally hostile to what ptk
  does: spawn worker processes, read and write the user's harness config,
  execute arbitrary user commands. A confined ptk is a broken ptk.
- **Chocolatey** — moderation overhead for a Windows audience winget and
  Scoop already cover.
- **npm / pip** — wrong ecosystems; would be a shim that downloads the same
  binaries, adding a runtime dependency for nothing.
- **`dotnet tool`** — directly contradicts the self-contained design: the
  payload embeds its own PowerShell engine specifically so no SDK or runtime
  is required. A dotnet tool would reintroduce that requirement.

### D4 — bootstrap one-liner

The fix for fact 4, independent of any package manager and useful
immediately:

```
# Windows / anywhere with pwsh
irm https://ptk.<host>/install.ps1 | iex

# POSIX without pwsh present
curl -fsSL https://ptk.<host>/install.sh | sh
```

Open sub-questions: whether to serve from `raw.githubusercontent.com` (zero
infrastructure, ugly URL, rate-limited) or a custom domain/redirect; and how
arguments reach a piped script (`iex` cannot take parameters — the usual
answer is `& ([scriptblock]::Create((irm ...))) -FromRelease`, which is
ugly, or an env-var protocol). Note the existing unqueued candidate "a POSIX
bootstrap so macOS/Linux can install without `pwsh` already present" is the
same work and should be merged into this decision rather than tracked twice.

### D5 — non-interactive `ptk init`

Package-manager users land in a shell, not in the installer's interactive
consent flow. `install.ps1` already supports `-Agent`/`-SkipAgent`/
`-AllAgents` and wires all harnesses with a notice when non-interactive.
The question is whether `ptk init` with no arguments should prompt (current
installer behavior) or refuse and require explicit flags. Registration edits
the user's harness config, so defaulting to "wire everything found" without
a prompt is a consent question, not a UX one.

## Slices

Each slice is independently landable and independently verifiable. **No
slice may start before D1 (and its own listed decisions) are ruled.**

### Slice 1 — the `ptk` entry point (gated: D1)

Implements whatever D1 rules. If D1 = (a):

- Add verb dispatch to `Program.cs` **after** the existing
  `WorkerProcessEntry.IsWorkerInvocation(args)` check, which must remain the
  first executable action.
- Bare invocation and any argument shape today's harness registrations use
  must behave byte-for-byte as now. Existing registrations point at the
  binary with no verb; breaking that breaks every installed user.
- `ptk serve` is the explicit synonym; `ptk version` prints the version and
  exits 0 without requiring rtk (today's exit-78 gate must not block a
  version query); `ptk init` hosts the registration logic; `ptk doctor`
  reports rtk resolution and harness registration state.
- Ship the binary under the name `ptk` in the layout, keeping
  `PtkMcpServer` as the internal/worker name if renaming the file breaks
  worker relaunch (verify: `WorkerLaunchCommand` and both platform
  launchers resolve the current executable path — confirm whether any of
  them hard-codes the file name before renaming).

**Verification:** existing battery green; the handshake still passes
launching the binary bare; a new test pins that a worker invocation is
still classified before any verb parsing; `ptk version` succeeds with rtk
absent.

### Slice 2 — split wiring from payload (gated: D1, D5)

Extract the registration half of `install.ps1` behind `ptk init` so it can
run against an already-placed payload that the script did not stage. The
payload-placement half stays for the bootstrap path.

**Verification:** `ptk init` against a payload extracted by hand (no
`install.ps1` involvement) registers harnesses and passes the same checks
the installer's registration step passes today; uninstall still reverses
registration.

### Slice 3 — bootstrap one-liner (gated: D4)

Publish the bootstrap entry point D4 selects, plus README rewrite fixing
fact 4's circularity.

**Verification:** on a machine with no clone, the documented one-liner
installs a working ptk; on macOS/Linux without `pwsh`, the POSIX variant
does too. Both verified against the *published* release, not a checkout.

### Slice 4 — Scoop bucket (gated: D2, D3)

Owner-controlled bucket repo with a manifest per architecture; release
workflow updates version + hash on publish.

**Verification:** `scoop install ptk` on a clean Windows VM yields a `ptk`
on PATH that passes `ptk doctor`; the ARM64 VM
(`.agents/machines.md` §`10.1.10.212`) is the arm64 bench.

### Slice 5 — Homebrew tap (gated: D2, D3)

`AlsoBeltrix/homebrew-ptk` with a formula selecting the per-arch tarball.

**Verification:** `brew tap && brew install ptk` on macOS arm64 produces a
working `ptk`; `brew audit --strict` passes; formula pulls the signed
release asset and the installed binary still verifies with `codesign`.

### Slice 6 — winget (gated: D2, D3)

`microsoft/winget-pkgs` manifest, zip + nested portable, `ptk` alias.
Automate submission with `wingetcreate` from the release workflow.

**Verification:** manifest passes `winget validate` and the sandbox test;
install on both Windows RIDs.

### Slice 7 — AUR (gated: D2, D3)

`ptk-bin` PKGBUILD. Requires an AUR account and registered SSH key (owner
action, cannot be done by an agent).

**Verification:** `makepkg -si` in a clean Arch container; `namcap` clean.

### Slice 8 — release-workflow automation (gated: whichever of 4-7 land)

Every channel above is a per-release chore unless automated. Extend
`.github/workflows/release.yml` (or a follow-on workflow triggered by
publication) to update each channel's manifest with the new version and
hashes. A channel without automation is a channel that goes stale; treat
this slice as part of shipping any channel, not as optional polish.

**Verification:** a dry-run release updates every manifest with correct
hashes computed from the actual published assets.

## Non-goals

- Publishing rtk to any ecosystem. ptk does not own rtk.
- homebrew-core, until notability requirements are plausibly met.
- Any channel whose confinement model breaks process spawning or harness
  config editing (see D3 rejections).
