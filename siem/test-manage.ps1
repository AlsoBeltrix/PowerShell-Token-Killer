#!/usr/bin/env pwsh
#Requires -Version 7

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$manager = Join-Path $PSScriptRoot 'manage.ps1'
$operatorGuide = Join-Path $PSScriptRoot 'PtkSiemReceiver/README.md'
$root = Join-Path ([IO.Path]::GetTempPath()) (
    'ptk-siem-manage-' + [guid]::NewGuid().ToString('N'))

$platformServiceKind = if ($IsWindows) {
    'windows'
} elseif ($IsMacOS) {
    'launchd'
} else {
    'systemd'
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-WindowsPrivatePath([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    $security = [IO.FileSystemAclExtensions]::GetAccessControl($item)
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new(
        $security.GetSecurityDescriptorBinaryForm(), 0)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $valid = $null -ne $descriptor.Owner -and $sid.Equals($descriptor.Owner) -and
        $descriptor.ControlFlags.HasFlag(
            [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected) -and
        $null -ne $descriptor.DiscretionaryAcl -and $descriptor.DiscretionaryAcl.Count -eq 1
    if ($valid) {
        $ace = $descriptor.DiscretionaryAcl[0]
        $valid = $ace -is [Security.AccessControl.CommonAce] -and
            $ace.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed -and
            -not $ace.IsCallback -and $ace.AceFlags -eq [Security.AccessControl.AceFlags]::None -and
            $sid.Equals($ace.SecurityIdentifier) -and
            $ace.AccessMask -eq [int][Security.AccessControl.FileSystemRights]::FullControl
    }
    Assert-True $valid "Windows private ACL contract failed: $Path"
}

function New-TestRelease {
    param([string]$Version)
    $case = Join-Path $root $Version
    $package = Join-Path $case 'package'
    [void](New-Item -ItemType Directory -Path $package -Force)
    Set-Content -LiteralPath (Join-Path $package 'PtkSiemReceiver') `
        -Value "receiver-$Version" -NoNewline
    Set-Content -LiteralPath (Join-Path $package 'VERSION') -Value $Version -NoNewline
    Set-Content -LiteralPath (Join-Path $package 'manage.ps1') -Value 'packaged manager' -NoNewline
    $archive = Join-Path $case "ptk-siem-receiver-$Version-linux-x64.tar.gz"
    Set-Content -LiteralPath $archive -Value "archive-$Version" -NoNewline
    $checksum = Join-Path $case 'SHA256SUMS'
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $checksum -Value "$hash  $([IO.Path]::GetFileName($archive))"
    return [pscustomobject]@{
        Package = $package
        Archive = $archive
        Checksums = $checksum
        Version = $Version
    }
}

function Get-InstallParameters {
    param([object]$Release, [string]$Name, [string]$ServiceKind)
    $case = Join-Path $root $Name
    return @{
        Action = 'Install'
        PackageDir = $Release.Package
        ArchivePath = $Release.Archive
        ChecksumFile = $Release.Checksums
        ExpectedVersion = $Release.Version
        ExpectedRid = 'linux-x64'
        InstallRoot = (Join-Path $case 'program')
        ConfigurationPath = (Join-Path $case 'config/receiver.json')
        ManifestPath = (Join-Path $case 'deployment/deployment.json')
        ServiceDefinitionPath = (Join-Path $case "service/$Name.definition")
        ServiceKind = $ServiceKind
        ServiceName = $Name
        ServiceIdentity = if ($IsWindows) {
            [Security.Principal.WindowsIdentity]::GetCurrent().Name
        } else {
            [Environment]::UserName
        }
        DataDirectory = (Join-Path $case 'data')
        WitnessDirectory = (Join-Path $case 'witness')
        IngestBindAddress = '127.0.0.1'
        IngestPort = 19418
        OperatorPort = 19419
        GenerateCredentials = $true
        GenerateSelfSignedTls = $true
        TlsDnsName = '127.0.0.1'
        AllowUnboundedRetention = $true
    }
}

try {
    [void](New-Item -ItemType Directory -Path $root)
    $guideText = Get-Content -LiteralPath $operatorGuide -Raw
    Assert-True ($guideText -notmatch '(?m)&[^\r\n]+@\{') `
        'Operator guide contains an anonymous hashtable where PowerShell splatting is required.'
    $managerText = Get-Content -LiteralPath $manager -Raw
    Assert-True ($managerText -match 'Set-ServiceProgramAccess \$programRoot \$ServiceIdentity') `
        'Install does not grant the service read/execute access to the program root.'
    Assert-True ($managerText -match 'Resolve-WindowsAclIdentity \$ServiceIdentity') `
        'Service-owned paths do not resolve Windows built-in identities for ACLs.'
    Assert-True ($managerText -match 'Resolve-WindowsAclIdentity \$Identity') `
        'Program access does not resolve Windows built-in identities for ACLs.'
    Assert-True ($managerText -notmatch '\$serviceOwnedPaths\s*=\s*@\(\$programRoot') `
        'Install lets the service identity own and rewrite the privileged manager.'
    $tokens = $null
    $parseErrors = $null
    $managerAst = [Management.Automation.Language.Parser]::ParseFile(
        $manager,
        [ref]$tokens,
        [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) 'Manager did not parse for ACL identity tests.'
    $resolverAst = $managerAst.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq 'Resolve-WindowsAclIdentity'
        }, $true)
    Assert-True ($null -ne $resolverAst) 'Windows ACL identity resolver is missing.'
    . ([scriptblock]::Create($resolverAst.Extent.Text))
    Assert-True ((Resolve-WindowsAclIdentity 'LocalSystem') -ceq '*S-1-5-18') `
        'LocalSystem ACL identity did not resolve to its stable SID.'
    Assert-True ((Resolve-WindowsAclIdentity 'NT AUTHORITY\LocalService') -ceq '*S-1-5-19') `
        'LocalService ACL identity did not resolve to its stable SID.'
    Assert-True ((Resolve-WindowsAclIdentity 'NT AUTHORITY\NetworkService') -ceq '*S-1-5-20') `
        'NetworkService ACL identity did not resolve to its stable SID.'
    Assert-True ((Resolve-WindowsAclIdentity 'PTK\receiver') -ceq 'PTK\receiver') `
        'Named service ACL identity was changed.'

    $release1 = New-TestRelease '9.8.1-test'
    $parameters = Get-InstallParameters $release1 'manifest-safety' $platformServiceKind

    $wrongChecksums = Join-Path $root 'wrong-SHA256SUMS'
    Set-Content -LiteralPath $wrongChecksums -Value (
        ('0' * 64) + '  ' + [IO.Path]::GetFileName($release1.Archive))
    $rejectedChecksum = $false
    $wrongParameters = @{} + $parameters
    $wrongParameters.ChecksumFile = $wrongChecksums
    try {
        & $manager @wrongParameters
    } catch {
        $rejectedChecksum = $_.Exception.Message -match 'checksum mismatch'
    }
    Assert-True $rejectedChecksum 'Install accepted an archive with the wrong checksum.'
    Assert-True (-not (Test-Path -LiteralPath $parameters.InstallRoot)) `
        'Checksum refusal changed the install root.'

    $unsafeManifestParameters = @{} + $parameters
    $unsafeManifestParameters.InstallRoot = Join-Path $root 'unsafe-manifest/program'
    $unsafeManifestParameters.ConfigurationPath = Join-Path $root 'unsafe-manifest/config/receiver.json'
    $unsafeManifestParameters.ManifestPath = Join-Path $root 'unsafe-manifest/config/deployment.json'
    $unsafeManifestParameters.ServiceDefinitionPath = Join-Path $root 'unsafe-manifest/service/receiver.service'
    $unsafeManifestParameters.DataDirectory = Join-Path $root 'unsafe-manifest/data'
    $unsafeManifestParameters.WitnessDirectory = Join-Path $root 'unsafe-manifest/witness'
    $unsafeManifestRejected = $false
    try {
        & $manager @unsafeManifestParameters
    } catch {
        $unsafeManifestRejected = $_.Exception.Message -match 'ManifestPath must be outside'
    }
    Assert-True $unsafeManifestRejected `
        'Install allowed receiver-owned configuration to control the uninstall manifest.'
    Assert-True (-not (Test-Path -LiteralPath $unsafeManifestParameters.InstallRoot)) `
        'Unsafe manifest refusal changed the install root.'

    $unsafeServiceParameters = Get-InstallParameters $release1 'unsafe-service' $platformServiceKind
    $unsafeServiceParameters.ServiceDefinitionPath = Join-Path `
        (Split-Path -Parent $unsafeServiceParameters.ConfigurationPath) `
        'receiver.service'
    $unsafeServiceRejected = $false
    try {
        & $manager @unsafeServiceParameters
    } catch {
        $unsafeServiceRejected = $_.Exception.Message -match 'ServiceDefinitionPath must be outside'
    }
    Assert-True $unsafeServiceRejected `
        'Install allowed a receiver-writable privileged service definition.'
    Assert-True (-not (Test-Path -LiteralPath $unsafeServiceParameters.InstallRoot)) `
        'Unsafe service-definition refusal changed the install root.'

    $overlapParameters = Get-InstallParameters $release1 'overlap' $platformServiceKind
    $overlapParameters.DataDirectory = Join-Path $overlapParameters.InstallRoot 'data'
    $overlapRejected = $false
    $overlapFailure = 'Install returned success.'
    try {
        & $manager @overlapParameters
    } catch {
        $overlapFailure = $_.Exception.Message
        $overlapRejected = $_.Exception.Message -match 'overlaps deployment'
    }
    Assert-True $overlapRejected `
        "Install allowed a destructive data/program overlap. Data='$($overlapParameters.DataDirectory)' Program='$($overlapParameters.InstallRoot)' Failure='$overlapFailure'"
    Assert-True (-not (Test-Path -LiteralPath $overlapParameters.InstallRoot)) `
        'Overlapping-path refusal changed the install root.'

    $parameters.IngestBindAddress = '::1'
    $parameters.OperatorBindAddress = '::1'
    $parameters.TlsDnsName = '::1'

    $installed = & $manager @parameters
    Assert-True $installed.installed 'Install did not report success.'
    Assert-True (-not $installed.anchored) 'A no-anchor install was reported as anchored.'
    Assert-True (-not $installed.ptk_destination_selected) `
        'Mini-SIEM deployment silently selected a PTK destination.'
    $manifest = Get-Content -LiteralPath $parameters.ManifestPath -Raw |
        ConvertFrom-Json -Depth 32
    if ($IsWindows) {
        $configDirectory = Split-Path -Parent $parameters.ConfigurationPath
        $privatePaths = @($configDirectory, $parameters.DataDirectory,
            $parameters.WitnessDirectory, $parameters.ManifestPath,
            $parameters.ServiceDefinitionPath) + @(
            Get-ChildItem -LiteralPath $configDirectory -Recurse -Force |
                ForEach-Object FullName)
        foreach ($path in $privatePaths) { Assert-WindowsPrivatePath $path }

        # Require rejection of an extra reader, then restore the disposable config.
        $configFile = [IO.FileInfo]::new($parameters.ConfigurationPath)
        $originalSecurity = [IO.FileSystemAclExtensions]::GetAccessControl($configFile)
        $permissiveSecurity = [IO.FileSystemAclExtensions]::GetAccessControl($configFile)
        $permissiveSecurity.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]::new('S-1-1-0'),
                [Security.AccessControl.FileSystemRights]::Read,
                [Security.AccessControl.AccessControlType]::Allow))
        $extraReaderRejected = $false
        try {
            [IO.FileSystemAclExtensions]::SetAccessControl($configFile, $permissiveSecurity)
            try { Assert-WindowsPrivatePath $parameters.ConfigurationPath }
            catch { $extraReaderRejected = $_.Exception.Message -like 'Windows private ACL contract failed:*' }
        }
        finally { [IO.FileSystemAclExtensions]::SetAccessControl($configFile, $originalSecurity) }
        Assert-True $extraReaderRejected 'Windows ACL guard accepted an extra reader.'

        # Exercise the handoff path without taking files away from the test
        # account: Windows resolves differently-cased account names to one SID.
        foreach ($functionName in 'Set-WindowsPrivatePathAcl','Set-ServicePathOwner') {
            $functionAst = $managerAst.Find({
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -ceq $functionName
                }, $true)
            Assert-True ($null -ne $functionAst) "Manager function missing: $functionName"
            . ([scriptblock]::Create($functionAst.Extent.Text))
        }
        $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $ServiceIdentity = if ($currentIdentity -cne $currentIdentity.ToUpperInvariant()) {
            $currentIdentity.ToUpperInvariant()
        } else { $currentIdentity.ToLowerInvariant() }
        $handoffRoot = Join-Path $root 'same-sid-handoff'
        $nestedHandoff = Join-Path $handoffRoot 'nested'
        New-Item -ItemType Directory -Path $nestedHandoff -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $nestedHandoff 'secret.txt') -Value 'fixture'
        $ApplyServiceIdentityOwnership = $false
        $handoffRefused = $false
        try { Set-ServicePathOwner @($handoffRoot) }
        catch { $handoffRefused = $_.Exception.Message -match 'ApplyServiceIdentityOwnership' }
        Assert-True $handoffRefused 'Service ownership handoff omitted its explicit consent gate.'
        $ApplyServiceIdentityOwnership = $true
        Set-ServicePathOwner @($handoffRoot)
        foreach ($path in @($handoffRoot) + @(
                Get-ChildItem -LiteralPath $handoffRoot -Recurse -Force | ForEach-Object FullName)) {
            Assert-WindowsPrivatePath $path
        }
        Write-Host 'WINDOWS PRIVATE DEPLOYMENT ACL TEST PASSED'
    }
    Assert-True (-not $manifest.anchored) 'Manifest default implied anchored deployment.'
    Assert-True ($manifest.ingest_endpoint -ceq 'https://[::1]:19418/v1/logs') `
        'Manifest did not format an IPv6 ingest endpoint safely.'
    Assert-True ($manifest.operator_uri -ceq 'http://[::1]:19419/') `
        'Manifest did not format an IPv6 operator endpoint safely.'
    Assert-True ($manifest.owned_files -notcontains $parameters.DataDirectory) `
        'Database directory was incorrectly manifest-owned.'
    Assert-True ($manifest.owned_files -notcontains $parameters.WitnessDirectory) `
        'Witness directory was incorrectly manifest-owned.'
    $retentionProperties = @((Get-Content -LiteralPath $parameters.ConfigurationPath -Raw |
        ConvertFrom-Json).storage.retention.PSObject.Properties)
    Assert-True ($retentionProperties.Count -eq 0) `
        'Explicit unbounded retention was not represented truthfully.'

    Set-Content -LiteralPath (Join-Path $parameters.DataDirectory 'keep.db') -Value 'evidence'
    Set-Content -LiteralPath (Join-Path $parameters.WitnessDirectory 'keep.witness') -Value 'evidence'
    $outsideOwnedBoundary = Join-Path $root 'must-not-delete.txt'
    Set-Content -LiteralPath $outsideOwnedBoundary -Value 'operator data'
    $originalManifestBytes = Get-Content -LiteralPath $parameters.ManifestPath -Raw
    $tamperedManifest = $originalManifestBytes | ConvertFrom-Json -Depth 32
    $tamperedManifest.owned_files = @($tamperedManifest.owned_files) + $outsideOwnedBoundary
    Set-Content -LiteralPath $parameters.ManifestPath `
        -Value ($tamperedManifest | ConvertTo-Json -Depth 32)
    $escapedOwnershipRejected = $false
    try {
        & $manager -Action Uninstall -ManifestPath $parameters.ManifestPath
    } catch {
        $escapedOwnershipRejected = $_.Exception.Message -match 'escapes its program/configuration boundary'
    }
    Assert-True $escapedOwnershipRejected `
        'Uninstall trusted a manifest-owned file outside deployment boundaries.'
    Assert-True (Test-Path -LiteralPath $outsideOwnedBoundary) `
        'Refused uninstall deleted an out-of-bound operator file.'
    Set-Content -LiteralPath $parameters.ManifestPath -Value $originalManifestBytes -NoNewline
    $uninstalled = & $manager -Action Uninstall -ManifestPath $parameters.ManifestPath
    Assert-True $uninstalled.data_preserved 'Uninstall did not report preserved data.'
    Assert-True (Test-Path -LiteralPath (Join-Path $parameters.DataDirectory 'keep.db')) `
        'Uninstall deleted database evidence.'
    Assert-True (Test-Path -LiteralPath (Join-Path $parameters.WitnessDirectory 'keep.witness')) `
        'Uninstall deleted witness evidence.'

    $parameters2 = Get-InstallParameters $release1 'destructive-data' $platformServiceKind
    [void](& $manager @parameters2)
    Set-Content -LiteralPath (Join-Path $parameters2.DataDirectory 'remove.db') -Value 'evidence'
    $refusedData = $false
    try {
        & $manager -Action RemoveData -ManifestPath $parameters2.ManifestPath
    } catch {
        $refusedData = $_.Exception.Message -match 'requires -ConfirmRemoveData'
    }
    Assert-True $refusedData 'RemoveData did not require its separate confirmation.'
    Assert-True (Test-Path -LiteralPath $parameters2.DataDirectory) `
        'Refused RemoveData changed database evidence.'
    $deployment2 = Get-Content -LiteralPath $parameters2.ManifestPath -Raw |
        ConvertFrom-Json -Depth 32
    $dataMarker = Join-Path (Split-Path -Parent $parameters2.DataDirectory) `
        ".$([IO.Path]::GetFileName($parameters2.DataDirectory)).ptk-siem-data-owner"
    Set-Content -LiteralPath $dataMarker -Value 'different-deployment' -NoNewline
    $mismatchedMarkerRejected = $false
    try {
        & $manager -Action RemoveData -ManifestPath $parameters2.ManifestPath `
            -ConfirmRemoveData -Confirmation 'REMOVE PTK SIEM DATA'
    } catch {
        $mismatchedMarkerRejected = $_.Exception.Message -match 'ownership marker'
    }
    Assert-True $mismatchedMarkerRejected `
        'RemoveData trusted a path without its exact deployment ownership marker.'
    Assert-True (Test-Path -LiteralPath $parameters2.DataDirectory) `
        'Mismatched ownership marker changed database evidence.'
    Set-Content -LiteralPath $dataMarker -Value $deployment2.deployment_id -NoNewline
    $removed = & $manager -Action RemoveData -ManifestPath $parameters2.ManifestPath `
        -ConfirmRemoveData -Confirmation 'REMOVE PTK SIEM DATA'
    Assert-True ($removed.data_removed -and -not $removed.recoverable) `
        'Confirmed RemoveData did not report destructive completion.'
    Assert-True (-not (Test-Path -LiteralPath $parameters2.DataDirectory)) `
        'Confirmed RemoveData left the database directory.'
    Assert-True (-not (Test-Path -LiteralPath $dataMarker)) `
        'Confirmed RemoveData left the database ownership marker.'
    [void](& $manager -Action Uninstall -ManifestPath $parameters2.ManifestPath)

    $parameters3 = Get-InstallParameters $release1 'upgrade' $platformServiceKind
    [void](& $manager @parameters3)
    $release2 = New-TestRelease '9.8.2-test'
    $upgrade = & $manager -Action Upgrade `
        -ManifestPath $parameters3.ManifestPath `
        -PackageDir $release2.Package `
        -ArchivePath $release2.Archive `
        -ChecksumFile $release2.Checksums `
        -ExpectedVersion $release2.Version `
        -ExpectedRid linux-x64
    Assert-True ($upgrade.upgraded -and $upgrade.configuration_preserved -and
        $upgrade.data_preserved) 'Upgrade did not preserve configuration/data.'
    Assert-True ((Get-Content -LiteralPath (
        Join-Path $parameters3.InstallRoot 'PtkSiemReceiver') -Raw) -ceq
        'receiver-9.8.2-test') 'Upgrade did not activate the verified package.'
    Assert-True (Test-Path -LiteralPath $parameters3.ConfigurationPath) `
        'Upgrade removed receiver configuration.'
    [void](& $manager -Action Uninstall -ManifestPath $parameters3.ManifestPath)

    'mini-SIEM deployment lifecycle tests passed'
} finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
