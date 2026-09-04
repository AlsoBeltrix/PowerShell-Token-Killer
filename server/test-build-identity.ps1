#!/usr/bin/env pwsh
#Requires -Version 7

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $repoRoot 'scripts' 'install.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'ptk-build-identity-{0}' -f [guid]::NewGuid().ToString('N'))
$version = '9.8.7-build-identity-test'

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Get-LocalRid {
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($architecture -eq 'x64') { $architecture = 'x64' }
    elseif ($architecture -eq 'arm64') { $architecture = 'arm64' }
    else { throw "Unsupported test architecture '$architecture'." }

    if ($IsWindows) { return "win-$architecture" }
    if ($IsMacOS) { return "osx-$architecture" }
    if ($IsLinux) { return "linux-$architecture" }
    throw 'Unsupported test operating system.'
}

function Get-InformationalVersion {
    param([Parameter(Mandatory)][string]$AssemblyPath)

    $context = [Runtime.Loader.AssemblyLoadContext]::new(
        'ptk-build-identity-' + [guid]::NewGuid().ToString('N'),
        $true)
    try {
        $assembly = $context.LoadFromAssemblyPath([IO.Path]::GetFullPath($AssemblyPath))
        return $assembly.GetCustomAttributesData() |
            Where-Object AttributeType -eq ([Reflection.AssemblyInformationalVersionAttribute]) |
            ForEach-Object { [string]$_.ConstructorArguments[0].Value } |
            Select-Object -First 1
    }
    finally {
        $context.Unload()
    }
}

$rid = Get-LocalRid
$layouts = @(
    (Join-Path $tempRoot 'one'),
    (Join-Path $tempRoot 'two'))

try {
    Import-Module (Join-Path $repoRoot 'scripts' 'ptk_build_provenance.psm1') -Force
    $dirtyMarker = Join-Path $repoRoot (
        '.ptk-build-identity-dirty-' + [guid]::NewGuid().ToString('N'))
    try {
        Set-Content -LiteralPath $dirtyMarker -Value 'dirty-provenance-probe'
        $dirtyRecord = New-PtkBuildProvenance `
            -Product 'ptk-test' `
            -ProductVersion $version `
            -TargetRid $rid `
            -SourceRoot $repoRoot
        Assert-True ($dirtyRecord.source_dirty -eq $true) `
            'An untracked source change was not recorded as dirty provenance.'
    }
    finally {
        Remove-Item -LiteralPath $dirtyMarker -Force -ErrorAction SilentlyContinue
    }
    $sourceStatus = @(& git -C $repoRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not establish the source tree state for the provenance test.'
    }
    $expectedSourceDirty = $sourceStatus.Count -gt 0

    $records = foreach ($layout in $layouts) {
        & $installer -LayoutOnly -OutputDir $layout -Rid $rid -Version $version |
            Out-Host

        $provenancePath = Join-Path $layout 'BUILD-PROVENANCE.json'
        Assert-True (Test-Path -LiteralPath $provenancePath) `
            "Layout '$layout' has no BUILD-PROVENANCE.json."
        $record = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json

        Assert-True ($record.schema_version -eq 1) 'Unexpected provenance schema version.'
        Assert-True ($record.product -ceq 'ptk') 'Unexpected provenance product.'
        Assert-True ($record.product_version -ceq $version) 'Provenance version mismatch.'
        Assert-True ($record.target_rid -ceq $rid) 'Provenance RID mismatch.'
        Assert-True ($record.build_identity -cmatch '^[0-9a-f]{32}$') `
            "Invalid build identity '$($record.build_identity)'."
        Assert-True ($record.source_commit -cmatch '^(?:[0-9a-f]{40}|unknown)$') `
            "Invalid source commit '$($record.source_commit)'."
        Assert-True ($record.source_dirty -is [bool]) 'source_dirty is not Boolean.'
        Assert-True ($record.source_dirty -eq $expectedSourceDirty) `
            'PTK provenance does not match the source tree dirty state.'

        $builtAt = [datetimeoffset]::MinValue
        Assert-True ([datetimeoffset]::TryParse(
                [string]$record.build_time_utc,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::AssumeUniversal,
                [ref]$builtAt)) `
            "Invalid build_time_utc '$($record.build_time_utc)'."
        Assert-True ($builtAt.Offset -eq [timespan]::Zero) 'build_time_utc is not UTC.'

        $serverName = if ($IsWindows) { 'PtkMcpServer.dll' } else { 'PtkMcpServer.dll' }
        $informational = Get-InformationalVersion (
            Join-Path $layout 'bin' $serverName)
        $revision = if ($record.source_commit -ceq 'unknown') {
            'unknown'
        }
        else {
            $record.source_commit.Substring(0, 7)
        }
        $expected = "$version+$revision.build.$($record.build_identity)"
        Assert-True ($informational -ceq $expected) `
            "Assembly identity mismatch: expected '$expected', got '$informational'."

        $record
    }

    Assert-True ($records[0].build_identity -cne $records[1].build_identity) `
        'Two builds of the same version and commit reused one build identity.'
    Assert-True ($records[0].source_commit -ceq $records[1].source_commit) `
        'Two builds from one checkout disagree on source commit.'

    $siemRecords = foreach ($name in 'siem-one', 'siem-two') {
        $layout = Join-Path $tempRoot $name
        & (Join-Path $repoRoot 'siem' 'build-package.ps1') `
            -OutputDir $layout `
            -Rid $rid `
            -Version $version | Out-Host
        $record = Get-Content -LiteralPath (
            Join-Path $layout 'BUILD-PROVENANCE.json') -Raw | ConvertFrom-Json
        Assert-True ($record.schema_version -eq 1) `
            'Unexpected SIEM provenance schema version.'
        Assert-True ($record.product -ceq 'ptk-siem-receiver') `
            'Unexpected SIEM provenance product.'
        Assert-True ($record.product_version -ceq $version) `
            'SIEM provenance version mismatch.'
        Assert-True ($record.target_rid -ceq $rid) 'SIEM provenance RID mismatch.'
        Assert-True ($record.build_identity -cmatch '^[0-9a-f]{32}$') `
            "Invalid SIEM build identity '$($record.build_identity)'."
        Assert-True ($record.source_commit -ceq $records[0].source_commit) `
            'PTK and SIEM builds from one checkout disagree on source commit.'
        Assert-True ($record.source_dirty -eq $expectedSourceDirty) `
            'SIEM provenance does not match the source tree dirty state.'
        $informational = Get-InformationalVersion (
            Join-Path $layout 'PtkSiemReceiver.dll')
        $revision = if ($record.source_commit -ceq 'unknown') {
            'unknown'
        }
        else {
            $record.source_commit.Substring(0, 7)
        }
        $expected = "$version+$revision.build.$($record.build_identity)"
        Assert-True ($informational -ceq $expected) `
            "SIEM assembly identity mismatch: expected '$expected', got '$informational'."
        $record
    }
    Assert-True ($siemRecords[0].build_identity -cne $siemRecords[1].build_identity) `
        'Two SIEM builds of the same version and commit reused one build identity.'
    $allIdentities = @($records.build_identity) + @($siemRecords.build_identity)
    Assert-True (@($allIdentities | Sort-Object -Unique).Count -eq 4) `
        'Independent PTK and SIEM builds did not receive unique identities.'

    $ci = Get-Content -LiteralPath (
        Join-Path $repoRoot '.github/workflows/ci.yml') -Raw
    Assert-True ($ci -match 'server/test-build-identity\.ps1') `
        'CI does not run the unique-build-identity guard.'
    $release = Get-Content -LiteralPath (
        Join-Path $repoRoot '.github/workflows/release.yml') -Raw
    Assert-True ($release -match (
            'direct-product-proof\.ps1[\s\S]{0,300}?-RequireCleanSource')) `
        'Release workflow does not require clean PTK provenance.'
    Assert-True ($release -match (
            'verify-package\.ps1[\s\S]{0,400}?-RequireCleanSource')) `
        'Release workflow does not require clean SIEM provenance.'

    'BUILD IDENTITY TEST PASSED'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
