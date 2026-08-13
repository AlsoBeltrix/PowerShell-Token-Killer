#!/usr/bin/env pwsh
#Requires -Version 7

[CmdletBinding()]
param(
    [string]$ReadmePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'README.md')
)

$ErrorActionPreference = 'Stop'
$readme = [IO.File]::ReadAllText($ReadmePath).
    Replace("`r`n", "`n", [StringComparison]::Ordinal).
    Replace("`r", "`n", [StringComparison]::Ordinal)

function Assert-Match {
    param(
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Failure
    )
    if (-not [Regex]::IsMatch($readme, $Pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
        throw $Failure
    }
}

function Assert-Omission {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Failure
    )
    if ($readme.Contains($Text, [StringComparison]::Ordinal)) {
        throw $Failure
    }
}

Assert-Match `
    -Pattern '(?m)^- \*\*Windows\*\* assets are Authenticode-signed with Azure Trusted Signing and$' `
    -Failure 'README no longer states the Windows Authenticode signing contract.'
Assert-Match `
    -Pattern '(?m)^- \*\*macOS\*\* assets are Developer ID-signed with hardened runtime and$' `
    -Failure 'README no longer states the macOS Developer ID signing contract.'
Assert-Match `
    -Pattern '(?ms)^- \*\*macOS\*\* assets are Developer ID-signed with hardened runtime and\r?\n  notarized with Apple\.' `
    -Failure 'README no longer states the macOS notarization contract.'
Assert-Match `
    -Pattern '(?m)^- \*\*Linux\*\* assets are not publisher code-signed\. The installer verifies each$' `
    -Failure 'README must say Linux release assets are not publisher code-signed.'
Assert-Match `
    -Pattern '(?ms)^- \*\*Linux\*\* assets are not publisher code-signed\..*?`SHA256SUMS` before extraction\.' `
    -Failure 'README must bind Linux release integrity to SHA256SUMS.'
Assert-Match `
    -Pattern '(?ms)`-FromRelease` preserves the Windows and macOS signatures and verifies every\r?\nplatform asset against `SHA256SUMS`' `
    -Failure 'README must qualify signature preservation while covering every platform checksum.'

Assert-Omission `
    -Text 'Installs self-contained, **signed** binaries' `
    -Failure 'Public install still calls every platform binary signed.'
Assert-Omission `
    -Text 'Release binaries are signed' `
    -Failure 'Signing section still calls every platform binary signed.'
Assert-Omission `
    -Text '-FromRelease` preserves those signatures' `
    -Failure 'Release install still implies every platform carries a signature.'

'RELEASE SIGNING DOCUMENTATION TEST PASSED'
