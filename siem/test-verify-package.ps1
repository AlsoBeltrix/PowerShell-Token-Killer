#Requires -Version 7
<#
.SYNOPSIS
Guards release-bound SIEM package verification and its workflow callers.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$PackageDir,

    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-arm64')]
    [string]$Rid,

    [Parameter(Mandatory)]
    [ValidatePattern('^[vV]?\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{7,40}$')]
    [string]$SourceCommit,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$SourceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifier = Join-Path $PSScriptRoot 'verify-package.ps1'
$normalizedCommit = $SourceCommit.ToLowerInvariant()
$expectedStampedCommit = $normalizedCommit.Substring(0, 7)
$common = @{
    PackageDir = $PackageDir
    Rid = $Rid
    Version = $Version
    SourceRoot = $SourceRoot
}

$command = Get-Command $verifier
$sourceCommitParameter = $command.Parameters['SourceCommit']
$sourceCommitAttribute = @($sourceCommitParameter.Attributes | Where-Object {
        $_ -is [Management.Automation.ParameterAttribute]
    } | Select-Object -First 1)
if ($null -eq $sourceCommitParameter -or
    $sourceCommitAttribute.Count -ne 1 -or
    -not $sourceCommitAttribute[0].Mandatory) {
    throw 'verify-package.ps1 must require -SourceCommit.'
}
$sourceRootAttribute = @($command.Parameters['SourceRoot'].Attributes | Where-Object {
        $_ -is [Management.Automation.ParameterAttribute]
    } | Select-Object -First 1)
if ($sourceRootAttribute.Count -eq 1 -and $sourceRootAttribute[0].Mandatory) {
    throw 'verify-package.ps1 -SourceRoot must remain optional for verification of older published artifacts.'
}

& $verifier @common -SourceCommit $normalizedCommit

if ($normalizedCommit.Length -gt 7) {
    & $verifier @common -SourceCommit $expectedStampedCommit
}

$replacement = if ($normalizedCommit[0] -ceq '0') { '1' } else { '0' }
$wrongCommit = $replacement + $normalizedCommit.Substring(1)
$wrongCommitRejected = $false
try {
    & $verifier @common -SourceCommit $wrongCommit
}
catch {
    if ($_.Exception.Message -notmatch 'informational version mismatch') {
        throw "Wrong source commit failed for an unexpected reason: $($_.Exception.Message)"
    }
    $wrongCommitRejected = $true
}
if (-not $wrongCommitRejected) {
    throw 'verify-package.ps1 accepted a package whose release-bound source commit did not match.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$ci = Get-Content -LiteralPath (Join-Path $repoRoot '.github/workflows/ci.yml') -Raw
$release = Get-Content -LiteralPath (Join-Path $repoRoot '.github/workflows/release.yml') -Raw
if ($ci -notmatch 'test-verify-package\.ps1[\s\S]*?-SourceCommit[\s\S]*?-SourceRoot') {
    throw 'CI does not pass release-bound source identity into the package verifier guard.'
}
if ($release -notmatch 'verify-package\.ps1[\s\S]*?-SourceCommit[\s\S]*?-SourceRoot') {
    throw 'Release workflow does not pass release-bound source identity into package verification.'
}

Write-Information "SIEM package verifier guards passed: $PackageDir ($Rid, $expectedStampedCommit)" `
    -InformationAction Continue
