# PwshTokenCompressor (`pwsh_token_compressor`)

PowerShell-first tool output compression for agent workflows.

Use `ptk` when an agent is doing PowerShell-heavy work and the normal shell
output is too noisy, too large, or loses useful object shape. It keeps common
PowerShell output compact enough for tool responses while still showing the
parts an agent usually needs.

## What You Get

- `ptk_invoke`: an MCP shell tool backed by a warm PowerShell runspace. Use this
  for agent shell work so modules, variables, and connections can survive
  across calls.
- `ptk`: a local PowerShell command for compact file reads, directory listings,
  searches, summaries, and object compression.
- Optional Claude Code hook: redirects ordinary Bash/PowerShell tool calls
  toward `ptk_invoke`.

This is not a sandbox. Commands run with the same authority as the shell or MCP
client that calls them.

## Quick Start: Agent Use

Verify the MCP server:

```powershell
dotnet test server/PtkMcpServer.slnx
pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand -TimeoutSec 90
```

Inside this repo, `.mcp.json` already registers the server as `ptk` for Claude
Code. For use from other project directories, register it user-wide:

```powershell
claude mcp add ptk --scope user -- dotnet run -v q --project <path-to-repo>/server/PtkMcpServer
```

Then use `ptk_invoke` for shell work. Typical calls are ordinary PowerShell or
native commands:

```powershell
Get-ChildItem . -Recurse
Get-Process
git status --short
Import-Module ActiveDirectory
```

To install the Claude Code redirect hook:

```powershell
pwsh -File scripts/ptk_init.ps1 -Global
```

Use `PTK_DIRECT` in a command comment when a command genuinely needs the normal
harness shell instead of `ptk_invoke`.

Full server setup and hook details are in [server/README.md](server/README.md).

## Quick Start: Local CLI

```powershell
Import-Module .\src\PwshTokenCompressor.psd1 -Force

ptk ls .
ptk read .\README.md -MaxLines 20
ptk smart .\src\PwshTokenCompressor.psm1
ptk grep "function" .\src
ptk run { Get-ChildItem . | Select-Object Name,Length }
Get-ChildItem . | ptk compress -MaxItems 5
```

Or use the repo launcher without importing the module first:

```powershell
.\ptk.ps1 read .\README.md -MaxLines 20
.\ptk.ps1 run "Get-ChildItem . | Select-Object Name,Length"
```

Local command reference is in [docs/usage.md](docs/usage.md).

## Common Uses

- Keep recursive directory listings readable.
- Read or summarize source files without flooding the agent context.
- Search files and return compact match groups.
- Run PowerShell commands that return objects before they become formatted text.
- Keep expensive module imports warm during an agent session.

## Verification

Current local verification commands:

```powershell
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
dotnet test server/PtkMcpServer.slnx
pwsh -NoProfile -File server/test-handshake.ps1
```

For the same launch path used by `.mcp.json`, run:

```powershell
pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand -TimeoutSec 90
```

## More Docs

- [Local CLI usage](docs/usage.md)
- [MCP server setup](server/README.md)
