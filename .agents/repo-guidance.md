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

Confirmed automated verification commands (re-run 2026-08-05 at `f99a92b`).
Counts are volatile — treat them as of that commit and re-verify rather than
trusting them at a later head.

```
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
```
— 107 passed, 1 platform-skipped (PowerShell module/setup suite; requires
Pester 5 or later), as of `f99a92b`.

```
dotnet test server/PtkMcpServer.slnx
```
— 1,132/1,132 passed (C# MCP supervisor, named workers, containment, output,
and retained administration suite), as of `f99a92b`. Run it from a plain
shell: from a ptk session the four `StateToolTests` module probes can fail
together on a truncated `PSModulePath` (see `.agents/machines.md`).

```
dotnet test siem/PtkSiem.slnx
```
— 247/247 passed (standalone retained SIEM receiver suite) in CI and on hosts
whose identity may create symlinks. On a Windows host without
`SeCreateSymbolicLinkPrivilege` it is 226/247: the 21 failures stop in symlink
test setup before any product assertion (see `.agents/machines.md`), and are
not a product failure. This clone's host is one of those — re-confirmed
226/247 at `f99a92b`.

```
dotnet list server/PtkMcpServer.slnx package --vulnerable --include-transitive
```
— every server project reported no vulnerable packages, re-confirmed at
`f99a92b`. Treat any listed package as a failed production dependency check
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
— 16/16 checks against an **installed** candidate, not a checkout: the five
tools, warm named sessions, object compression, trusted-type rendering, text
preservation, `ptk_output` recovery, timeout recovery, reset/close, compound
native routing, and the RTK startup gate (exit 78 naming `PTK_RTK_PATH`).
This is the release gate for a packaged artifact; the handshake proves the
transport, this proves the product contract. Run it per selected platform
before publishing.

Release artifacts are built by `.github/workflows/release.yml` on a `v*` tag
or `workflow_dispatch`. It builds each of the five RIDs on its own native
runner, smoke-tests and RTK-gate-proves every artifact there, and assembles a
**draft** release only — publishing is an owner action. There is one public
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

Remote configuration is per-clone and churns. In this clone (`git remote -v`,
confirmed 2026-08-05) exactly one remote is configured:

- `origin` — `https://github.com/AlsoBeltrix/PowerShell-Token-Killer.git`
  (GitHub renamed the repo to capital-W `PowerShell-Token-Killer`; the URL
  was updated to match on the owner's go, 2026-07-10). `master` tracks
  `origin/master`.

Remotes recorded here previously and since absent from this clone's git
config: `github` (`roethlar/Powershell-Token-Killer`, the owner's fork) and
`gitea` (`http://q:3000/michael/Powershell-Token-Killer`, LAN forge), both
added between the 2026-08-03 and 2026-08-04 checks and gone by 2026-08-05;
and `personal` (`roethlar/-PowerShell-Token-Killer`), gone by 2026-07-03.
Re-check `git remote -v` per clone rather than trusting this list. Push
policy stays in `.agents/push-policy.md`, not here.

## Earned Practices

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
