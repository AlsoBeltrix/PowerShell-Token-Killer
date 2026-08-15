<#
.SYNOPSIS
Builds the standalone PtkSiemReceiver release layout for one supported RID.

.DESCRIPTION
This is the single package-layout path used by local verification, CI, and
release.yml. It stamps the requested release version into the binary, includes
the operator guide and required licenses, and writes a VERSION marker. Archive
creation and platform signing remain release-workflow responsibilities.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDir,

    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-arm64')]
    [string]$Rid,

    [Parameter(Mandatory)]
    [ValidatePattern('^[vV]?\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$destination = [IO.Path]::GetFullPath($OutputDir)
$payloadVersion = $Version -replace '^[vV]', ''

if ((Test-Path -LiteralPath $destination) -and
    (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1)) {
    throw "OutputDir '$destination' is not empty - refusing to clobber."
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null

Write-Information "Publishing PtkSiemReceiver ($Rid, $payloadVersion)..." `
    -InformationAction Continue
dotnet publish (Join-Path $PSScriptRoot 'PtkSiemReceiver' 'PtkSiemReceiver.csproj') `
    -c Release -r $Rid --self-contained true `
    "-p:Version=$payloadVersion" `
    -o $destination -v q --nologo | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "PtkSiemReceiver publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PtkSiemReceiver' 'README.md') `
    -Destination (Join-Path $destination 'README.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manage.ps1') `
    -Destination (Join-Path $destination 'manage.ps1')
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') `
    -Destination (Join-Path $destination 'LICENSE')

$thirdParty = New-Item -ItemType Directory `
    -Path (Join-Path $destination 'THIRD-PARTY-LICENSES') -Force
Copy-Item -LiteralPath (
    Join-Path $PSScriptRoot 'PtkSiemReceiver' 'Protos' 'LICENSE.OpenTelemetry-Apache-2.0.txt') `
    -Destination (Join-Path $thirdParty.FullName 'OpenTelemetry-Apache-2.0.txt')
Copy-Item -LiteralPath (
    Join-Path $PSScriptRoot 'PtkSiemReceiver' 'Protos' 'LICENSE.Microsoft.Extensions.Hosting.WindowsServices-MIT.txt') `
    -Destination (Join-Path $thirdParty.FullName 'Microsoft.Extensions.Hosting.WindowsServices-MIT.txt')

Set-Content -LiteralPath (Join-Path $destination 'VERSION') `
    -Value $payloadVersion -NoNewline

Write-Information "SIEM package layout ready: $destination ($Rid, $payloadVersion)" `
    -InformationAction Continue
