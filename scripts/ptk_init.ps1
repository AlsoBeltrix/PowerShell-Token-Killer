#Requires -Version 7
<#
.SYNOPSIS
Installs (or removes) the ptk redirect hook in Claude Code settings, mirroring
`rtk init` semantics: local project settings by default, -Global for
~/.claude/settings.json, plus -Show / -Uninstall / -DryRun.

.DESCRIPTION
Adds one PreToolUse entry (matcher "Bash|PowerShell") running
scripts/ptk-hook.ps1, which redirects shell tool calls to mcp__ptk__ptk_invoke
(deny-with-guidance; PTK_DIRECT in a command is the escape hatch). Existing
hooks - including rtk's own Bash rewrite hook - are preserved; ptk's deny
takes precedence over a concurrent rewrite by the hook protocol. Idempotent:
re-running replaces the ptk entry instead of duplicating it. Takes effect at
the next Claude Code session start.

.EXAMPLE
pwsh -File scripts/ptk_init.ps1 -Global           # install for all projects
pwsh -File scripts/ptk_init.ps1 -Global -Uninstall
pwsh -File scripts/ptk_init.ps1 -Show
#>
[CmdletBinding()]
param(
    [switch]$Global,
    [switch]$Show,
    [switch]$Uninstall,
    [switch]$DryRun,
    # Test seam / explicit target; overrides the -Global/local default.
    [string]$SettingsPath
)
$ErrorActionPreference = 'Stop'

$hookScript = Join-Path $PSScriptRoot 'ptk-hook.ps1'
if (-not $SettingsPath) {
    $SettingsPath = if ($Global) {
        Join-Path $HOME '.claude' 'settings.json'
    } else {
        Join-Path (Get-Location) '.claude' 'settings.json'
    }
}
# The marker every ptk-owned entry is recognized by (install, show, uninstall).
$hookMarker = 'ptk-hook.ps1'
$hookCommand = 'pwsh -NoProfile -File "{0}"' -f $hookScript

function Read-PtkSettings {
    if (Test-Path -LiteralPath $SettingsPath) {
        $raw = Get-Content -LiteralPath $SettingsPath -Raw
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            return $raw | ConvertFrom-Json -AsHashtable
        }
    }
    @{}
}

function Test-PtkEntry {
    param([object]$Entry)
    foreach ($hook in @($Entry['hooks'])) {
        if ($null -ne $hook -and [string]$hook['command'] -like "*$hookMarker*") { return $true }
    }
    $false
}

$settings = Read-PtkSettings
$preToolUse = @()
if ($settings.ContainsKey('hooks') -and $settings['hooks'].ContainsKey('PreToolUse')) {
    $preToolUse = @($settings['hooks']['PreToolUse'])
}
$installed = @($preToolUse | Where-Object { Test-PtkEntry $_ }).Count -gt 0

if ($Show) {
    Write-Host "settings: $SettingsPath"
    Write-Host ("ptk hook: {0}" -f ($installed ? 'INSTALLED' : 'not installed'))
    Write-Host "hook script: $hookScript $((Test-Path -LiteralPath $hookScript) ? '' : '(MISSING)')"
    return
}

# Drop any existing ptk entries (uninstall, and the replace half of install).
$preToolUse = @($preToolUse | Where-Object { -not (Test-PtkEntry $_) })

if (-not $Uninstall) {
    $preToolUse += @{
        matcher = 'Bash|PowerShell'
        hooks   = @(@{ type = 'command'; command = $hookCommand })
    }
}

if (-not $settings.ContainsKey('hooks')) { $settings['hooks'] = @{} }
if ($preToolUse.Count -gt 0) {
    $settings['hooks']['PreToolUse'] = $preToolUse
} else {
    $settings['hooks'].Remove('PreToolUse')
    if ($settings['hooks'].Count -eq 0) { $settings.Remove('hooks') }
}

$json = $settings | ConvertTo-Json -Depth 32
if ($DryRun) {
    Write-Host "DRY RUN - would write ${SettingsPath}:"
    Write-Host $json
    return
}

$dir = Split-Path -Parent $SettingsPath
if ($dir -and -not (Test-Path -LiteralPath $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
Set-Content -LiteralPath $SettingsPath -Value $json -NoNewline
Write-Host ("ptk hook {0} in {1} (takes effect next Claude Code session)" -f
    ($Uninstall ? 'removed' : 'installed'), $SettingsPath)
