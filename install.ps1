#!/usr/bin/env pwsh
#Requires -Version 7
<#
.SYNOPSIS
Installs PowerShell Token Killer (ptk) from GitHub Releases.

.DESCRIPTION
Downloads the release asset for this machine's platform, verifies it against
the release's SHA256SUMS, lays it out under ~/.ptk, ensures RTK is available,
and registers the MCP server. RTK is a required dependency: this installer
never completes onto a machine where ptk would refuse to start.

.EXAMPLE
irm https://raw.githubusercontent.com/AlsoBeltrix/PowerShell-Token-Killer/master/install.ps1 | iex

.EXAMPLE
pwsh -File install.ps1 -Version 0.2.0
pwsh -File install.ps1 -Uninstall
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    # Release to install; defaults to the latest published release.
    [Parameter(ParameterSetName = 'Install')]
    [string]$Version,

    [Parameter(ParameterSetName = 'Uninstall', Mandatory)]
    [switch]$Uninstall,

    # Also remove user-owned configuration under ~/.ptk.
    [Parameter(ParameterSetName = 'Uninstall')]
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'

$PtkRepository = 'AlsoBeltrix/PowerShell-Token-Killer'
$RtkRepository = 'rtk-ai/rtk'
$PtkHome = Join-Path $HOME '.ptk'
# Everything the installer owns and replaces wholesale; anything else under
# ~/.ptk is user-owned and survives install, upgrade, and uninstall.
$PayloadEntries = @('bin', 'src', 'scripts', 'VERSION', 'LICENSE', 'README.md')
# Records the rtk this installer placed, so uninstall removes only our copy
# and never a user's own rtk.
$RtkMarkerName = '.ptk-installed-rtk'
$ArpKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ptk'

function Assert-NotElevated {
    # ptk is a per-user tool and its warm runspace inherits the harness's
    # privileges; an elevated install invites root-owned files and an
    # elevated-execution footgun.
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

function Get-PtkRid {
    $arch = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported architecture: $_" }
    }
    $os = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' }
    else { throw 'Unsupported operating system.' }
    $rid = "$os-$arch"
    if ($rid -eq 'osx-x64') {
        throw 'Intel macOS (osx-x64) is not a supported ptk platform. Supported: win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64.'
    }
    $rid
}

function Assert-PtkRuntimeNotRunning {
    # Replacing bin/ under a live server half-fails on Windows file locks and
    # leaves a stale server running old code.
    $running = @(Get-Process -Name 'PtkMcpServer', 'PtkWorkerBroker' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($PtkHome, [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -gt 0) {
        throw ("ptk is running from {0} (PID {1}). Close the harness session or stop those processes, then re-run." -f
            $PtkHome, ($running.Id -join ', '))
    }
}

function Invoke-PtkDownload {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$OutFile
    )
    Write-Host "  downloading $(Split-Path $Uri -Leaf)"
    Invoke-WebRequest -Uri $Uri -OutFile $OutFile -UseBasicParsing
}

function Assert-PtkChecksum {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Expected
    )
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.ToLowerInvariant()) {
        throw ("Checksum mismatch for {0}.`n  expected {1}`n  actual   {2}`nRefusing to install an unverified download." -f
            (Split-Path $Path -Leaf), $Expected, $actual)
    }
}

function Expand-PtkArchive {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Destination
    )
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    if ($Path.EndsWith('.zip')) {
        Expand-Archive -LiteralPath $Path -DestinationPath $Destination -Force
    }
    else {
        tar -xzf $Path -C $Destination
        if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $Path" }
    }
}

# --- RTK -------------------------------------------------------------------
# RTK is required, not recommended: ptk is a compression router and refuses to
# start without it (exit 78). The installer must therefore leave the machine
# with a working rtk or fail.

function Get-RtkAssetName {
    param([Parameter(Mandatory)][string]$Rid)
    switch ($Rid) {
        'win-x64' { 'rtk-x86_64-pc-windows-msvc.zip' }
        # No upstream aarch64 Windows build; the x64 binary runs under
        # Windows ARM64 emulation and is probed below.
        'win-arm64' { 'rtk-x86_64-pc-windows-msvc.zip' }
        'linux-x64' { 'rtk-x86_64-unknown-linux-musl.tar.gz' }
        'linux-arm64' { 'rtk-aarch64-unknown-linux-gnu.tar.gz' }
        'osx-arm64' { 'rtk-aarch64-apple-darwin.tar.gz' }
        default { throw "No rtk asset mapping for RID '$Rid'." }
    }
}

function Test-RtkAnswers {
    param([Parameter(Mandatory)][string]$Path)
    # A version banner only proves the image loaded. ptk depends on the
    # rewriter answering, which is what must work under emulation.
    try {
        $rewritten = & $Path hook check --agent ptk 'git status --short' 2>$null
        return $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($rewritten)
    }
    catch { return $false }
}

function Resolve-PtkRtk {
    param([Parameter(Mandatory)][string]$Rid)

    $existing = Get-Command rtk -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($existing -and (Test-RtkAnswers -Path $existing.Source)) {
        Write-Host "rtk found on PATH: $($existing.Source)"
        return [pscustomobject]@{ Path = $existing.Source; Installed = $false }
    }

    Write-Host 'rtk not found; installing it alongside ptk (required dependency).'
    $asset = Get-RtkAssetName -Rid $Rid
    $staging = Join-Path ([IO.Path]::GetTempPath()) "ptk-rtk-$(New-Guid)"
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        $archive = Join-Path $staging $asset
        Invoke-PtkDownload -Uri "https://github.com/$RtkRepository/releases/latest/download/$asset" -OutFile $archive

        # rtk publishes checksums.txt alongside its assets; an unverified
        # binary that ptk then pins and hashes is exactly the substitution
        # the pinning exists to prevent.
        $sums = Join-Path $staging 'checksums.txt'
        try {
            Invoke-PtkDownload -Uri "https://github.com/$RtkRepository/releases/latest/download/checksums.txt" -OutFile $sums
        }
        catch {
            throw "Could not download rtk's checksums.txt to verify the download: $($_.Exception.Message)"
        }
        $line = Get-Content $sums | Where-Object { $_ -match [regex]::Escape($asset) } | Select-Object -First 1
        if (-not $line) { throw "checksums.txt has no entry for $asset; refusing to install an unverified rtk." }
        Assert-PtkChecksum -Path $archive -Expected ($line -split '\s+')[0]

        $extracted = Join-Path $staging 'x'
        Expand-PtkArchive -Path $archive -Destination $extracted
        $binaryName = if ($IsWindows) { 'rtk.exe' } else { 'rtk' }
        $found = Get-ChildItem -Path $extracted -Filter $binaryName -Recurse -File |
            Select-Object -First 1
        if (-not $found) { throw "rtk archive did not contain $binaryName." }

        $target = Join-Path $PtkHome 'bin' $binaryName
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $found.FullName -Destination $target -Force
        if (-not $IsWindows) { chmod +x $target }

        if (-not (Test-RtkAnswers -Path $target)) {
            $hint = if ($Rid -eq 'win-arm64') {
                " On Windows ARM64 the x64 rtk runs under emulation; verify x64 emulation is available."
            }
            else { '' }
            throw "The installed rtk did not answer 'hook check'.$hint ptk would refuse to start, so this install is being aborted."
        }
        Set-Content -LiteralPath (Join-Path $PtkHome $RtkMarkerName) -Value $target -NoNewline
        Write-Host "rtk installed: $target"
        return [pscustomobject]@{ Path = $target; Installed = $true }
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- payload ---------------------------------------------------------------

function Get-PtkReleaseAsset {
    param([Parameter(Mandatory)][string]$Rid)

    $extension = if ($Rid.StartsWith('win-')) { 'zip' } else { 'tar.gz' }
    if ($Version) {
        $tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
        $number = $tag.TrimStart('v')
        $base = "https://github.com/$PtkRepository/releases/download/$tag"
    }
    else {
        $latest = Invoke-RestMethod "https://api.github.com/repos/$PtkRepository/releases/latest"
        $tag = $latest.tag_name
        $number = $tag.TrimStart('v')
        $base = "https://github.com/$PtkRepository/releases/download/$tag"
    }
    [pscustomobject]@{
        Tag       = $tag
        Version   = $number
        AssetName = "ptk-$number-$Rid.$extension"
        AssetUri  = "$base/ptk-$number-$Rid.$extension"
        SumsUri   = "$base/SHA256SUMS"
    }
}

function Write-PtkArpEntry {
    param([Parameter(Mandatory)][string]$PayloadVersion)
    if (-not $IsWindows) { return }
    New-Item -Path $ArpKeyPath -Force | Out-Null
    Set-ItemProperty -Path $ArpKeyPath -Name DisplayName -Value 'PowerShell Token Killer (ptk)'
    Set-ItemProperty -Path $ArpKeyPath -Name DisplayVersion -Value $PayloadVersion
    Set-ItemProperty -Path $ArpKeyPath -Name Publisher -Value 'PowerShell-Token-Killer'
    Set-ItemProperty -Path $ArpKeyPath -Name InstallLocation -Value $PtkHome
    Set-ItemProperty -Path $ArpKeyPath -Name UninstallString -Value (
        'pwsh -NoProfile -File "{0}" -Uninstall' -f (Join-Path $PtkHome 'scripts' 'install.ps1'))
    Set-ItemProperty -Path $ArpKeyPath -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $ArpKeyPath -Name NoRepair -Value 1 -Type DWord
}

function Remove-PtkArpEntry {
    if (-not $IsWindows) { return }
    if (Test-Path -Path $ArpKeyPath) {
        Remove-Item -Path $ArpKeyPath -Recurse -Force
    }
}

function Register-PtkServer {
    param([Parameter(Mandatory)][string]$BinaryPath)
    $claude = Get-Command claude -CommandType Application -ErrorAction SilentlyContinue
    if (-not $claude) {
        Write-Host ''
        Write-Host 'Register ptk with your MCP harness using:'
        Write-Host "  claude mcp add --scope user ptk `"$BinaryPath`""
        return
    }
    # Remove-then-add so re-installs never collide with a stale registration.
    & $claude mcp remove --scope user ptk *> $null
    & $claude mcp add --scope user ptk $BinaryPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw ("claude mcp add failed; register manually: claude mcp add --scope user ptk `"{0}`"" -f $BinaryPath)
    }
    Write-Host 'Registered with Claude Code (user scope).'
}

function Install-Ptk {
    Assert-NotElevated
    $rid = Get-PtkRid
    Assert-PtkRuntimeNotRunning
    Write-Host "Installing ptk for $rid into $PtkHome"

    $release = Get-PtkReleaseAsset -Rid $rid
    Write-Host "Release $($release.Tag)"

    $staging = Join-Path ([IO.Path]::GetTempPath()) "ptk-install-$(New-Guid)"
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    $backup = Join-Path ([IO.Path]::GetTempPath()) "ptk-backup-$(New-Guid)"
    try {
        $archive = Join-Path $staging $release.AssetName
        Invoke-PtkDownload -Uri $release.AssetUri -OutFile $archive

        $sums = Join-Path $staging 'SHA256SUMS'
        Invoke-PtkDownload -Uri $release.SumsUri -OutFile $sums
        $line = Get-Content $sums | Where-Object { $_ -match [regex]::Escape($release.AssetName) } |
            Select-Object -First 1
        if (-not $line) { throw "SHA256SUMS has no entry for $($release.AssetName)." }
        Assert-PtkChecksum -Path $archive -Expected ($line -split '\s+')[0]
        Write-Host '  checksum verified'

        $payload = Join-Path $staging 'payload'
        Expand-PtkArchive -Path $archive -Destination $payload
        $serverName = if ($IsWindows) { 'PtkMcpServer.exe' } else { 'PtkMcpServer' }
        $stagedServer = Join-Path $payload 'bin' $serverName
        if (-not (Test-Path -LiteralPath $stagedServer)) {
            throw "The downloaded payload has no bin/$serverName; refusing to activate an incomplete install."
        }

        # Snapshot the prior payload so a failure part-way through activation
        # restores exactly what was there.
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        foreach ($entry in $PayloadEntries) {
            $existing = Join-Path $PtkHome $entry
            if (Test-Path -LiteralPath $existing) {
                Copy-Item -LiteralPath $existing -Destination $backup -Recurse -Force
            }
        }

        try {
            New-Item -ItemType Directory -Path $PtkHome -Force | Out-Null
            foreach ($entry in $PayloadEntries) {
                $target = Join-Path $PtkHome $entry
                if (Test-Path -LiteralPath $target) {
                    Remove-Item -LiteralPath $target -Recurse -Force
                }
                $source = Join-Path $payload $entry
                if (Test-Path -LiteralPath $source) {
                    Copy-Item -LiteralPath $source -Destination $PtkHome -Recurse -Force
                }
            }
            # This installer becomes the uninstall entry point the ARP key
            # targets, so it must live inside the payload it manages.
            Copy-Item -LiteralPath $PSCommandPath `
                -Destination (Join-Path $PtkHome 'scripts' 'install.ps1') -Force `
                -ErrorAction SilentlyContinue

            $server = Join-Path $PtkHome 'bin' $serverName
            if (-not $IsWindows) { chmod +x $server }

            # RTK last, but before registration: a machine without it gets a
            # server that refuses to start, which is not a successful install.
            $null = Resolve-PtkRtk -Rid $rid

            Write-PtkArpEntry -PayloadVersion $release.Version
            Register-PtkServer -BinaryPath $server
        }
        catch {
            Write-Warning "Install failed; restoring the previous payload. $($_.Exception.Message)"
            foreach ($entry in $PayloadEntries) {
                $target = Join-Path $PtkHome $entry
                if (Test-Path -LiteralPath $target) {
                    Remove-Item -LiteralPath $target -Recurse -Force
                }
                $saved = Join-Path $backup $entry
                if (Test-Path -LiteralPath $saved) {
                    Copy-Item -LiteralPath $saved -Destination $PtkHome -Recurse -Force
                }
            }
            throw
        }

        Write-Host ''
        Write-Host "ptk $($release.Version) installed to $PtkHome"
        Write-Host 'Start a new harness session to pick it up.'
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Uninstall-Ptk {
    Assert-NotElevated
    Assert-PtkRuntimeNotRunning

    $claude = Get-Command claude -CommandType Application -ErrorAction SilentlyContinue
    if ($claude) {
        & $claude mcp remove --scope user ptk *> $null
        Write-Host 'Removed Claude Code registration (user scope).'
    }
    Remove-PtkArpEntry

    # Only ever remove an rtk this installer placed.
    $marker = Join-Path $PtkHome $RtkMarkerName
    if (Test-Path -LiteralPath $marker) {
        $ours = (Get-Content -LiteralPath $marker -Raw).Trim()
        if ($ours -and (Test-Path -LiteralPath $ours)) {
            Remove-Item -LiteralPath $ours -Force
            Write-Host "Removed the rtk this installer placed: $ours"
        }
        Remove-Item -LiteralPath $marker -Force
    }

    foreach ($entry in $PayloadEntries) {
        $target = Join-Path $PtkHome $entry
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }

    if ($Purge -and (Test-Path -LiteralPath $PtkHome)) {
        Remove-Item -LiteralPath $PtkHome -Recurse -Force
        Write-Host "Purged $PtkHome"
    }
    elseif ((Test-Path -LiteralPath $PtkHome) -and
        -not (Get-ChildItem -LiteralPath $PtkHome -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $PtkHome -Force
    }
    Write-Host 'ptk uninstalled.'
}

if ($Uninstall) { Uninstall-Ptk } else { Install-Ptk }
