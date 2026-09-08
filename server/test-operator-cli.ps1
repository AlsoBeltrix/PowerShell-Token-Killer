#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$LayoutRoot)
$ErrorActionPreference = 'Stop'
$layout = (Resolve-Path -LiteralPath $LayoutRoot).ProviderPath
$cli = Join-Path $layout 'bin' ($IsWindows ? 'ptk.exe' : 'ptk')
$rtk = $env:PTK_RTK_PATH ? $env:PTK_RTK_PATH : (
    Get-Command rtk -CommandType Application | Select-Object -First 1).Source
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ptk-cli-proof-' + [guid]::NewGuid().ToString('N'))
$kimi = Join-Path $fixture 'kimi'
New-Item -ItemType Directory -Path $kimi -Force | Out-Null

function Invoke-CliProof {
    param([string[]]$Arguments, [int]$ExpectedExit = 0, [switch]$WithoutRtk)
    $start = [Diagnostics.ProcessStartInfo]::new($cli)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    # No shell on PATH. Registration must use the embedded engine. Keep all
    # harness writes inside this fixture even if the operator has Kimi set up.
    $start.Environment['PATH'] = $fixture
    $start.Environment['HOME'] = $fixture
    $start.Environment['USERPROFILE'] = $fixture
    if ($IsWindows) {
        $start.Environment['HOMEDRIVE'] = [IO.Path]::GetPathRoot($fixture).TrimEnd('\')
        $start.Environment['HOMEPATH'] = $fixture.Substring(2)
    }
    $start.Environment['KIMI_CODE_HOME'] = $kimi
    $start.Environment.Remove('PSModulePath') | Out-Null
    $start.Environment['PTK_RTK_PATH'] = $WithoutRtk ? (Join-Path $fixture 'absent-rtk') : $rtk
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            throw 'Disposable CLI proof timed out.'
        }
        $result = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $ExpectedExit) {
            throw "CLI $Arguments expected exit $ExpectedExit, got $($process.ExitCode): $result $errors"
        }
        $result + $errors
    }
    finally { $process.Dispose() }
}

try {
    $identity = Invoke-CliProof -Arguments @('version') -WithoutRtk
    $provenance = Get-Content (Join-Path $layout 'BUILD-PROVENANCE.json') -Raw | ConvertFrom-Json
    $expectedIdentity = '{0}+{1}.build.{2}' -f $provenance.product_version,
        $provenance.source_commit.Substring(0, 7), $provenance.build_identity
    if ($identity.Trim() -cne $expectedIdentity) {
        throw "CLI identity disagrees with package provenance: $identity"
    }
    $null = Invoke-CliProof -Arguments @('init') -ExpectedExit 64
    if (Get-ChildItem $kimi -Force) { throw 'Unselected init wrote harness state.' }
    $null = Invoke-CliProof -Arguments @('doctor', '--home', $layout)
    $null = Invoke-CliProof -Arguments @('init', '--agent', 'kimi', '--home', $layout) -WithoutRtk -ExpectedExit 78
    if (Get-ChildItem $kimi -Force) { throw 'Dependency refusal wrote harness state.' }
    $null = Invoke-CliProof -Arguments @('init', '--agent', 'kimi', '--home', $layout, '--dry-run')
    if (Get-ChildItem $kimi -Force) { throw 'Dry-run wrote harness state.' }

    $mcpPath = Join-Path $kimi 'mcp.json'
    Set-Content $mcpPath '{"mcpServers":{"foreign":{"command":"keep-me"}}}'
    $null = Invoke-CliProof -Arguments @('init', '--agent', 'kimi', '--home', $layout)
    $mcp = Get-Content $mcpPath -Raw | ConvertFrom-Json
    $expected = Join-Path $layout 'bin' ($IsWindows ? 'PtkMcpServer.exe' : 'PtkMcpServer')
    if ($mcp.mcpServers.ptk.command -cne $expected -or $mcp.mcpServers.foreign.command -cne 'keep-me') {
        throw 'CLI registration did not preserve foreign state and select this package.'
    }
    if ((Get-Content (Join-Path $kimi 'config.toml') -Raw) -notmatch 'ptk-hook') {
        throw 'CLI did not register the native hook.'
    }
    $null = Invoke-CliProof -Arguments @('uninstall', '--agent', 'kimi', '--home', $layout) -WithoutRtk
    $mcp = Get-Content $mcpPath -Raw | ConvertFrom-Json
    if ($mcp.mcpServers.ptk -or $mcp.mcpServers.foreign.command -cne 'keep-me' -or -not (Test-Path $cli)) {
        throw 'CLI removal failed to preserve foreign state or package-owned files.'
    }
    Write-Host 'Packaged CLI passed: exact version, explicit selection, dependency refusal, no-shell embedded setup, dry-run, registration, native hook, and removal.'
}
finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
