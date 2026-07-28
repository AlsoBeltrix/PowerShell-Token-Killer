#Requires -Version 7
<#
.SYNOPSIS
Dev-only installer (release-distribution plan, tier 1): publishes the current
checkout self-contained and installs it into the one ptk home (~/.ptk), or
produces the canonical release layout for CI. NOT the public install story —
end users get install.ps1/install.sh against GitHub Releases.

.DESCRIPTION
Default (install): publish for this machine's RID -> replace the
installer-owned payload in ~/.ptk (bin/, src/, scripts/, VERSION) wholesale,
leaving every other file (user config) untouched -> register the server with
Claude Code at user scope (remove-then-add) -> write the Add/Remove Programs
entry on Windows -> run the full per-agent init (ptk_init.ps1: hooks,
registrations, guidance for every detected harness). One command per
machine. -Uninstall reverses all of it and keeps user files. -LayoutOnly -OutputDir <dir> only
builds the layout (release CI reuses this so dev and release artifacts are
the same layout by construction); -Rid and -Version parameterize it.

Install logic lives in small functions so a future `PtkMcpServer install`
verb can host it in-process (the binary embeds the PowerShell engine).

.EXAMPLE
pwsh -File scripts/dev-install.ps1                # install current HEAD
pwsh -File scripts/dev-install.ps1 -Hook          # ... and install the hook
pwsh -File scripts/dev-install.ps1 -Uninstall
pwsh -File scripts/dev-install.ps1 -LayoutOnly -Validate -OutputDir out/ptk-layout
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    # Deprecated, accepted for compatibility: the full per-agent init
    # (hooks, registrations, guidance - every detected harness) runs by
    # DEFAULT after a successful registration; -Hook adds nothing.
    [Parameter(ParameterSetName = 'Install')]
    [switch]$Hook,

    [Parameter(ParameterSetName = 'Uninstall', Mandatory)]
    [switch]$Uninstall,

    # Build the canonical layout into -OutputDir and stop: no home install,
    # no registration. This is the mode release CI drives per RID.
    [Parameter(ParameterSetName = 'LayoutOnly', Mandatory)]
    [switch]$LayoutOnly,
    [Parameter(ParameterSetName = 'LayoutOnly', Mandatory)]
    [string]$OutputDir,
    # Target RID for -LayoutOnly (defaults to this machine's). Cross-RID
    # publish needs no target runtime installed.
    [Parameter(ParameterSetName = 'LayoutOnly')]
    [string]$Rid,

    # Version stamped into the publish (-p:Version) and the VERSION file.
    # Release CI passes the tag version; dev installs default to
    # 0.2.0-dev.g<shortsha>.
    [Parameter(ParameterSetName = 'Install')]
    [Parameter(ParameterSetName = 'LayoutOnly')]
    [string]$Version,

    # Run the full public handshake against a local-RID layout without
    # activating it. Cross-RID layouts cannot execute on this host.
    [Parameter(ParameterSetName = 'LayoutOnly')]
    [switch]$Validate
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$ptkHome = Join-Path $HOME '.ptk'
# Everything the installer owns and replaces wholesale on upgrade; anything
# else under ~/.ptk is user-owned and never touched here.
$payloadEntries = @('bin', 'src', 'scripts', 'VERSION')
$arpKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ptk'
$installTransactionModule = Join-Path $PSScriptRoot 'ptk_install_transaction.psm1'
Import-Module $installTransactionModule -Force

function Get-PtkRid {
    $arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported architecture: $_" }
    }
    $os = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' }
    else { throw 'Unsupported OS.' }
    "$os-$arch"
}

function Assert-NotElevated {
    # ptk is a per-user tool and the warm runspace inherits the harness's
    # privileges; an elevated install invites root-owned files and an
    # elevated-execution footgun (plan: Design commitments).
    $elevated = if ($IsWindows) {
        [Security.Principal.WindowsPrincipal]::new(
            [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    else {
        (id -u) -eq '0'
    }
    if ($elevated) {
        throw 'Refusing to run elevated (root/Administrator): ptk installs per-user. Re-run from a normal shell.'
    }
}

function Get-PtkVersion {
    if ($Version) {
        # Accept tag-shaped values (v0.2.0-rc.1): release CI passes the git
        # tag verbatim and MSBuild rejects a leading v as an invalid version.
        return $Version -replace '^[vV]', ''
    }
    # Get-Command first: a missing native command is a terminating
    # CommandNotFoundException that 2>$null does not suppress.
    $git = Get-Command git -ErrorAction SilentlyContinue
    $sha = if ($git) { & $git -C $repoRoot rev-parse --short HEAD 2>$null } else { $null }
    if (-not $sha -or $LASTEXITCODE -ne 0) { $sha = 'unknown' }
    "0.2.0-dev.g$sha"
}

function Assert-PtkServerNotRunning {
    # Replacing or removing bin/ under a live server half-fails on Windows
    # file locks and leaves a stale server running old code elsewhere;
    # recorded precedent: every rebuild needed Stop-Process first
    # (.agents/state.md).
    $running = @(Get-Process -Name PtkMcpServer -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($ptkHome, [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -gt 0) {
        throw ("A ptk server from {0} is running (PID {1}). Stop it first " +
            "(Stop-Process -Name PtkMcpServer) or restart the harness session, then re-run.") -f
            $ptkHome, ($running.Id -join ', ')
    }
}

function Get-PtkServerBinaryName {
    param([string]$TargetRid)
    if ($TargetRid -like 'win-*') { 'PtkMcpServer.exe' } else { 'PtkMcpServer' }
}

# Interim mitigation for GitHub issue #7: Microsoft Defender falsely detected
# PtkMcpServer.dll (reported as Trojan:MSIL/AsyncRAT.AB!MTB) and quarantined it
# out of the build output and the installed payload. When that happens the
# publish/copy steps succeed but the file is silently missing afterwards, so
# verify the payload landed intact and fail with actionable guidance if not.
function Assert-PtkPayloadIntact {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$TargetRid
    )
    $required = @(
        (Join-Path $Root 'bin' (Get-PtkServerBinaryName -TargetRid $TargetRid))
        (Join-Path $Root 'bin' 'PtkMcpServer.dll')
    )
    if ($TargetRid -notlike 'win-*') {
        $required += Join-Path $Root 'bin' 'PtkWorkerBroker'
    }
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($missing.Count -eq 0) { return }
    Write-Warning ((@(
        'These files are missing from the freshly written payload:'
        ($missing | ForEach-Object { "  $_" })
        'An antivirus quarantine is the most likely cause: Microsoft Defender has'
        'falsely detected PtkMcpServer.dll (Trojan:MSIL/AsyncRAT.AB!MTB) and removed'
        'it immediately after install. See the false-positive tracking issue'
        'https://github.com/AlsoBeltrix/PowerShell-Token-Killer/issues/7 and the'
        'runbook .agents/plans/defender-fp-submission.md. Check the Defender'
        'protection history before restoring anything, and do not add broad'
        'exclusions.'
    ) | ForEach-Object { $_ }) -join [Environment]::NewLine)
    throw 'Install incomplete: payload files missing (possible antivirus quarantine).'
}

# Publishes the runtime server and assembles the canonical layout (bin/, src/,
# scripts/, VERSION) in $Destination. Legacy audit administration remains a
# separate source project and is not part of the installed runtime payload.
# The one layout generator dev installs and release CI share.
function New-PtkLayout {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$TargetRid,
        [Parameter(Mandatory)][string]$PayloadVersion
    )
    Write-Host "Publishing PtkMcpServer ($TargetRid, $PayloadVersion)..."
    dotnet publish (Join-Path $repoRoot 'server' 'PtkMcpServer') `
        -c Release -r $TargetRid --self-contained `
        -p:Version=$PayloadVersion `
        -o (Join-Path $Destination 'bin') -v q --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    $src = New-Item -ItemType Directory -Path (Join-Path $Destination 'src') -Force
    foreach ($f in 'PwshTokenCompressor.psd1', 'PwshTokenCompressor.psm1') {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'src' $f) -Destination $src.FullName
    }
    $scripts = New-Item -ItemType Directory -Path (Join-Path $Destination 'scripts') -Force
    foreach ($f in 'ptk-hook.ps1', 'ptk_init.ps1', 'dev-install.ps1',
        'ptk_install_transaction.psm1') {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts' $f) -Destination $scripts.FullName
    }
    Set-Content -LiteralPath (Join-Path $Destination 'VERSION') -Value $PayloadVersion -NoNewline
}

function Remove-PtkPayload {
    foreach ($entry in $payloadEntries) {
        $target = Join-Path $ptkHome $entry
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
            Write-Host "Removed $target"
        }
    }
    # Drop the home itself only when nothing user-owned remains. -Force:
    # dot-named directories carry the Hidden attribute on Unix, which
    # Remove-Item refuses without it.
    if ((Test-Path -LiteralPath $ptkHome) -and
        -not (Get-ChildItem -LiteralPath $ptkHome -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $ptkHome -Force
        Write-Host "Removed empty $ptkHome"
    }
}

# Returns $true when the server actually got registered with Claude Code;
# $false when registration was left to the user (the install arm then warns,
# and the claude leg of ptk_init skips its blocking hook - mhi-6/mhi-9).
function Register-PtkServer {
    param([Parameter(Mandatory)][string]$BinaryPath)
    $claude = Get-Command claude -ErrorAction SilentlyContinue
    if (-not $claude) {
        Write-Host 'claude CLI not found - register manually:'
        Write-Host "  claude mcp add --scope user ptk `"$BinaryPath`""
        return $false
    }
    # Remove-then-add so re-installs and dev<->release switches never collide.
    claude mcp remove --scope user ptk *> $null
    claude mcp add --scope user ptk $BinaryPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw ("claude mcp add failed; any previous ptk user-scope registration was " +
            "already removed. Register manually: claude mcp add --scope user ptk `"{0}`"" -f $BinaryPath)
    }
    Write-Host 'Registered with Claude Code (user scope).'
    $true
}

function Unregister-PtkServer {
    $claude = Get-Command claude -ErrorAction SilentlyContinue
    if (-not $claude) { return }
    claude mcp remove --scope user ptk *> $null
    if ($LASTEXITCODE -eq 0) { Write-Host 'Removed Claude Code registration (user scope).' }
}

function Write-PtkArpEntry {
    param([Parameter(Mandatory)][string]$PayloadVersion)
    if (-not $IsWindows) { return }
    # The per-user Add/Remove Programs entry winget's upgrade/uninstall
    # tracking keys off (plan: winget-ready from v0.2.0).
    New-Item -Path $arpKeyPath -Force | Out-Null
    Set-ItemProperty -Path $arpKeyPath -Name DisplayName -Value 'PowerShell Token Killer (ptk)'
    Set-ItemProperty -Path $arpKeyPath -Name DisplayVersion -Value $PayloadVersion
    Set-ItemProperty -Path $arpKeyPath -Name Publisher -Value 'PowerShell-Token-Killer'
    Set-ItemProperty -Path $arpKeyPath -Name InstallLocation -Value $ptkHome
    Set-ItemProperty -Path $arpKeyPath -Name UninstallString -Value (
        'pwsh -NoProfile -File "{0}" -Uninstall' -f (Join-Path $ptkHome 'scripts' 'dev-install.ps1'))
    Set-ItemProperty -Path $arpKeyPath -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $arpKeyPath -Name NoRepair -Value 1 -Type DWord
    Write-Host 'Wrote Add/Remove Programs entry (HKCU).'
}

function Remove-PtkArpEntry {
    if (-not $IsWindows) { return }
    if (Test-Path -Path $arpKeyPath) {
        Remove-Item -Path $arpKeyPath -Recurse -Force
        Write-Host 'Removed Add/Remove Programs entry.'
    }
}

function Get-PtkArpState {
    if (-not $IsWindows) {
        return [pscustomobject]@{ Exists = $false; Values = @() }
    }
    if (-not (Test-Path -Path $arpKeyPath)) {
        return [pscustomobject]@{ Exists = $false; Values = @() }
    }

    $key = Get-Item -Path $arpKeyPath
    $values = @($key.GetValueNames() | Sort-Object | ForEach-Object {
            [pscustomobject]@{
                Name = $_
                Kind = $key.GetValueKind($_).ToString()
                Value = $key.GetValue(
                    $_,
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            }
        })
    [pscustomobject]@{ Exists = $true; Values = $values }
}

function Restore-PtkArpState {
    param([Parameter(Mandatory)]$State)
    if (-not $IsWindows) { return }

    Remove-PtkArpEntry
    if (-not $State.Exists) { return }

    New-Item -Path $arpKeyPath -Force | Out-Null
    foreach ($value in @($State.Values)) {
        $propertyType = switch ($value.Kind) {
            'String' { 'String' }
            'ExpandString' { 'ExpandString' }
            'Binary' { 'Binary' }
            'DWord' { 'DWord' }
            'MultiString' { 'MultiString' }
            'QWord' { 'QWord' }
            default { throw "Unsupported prior ARP registry value kind: $($value.Kind)" }
        }
        New-ItemProperty `
            -Path $arpKeyPath `
            -Name $value.Name `
            -Value $value.Value `
            -PropertyType $propertyType `
            -Force |
            Out-Null
    }
}

function Assert-PtkArpStateRestored {
    param([Parameter(Mandatory)]$Expected)
    if (-not $IsWindows) { return }

    $actualJson = Get-PtkArpState | ConvertTo-Json -Depth 6 -Compress
    $expectedJson = $Expected | ConvertTo-Json -Depth 6 -Compress
    if ($actualJson -cne $expectedJson) {
        throw 'The Add/Remove Programs entry was not restored exactly.'
    }
}

function Get-PtkRegistrationPaths {
    @(
        (Join-Path $HOME '.claude.json')
        (Join-Path $HOME '.claude' 'settings.json')
        (Join-Path $HOME '.claude' 'CLAUDE.md')
        (Join-Path $HOME '.codex' 'config.toml')
        (Join-Path $HOME '.codex' 'AGENTS.md')
        (Join-Path $HOME '.grok' 'config.toml')
        (Join-Path $HOME '.gemini' 'config' 'mcp_config.json')
        (Join-Path $HOME '.gemini' 'config' 'plugins' 'ptk')
    )
}

function Invoke-PtkPackageSmoke {
    param([Parameter(Mandatory)][string]$BinaryPath)

    $handshake = Join-Path $repoRoot 'server' 'test-handshake.ps1'
    if (-not (Test-Path -LiteralPath $handshake -PathType Leaf)) {
        throw "Package smoke script is unavailable: $handshake"
    }
    Write-Host "Validating staged PTK package: $BinaryPath"
    & ([Environment]::ProcessPath) -NoProfile -File $handshake `
        -ServerCommand $BinaryPath `
        -TimeoutSec 90 |
        Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Package handshake failed for $BinaryPath"
    }
}

function Invoke-PtkHarnessInitialization {
    param(
        [Parameter(Mandatory)][string]$InitScript,
        [string[]]$Arguments = @()
    )

    # ptk_init.ps1 deliberately exits nonzero when any harness leg fails. Run
    # it as a child so that exit cannot terminate this installer before the
    # transaction restores the previous payload and registrations.
    & ([Environment]::ProcessPath) -NoProfile -File $InitScript @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Per-harness initialization failed with exit code $LASTEXITCODE."
    }
}

# True only when a REAL ptk hook entry exists: a marker-matched command
# inside hooks.PreToolUse. A raw text match on the whole settings file would
# treat 'ptk-hook.ps1' anywhere (permissions lists, other hook events) as
# hook consent (i2-1).
function Test-PtkHookEntryPresent {
    param([Parameter(Mandatory)][string]$SettingsPath)
    if (-not (Test-Path -LiteralPath $SettingsPath)) { return $false }
    $raw = Get-Content -LiteralPath $SettingsPath -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) { return $false }
    try { $config = $raw | ConvertFrom-Json -AsHashtable } catch { return $false }
    if ($null -eq $config -or -not $config.ContainsKey('hooks') -or
        -not $config['hooks'].ContainsKey('PreToolUse')) { return $false }
    foreach ($entry in @($config['hooks']['PreToolUse'])) {
        if ($null -eq $entry) { continue }
        foreach ($hook in @($entry['hooks'])) {
            if ($null -ne $hook -and [string]$hook['command'] -like '*ptk-hook.ps1*') { return $true }
        }
    }
    $false
}

function Show-PtkCodexSnippet {
    param([Parameter(Mandatory)][string]$BinaryPath)
    Write-Host ''
    Write-Host 'Codex (~/.codex/config.toml):'
    Write-Host '  [mcp_servers.ptk]'
    # TOML basic string with explicit escaping: literal (single-quoted)
    # strings cannot hold an apostrophe at all, and unescaped Windows
    # backslashes are illegal escape sequences in a basic string.
    $escaped = $BinaryPath.Replace('\', '\\').Replace('"', '\"')
    Write-Host ('  command = "{0}"' -f $escaped)
}

$mode = $PSCmdlet.ParameterSetName
# Parameter-set membership alone would let an explicit -Uninstall:$false run
# a full (destructive) uninstall; honor the switch's VALUE. -LayoutOnly:$false
# with -OutputDir has no coherent meaning - refuse rather than guess.
if ($mode -eq 'Uninstall' -and -not $Uninstall) { $mode = 'Install' }
if ($mode -eq 'LayoutOnly' -and -not $LayoutOnly) {
    throw '-LayoutOnly:$false with -OutputDir is ambiguous; pass -LayoutOnly or drop -OutputDir.'
}

switch ($mode) {
    'LayoutOnly' {
        $targetRid = if ($Rid) { $Rid } else { Get-PtkRid }
        $payloadVersion = Get-PtkVersion
        if ((Test-Path -LiteralPath $OutputDir) -and
            (Get-ChildItem -LiteralPath $OutputDir -Force | Select-Object -First 1)) {
            throw "OutputDir '$OutputDir' is not empty - refusing to clobber."
        }
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
        New-PtkLayout -Destination $OutputDir -TargetRid $targetRid -PayloadVersion $payloadVersion
        Assert-PtkPayloadIntact -Root $OutputDir -TargetRid $targetRid
        if ($Validate) {
            $localRid = Get-PtkRid
            if ($targetRid -cne $localRid) {
                throw "Cannot execute $targetRid layout validation on $localRid."
            }
            Invoke-PtkPackageSmoke -BinaryPath (
                Join-Path $OutputDir 'bin' (
                    Get-PtkServerBinaryName -TargetRid $targetRid))
        }
        Write-Host "Layout ready: $OutputDir ($targetRid, $payloadVersion)"
    }
    'Uninstall' {
        Assert-NotElevated
        Assert-PtkServerNotRunning
        # Per-agent init reversal first (needs a ptk_init.ps1), then Claude
        # registration, ARP, payload. ptk_init -Uninstall reverses every
        # SUPPORTED leg - not just detected ones (mhi-10) - (hook + guidance
        # blocks, codex/grok registrations, agy plugin) and no-ops safely
        # where nothing is installed.
        $init = Join-Path $ptkHome 'scripts' 'ptk_init.ps1'
        if (-not (Test-Path -LiteralPath $init)) { $init = Join-Path $PSScriptRoot 'ptk_init.ps1' }
        if (Test-Path -LiteralPath $init) {
            try {
                Invoke-PtkHarnessInitialization `
                    -InitScript $init `
                    -Arguments '-Uninstall'
            }
            catch { Write-Warning "Per-agent uninstall failed (continuing): $_" }
        }
        elseif (Test-PtkHookEntryPresent -SettingsPath (Join-Path $HOME '.claude' 'settings.json')) {
            Write-Warning ('A ptk hook entry exists in the user settings but no ptk_init.ps1 was ' +
                'found; run ptk_init.ps1 -Uninstall from a checkout to remove it.')
        }
        Unregister-PtkServer
        Remove-PtkArpEntry
        Remove-PtkPayload
        Write-Host 'ptk uninstalled. User-owned files under ~/.ptk (if any) were kept.'
    }
    default {
        Assert-NotElevated
        Assert-PtkServerNotRunning
        $targetRid = Get-PtkRid
        $payloadVersion = Get-PtkVersion
        $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-stage-{0}" -f ([guid]::NewGuid()))
        $snapshot = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-rollback-{0}" -f ([guid]::NewGuid()))
        New-Item -ItemType Directory -Path $staging | Out-Null
        try {
            New-PtkLayout -Destination $staging -TargetRid $targetRid -PayloadVersion $payloadVersion
            Assert-PtkPayloadIntact -Root $staging -TargetRid $targetRid
            Invoke-PtkInstallTransaction `
                -StagingRoot $staging `
                -PayloadRoot $ptkHome `
                -PayloadEntries $payloadEntries `
                -RegistrationPaths (Get-PtkRegistrationPaths) `
                -SnapshotRoot $snapshot `
                -CaptureExternalState { Get-PtkArpState } `
                -RestoreExternalState {
                    param($state)
                    Restore-PtkArpState -State $state
                } `
                -AssertExternalStateRestored {
                    param($state)
                    Assert-PtkArpStateRestored -Expected $state
                } `
                -StagedValidation {
                    param($stagedRoot)
                    Invoke-PtkPackageSmoke -BinaryPath (
                        Join-Path $stagedRoot 'bin' (
                            Get-PtkServerBinaryName -TargetRid $targetRid))
                } `
                -InstalledValidation {
                    param($installedRoot)
                    $installedBinary = Join-Path $installedRoot 'bin' (
                        Get-PtkServerBinaryName -TargetRid $targetRid)
                    Assert-PtkPayloadIntact -Root $installedRoot -TargetRid $targetRid
                    Invoke-PtkPackageSmoke -BinaryPath $installedBinary
                } `
                -RegistrationCutover {
                    $installedBinary = Join-Path $ptkHome 'bin' (
                        Get-PtkServerBinaryName -TargetRid $targetRid)
                    $registeredNow = Register-PtkServer -BinaryPath $installedBinary
                    if ($Hook) {
                        Write-Host 'NOTE: -Hook is deprecated - the full per-agent init runs by default.'
                    }
                    if (-not $registeredNow) {
                        Write-Warning (('ptk is not registered with Claude Code (claude CLI not found); ' +
                            'the claude leg installs guidance only, no blocking hook. Register manually, ' +
                            'then re-run: pwsh -File "{0}"') -f (
                            Join-Path $ptkHome 'scripts' 'ptk_init.ps1'))
                    }
                    Invoke-PtkHarnessInitialization -InitScript (
                        Join-Path $ptkHome 'scripts' 'ptk_init.ps1')
                    Write-PtkArpEntry -PayloadVersion $payloadVersion
                }
        }
        finally {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        }
        $binaryPath = Join-Path $ptkHome 'bin' (Get-PtkServerBinaryName -TargetRid $targetRid)
        Show-PtkCodexSnippet -BinaryPath $binaryPath
        Write-Host ''
        Write-Host "Installed ptk $payloadVersion to $ptkHome ($targetRid)."
    }
}
