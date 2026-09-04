#Requires -Version 7
<#
.SYNOPSIS
Evaluates SIEM operator readiness from published PTK and mini-SIEM artifacts.

.DESCRIPTION
This is a release gate, not a backend smoke test. It extracts the supplied
archives into a fresh isolated home, verifies their release identity, and
checks an observation record produced by an artifact-only acceptance run.

The observation record is evidence from the public operator workflow. It must
describe setup, configured destinations, activities returned by the SIEM,
dashboard investigation, restart durability, a real external-SIEM run, and an
explicit multiple-destination run. Missing facts fail by their operator-facing
capability name. Checkout-built product binaries are never accepted as proof.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PtkArchive,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$SiemArchive,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ObservationFile,

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
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$PtkArchiveSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$SiemArchiveSha256,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedClientName,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedAgentName,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedModelProvider,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedModelName,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedWorkingDirectory,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedCommand,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedResponse,

    [string]$ResultPath,

    [switch]$KeepWorkRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NestedValue {
    param(
        [AllowNull()][object]$InputObject,
        [Parameter(Mandatory)][string]$Path
    )

    $value = $InputObject
    foreach ($segment in $Path.Split('.')) {
        if ($null -eq $value) {
            return $null
        }

        $property = $value.PSObject.Properties[$segment]
        if ($null -eq $property) {
            return $null
        }

        $value = $property.Value
    }

    return $value
}

function Expand-PublishedArchive {
    param(
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$Destination
    )

    [void](New-Item -ItemType Directory -Path $Destination)
    if ($Archive.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        Expand-Archive -LiteralPath $Archive -DestinationPath $Destination
        return
    }

    if ($Archive.EndsWith('.tar.gz', [StringComparison]::OrdinalIgnoreCase) -or
        $Archive.EndsWith('.tgz', [StringComparison]::OrdinalIgnoreCase)) {
        & tar -xzf $Archive -C $Destination
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to extract published archive '$Archive'."
        }
        return
    }

    throw "Unsupported published archive format: '$Archive'."
}

function Find-PackageRoot {
    param(
        [Parameter(Mandatory)][string]$ExtractedRoot,
        [Parameter(Mandatory)][string]$RequiredRelativePath
    )

    if (Test-Path -LiteralPath (Join-Path $ExtractedRoot $RequiredRelativePath) -PathType Leaf) {
        return $ExtractedRoot
    }

    $matches = @(Get-ChildItem -LiteralPath $ExtractedRoot -Directory | Where-Object {
            Test-Path -LiteralPath (Join-Path $_.FullName $RequiredRelativePath) -PathType Leaf
        })
    if ($matches.Count -ne 1) {
        throw "Published archive did not contain one package root with '$RequiredRelativePath'."
    }

    return $matches[0].FullName
}

function Get-Utf8Sha256 {
    param([Parameter(Mandatory)][string]$Text)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Test-BuildProvenance {
    param(
        [Parameter(Mandatory)][psobject]$Record,
        [Parameter(Mandatory)][string]$Product,
        [Parameter(Mandatory)][string]$ProductVersion,
        [Parameter(Mandatory)][string]$TargetRid,
        [Parameter(Mandatory)][string]$SourceCommit
    )

    $builtAt = [datetimeoffset]::MinValue
    return $Record.schema_version -eq 1 -and
        $Record.product -ceq $Product -and
        $Record.product_version -ceq $ProductVersion -and
        $Record.target_rid -ceq $TargetRid -and
        $Record.build_identity -cmatch '^[0-9a-f]{32}$' -and
        ([string]$Record.source_commit).StartsWith(
            $SourceCommit, [StringComparison]::Ordinal) -and
        $Record.source_dirty -eq $false -and
        [datetimeoffset]::TryParse(
            [string]$Record.build_time_utc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$builtAt) -and
        $builtAt.Offset -eq [timespan]::Zero
}

$results = [Collections.Generic.List[object]]::new()
function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Passed,
        [Parameter(Mandatory)][string]$Detail
    )

    $results.Add([pscustomobject]@{
            name = $Name
            passed = $Passed
            detail = $Detail
        })
}

function Test-Requirement {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Condition,
        [Parameter(Mandatory)][string]$Failure,
        [string]$Success = 'observed'
    )

    try {
        $passed = [bool](& $Condition)
    }
    catch {
        Add-Result -Name $Name -Passed $false -Detail "$Failure ($($_.Exception.Message))"
        return
    }

    Add-Result -Name $Name -Passed $passed -Detail $(if ($passed) { $Success } else { $Failure })
}

$normalizedVersion = $Version.TrimStart('v', 'V')
$normalizedSourceCommit = $SourceCommit.ToLowerInvariant()
$expectedStampedCommit = $normalizedSourceCommit.Substring(0, 7)
$workRoot = Join-Path ([IO.Path]::GetTempPath()) "ptk-siem-operator-acceptance-$([Guid]::NewGuid().ToString('N'))"

try {
    [void](New-Item -ItemType Directory -Path $workRoot)
    $ptkExtractRoot = Join-Path $workRoot 'fresh-home/.ptk'
    $siemExtractRoot = Join-Path $workRoot 'mini-siem'
    Expand-PublishedArchive -Archive ([IO.Path]::GetFullPath($PtkArchive)) -Destination $ptkExtractRoot
    Expand-PublishedArchive -Archive ([IO.Path]::GetFullPath($SiemArchive)) -Destination $siemExtractRoot

    $ptkRoot = Find-PackageRoot -ExtractedRoot $ptkExtractRoot -RequiredRelativePath 'bin/PtkMcpServer.dll'
    $siemRoot = Find-PackageRoot -ExtractedRoot $siemExtractRoot -RequiredRelativePath 'PtkSiemReceiver.dll'
    $ptkProvenance = Get-Content -LiteralPath (
        Join-Path $ptkRoot 'BUILD-PROVENANCE.json') -Raw | ConvertFrom-Json
    $siemProvenance = Get-Content -LiteralPath (
        Join-Path $siemRoot 'BUILD-PROVENANCE.json') -Raw | ConvertFrom-Json
    $expectedPtkProductVersion = (
        "$normalizedVersion+$expectedStampedCommit.build.$($ptkProvenance.build_identity)")
    $expectedSiemProductVersion = (
        "$normalizedVersion+$expectedStampedCommit.build.$($siemProvenance.build_identity)")
    $observation = Get-Content -LiteralPath $ObservationFile -Raw | ConvertFrom-Json -Depth 100

    $actualPtkHash = (Get-FileHash -LiteralPath $PtkArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualSiemHash = (Get-FileHash -LiteralPath $SiemArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    Test-Requirement 'artifact.ptk_archive_sha256' { $actualPtkHash -ceq $PtkArchiveSha256.ToLowerInvariant() } `
        'PTK archive does not match the release-record SHA-256.' $actualPtkHash
    Test-Requirement 'artifact.siem_archive_sha256' { $actualSiemHash -ceq $SiemArchiveSha256.ToLowerInvariant() } `
        'Mini-SIEM archive does not match the release-record SHA-256.' $actualSiemHash
    Test-Requirement 'artifact.fresh_isolated_home' {
        $ptkRoot.StartsWith($workRoot, [StringComparison]::Ordinal) -and
        $siemRoot.StartsWith($workRoot, [StringComparison]::Ordinal)
    } 'Product package roots were not extracted beneath the fresh acceptance root.' $workRoot

    Test-Requirement 'artifact.ptk_build_provenance' {
        Test-BuildProvenance `
            -Record $ptkProvenance `
            -Product 'ptk' `
            -ProductVersion $normalizedVersion `
            -TargetRid $Rid `
            -SourceCommit $normalizedSourceCommit
    } 'PTK build provenance is absent, dirty, malformed, or source-mismatched.' `
        $expectedPtkProductVersion
    Test-Requirement 'artifact.siem_build_provenance' {
        Test-BuildProvenance `
            -Record $siemProvenance `
            -Product 'ptk-siem-receiver' `
            -ProductVersion $normalizedVersion `
            -TargetRid $Rid `
            -SourceCommit $normalizedSourceCommit
    } 'Mini-SIEM build provenance is absent, dirty, malformed, or source-mismatched.' `
        $expectedSiemProductVersion

    $ptkVersion = [IO.File]::ReadAllText((Join-Path $ptkRoot 'VERSION')).Trim()
    $ptkProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
        (Join-Path $ptkRoot 'bin/PtkMcpServer.dll')).ProductVersion
    Test-Requirement 'artifact.ptk_release_identity' {
        $ptkVersion -ceq $normalizedVersion -and
        $ptkProductVersion -ceq $expectedPtkProductVersion
    } "PTK package is not $expectedPtkProductVersion." "$ptkVersion / $ptkProductVersion"

    & (Join-Path $PSScriptRoot 'verify-package.ps1') `
        -PackageDir $siemRoot -Rid $Rid -Version $normalizedVersion `
        -SourceCommit $normalizedSourceCommit
    Test-Requirement 'artifact.siem_release_identity' { $true } `
        'Mini-SIEM package verification failed.' $expectedSiemProductVersion

    $bundledReceiver = @(Get-ChildItem -LiteralPath $ptkRoot -Recurse -File -Filter 'PtkSiemReceiver*')
    Test-Requirement 'destination.mini_siem_separately_deployed' { $bundledReceiver.Count -eq 0 } `
        'PTK package silently bundles the mini-SIEM destination.' 'separate artifact'

    Test-Requirement 'observation.release_identity' {
        (Get-NestedValue $observation 'release').TrimStart('v', 'V') -ceq $normalizedVersion -and
        (Get-NestedValue $observation 'source_commit').ToLowerInvariant().StartsWith($normalizedSourceCommit)
    } 'Observation record is not bound to the supplied release and source commit.'
    Test-Requirement 'observation.archive_identity' {
        (Get-NestedValue $observation 'producer_archive_sha256') -ceq $actualPtkHash -and
        (Get-NestedValue $observation 'receiver_archive_sha256') -ceq $actualSiemHash
    } 'Observation record is not bound to both supplied archives.'

    Test-Requirement 'setup.public_commands_only' {
        (Get-NestedValue $observation 'setup.public_commands_only') -eq $true -and
        (Get-NestedValue $observation 'setup.source_tree_product_binaries_used') -eq $false
    } 'No proof that a fresh operator used only public setup commands and published product binaries.'
    Test-Requirement 'setup.no_undocumented_forwarder' {
        (Get-NestedValue $observation 'setup.undocumented_proxy_used') -eq $false
    } 'The producer-to-SIEM path required an undocumented proxy or forwarder.'

    $destinations = @(Get-NestedValue $observation 'destinations')
    Test-Requirement 'destination.default_one_explicit' {
        $destinations.Count -eq 1 -and
        $destinations[0].type -ceq 'mini_siem' -and
        $destinations[0].explicitly_selected -eq $true
    } 'The default run did not prove exactly one explicitly selected mini-SIEM destination.'
    Test-Requirement 'destination.no_hidden_contact' {
        (Get-NestedValue $observation 'network.unconfigured_destination_requests') -eq 0
    } 'The run did not prove zero requests to unconfigured SIEM destinations.'

    $activities = @(Get-NestedValue $observation 'activities')
    $activity = @($activities | Where-Object {
            (Get-NestedValue $_ 'command.exact_text') -ceq $ExpectedCommand
        } | Select-Object -First 1)
    $activity = if ($activity.Count -eq 1) { $activity[0] } else { $null }

    Test-Requirement 'activity.correlated_row' { $null -ne $activity } `
        'No operator-facing activity correlates the admitted call and terminal result.'
    Test-Requirement 'activity.client_identity' {
        (Get-NestedValue $activity 'client.name') -ceq $ExpectedClientName -and
        -not [string]::IsNullOrWhiteSpace((Get-NestedValue $activity 'client.attribution_strength'))
    } 'The activity does not show the expected client and its trust strength.'
    Test-Requirement 'activity.agent_identity' {
        (Get-NestedValue $activity 'agent.name') -ceq $ExpectedAgentName
    } 'The activity does not show which agent made the call.'
    Test-Requirement 'activity.model_identity' {
        (Get-NestedValue $activity 'model.provider') -ceq $ExpectedModelProvider -and
        (Get-NestedValue $activity 'model.name') -ceq $ExpectedModelName
    } 'The activity does not show which model made the call.'
    Test-Requirement 'activity.attribution_provenance' {
        (Get-NestedValue $activity 'attribution.source') -in @('client', 'operator_configuration', 'transport') -and
        (Get-NestedValue $activity 'attribution.strength') -in @('client_asserted', 'authenticated', 'transport_only')
    } 'Agent/model attribution source and trust strength are absent or invalid.'
    Test-Requirement 'activity.execution_context' {
        (Get-NestedValue $activity 'context.effective_cwd') -ceq $ExpectedWorkingDirectory
    } 'The activity does not expose the effective working directory.'

    $expectedCommandHash = Get-Utf8Sha256 $ExpectedCommand
    Test-Requirement 'activity.exact_command_evidence' {
        (Get-NestedValue $activity 'command.exact_text') -ceq $ExpectedCommand -and
        (Get-NestedValue $activity 'command.sha256') -ceq $expectedCommandHash -and
        (Get-NestedValue $activity 'command.availability') -ceq 'destination'
    } 'The SIEM cannot return the exact submitted command with a verified digest.'

    $responseText = Get-NestedValue $activity 'response.exact_text'
    Test-Requirement 'activity.complete_response_evidence' {
        $responseText -is [string] -and
        $responseText.Contains($ExpectedResponse, [StringComparison]::Ordinal) -and
        (Get-NestedValue $activity 'response.sha256') -ceq (Get-Utf8Sha256 $responseText) -and
        (Get-NestedValue $activity 'response.availability') -ceq 'destination'
    } 'The SIEM cannot return complete captured response/output/error evidence with a verified digest.'
    Test-Requirement 'activity.terminal_outcome' {
        (Get-NestedValue $activity 'state') -in @('completed', 'failed', 'canceled', 'timed_out', 'outcome_unknown', 'not_started') -and
        $null -ne (Get-NestedValue $activity 'outcome.duration_ms')
    } 'The activity does not expose a terminal result and duration.'
    Test-Requirement 'activity.chain_status' {
        (Get-NestedValue $activity 'chain.status') -ceq 'intact'
    } 'The activity does not expose its audit-chain status.'

    $visibleFields = @(Get-NestedValue $observation 'dashboard.visible_fields')
    $requiredVisibleFields = @('client', 'agent', 'model', 'working_directory', 'command', 'response', 'outcome', 'chain_status')
    Test-Requirement 'dashboard.required_activity_fields' {
        @($requiredVisibleFields | Where-Object { $_ -notin $visibleFields }).Count -eq 0
    } 'The dashboard does not visibly expose every required activity field.'
    Test-Requirement 'dashboard.detail_navigation' {
        (Get-NestedValue $observation 'dashboard.activity_rows_link_to_detail') -eq $true -and
        (Get-NestedValue $observation 'dashboard.detail_includes_raw_events') -eq $true
    } 'Activity rows do not open a detail view containing correlated raw events and evidence.'
    Test-Requirement 'dashboard.system_events_separate' {
        (Get-NestedValue $observation 'dashboard.system_events_separate') -eq $true
    } 'Lifecycle/system events are still interleaved as if each were a user activity.'

    Test-Requirement 'investigation.alerts' {
        (Get-NestedValue $observation 'investigation.alerts_accessible') -eq $true
    } 'The operator workflow did not demonstrate alert investigation.'
    Test-Requirement 'investigation.gaps_and_quarantine' {
        (Get-NestedValue $observation 'investigation.gaps_accessible') -eq $true -and
        (Get-NestedValue $observation 'investigation.quarantine_accessible') -eq $true
    } 'The operator workflow did not demonstrate gap and quarantine investigation.'
    Test-Requirement 'investigation.custody' {
        (Get-NestedValue $observation 'custody.healthy') -eq $true -and
        (Get-NestedValue $observation 'custody.visible_to_operator') -eq $true
    } 'Healthy custody was not both proved and visible to the operator.'
    Test-Requirement 'restart.durable_activity' {
        (Get-NestedValue $observation 'restart.ptk_restarted') -eq $true -and
        (Get-NestedValue $observation 'restart.siem_restarted') -eq $true -and
        (Get-NestedValue $observation 'restart.activity_survived') -eq $true
    } 'The same activity was not recovered after restarting both PTK and the mini-SIEM.'

    Test-Requirement 'external_siem.real_product_acceptance' {
        -not [string]::IsNullOrWhiteSpace((Get-NestedValue $observation 'external_siem.product')) -and
        -not [string]::IsNullOrWhiteSpace((Get-NestedValue $observation 'external_siem.version')) -and
        (Get-NestedValue $observation 'external_siem.authorized_access') -eq $true -and
        (Get-NestedValue $observation 'external_siem.full_evidence_digest_verified') -eq $true
    } 'No version-pinned real external SIEM accepted and returned the complete forensic record.'
    Test-Requirement 'multiple_destinations.explicit_and_independent' {
        (Get-NestedValue $observation 'multiple_destinations.explicitly_opted_in') -eq $true -and
        (Get-NestedValue $observation 'multiple_destinations.destination_count') -eq 2 -and
        (Get-NestedValue $observation 'multiple_destinations.full_fidelity_equal') -eq $true -and
        (Get-NestedValue $observation 'multiple_destinations.independent_failure_observed') -eq $true -and
        (Get-NestedValue $observation 'multiple_destinations.replay_closed_only_failed_backlog') -eq $true -and
        (Get-NestedValue $observation 'multiple_destinations.unconfigured_destination_requests') -eq 0
    } 'Explicit two-destination full-fidelity delivery and independent failure/replay accounting were not proved.'
}
finally {
    if (-not $KeepWorkRoot -and (Test-Path -LiteralPath $workRoot)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}

$summary = [pscustomobject]@{
    schema = 'ptk.siem.operator-readiness/1'
    evaluated_utc = [DateTimeOffset]::UtcNow.ToString('O')
    release = $normalizedVersion
    source_commit = $normalizedSourceCommit
    passed = @($results | Where-Object passed).Count
    failed = @($results | Where-Object { -not $_.passed }).Count
    results = @($results)
}

if (-not [string]::IsNullOrWhiteSpace($ResultPath)) {
    $resultFullPath = [IO.Path]::GetFullPath($ResultPath)
    $resultDirectory = Split-Path -Parent $resultFullPath
    if (-not [string]::IsNullOrWhiteSpace($resultDirectory)) {
        [void](New-Item -ItemType Directory -Path $resultDirectory -Force)
    }
    [IO.File]::WriteAllText($resultFullPath, ($summary | ConvertTo-Json -Depth 20))
}

foreach ($result in $results) {
    $label = if ($result.passed) { 'PASS' } else { 'FAIL' }
    Write-Information "[$label] $($result.name): $($result.detail)" -InformationAction Continue
}

if ($summary.failed -gt 0) {
    throw "SIEM operator readiness failed: $($summary.failed) of $($results.Count) requirements failed."
}

Write-Information "SIEM operator readiness passed: $($results.Count) requirements." -InformationAction Continue
