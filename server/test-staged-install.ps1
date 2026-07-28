#!/usr/bin/env pwsh
#Requires -Version 7

<#
.SYNOPSIS
Exercises one already-published PTK layout through the real install
transaction in a disposable home. The staged and activated binary each run
the complete public handshake; no live payload or harness registration is
touched.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$LayoutRoot,
    [int]$TimeoutSec = 90
)

$ErrorActionPreference = 'Stop'
$serverDir = Split-Path -Parent $PSCommandPath
$layout = (Resolve-Path -LiteralPath $LayoutRoot).ProviderPath
$transactionModule = Join-Path $layout 'scripts' 'ptk_install_transaction.psm1'
if (-not (Test-Path -LiteralPath $transactionModule -PathType Leaf)) {
    throw "Published transaction module is missing: $transactionModule"
}
Import-Module $transactionModule -Force

$binaryName = $IsWindows ? 'PtkMcpServer.exe' : 'PtkMcpServer'
$handshake = Join-Path $serverDir 'test-handshake.ps1'
$testRoot = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
) ('.ptk/staged-install-tests/' + [guid]::NewGuid().ToString('N'))
$staging = Join-Path $testRoot 'staging'
$payload = Join-Path $testRoot 'home'
$snapshot = Join-Path $testRoot 'snapshot'
$registration = Join-Path $testRoot 'registration.toml'

function Invoke-LayoutHandshake {
    param([Parameter(Mandatory)][string]$Root)
    $binary = Join-Path $Root 'bin' $binaryName
    & pwsh -NoProfile -File $handshake `
        -ServerCommand $binary `
        -TimeoutSec $TimeoutSec |
        Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Published package handshake failed: $binary"
    }
}

New-Item -ItemType Directory -Path $testRoot, $payload -Force | Out-Null
try {
    Copy-Item -LiteralPath $layout -Destination $staging -Recurse -Force
    foreach ($entry in 'bin', 'src', 'scripts') {
        New-Item -ItemType Directory -Path (Join-Path $payload $entry) -Force |
            Out-Null
        Set-Content `
            -LiteralPath (Join-Path $payload $entry 'old.txt') `
            -Value "old-$entry" `
            -NoNewline
    }
    Set-Content -LiteralPath (Join-Path $payload 'VERSION') -Value 'old' -NoNewline
    Set-Content `
        -LiteralPath (Join-Path $payload 'user-owned.conf') `
        -Value 'preserve' `
        -NoNewline
    Set-Content -LiteralPath $registration -Value 'old-registration' -NoNewline

    Invoke-PtkInstallTransaction `
        -StagingRoot $staging `
        -PayloadRoot $payload `
        -PayloadEntries @('bin', 'src', 'scripts', 'VERSION') `
        -RegistrationPaths @($registration) `
        -SnapshotRoot $snapshot `
        -StagedValidation {
            param($stagedRoot)
            Invoke-LayoutHandshake -Root $stagedRoot
        } `
        -InstalledValidation {
            param($installedRoot)
            Invoke-LayoutHandshake -Root $installedRoot
        } `
        -RegistrationCutover {
            Set-Content `
                -LiteralPath $registration `
                -Value 'new-registration' `
                -NoNewline
        }

    if ((Get-Content -LiteralPath $registration -Raw) -cne 'new-registration') {
        throw 'Disposable registration cutover did not land.'
    }
    if ((Get-Content -LiteralPath (Join-Path $payload 'user-owned.conf') -Raw) -cne
        'preserve') {
        throw 'Disposable install changed user-owned payload-root content.'
    }
    if (Test-Path -LiteralPath $snapshot) {
        throw 'Disposable install retained its sensitive rollback snapshot.'
    }
    Write-Host 'STAGED INSTALL TEST PASSED'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
