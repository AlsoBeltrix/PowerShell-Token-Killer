#!/usr/bin/env pwsh
#Requires -Version 7

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$installerPath = Join-Path $repoRoot 'scripts' 'install.ps1'
$readmePath = Join-Path $repoRoot 'README.md'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $installerPath,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Installer parse failed: $($parseErrors[0].Message)"
}
$functions = @($ast.FindAll(
        {
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -cin @(
                    'Select-PtkLatestPublishedReleaseTag',
                    'Get-PtkLatestPublishedReleaseTag')
        },
        $true))
if ($functions.Count -ne 2) {
    throw 'Installer does not define exactly one release fetcher and selector.'
}
foreach ($function in $functions) {
    . ([scriptblock]::Create($function.Extent.Text))
}

function New-Release {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][bool]$Draft,
        [AllowNull()]$PublishedAt,
        [bool]$Prerelease = $false
    )
    [pscustomobject]@{
        tag_name = $Tag
        draft = $Draft
        prerelease = $Prerelease
        published_at = $PublishedAt
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)][string]$Message
    )
    if ($Expected -cne $Actual) {
        throw "$Message Expected [$Expected], found [$Actual]."
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Message
    )
    try {
        & $Action
    }
    catch {
        return
    }
    throw $Message
}

$selected = Select-PtkLatestPublishedReleaseTag -Release @(
    (New-Release -Tag 'v0.2.2' -Draft $false -PublishedAt '2026-08-01T00:00:00Z'),
    (New-Release -Tag 'v0.4.0-draft' -Draft $true `
        -PublishedAt '2026-08-14T00:00:00Z'),
    (New-Release -Tag 'v0.3.0-rc.1' -Draft $false -Prerelease $true `
        -PublishedAt ([DateTimeOffset]'2026-08-13T01:38:18Z')),
    (New-Release -Tag 'v0.1.0-unpublished' -Draft $false -PublishedAt $null)
)
Assert-Equal `
    -Expected 'v0.3.0-rc.1' `
    -Actual $selected `
    -Message 'Latest published prerelease was not selected.'

$selected = Select-PtkLatestPublishedReleaseTag -Release @(
    (New-Release -Tag 'v0.3.0-rc.1' -Draft $false -Prerelease $true `
        -PublishedAt '2026-08-13T01:38:18Z'),
    (New-Release -Tag 'v0.3.0' -Draft $false `
        -PublishedAt ([datetime]'2026-08-14T01:00:00Z'))
)
Assert-Equal `
    -Expected 'v0.3.0' `
    -Actual $selected `
    -Message 'A newer stable release was not selected.'

$script:releaseResponse = @(
    (New-Release -Tag 'v0.2.2' -Draft $false -PublishedAt '2026-08-01T00:00:00Z'),
    (New-Release -Tag 'v0.3.0-rc.1' -Draft $false -Prerelease $true `
        -PublishedAt '2026-08-13T01:38:18Z')
)
$script:observedUri = $null
$script:observedHeaders = $null
function Invoke-RestMethod {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][hashtable]$Headers
    )
    $script:observedUri = $Uri
    $script:observedHeaders = $Headers
    Write-Output -NoEnumerate $script:releaseResponse
}
$selected = Get-PtkLatestPublishedReleaseTag `
    -Repository 'AlsoBeltrix/PowerShell-Token-Killer'
Assert-Equal `
    -Expected 'v0.3.0-rc.1' `
    -Actual $selected `
    -Message 'Fetcher did not preserve the GitHub release array for selection.'
Assert-Equal `
    -Expected 'https://api.github.com/repos/AlsoBeltrix/PowerShell-Token-Killer/releases?per_page=100' `
    -Actual $script:observedUri `
    -Message 'Fetcher used the wrong GitHub endpoint.'
Assert-Equal `
    -Expected 'application/vnd.github+json' `
    -Actual $script:observedHeaders.Accept `
    -Message 'Fetcher omitted the GitHub JSON accept header.'

Assert-Throws `
    -Action {
        Select-PtkLatestPublishedReleaseTag -Release @(
            (New-Release -Tag 'v0.3.0-a' -Draft $false `
                -PublishedAt '2026-08-13T01:38:18Z'),
            (New-Release -Tag 'v0.3.0-b' -Draft $false `
                -PublishedAt '2026-08-13T01:38:18Z'))
    } `
    -Message 'Tied published releases did not fail closed.'
Assert-Throws `
    -Action {
        Select-PtkLatestPublishedReleaseTag -Release @(
            [pscustomobject]@{
                tag_name = 'v0.3.0'
                draft = 'false'
                published_at = '2026-08-14T01:00:00Z'
            })
    } `
    -Message 'A non-Boolean draft flag was accepted.'
Assert-Throws `
    -Action {
        Select-PtkLatestPublishedReleaseTag -Release @(
            (New-Release -Tag 'v0.3.0' -Draft $false -PublishedAt 'not-a-date'))
    } `
    -Message 'An invalid publication timestamp was accepted.'
Assert-Throws `
    -Action {
        Select-PtkLatestPublishedReleaseTag -Release @(
            (New-Release -Tag ' ' -Draft $false -PublishedAt '2026-08-14T01:00:00Z'))
    } `
    -Message 'A published release without a tag was accepted.'
Assert-Throws `
    -Action {
        Select-PtkLatestPublishedReleaseTag -Release @(
            (New-Release -Tag 'v0.4.0' -Draft $true -PublishedAt $null),
            (New-Release -Tag 'v0.3.0' -Draft $false -PublishedAt $null))
    } `
    -Message 'An API response without a published release was accepted.'

$installer = [IO.File]::ReadAllText($installerPath)
if ($installer.Contains(
        '/repos/$repository/releases/latest',
        [StringComparison]::Ordinal)) {
    throw 'Unversioned install still uses GitHub latest-stable selection.'
}
if (-not $installer.Contains('/releases?per_page=100', [StringComparison]::Ordinal)) {
    throw 'Unversioned install does not enumerate published releases.'
}

$readme = [IO.File]::ReadAllText($readmePath)
if ($readme.Contains('/releases/latest/download', [StringComparison]::Ordinal)) {
    throw 'README bootstrap still uses GitHub latest-stable selection.'
}
if (-not $readme.Contains('/releases?per_page=100', [StringComparison]::Ordinal)) {
    throw 'README bootstrap does not enumerate published releases.'
}
if (-not $readme.Contains('-FromRelease -Version $version', [StringComparison]::Ordinal)) {
    throw 'README bootstrap does not pin installer payload selection to its bundle release.'
}

'RELEASE SELECTION TEST PASSED'
