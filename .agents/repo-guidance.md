# Repo-Specific Guidance
<!-- Extends AGENTS.md; never overrides it. Rules and pointers only — state
     lives in .agents/state.md. -->

## Mission Detail

PowerShell Token Killer (invoked as `ptk`; the module on disk is named
`PwshTokenCompressor`, and the name credits rtk, the Rust Token Killer) is a
PowerShell-first token-compression tool for agent workflows: it captures PowerShell objects before they are
formatted to text, summarizes them by type and selected properties, and
renders compact output for LLM tool use. It is a structured-output compressor,
not a Unix-command wrapper (see `README.md`).

PTK targets a global public release for unaffiliated users on its supported
platforms. It is not confined to personal or team use. Product, documentation,
packaging, security, compatibility, and support decisions must therefore be
safe and understandable without access to the owner's environment. The product
go/no-go gate was decided **GO 2026-07-08** (unqualified; archived in
`docs/history/decisions-archive.md`). Individual larger features still require
their own evidence and approval; the global audience is not blanket authority
for speculative architecture.

## Simplicity

Prefer the smallest design that solves an observed, recurring problem. An
accepted operational interruption is not itself a requirement to engineer it
away. Do not add speculative continuity, resilience, compatibility, migration,
or retained-version machinery without concrete evidence and explicit owner
approval.

## Reading Order

1. `AGENTS.md`
2. `.agents/repo-guidance.md` (this file)
3. `.agents/state.md`
4. `.agents/decisions.md`
5. `README.md`
6. `src/PwshTokenCompressor.psm1` and `tests/PwshTokenCompressor.Tests.ps1`
7. `server/README.md` and `server/PtkMcpServer/Program.cs` (warm-runspace MCP
   server; see `.agents/plans/warm-runspace-mcp-server.md`)

## Verification

Confirmed automated verification commands (re-run 2026-08-05 at `78b2dbb`,
on macOS arm64). Counts are volatile — treat them as of that commit and
re-verify rather than trusting them at a later head. Host-conditional results
are noted per command; per-host records live in `.agents/machines.md`, never
as a "this clone" claim here, since this file is shared by every clone.

```
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
```
— 107 passed, 1 platform-skipped (PowerShell module/setup suite; requires
Pester 5 or later), as of `78b2dbb`.

```
dotnet test server/PtkMcpServer.slnx
```
— 1,151/1,151 passed (C# MCP supervisor, named workers, containment, output,
and retained administration suite), as of `78b2dbb`. Prefer a plain shell:
from a ptk session the four `StateToolTests` module probes can fail together
on a truncated `PSModulePath` (see `.agents/machines.md`) — not universal,
the `78b2dbb` run above was clean from a ptk session.

```
dotnet test siem/PtkSiem.slnx
```
— 247/247 passed (standalone retained SIEM receiver suite) in CI and on hosts
whose identity may create symlinks, re-confirmed 247/247 at `78b2dbb`. On a
Windows host without `SeCreateSymbolicLinkPrivilege` it is 226/247: the 21
failures stop in symlink test setup before any product assertion (see
`.agents/machines.md` §`ASHBIAMWEB1`), and are not a product failure.

```
dotnet list server/PtkMcpServer.slnx package --vulnerable --include-transitive
```
— all five server projects reported no vulnerable packages, re-confirmed at
`78b2dbb`. Treat any listed package as a failed production dependency check
even if the command itself returns zero.

```
pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand -TimeoutSec 90
```
— passed the stdout-clean direct-checkout launch and complete five-tool,
multi-session stdio handshake. Run manually when server-facing setup or code
changes.

```
pwsh -NoProfile -File server/direct-product-proof.ps1 -ServerPath <installed>/bin/PtkMcpServer[.exe]
```
— 24/24 on Windows (22 elsewhere; the extra two are the Defender
scan-completion and payload-survival checks, r806-4) against an
**installed** candidate, not a checkout: the five
tools, warm named sessions, object compression, trusted-type rendering, a
type needing the wider assembly set (#42), text preservation, `ptk_output`
recovery, timeout recovery, reset/close, compound native routing, the fresh
session's `ls` alias and `PSModuleAutoloadingPreference=None`, the positive
audit-journaling gate (audit-restoration R6: the packaged bits must journal
the proof's own calls — nonempty artifacts carrying real records under an
isolated `$HOME`-rooted audit root; temp-dir roots are refused on macOS
because `/var` is a symlink), and the RTK
startup gate (exit 78 naming `PTK_RTK_PATH`). On Windows it also scans the
packaged bits with Defender, asserting both that the scan completed (exit 0
or 2; an absent or failing scanner fails the gate — r806-4) and that the
payload survives — quarantine of
the supported Windows artifact is a release blocker independent of #7's WDSI
verdict. It writes no EICAR control: a release gate must not manufacture
antivirus detections on the operator's machine.
This is the release gate for a packaged artifact; the handshake proves the
transport, this proves the product contract. Run it per selected platform
before publishing.

Add `-UninstallHome <installed-root>` to also run the plan's uninstall check
(two further checks). It is destructive and opt-in, refuses unless that root contains
the server under proof (a path-component test, and the root must be a
directory literally named `.ptk` — r806-3), and must be pointed at a
throwaway home — never the operator's real `~/.ptk`. Install into an isolated home by setting
`USERPROFILE`, `HOME`, `HOMEDRIVE` and `HOMEPATH` on the child process;
`$HOME` itself is read-only, and on Windows PowerShell derives it from
`HOMEDRIVE`+`HOMEPATH`, not `USERPROFILE`.

Release artifacts are built by `.github/workflows/release.yml` on a `v*` tag
or `workflow_dispatch`. It builds each of the five RIDs on its own native
runner, smoke-tests and RTK-gate-proves every artifact there, and assembles a
**draft** release only — publishing is an owner action. Since
audit-restoration R6, each leg also publishes, signs, smokes
(no-config refusal naming `PTK_SIEM_CONFIG`), and archives the standalone
SIEM receiver as its own artifact (`ptk-siem-receiver-<version>-<rid>`), so
a draft carries ten artifacts, and the macOS notarization envelope covers
both payloads. The osx-arm64 leg
additionally Developer ID-signs every Mach-O (hardened runtime; executables
get `server/macos-signing-entitlements.plist` — the JITting .NET runtime
dies under hardening without it) **before** the gates run, then notarizes
the payload after them; both steps fail closed when the five signing
secrets (`MACOS_CERT_P12_BASE64`, `MACOS_CERT_PASSWORD`, `APPLE_ID`,
`APPLE_APP_SPECIFIC_PASSWORD`, `APPLE_TEAM_ID`) are absent. Bare
executables in a tarball cannot carry a stapled ticket, so notarization
acceptance lives on Apple's side and Gatekeeper checks it online. Both
`win-x64` and `win-arm64` legs Azure Trusted Signing-sign every `.exe`/
`.dll` before the gates run, via the `github-signing` app registration
(client-secret auth, no OIDC step; scoped to only the Certificate Profile
Signer role on the signing account) and the `public-trust` certificate
profile under account `roethlar-app-signing`
(`AZURE_TENANT_ID`/`AZURE_CLIENT_ID`/`AZURE_CLIENT_SECRET`/
`AZURE_SIGNING_ENDPOINT`/`AZURE_SIGNING_ACCOUNT` secrets). There is one public
installer, `scripts/install.ps1` (`3109ec1`, 2026-08-04); the former root
`install.ps1`/`install.sh` pair was consolidated into it and deleted.

CI exists as of 2026-07-08 (release-plan slice 2): `.github/workflows/ci.yml`
runs the same battery (Pester, server/SIEM tests, handshake) on an
ubuntu/windows/macos matrix for pushes to `master`/`ci/**` and PRs to
`master`. It first installs RTK on every platform, since the server refuses to
start without it. Local verification before claiming completion still applies.
The
machine-readable `.agents/repo-map.json` record was removed by the governance
refresh at `8e6624c`; this section is now the only record of the verification
battery. (`.agents/plans/release-distribution.md` and
`.agents/plans/warm-runspace-mcp-server.md` still instruct agents to update
that deleted file — stale plan text, not an instruction to recreate it.)

## Remotes & Sync

Remote configuration is per-clone and churns — this list has been recorded
wrong in both directions inside three days, so **run `git remote -v` in your
own clone and trust that, not this file.** Only the durable facts belong
here:

- `origin` is the canonical GitHub remote:
  `https://github.com/AlsoBeltrix/PowerShell-Token-Killer.git` (GitHub
  renamed the repo to capital-W `PowerShell-Token-Killer`; the URL was
  updated to match on the owner's go, 2026-07-10). `master` tracks
  `origin/master`.
- Other remotes appear and disappear per clone and are not required by any
  workflow. Names seen at least once: `github`
  (`roethlar/Powershell-Token-Killer`, the owner's fork), `gitea`
  (`http://q:3000/michael/Powershell-Token-Killer`, LAN forge), and
  `personal` (`roethlar/-PowerShell-Token-Killer`).

- **`gh` resolves its default repo to the `github` fork remote when that
  remote exists** (observed 2026-08-07: a `gh workflow run` landed on
  `roethlar/…` instead of canonical and had to be canceled). Always pass
  `-R AlsoBeltrix/PowerShell-Token-Killer` explicitly for `gh` operations.

Push policy stays in `.agents/push-policy.md`, not here.

## Earned Practices

- **Known-broken means pre-authorized (owner, 2026-08-07, blanket).** The
  owner's words: "assume that if it's broken and we all know it's broken
  that I need it fixed. don't make me sign off on every fix. blanket: FIX
  IT." A diagnosed and verified defect needs no per-fix owner go — fix it,
  guard it, commit it, record it, in the same motion. Still separately
  gated: tags/publishes and other outward-facing release actions, scope
  beyond the repair, and fixes that fork an undecided design. Reviewer
  dispatch is likewise not implied by a fix; it happens on the owner's
  word or an explicitly recorded cadence.

- **Agent experience leads on model-facing guidance text (owner,
  2026-07-10, sd3-1 adjudication).** ptk's model-visible wording — tool
  descriptions, in-band markers, nudge text, refusal guidance — is
  guidance by an agent for an agent. When an approved plan's letter runs
  contrary to what the implementing agent and the reviewer judge works
  best for model interaction, lean toward that judgment and surface the
  final question to the owner rather than implementing wording the agents
  believe misleads. Incident: sd3-1 (`.agents/review/findings/sd3-1.md`)
  — the plan's per-surface pairing requirement would have put a useless
  recovery suggestion inside every elision marker; the owner delegated
  the call and the marker stayed lean, with the D2 amendment recorded in
  `.agents/decisions.md`.
