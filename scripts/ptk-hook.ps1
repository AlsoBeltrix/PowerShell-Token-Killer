#Requires -Version 7
<#
.SYNOPSIS
Claude Code PreToolUse hook: redirects Bash/PowerShell tool calls to the ptk
MCP tool (mcp__ptk__ptk_invoke), where output is token-compressed and the
warm runspace persists state across calls (unified-shell-routing plan).

Cross-tool rewrite is impossible in the hook protocol (updatedInput is
same-tool only - slice-0 probe), so the redirect is deny-with-guidance: the
permissionDecisionReason is shown to the model verbatim.

Escape hatch: a command containing PTK_DIRECT (e.g. in a comment) is allowed
through for work that genuinely needs the harness shell - interactive
prompts, TTY-dependent tools, or commands that must run even when the ptk
server is down.

Installed by scripts/ptk_init.ps1; exits 0 with no output to allow a call.
#>
$ErrorActionPreference = 'Stop'

try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $command = [string]$payload.tool_input.command
    $cwd = if ($payload.PSObject.Properties['cwd']) { [string]$payload.cwd } else { '' }
} catch {
    # Unparseable input: never block the harness on our own failure.
    exit 0
}

if ([string]::IsNullOrWhiteSpace($command) -or $command -match 'PTK_DIRECT') {
    exit 0
}

# The warm runspace keeps its own current directory across calls, so a
# replayed command with relative paths must re-anchor to this call's cwd.
$cwdAdvice = if ($cwd) {
    " The warm runspace keeps its own current directory, so anchor the command: prefix it with: Set-Location '$cwd'; "
} else {
    ' '
}
$reason =
    'Shell commands run through ptk: call mcp__ptk__ptk_invoke with "script" set to this same command.' +
    $cwdAdvice +
    'It runs in a persistent warm PowerShell runspace (state and imported modules survive across calls) ' +
    'and output comes back token-compressed. Only if the command genuinely needs this harness shell ' +
    '(interactive/TTY, or ptk is unavailable), re-run it here with PTK_DIRECT in a comment.'

@{
    hookSpecificOutput = @{
        hookEventName            = 'PreToolUse'
        permissionDecision       = 'deny'
        permissionDecisionReason = $reason
    }
} | ConvertTo-Json -Depth 4 -Compress
