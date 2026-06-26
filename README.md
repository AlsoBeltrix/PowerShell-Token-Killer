# pwsh_token_compressor

PowerShell-first token compression for agent workflows.

This is a structured-output compressor, not a Unix command wrapper. It captures
PowerShell objects before formatting, summarizes them by type and selected
properties, and renders compact text suitable for LLM tool output.

## Quick Start

```powershell
Import-Module .\src\PwshTokenCompressor.psd1 -Force

ptk ls .
ptk read .\README.md -MaxLines 20
ptk read .\src\PwshTokenCompressor.psm1 -Level aggressive
ptk smart .\src\PwshTokenCompressor.psm1
ptk savings .\src\PwshTokenCompressor.psm1
ptk grep "function" .\src
ptk run { Get-ChildItem . | Select-Object Name,Length }
```

Or use the launcher:

```powershell
.\ptk.ps1 ls .
.\ptk.ps1 read .\README.md -MaxLines 20
.\ptk.ps1 smart .\src\PwshTokenCompressor.psm1
.\ptk.ps1 run "Get-ChildItem . | Select-Object Name,Length"
```

## Smart Reads

`ptk read` supports four levels:

- `none`: preserve the requested text window.
- `minimal`: strip comments and repeated blank lines where safe.
- `aggressive`: keep structure, signatures, imports, exports, and drop most bodies.
- `summary`: emit a compact technical summary.

PowerShell files use the PowerShell parser/AST for function, class, alias, import,
and export detection. Other code uses conservative regex heuristics inspired by
RTK's filter levels and local heuristic summaries.

## Design Goals

- Keep PowerShell pipelines as PowerShell, not `cmd /C` strings.
- Compress objects before `Format-Table` turns them into lossy text.
- Use `pwsh -NoProfile -NonInteractive` for command-string execution.
- Preserve useful error, warning, and exit-code information.
- Stay safe by default: read/search/list helpers do not mutate state.

## Attribution

RTK inspired the filter-level model and heuristic summary approach. This project
uses a PowerShell-native implementation, including AST parsing for PowerShell
source files.
