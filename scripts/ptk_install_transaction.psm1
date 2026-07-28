Set-StrictMode -Version Latest

function Get-PtkInstallPathFingerprint {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return 'missing'
    }

    $root = Get-Item -LiteralPath $Path -Force
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Install transaction refuses a link or reparse point: $Path"
    }

    $records = [Collections.Generic.List[string]]::new()
    if (-not $root.PSIsContainer) {
        $hash = (Get-FileHash -LiteralPath $root.FullName -Algorithm SHA256).Hash
        $mode = if ($IsWindows) {
            'windows'
        }
        else {
            [int][IO.File]::GetUnixFileMode($root.FullName)
        }
        $records.Add("file|$mode|$($root.Length)|$hash")
        return $records -join "`n"
    }

    $rootMode = if ($IsWindows) {
        'windows'
    }
    else {
        [int][IO.File]::GetUnixFileMode($root.FullName)
    }
    $records.Add("directory|$rootMode")
    $children = @(Get-ChildItem -LiteralPath $root.FullName -Force -Recurse |
            Sort-Object {
                [IO.Path]::GetRelativePath($root.FullName, $_.FullName)
            })
    foreach ($child in $children) {
        if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Install transaction refuses a link or reparse point: $($child.FullName)"
        }

        $relative = [IO.Path]::GetRelativePath($root.FullName, $child.FullName)
        $encodedRelative = [Convert]::ToBase64String(
            [Text.Encoding]::UTF8.GetBytes($relative))
        $mode = if ($IsWindows) {
            'windows'
        }
        else {
            [int][IO.File]::GetUnixFileMode($child.FullName)
        }
        if ($child.PSIsContainer) {
            $records.Add("directory|$encodedRelative|$mode")
            continue
        }

        $hash = (Get-FileHash -LiteralPath $child.FullName -Algorithm SHA256).Hash
        $records.Add("file|$encodedRelative|$mode|$($child.Length)|$hash")
    }
    $records -join "`n"
}

function Copy-PtkInstallPath {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    $parent = Split-Path -Parent $Destination
    if ($parent) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

function Remove-PtkInstallPath {
    param([Parameter(Mandatory)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Set-PtkInstallRootAccess {
    param([Parameter(Mandatory)][string]$PayloadRoot)

    if (-not $IsWindows) {
        return
    }
    if (Test-Path -LiteralPath $PayloadRoot -PathType Leaf) {
        throw "$PayloadRoot exists as a file; the payload root must be a directory."
    }

    $directory = [IO.Directory]::CreateDirectory($PayloadRoot)
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $ownerSecurity = [IO.FileSystemAclExtensions]::GetAccessControl(
        $directory,
        [Security.AccessControl.AccessControlSections]::Owner)
    $ownerSid = $ownerSecurity.GetOwner([Security.Principal.SecurityIdentifier])
    if (-not $ownerSid.Equals($userSid)) {
        throw "The PTK payload root must be owned by the current Windows user."
    }

    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $userSid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow))
    [IO.FileSystemAclExtensions]::SetAccessControl($directory, $security)
}

function New-PtkInstallSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PayloadRoot,
        [Parameter(Mandatory)][string[]]$PayloadEntries,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$RegistrationPaths,
        [Parameter(Mandatory)][string]$SnapshotRoot
    )

    if (Test-Path -LiteralPath $SnapshotRoot) {
        throw "Install snapshot path already exists: $SnapshotRoot"
    }
    New-Item -ItemType Directory -Path $SnapshotRoot | Out-Null
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode(
            $SnapshotRoot,
            [IO.UnixFileMode]::UserRead -bor
            [IO.UnixFileMode]::UserWrite -bor
            [IO.UnixFileMode]::UserExecute)
    }

    $targets = [Collections.Generic.List[object]]::new()
    foreach ($entry in $PayloadEntries) {
        if ([string]::IsNullOrWhiteSpace($entry) -or
            [IO.Path]::IsPathRooted($entry) -or
            $entry -ne [IO.Path]::GetFileName($entry)) {
            throw "Invalid installer-owned payload entry: '$entry'"
        }
        $targets.Add([pscustomobject]@{
            Kind = 'payload'
            Path = Join-Path $PayloadRoot $entry
        })
    }
    foreach ($path in $RegistrationPaths) {
        if ([string]::IsNullOrWhiteSpace($path) -or
            -not [IO.Path]::IsPathRooted($path)) {
            throw "Registration snapshot path must be absolute: '$path'"
        }
        $targets.Add([pscustomobject]@{
            Kind = 'registration'
            Path = [IO.Path]::GetFullPath($path)
        })
    }

    $records = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $targets.Count; $index++) {
        $target = $targets[$index]
        $backup = Join-Path $SnapshotRoot ('item-{0:D3}' -f $index)
        $fingerprint = Get-PtkInstallPathFingerprint -Path $target.Path
        if ($fingerprint -ne 'missing') {
            Copy-PtkInstallPath -Source $target.Path -Destination $backup
        }
        $records.Add([pscustomobject]@{
            Kind = $target.Kind
            Path = $target.Path
            Backup = $backup
            Fingerprint = $fingerprint
        })
    }

    $manifest = [ordered]@{
        format = 'ptk.install-snapshot/1'
        records = @($records | ForEach-Object {
                [ordered]@{
                    kind = $_.Kind
                    path = $_.Path
                    backup = [IO.Path]::GetFileName($_.Backup)
                    fingerprint = $_.Fingerprint
                }
            })
    } | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText(
        (Join-Path $SnapshotRoot 'manifest.json'),
        $manifest,
        [Text.UTF8Encoding]::new($false))

    [pscustomobject]@{
        Root = $SnapshotRoot
        Records = $records.ToArray()
    }
}

function Restore-PtkInstallSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Snapshot)

    $failures = [Collections.Generic.List[string]]::new()
    foreach ($record in @($Snapshot.Records)) {
        try {
            Remove-PtkInstallPath -Path $record.Path
            if ($record.Fingerprint -ne 'missing') {
                Copy-PtkInstallPath -Source $record.Backup -Destination $record.Path
            }
        }
        catch {
            $failures.Add("$($record.Kind):$($record.Path): $($_.Exception.Message)")
        }
    }
    if ($failures.Count -gt 0) {
        throw "Install rollback could not restore every path: $($failures -join '; ')"
    }
}

function Assert-PtkInstallSnapshotRestored {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Snapshot)

    $mismatches = [Collections.Generic.List[string]]::new()
    foreach ($record in @($Snapshot.Records)) {
        try {
            $actual = Get-PtkInstallPathFingerprint -Path $record.Path
            if ($actual -cne $record.Fingerprint) {
                $mismatches.Add("$($record.Kind):$($record.Path)")
            }
        }
        catch {
            $mismatches.Add(
                "$($record.Kind):$($record.Path): $($_.Exception.Message)")
        }
    }
    if ($mismatches.Count -gt 0) {
        throw "Install rollback was not byte-identical: $($mismatches -join ', ')"
    }
}

function Remove-PtkInstallSnapshot {
    param([Parameter(Mandatory)][string]$SnapshotRoot)

    if (Test-Path -LiteralPath $SnapshotRoot) {
        Remove-Item -LiteralPath $SnapshotRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $SnapshotRoot) {
        throw "Sensitive install snapshot still exists: $SnapshotRoot"
    }
}

function Install-PtkStagedPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StagingRoot,
        [Parameter(Mandatory)][string]$PayloadRoot,
        [Parameter(Mandatory)][string[]]$PayloadEntries,
        [int]$FaultAfterEntry = 0
    )

    foreach ($entry in $PayloadEntries) {
        if ([string]::IsNullOrWhiteSpace($entry) -or
            [IO.Path]::IsPathRooted($entry) -or
            $entry -ne [IO.Path]::GetFileName($entry)) {
            throw "Invalid installer-owned payload entry: '$entry'"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $StagingRoot $entry))) {
            throw "Staged payload entry is missing: $entry"
        }
    }

    if (Test-Path -LiteralPath $PayloadRoot -PathType Leaf) {
        throw "$PayloadRoot exists as a file; the payload root must be a directory."
    }
    New-Item -ItemType Directory -Path $PayloadRoot -Force | Out-Null

    $activated = 0
    foreach ($entry in $PayloadEntries) {
        $target = Join-Path $PayloadRoot $entry
        Remove-PtkInstallPath -Path $target
        Move-Item -LiteralPath (Join-Path $StagingRoot $entry) -Destination $target
        $activated++
        if ($FaultAfterEntry -eq $activated) {
            throw "Injected activation failure after payload entry $activated."
        }
    }
}

function Invoke-PtkInstallTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StagingRoot,
        [Parameter(Mandatory)][string]$PayloadRoot,
        [Parameter(Mandatory)][string[]]$PayloadEntries,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$RegistrationPaths,
        [Parameter(Mandatory)][string]$SnapshotRoot,
        [Parameter(Mandatory)][scriptblock]$StagedValidation,
        [Parameter(Mandatory)][scriptblock]$InstalledValidation,
        [Parameter(Mandatory)][scriptblock]$RegistrationCutover,
        [scriptblock]$CaptureExternalState,
        [scriptblock]$RestoreExternalState,
        [scriptblock]$AssertExternalStateRestored,
        [int]$ActivationFaultAfterEntry = 0
    )

    $externalHooks = @(
        $CaptureExternalState,
        $RestoreExternalState,
        $AssertExternalStateRestored |
            Where-Object { $null -ne $_ }
    )
    if ($externalHooks.Count -ne 0 -and $externalHooks.Count -ne 3) {
        throw ('CaptureExternalState, RestoreExternalState, and ' +
            'AssertExternalStateRestored must be supplied together.')
    }

    # Package smoke creates roots beneath ~/.ptk. Repair a sandbox-created
    # non-inheriting home ACL before that validation can strand its children.
    Set-PtkInstallRootAccess -PayloadRoot $PayloadRoot

    & $StagedValidation $StagingRoot | Out-Null

    $externalState = if ($CaptureExternalState) {
        & $CaptureExternalState
    }
    else {
        $null
    }

    try {
        $snapshot = New-PtkInstallSnapshot `
            -PayloadRoot $PayloadRoot `
            -PayloadEntries $PayloadEntries `
            -RegistrationPaths $RegistrationPaths `
            -SnapshotRoot $SnapshotRoot
    }
    catch {
        $snapshotFailure = $_
        try {
            Remove-PtkInstallSnapshot -SnapshotRoot $SnapshotRoot
        }
        catch {
            throw [InvalidOperationException]::new(
                "Install snapshot creation failed ('$($snapshotFailure.Exception.Message)') " +
                "and its partial sensitive copy could not be removed from '$SnapshotRoot' " +
                "('$($_.Exception.Message)').",
                $_.Exception)
        }
        throw $snapshotFailure
    }

    try {
        Install-PtkStagedPayload `
            -StagingRoot $StagingRoot `
            -PayloadRoot $PayloadRoot `
            -PayloadEntries $PayloadEntries `
            -FaultAfterEntry $ActivationFaultAfterEntry
        & $InstalledValidation $PayloadRoot | Out-Null
        & $RegistrationCutover | Out-Null
    }
    catch {
        $installFailure = $_
        $rollbackFailures = [Collections.Generic.List[string]]::new()
        try {
            Restore-PtkInstallSnapshot -Snapshot $snapshot
        }
        catch {
            $rollbackFailures.Add("files: $($_.Exception.Message)")
        }
        if ($RestoreExternalState) {
            try {
                & $RestoreExternalState $externalState | Out-Null
            }
            catch {
                $rollbackFailures.Add("external state: $($_.Exception.Message)")
            }
        }
        try {
            Assert-PtkInstallSnapshotRestored -Snapshot $snapshot
        }
        catch {
            $rollbackFailures.Add("file verification: $($_.Exception.Message)")
        }
        if ($AssertExternalStateRestored) {
            try {
                & $AssertExternalStateRestored $externalState
            }
            catch {
                $rollbackFailures.Add(
                    "external-state verification: $($_.Exception.Message)")
            }
        }

        if ($rollbackFailures.Count -gt 0) {
            throw [InvalidOperationException]::new(
                "Install failed ('$($installFailure.Exception.Message)') and rollback " +
                "could not be confirmed. The recovery snapshot was retained at " +
                "'$SnapshotRoot'. Failures: $($rollbackFailures -join '; ')",
                $installFailure.Exception)
        }

        try {
            Remove-PtkInstallSnapshot -SnapshotRoot $SnapshotRoot
        }
        catch {
            throw [InvalidOperationException]::new(
                "Install failed ('$($installFailure.Exception.Message)'); rollback was " +
                "confirmed, but the sensitive recovery snapshot could not be removed " +
                "from '$SnapshotRoot' ('$($_.Exception.Message)').",
                $_.Exception)
        }
        throw $installFailure
    }

    try {
        Remove-PtkInstallSnapshot -SnapshotRoot $SnapshotRoot
    }
    catch {
        throw [InvalidOperationException]::new(
            "Install completed, but the sensitive recovery snapshot could not be " +
            "removed from '$SnapshotRoot' ('$($_.Exception.Message)').",
            $_.Exception)
    }
}

Export-ModuleMember -Function @(
    'Get-PtkInstallPathFingerprint',
    'New-PtkInstallSnapshot',
    'Restore-PtkInstallSnapshot',
    'Assert-PtkInstallSnapshotRestored',
    'Install-PtkStagedPayload',
    'Invoke-PtkInstallTransaction'
)
