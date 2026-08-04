#Requires -Version 7
<#
.SYNOPSIS
The ptk installer. Installs a published release, or this checkout, into the
one ptk home (~/.ptk); also produces the canonical layout for release CI.

.DESCRIPTION
Install obtains a payload — a prebuilt release asset with -FromRelease, or a
self-contained publish of this checkout otherwise — and from there the path
is identical: stage it, smoke-test it, snapshot the prior payload, activate
as a unit, ensure rtk is present, register the server, and run the per-agent
init that wires up every detected harness (claude, codex, grok, agy, kimi). Any
failure during activation or registration restores the previous payload
byte-identically.

~/.ptk holds bin/, src/, scripts/, VERSION, LICENSE, README.md — replaced
wholesale on upgrade. Everything else there is user-owned and is never
touched except by -Purge.

rtk is a required dependency: the server exits 78 without one. An rtk already
on PATH is used as-is; otherwise the matching build is fetched from rtk's
releases, checksum-verified, and recorded so uninstall removes only that copy.

-Uninstall reverses all of it and keeps user files; add -Purge to remove them.
-LayoutOnly -OutputDir <dir> builds the layout and stops — release CI drives
that mode per RID, so release artifacts and local installs are the same layout
by construction.

Install logic lives in small functions so a future `PtkMcpServer install`
verb can host it in-process (the binary embeds the PowerShell engine).

.EXAMPLE
pwsh -File scripts/install.ps1 -FromRelease         # latest published release
pwsh -File scripts/install.ps1 -FromRelease -Version 0.2.0
pwsh -File scripts/install.ps1                      # build and install this checkout
pwsh -File scripts/install.ps1 -Uninstall
pwsh -File scripts/install.ps1 -LayoutOnly -Validate -OutputDir out/ptk-layout
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
    # Target RID for -LayoutOnly (defaults to this machine's). PTK has no
    # supported cross-target native package build; Unix layouts include a
    # platform-native worker broker.
    [Parameter(ParameterSetName = 'LayoutOnly')]
    [string]$Rid,

    # Version stamped into the publish (-p:Version) and the VERSION file.
    # Release CI passes the tag version; source installs default to
    # 0.2.0-dev.g<shortsha>. With -FromRelease, selects which release to
    # install instead (default: latest).
    [Parameter(ParameterSetName = 'Install')]
    [Parameter(ParameterSetName = 'LayoutOnly')]
    [string]$Version,

    # Install a prebuilt release asset instead of building this checkout.
    # This is how a user with no .NET SDK installs; everything after the
    # payload is obtained is identical either way.
    [Parameter(ParameterSetName = 'Install')]
    [switch]$FromRelease,

    # Also remove user-owned files under ~/.ptk. Uninstall keeps them by
    # default.
    [Parameter(ParameterSetName = 'Uninstall')]
    [switch]$Purge,

    # Run the full public handshake against the layout without activating it.
    [Parameter(ParameterSetName = 'LayoutOnly')]
    [switch]$Validate
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$ptkHome = Join-Path $HOME '.ptk'
# Everything the installer owns and replaces wholesale on upgrade; anything
# else under ~/.ptk is user-owned and never touched here.
$payloadEntries = @('bin', 'src', 'scripts', 'VERSION', 'LICENSE', 'README.md')
# Records an rtk this installer placed, so uninstall removes only that copy
# and never a user's own.
$rtkMarkerName = '.ptk-installed-rtk'
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

function Assert-PtkNativeBuildRid {
    param([Parameter(Mandatory)][string]$TargetRid)

    $localRid = Get-PtkRid
    if ($TargetRid -cne $localRid) {
        throw (
            "Cross-RID layout publishing is refused: target '$TargetRid' does not " +
            "match build host '$localRid'. PTK packages have no supported " +
            'cross-target native build; run this layout build on a matching host.')
    }
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

function Assert-PtkRuntimeNotRunning {
    # Replacing or removing bin/ under a live server half-fails on Windows
    # file locks and leaves a stale server running old code elsewhere;
    # recorded precedent: every rebuild needed Stop-Process first
    # (.agents/state.md).
    $ptkRuntimeProcessNames = @('PtkMcpServer', 'PtkWorkerBroker')
    $running = @(Get-Process -Name $ptkRuntimeProcessNames -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($ptkHome, [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -gt 0) {
        throw ("PTK process(es) from {0} are running (PID {1}; name(s) {2}). " +
            "Stop all PTK processes or restart the harness session, then re-run.") -f
            $ptkHome,
            ($running.Id -join ', '),
            (($running.ProcessName | Sort-Object -Unique) -join ', ')
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
        (Join-Path $Root 'src' 'PwshTokenCompressor.psd1')
        (Join-Path $Root 'LICENSE')
        (Join-Path $Root 'VERSION')
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

# --- release payload -------------------------------------------------------
# Obtaining the payload is the only thing that differs between installing a
# published release and building this checkout. Everything downstream -
# transaction, validation, registration, harness init - is shared.

function Get-PtkReleaseLayout {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$TargetRid,
        [string]$ReleaseVersion
    )

    $repository = 'AlsoBeltrix/PowerShell-Token-Killer'
    $extension = if ($TargetRid -like 'win-*') { 'zip' } else { 'tar.gz' }
    if ($ReleaseVersion) {
        $tag = if ($ReleaseVersion.StartsWith('v')) { $ReleaseVersion } else { "v$ReleaseVersion" }
    }
    else {
        $latest = Invoke-RestMethod "https://api.github.com/repos/$repository/releases/latest"
        $tag = $latest.tag_name
    }
    $number = $tag.TrimStart('v')
    $asset = "ptk-$number-$TargetRid.$extension"
    $base = "https://github.com/$repository/releases/download/$tag"

    $staging = Join-Path ([IO.Path]::GetTempPath()) ("ptk-dl-{0}" -f ([guid]::NewGuid()))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        Write-Host "Downloading $asset ($tag)..."
        $archive = Join-Path $staging $asset
        Invoke-WebRequest -Uri "$base/$asset" -OutFile $archive -UseBasicParsing
        $sums = Join-Path $staging 'SHA256SUMS'
        Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile $sums -UseBasicParsing

        $line = Get-Content -LiteralPath $sums |
            Where-Object { $_ -match [regex]::Escape($asset) } |
            Select-Object -First 1
        if (-not $line) { throw "SHA256SUMS has no entry for $asset." }
        $expected = ($line -split '\s+')[0].ToLowerInvariant()
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual) {
            throw ("Checksum mismatch for {0}.`n  expected {1}`n  actual   {2}`n" +
                'Refusing to install an unverified download.') -f $asset, $expected, $actual
        }
        Write-Host '  checksum verified'

        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        if ($extension -eq 'zip') {
            Expand-Archive -LiteralPath $archive -DestinationPath $Destination -Force
        }
        else {
            tar -xzf $archive -C $Destination
            if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $asset" }
        }
        return $number
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- rtk -------------------------------------------------------------------
# rtk is a required dependency: the server exits 78 without one. An rtk the
# user already has is used as-is and never touched; one this installer places
# is recorded so uninstall removes only that copy.

function Get-PtkRtkAssetName {
    param([Parameter(Mandatory)][string]$TargetRid)
    switch ($TargetRid) {
        'win-x64' { 'rtk-x86_64-pc-windows-msvc.zip' }
        # No upstream aarch64 Windows build; the x64 binary runs under
        # Windows ARM64 emulation and is probed below like any other.
        'win-arm64' { 'rtk-x86_64-pc-windows-msvc.zip' }
        'linux-x64' { 'rtk-x86_64-unknown-linux-musl.tar.gz' }
        'linux-arm64' { 'rtk-aarch64-unknown-linux-gnu.tar.gz' }
        'osx-arm64' { 'rtk-aarch64-apple-darwin.tar.gz' }
        default { throw "No rtk asset mapping for RID '$TargetRid'." }
    }
}

function Test-PtkRtkAnswers {
    param([Parameter(Mandatory)][string]$Path)
    # A version banner only proves the image loaded. ptk depends on the
    # rewriter answering, which is what must work under emulation.
    try {
        $rewritten = & $Path hook check --agent ptk 'git status --short' 2>$null
        return $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($rewritten)
    }
    catch { return $false }
}

function Install-PtkRtk {
    param([Parameter(Mandatory)][string]$TargetRid)

    $existing = Get-Command rtk -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($existing -and (Test-PtkRtkAnswers -Path $existing.Source)) {
        Write-Host "rtk found on PATH: $($existing.Source)"
        return
    }

    Write-Host 'rtk not found; installing it alongside ptk (required dependency).'
    $asset = Get-PtkRtkAssetName -TargetRid $TargetRid
    $staging = Join-Path ([IO.Path]::GetTempPath()) ("ptk-rtk-{0}" -f ([guid]::NewGuid()))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        $archive = Join-Path $staging $asset
        Invoke-WebRequest -UseBasicParsing `
            -Uri "https://github.com/rtk-ai/rtk/releases/latest/download/$asset" `
            -OutFile $archive
        $sums = Join-Path $staging 'checksums.txt'
        Invoke-WebRequest -UseBasicParsing `
            -Uri 'https://github.com/rtk-ai/rtk/releases/latest/download/checksums.txt' `
            -OutFile $sums

        $line = Get-Content -LiteralPath $sums |
            Where-Object { $_ -match [regex]::Escape($asset) } |
            Select-Object -First 1
        if (-not $line) { throw "rtk checksums.txt has no entry for $asset." }
        $expected = ($line -split '\s+')[0].ToLowerInvariant()
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual) { throw "Checksum mismatch for $asset; refusing to install it." }

        $extracted = Join-Path $staging 'x'
        New-Item -ItemType Directory -Path $extracted -Force | Out-Null
        if ($asset.EndsWith('.zip')) {
            Expand-Archive -LiteralPath $archive -DestinationPath $extracted -Force
        }
        else {
            tar -xzf $archive -C $extracted
            if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $asset" }
        }

        $binaryName = if ($IsWindows) { 'rtk.exe' } else { 'rtk' }
        $found = Get-ChildItem -LiteralPath $extracted -Filter $binaryName -Recurse -File |
            Select-Object -First 1
        if (-not $found) { throw "rtk archive did not contain $binaryName." }

        $target = Join-Path $ptkHome 'bin' $binaryName
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $found.FullName -Destination $target -Force
        if (-not $IsWindows) { chmod +x $target }

        if (-not (Test-PtkRtkAnswers -Path $target)) {
            $hint = if ($TargetRid -eq 'win-arm64') {
                ' On Windows ARM64 the x64 rtk runs under emulation; check that x64 emulation is available.'
            }
            else { '' }
            throw ("The installed rtk did not answer 'hook check'.$hint " +
                'ptk would refuse to start, so this install is being stopped.')
        }
        Set-Content -LiteralPath (Join-Path $ptkHome $rtkMarkerName) -Value $target -NoNewline
        Write-Host "rtk installed: $target"
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Remove-PtkInstalledRtk {
    $marker = Join-Path $ptkHome $rtkMarkerName
    if (-not (Test-Path -LiteralPath $marker)) { return }
    $ours = (Get-Content -LiteralPath $marker -Raw).Trim()
    if ($ours -and (Test-Path -LiteralPath $ours)) {
        Remove-Item -LiteralPath $ours -Force
        Write-Host "Removed the rtk this installer placed: $ours"
    }
    Remove-Item -LiteralPath $marker -Force
}

# Stamps the built version into the packaged module manifest so every
# user-visible surface agrees. ModuleVersion is a System.Version and cannot
# hold a prerelease label, so a version like 0.2.0-rc.1 splits: 0.2.0 into
# ModuleVersion and rc.1 into PrivateData.PSData.Prerelease, which is where
# PowerShell itself expects it.
function Set-PtkManifestVersion {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$PayloadVersion
    )
    $numeric, $prerelease = $PayloadVersion -split '-', 2
    # Build metadata (+sha) is not part of a module version at all.
    $numeric = ($numeric -split '\+', 2)[0]
    if ($numeric -notmatch '^\d+(\.\d+){1,3}$') {
        throw "Cannot stamp module manifest: '$PayloadVersion' has no usable numeric version."
    }

    $text = Get-Content -LiteralPath $ManifestPath -Raw
    $pattern = [regex]"(?m)^(\s*ModuleVersion\s*=\s*')[^']*(')"
    # Check the match, not whether the text changed: stamping the version the
    # manifest already carries is a no-op replacement, not a failure.
    if (-not $pattern.IsMatch($text)) {
        throw "Cannot stamp module manifest: no ModuleVersion assignment found in $ManifestPath."
    }
    $updated = $pattern.Replace($text, "`${1}$numeric`${2}", 1)

    if ($prerelease) {
        # PSData.Prerelease may not contain a leading hyphen or dots in the
        # PowerShell gallery sense; normalize rc.1 to rc1.
        $tag = ($prerelease -replace '[^A-Za-z0-9]', '')
        $updated = $updated.TrimEnd() -replace '\}\s*$', @"
    PrivateData       = @{
        PSData = @{
            Prerelease = '$tag'
        }
    }
}
"@
    }
    Set-Content -LiteralPath $ManifestPath -Value $updated -NoNewline

    $check = Import-PowerShellDataFile -LiteralPath $ManifestPath
    if ($check.ModuleVersion -ne $numeric) {
        throw "Module manifest stamp failed: expected $numeric, manifest reports $($check.ModuleVersion)."
    }
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
    Set-PtkManifestVersion `
        -ManifestPath (Join-Path $src.FullName 'PwshTokenCompressor.psd1') `
        -PayloadVersion $PayloadVersion
    $scripts = New-Item -ItemType Directory -Path (Join-Path $Destination 'scripts') -Force
    foreach ($f in 'ptk-hook.ps1', 'ptk_init.ps1', 'install.ps1',
        'ptk_install_transaction.psm1') {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts' $f) -Destination $scripts.FullName
    }
    # Apache-2.0 requires the licence to travel with the distribution, and a
    # user inspecting an installed payload should find it without the repo.
    foreach ($f in 'LICENSE', 'README.md') {
        Copy-Item -LiteralPath (Join-Path $repoRoot $f) -Destination $Destination
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
        'pwsh -NoProfile -File "{0}" -Uninstall' -f (Join-Path $ptkHome 'scripts' 'install.ps1'))
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
    # Kimi's data root moves with KIMI_CODE_HOME (same rule as ptk_init).
    $kimiHome = [string]$env:KIMI_CODE_HOME ? $env:KIMI_CODE_HOME : (Join-Path $HOME '.kimi-code')
    @(
        (Join-Path $HOME '.claude.json')
        (Join-Path $HOME '.claude' 'settings.json')
        (Join-Path $HOME '.claude' 'CLAUDE.md')
        (Join-Path $HOME '.codex' 'config.toml')
        (Join-Path $HOME '.codex' 'AGENTS.md')
        (Join-Path $HOME '.grok' 'config.toml')
        (Join-Path $HOME '.gemini' 'config' 'mcp_config.json')
        (Join-Path $HOME '.gemini' 'config' 'plugins' 'ptk')
        (Join-Path $kimiHome 'mcp.json')
        (Join-Path $kimiHome 'config.toml')
        (Join-Path $kimiHome 'AGENTS.md')
    )
}

function Invoke-PtkPackageSmoke {
    param([Parameter(Mandatory)][string]$BinaryPath)

    $handshake = Join-Path $repoRoot 'server' 'test-handshake.ps1'
    if (-not (Test-Path -LiteralPath $handshake -PathType Leaf)) {
        throw "Package smoke script is unavailable: $handshake"
    }
    # Say what this is before it happens. The smoke test starts local worker
    # processes and opens named test sessions; unannounced, that reads like an
    # installer connecting to something.
    Write-Host "Validating staged PTK package: $BinaryPath"
    Write-Host ('  Local smoke test: starts the packaged server and two throwaway ' +
        'PowerShell worker processes to prove session isolation. No network, no ' +
        'external services.')
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
        Assert-PtkNativeBuildRid -TargetRid $targetRid
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
    Assert-PtkRuntimeNotRunning
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
        Remove-PtkInstalledRtk
        Remove-PtkPayload
        if ($Purge -and (Test-Path -LiteralPath $ptkHome)) {
            Remove-Item -LiteralPath $ptkHome -Recurse -Force
            Write-Host "ptk uninstalled and $ptkHome purged."
        }
        else {
            Write-Host 'ptk uninstalled. User-owned files under ~/.ptk (if any) were kept.'
        }
    }
    default {
        Assert-NotElevated
Assert-PtkRuntimeNotRunning
        $targetRid = Get-PtkRid
        $payloadVersion = Get-PtkVersion
        $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-stage-{0}" -f ([guid]::NewGuid()))
        $snapshot = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-rollback-{0}" -f ([guid]::NewGuid()))
        New-Item -ItemType Directory -Path $staging | Out-Null
        try {
            if ($FromRelease) {
                $payloadVersion = Get-PtkReleaseLayout `
                    -Destination $staging -TargetRid $targetRid -ReleaseVersion $Version
            }
            else {
                New-PtkLayout -Destination $staging -TargetRid $targetRid -PayloadVersion $payloadVersion
            }
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
                    # Before any harness is pointed at this payload: the server
                    # exits 78 without rtk, and an install that registers a
                    # server which cannot start is not a successful install.
                    Install-PtkRtk -TargetRid $targetRid
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
