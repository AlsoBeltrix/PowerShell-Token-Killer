#Requires -Version 7
<#
.SYNOPSIS
Per-agent ptk init: installs (or inspects/removes) ptk's redirect hook and
guidance nudge for each supported agent harness, mirroring `rtk init`
semantics: user-level by default, -Show / -Uninstall / -DryRun, idempotent
marker-owned entries.

.DESCRIPTION
Multi-harness init (.agents/plans/multi-harness-init.md): three independent
layers per harness - registration (the MCP server), enforcement (a blocking
pre-tool hook, only where live-verified), nudge (a marker-delimited guidance
block in the harness's user-level instructions file). Implemented legs:
claude, codex, grok, agy, kimi.

kimi leg: registration by direct merge into <kimi-home>/mcp.json (no
scriptable kimi CLI surface exists - /mcp-config is TUI-only), a PreToolUse
deny hook in <kimi-home>/config.toml ([[hooks]] matcher "Bash", kimi's shell
tool; the documented hook protocol accepts ptk-hook.ps1's stdout deny JSON
verbatim), and the shared nudge block in <kimi-home>/AGENTS.md, the
documented global instruction file. Kimi home is $KIMI_CODE_HOME when set,
else ~/.kimi-code. The hook is gated like claude's: it steers shell calls at
the ptk tools, so it ships only when a registration can answer (already
registered, or the installed payload exists).

codex leg: idempotent registration (`codex mcp get ptk` answers -> the
existing entry is left as-is; otherwise `codex mcp add ptk -- <installed
binary>`) and the guidance block in ~/.codex/AGENTS.md. No hook: codex
hooks are trust-gated (plan: Evidence).

claude leg: registers the server user-scope (claude mcp add, remove-then-add
so re-installs never collide), checks the installed payload (~/.ptk) and
refuses registration and the hook when it is missing - a redirect hook
without a server steers every shell call at a tool that cannot answer; run
scripts/install.ps1 first. Installs one PreToolUse entry (matcher
"Bash|PowerShell") running scripts/ptk-hook.ps1 (deny-with-guidance;
PTK_DIRECT in a command is the escape hatch) and the ptk guidance block in
~/.claude/CLAUDE.md (standard layer, no opt-in - it is also grok's nudge
home). Existing hooks - including rtk's own Bash rewrite hook - are
preserved. Idempotent: re-running replaces ptk-owned entries instead of
duplicating them. Takes effect at the next session start.

Consent: a detection-mode install (no -Agent) asks once, pacman-style -
a numbered list of the detected harnesses and a single skip selection
(1,3; 2-4; 0=skip all; Enter=install all). Declined or -SkipAgent'd
harnesses print a manual-setup blurb. -AllAgents answers yes to everything;
a non-interactive session installs all with a notice. -Uninstall and -Show
never ask.

BREAKING CHANGE vs the single-harness version of this script: installs are
USER-LEVEL BY DEFAULT (~/.claude/settings.json). The old project-local
default is behind -Local, which warns: repo-file writes collide with
content-tracking tooling.

.EXAMPLE
pwsh -File scripts/ptk_init.ps1                     # detected agents, user level
pwsh -File scripts/ptk_init.ps1 -Show
pwsh -File scripts/ptk_init.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    # Agent legs to run; default is every supported agent detected on this
    # machine - except -Uninstall, which defaults to EVERY supported leg
    # (mhi-10: detection must not scope removal). 'all' forces every leg.
    # Validated manually (not ValidateSet) because `pwsh -File` passes
    # "codex,grok" as one literal string; comma-separated values are split
    # and accepted.
    [string[]]$Agent,

    # Harnesses to exclude (comma-separated accepted, like -Agent). Prints
    # the same manual-setup blurb a prompt decline would.
    [string[]]$SkipAgent,

    # Answer yes to every harness: no consent prompt. Cannot be combined
    # with -Agent/-SkipAgent.
    [switch]$AllAgents,

    # Claude-only opt-in: patch the project-local .claude/settings.json (the
    # pre-multi-harness default) instead of the user-level file.
    [switch]$Local,

    # Deprecated: user-level is the default now. Accepted so older callers
    # (an installed install.ps1 from a previous payload) keep working.
    [switch]$Global,

    [switch]$Show,
    [switch]$Uninstall,
    [switch]$DryRun,

    # Explicit targets / test seams. -SettingsPath is claude-only;
    # -NudgePath applies to whichever SINGLE leg is selected (default
    # claude). Providing either restricts the run to exactly one leg.
    [string]$SettingsPath,
    [string]$NudgePath,
    # Per-leg config targets (test seams).
    [string]$CodexConfigPath,
    [string]$GrokConfigPath,
    [string]$AgyPluginRoot,
    [string]$AgyConfigPath,
    [string]$KimiMcpPath,
    [string]$KimiConfigPath,
    # Where the installed payload lives (test seam).
    [string]$PtkHome = (Join-Path $HOME '.ptk')
)
$ErrorActionPreference = 'Stop'

$supportedAgents = @('claude', 'codex', 'grok', 'agy', 'kimi')

# Normalize -Agent: split comma-joined values (`pwsh -File` hands
# "codex,grok" over as one string), trim, lowercase, validate.
$Agent = @($Agent |
    ForEach-Object { [string]$_ -split ',' } |
    ForEach-Object { $_.Trim().ToLowerInvariant() } |
    Where-Object { $_ })
foreach ($name in $Agent) {
    if ($name -notin ($supportedAgents + 'all')) {
        throw ("Unknown agent '{0}'. Supported: {1}, all." -f $name, ($supportedAgents -join ', '))
    }
}
# -SkipAgent normalizes the same way ('all' is not meaningful there).
$SkipAgent = @($SkipAgent |
    ForEach-Object { [string]$_ -split ',' } |
    ForEach-Object { $_.Trim().ToLowerInvariant() } |
    Where-Object { $_ })
foreach ($name in $SkipAgent) {
    if ($name -notin $supportedAgents) {
        throw ("Unknown agent '{0}' in -SkipAgent. Supported: {1}." -f $name, ($supportedAgents -join ', '))
    }
}
if ($AllAgents -and ($Agent -or $SkipAgent)) {
    throw '-AllAgents cannot be combined with -Agent or -SkipAgent.'
}
if ($Agent -and $SkipAgent) {
    throw '-Agent and -SkipAgent are mutually exclusive.'
}

# The marker every ptk-owned settings entry is recognized by (install, show,
# uninstall).
$hookMarker = 'ptk-hook.ps1'
# Register the INSTALLED copy whenever it exists: checkouts move and get
# renamed, stranding registrations that then fail open silently on every
# shell call (issue #2). The checkout sibling is only the fallback for
# payload-less runs (test seams).
$installedHook = Join-Path $PtkHome 'scripts' 'ptk-hook.ps1'
# -PathType Leaf: a DIRECTORY at the script path would satisfy a bare
# Test-Path and re-create the silent fail-open (pwsh -File <dir>) (i2-3).
$hookScript = (Test-Path -LiteralPath $installedHook -PathType Leaf) ? $installedHook : (Join-Path $PSScriptRoot 'ptk-hook.ps1')
$hookCommand = 'pwsh -NoProfile -File "{0}"' -f $hookScript

# The -File target of a ptk-owned hook command, for staleness checks; $null
# when the shape is unrecognized.
function Get-PtkHookCommandTarget {
    param([string]$Command)
    ([string]$Command -match '-File\s+"([^"]+)"') ? $Matches[1] : $null
}

# Markers delimiting the ptk-owned block in a nudge (guidance) file. The
# wording is harness-neutral and conditional ("when available") so the same
# block is safe on machines where ptk is not registered, and tool names carry
# no per-harness prefix (three schemes exist - docs/harness-support.md).
$nudgeBegin = '<!-- ptk-guidance -->'
$nudgeEnd = '<!-- /ptk-guidance -->'
$nudgeBlock = @"
$nudgeBegin
When the ptk MCP server is available, use ptk_invoke for shell commands
instead of the built-in shell tools. One MCP connection can own several named
warm PowerShell sessions; each session has its own long-lived worker/runspace,
so modules, connections, variables, and working directory stay isolated.
Unqualified calls use default; use ptk_session to list/open/close sessions and
session=... to select one. The dialect is PowerShell 7, not bash: translate bash-only syntax.
Wrap a bash script whole as bash -lc '...' where bash exists. For long work
that needs warm state, raise timeoutSeconds. ptk_state
diagnoses the selected session; ptk_reset restores that session to factory
state. Compressed output preserves errors, exit codes, and structure.
raw=true is deprecated compatibility telemetry and does not change execution or shaping.
route=pwsh is exact PowerShell consent independent of raw, with normal capture and shaping.
If a response returns a ptk_output handle, use it to read the immutable same-invocation artifact
without rerunning the command. When the ptk tools are not available in this
session, use the normal shell tools.
$nudgeEnd
"@

function Test-PtkAgentPresent {
    param([Parameter(Mandatory)][string]$Name)
    if (Get-Command -Name $Name -ErrorAction SilentlyContinue) { return $true }
    # Claude Code is configured through files; an existing ~/.claude is as
    # good a detection signal as the CLI. Kimi is file-configured the same
    # way (no scriptable CLI registration surface), so ~/.kimi-code counts.
    # (Parenthesized: -and/-or are left-associative in PowerShell.)
    (($Name -eq 'claude') -and (Test-Path -LiteralPath (Join-Path $HOME '.claude'))) -or
    (($Name -eq 'kimi') -and (Test-Path -LiteralPath (Get-PtkKimiHome)))
}

# Kimi's data root: $KIMI_CODE_HOME when set, else ~/.kimi-code (the mcp.json,
# config.toml, and global AGENTS.md all move with it).
function Get-PtkKimiHome {
    [string]$env:KIMI_CODE_HOME ? $env:KIMI_CODE_HOME : (Join-Path $HOME '.kimi-code')
}

function Read-PtkSettings {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) {
        $raw = Get-Content -LiteralPath $Path -Raw
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

# Text with the ptk-owned block removed. Surgical (mhi-7): strips exactly
# the shape the installer writes - an optional blank-line separator pair,
# the marker block, one trailing newline - so user content and its
# whitespace stay byte-identical and install/uninstall round-trips. Repeated
# cycles are stable: each install's strip removes exactly what the previous
# install added.
function Get-PtkNudgeStripped {
    param([string]$Text)
    $pattern = '(?s)(?:\r?\n\r?\n)?{0}.*?{1}(?:\r?\n)?' -f
        [regex]::Escape($nudgeBegin), [regex]::Escape($nudgeEnd)
    [regex]::Replace([string]$Text, $pattern, '')
}

function Install-PtkNudgeBlock {
    param([Parameter(Mandatory)][string]$Path)
    $existing = (Test-Path -LiteralPath $Path) ? (Get-Content -LiteralPath $Path -Raw) : ''
    $kept = Get-PtkNudgeStripped $existing
    $nl = [Environment]::NewLine
    $content = ($kept ? ($kept + $nl + $nl) : '') + $nudgeBlock.Trim() + $nl
    if ($DryRun) {
        Write-Host "DRY RUN - would write ${Path}:"
        Write-Host $content
        return
    }
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    Set-Content -LiteralPath $Path -Value $content -NoNewline
    Write-Host "ptk guidance block installed in $Path"
}

function Uninstall-PtkNudgeBlock {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $existing = Get-Content -LiteralPath $Path -Raw
    if ($existing -notlike "*$nudgeBegin*") { return }
    if ($DryRun) {
        Write-Host "DRY RUN - would remove the ptk guidance block from $Path"
        return
    }
    Set-Content -LiteralPath $Path -Value (Get-PtkNudgeStripped $existing) -NoNewline
    Write-Host "ptk guidance block removed from $Path"
}

# --- per-harness consent ---------------------------------------------------
# Pacman-style: one numbered list, one skip selection. Declined or
# -SkipAgent'd harnesses get a manual-setup blurb so nothing is a dead end.

function Get-PtkManualBlurb {
    param([Parameter(Mandatory)][string]$Name)
    $binary = Join-Path $PtkHome 'bin' ($IsWindows ? 'PtkMcpServer.exe' : 'PtkMcpServer')
    switch ($Name) {
        'claude' { "  by hand: claude mcp add --scope user ptk `"$binary`"" }
        'codex'  { "  by hand: codex mcp add ptk -- `"$binary`"" }
        'grok'   { "  by hand: grok mcp add -s user ptk `"$binary`"" }
        'agy'    { '  by hand: no CLI registration surface - re-run the leg to write the plugin' }
        'kimi'   { "  by hand: add to ~/.kimi-code/mcp.json under `"mcpServers`": `"ptk`": {`"command`": `"$binary`", `"args`": []}" }
    }
}

function Write-PtkSkipNotice {
    param([Parameter(Mandatory)][string]$Name)
    Write-Host ("[{0}] skipped. Later: pwsh -File `"{1}`" -Agent {0}" -f $Name, $PSCommandPath)
    Write-Host (Get-PtkManualBlurb -Name $Name)
}

# Returns the harness names the user chose to skip. Enter = skip nothing;
# tokens are 1-based numbers, ranges (2-4), or 0 for everything; separators
# are commas/semicolons/whitespace. Invalid input re-asks.
function Read-PtkConsentSkips {
    param([Parameter(Mandatory)][string[]]$Detected)
    $numbered = for ($i = 0; $i -lt $Detected.Count; $i++) { '{0} {1}' -f ($i + 1), $Detected[$i] }
    Write-Host ("Found: {0}" -f ($numbered -join ', '))
    while ($true) {
        $answer = Read-Host 'Skip (1,3; 2-4; 0=skip all; Enter=install all)'
        if ([string]::IsNullOrWhiteSpace($answer)) { return @() }
        $picked = @{}
        $valid = $true
        foreach ($token in ($answer -split '[,;\s]+' | Where-Object { $_ })) {
            if ($token -eq '0') {
                1..$Detected.Count | ForEach-Object { $picked[$_] = $true }
            }
            elseif ($token -match '^(\d+)-(\d+)$' -and [int]$Matches[1] -ge 1 -and
                    [int]$Matches[2] -le $Detected.Count -and [int]$Matches[1] -le [int]$Matches[2]) {
                [int]$Matches[1]..[int]$Matches[2] | ForEach-Object { $picked[$_] = $true }
            }
            elseif ($token -match '^\d+$' -and [int]$token -ge 1 -and [int]$token -le $Detected.Count) {
                $picked[[int]$token] = $true
            }
            else { $valid = $false; break }
        }
        if ($valid) {
            return @($picked.Keys | Sort-Object { [int]$_ } | ForEach-Object { $Detected[[int]$_ - 1] })
        }
        Write-Host "Unrecognized selection '$answer' - try again."
    }
}

function Invoke-PtkClaudeLeg {
    $target = $SettingsPath
    if (-not $target) {
        $target = $Local ?
            (Join-Path (Get-Location) '.claude' 'settings.json') :
            (Join-Path $HOME '.claude' 'settings.json')
    }
    $nudgeTarget = $NudgePath ? $NudgePath : (Join-Path $HOME '.claude' 'CLAUDE.md')

    $settings = Read-PtkSettings -Path $target
    $preToolUse = @()
    if ($settings.ContainsKey('hooks') -and $settings['hooks'].ContainsKey('PreToolUse')) {
        $preToolUse = @($settings['hooks']['PreToolUse'])
    }
    $installed = @($preToolUse | Where-Object { Test-PtkEntry $_ }).Count -gt 0
    # Payload = the server binary itself, as a leaf (hcc-2): registration
    # points at that exact file, so a leftover or damaged bin/ directory
    # must not pass the gate - it would replace a working registration with
    # a dead one and arm a deny hook toward a server that cannot start.
    $payloadPresent = Test-Path -LiteralPath (
        Join-Path $PtkHome 'bin' ($IsWindows ? 'PtkMcpServer.exe' : 'PtkMcpServer')) -PathType Leaf
    $nudgePresent = (Test-Path -LiteralPath $nudgeTarget) -and
        ((Get-Content -LiteralPath $nudgeTarget -Raw) -like "*$nudgeBegin*")

    # ptk-owned entries whose registered -File target no longer exists: they
    # fail open silently on every shell call (issue #2) - flag on -Show,
    # name them when an install replaces them.
    $staleTargets = @(foreach ($entry in $preToolUse) {
        foreach ($hook in @($entry['hooks'])) {
            if ($null -ne $hook -and [string]$hook['command'] -like "*$hookMarker*") {
                $hookTarget = Get-PtkHookCommandTarget ([string]$hook['command'])
                # Leaf: a directory at the target still fails open (i2-3).
                if ($hookTarget -and -not (Test-Path -LiteralPath $hookTarget -PathType Leaf)) { $hookTarget }
            }
        }
    })

    if ($Show) {
        Write-Host "[claude] settings: $target"
        $hookState = if (-not $installed) { 'not installed' }
        elseif ($staleTargets.Count -gt 0) {
            'INSTALLED but STALE - registered file missing: {0} (re-run this script to heal)' -f ($staleTargets -join ', ')
        }
        else { 'INSTALLED' }
        Write-Host "[claude] ptk hook: $hookState"
        $registration = 'unknown (claude CLI not found)'
        if (Get-Command claude -ErrorAction SilentlyContinue) {
            claude mcp get ptk *> $null
            $registration = ($LASTEXITCODE -eq 0) ? 'REGISTERED' : 'not registered'
        }
        Write-Host "[claude] registration: $registration (user scope)"
        Write-Host "[claude] hook script: $hookScript $((Test-Path -LiteralPath $hookScript) ? '' : '(MISSING)')"
        Write-Host ("[claude] nudge block: {0} in {1}" -f ($nudgePresent ? 'INSTALLED' : 'not installed'), $nudgeTarget)
        Write-Host ("[claude] installed payload: {0} ({1})" -f
            ($payloadPresent ? 'present' : 'MISSING - run scripts/install.ps1'), $PtkHome)
        return $true
    }

    if ($Local) {
        Write-Warning (('-Local targets {0}, which lives inside a repo: anything tracking that file ' +
            'by content (governance refresh tooling, dotfile managers) will flag the edit as an ' +
            'owner modification forever after. The user-level default avoids this.') -f $target)
    }
    elseif (-not $SettingsPath -and -not $Uninstall) {
        Write-Host ('NOTE: ptk_init now installs USER-LEVEL by default (~/.claude/settings.json); ' +
            'the old project-local behavior is -Local.')
    }

    if (-not $Uninstall -and -not $payloadPresent) {
        # Registration gate: enforce only where the steered-to tool can
        # answer. A hook without an installed server denies every shell call
        # while pointing at nothing.
        Write-Warning (('No installed ptk payload at {0}. Run scripts/install.ps1 first - it ' +
            'publishes the server to ~/.ptk and registers it with Claude Code - then re-run ' +
            'this script.') -f $PtkHome)
        if (-not $DryRun) { return $false }
        Write-Host 'DRY RUN - continuing to show what an install would write.'
    }

    # Registration lives in this leg (folded in from install.ps1 with the
    # per-harness consent work) so one claude consent covers every claude
    # change. Remove-then-add: re-installs and dev<->release switches never
    # collide. The hook gate (mhi-6/mhi-9) is registration-based: the hook
    # blocks shell calls to steer them at MCP ptk, which only answers where
    # Claude Code can see the server - no registration, no hook. Skip ONLY
    # the hook: the nudge below is conditionally worded, safe everywhere,
    # and grok's single layer.
    $binary = Join-Path $PtkHome 'bin' ($IsWindows ? 'PtkMcpServer.exe' : 'PtkMcpServer')
    $claudeCli = Get-Command claude -ErrorAction SilentlyContinue
    $registered = $false
    if ($Uninstall) {
        if ($claudeCli) {
            if ($DryRun) {
                Write-Host 'DRY RUN - would run: claude mcp remove --scope user ptk'
            }
            else {
                claude mcp remove --scope user ptk *> $null
                Write-Host (($LASTEXITCODE -eq 0) ?
                    '[claude] registration removed.' : '[claude] no registration to remove.')
            }
        }
    }
    elseif (-not $claudeCli) {
        Write-Warning (('[claude] claude CLI not found - not registering, not installing the ' +
            'blocking hook. Register ptk first (claude mcp add --scope user ptk "{0}"), then ' +
            're-run this script. Installing the guidance block only.') -f $binary)
    }
    elseif ($DryRun) {
        Write-Host (('DRY RUN - would run: claude mcp remove --scope user ptk; ' +
            'claude mcp add --scope user ptk "{0}"') -f $binary)
        $registered = $true  # show the hook write a real run would perform
    }
    else {
        claude mcp remove --scope user ptk *> $null
        claude mcp add --scope user ptk $binary | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Warning (('[claude] claude mcp add failed - not installing the blocking hook ' +
                '(it would steer at a tool claude cannot see). Register manually: ' +
                'claude mcp add --scope user ptk "{0}"') -f $binary)
        }
        else {
            Write-Host '[claude] registered with Claude Code (user scope).'
            $registered = $true
        }
    }
    $skipHook = -not $Uninstall -and -not $registered

    if ($skipHook) {
        # The gate that decided this (CLI absent or registration failed)
        # already printed its warning - nothing further to say here.
    }
    elseif ($Uninstall -and -not $installed) {
        # Nothing ptk-owned in the settings: skip the write entirely rather
        # than create or reformat a file just to remove nothing.
        Write-Host "[claude] no ptk hook entry in $target - nothing to remove."
    }
    else {
        # Remove OUR hook surgically: strip it from each entry's hooks array
        # and drop an entry only when nothing remains, so a foreign hook
        # sharing an entry with ours (one matcher, several hooks) survives
        # install and uninstall.
        $preToolUse = @(foreach ($entry in $preToolUse) {
            $kept = @(@($entry['hooks']) | Where-Object {
                $null -ne $_ -and [string]$_['command'] -notlike "*$hookMarker*"
            })
            if ($kept.Count -gt 0) {
                $entry['hooks'] = $kept
                $entry
            }
        })

        if (-not $Uninstall) {
            $preToolUse += @{
                matcher = 'Bash|PowerShell'
                hooks   = @(@{ type = 'command'; command = $hookCommand })
            }
        }

        if (-not $settings.ContainsKey('hooks')) { $settings['hooks'] = @{} }
        if ($preToolUse.Count -gt 0) {
            $settings['hooks']['PreToolUse'] = $preToolUse
        }
        else {
            $settings['hooks'].Remove('PreToolUse')
            if ($settings['hooks'].Count -eq 0) { $settings.Remove('hooks') }
        }

        $json = $settings | ConvertTo-Json -Depth 32
        if ($DryRun) {
            Write-Host "DRY RUN - would write ${target}:"
            Write-Host $json
        }
        else {
            $dir = Split-Path -Parent $target
            if ($dir -and -not (Test-Path -LiteralPath $dir)) {
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
            }
            Set-Content -LiteralPath $target -Value $json -NoNewline
            if (-not $Uninstall -and $staleTargets.Count -gt 0) {
                Write-Host ("[claude] replaced STALE registration (pointed at missing {0})" -f
                    ($staleTargets -join ', '))
            }
            Write-Host ("[claude] ptk hook {0} in {1} (takes effect next session)" -f
                ($Uninstall ? 'removed' : 'installed'), $target)
        }
    }

    if ($Uninstall) {
        Uninstall-PtkNudgeBlock -Path $nudgeTarget
    }
    else {
        # Standard layer, no opt-in (owner amendment 2026-07-09): the block
        # is idempotent, marker-owned, and conditionally worded - and it is
        # grok's ONLY layer (grok session-loads this file, no hook).
        Install-PtkNudgeBlock -Path $nudgeTarget
    }
    (-not $skipHook)
}

# mhi-12: `codex mcp remove ptk` strips only the base [mcp_servers.ptk]
# table. Tool-approval subtables codex itself writes when the user approves
# ptk tools ([mcp_servers.ptk.tools.*]) stay behind, and codex then refuses
# to load its own config ("invalid transport" - a server table with no
# command): the CLI is bricked and cannot self-repair, because every codex
# command starts by loading the config. These helpers are the ONE place the
# leg touches ~/.codex/config.toml directly - they act only on ptk.-scoped
# subtables (never the base table), the exact orphans left by removing our
# registration. The sweep runs after the CLI remove (and when the CLI is
# absent), healing both fresh uninstalls and machines an earlier uninstall
# already bricked.
function Test-PtkCodexOrphanTable {
    param([Parameter(Mandatory)][string]$Path)
    (Test-Path -LiteralPath $Path -PathType Leaf) -and
        ((Get-Content -LiteralPath $Path -Raw) -match '(?m)^\s*\[mcp_servers\.ptk\.')
}

function Remove-PtkCodexOrphanTable {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-PtkCodexOrphanTable -Path $Path)) { return $false }
    $kept = [System.Collections.Generic.List[string]]::new()
    $inPtk = $false
    foreach ($line in (Get-Content -LiteralPath $Path)) {
        if ($line -match '^\s*\[mcp_servers\.ptk\.') { $inPtk = $true; continue }
        if ($inPtk) {
            if ($line -match '^\s*\[') { $inPtk = $false }
            else { continue }
        }
        $kept.Add($line)
    }
    Set-Content -LiteralPath $Path -Value ($kept -join "`n")
    return $true
}

# codex leg: registration (idempotent - an existing entry is left as-is so
# user customizations like env blocks survive) + nudge in ~/.codex/AGENTS.md.
# No hook: codex hooks are trust-gated and unresolved (plan: Evidence table).
# The nudge is written even when registration fails - its wording is
# conditional ("when available"), safe on machines where ptk never arrives.
function Invoke-PtkCodexLeg {
    $nudgeTarget = $NudgePath ? $NudgePath : (Join-Path $HOME '.codex' 'AGENTS.md')
    $binary = Join-Path $PtkHome 'bin' ($IsWindows ? 'PtkMcpServer.exe' : 'PtkMcpServer')
    $cli = Get-Command codex -ErrorAction SilentlyContinue
    $nudgePresent = (Test-Path -LiteralPath $nudgeTarget) -and
        ((Get-Content -LiteralPath $nudgeTarget -Raw) -like "*$nudgeBegin*")

    if ($Show) {
        Write-Host ("[codex] cli: {0}" -f ($cli ? $cli.Source : 'NOT FOUND'))
        $registration = 'unknown (codex CLI not found)'
        if ($cli) {
            codex mcp get ptk *> $null
            $registration = ($LASTEXITCODE -eq 0) ? 'REGISTERED' : 'not registered'
        }
        Write-Host "[codex] registration: $registration"
        Write-Host ("[codex] nudge block: {0} in {1}" -f
            ($nudgePresent ? 'INSTALLED' : 'not installed'), $nudgeTarget)
        return $true
    }

    if ($Uninstall) {
        $codexConfig = $CodexConfigPath ? $CodexConfigPath : (Join-Path $HOME '.codex' 'config.toml')
        if ($DryRun) {
            Write-Host 'DRY RUN - would run: codex mcp remove ptk'
            if (Test-PtkCodexOrphanTable -Path $codexConfig) {
                Write-Host ('DRY RUN - would sweep orphaned [mcp_servers.ptk.*] tables from {0}' -f $codexConfig)
            }
        }
        else {
            if ($cli) {
                codex mcp remove ptk *> $null
                Write-Host (($LASTEXITCODE -eq 0) ?
                    '[codex] registration removed.' : '[codex] no registration to remove.')
            }
            else {
                # mhi-10 re-grade: the CLI left PATH, so the config is read
                # directly - a stale [mcp_servers.ptk] entry gets a manual-
                # removal warning (grok-leg parity), not a false "no
                # registration" report. Warn, not edit: the base entry is
                # valid config a reinstalled CLI can manage.
                if ((Test-Path -LiteralPath $codexConfig -PathType Leaf) -and
                    ((Get-Content -LiteralPath $codexConfig -Raw) -match '(?m)^\s*\[mcp_servers\.ptk\]')) {
                    Write-Warning (('[codex] codex CLI not found - remove the [mcp_servers.ptk] ' +
                        'entry from {0} manually.') -f $codexConfig)
                }
                else {
                    Write-Host '[codex] codex CLI not found - no registration to remove.'
                }
            }
            # mhi-12: sweep the tool-approval subtables `codex mcp remove`
            # leaves behind - orphaned, they make the whole config
            # unloadable and brick the codex CLI.
            if (Remove-PtkCodexOrphanTable -Path $codexConfig) {
                Write-Host (('[codex] swept orphaned [mcp_servers.ptk.*] tables from {0} ' +
                    '(codex mcp remove leaves them; orphaned they make the config unloadable).') -f $codexConfig)
            }
        }
        Uninstall-PtkNudgeBlock -Path $nudgeTarget
        return $true
    }

    $ok = $true
    if ($DryRun) {
        Write-Host ("DRY RUN - would ensure registration (skipped when codex mcp get ptk " +
            "already answers): codex mcp add ptk -- `"{0}`"" -f $binary)
    }
    elseif (-not $cli) {
        Write-Warning (('[codex] codex CLI not found on PATH - register manually: ' +
            'codex mcp add ptk -- "{0}"') -f $binary)
        $ok = $false
    }
    else {
        # Probe FIRST: an existing registration - including a custom one
        # pointing somewhere other than ~/.ptk - is left as-is, so the
        # payload gate below only guards the add it would actually perform
        # (mhi-8).
        codex mcp get ptk *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host '[codex] already registered - left as is (codex mcp get ptk answers).'
        }
        elseif (-not (Test-Path -LiteralPath $binary)) {
            # Registering a missing exe writes a broken config.toml entry.
            Write-Warning (('[codex] no installed ptk server at {0}. Run scripts/install.ps1 ' +
                'first, then re-run this script.') -f $binary)
            $ok = $false
        }
        else {
            codex mcp add ptk '--' $binary | Out-Host
            if ($LASTEXITCODE -ne 0) {
                Write-Warning (('[codex] codex mcp add failed - register manually: ' +
                    'codex mcp add ptk -- "{0}"') -f $binary)
                $ok = $false
            }
            else {
                Write-Host '[codex] registered (user-level ~/.codex/config.toml).'
            }
        }
    }

    # Standard layer, no opt-in (owner amendment 2026-07-09); written even
    # when registration failed - the wording is conditional, safe on
    # machines where ptk never arrives.
    Install-PtkNudgeBlock -Path $nudgeTarget
    $ok
}

# grok leg: registration presence is read from ~/.grok/config.toml (format
# live-verified 2026-07-08, docs/harness-support.md); an existing
# [mcp_servers.ptk] entry is left as-is, otherwise `grok mcp add -s user`
# (the slice-0-verified surface) registers the installed binary. No hook:
# no verified grok hook surface exists. grok session-loads
# ~/.claude/CLAUDE.md (VERIFIED), so its nudge is the same block the claude
# leg owns there - this leg ensures it exists on install and leaves
# removal to the claude leg's uninstall.
function Invoke-PtkGrokLeg {
    $configPath = $GrokConfigPath ? $GrokConfigPath : (Join-Path $HOME '.grok' 'config.toml')
    $nudgeTarget = $NudgePath ? $NudgePath : (Join-Path $HOME '.claude' 'CLAUDE.md')
    $binary = Join-Path $PtkHome 'bin' ($IsWindows ? 'PtkMcpServer.exe' : 'PtkMcpServer')
    $cli = Get-Command grok -ErrorAction SilentlyContinue
    $registered = (Test-Path -LiteralPath $configPath -PathType Leaf) -and
        ((Get-Content -LiteralPath $configPath -Raw) -match '(?m)^\s*\[mcp_servers\.ptk\]')
    $nudgePresent = (Test-Path -LiteralPath $nudgeTarget) -and
        ((Get-Content -LiteralPath $nudgeTarget -Raw) -like "*$nudgeBegin*")

    if ($Show) {
        Write-Host ("[grok] cli: {0}" -f ($cli ? $cli.Source : 'NOT FOUND'))
        Write-Host ("[grok] registration: {0} ({1})" -f
            ($registered ? 'REGISTERED' : 'not registered'), $configPath)
        Write-Host ("[grok] nudge block: {0} in {1} (shared with the claude leg)" -f
            ($nudgePresent ? 'INSTALLED' : 'not installed'), $nudgeTarget)
        return $true
    }

    if ($Uninstall) {
        if ($DryRun) {
            Write-Host 'DRY RUN - would run: grok mcp remove -s user ptk'
        }
        elseif ($registered -and $cli) {
            grok mcp remove -s user ptk *> $null
            if ($LASTEXITCODE -eq 0) { Write-Host '[grok] registration removed.' }
            else {
                Write-Warning (('[grok] grok mcp remove failed (the remove syntax mirrors the ' +
                    'verified add form but is not itself live-verified) - remove the ' +
                    '[mcp_servers.ptk] entry from {0} manually.') -f $configPath)
            }
        }
        elseif ($registered) {
            Write-Warning (('[grok] grok CLI not found - remove the [mcp_servers.ptk] entry ' +
                'from {0} manually.') -f $configPath)
        }
        else {
            Write-Host '[grok] no registration to remove.'
        }
        # The nudge block in ~/.claude/CLAUDE.md is claude-leg-owned;
        # removing it here would strip claude's nudge too.
        return $true
    }

    $ok = $true
    if ($DryRun) {
        Write-Host (('DRY RUN - would ensure registration (skipped when {0} already has ' +
            '[mcp_servers.ptk]): grok mcp add -s user ptk "{1}"') -f $configPath, $binary)
    }
    elseif ($registered) {
        # Probe before gates (the mhi-8 lesson): an existing entry -
        # including a custom one - is left as-is regardless of payload.
        Write-Host ('[grok] already registered - left as is ({0} has [mcp_servers.ptk]).' -f $configPath)
    }
    elseif (-not $cli) {
        Write-Warning (('[grok] grok CLI not found on PATH - register manually: ' +
            'grok mcp add -s user ptk "{0}"') -f $binary)
        $ok = $false
    }
    elseif (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
        Write-Warning (('[grok] no installed ptk server at {0}. Run scripts/install.ps1 ' +
            'first, then re-run this script.') -f $binary)
        $ok = $false
    }
    else {
        grok mcp add -s user ptk $binary | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Warning (('[grok] grok mcp add failed - register manually: ' +
                'grok mcp add -s user ptk "{0}"') -f $binary)
            $ok = $false
        }
        else {
            Write-Host ('[grok] registered (user-level {0}).' -f $configPath)
        }
    }

    # Standard layer, written even on registration failure (conditional
    # wording, safe on ptk-less machines).
    Install-PtkNudgeBlock -Path $nudgeTarget
    $ok
}

# agy (Antigravity) leg: one user-level plugin directory at
# ~/.gemini/config/plugins/ptk/ carrying plugin.json, rules/ptk.md (the
# nudge), and mcp_config.json (registration) - the last omitted when the
# global ~/.gemini/config/mcp_config.json already registers ptk, which is
# left as-is. NO hooks.json: agy's deny-with-guidance hook is documented
# but its live firing has never been demonstrated; enforcement is deferred
# until the owner's install run meets the verify-once bar (plan amendment
# 2026-07-09). Whether agy auto-discovers plugins from this root is a
# documented open probe for that same run. Install = write the directory;
# uninstall = remove it (the global config entry is never touched).
function Invoke-PtkAgyLeg {
    $pluginRoot = $AgyPluginRoot ? $AgyPluginRoot : (Join-Path $HOME '.gemini' 'config' 'plugins')
    $pluginDir = Join-Path $pluginRoot 'ptk'
    $globalConfig = $AgyConfigPath ? $AgyConfigPath : (Join-Path $HOME '.gemini' 'config' 'mcp_config.json')
    $binary = Join-Path $PtkHome 'bin' ($IsWindows ? 'PtkMcpServer.exe' : 'PtkMcpServer')

    $globallyRegistered = $false
    if (Test-Path -LiteralPath $globalConfig -PathType Leaf) {
        try {
            $cfg = Get-Content -LiteralPath $globalConfig -Raw | ConvertFrom-Json -AsHashtable
            $globallyRegistered = $null -ne $cfg -and $cfg.ContainsKey('mcpServers') -and
                $cfg['mcpServers'].ContainsKey('ptk')
        }
        catch { }
    }
    $pluginPresent = Test-Path -LiteralPath (Join-Path $pluginDir 'plugin.json') -PathType Leaf

    if ($Show) {
        Write-Host ("[agy] plugin: {0} ({1})" -f
            ($pluginPresent ? 'INSTALLED' : 'not installed'), $pluginDir)
        Write-Host ("[agy] global registration: {0} ({1})" -f
            ($globallyRegistered ? 'REGISTERED' : 'not registered'), $globalConfig)
        Write-Host '[agy] hook: not shipped (deferred pending live verification)'
        return $true
    }

    if ($Uninstall) {
        if ($DryRun) {
            Write-Host "DRY RUN - would remove $pluginDir"
        }
        elseif (Test-Path -LiteralPath $pluginDir) {
            Remove-Item -LiteralPath $pluginDir -Recurse -Force
            Write-Host "[agy] plugin removed ($pluginDir)."
        }
        else {
            Write-Host '[agy] no plugin to remove.'
        }
        # The global mcp_config.json entry (owner-installed, slice 0) is
        # never touched here.
        return $true
    }

    if ($DryRun) {
        Write-Host (('DRY RUN - would write the ptk plugin to {0}: plugin.json, rules/ptk.md{1}. ' +
            'No hooks.json - agy enforcement is deferred pending a live demonstration.') -f
            $pluginDir, ($globallyRegistered ? ' (registration exists globally - left as-is)' : ', mcp_config.json'))
        if (Test-Path -LiteralPath (Join-Path $pluginDir 'hooks.json') -PathType Leaf) {
            Write-Host 'DRY RUN - would remove the pre-existing hooks.json (the shipped plugin carries no hook).'
        }
        return $true
    }

    if (-not $globallyRegistered -and -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
        Write-Warning (('[agy] no installed ptk server at {0}. Run scripts/install.ps1 ' +
            'first, then re-run this script.') -f $binary)
        return $false
    }

    New-Item -ItemType Directory -Path (Join-Path $pluginDir 'rules') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $pluginDir 'plugin.json') -Value (@{
        name        = 'ptk'
        description = 'PowerShell Token Killer: warm-runspace shell with token-compressed output.'
    } | ConvertTo-Json)
    Set-Content -LiteralPath (Join-Path $pluginDir 'rules' 'ptk.md') -Value ($nudgeBlock.Trim() + [Environment]::NewLine) -NoNewline
    if ($globallyRegistered) {
        Write-Host '[agy] global registration exists - left as-is; the plugin carries rules only.'
        # A stale plugin-level registration from an earlier install must not
        # double-register against the global entry.
        Remove-Item -LiteralPath (Join-Path $pluginDir 'mcp_config.json') -Force -ErrorAction SilentlyContinue
    }
    else {
        Set-Content -LiteralPath (Join-Path $pluginDir 'mcp_config.json') -Value (@{
            mcpServers = @{ ptk = @{ command = $binary; args = @() } }
        } | ConvertTo-Json -Depth 4)
        Write-Host '[agy] plugin carries the registration (no global entry found).'
    }
    # The shipped plugin carries NO hooks.json (enforcement deferred pending
    # live verification): a stale hook from an earlier attempt would stay
    # active - an unverified deny hook blocking agy commands - while this
    # run reports that no hook ships (mhi-11). Install enforces the
    # documented no-hook end-state. When a verified hooks.json ships, this
    # removal must become marker/ownership-aware.
    $hooksPath = Join-Path $pluginDir 'hooks.json'
    if (Test-Path -LiteralPath $hooksPath -PathType Leaf) {
        Remove-Item -LiteralPath $hooksPath -Force
        Write-Host '[agy] pre-existing hooks.json removed - the shipped plugin carries no hook.'
    }
    Write-Host ("[agy] plugin installed ($pluginDir). No hook: enforcement deferred pending " +
        'live verification; first install run should also confirm agy discovers the plugin.')
    $true
}

# kimi leg: registration in <kimi-home>/mcp.json (mcpServers.ptk entry, JSON
# merged in place - there is no scriptable kimi CLI surface; /mcp-config is
# TUI-only), a PreToolUse deny hook in <kimi-home>/config.toml, and the
# shared nudge block in <kimi-home>/AGENTS.md (documented global instruction
# file). The kimi hook protocol passes the same stdin shape the claude hook
# reads (tool_input.command) and accepts the same stdout deny JSON
# ptk-hook.ps1 already emits, so the one hook script serves both harnesses.
# Kimi's matcher targets its shell tool by name: "Bash" (there is no
# PowerShell tool). Hook failure on the kimi side is fail-open by design
# (documented), matching the nudge's conditional wording.

# Markers delimiting the ptk-owned [[hooks]] block in kimi's config.toml
# (TOML comments), so install/uninstall round-trips leave user content
# byte-identical - the same contract as the nudge markers.
$kimiHookBegin = '# >>> ptk-hook (managed by ptk_init.ps1 - do not edit between the markers)'
$kimiHookEnd = '# <<< ptk-hook'
# TOML basic string for the hook command (hcc-3): literal strings cannot
# contain an apostrophe, and the hook path is user-controlled
# (C:\Users\O'Brien). Escape backslashes and double quotes.
function ConvertTo-PtkTomlBasicString {
    param([string]$Text)
    '"' + (($Text -replace '\\', '\\') -replace '"', '\"') + '"'
}
$kimiHookBlock = @"
$kimiHookBegin
[[hooks]]
event = "PreToolUse"
matcher = "Bash"
command = $(ConvertTo-PtkTomlBasicString $hookCommand)
$kimiHookEnd
"@

function Get-PtkKimiHookStripped {
    param([string]$Text)
    $pattern = '(?s)(?:\r?\n\r?\n)?{0}.*?{1}(?:\r?\n)?' -f
        [regex]::Escape($kimiHookBegin), [regex]::Escape($kimiHookEnd)
    [regex]::Replace([string]$Text, $pattern, '')
}

# The -File target of the ptk-owned kimi hook block, for staleness checks;
# $null when no marked block or an unrecognized shape.
function Get-PtkKimiHookTarget {
    param([string]$Text)
    if ([string]$Text -match ('(?s){0}(.*?){1}' -f
        [regex]::Escape($kimiHookBegin), [regex]::Escape($kimiHookEnd))) {
        # Undo the TOML basic-string escaping (hcc-3) before shape-parsing,
        # or Windows backslashes read back doubled and every install looks
        # stale.
        return Get-PtkHookCommandTarget ($Matches[1] -replace '\\(.)', '$1')
    }
    $null
}

function Invoke-PtkKimiLeg {
    $kimiHome = Get-PtkKimiHome
    $mcpPath = $KimiMcpPath ? $KimiMcpPath : (Join-Path $kimiHome 'mcp.json')
    $configPath = $KimiConfigPath ? $KimiConfigPath : (Join-Path $kimiHome 'config.toml')
    $nudgeTarget = $NudgePath ? $NudgePath : (Join-Path $kimiHome 'AGENTS.md')
    $binary = Join-Path $PtkHome 'bin' ($IsWindows ? 'PtkMcpServer.exe' : 'PtkMcpServer')

    $mcp = Read-PtkSettings -Path $mcpPath
    $registered = $mcp.ContainsKey('mcpServers') -and $null -ne $mcp['mcpServers'] -and
        ($mcp['mcpServers'] -is [System.Collections.IDictionary]) -and
        $mcp['mcpServers'].Contains('ptk')
    $payloadPresent = Test-Path -LiteralPath $binary -PathType Leaf
    $configText = (Test-Path -LiteralPath $configPath -PathType Leaf) ?
        (Get-Content -LiteralPath $configPath -Raw) : ''
    $hookInstalled = $configText -like "*$kimiHookBegin*"
    $hookTarget = Get-PtkKimiHookTarget $configText
    $hookStale = $hookInstalled -and $hookTarget -and
        (-not (Test-Path -LiteralPath $hookTarget -PathType Leaf))
    $nudgePresent = (Test-Path -LiteralPath $nudgeTarget) -and
        ((Get-Content -LiteralPath $nudgeTarget -Raw) -like "*$nudgeBegin*")

    if ($Show) {
        Write-Host ("[kimi] cli: {0}" -f
            ((Get-Command kimi -ErrorAction SilentlyContinue) ? (Get-Command kimi).Source : 'NOT FOUND'))
        Write-Host ("[kimi] registration: {0} ({1})" -f
            ($registered ? 'REGISTERED' : 'not registered'), $mcpPath)
        $hookState = if (-not $hookInstalled) { 'not installed' }
        elseif ($hookStale) { 'INSTALLED but STALE - registered file missing: {0} (re-run this script to heal)' -f $hookTarget }
        else { 'INSTALLED' }
        Write-Host "[kimi] ptk hook: $hookState ($configPath)"
        Write-Host ("[kimi] nudge block: {0} in {1}" -f
            ($nudgePresent ? 'INSTALLED' : 'not installed'), $nudgeTarget)
        Write-Host ("[kimi] installed payload: {0} ({1})" -f
            ($payloadPresent ? 'present' : 'MISSING - run scripts/install.ps1'), $PtkHome)
        return $true
    }

    if ($Uninstall) {
        # Registration: remove only the entry this leg manages - one whose
        # command is the installed payload binary. A custom entry (install
        # left it as-is, mhi-8) is not ptk-owned state; removing it on
        # uninstall would destroy a registration ptk never created (hcc-1).
        # A file left holding nothing is removed rather than left as an
        # empty shell.
        $entryIsOurs = $registered -and
            ([string]$mcp['mcpServers']['ptk']['command'] -eq [string]$binary)
        if ($registered -and -not $entryIsOurs) {
            Write-Warning (('[kimi] mcpServers.ptk points at a custom command ({0}) - left as-is; ' +
                'remove it from {1} manually if unwanted.') -f
                $mcp['mcpServers']['ptk']['command'], $mcpPath)
        }
        elseif ($registered) {
            if ($DryRun) {
                Write-Host "[kimi] DRY RUN - would remove the mcpServers.ptk entry from $mcpPath"
            }
            else {
                $servers = $mcp['mcpServers']
                $servers.Remove('ptk')
                if ($servers.Count -eq 0) { $mcp.Remove('mcpServers') }
                if ($mcp.Count -eq 0) {
                    Remove-Item -LiteralPath $mcpPath -Force
                }
                else {
                    Set-Content -LiteralPath $mcpPath -Value ($mcp | ConvertTo-Json -Depth 32) -NoNewline
                }
                Write-Host '[kimi] registration removed.'
            }
        }
        else {
            Write-Host '[kimi] no registration to remove.'
        }
        # Hook: strip exactly the marked block, leaving any user [[hooks]]
        # entries and other config untouched.
        if ($hookInstalled) {
            if ($DryRun) {
                Write-Host "[kimi] DRY RUN - would remove the ptk hook block from $configPath"
            }
            else {
                $kept = Get-PtkKimiHookStripped $configText
                if ([string]::IsNullOrWhiteSpace($kept)) {
                    Remove-Item -LiteralPath $configPath -Force
                }
                else {
                    Set-Content -LiteralPath $configPath -Value $kept -NoNewline
                }
                Write-Host '[kimi] ptk hook removed.'
            }
        }
        else {
            Write-Host '[kimi] no ptk hook entry - nothing to remove.'
        }
        Uninstall-PtkNudgeBlock -Path $nudgeTarget
        return $true
    }

    $ok = $true

    # Registration. Probe FIRST (the mhi-8 lesson): an existing entry -
    # including a custom one pointing somewhere other than ~/.ptk - is left
    # as-is regardless of payload.
    if ($registered) {
        Write-Host '[kimi] already registered - left as is (mcp.json has mcpServers.ptk).'
    }
    elseif ($DryRun) {
        Write-Host ("DRY RUN - would add mcpServers.ptk -> `"{0}`" to {1} (other servers preserved)" -f
            $binary, $mcpPath)
    }
    elseif (-not $payloadPresent) {
        # Registering a missing binary points every session at a tool that
        # cannot answer.
        Write-Warning (('[kimi] no installed ptk server at {0}. Run scripts/install.ps1 ' +
            'first, then re-run this script.') -f $binary)
        $ok = $false
    }
    else {
        if (-not $mcp.ContainsKey('mcpServers') -or $null -eq $mcp['mcpServers']) {
            $mcp['mcpServers'] = @{}
        }
        $mcp['mcpServers']['ptk'] = @{ command = $binary; args = @() }
        $dir = Split-Path -Parent $mcpPath
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        Set-Content -LiteralPath $mcpPath -Value ($mcp | ConvertTo-Json -Depth 32) -NoNewline
        Write-Host "[kimi] registered (user-level $mcpPath)."
    }

    # Hook (the mhi-6/mhi-9 gate): it steers shell calls at the ptk tools, so
    # it ships only where a registration can answer - an existing entry
    # (custom or ours) or the payload this run just registered. Skipping the
    # hook never blocks the nudge.
    $hookAllowed = $registered -or $payloadPresent
    if (-not $hookAllowed) {
        Write-Warning ('[kimi] no registration can answer (no existing entry, no installed ' +
            'payload) - not installing the blocking hook. Run scripts/install.ps1 first, ' +
            'then re-run this script.')
    }
    elseif ($DryRun) {
        Write-Host ("DRY RUN - would {0} the ptk PreToolUse hook block in {1} (matcher `"Bash`")" -f
            ($hookInstalled ? 'replace' : 'add'), $configPath)
    }
    else {
        $kept = Get-PtkKimiHookStripped $configText
        $nl = [Environment]::NewLine
        $content = ($kept ? ($kept + $nl + $nl) : '') + $kimiHookBlock.Trim() + $nl
        $dir = Split-Path -Parent $configPath
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        Set-Content -LiteralPath $configPath -Value $content -NoNewline
        if ($hookStale) {
            Write-Host ("[kimi] replaced STALE hook registration (pointed at missing {0})" -f $hookTarget)
        }
        Write-Host "[kimi] ptk hook installed in $configPath (takes effect next session)."
    }

    # Standard layer, written even when registration failed (conditional
    # wording, safe on machines where ptk never arrives).
    Install-PtkNudgeBlock -Path $nudgeTarget
    $ok
}

# --- resolve which legs run ---------------------------------------------

$explicitTargets = [bool]$SettingsPath -or [bool]$NudgePath
if ($SettingsPath -and -not $NudgePath) {
    # A seam run must never write outside its sandbox: with the nudge a
    # standard layer, defaulting it to the real user guidance file while
    # the settings target is redirected would leak writes onto the machine.
    throw ('-SettingsPath without -NudgePath would leave the nudge targeting the real user ' +
        'guidance file; explicit-target runs must pass both.')
}
$resolvedAgents =
    if ($explicitTargets -or $Local) {
        if (-not $Agent) { $Agent = @('claude') }
        if (@($Agent).Count -ne 1 -or $Agent[0] -eq 'all') {
            throw '-Local/-SettingsPath/-NudgePath target a single leg; pass exactly one -Agent.'
        }
        if (($Local -or $SettingsPath) -and $Agent[0] -ne 'claude') {
            throw (('-Local/-SettingsPath are claude-leg targets (got -Agent {0}).') -f $Agent[0])
        }
        @($Agent)
    }
    elseif (-not $Agent) {
        if ($Uninstall) {
            # Uninstall reverses EVERY leg, not just currently-detected ones
            # (mhi-10): a harness CLI that left PATH after install would
            # otherwise strand its ptk state forever (nudge blocks, config
            # entries pointing at a deleted binary). Every leg's uninstall
            # no-ops safely without its CLI and warns about the one thing it
            # cannot remove itself (a grok registration).
            $supportedAgents
        }
        else {
            $detected = @($supportedAgents | Where-Object { Test-PtkAgentPresent -Name $_ })
            if ($detected.Count -eq 0) {
                Write-Host ('No supported agent harness detected (claude/codex/grok/agy/kimi); defaulting ' +
                    'to the claude leg. Pass -Agent to choose explicitly.')
                $detected = @('claude')
            }
            $detected
        }
    }
    elseif ($Agent -contains 'all') { $supportedAgents }
    else { @($Agent | Select-Object -Unique) }

# -SkipAgent excludes legs in every mode; each exclusion gets the same
# manual-setup blurb a prompt decline prints.
if ($SkipAgent) {
    $resolvedAgents = @($resolvedAgents | Where-Object { $_ -notin $SkipAgent })
    foreach ($name in $SkipAgent) { Write-PtkSkipNotice -Name $name }
}

# Per-harness consent (pacman-style, one prompt): detection-mode installs
# only - an explicit -Agent is already consent, and -Uninstall/-Show never
# ask. PTK_INIT_INTERACTIVE=1 forces the prompt path (test seam).
$prompted = -not $Agent -and -not $Uninstall -and -not $Show -and -not $AllAgents -and
    $resolvedAgents.Count -gt 0
$interactive = (-not [Console]::IsInputRedirected) -or ($env:PTK_INIT_INTERACTIVE -eq '1')
if ($prompted -and $interactive) {
    $skipped = @(Read-PtkConsentSkips -Detected $resolvedAgents)
    foreach ($name in $skipped) { Write-PtkSkipNotice -Name $name }
    $resolvedAgents = @($resolvedAgents | Where-Object { $_ -notin $skipped })
}
elseif ($prompted) {
    Write-Host (('Non-interactive session: wiring up every detected harness ({0}). ' +
        'Scope with -Agent/-SkipAgent; -AllAgents silences this notice.') -f ($resolvedAgents -join ', '))
}

$failedLegs = @()
foreach ($name in $resolvedAgents) {
    $ok = switch ($name) {
        'claude' { Invoke-PtkClaudeLeg }
        'codex' { Invoke-PtkCodexLeg }
        'grok' { Invoke-PtkGrokLeg }
        'agy' { Invoke-PtkAgyLeg }
        'kimi' { Invoke-PtkKimiLeg }
    }
    if (-not $ok) { $failedLegs += $name }
}
if ($failedLegs.Count -gt 0) {
    Write-Warning ('ptk_init: leg(s) failed: {0}' -f ($failedLegs -join ', '))
    exit 1
}
