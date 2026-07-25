# Repo-Specific Guidance
<!-- Extends AGENTS.md; never overrides it. Rules and pointers only — state
     lives in .agents/state.md, decisions in .agents/decisions.md. -->

## Mission

PowerShell Token Killer (`ptk`; module on disk `PwshTokenCompressor`, named
after rtk) captures PowerShell objects before they are formatted to text and
renders compact typed summaries for LLM tool use. It is a structured-output
compressor, not a Unix-command wrapper (`README.md`).

A personal/team tool, not an org-wide product. Larger architectural changes are
triggered by *experienced* benefit on real daily usage — never anticipated need,
and never a tool's self-reported savings metric. Feature-level gates live in the
Open Decisions of `.agents/decisions.md`.

## Reading Order

`AGENTS.md` → this file → `.agents/state.md` → `.agents/decisions.md` →
`README.md` → `src/PwshTokenCompressor.psm1` + `tests/PwshTokenCompressor.Tests.ps1`
→ `server/README.md` + `server/PtkMcpServer/Program.cs`.

## Verification

```
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
dotnet test server/PtkMcpServer.slnx
pwsh -NoProfile -File server/test-handshake.ps1
```

The handshake is a manual stdio check; run it when server-facing code changes.
Pass counts are not recorded here — read them from the run. `.github/workflows/ci.yml`
runs the same battery on an ubuntu/windows/macos matrix; local verification
before claiming completion still applies.

Host-specific requirements (physical `TMPDIR` on macOS, reaching the Windows
box) live in `.agents/machines.md`.

## Remotes

`master` tracks `origin/master`. The configured set is clone-local — confirm
with `git remote -v` rather than trusting this list.

- `origin` — `https://github.com/AlsoBeltrix/PowerShell-Token-Killer.git`
- `gitea` — `http://q:3000/michael/Powershell-Token-Killer.git` (local mirror)
- `github` — `https://github.com/roethlar/Powershell-Token-Killer` (presumed
  personal mirror)

Push policy: `.agents/push-policy.md`.

## Earned Practices

- **Agent experience leads on model-facing guidance text** (owner, 2026-07-10,
  sd3-1). ptk's model-visible wording — tool descriptions, in-band markers,
  nudge text, refusal guidance — is guidance by an agent for an agent. Where an
  approved plan's letter runs contrary to what the implementing agent and
  reviewer judge works best for model interaction, lean toward that judgment and
  surface the question rather than shipping wording the agents believe misleads.

- **A Windows-gated test failure is not evidence of a Windows-specific defect**
  (2026-07-25, `r6x-2`). All three defects in that finding presented as
  Windows-only and none were; the gate was a fact about coverage. Reproduce the
  scenario on another platform before assuming the Windows host is required —
  each check took minutes against days of assuming otherwise.

- **A green suite is not a correct fix** (2026-07-25, `r6x-2` #3). A fix that
  passed on both platforms was reopened in review for sealing truncated output
  as complete and for never sealing at all past the 5-minute call deadline.
  Prefer a second party running the guard proof over one's own confidence.
