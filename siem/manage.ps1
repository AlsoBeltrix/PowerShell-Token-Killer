#!/usr/bin/env pwsh
#Requires -Version 7

<#
.SYNOPSIS
Deploys and operates the separately packaged PTK mini-SIEM receiver.

.DESCRIPTION
This script installs only an explicitly supplied, release-checksummed receiver
archive. It writes a manifest naming every program, configuration, and service
definition file it owns. Data, custody witness, and anchor paths are recorded
but never removed by Uninstall. RemoveData is a separate destructive action.

It emits OS-native service definitions and foreground commands; it is not a
process or job manager.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'Install', 'Upgrade', 'Validate', 'Run', 'Status', 'ConnectionInfo', 'DoctorHelp',
        'OpenDashboard', 'Uninstall', 'RemoveData')]
    [string]$Action,

    [string]$PackageDir,
    [string]$ArchivePath,
    [string]$ChecksumFile,
    [string]$ExpectedVersion,
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-arm64')]
    [string]$ExpectedRid,

    [string]$InstallRoot,
    [string]$ConfigurationPath,
    [string]$ManifestPath,
    [string]$ServiceDefinitionPath,
    [ValidateSet('systemd', 'launchd', 'windows')]
    [string]$ServiceKind,
    [string]$ServiceName = 'ptk-siem-receiver',
    [string]$ServiceIdentity,
    [switch]$ApplyServiceIdentityOwnership,

    [string]$DataDirectory,
    [string]$WitnessDirectory,
    [string]$AnchorDirectory,

    [string]$IngestBindAddress,
    [ValidateRange(1, 65535)][int]$IngestPort,
    [string]$OperatorBindAddress = '127.0.0.1',
    [ValidateRange(1, 65535)][int]$OperatorPort,

    [string]$IngestToken,
    [string]$OperatorToken,
    [switch]$GenerateCredentials,

    [string]$TlsCertificatePath,
    [string]$TlsPrivateKeyPath,
    [string[]]$ClientCaBundlePath,
    [switch]$GenerateSelfSignedTls,
    [string]$TlsDnsName,
    [switch]$UseIngestTlsForOperator,

    [ValidateRange(1, 36500)][int]$RetentionMaxAgeDays,
    [ValidateRange(1, [long]::MaxValue)][long]$RetentionMaxTotalBytes,
    [switch]$AllowUnboundedRetention,
    [ValidateRange(1, 86400)][int]$WitnessIntervalSeconds = 60,

    [switch]$ConfirmRemoveData,
    [string]$Confirmation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Initialize-PinnedTlsValidation {
    if ($null -ne ('Ptk.SiemManagerPinnedTls' -as [type])) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ptk
{
    public static class SiemManagerPinnedTls
    {
        private static readonly HttpRequestOptionsKey<string> PinOption =
            new HttpRequestOptionsKey<string>("ptk.siem.manager.server-certificate-sha256");

        public static readonly Func<HttpRequestMessage, X509Certificate2, X509Chain,
            SslPolicyErrors, bool> Callback = Validate;

        public static void ApplyPin(HttpRequestMessage request, string pin)
        {
            request.Options.Set(PinOption, pin);
        }

        private static bool Validate(
            HttpRequestMessage request,
            X509Certificate2 certificate,
            X509Chain chain,
            SslPolicyErrors errors)
        {
            string expectedPin;
            if (!request.Options.TryGetValue(PinOption, out expectedPin))
                return errors == SslPolicyErrors.None;
            if (certificate == null ||
                (errors & (SslPolicyErrors.RemoteCertificateNameMismatch |
                    SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
                return false;
            var now = DateTime.UtcNow;
            if (now < certificate.NotBefore.ToUniversalTime() ||
                now > certificate.NotAfter.ToUniversalTime())
                return false;
            var actualPin = SHA256.HashData(certificate.RawData);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedPin), actualPin);
        }
    }
}
'@
}

function Assert-ValuePresent {
    param([string]$Name, [object]$Value)
    if ($null -eq $Value -or
        ($Value -is [string] -and [string]::IsNullOrWhiteSpace($Value))) {
        throw "$Name is required for action $Action."
    }
}

function Resolve-FullPath {
    param([string]$Name, [string]$Path)
    Assert-ValuePresent $Name $Path
    return [IO.Path]::GetFullPath($Path)
}

function Set-PrivateDirectory {
    param([string]$Path)
    [void](New-Item -ItemType Directory -Path $Path -Force)
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode(
            $Path,
            [IO.UnixFileMode]::UserRead -bor
            [IO.UnixFileMode]::UserWrite -bor
            [IO.UnixFileMode]::UserExecute)
    }
}

function Set-PrivateFile {
    param([string]$Path)
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode(
            $Path,
            [IO.UnixFileMode]::UserRead -bor
            [IO.UnixFileMode]::UserWrite)
    }
}

function Test-SameOrDescendant {
    param([string]$Candidate, [string]$Root)
    $currentPath = [IO.Path]::GetFullPath($Candidate).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rootPath = [IO.Path]::GetFullPath($Root)
    if ([IO.Path]::GetPathRoot($rootPath) -cne $rootPath) {
        $rootPath = $rootPath.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
    }
    $comparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    while (-not [string]::IsNullOrWhiteSpace($currentPath)) {
        if ($currentPath.Equals($rootPath, $comparison)) { return $true }
        $parentPath = [IO.Path]::GetDirectoryName($currentPath)
        if ([string]::IsNullOrWhiteSpace($parentPath) -or
            $parentPath.Equals($currentPath, $comparison)) {
            break
        }
        $currentPath = $parentPath
    }
    return $false
}

function Test-PathsOverlap {
    param([string]$Left, [string]$Right)
    return (Test-SameOrDescendant $Left $Right) -or
        (Test-SameOrDescendant $Right $Left)
}

function ConvertTo-UriHost {
    param([string]$Value)
    $address = $null
    if ([Net.IPAddress]::TryParse($Value, [ref]$address) -and
        $address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6) {
        return "[$Value]"
    }
    return $Value
}

function Set-ServicePathOwner {
    param([string[]]$Paths)
    $currentIdentity = if ($IsWindows) {
        [Security.Principal.WindowsIdentity]::GetCurrent().Name
    } else {
        [Environment]::UserName
    }
    if ($currentIdentity -ceq $ServiceIdentity) { return }
    if (-not $ApplyServiceIdentityOwnership) {
        throw "ServiceIdentity '$ServiceIdentity' differs from installer identity '$currentIdentity'. Run Install as the service identity or pass -ApplyServiceIdentityOwnership from an administrator."
    }
    if ($IsWindows) {
        foreach ($path in $Paths) {
            & icacls.exe $path /setowner $ServiceIdentity /T /C | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Could not set service ownership on '$path'." }
            & icacls.exe $path /inheritance:r /grant:r "${ServiceIdentity}:(OI)(CI)F" /T /C |
                Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Could not protect service path '$path'." }
        }
    } else {
        foreach ($path in $Paths) {
            & chown -R $ServiceIdentity $path
            if ($LASTEXITCODE -ne 0) { throw "Could not set service ownership on '$path'." }
        }
    }
}

function Set-ServiceProgramAccess {
    param([string]$Path, [string]$Identity)
    $currentIdentity = if ($IsWindows) {
        [Security.Principal.WindowsIdentity]::GetCurrent().Name
    } else {
        [Environment]::UserName
    }
    if ($currentIdentity -ceq $Identity) { return }
    if ($IsWindows) {
        & icacls.exe $Path /grant:r "${Identity}:(OI)(CI)RX" /T /C | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Could not grant service read/execute access to '$Path'."
        }
    } else {
        & chmod -R 'a+rX' $Path
        if ($LASTEXITCODE -ne 0) {
            throw "Could not grant service read/execute access to '$Path'."
        }
    }
}

function Get-DataOwnershipMarkerPath {
    param([string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $leaf = Split-Path -Leaf $fullPath
    if ([string]::IsNullOrWhiteSpace($leaf)) {
        throw "A filesystem root cannot be a deployment data path: '$fullPath'."
    }

    return Join-Path (Split-Path -Parent $fullPath) ".$leaf.ptk-siem-data-owner"
}

function Write-DataOwnershipMarker {
    param([string]$Path, [string]$DeploymentId)
    $marker = Get-DataOwnershipMarkerPath $Path
    if (Test-Path -LiteralPath $marker) {
        throw "Data ownership marker already exists '$marker'."
    }
    [IO.File]::WriteAllText($marker, $DeploymentId)
    Set-PrivateFile $marker
}

function New-RandomToken {
    $bytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    try {
        return [Convert]::ToBase64String($bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')
    } finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
    }
}

function Assert-ReleasePackage {
    $package = Resolve-FullPath PackageDir $PackageDir
    $archive = Resolve-FullPath ArchivePath $ArchivePath
    $checksums = Resolve-FullPath ChecksumFile $ChecksumFile
    Assert-ValuePresent ExpectedVersion $ExpectedVersion
    Assert-ValuePresent ExpectedRid $ExpectedRid
    foreach ($leaf in $archive, $checksums) {
        if (-not (Test-Path -LiteralPath $leaf -PathType Leaf)) {
            throw "Required release file was not found: '$leaf'."
        }
    }
    if (-not (Test-Path -LiteralPath $package -PathType Container)) {
        throw "Extracted package directory was not found: '$package'."
    }

    $archiveName = [IO.Path]::GetFileName($archive)
    if ($archiveName -notlike "ptk-siem-receiver-$ExpectedVersion-$ExpectedRid.*") {
        throw "Archive '$archiveName' does not identify expected version/RID '$ExpectedVersion/$ExpectedRid'."
    }
    $checksumMatches = @(Get-Content -LiteralPath $checksums | ForEach-Object {
        if ($_ -match '^([0-9a-fA-F]{64})\s+\*?(.+)$' -and
            [IO.Path]::GetFileName($Matches[2].Trim()) -ceq $archiveName) {
            $Matches[1].ToUpperInvariant()
        }
    })
    if ($checksumMatches.Count -ne 1) {
        throw "SHA256SUMS does not contain exactly one entry for '$archiveName'."
    }
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actualHash -cne $checksumMatches[0]) {
        throw "Archive checksum mismatch for '$archiveName'."
    }

    $versionPath = Join-Path $package 'VERSION'
    if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf) -or
        [IO.File]::ReadAllText($versionPath).Trim() -cne $ExpectedVersion) {
        throw "Extracted package VERSION does not match '$ExpectedVersion'."
    }
    $executableName = if ($ExpectedRid.StartsWith('win-', [StringComparison]::Ordinal)) {
        'PtkSiemReceiver.exe'
    } else {
        'PtkSiemReceiver'
    }
    $executable = Join-Path $package $executableName
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Extracted package is missing '$executableName'."
    }
    return [pscustomobject]@{
        package = $package
        archive = $archive
        archive_sha256 = $actualHash
        executable_name = $executableName
        version = $ExpectedVersion
        rid = $ExpectedRid
    }
}

function Copy-TlsMaterial {
    param([string]$ConfigDirectory)
    $tlsDirectory = Join-Path $ConfigDirectory 'tls'
    Set-PrivateDirectory $tlsDirectory
    $certificateTarget = Join-Path $tlsDirectory 'server-certificate.pem'
    $keyTarget = Join-Path $tlsDirectory 'server-key.pem'
    $caTargets = [Collections.Generic.List[string]]::new()

    if ($GenerateSelfSignedTls) {
        Assert-ValuePresent TlsDnsName $TlsDnsName
        $caRsa = [Security.Cryptography.RSA]::Create(3072)
        $rsa = [Security.Cryptography.RSA]::Create(3072)
        $caCertificate = $null
        try {
            $caRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
                'CN=PTK mini-SIEM local client CA',
                $caRsa,
                [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $caRequest.CertificateExtensions.Add(
                [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
                    $true, $false, 0, $true))
            $caRequest.CertificateExtensions.Add(
                [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                    [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
                    [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign,
                    $true))
            $caCertificate = $caRequest.CreateSelfSigned(
                [DateTimeOffset]::UtcNow.AddMinutes(-5),
                [DateTimeOffset]::UtcNow.AddYears(5))
            $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
                "CN=$TlsDnsName",
                $rsa,
                [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
            $parsedIp = $null
            if ([Net.IPAddress]::TryParse($TlsDnsName, [ref]$parsedIp)) {
                $san.AddIpAddress($parsedIp)
            } else {
                $san.AddDnsName($TlsDnsName)
            }
            $request.CertificateExtensions.Add($san.Build())
            $request.CertificateExtensions.Add(
                [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
                    $false, $false, 0, $true))
            $request.CertificateExtensions.Add(
                [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                    [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
                    [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment,
                    $true))
            $enhancedKeyUsage = [Security.Cryptography.OidCollection]::new()
            [void]$enhancedKeyUsage.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
            $request.CertificateExtensions.Add(
                [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
                    $enhancedKeyUsage,
                    $true))
            $serial = [byte[]]::new(16)
            [Security.Cryptography.RandomNumberGenerator]::Fill($serial)
            $serial[0] = $serial[0] -band 0x7f
            $publicCertificate = $request.Create(
                $caCertificate,
                [DateTimeOffset]::UtcNow.AddMinutes(-5),
                [DateTimeOffset]::UtcNow.AddYears(1),
                $serial)
            try {
                $certificate = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey(
                    $publicCertificate,
                    $rsa)
                try {
                    [IO.File]::WriteAllText(
                        $certificateTarget,
                        $certificate.ExportCertificatePem())
                    [IO.File]::WriteAllText($keyTarget, $rsa.ExportPkcs8PrivateKeyPem())
                    $pin = [Convert]::ToHexString(
                        [Security.Cryptography.SHA256]::HashData($certificate.RawData))
                } finally {
                    $certificate.Dispose()
                }
            } finally {
                $publicCertificate.Dispose()
            }
            $caTarget = Join-Path $tlsDirectory 'client-ca-bundle.pem'
            [IO.File]::WriteAllText($caTarget, $caCertificate.ExportCertificatePem())
            $caTargets.Add($caTarget)
        } finally {
            if ($null -ne $caCertificate) { $caCertificate.Dispose() }
            $rsa.Dispose()
            $caRsa.Dispose()
        }
    } else {
        Assert-ValuePresent TlsCertificatePath $TlsCertificatePath
        Assert-ValuePresent TlsPrivateKeyPath $TlsPrivateKeyPath
        if ($null -eq $ClientCaBundlePath -or $ClientCaBundlePath.Count -eq 0) {
            throw 'ClientCaBundlePath is required unless -GenerateSelfSignedTls is used.'
        }
        Copy-Item -LiteralPath (Resolve-FullPath TlsCertificatePath $TlsCertificatePath) `
            -Destination $certificateTarget
        Copy-Item -LiteralPath (Resolve-FullPath TlsPrivateKeyPath $TlsPrivateKeyPath) `
            -Destination $keyTarget
        for ($index = 0; $index -lt $ClientCaBundlePath.Count; $index++) {
            $caTarget = Join-Path $tlsDirectory "client-ca-$index.pem"
            Copy-Item -LiteralPath (
                Resolve-FullPath "ClientCaBundlePath[$index]" $ClientCaBundlePath[$index]) `
                -Destination $caTarget
            $caTargets.Add($caTarget)
        }
        $loaded = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile(
            $certificateTarget)
        try {
            $pin = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData($loaded.RawData))
        } finally {
            $loaded.Dispose()
        }
    }
    Set-PrivateFile $certificateTarget
    Set-PrivateFile $keyTarget
    foreach ($path in $caTargets) { Set-PrivateFile $path }
    return [pscustomobject]@{
        certificate = $certificateTarget
        key = $keyTarget
        client_ca_bundles = @($caTargets)
        sha256 = $pin
        owned_files = @($certificateTarget, $keyTarget) + @($caTargets)
    }
}

function ConvertTo-XmlEscapedText([string]$Value) {
    return [Security.SecurityElement]::Escape($Value)
}

function New-ServiceDefinition {
    param(
        [string]$Executable,
        [string]$ConfigPath,
        [string]$DefinitionPath
    )
    Assert-ValuePresent ServiceKind $ServiceKind
    Assert-ValuePresent ServiceIdentity $ServiceIdentity
    if ($ServiceIdentity -match '[\r\n]') {
        throw 'ServiceIdentity cannot contain a line break.'
    }
    if ($ServiceKind -in 'systemd', 'launchd' -and
        $ServiceIdentity -notmatch '^[A-Za-z_][A-Za-z0-9_.-]*$') {
        throw 'Unix service identity contains unsupported characters.'
    }
    if ($ServiceKind -ceq 'systemd' -and
        ($Executable -match '["\\\r\n]' -or $ConfigPath -match '["\\\r\n]')) {
        throw 'systemd executable/configuration paths contain unsupported quoting characters.'
    }
    if ($ServiceName -notmatch '^[A-Za-z0-9_.-]+$') {
        throw 'ServiceName may contain only letters, digits, period, underscore, and hyphen.'
    }
    $definitionDirectory = Split-Path -Parent $DefinitionPath
    Set-PrivateDirectory $definitionDirectory
    switch ($ServiceKind) {
        'systemd' {
            $content = @"
[Unit]
Description=PTK mini-SIEM receiver
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$ServiceIdentity
ExecStart="$Executable" --config "$ConfigPath"
Restart=on-failure
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
"@
            $instructions = "Install with: sudo install -m 0644 '$DefinitionPath' '/etc/systemd/system/$ServiceName.service'; sudo systemctl daemon-reload; sudo systemctl enable --now '$ServiceName.service'"
        }
        'launchd' {
            $label = ConvertTo-XmlEscapedText $ServiceName
            $content = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>Label</key><string>$label</string>
<key>UserName</key><string>$(ConvertTo-XmlEscapedText $ServiceIdentity)</string>
<key>ProgramArguments</key><array><string>$(ConvertTo-XmlEscapedText $Executable)</string><string>--config</string><string>$(ConvertTo-XmlEscapedText $ConfigPath)</string></array>
<key>RunAtLoad</key><true/>
<key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
</dict></plist>
"@
            $instructions = "Install with: sudo cp '$DefinitionPath' '/Library/LaunchDaemons/$ServiceName.plist'; sudo launchctl bootstrap system '/Library/LaunchDaemons/$ServiceName.plist'"
        }
        'windows' {
            $escapedExecutable = $Executable.Replace("'", "''")
            $escapedConfig = $ConfigPath.Replace("'", "''")
            $escapedName = $ServiceName.Replace("'", "''")
            $escapedIdentity = $ServiceIdentity.Replace("'", "''")
            $content = @"
#Requires -RunAsAdministrator
[CmdletBinding()]
param([pscredential]`$Credential)
`$parameters = @{
    Name = '$escapedName'
    BinaryPathName = '"$escapedExecutable" --config "$escapedConfig"'
    DisplayName = 'PTK mini-SIEM receiver'
    StartupType = 'Automatic'
}
`$identity = '$escapedIdentity'
`$configureBuiltInIdentity = `$false
if (`$null -ne `$Credential) { `$parameters.Credential = `$Credential }
elseif (`$identity -in 'NT AUTHORITY\LocalService','NT AUTHORITY\NetworkService') {
    `$configureBuiltInIdentity = `$true
} elseif (`$identity -cne 'LocalSystem') {
    throw 'Pass -Credential for the configured service identity.'
}
New-Service @parameters
if (`$configureBuiltInIdentity) {
    & sc.exe config '$escapedName' 'obj=' `$identity | Out-Host
    if (`$LASTEXITCODE -ne 0) { throw 'Could not apply the built-in service identity.' }
}
Start-Service -Name '$escapedName'
"@
            $instructions = "Run elevated: pwsh -File '$DefinitionPath' -Credential (Get-Credential '$ServiceIdentity')"
        }
    }
    [IO.File]::WriteAllText($DefinitionPath, $content)
    Set-PrivateFile $DefinitionPath
    return $instructions
}

function Get-Manifest {
    $path = Resolve-FullPath ManifestPath $ManifestPath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Deployment manifest was not found at '$path'."
    }
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 32
    if ($manifest.schema -cne 'ptk.siem.deployment/1') {
        throw 'Deployment manifest schema is unsupported.'
    }
    Assert-ManifestOwnershipBoundary $manifest
    return $manifest
}

function Assert-ManifestOwnershipBoundary {
    param([object]$Manifest)
    $deploymentId = [guid]::Empty
    if (-not [guid]::TryParse([string]$Manifest.deployment_id, [ref]$deploymentId)) {
        throw 'Deployment manifest ID is invalid.'
    }
    $programRoot = [IO.Path]::GetFullPath([string]$Manifest.program_root)
    $configurationPath = [IO.Path]::GetFullPath([string]$Manifest.configuration_path)
    $configurationRoot = Split-Path -Parent $configurationPath
    $serviceDefinition = [IO.Path]::GetFullPath([string]$Manifest.service_definition_path)
    foreach ($entry in @($Manifest.owned_files)) {
        $path = [IO.Path]::GetFullPath([string]$entry)
        if (-not (Test-SameOrDescendant $path $programRoot) -and
            -not (Test-SameOrDescendant $path $configurationRoot) -and
            $path -cne $serviceDefinition) {
            throw "Deployment manifest owned file escapes its program/configuration boundary: '$path'."
        }
    }
}

function Write-DeploymentManifest {
    param([Collections.IDictionary]$Manifest)
    $path = [IO.Path]::GetFullPath($Manifest.manifest_path)
    Set-PrivateDirectory (Split-Path -Parent $path)
    $temporary = Join-Path (Split-Path -Parent $path) (
        '.' + [IO.Path]::GetFileName($path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, ($Manifest | ConvertTo-Json -Depth 16))
        Set-PrivateFile $temporary
        [IO.File]::Move($temporary, $path, $true)
    } finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Install-Receiver {
    $release = Assert-ReleasePackage
    $programRoot = Resolve-FullPath InstallRoot $InstallRoot
    $configPath = Resolve-FullPath ConfigurationPath $ConfigurationPath
    $configDirectory = Split-Path -Parent $configPath
    $manifestFile = Resolve-FullPath ManifestPath $ManifestPath
    $serviceFile = Resolve-FullPath ServiceDefinitionPath $ServiceDefinitionPath
    $dataRoot = Resolve-FullPath DataDirectory $DataDirectory
    $witnessRoot = Resolve-FullPath WitnessDirectory $WitnessDirectory
    if (Test-PathsOverlap $configDirectory $programRoot) {
        throw 'ConfigurationPath must be outside InstallRoot so upgrades cannot replace it.'
    }
    if ((Test-SameOrDescendant $manifestFile $programRoot) -or
        (Test-SameOrDescendant $manifestFile $configDirectory)) {
        throw 'ManifestPath must be outside program and receiver-owned configuration roots.'
    }
    if ((Test-SameOrDescendant $serviceFile $programRoot) -or
        (Test-SameOrDescendant $serviceFile $configDirectory)) {
        throw 'ServiceDefinitionPath must be outside program and receiver-owned configuration roots.'
    }
    Assert-ValuePresent IngestBindAddress $IngestBindAddress
    if ($IngestPort -lt 1) { throw 'IngestPort is required for action Install.' }
    if ($OperatorPort -lt 1) { throw 'OperatorPort is required for action Install.' }
    Assert-ValuePresent ServiceIdentity $ServiceIdentity
    $installerIdentity = if ($IsWindows) {
        [Security.Principal.WindowsIdentity]::GetCurrent().Name
    } else {
        [Environment]::UserName
    }
    if ($installerIdentity -cne $ServiceIdentity -and
        -not $ApplyServiceIdentityOwnership) {
        throw "ServiceIdentity '$ServiceIdentity' differs from installer identity '$installerIdentity'. Run Install as the service identity or pass -ApplyServiceIdentityOwnership from an administrator."
    }
    Assert-ValuePresent TlsDnsName $TlsDnsName
    if ($IngestPort -eq $OperatorPort) { throw 'IngestPort and OperatorPort must differ.' }
    $parsedIngestAddress = $null
    if (-not [Net.IPAddress]::TryParse($IngestBindAddress, [ref]$parsedIngestAddress)) {
        throw 'IngestBindAddress must be an IP address.'
    }
    $parsedOperatorAddress = $null
    if (-not [Net.IPAddress]::TryParse($OperatorBindAddress, [ref]$parsedOperatorAddress)) {
        throw 'OperatorBindAddress must be an IP address.'
    }
    if (-not [Net.IPAddress]::IsLoopback($parsedOperatorAddress) -and
        -not $UseIngestTlsForOperator) {
        throw 'A non-loopback operator bind requires -UseIngestTlsForOperator.'
    }
    if (-not $AllowUnboundedRetention -and
        $RetentionMaxAgeDays -eq 0 -and $RetentionMaxTotalBytes -eq 0) {
        throw 'Choose a retention bound, or explicitly pass -AllowUnboundedRetention.'
    }
    if ($GenerateCredentials) {
        if ([string]::IsNullOrWhiteSpace($IngestToken)) { $script:IngestToken = New-RandomToken }
        if ([string]::IsNullOrWhiteSpace($OperatorToken)) { $script:OperatorToken = New-RandomToken }
    }
    Assert-ValuePresent IngestToken $IngestToken
    Assert-ValuePresent OperatorToken $OperatorToken
    if ($IngestToken -ceq $OperatorToken) { throw 'Ingest and operator credentials must differ.' }
    if ($IngestToken.Length -lt 16) { throw 'IngestToken must contain at least 16 characters.' }
    $anchorRoot = $null
    if (-not [string]::IsNullOrWhiteSpace($AnchorDirectory)) {
        $anchorRoot = Resolve-FullPath AnchorDirectory $AnchorDirectory
    }
    $dataPaths = @($dataRoot, $witnessRoot) + @(
        $anchorRoot | Where-Object { $null -ne $_ })
    for ($left = 0; $left -lt $dataPaths.Count; $left++) {
        for ($right = $left + 1; $right -lt $dataPaths.Count; $right++) {
            if (Test-PathsOverlap $dataPaths[$left] $dataPaths[$right]) {
                throw 'Database, witness, and anchor paths must be mutually independent.'
            }
        }
    }
    foreach ($dataPath in $dataPaths) {
        if ((Test-PathsOverlap $dataPath $programRoot) -or
            (Test-PathsOverlap $dataPath $configDirectory) -or
            (Test-SameOrDescendant $manifestFile $dataPath) -or
            (Test-SameOrDescendant $serviceFile $dataPath)) {
            throw "Data path '$dataPath' overlaps deployment program, configuration, or control files."
        }
    }

    foreach ($path in $programRoot, $configPath, $manifestFile, $serviceFile) {
        if (Test-Path -LiteralPath $path) {
            throw "Install refuses to overwrite existing path '$path'. Use Upgrade for an existing deployment."
        }
    }
    foreach ($path in @($dataRoot, $witnessRoot) + @(
        $anchorRoot | Where-Object { $null -ne $_ })) {
        if ((Test-Path -LiteralPath $path -PathType Container) -and
            (Get-ChildItem -LiteralPath $path -Force | Select-Object -First 1)) {
            throw "Install requires an empty dedicated data path: '$path'."
        }
    }
    Set-PrivateDirectory $programRoot
    Set-PrivateDirectory $dataRoot
    Set-PrivateDirectory $witnessRoot
    if ($null -ne $anchorRoot) { Set-PrivateDirectory $anchorRoot }
    $deploymentId = [guid]::NewGuid().ToString('D')
    Write-DataOwnershipMarker $dataRoot $deploymentId
    Write-DataOwnershipMarker $witnessRoot $deploymentId
    if ($null -ne $anchorRoot) { Write-DataOwnershipMarker $anchorRoot $deploymentId }

    Copy-Item -Path (Join-Path $release.package '*') -Destination $programRoot `
        -Recurse -Force
    $programFiles = @(Get-ChildItem -LiteralPath $programRoot -File -Recurse | ForEach-Object {
        $_.FullName
    })
    Set-PrivateDirectory $configDirectory
    $tls = Copy-TlsMaterial $configDirectory

    $retention = @{}
    if ($RetentionMaxAgeDays -gt 0) { $retention.maxAgeDays = $RetentionMaxAgeDays }
    if ($RetentionMaxTotalBytes -gt 0) { $retention.maxTotalBytes = $RetentionMaxTotalBytes }
    $configuration = [ordered]@{
        ingest = [ordered]@{
            bindAddress = $IngestBindAddress
            port = $IngestPort
            serverCertificatePath = $tls.certificate
            serverCertificateKeyPath = $tls.key
            clientCaBundlePaths = @($tls.client_ca_bundles)
            revocationCheckMode = 'NoCheck'
            maxRequestBytes = 1048576
            maxConcurrentRequests = 64
            token = $IngestToken
        }
        operator = [ordered]@{
            bindAddress = $OperatorBindAddress
            port = $OperatorPort
            token = $OperatorToken
        }
        storage = [ordered]@{
            sqlitePath = (Join-Path $dataRoot 'ptk-siem.db')
            retention = $retention
            custodyWitness = [ordered]@{
                directoryPath = $witnessRoot
                checkpointIntervalSeconds = $WitnessIntervalSeconds
            }
        }
    }
    if ($null -ne $anchorRoot) {
        $configuration.storage.custodyWitness.anchorDirectoryPath = $anchorRoot
    }
    if ($UseIngestTlsForOperator) {
        $configuration.operator.httpsCertificatePath = $tls.certificate
        $configuration.operator.httpsCertificateKeyPath = $tls.key
    }
    [IO.File]::WriteAllText($configPath, ($configuration | ConvertTo-Json -Depth 12))
    Set-PrivateFile $configPath

    $executable = Join-Path $programRoot $release.executable_name
    $serviceInstructions = New-ServiceDefinition $executable $configPath $serviceFile
    $operatorScheme = if ($UseIngestTlsForOperator) { 'https' } else { 'http' }
    $operatorAddress = if ($UseIngestTlsForOperator -or
        $OperatorBindAddress -in '0.0.0.0', '::') {
        $TlsDnsName
    } else {
        $OperatorBindAddress
    }
    $operatorHost = ConvertTo-UriHost $operatorAddress
    $ingestHost = ConvertTo-UriHost $TlsDnsName
    $manifest = [ordered]@{
        schema = 'ptk.siem.deployment/1'
        deployment_id = $deploymentId
        manifest_path = $manifestFile
        version = $release.version
        rid = $release.rid
        archive_sha256 = $release.archive_sha256
        installed_utc = [DateTimeOffset]::UtcNow.ToString('O')
        program_root = $programRoot
        executable = $executable
        configuration_path = $configPath
        service_kind = $ServiceKind
        service_name = $ServiceName
        service_identity = $ServiceIdentity
        service_definition_path = $serviceFile
        service_instructions = $serviceInstructions
        owned_files = @($programFiles) + @($tls.owned_files) + @($configPath, $serviceFile)
        data_paths = [ordered]@{
            sqlite_directory = $dataRoot
            witness_directory = $witnessRoot
            anchor_directory = $anchorRoot
        }
        anchored = $null -ne $anchorRoot
        ingest_endpoint = "https://$ingestHost`:$IngestPort/v1/logs"
        server_certificate_sha256 = $tls.sha256
        operator_uri = "${operatorScheme}://${operatorHost}:$OperatorPort/"
        retention = [ordered]@{
            maximum_age_days = if ($RetentionMaxAgeDays -gt 0) { $RetentionMaxAgeDays } else { $null }
            maximum_total_bytes = if ($RetentionMaxTotalBytes -gt 0) { $RetentionMaxTotalBytes } else { $null }
        }
    }
    Write-DeploymentManifest $manifest
    Set-ServiceProgramAccess $programRoot $ServiceIdentity
    $serviceOwnedPaths = @($configDirectory, $dataRoot, $witnessRoot)
    if ($null -ne $anchorRoot) { $serviceOwnedPaths += $anchorRoot }
    Set-ServicePathOwner $serviceOwnedPaths
    [pscustomobject]@{
        installed = $true
        version = $release.version
        rid = $release.rid
        archive_sha256 = $release.archive_sha256
        manifest = $manifestFile
        ingest_endpoint = $manifest.ingest_endpoint
        operator_uri = $manifest.operator_uri
        server_certificate_sha256 = $tls.sha256
        anchored = $manifest.anchored
        retention = $manifest.retention
        service_definition = $serviceFile
        service_instructions = $serviceInstructions
        foreground_command = "& '$executable' --config '$configPath'"
        ptk_destination_selected = $false
        ptk_restart_required_after_destination_change = $false
    }
}

function Update-Receiver {
    $manifest = Get-Manifest
    $release = Assert-ReleasePackage
    if ([string]$manifest.rid -cne [string]$release.rid) {
        throw "Upgrade RID '$($release.rid)' does not match installed RID '$($manifest.rid)'."
    }
    $programRoot = [string]$manifest.program_root
    $ownedProgramFiles = @($manifest.owned_files | Where-Object {
        [IO.Path]::GetFullPath([string]$_).StartsWith(
            $programRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::Ordinal)
    })
    $actualProgramFiles = @(Get-ChildItem -LiteralPath $programRoot -File -Recurse |
        ForEach-Object FullName)
    $unowned = @($actualProgramFiles | Where-Object { $_ -notin $ownedProgramFiles })
    if ($unowned.Count -gt 0) {
        throw "Upgrade refuses a program root containing unowned files: $($unowned -join ', ')."
    }

    $parent = Split-Path -Parent $programRoot
    $stage = Join-Path $parent ('.ptk-siem-upgrade-' + [guid]::NewGuid().ToString('N'))
    $backup = Join-Path $parent ('.ptk-siem-backup-' + [guid]::NewGuid().ToString('N'))
    try {
        [void](New-Item -ItemType Directory -Path $stage)
        Copy-Item -Path (Join-Path $release.package '*') -Destination $stage `
            -Recurse -Force
        Set-ServiceProgramAccess $stage ([string]$manifest.service_identity)

        $nonProgramOwned = @($manifest.owned_files | Where-Object {
            $_ -notin $ownedProgramFiles
        })
        $newProgramOwned = @(Get-ChildItem -LiteralPath $stage -File -Recurse |
            ForEach-Object {
                Join-Path $programRoot ([IO.Path]::GetRelativePath($stage, $_.FullName))
            })
        $manifest.version = $release.version
        $manifest.archive_sha256 = $release.archive_sha256
        $manifest.installed_utc = [DateTimeOffset]::UtcNow.ToString('O')
        $manifest.executable = Join-Path $programRoot $release.executable_name
        $manifest.owned_files = @($newProgramOwned) + @($nonProgramOwned)

        Move-Item -LiteralPath $programRoot -Destination $backup
        try {
            Move-Item -LiteralPath $stage -Destination $programRoot
            Write-DeploymentManifest (
                $manifest | ConvertTo-Json -Depth 16 | ConvertFrom-Json -AsHashtable)
        } catch {
            if (Test-Path -LiteralPath $programRoot) {
                Remove-Item -LiteralPath $programRoot -Recurse -Force
            }
            Move-Item -LiteralPath $backup -Destination $programRoot
            throw
        }
        Remove-Item -LiteralPath $backup -Recurse -Force
    } finally {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    }

    [pscustomobject]@{
        upgraded = $true
        version = $release.version
        rid = $release.rid
        configuration_preserved = $true
        data_preserved = $true
        service_restart_required = $true
        ptk_restart_required = $false
    }
}

function Invoke-ReceiverGet {
    param([object]$Manifest, [string]$RelativePath)
    $configuration = Get-Content -LiteralPath $Manifest.configuration_path -Raw |
        ConvertFrom-Json -Depth 16
    $token = [string]$configuration.operator.token
    $uri = [uri]::new([uri]$Manifest.operator_uri, $RelativePath)
    $handler = [Net.Http.HttpClientHandler]::new()
    if ($uri.Scheme -ceq 'https') {
        $expectedPin = [string]$Manifest.server_certificate_sha256
        Initialize-PinnedTlsValidation
        $handler.ServerCertificateCustomValidationCallback =
            [Ptk.SiemManagerPinnedTls]::Callback
    }
    $client = [Net.Http.HttpClient]::new($handler, $true)
    $request = $null
    $response = $null
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(15)
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $uri)
        $request.Headers.Authorization =
            [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
        if ($uri.Scheme -ceq 'https') {
            [Ptk.SiemManagerPinnedTls]::ApplyPin($request, $expectedPin)
        }
        $response = $client.Send($request)
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "Receiver request failed with HTTP $([int]$response.StatusCode)."
        }
        return $content | ConvertFrom-Json -Depth 32
    } finally {
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $request) { $request.Dispose() }
        $client.Dispose()
    }
}

function Test-Deployment {
    $manifest = Get-Manifest
    foreach ($path in @($manifest.owned_files) + @($manifest.manifest_path)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Manifest-owned file is missing: '$path'."
        }
    }
    $health = Invoke-ReceiverGet $manifest '/api/health'
    $receiverHealth = if ($health.integrity.status -ceq 'intact' -and
        $health.custody.status -ceq 'healthy') {
        'healthy'
    } else {
        'attention_required'
    }
    [pscustomobject]@{
        valid = $true
        version = $manifest.version
        rid = $manifest.rid
        receiver_health = $receiverHealth
        custody = $health.custody.status
        anchored = $manifest.anchored
        data_paths_manifest_owned = $false
        ptk_destination_selected = $false
    }
}

function Remove-EmptyParentDirectory {
    param([string[]]$Files)
    $directories = @($Files | ForEach-Object { Split-Path -Parent $_ } | Sort-Object -Unique |
        Sort-Object { $_.Length } -Descending)
    foreach ($directory in $directories) {
        if ((Test-Path -LiteralPath $directory -PathType Container) -and
            -not (Get-ChildItem -LiteralPath $directory -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $directory -Force
        }
    }
}

function Uninstall-Receiver {
    $manifest = Get-Manifest
    $owned = @($manifest.owned_files | ForEach-Object { [IO.Path]::GetFullPath([string]$_) })
    foreach ($path in $owned) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path -Force
        }
    }
    Remove-EmptyParentDirectory $owned
    Remove-Item -LiteralPath $manifest.manifest_path -Force
    [pscustomobject]@{
        uninstalled = $true
        removed_manifest_owned_files = $owned.Count + 1
        data_preserved = $true
        preserved_paths = $manifest.data_paths
        recovery = 'Program/configuration files are removed. Data remains until the separately named RemoveData action is confirmed.'
    }
}

function Remove-ReceiverData {
    $manifest = Get-Manifest
    if (-not $ConfirmRemoveData -or $Confirmation -cne 'REMOVE PTK SIEM DATA') {
        throw "RemoveData requires -ConfirmRemoveData and -Confirmation 'REMOVE PTK SIEM DATA'. Nothing was removed."
    }
    $paths = @(
        $manifest.data_paths.sqlite_directory,
        $manifest.data_paths.witness_directory,
        $manifest.data_paths.anchor_directory
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        ForEach-Object { [IO.Path]::GetFullPath([string]$_) }
    if ($paths.Count -ne (@($paths | Sort-Object -Unique)).Count) {
        throw 'Manifest data paths overlap or repeat; refusing destructive removal.'
    }
    for ($left = 0; $left -lt $paths.Count; $left++) {
        for ($right = $left + 1; $right -lt $paths.Count; $right++) {
            if ((Test-SameOrDescendant $paths[$left] $paths[$right]) -or
                (Test-SameOrDescendant $paths[$right] $paths[$left])) {
                throw 'Manifest data paths overlap or repeat; refusing destructive removal.'
            }
        }
    }
    foreach ($path in $paths) {
        if ([IO.Path]::GetPathRoot($path) -ceq $path) {
            throw "Refusing destructive root path '$path'."
        }
    }
    foreach ($path in $paths) {
        $marker = Get-DataOwnershipMarkerPath $path
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
            [IO.File]::ReadAllText($marker).Trim() -cne [string]$manifest.deployment_id) {
            throw "Data ownership marker is missing or mismatched at '$path'. Nothing was removed."
        }
    }
    foreach ($path in $paths) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        Remove-Item -LiteralPath (Get-DataOwnershipMarkerPath $path) -Force
    }
    [pscustomobject]@{
        data_removed = $true
        removed_paths = $paths
        recoverable = $false
    }
}

switch ($Action) {
    'Install' { Install-Receiver }
    'Upgrade' { Update-Receiver }
    'Validate' { Test-Deployment }
    'Status' {
        $manifest = Get-Manifest
        $health = Invoke-ReceiverGet $manifest '/api/health'
        [pscustomobject]@{
            version = $manifest.version
            rid = $manifest.rid
            operator_uri = $manifest.operator_uri
            health = $health
            anchored = $manifest.anchored
            service_instructions = $manifest.service_instructions
        }
    }
    'ConnectionInfo' {
        $manifest = Get-Manifest
        [pscustomobject]@{
            ingest_endpoint = $manifest.ingest_endpoint
            server_certificate_sha256 = $manifest.server_certificate_sha256
            receiver_operator_uri = $manifest.operator_uri
            receiver_configuration_path = $manifest.configuration_path
            anchored = $manifest.anchored
        }
    }
    'DoctorHelp' {
        $manifest = Get-Manifest
        [pscustomobject]@{
            next = 'Run the PTK package script ptk-audit-destination.ps1 -Action Doctor.'
            destination_endpoint = $manifest.ingest_endpoint
            receiver_operator_uri = $manifest.operator_uri
            server_certificate_sha256 = $manifest.server_certificate_sha256
            boundary = 'Doctor contacts only the explicitly named PTK destination and this receiver operator endpoint.'
        }
    }
    'Run' {
        $manifest = Get-Manifest
        & $manifest.executable --config $manifest.configuration_path
        exit $LASTEXITCODE
    }
    'OpenDashboard' {
        $manifest = Get-Manifest
        Start-Process $manifest.operator_uri
        [pscustomobject]@{ opened = $true; operator_uri = $manifest.operator_uri }
    }
    'Uninstall' { Uninstall-Receiver }
    'RemoveData' { Remove-ReceiverData }
}
