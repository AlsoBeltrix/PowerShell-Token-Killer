#!/usr/bin/env pwsh
#Requires -Version 7

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRootForInstaller = Split-Path -Parent $PSScriptRoot
$installerPath = Join-Path $repoRootForInstaller 'scripts' 'install.ps1'
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
                $node.Name -ceq 'Get-PtkVersion'
        },
        $true))
if ($functions.Count -ne 1) {
    throw 'Installer does not define exactly one Get-PtkVersion function.'
}
. ([scriptblock]::Create($functions[0].Extent.Text))

function Assert-Match {
    param(
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Actual,
        [Parameter(Mandatory)][string]$Message
    )
    if ($Actual -notmatch $Pattern) {
        throw "$Message Pattern [$Pattern] did not match [$Actual]."
    }
}

# Get-PtkVersion reads the outer script's $Version and $repoRoot variables
# via dynamic scope; the extracted function needs both present here.
$Version = $null

$tempRepo = Join-Path ([IO.Path]::GetTempPath()) ("ptk-version-fallback-{0}" -f ([guid]::NewGuid()))
New-Item -ItemType Directory -Path $tempRepo -Force | Out-Null
try {
    git init --quiet $tempRepo
    git -C $tempRepo config user.email 'test@example.com'
    git -C $tempRepo config user.name 'Test'
    Set-Content -LiteralPath (Join-Path $tempRepo 'f.txt') -Value 'x'
    git -C $tempRepo add f.txt
    git -C $tempRepo commit --quiet -m 'first'
    git -C $tempRepo tag v0.5.2

    $repoRoot = $tempRepo
    $result = Get-PtkVersion
    Assert-Match `
        -Pattern '^0\.5\.2-dev\.g[0-9a-f]+$' `
        -Actual $result `
        -Message 'Source-install fallback did not track the nearest tag.'

    # A second, later commit past the tag still reports the tag's base
    # version (the -dev suffix, not a bumped number) plus the new sha.
    Set-Content -LiteralPath (Join-Path $tempRepo 'f.txt') -Value 'y'
    git -C $tempRepo add f.txt
    git -C $tempRepo commit --quiet -m 'second'
    $result = Get-PtkVersion
    Assert-Match `
        -Pattern '^0\.5\.2-dev\.g[0-9a-f]+$' `
        -Actual $result `
        -Message 'Fallback did not track the nearest tag past new commits.'

    # No reachable tag at all falls back to 0.0.0 rather than failing.
    $tempRepoNoTags = Join-Path ([IO.Path]::GetTempPath()) ("ptk-version-fallback-notags-{0}" -f ([guid]::NewGuid()))
    New-Item -ItemType Directory -Path $tempRepoNoTags -Force | Out-Null
    try {
        git init --quiet $tempRepoNoTags
        git -C $tempRepoNoTags config user.email 'test@example.com'
        git -C $tempRepoNoTags config user.name 'Test'
        Set-Content -LiteralPath (Join-Path $tempRepoNoTags 'f.txt') -Value 'x'
        git -C $tempRepoNoTags add f.txt
        git -C $tempRepoNoTags commit --quiet -m 'first'

        $repoRoot = $tempRepoNoTags
        $result = Get-PtkVersion
        Assert-Match `
            -Pattern '^0\.0\.0-dev\.g[0-9a-f]+$' `
            -Actual $result `
            -Message 'A tagless repo did not fall back to 0.0.0.'
    }
    finally {
        Remove-Item -LiteralPath $tempRepoNoTags -Recurse -Force -ErrorAction SilentlyContinue
    }

    # An explicit -Version still bypasses the tag lookup entirely.
    $repoRoot = $tempRepo
    $Version = 'v1.2.3'
    $result = Get-PtkVersion
    Assert-Match `
        -Pattern '^1\.2\.3$' `
        -Actual $result `
        -Message 'An explicit -Version was not honored ahead of tag lookup.'
}
finally {
    Remove-Item -LiteralPath $tempRepo -Recurse -Force -ErrorAction SilentlyContinue
}

$installer = [IO.File]::ReadAllText($installerPath)
if ($installer.Contains('"0.2.0-dev.g$sha"', [StringComparison]::Ordinal)) {
    throw 'Installer still hardcodes the 0.2.0 dev-version literal.'
}

'VERSION FALLBACK TEST PASSED'
