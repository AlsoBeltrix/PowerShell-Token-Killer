<#
.SYNOPSIS
Verifies the exact standalone PtkSiemReceiver layout that will be archived.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackageDir,

    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-arm64')]
    [string]$Rid,

    [Parameter(Mandatory)]
    [ValidatePattern('^[vV]?\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{7,40}$')]
    [string]$SourceCommit,

    [ValidateNotNullOrEmpty()]
    [string]$SourceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($PackageDir)
$payloadVersion = $Version -replace '^[vV]', ''
$expectedSourceCommit = $SourceCommit.ToLowerInvariant().Substring(0, 7)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "SIEM package directory does not exist: $root"
}

$executableName = if ($Rid.StartsWith('win-', [StringComparison]::Ordinal)) {
    'PtkSiemReceiver.exe'
} else {
    'PtkSiemReceiver'
}

$requiredFiles = @(
    $executableName,
    'PtkSiemReceiver.dll',
    'README.md',
    'LICENSE',
    'VERSION',
    (Join-Path 'THIRD-PARTY-LICENSES' 'OpenTelemetry-Apache-2.0.txt')
)
foreach ($relative in $requiredFiles) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "SIEM package is missing required file '$relative'."
    }
}

$versionText = [IO.File]::ReadAllText((Join-Path $root 'VERSION'))
if ($versionText -cne $payloadVersion) {
    throw "SIEM VERSION mismatch: expected '$payloadVersion', found '$versionText'."
}

if ($PSBoundParameters.ContainsKey('SourceRoot')) {
    $sourceRootPath = [IO.Path]::GetFullPath($SourceRoot)
    foreach ($pair in @(
            @((Join-Path $root 'README.md'),
              (Join-Path $sourceRootPath 'siem' 'PtkSiemReceiver' 'README.md')),
            @((Join-Path $root 'LICENSE'), (Join-Path $sourceRootPath 'LICENSE')),
            @((Join-Path $root 'THIRD-PARTY-LICENSES' 'OpenTelemetry-Apache-2.0.txt'),
              (Join-Path $sourceRootPath 'siem' 'PtkSiemReceiver' 'Protos' 'LICENSE.OpenTelemetry-Apache-2.0.txt'))
        )) {
        if (-not (Test-Path -LiteralPath $pair[1] -PathType Leaf)) {
            throw "SIEM source root is missing required file '$($pair[1])'."
        }
        $packagedHash = (Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash
        $sourceHash = (Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash
        if ($packagedHash -cne $sourceHash) {
            throw "Packaged file '$($pair[0])' does not match '$($pair[1])'."
        }
    }
}

$assemblyPath = Join-Path $root 'PtkSiemReceiver.dll'
$numericVersion = [Version](($payloadVersion -split '-', 2)[0])
$expectedAssemblyVersion = [Version]::new(
    $numericVersion.Major,
    $numericVersion.Minor,
    $numericVersion.Build,
    0)
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version
if ($assemblyVersion -ne $expectedAssemblyVersion) {
    throw "SIEM assembly version mismatch: expected '$expectedAssemblyVersion', found '$assemblyVersion'."
}

$expectedProductVersion = "$payloadVersion+$expectedSourceCommit"
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath)
if ($versionInfo.ProductVersion -cne $expectedProductVersion) {
    throw "SIEM informational version mismatch: expected '$expectedProductVersion', found '$($versionInfo.ProductVersion)'."
}

$executable = Join-Path $root $executableName
if (-not $IsWindows) {
    $mode = [IO.File]::GetUnixFileMode($executable)
    if (($mode -band [IO.UnixFileMode]::UserExecute) -eq 0) {
        throw "SIEM executable is not owner-executable: $executable"
    }
}

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $executable
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
[void]$startInfo.Environment.Remove('PTK_SIEM_CONFIG')
$process = [Diagnostics.Process]::Start($startInfo)
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()
if (-not $process.WaitForExit(60000)) {
    $process.Kill($true)
    throw 'Packaged SIEM receiver did not exit within 60 seconds without configuration.'
}
$stdout = $stdoutTask.GetAwaiter().GetResult()
$stderr = $stderrTask.GetAwaiter().GetResult()
if ($process.ExitCode -ne 1 -or $stderr -notmatch 'PTK_SIEM_CONFIG') {
    throw "Expected packaged SIEM receiver to exit 1 naming PTK_SIEM_CONFIG; exit=$($process.ExitCode), stderr='$stderr'."
}
if (-not [string]::IsNullOrWhiteSpace($stdout)) {
    throw "Packaged SIEM receiver wrote unexpected stdout: '$stdout'"
}

Write-Information "SIEM package verified: $root ($Rid, $expectedProductVersion)" `
    -InformationAction Continue
