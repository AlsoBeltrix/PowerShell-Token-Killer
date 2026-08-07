#!/usr/bin/env pwsh
#Requires -Version 7
<#
.SYNOPSIS
Smoke-tests the PtkMcpServer stdio transport end to end: starts the server as
a child process and performs an MCP initialize / tools/list /
tools/call(ptk_state) handshake over real stdin/stdout. Three launch modes:
default builds this checkout and drives the built dll; -UseRegistrationCommand
builds first, then drives the exact stdout-clean
`dotnet run --no-build --no-launch-profile` command used by a direct checkout
registration;
-ServerCommand drives an arbitrary server binary (e.g. a published
self-contained build, for release-artifact smoke tests) — multi-element
commands use array syntax: -ServerCommand dotnet,exec,PtkMcpServer.dll.

Exits 0 on success, 1 on failure. Used as part of slice verification alongside
`dotnet test`.
#>
[CmdletBinding(DefaultParameterSetName = 'BuiltDll')]
param(
    [int]$TimeoutSec = 30,
    # Build first, then drive the server with the exact stdout-clean
    # `dotnet run --no-build --no-launch-profile` command used by a direct
    # checkout registration, instead of dotnet exec against a prebuilt dll. A
    # build-on-launch command is not MCP-safe: restore/build warnings are
    # written to protocol stdout.
    [Parameter(ParameterSetName = 'Registration', Mandatory)]
    [switch]$UseRegistrationCommand,
    # Drive an arbitrary server binary instead of building this checkout:
    # first element is the executable, remaining elements are its arguments.
    # Named array parameter: multi-element commands need PowerShell array
    # syntax (-ServerCommand dotnet,exec,Server.dll) from a command context.
    # Space-separated tokens after -ServerCommand do NOT collect (binding
    # errors loudly), and `pwsh -File` binds literally — from -File, pass a
    # single executable path. The child runs in this script's current
    # PowerShell location (pinned below), so relative paths resolve where
    # the caller expects.
    [Parameter(ParameterSetName = 'ServerCommand', Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]]$ServerCommand
)

$ErrorActionPreference = 'Stop'
$serverDir = Split-Path -Parent $PSCommandPath

$mode = $PSCmdlet.ParameterSetName
# An explicit -UseRegistrationCommand:$false means the default built-dll mode,
# as it did before parameter sets were introduced (set membership alone would
# select the Registration branch regardless of the switch's value).
if ($mode -eq 'Registration' -and -not $UseRegistrationCommand) { $mode = 'BuiltDll' }

$psi = [System.Diagnostics.ProcessStartInfo]::new()
switch ($mode) {
    'ServerCommand' {
        Write-Host "Starting via server command: $($ServerCommand -join ' ')"
        $exe = $ServerCommand[0]
        # Resolve path-shaped executables against this script's PowerShell
        # location: Process.Start resolves a relative FileName against the
        # process-wide cwd (not $PWD), even when WorkingDirectory is set.
        # A bare command name (no separator) passes through to PATH lookup.
        if ($exe -match '[\\/]') {
            $exe = (Resolve-Path -LiteralPath $exe).ProviderPath
        }
        $psi.FileName = $exe
        foreach ($a in ($ServerCommand | Select-Object -Skip 1)) {
            $psi.ArgumentList.Add($a)
        }
    }
    'Registration' {
        $proj = Join-Path $serverDir 'PtkMcpServer'
        Write-Host 'Building server before stdout-clean registration launch...'
        dotnet build $proj -v q --nologo | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Error 'Build failed.'; exit 1 }

        Write-Host 'Starting via dotnet run --no-build --no-launch-profile (registration command)...'
        $psi.FileName = 'dotnet'
        foreach ($a in @(
                'run',
                '--no-build',
                '--no-launch-profile',
                '-v',
                'q',
                '--project',
                $proj)) {
            $psi.ArgumentList.Add($a)
        }
    }
    default {
        $proj = Join-Path $serverDir 'PtkMcpServer'
        Write-Host 'Building server...'
        dotnet build $proj -v q --nologo | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Error 'Build failed.'; exit 1 }

        $dll = Join-Path $proj 'bin/Debug/net10.0/PtkMcpServer.dll'
        if (-not (Test-Path $dll)) { Write-Error "Built assembly not found at $dll"; exit 1 }

        $psi.FileName = 'dotnet'
        $psi.ArgumentList.Add('exec')
        $psi.ArgumentList.Add($dll)
    }
}
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$auditRoot = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) (
    '.ptk/test-handshake-audit-' + [guid]::NewGuid().ToString('N'))
$outputParent = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) (
    '.ptk/test-handshake-output-' + [guid]::NewGuid().ToString('N'))
$psi.Environment['PTK_AUDIT_ROOT'] = $auditRoot
$psi.Environment['PTK_OUTPUT_ROOT'] = $outputParent
# Pin the child to this script's PowerShell location: Process.Start would
# otherwise resolve against the process-wide cwd, which an interactive
# session's Set-Location does not change.
$psi.WorkingDirectory = (Get-Location).ProviderPath
$proc = [System.Diagnostics.Process]::Start($psi)

function Send-Rpc {
    param([hashtable]$Message)
    $json = $Message | ConvertTo-Json -Depth 12 -Compress
    $proc.StandardInput.WriteLine($json)
    $proc.StandardInput.Flush()
}

function Read-RpcResponse {
    # Reads stdout lines until a message with the given id arrives; skips
    # notifications (messages without an id) the server may emit in between.
    param([int]$Id)
    while ($true) {
        $task = $proc.StandardOutput.ReadLineAsync()
        if (-not $task.Wait($TimeoutSec * 1000)) {
            throw "Timed out after ${TimeoutSec}s waiting for response id=$Id."
        }
        $line = $task.Result
        if ($null -eq $line) { throw "Server closed stdout while waiting for id=$Id." }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $msg = $line | ConvertFrom-Json
        if ($msg.PSObject.Properties['id'] -and $msg.id -eq $Id) { return $msg }
        Write-Host "  (skipped notification: $($msg.method))"
    }
}

function Stop-ServerProcess {
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if (-not $Process.HasExited) {
        try {
            # EOF is the stdio transport's graceful shutdown signal. Closing
            # the writer also proves that harness-owned resources are disposed
            # before this smoke test inspects their protected roots.
            $Process.StandardInput.Close()
        }
        catch {
            if (-not $Process.HasExited) {
                Write-Host "$Label stdin close failed: $_"
            }
        }
    }

    if ($Process.HasExited -or $Process.WaitForExit($TimeoutSec * 1000)) {
        return $true
    }

    Write-Host "$Label did not exit within ${TimeoutSec}s after stdin EOF; killing its process tree."
    try {
        $Process.Kill($true)
    }
    catch {
        if (-not $Process.HasExited) { throw }
    }
    if (-not $Process.HasExited -and -not $Process.WaitForExit($TimeoutSec * 1000)) {
        throw "$Label did not exit within ${TimeoutSec}s after the kill fallback."
    }
    return $false
}

# Whether an enumerable artifact entry is a live link. The store unlinks
# every artifact while its write handle stays open, and a classic NTFS
# delete stays pending -- name still enumerable -- until that handle
# closes. The product documents that namespace disappearance is not a
# valid success test on Windows
# (SecureAuditStorage.DeleteRetainedProtectedFile); every LIVE-phase count
# in this script must honor the same rule. Probe the name: a
# delete-pending entry refuses to open with access-denied and is no live
# link, while one that opens -- or fails any other way, such as a sharing
# violation -- is a real stray and still counts. Server 2019 fails raw
# counts deterministically; Server 2022's POSIX-delete default hides it
# (GitHub #43, both failures). Post-exit counts run after handles close
# and must keep asserting true disappearance on every platform -- never
# route them through this probe.
function Test-LiveArtifactEntry {
    param([Parameter(Mandatory)][System.IO.FileInfo]$File)
    if (-not $IsWindows) { return $true }
    try {
        ([System.IO.File]::Open(
            $File.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite -bor
                [System.IO.FileShare]::Delete)).Dispose()
        return $true
    }
    catch [System.UnauthorizedAccessException] { return $false }
    catch [System.IO.FileNotFoundException] { return $false }
    catch { return $true }
}

function Assert-LiveOutputRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Parent,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Parent -PathType Container)) {
        throw "$Label did not create its configured PTK_OUTPUT_ROOT parent"
    }
    $children = @(Get-ChildItem -LiteralPath $Parent -Force)
    $serverRoots = @($children | Where-Object {
        $_.PSIsContainer -and $_.Name -match '^server-\d+-[0-9a-f]{32}$'
    })
    if ($children.Count -ne 1 -or $serverRoots.Count -ne 1) {
        throw "$Label output parent contained $($children.Count) children and $($serverRoots.Count) valid server roots"
    }

    $serverRoot = $serverRoots[0]
    if (($serverRoot.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label output server root was a link or reparse point"
    }
    if (-not $IsWindows) {
        $expectedMode = [System.IO.UnixFileMode]::UserRead -bor
            [System.IO.UnixFileMode]::UserWrite -bor
            [System.IO.UnixFileMode]::UserExecute
        $actualMode = [System.IO.File]::GetUnixFileMode($serverRoot.FullName)
        if ($actualMode -ne $expectedMode) {
            throw "$Label output server root mode was $actualMode instead of $expectedMode"
        }
    }
    $rootFiles = @(
        Get-ChildItem -LiteralPath $serverRoot.FullName -Recurse -Force -File)
    $rootFiles = @($rootFiles | Where-Object {
        $_.Name -notlike 'artifact-*.out' -or (Test-LiveArtifactEntry -File $_)
    })
    $ownerMarkers = @($rootFiles | Where-Object Name -CEQ 'owner.v1.json')
    $namedArtifacts = @($rootFiles | Where-Object Name -Like 'artifact-*.out')
    if ($rootFiles.Count -ne 1 -or
        $ownerMarkers.Count -ne 1 -or
        $namedArtifacts.Count -ne 0) {
        throw "$Label output server root did not contain exactly its live owner marker and no named artifacts"
    }
}

$onlineToken = 'ptk-online-' + [guid]::NewGuid().ToString('N')
$onPremToken = 'ptk-onprem-' + [guid]::NewGuid().ToString('N')
$escapedOnlineToken = [regex]::Escape($onlineToken)
$escapedOnPremToken = [regex]::Escape($onPremToken)
$quotedOnlineToken = "'" + $onlineToken.Replace("'", "''") + "'"
$quotedOnPremToken = "'" + $onPremToken.Replace("'", "''") + "'"
$onlineSeedScript = (
    '$shared = ' + $quotedOnlineToken +
    '; function Get-PtkOverlap { ' + $quotedOnlineToken + ' }; $shared')
$onPremSeedScript = (
    '$shared = ' + $quotedOnPremToken +
    '; function Get-PtkOverlap { ' + $quotedOnPremToken + ' }; $shared')
$executionScript = 'if (-not (Test-Path Variable:executionCount)) { $executionCount = 0 }; $executionCount++; $executionCount'
$mainExitedGracefully = $false
$failed = $false
try {
    Send-Rpc @{
        jsonrpc = '2.0'; id = 1; method = 'initialize'
        params = @{
            protocolVersion = '2025-06-18'
            capabilities    = @{}
            clientInfo      = @{ name = 'ptk-handshake'; version = '0.0.0' }
        }
    }
    $init = Read-RpcResponse -Id 1
    if (-not $init.result.serverInfo.name) {
        throw "initialize failed: $($init | ConvertTo-Json -Depth 12 -Compress)"
    }
    Write-Host "initialize ok: $($init.result.serverInfo.name) $($init.result.serverInfo.version)"

    Send-Rpc @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    Send-Rpc @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
    $tools = Read-RpcResponse -Id 2
    $names = @($tools.result.tools.name | Sort-Object)
    $expectedNames = @('ptk_invoke', 'ptk_output', 'ptk_reset', 'ptk_session', 'ptk_state')
    if ($names.Count -ne $expectedNames.Count -or
        ($names -join ',') -cne ($expectedNames -join ',')) {
        throw "tools/list surface changed; expected=$($expectedNames -join ','); actual=$($names -join ',')"
    }

    $expectedInputFields = @{
        ptk_invoke  = @('script', 'raw', 'route', 'timeoutSeconds', 'session')
        ptk_output  = @('handle', 'action', 'offset', 'maxBytes', 'pattern', 'session')
        ptk_reset   = @('session')
        ptk_session = @('action', 'name')
        ptk_state   = @('listAvailable', 'session')
    }
    $expectedRequiredFields = @{
        ptk_invoke  = @('script')
        ptk_output  = @()
        ptk_reset   = @()
        ptk_session = @('action')
        ptk_state   = @()
    }
    foreach ($tool in @($tools.result.tools)) {
        $inputFields = @($tool.inputSchema.properties.PSObject.Properties.Name | Where-Object { $null -ne $_ })
        $expectedFields = @($expectedInputFields[$tool.name] | Where-Object { $null -ne $_ })
        $missingFields = @($expectedFields | Where-Object { $inputFields -cnotcontains $_ })
        $unexpectedFields = @($inputFields | Where-Object { $expectedFields -cnotcontains $_ })
        if ($missingFields.Count -gt 0 -or $unexpectedFields.Count -gt 0) {
            throw "$($tool.name) MCP input fields changed; missing=$($missingFields -join ','); unexpected=$($unexpectedFields -join ',')"
        }
        $requiredFields = @($tool.inputSchema.required | Where-Object { $null -ne $_ })
        $expectedRequired = @($expectedRequiredFields[$tool.name] | Where-Object { $null -ne $_ })
        $missingRequired = @($expectedRequired | Where-Object { $requiredFields -cnotcontains $_ })
        $unexpectedRequired = @($requiredFields | Where-Object { $expectedRequired -cnotcontains $_ })
        if ($missingRequired.Count -gt 0 -or $unexpectedRequired.Count -gt 0) {
            throw "$($tool.name) MCP required fields changed; missing=$($missingRequired -join ','); unexpected=$($unexpectedRequired -join ',')"
        }
        foreach ($hostOnlyField in @('auditContext', 'cancellationToken', 'outputStore', 'runtime')) {
            if ($inputFields -contains $hostOnlyField) {
                throw "host-only $hostOnlyField leaked into the $($tool.name) MCP input schema"
            }
        }
    }

    $outputTool = @($tools.result.tools | Where-Object name -CEQ 'ptk_output')
    if ($outputTool.Count -ne 1) {
        throw "tools/list returned $($outputTool.Count) ptk_output definitions"
    }
    $outputSchema = $outputTool[0].inputSchema
    $outputFields = @($outputSchema.properties.PSObject.Properties.Name | Sort-Object)
    if (($outputFields -join ',') -ne 'action,handle,maxBytes,offset,pattern,session') {
        throw "ptk_output input fields drifted: $($outputFields -join ', ')"
    }
    $outputActions = @($outputSchema.properties.action.enum)
    if (($outputActions -join ',') -ne 'read,search,status,list') {
        throw "ptk_output action enum drifted: $($outputActions -join ', ')"
    }
    if ($outputSchema.properties.offset.minimum -ne 0 -or
        $outputSchema.properties.maxBytes.minimum -ne 1 -or
        $outputSchema.properties.maxBytes.maximum -ne 65536) {
        throw 'ptk_output bounds drifted'
    }
    if ($outputSchema.properties.action.default -ne 'read' -or
        $outputSchema.properties.offset.default -ne 0 -or
        $outputSchema.properties.maxBytes.default -ne 16384 -or
        -not $outputSchema.properties.pattern.PSObject.Properties['default'] -or
        $null -ne $outputSchema.properties.pattern.default) {
        throw 'ptk_output defaults drifted'
    }

    $sessionTool = @($tools.result.tools | Where-Object name -CEQ 'ptk_session')
    $sessionActions = @($sessionTool[0].inputSchema.properties.action.enum)
    if ($sessionTool.Count -ne 1 -or
        ($sessionActions -join ',') -ne 'list,open,close') {
        throw "ptk_session action enum drifted: $($sessionActions -join ', ')"
    }
    Write-Host "tools/list ok: exactly $($names.Count) tools ($($names -join ', '))"

    Send-Rpc @{
        jsonrpc = '2.0'; id = 3; method = 'tools/call'
        params = @{ name = 'ptk_state'; arguments = @{} }
    }
    $coldState = (Read-RpcResponse -Id 3).result
    $coldStateText = $coldState.content[0].text
    if ($coldState.isError -or
        $coldStateText -notmatch '(?m)^ptk supervisor: pid=\d+ sessions=1/8\r?$' -or
        $coldStateText -notmatch '(?m)^session=default state=cold worker_pid=none active=false ' -or
        $coldStateText -notmatch '(?m)^audit: disabled\r?$' -or
        $coldStateText -notmatch 'runspace: unavailable \(detail=session_cold\)') {
        throw "cold default ptk_state returned unexpected text: '$coldStateText'"
    }
    Write-Host 'ptk_state ok: default remained cold and audit is disabled'

    Send-Rpc @{
        jsonrpc = '2.0'; id = 4; method = 'tools/call'
        params = @{
            name = 'ptk_invoke'
            arguments = @{ script = '$neverDispatched = 1'; session = 'missing-session' }
        }
    }
    $unknownInvoke = (Read-RpcResponse -Id 4).result.content[0].text
    if ($unknownInvoke -notmatch '^\[ptk invoke\] refused session=missing-session detail=session_not_found;' -or
        $unknownInvoke -notmatch 'Nothing was executed\.$') {
        throw "unknown session did not refuse before dispatch: '$unknownInvoke'"
    }

    Send-Rpc @{
        jsonrpc = '2.0'; id = 5; method = 'tools/call'
        params = @{ name = 'ptk_session'; arguments = @{ action = 'list' } }
    }
    $afterRefusalList = (Read-RpcResponse -Id 5).result.content[0].text
    if ([regex]::Matches($afterRefusalList, '(?m)^session=').Count -ne 1 -or
        $afterRefusalList -notmatch '(?m)^session=default state=cold worker_pid=none ') {
        throw "unknown-session refusal changed the session registry: '$afterRefusalList'"
    }
    Write-Host 'unknown-session refusal ok: no worker started and no fallback occurred'

    Send-Rpc @{
        jsonrpc = '2.0'; id = 6; method = 'tools/call'
        params = @{
            name = 'ptk_session'
            arguments = @{ action = 'open'; name = 'sample-online' }
        }
    }
    $onlineOpen = (Read-RpcResponse -Id 6).result
    $onlineOpenText = $onlineOpen.content[0].text
    $onlinePidMatch = [regex]::Match(
        $onlineOpenText,
        '(?m)^session=sample-online state=ready worker_pid=(\d+) active=false ')
    if ($onlineOpen.isError -or -not $onlinePidMatch.Success) {
        throw "sample-online did not open a ready worker: '$onlineOpenText'"
    }
    $onlinePid = [int]$onlinePidMatch.Groups[1].Value

    Send-Rpc @{
        jsonrpc = '2.0'; id = 7; method = 'tools/call'
        params = @{
            name = 'ptk_session'
            arguments = @{ action = 'open'; name = 'sample-onprem' }
        }
    }
    $onPremOpen = (Read-RpcResponse -Id 7).result
    $onPremOpenText = $onPremOpen.content[0].text
    $onPremPidMatch = [regex]::Match(
        $onPremOpenText,
        '(?m)^session=sample-onprem state=ready worker_pid=(\d+) active=false ')
    if ($onPremOpen.isError -or -not $onPremPidMatch.Success) {
        throw "sample-onprem did not open a ready worker: '$onPremOpenText'"
    }
    $onPremPid = [int]$onPremPidMatch.Groups[1].Value
    if ($onlinePid -eq $onPremPid) {
        throw "named sessions shared worker pid $onlinePid"
    }
    Write-Host ("named worker topology ok: two local test sessions got separate " +
        "workers (sample-online pid=$onlinePid, sample-onprem pid=$onPremPid)")

    Send-Rpc @{
        jsonrpc = '2.0'; id = 8; method = 'tools/call'
        params = @{
            name = 'ptk_invoke'
            arguments = @{ script = $onlineSeedScript; session = 'sample-online' }
        }
    }
    $onlineSeed = (Read-RpcResponse -Id 8).result.content[0].text
    if ($onlineSeed -notmatch "(?m)^$escapedOnlineToken\r?$") {
        throw "sample-online seed failed: '$onlineSeed'"
    }

    Send-Rpc @{
        jsonrpc = '2.0'; id = 9; method = 'tools/call'
        params = @{
            name = 'ptk_invoke'
            arguments = @{ script = $onPremSeedScript; session = 'sample-onprem' }
        }
    }
    $onPremSeed = (Read-RpcResponse -Id 9).result.content[0].text
    if ($onPremSeed -notmatch "(?m)^$escapedOnPremToken\r?$") {
        throw "sample-onprem seed failed: '$onPremSeed'"
    }

    Send-Rpc @{
        jsonrpc = '2.0'; id = 10; method = 'tools/call'
        params = @{
            name = 'ptk_invoke'
            arguments = @{
                script = '$shared; Get-PtkOverlap'
                session = 'sample-online'
            }
        }
    }
    $onlineRead = (Read-RpcResponse -Id 10).result.content[0].text
    if ([regex]::Matches(
            $onlineRead,
            "(?m)^$escapedOnlineToken\r?$").Count -ne 2 -or
        $onlineRead -match $escapedOnPremToken) {
        throw "sample-online variable/function state was not isolated: '$onlineRead'"
    }

    Send-Rpc @{
        jsonrpc = '2.0'; id = 11; method = 'tools/call'
        params = @{
            name = 'ptk_invoke'
            arguments = @{
                script = '$shared; Get-PtkOverlap'
                session = 'sample-onprem'
            }
        }
    }
    $onPremRead = (Read-RpcResponse -Id 11).result.content[0].text
    if ([regex]::Matches(
            $onPremRead,
            "(?m)^$escapedOnPremToken\r?$").Count -ne 2 -or
        $onPremRead -match $escapedOnlineToken) {
        throw "sample-onprem variable/function state was not isolated: '$onPremRead'"
    }
    Write-Host 'warm-state isolation ok: identical variable/function names retained different behavior'

    Send-Rpc @{
        jsonrpc = '2.0'; id = 12; method = 'tools/call'
        params = @{
            name = 'ptk_invoke'
            arguments = @{ script = $executionScript; session = 'sample-online' }
        }
    }
    $executionResult = (Read-RpcResponse -Id 12).result.content[0].text
    Send-Rpc @{
        jsonrpc = '2.0'; id = 13; method = 'tools/call'
        params = @{
            name = 'ptk_invoke'
            arguments = @{ script = '$executionCount'; session = 'sample-online' }
        }
    }
    $executionState = (Read-RpcResponse -Id 13).result.content[0].text
    if ($executionResult -notmatch '(?m)^1\r?$' -or
        $executionState -notmatch '(?m)^1\r?$') {
        throw "one accepted invoke did not execute exactly once: first='$executionResult'; state='$executionState'"
    }
    Write-Host 'exactly-once dispatch ok: one accepted invoke changed warm state once'

    $handleMatches = [regex]::Matches(
        $onlineRead,
        '(?m)^recovery=available: ptk_output handle=(ptko_[A-Za-z0-9_-]+)\r?$')
    if ($handleMatches.Count -ne 1) {
        throw "ptk_invoke returned $($handleMatches.Count) recovery handles; text was '$onlineRead'."
    }
    $recoveryHandle = $handleMatches[0].Groups[1].Value

    Send-Rpc @{
        jsonrpc = '2.0'; id = 14; method = 'tools/call'
        params = @{ name = 'ptk_reset'; arguments = @{ session = 'sample-online' } }
    }
    $resetText = (Read-RpcResponse -Id 14).result.content[0].text
    if ($resetText -notmatch '(?m)^\[ptk reset\] completed\r?$' -or
        $resetText -notmatch '(?m)^session=sample-online state=ready worker_pid=\d+ ') {
        throw "selected-session reset failed: '$resetText'"
    }

    Send-Rpc @{
        jsonrpc = '2.0'; id = 15; method = 'tools/call'
        params = @{
            name = 'ptk_invoke'
            arguments = @{ script = '$shared'; session = 'sample-online' }
        }
    }
    $onlineAfterReset = (Read-RpcResponse -Id 15).result.content[0].text
    if ($onlineAfterReset -match $escapedOnlineToken -or
        $onlineAfterReset -match $escapedOnPremToken) {
        throw "reset retained or crossed warm state: '$onlineAfterReset'"
    }

    Send-Rpc @{
        jsonrpc = '2.0'; id = 16; method = 'tools/call'
        params = @{
            name = 'ptk_invoke'
            arguments = @{
                script = '$shared; Get-PtkOverlap'
                session = 'sample-onprem'
            }
        }
    }
    $onPremAfterReset = (Read-RpcResponse -Id 16).result.content[0].text
    if ([regex]::Matches(
            $onPremAfterReset,
            "(?m)^$escapedOnPremToken\r?$").Count -ne 2) {
        throw "reset of sample-online damaged sample-onprem: '$onPremAfterReset'"
    }
    Write-Host 'selected reset ok: one worker lost warm state and the other did not'

    Send-Rpc @{
        jsonrpc = '2.0'; id = 17; method = 'tools/call'
        params = @{
            name = 'ptk_session'
            arguments = @{ action = 'close'; name = 'sample-online' }
        }
    }
    $closeText = (Read-RpcResponse -Id 17).result.content[0].text
    if ($closeText -ne '[ptk session] closed session=sample-online') {
        throw "sample-online close failed: '$closeText'"
    }

    Send-Rpc @{
        jsonrpc = '2.0'; id = 18; method = 'tools/call'
        params = @{
            name = 'ptk_invoke'
            arguments = @{ script = '$neverDispatched = 2'; session = 'sample-online' }
        }
    }
    $closedInvoke = (Read-RpcResponse -Id 18).result.content[0].text
    if ($closedInvoke -notmatch '^\[ptk invoke\] refused session=sample-online detail=session_not_found;' -or
        $closedInvoke -notmatch 'Nothing was executed\.$') {
        throw "closed session did not refuse before dispatch: '$closedInvoke'"
    }

    Send-Rpc @{
        jsonrpc = '2.0'; id = 19; method = 'tools/call'
        params = @{ name = 'ptk_output'; arguments = @{ handle = $recoveryHandle } }
    }
    $outputRead = (Read-RpcResponse -Id 19).result
    $outputText = $outputRead.content[0].text
    $outputHeader = ($outputText -split '\r?\n', 2)[0]
    if ($outputRead.isError -or
        $outputHeader -notmatch '^\[ptk output\] action=read state=available complete=true bytes=\d+ provenance=powershell_objects offset=0 next_offset=\d+ bytes_returned=\d+$' -or
        $outputText -notmatch "(?m)^$escapedOnlineToken\r?$") {
        throw "sealed output did not survive reset and close: '$outputText'"
    }
    Write-Host 'output recovery ok: sealed handle survived worker reset and session close'

    Send-Rpc @{
        jsonrpc = '2.0'; id = 20; method = 'tools/call'
        params = @{ name = 'ptk_session'; arguments = @{ action = 'list' } }
    }
    $finalList = (Read-RpcResponse -Id 20).result.content[0].text
    if ([regex]::Matches($finalList, '(?m)^session=').Count -ne 2 -or
        $finalList -notmatch '(?m)^session=default state=cold worker_pid=none ' -or
        $finalList -notmatch '(?m)^session=sample-onprem state=ready worker_pid=\d+ ' -or
        $finalList -match '(?m)^session=sample-online ') {
        throw "final session registry was wrong: '$finalList'"
    }
    Write-Host 'session close ok: closed alias stayed absent and remaining session stayed ready'

    Assert-LiveOutputRoot -Parent $outputParent -Label 'main server'

}
catch {
    Write-Host "HANDSHAKE FAILED: $_"
    $failed = $true
}
finally {
    try {
        $mainExitedGracefully = Stop-ServerProcess -Process $proc -Label 'main server'
        if (-not $mainExitedGracefully) {
            Write-Host 'HANDSHAKE FAILED: main server required the kill fallback during shutdown.'
            $failed = $true
        }
    }
    catch {
        Write-Host "HANDSHAKE FAILED: main server shutdown failed: $_"
        $failed = $true
    }
    $serverError = if ($proc.HasExited) {
        $proc.StandardError.ReadToEnd()
    }
    else {
        'server process remained alive after bounded shutdown attempts'
    }
    if (Test-Path -LiteralPath $auditRoot) {
        Write-Host 'AUDIT DISABLEMENT VERIFICATION FAILED: the runtime created the configured audit root.'
        $failed = $true
    }
    else {
        Write-Host 'audit disablement ok: runtime created no audit or exact-script storage'
    }
    if ($mainExitedGracefully) {
        try {
            $retainedOutputFiles = if (Test-Path -LiteralPath $outputParent) {
                @(Get-ChildItem -LiteralPath $outputParent -Recurse -Force -File)
            }
            else {
                @()
            }
            if ($retainedOutputFiles.Count -ne 0) {
                throw "graceful shutdown retained $($retainedOutputFiles.Count) output artifact file(s)"
            }
            Write-Host 'output cleanup ok: graceful main exit retained no artifact files'
        }
        catch {
            Write-Host "OUTPUT CLEANUP VERIFICATION FAILED: $_"
            $failed = $true
        }
    }
    if ($failed -and -not [string]::IsNullOrWhiteSpace($serverError)) {
        Write-Host "server stderr:`n$serverError"
    }
    $proc.Dispose()
    if (Test-Path -LiteralPath $auditRoot) {
        Remove-Item -LiteralPath $auditRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $outputParent) {
        Remove-Item -LiteralPath $outputParent -Recurse -Force
    }
}

# A broken legacy audit root must not affect ordinary execution.
if (-not $failed) {
    $diagnosticParent = Join-Path (
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) (
        '.ptk/test-handshake-diagnostic-' + [guid]::NewGuid().ToString('N'))
    $blocker = Join-Path $diagnosticParent 'not-a-directory'
    $marker = Join-Path $diagnosticParent 'effect-ran'
    $diagnosticOutputParent = Join-Path $diagnosticParent 'output'
    $proc = $null
    try {
        [void](New-Item -ItemType Directory -Path $diagnosticParent)
        Set-Content -LiteralPath $blocker -Value 'blocked'
        $psi.Environment['PTK_AUDIT_ROOT'] = Join-Path $blocker 'audit'
        $psi.Environment['PTK_OUTPUT_ROOT'] = $diagnosticOutputParent
        $proc = [System.Diagnostics.Process]::Start($psi)

        Send-Rpc @{
            jsonrpc = '2.0'; id = 101; method = 'initialize'
            params = @{
                protocolVersion = '2025-06-18'
                capabilities    = @{}
                clientInfo      = @{ name = 'ptk-handshake-diagnostic'; version = '0.0.0' }
            }
        }
        $diagnosticInit = Read-RpcResponse -Id 101
        if (-not $diagnosticInit.result.serverInfo.name) {
            throw 'diagnostic-only initialize failed'
        }
        Send-Rpc @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
        Send-Rpc @{ jsonrpc = '2.0'; id = 102; method = 'tools/list' }
        $diagnosticTools = @((Read-RpcResponse -Id 102).result.tools.name)
        if ($diagnosticTools -notcontains 'ptk_state' -or
            $diagnosticTools -notcontains 'ptk_invoke') {
            throw 'diagnostic-only tools/list was incomplete'
        }

        Send-Rpc @{
            jsonrpc = '2.0'; id = 103; method = 'tools/call'
            params = @{ name = 'ptk_state'; arguments = @{} }
        }
        $diagnosticState = (Read-RpcResponse -Id 103).result.content[0].text
        if ($diagnosticState -notmatch '(?m)^ptk supervisor: pid=\d+ sessions=1/8\r?$' -or
            $diagnosticState -notmatch '(?m)^session=default state=cold worker_pid=none ' -or
            $diagnosticState -notmatch '(?m)^audit: disabled\r?$') {
            throw "audit-independent ptk_state omitted expected state: '$diagnosticState'"
        }

        $literalMarker = "'" + $marker.Replace("'", "''") + "'"
        $effectToken = 'audit-independent-effect'
        Send-Rpc @{
            jsonrpc = '2.0'; id = 104; method = 'tools/call'
            params = @{
                name = 'ptk_invoke'
                arguments = @{
                    script = "Set-Content -LiteralPath $literalMarker -Value ran; '$effectToken'"
                    route = 'pwsh'
                }
            }
        }
        $diagnosticInvoke = (Read-RpcResponse -Id 104).result
        if ($diagnosticInvoke.isError -or
            $diagnosticInvoke.content[0].text -notmatch $effectToken -or
            -not (Test-Path -LiteralPath $marker)) {
            throw 'unwritable legacy audit root blocked a valid invoke'
        }
        Assert-LiveOutputRoot -Parent $diagnosticOutputParent -Label 'audit-independent server'
        Write-Host 'audit independence ok: unwritable legacy root did not block state or execution'
    }
    catch {
        Write-Host "DIAGNOSTIC HANDSHAKE FAILED: $_"
        $failed = $true
    }
    finally {
        if ($null -ne $proc) {
            try {
                $diagnosticExitedGracefully = Stop-ServerProcess -Process $proc -Label 'diagnostic server'
                if (-not $diagnosticExitedGracefully) {
                    Write-Host 'DIAGNOSTIC HANDSHAKE FAILED: server required the kill fallback during shutdown.'
                    $failed = $true
                }
            }
            catch {
                Write-Host "DIAGNOSTIC HANDSHAKE FAILED: server shutdown failed: $_"
                $failed = $true
            }
            $diagnosticError = if ($proc.HasExited) {
                $proc.StandardError.ReadToEnd()
            }
            else {
                'diagnostic server remained alive after bounded shutdown attempts'
            }
            if ($failed -and -not [string]::IsNullOrWhiteSpace($diagnosticError)) {
                Write-Host "diagnostic server stderr:`n$diagnosticError"
            }
            $proc.Dispose()
        }
        if (Test-Path -LiteralPath $diagnosticParent) {
            Remove-Item -LiteralPath $diagnosticParent -Recurse -Force
        }
    }
}

# A hard-killed harness cannot run OutputStore.Dispose. Recovery must therefore
# use an already-unlinked artifact through its supervisor-owned open handle.
if (-not $failed) {
    $hardKillAuditRoot = Join-Path (
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) (
        '.ptk/test-handshake-hard-kill-audit-' + [guid]::NewGuid().ToString('N'))
    $hardKillOutputParent = Join-Path (
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) (
        '.ptk/test-handshake-hard-kill-output-' + [guid]::NewGuid().ToString('N'))
    $hardKillToken = 'ptk-hard-kill-' + [guid]::NewGuid().ToString('N')
    $hardKillScript = "'" + $hardKillToken.Replace("'", "''") + "'"
    $proc = $null
    try {
        $psi.Environment['PTK_AUDIT_ROOT'] = $hardKillAuditRoot
        $psi.Environment['PTK_OUTPUT_ROOT'] = $hardKillOutputParent
        $proc = [System.Diagnostics.Process]::Start($psi)

        Send-Rpc @{
            jsonrpc = '2.0'; id = 201; method = 'initialize'
            params = @{
                protocolVersion = '2025-06-18'
                capabilities    = @{}
                clientInfo      = @{ name = 'ptk-handshake-hard-kill'; version = '0.0.0' }
            }
        }
        $hardKillInit = Read-RpcResponse -Id 201
        if (-not $hardKillInit.result.serverInfo.name) {
            throw 'hard-kill initialize failed'
        }
        Send-Rpc @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
        Send-Rpc @{
            jsonrpc = '2.0'; id = 202; method = 'tools/call'
            params = @{
                name = 'ptk_invoke'
                arguments = @{ script = $hardKillScript; route = 'pwsh' }
            }
        }
        $hardKillInvoke = (Read-RpcResponse -Id 202).result
        $hardKillText = $hardKillInvoke.content[0].text
        $hardKillHandles = [regex]::Matches(
            $hardKillText,
            '(?m)^recovery=available: ptk_output handle=(ptko_[A-Za-z0-9_-]+)\r?$')
        if ($hardKillInvoke.isError -or
            $hardKillText -notmatch "(?m)^$([regex]::Escape($hardKillToken))\r?$" -or
            $hardKillHandles.Count -ne 1) {
            throw 'hard-kill invoke did not produce one recoverable PowerShell artifact'
        }
        $hardKillHandle = $hardKillHandles[0].Groups[1].Value
        Send-Rpc @{
            jsonrpc = '2.0'; id = 203; method = 'tools/call'
            params = @{ name = 'ptk_output'; arguments = @{ handle = $hardKillHandle } }
        }
        $hardKillRead = (Read-RpcResponse -Id 203).result
        if ($hardKillRead.isError -or
            $hardKillRead.content[0].text -notmatch "(?m)^$([regex]::Escape($hardKillToken))\r?$") {
            throw 'hard-kill guard could not read the live anonymous recovery artifact'
        }
        Assert-LiveOutputRoot -Parent $hardKillOutputParent -Label 'hard-kill server'

        # A live-phase count: the recovery artifact's write handle is still
        # open in the server, so on classic-delete Windows its unlinked name
        # is still enumerable here -- probe it like every live count
        # (GitHub #43, second failure; the post-kill count below stays raw).
        $liveArtifactFiles = if (Test-Path -LiteralPath $hardKillOutputParent) {
            @(Get-ChildItem -LiteralPath $hardKillOutputParent -Recurse -Force -File |
                Where-Object { $_.Name -like 'artifact-*.out' -and
                    (Test-LiveArtifactEntry -File $_) })
        }
        else {
            @()
        }
        if ($liveArtifactFiles.Count -ne 0) {
            throw "live anonymous recovery retained $($liveArtifactFiles.Count) named artifact file(s)"
        }

        $proc.Kill($true)
        if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
            throw "hard-killed server did not exit within ${TimeoutSec}s"
        }

        $remainingArtifactFiles = if (Test-Path -LiteralPath $hardKillOutputParent) {
            @(Get-ChildItem -LiteralPath $hardKillOutputParent -Recurse -Force -File |
                Where-Object Name -Like 'artifact-*.out')
        }
        else {
            @()
        }
        if ($remainingArtifactFiles.Count -ne 0) {
            throw "hard-kill retained $($remainingArtifactFiles.Count) output artifact file(s)"
        }
        Write-Host 'hard-kill output cleanup ok: anonymous live recovery left no named artifact before or after exit'
    }
    catch {
        Write-Host "HARD-KILL OUTPUT CLEANUP FAILED: $_"
        $failed = $true
    }
    finally {
        if ($null -ne $proc) {
            if (-not $proc.HasExited) {
                try { [void](Stop-ServerProcess -Process $proc -Label 'hard-kill guard server') }
                catch {
                    Write-Host "HARD-KILL OUTPUT CLEANUP FAILED: bounded process cleanup failed: $_"
                    $failed = $true
                }
            }
            $hardKillError = if ($proc.HasExited) {
                $proc.StandardError.ReadToEnd()
            }
            else {
                'hard-kill guard server remained alive after bounded cleanup attempts'
            }
            if ($failed -and -not [string]::IsNullOrWhiteSpace($hardKillError)) {
                Write-Host "hard-kill server stderr:`n$hardKillError"
            }
            $proc.Dispose()
        }
        if (Test-Path -LiteralPath $hardKillAuditRoot) {
            Remove-Item -LiteralPath $hardKillAuditRoot -Recurse -Force
        }
        if (Test-Path -LiteralPath $hardKillOutputParent) {
            Remove-Item -LiteralPath $hardKillOutputParent -Recurse -Force
        }
    }
}

if (-not $failed) { Write-Host 'HANDSHAKE PASSED' }
exit ($failed ? 1 : 0)
