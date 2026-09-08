#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DownloadRoot,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$Rid,
    [Parameter(Mandatory)][string]$RtkPath
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if ([Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier -cne $Rid) {
    throw "Downloaded proof requires native $Rid hardware."
}
if ($IsWindows -and [Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run downloaded product acceptance as a standard Windows user.'
}
$downloads = (Resolve-Path $DownloadRoot).ProviderPath
$fixture = Join-Path ([Environment]::GetFolderPath('UserProfile')) (
    '.ptk-downloaded-proof-' + [guid]::NewGuid().ToString('N'))
$layout = Join-Path $fixture 'layout'
$siem = Join-Path $fixture 'siem'
$installed = Join-Path $fixture 'home/.ptk'
$staging = Join-Path $fixture 'staging'
$snapshot = Join-Path $fixture 'snapshot'
$suffix = $IsWindows ? 'zip' : 'tar.gz'
$serverName = $IsWindows ? 'PtkMcpServer.exe' : 'PtkMcpServer'
$pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
$savedRtk = $env:PTK_RTK_PATH
$env:PTK_RTK_PATH = $RtkPath

function Invoke-ProofScript {
    param([string]$Path, [string[]]$Arguments)
    & $pwsh -NoProfile -File $Path @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Downloaded proof failed: $Path (exit $LASTEXITCODE)" }
}
function Expand-ProofArchive {
    param([string]$Archive, [string]$Destination)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    if ($IsWindows) { Expand-Archive -LiteralPath $Archive -DestinationPath $Destination }
    else {
        tar -xzf $Archive -C $Destination
        if ($LASTEXITCODE -ne 0) { throw "Archive extraction failed: $Archive" }
    }
}
try {
    New-Item -ItemType Directory -Path $fixture, $installed -Force | Out-Null
    $siemArchive = Join-Path $downloads "ptk-siem-receiver-$Version-$Rid.$suffix"
    Expand-ProofArchive (Join-Path $downloads "ptk-$Version-$Rid.$suffix") $layout
    Expand-ProofArchive $siemArchive $siem
    if ($IsWindows) {
        $signed = @(Get-ChildItem $layout, $siem -Recurse -File | Where-Object Extension -in '.exe', '.dll')
        foreach ($file in $signed) {
            $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
            if ($signature.Status -ne 'Valid') { throw "Invalid Authenticode signature: $($file.FullName): $($signature.Status)" }
        }
        Write-Host "Downloaded Authenticode signatures valid: $($signed.Count)"
    }
    elseif ($IsMacOS) {
        $count = 0
        foreach ($file in Get-ChildItem $layout, $siem -Recurse -File) {
            $type = & /usr/bin/file -b $file.FullName
            if ($LASTEXITCODE -ne 0) { throw 'file inspection failed.' }
            if ($type -notmatch 'Mach-O') { continue }
            & /usr/bin/codesign --verify --strict --check-notarization $file.FullName
            if ($LASTEXITCODE -ne 0) { throw "Signature/notarization failed: $($file.FullName)" }
            $authority = & /usr/bin/codesign -dvv $file.FullName 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0 -or $authority -notmatch 'Authority=Developer ID Application:') {
                throw "Missing Developer ID: $($file.FullName)"
            }
            $count++
        }
        if ($count -lt 3) { throw 'No complete native signature inventory was inspected.' }
        Write-Host "Downloaded Developer ID signatures and online notarization valid: $count"
    }

    Invoke-ProofScript (Join-Path $PSScriptRoot 'test-staged-install.ps1') @('-LayoutRoot', $layout)
    Import-Module (Join-Path $layout 'scripts/ptk_install_transaction.psm1') -Force
    Copy-Item $layout $staging -Recurse
    Set-Content (Join-Path $installed 'user-owned.conf') 'preserve' -NoNewline
    Invoke-PtkInstallTransaction -StagingRoot $staging -PayloadRoot $installed `
        -PayloadEntries @('bin', 'src', 'scripts', 'VERSION', 'BUILD-PROVENANCE.json', 'LICENSE', 'README.md') `
        -RegistrationPaths @() -SnapshotRoot $snapshot `
        -StagedValidation {
            param($root)
            Invoke-ProofScript (Join-Path $PSScriptRoot 'test-handshake.ps1') @('-ServerCommand', (Join-Path $root 'bin' $serverName))
        } `
        -InstalledValidation {
            param($root)
            Invoke-ProofScript (Join-Path $PSScriptRoot 'test-handshake.ps1') @('-ServerCommand', (Join-Path $root 'bin' $serverName))
        } `
        -RegistrationCutover { }
    $server = Join-Path $installed 'bin' $serverName
    Invoke-ProofScript (Join-Path $repository 'siem/verify-package.ps1') @(
        '-PackageDir', $siem, '-Rid', $Rid, '-Version', $Version,
        '-SourceCommit', $SourceCommit, '-RequireCleanSource')
    Invoke-ProofScript (Join-Path $repository 'siem/operator-workflow-proof.ps1') @(
        '-PtkServerPath', $server, '-PackageDir', $siem, '-ArchivePath', $siemArchive,
        '-ChecksumFile', (Join-Path $downloads 'SHA256SUMS'), '-Version', $Version, '-Rid', $Rid,
        '-RtkPath', $RtkPath, '-DestinationToolPath', (Join-Path $installed 'scripts/ptk-audit-destination.ps1'))
    Invoke-ProofScript (Join-Path $PSScriptRoot 'direct-product-proof.ps1') @(
        '-ServerPath', $server, '-RequireCleanSource', '-UninstallHome', $installed)
    if ((Get-Content (Join-Path $installed 'user-owned.conf') -Raw) -cne 'preserve') {
        throw 'Downloaded uninstall removed user-owned content.'
    }
    Write-Host "DOWNLOADED PRODUCT PASSED: $Version $Rid $SourceCommit"
}
finally {
    $env:PTK_RTK_PATH = $savedRtk
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
