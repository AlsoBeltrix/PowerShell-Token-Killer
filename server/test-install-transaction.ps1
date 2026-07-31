#!/usr/bin/env pwsh
#Requires -Version 7

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (
    Join-Path $repoRoot 'scripts' 'ptk_install_transaction.psm1'
) -Force

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Set-CaseState {
    param([Parameter(Mandatory)][string]$CaseRoot)

    $payload = Join-Path $CaseRoot 'home'
    $registration = Join-Path $CaseRoot 'registration'
    $staging = Join-Path $CaseRoot 'staging'
    New-Item -ItemType Directory -Path $payload, $registration, $staging -Force |
        Out-Null
    if ($IsWindows) {
        $payloadDirectory = [IO.DirectoryInfo]::new($payload)
        $payloadSecurity = [IO.FileSystemAclExtensions]::GetAccessControl(
            $payloadDirectory)
        $payloadSecurity.SetOwner(
            [Security.Principal.WindowsIdentity]::GetCurrent().User)
        [IO.FileSystemAclExtensions]::SetAccessControl(
            $payloadDirectory,
            $payloadSecurity)
    }

    foreach ($entry in 'bin', 'src', 'scripts') {
        New-Item -ItemType Directory -Path (Join-Path $payload $entry) -Force |
            Out-Null
        Set-Content `
            -LiteralPath (Join-Path $payload $entry 'old.txt') `
            -Value "old-$entry" `
            -NoNewline

        New-Item -ItemType Directory -Path (Join-Path $staging $entry) -Force |
            Out-Null
        Set-Content `
            -LiteralPath (Join-Path $staging $entry 'new.txt') `
            -Value "new-$entry" `
            -NoNewline
    }
    Set-Content -LiteralPath (Join-Path $payload 'VERSION') -Value 'old' -NoNewline
    Set-Content -LiteralPath (Join-Path $staging 'VERSION') -Value 'new' -NoNewline
    Set-Content -LiteralPath (Join-Path $payload 'user-owned.conf') -Value 'keep' -NoNewline

    $registrationFile = Join-Path $registration 'config.toml'
    $registrationDirectory = Join-Path $registration 'plugin'
    $missingRegistration = Join-Path $registration 'missing.json'
    $externalStateFile = Join-Path $registration 'external.state'
    Set-Content -LiteralPath $registrationFile -Value 'old-registration' -NoNewline
    Set-Content -LiteralPath $externalStateFile -Value 'old-external' -NoNewline
    New-Item -ItemType Directory -Path $registrationDirectory -Force | Out-Null
    Set-Content `
        -LiteralPath (Join-Path $registrationDirectory 'old.json') `
        -Value 'old-plugin' `
        -NoNewline
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode(
            (Join-Path $payload 'bin' 'old.txt'),
            [IO.UnixFileMode]::UserRead -bor
            [IO.UnixFileMode]::UserWrite -bor
            [IO.UnixFileMode]::UserExecute)
        [IO.File]::SetUnixFileMode(
            $registrationFile,
            [IO.UnixFileMode]::UserRead -bor
            [IO.UnixFileMode]::UserWrite)
    }

    [pscustomobject]@{
        Payload = $payload
        Staging = $staging
        RegistrationFile = $registrationFile
        RegistrationDirectory = $registrationDirectory
        MissingRegistration = $missingRegistration
        ExternalStateFile = $externalStateFile
        RegistrationPaths = @(
            $registrationFile,
            $registrationDirectory,
            $missingRegistration
        )
    }
}

function Assert-OldState {
    param([Parameter(Mandatory)]$State)

    foreach ($entry in 'bin', 'src', 'scripts') {
        Assert-True `
            -Condition (Test-Path -LiteralPath (Join-Path $State.Payload $entry 'old.txt')) `
            -Message "Old payload entry was not restored: $entry"
        Assert-True `
            -Condition (-not (Test-Path -LiteralPath (Join-Path $State.Payload $entry 'new.txt'))) `
            -Message "New payload residue survived rollback: $entry"
    }
    Assert-True `
        -Condition ((Get-Content -LiteralPath (Join-Path $State.Payload 'VERSION') -Raw) -ceq 'old') `
        -Message 'VERSION was not restored.'
    Assert-True `
        -Condition ((Get-Content -LiteralPath $State.RegistrationFile -Raw) -ceq 'old-registration') `
        -Message 'Registration file was not restored.'
    Assert-True `
        -Condition (Test-Path -LiteralPath (Join-Path $State.RegistrationDirectory 'old.json')) `
        -Message 'Registration directory was not restored.'
    Assert-True `
        -Condition (-not (Test-Path -LiteralPath $State.MissingRegistration)) `
        -Message 'Originally missing registration survived rollback.'
    Assert-True `
        -Condition ((Get-Content -LiteralPath $State.ExternalStateFile -Raw) -ceq 'old-external') `
        -Message 'External installer state was not restored.'
    Assert-True `
        -Condition ((Get-Content -LiteralPath (Join-Path $State.Payload 'user-owned.conf') -Raw) -ceq 'keep') `
        -Message 'User-owned payload-root content changed.'
}

function Invoke-FailingCase {
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$ActivationFaultAfterEntry = 0,
        [switch]$FailStagedValidation,
        [switch]$FailInstalledValidation,
        [switch]$FailRegistration,
        [switch]$FailExternalRestore,
        [switch]$ExpectUnconfirmedRollback
    )

    $caseRoot = Join-Path $root $Name
    $state = Set-CaseState -CaseRoot $caseRoot
    $snapshot = Join-Path $caseRoot 'snapshot'
    $failed = $false
    try {
        Invoke-PtkInstallTransaction `
            -StagingRoot $state.Staging `
            -PayloadRoot $state.Payload `
            -PayloadEntries @('bin', 'src', 'scripts', 'VERSION') `
            -RegistrationPaths $state.RegistrationPaths `
            -SnapshotRoot $snapshot `
            -ActivationFaultAfterEntry $ActivationFaultAfterEntry `
            -CaptureExternalState {
                Get-Content -LiteralPath $state.ExternalStateFile -Raw
            } `
            -RestoreExternalState {
                param($captured)
                if ($FailExternalRestore) {
                    throw 'Injected external-state rollback failure.'
                }
                Set-Content `
                    -LiteralPath $state.ExternalStateFile `
                    -Value $captured `
                    -NoNewline
            } `
            -AssertExternalStateRestored {
                param($captured)
                Assert-True `
                    -Condition ((Get-Content -LiteralPath $state.ExternalStateFile -Raw) -ceq $captured) `
                    -Message 'External state rollback was not exact.'
            } `
            -StagedValidation {
                param($stagingRoot)
                if ($FailStagedValidation) {
                    throw 'Injected staged validation failure.'
                }
                Assert-True `
                    -Condition (Test-Path -LiteralPath (Join-Path $stagingRoot 'VERSION')) `
                    -Message 'Staged validation did not receive the staged root.'
            } `
            -InstalledValidation {
                param($payloadRoot)
                if ($FailInstalledValidation) {
                    throw 'Injected installed validation failure.'
                }
                Assert-True `
                    -Condition (Test-Path -LiteralPath (Join-Path $payloadRoot 'VERSION')) `
                    -Message 'Installed validation did not receive the payload root.'
            } `
            -RegistrationCutover {
                Set-Content `
                    -LiteralPath $state.RegistrationFile `
                    -Value 'new-registration' `
                    -NoNewline
                Remove-Item -LiteralPath $state.RegistrationDirectory -Recurse -Force
                Set-Content `
                    -LiteralPath $state.MissingRegistration `
                    -Value 'new-registration' `
                    -NoNewline
                Set-Content `
                    -LiteralPath $state.ExternalStateFile `
                    -Value 'new-external' `
                    -NoNewline
                if ($FailRegistration) {
                    throw 'Injected registration failure.'
                }
            }
    }
    catch {
        $failed = $true
        $failure = $_
    }
    Assert-True -Condition $failed -Message "Fault case '$Name' unexpectedly succeeded."
    if ($ExpectUnconfirmedRollback) {
        Assert-True `
            -Condition ($failure.Exception.Message -match 'rollback could not be confirmed') `
            -Message "Fault case '$Name' did not report unconfirmed rollback."
        Assert-True `
            -Condition (Test-Path -LiteralPath $snapshot -PathType Container) `
            -Message "Fault case '$Name' discarded the recovery snapshot."
        $manifest = Get-Content `
            -LiteralPath (Join-Path $snapshot 'manifest.json') `
            -Raw |
            ConvertFrom-Json
        Assert-True `
            -Condition ($manifest.format -ceq 'ptk.install-snapshot/1') `
            -Message "Fault case '$Name' retained an unrecognized recovery manifest."
        Assert-True `
            -Condition (@($manifest.records).Count -eq 7) `
            -Message "Fault case '$Name' recovery manifest omitted tracked paths."
        Assert-True `
            -Condition ((Get-Content -LiteralPath $state.RegistrationFile -Raw) -ceq 'old-registration') `
            -Message "Fault case '$Name' did not restore registration files before the external fault."
        Assert-True `
            -Condition ((Get-Content -LiteralPath (Join-Path $state.Payload 'VERSION') -Raw) -ceq 'old') `
            -Message "Fault case '$Name' did not restore the payload before the external fault."
        Assert-True `
            -Condition ((Get-Content -LiteralPath $state.ExternalStateFile -Raw) -ceq 'new-external') `
            -Message "Fault case '$Name' unexpectedly claimed external-state restoration."
        Assert-True `
            -Condition ((Get-Content -LiteralPath (Join-Path $state.Payload 'user-owned.conf') -Raw) -ceq 'keep') `
            -Message "Fault case '$Name' changed user-owned payload content."
    }
    else {
        Assert-OldState -State $state
        Assert-True `
            -Condition (-not (Test-Path -LiteralPath $snapshot)) `
            -Message "Fault case '$Name' retained its sensitive snapshot."
    }
}

$root = Join-Path ([IO.Path]::GetTempPath()) (
    'ptk-install-transaction-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    Invoke-FailingCase -Name 'staged-validation' -FailStagedValidation
    foreach ($entry in 1..4) {
        Invoke-FailingCase `
            -Name "activation-$entry" `
            -ActivationFaultAfterEntry $entry
    }
    Invoke-FailingCase -Name 'installed-validation' -FailInstalledValidation
    Invoke-FailingCase -Name 'registration' -FailRegistration
    Invoke-FailingCase `
        -Name 'unconfirmed-external-rollback' `
        -FailRegistration `
        -FailExternalRestore `
        -ExpectUnconfirmedRollback

    $successRoot = Join-Path $root 'success'
    $success = Set-CaseState -CaseRoot $successRoot
    $successSnapshot = Join-Path $successRoot 'snapshot'
    Invoke-PtkInstallTransaction `
        -StagingRoot $success.Staging `
        -PayloadRoot $success.Payload `
        -PayloadEntries @('bin', 'src', 'scripts', 'VERSION') `
        -RegistrationPaths $success.RegistrationPaths `
        -SnapshotRoot $successSnapshot `
        -CaptureExternalState {
            Get-Content -LiteralPath $success.ExternalStateFile -Raw
        } `
        -RestoreExternalState {
            param($captured)
            Set-Content `
                -LiteralPath $success.ExternalStateFile `
                -Value $captured `
                -NoNewline
        } `
        -AssertExternalStateRestored {
            param($captured)
            Assert-True `
                -Condition ((Get-Content -LiteralPath $success.ExternalStateFile -Raw) -ceq $captured) `
                -Message 'Successful transaction unexpectedly needed external rollback.'
        } `
        -StagedValidation { param($stagingRoot) } `
        -InstalledValidation { param($payloadRoot) } `
        -RegistrationCutover {
            Set-Content `
                -LiteralPath $success.RegistrationFile `
                -Value 'new-registration' `
                -NoNewline
            Set-Content `
                -LiteralPath $success.ExternalStateFile `
                -Value 'new-external' `
                -NoNewline
        }

    Assert-True `
        -Condition ((Get-Content -LiteralPath (Join-Path $success.Payload 'VERSION') -Raw) -ceq 'new') `
        -Message 'Successful transaction did not activate the staged payload.'
    Assert-True `
        -Condition ((Get-Content -LiteralPath $success.RegistrationFile -Raw) -ceq 'new-registration') `
        -Message 'Successful transaction did not retain registration cutover.'
    Assert-True `
        -Condition ((Get-Content -LiteralPath $success.ExternalStateFile -Raw) -ceq 'new-external') `
        -Message 'Successful transaction did not retain external cutover.'
    Assert-True `
        -Condition ((Get-Content -LiteralPath (Join-Path $success.Payload 'user-owned.conf') -Raw) -ceq 'keep') `
        -Message 'Successful transaction changed user-owned content.'
    Assert-True `
        -Condition (-not (Test-Path -LiteralPath $successSnapshot)) `
        -Message 'Successful transaction retained its sensitive snapshot.'

    Write-Host 'INSTALL TRANSACTION TEST PASSED'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
