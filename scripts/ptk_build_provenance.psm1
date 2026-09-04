Set-StrictMode -Version Latest

function New-PtkBuildProvenance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Product,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ProductVersion,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$TargetRid,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$SourceRoot
    )

    $sourceCommit = 'unknown'
    $sourceDirty = $true
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($git) {
        $commitOutput = @(& $git.Source -C $SourceRoot rev-parse HEAD 2>$null)
        $commitExit = $LASTEXITCODE
        if ($commitExit -eq 0 -and $commitOutput.Count -gt 0 -and
            $commitOutput[0] -cmatch '^[0-9a-f]{40}$') {
            $sourceCommit = $commitOutput[0]
        }

        $statusOutput = @(& $git.Source -C $SourceRoot status --porcelain `
                --untracked-files=normal 2>$null)
        $statusExit = $LASTEXITCODE
        if ($statusExit -eq 0) {
            $sourceDirty = $statusOutput.Count -gt 0
        }
    }

    # Both native failures above are handled provenance states, not the result
    # of the caller's build command.
    $global:LASTEXITCODE = 0

    [pscustomobject][ordered]@{
        schema_version = 1
        product = $Product
        product_version = $ProductVersion
        build_identity = [guid]::NewGuid().ToString('N')
        source_commit = $sourceCommit
        source_dirty = $sourceDirty
        build_time_utc = [datetime]::UtcNow.ToString(
            'yyyy-MM-ddTHH:mm:ss.fffZ',
            [Globalization.CultureInfo]::InvariantCulture)
        target_rid = $TargetRid
    }
}

function New-PtkLegacyBuildProvenance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Product,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ProductVersion,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$TargetRid
    )

    [pscustomobject][ordered]@{
        schema_version = 1
        product = $Product
        product_version = $ProductVersion
        build_identity = 'legacy-unavailable'
        source_commit = 'unknown'
        source_dirty = $null
        build_time_utc = $null
        target_rid = $TargetRid
    }
}

function Write-PtkBuildProvenance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][psobject]$Provenance,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Path
    )

    $parent = Split-Path -Parent $Path
    if ($parent) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $Provenance | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

Export-ModuleMember -Function New-PtkBuildProvenance,
    New-PtkLegacyBuildProvenance,
    Write-PtkBuildProvenance
