#!/usr/bin/env pwsh
#Requires -Version 7

<#
.SYNOPSIS
Runs the uncredentialed production-acceptance checks that require the public
PTK executable boundary.

.DESCRIPTION
Starts real PTK MCP server processes and proves:

- two independent servers and multiple named sessions remain isolated while
  calls run concurrently;
- 100 sequential resets return the process fleet and native resources to a
  stable baseline while a sibling session stays warm;
- an uncooperative timeout contains its native child/grandchild and replaces
  only its worker;
- directly killing an executing worker contains its child/grandchild, reports
  the outcome as unknown, and preserves a warm sibling;
- an observed Unix process-group escape reports `descendants_unknown`, blocks
  worker reuse, and is never replayed; and
- killing only the public supervisor leaves no worker, child, or grandchild.

The default launch mode builds this checkout. -ServerCommand accepts a
published executable so an exact staged package can be tested without
installation or registration changes.
#>
[CmdletBinding(DefaultParameterSetName = 'BuiltDll')]
param(
    [ValidateRange(1, 10000)]
    [int]$ResetCycles = 100,
    [ValidateRange(5, 600)]
    [int]$TimeoutSec = 60,
    [Parameter(ParameterSetName = 'ServerCommand', Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]]$ServerCommand
)

$ErrorActionPreference = 'Stop'
$serverDir = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $serverDir
$checkpoint = [TimeSpan]::FromSeconds($TimeoutSec)
$testRoot = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
) ('.ptk/production-acceptance-' + [guid]::NewGuid().ToString('N'))
$servers = [System.Collections.Generic.List[object]]::new()

if ($PSCmdlet.ParameterSetName -eq 'BuiltDll') {
    dotnet build (Join-Path $serverDir 'PtkMcpServer') -v q --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'PTK server build failed.'
    }
    $serverAssembly = Join-Path $serverDir 'PtkMcpServer/bin/Debug/net10.0/PtkMcpServer.dll'
    if (-not (Test-Path -LiteralPath $serverAssembly -PathType Leaf)) {
        throw "Built PTK server assembly is missing: $serverAssembly"
    }
    $resolvedServerCommand = @('dotnet', 'exec', $serverAssembly)
}
else {
    $resolvedServerCommand = @($ServerCommand)
    if ($resolvedServerCommand[0] -match '[\\/]') {
        $resolvedServerCommand[0] = (
            Resolve-Path -LiteralPath $resolvedServerCommand[0]
        ).ProviderPath
    }
}

function ConvertTo-PsLiteral {
    param([Parameter(Mandatory)][string]$Value)
    "'" + $Value.Replace("'", "''") + "'"
}

function ConvertTo-EncodedCommand {
    param([Parameter(Mandatory)][string]$Script)
    [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
}

function Start-PtkServer {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    $root = Join-Path $testRoot $Label
    $outputRoot = Join-Path $root 'output'
    $auditRoot = Join-Path $root 'audit'
    New-Item -ItemType Directory -Path $root, $WorkingDirectory -Force |
        Out-Null

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $resolvedServerCommand[0]
    foreach ($argument in ($resolvedServerCommand | Select-Object -Skip 1)) {
        $start.ArgumentList.Add($argument)
    }
    $start.WorkingDirectory = $WorkingDirectory
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.UseShellExecute = $false
    $start.Environment['PTK_OUTPUT_ROOT'] = $outputRoot
    $start.Environment['PTK_AUDIT_ROOT'] = $auditRoot
    # Measure PTK resources, not PowerShell's delayed upstream telemetry client.
    $start.Environment['POWERSHELL_TELEMETRY_OPTOUT'] = '1'

    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) {
        throw "$Label PTK server did not start."
    }
    $server = [pscustomobject]@{
        Label = $Label
        Process = $process
        StandardError = $process.StandardError.ReadToEndAsync()
        Responses = @{}
        NextId = 0
        Disposed = $false
        OutputRoot = $outputRoot
        AuditRoot = $auditRoot
        WorkingDirectory = $WorkingDirectory
    }
    $servers.Add($server)
    $server
}

function Send-PtkMessage {
    param(
        [Parameter(Mandatory)]$Server,
        [Parameter(Mandatory)][hashtable]$Message
    )

    $json = $Message | ConvertTo-Json -Depth 12 -Compress
    $Server.Process.StandardInput.WriteLine($json)
    $Server.Process.StandardInput.Flush()
}

function Send-PtkRequest {
    param(
        [Parameter(Mandatory)]$Server,
        [Parameter(Mandatory)][string]$Method,
        [hashtable]$Params = @{}
    )

    $Server.NextId++
    $id = $Server.NextId
    Send-PtkMessage -Server $Server -Message @{
        jsonrpc = '2.0'
        id = $id
        method = $Method
        params = $Params
    }
    $id
}

function Receive-PtkResponse {
    param(
        [Parameter(Mandatory)]$Server,
        [Parameter(Mandatory)][int]$Id
    )

    $key = [string]$Id
    if ($Server.Responses.ContainsKey($key)) {
        $response = $Server.Responses[$key]
        $Server.Responses.Remove($key)
        return $response
    }

    $deadline = [DateTimeOffset]::UtcNow + $checkpoint
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Server.Process.HasExited) {
            throw "$($Server.Label) exited while waiting for response id=$Id."
        }
        $remaining = $deadline - [DateTimeOffset]::UtcNow
        $lineTask = $Server.Process.StandardOutput.ReadLineAsync()
        if (-not $lineTask.Wait([Math]::Max(1, [int]$remaining.TotalMilliseconds))) {
            break
        }
        $line = $lineTask.Result
        if ($null -eq $line) {
            throw "$($Server.Label) closed stdout while waiting for response id=$Id."
        }
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        $message = $line | ConvertFrom-Json
        if (-not $message.PSObject.Properties['id']) {
            continue
        }
        $messageKey = [string]$message.id
        if ($messageKey -eq $key) {
            return $message
        }
        $Server.Responses[$messageKey] = $message
    }
    throw "$($Server.Label) timed out after ${TimeoutSec}s waiting for response id=$Id."
}

function Receive-PtkToolResult {
    param(
        [Parameter(Mandatory)]$Server,
        [Parameter(Mandatory)][int]$Id
    )

    $response = Receive-PtkResponse -Server $Server -Id $Id
    if ($response.PSObject.Properties['error']) {
        throw "$($Server.Label) RPC id=$Id failed: $(
            $response.error | ConvertTo-Json -Depth 12 -Compress
        )"
    }
    if (-not $response.PSObject.Properties['result']) {
        throw "$($Server.Label) RPC id=$Id returned no result."
    }
    $response.result
}

function Send-PtkTool {
    param(
        [Parameter(Mandatory)]$Server,
        [Parameter(Mandatory)][string]$Name,
        [hashtable]$Arguments = @{}
    )
    Send-PtkRequest -Server $Server -Method 'tools/call' -Params @{
        name = $Name
        arguments = $Arguments
    }
}

function Invoke-PtkTool {
    param(
        [Parameter(Mandatory)]$Server,
        [Parameter(Mandatory)][string]$Name,
        [hashtable]$Arguments = @{}
    )
    $id = Send-PtkTool -Server $Server -Name $Name -Arguments $Arguments
    Receive-PtkToolResult -Server $Server -Id $id
}

function Get-PtkToolText {
    param([Parameter(Mandatory)]$Result)
    if (-not $Result.PSObject.Properties['content']) {
        throw 'PTK tool result contained no content.'
    }
    (@(
        $Result.content |
            Where-Object type -CEQ 'text' |
            ForEach-Object text
    ) -join [Environment]::NewLine)
}

function Initialize-PtkServer {
    param([Parameter(Mandatory)]$Server)

    $id = Send-PtkRequest -Server $Server -Method 'initialize' -Params @{
        protocolVersion = '2025-06-18'
        capabilities = @{}
        clientInfo = @{
            name = 'ptk-production-acceptance'
            version = '1.0.0'
        }
    }
    $response = Receive-PtkResponse -Server $Server -Id $id
    if (-not $response.result.serverInfo.name) {
        throw "$($Server.Label) initialize failed."
    }
    Send-PtkMessage -Server $Server -Message @{
        jsonrpc = '2.0'
        method = 'notifications/initialized'
    }
}

function Get-SessionPid {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )
    $match = [regex]::Match(
        $Text,
        "(?m)^session=$([regex]::Escape($Name)) state=ready worker_pid=(\d+) active=false ")
    if (-not $match.Success) {
        throw "Session '$Name' was not ready in: '$Text'"
    }
    [int]$match.Groups[1].Value
}

function Open-PtkSession {
    param(
        [Parameter(Mandatory)]$Server,
        [Parameter(Mandatory)][string]$Name
    )
    $result = Invoke-PtkTool -Server $Server -Name 'ptk_session' -Arguments @{
        action = 'open'
        name = $Name
    }
    Get-SessionPid -Text (Get-PtkToolText $result) -Name $Name
}

function Invoke-PtkScript {
    param(
        [Parameter(Mandatory)]$Server,
        [Parameter(Mandatory)][string]$Session,
        [Parameter(Mandatory)][string]$Script
    )
    $result = Invoke-PtkTool -Server $Server -Name 'ptk_invoke' -Arguments @{
        script = $Script
        route = 'pwsh'
        session = $Session
        timeoutSeconds = $TimeoutSec
    }
    if ($result.PSObject.Properties['isError'] -and $result.isError) {
        throw "$($Server.Label)/$Session invoke failed: $(Get-PtkToolText $result)"
    }
    Get-PtkToolText $result
}

function Stop-PtkServer {
    param(
        [Parameter(Mandatory)]$Server,
        [switch]$CleanupOnly
    )

    if ($Server.Disposed) {
        return
    }
    $usedKillFallback = $false
    try {
        if (-not $Server.Process.HasExited) {
            try {
                $Server.Process.StandardInput.Close()
            }
            catch {
                if (-not $CleanupOnly) {
                    throw
                }
            }
        }
        if (-not $Server.Process.HasExited -and
            -not $Server.Process.WaitForExit($TimeoutSec * 1000)) {
            $usedKillFallback = $true
            $Server.Process.Kill($true)
            if (-not $Server.Process.WaitForExit($TimeoutSec * 1000)) {
                throw "$($Server.Label) survived the cleanup kill fallback."
            }
        }
        if ($usedKillFallback -and -not $CleanupOnly) {
            throw "$($Server.Label) did not stop on MCP stdin EOF."
        }
        if (-not $CleanupOnly -and $Server.Process.ExitCode -ne 0) {
            throw "$($Server.Label) exited with code $($Server.Process.ExitCode)."
        }
    }
    finally {
        if ($Server.Process.HasExited) {
            try {
                [void]$Server.StandardError.Wait($TimeoutSec * 1000)
            }
            catch {
            }
        }
        $Server.Process.Dispose()
        $Server.Disposed = $true
    }
}

function Wait-ForFiles {
    param([Parameter(Mandatory)][string[]]$Paths)
    $deadline = [DateTimeOffset]::UtcNow + $checkpoint
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (@($Paths | Where-Object {
            -not (Test-Path -LiteralPath $_ -PathType Leaf)
        }).Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 25
    }
    $missing = @($Paths | Where-Object {
        -not (Test-Path -LiteralPath $_ -PathType Leaf)
    })
    throw "Timed out waiting for acceptance marker(s): $($missing -join ', ')"
}

function Get-ProcessPairs {
    if ($IsWindows) {
        return @(
            Get-CimInstance Win32_Process |
                ForEach-Object {
                    [pscustomobject]@{
                        Id = [int]$_.ProcessId
                        ParentId = [int]$_.ParentProcessId
                    }
                }
        )
    }

    $lines = @(& /bin/ps -e -o pid= -o ppid=)
    if ($LASTEXITCODE -ne 0) {
        throw 'ps failed while enumerating the PTK process fleet.'
    }
    @(
        foreach ($line in $lines) {
            if ($line -match '^\s*(\d+)\s+(\d+)\s*$') {
                [pscustomobject]@{
                    Id = [int]$Matches[1]
                    ParentId = [int]$Matches[2]
                }
            }
        }
    )
}

function Get-ProcessFleet {
    param([Parameter(Mandatory)][int]$SupervisorId)

    $pairs = @(Get-ProcessPairs)
    $ids = [Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($SupervisorId)
    do {
        $added = $false
        foreach ($pair in $pairs) {
            if ($ids.Contains($pair.ParentId) -and $ids.Add($pair.Id)) {
                $added = $true
            }
        }
    } while ($added)
    @($ids | Sort-Object)
}

function Get-LinuxPrivateBytes {
    param([Parameter(Mandatory)][int[]]$ProcessIds)
    [long]$bytes = 0
    foreach ($processId in $ProcessIds) {
        $path = "/proc/$processId/smaps_rollup"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Linux process $processId disappeared during resource measurement."
        }
        foreach ($line in [IO.File]::ReadLines($path)) {
            if ($line -match '^Private_(?:Clean|Dirty|Hugetlb):\s+(\d+)\s+kB$') {
                $bytes += [long]$Matches[1] * 1024
            }
        }
    }
    $bytes
}

function Get-MacFootprintBytes {
    param([Parameter(Mandatory)][int[]]$ProcessIds)

    $lines = @(& /usr/bin/footprint -f bytes --noCategories @ProcessIds 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "footprint failed: $($lines -join [Environment]::NewLine)"
    }
    $pattern = if ($ProcessIds.Count -gt 1) {
        '^Summary Footprint:\s+(\d+) B$'
    }
    else {
        'Footprint:\s+(\d+) B '
    }
    foreach ($line in ($lines | Select-Object -Last 12)) {
        if ($line -match $pattern) {
            return [long]$Matches[1]
        }
    }
    throw "footprint returned no aggregate byte count: $($lines -join ' | ')"
}

function Get-FleetResourcesOnce {
    param([Parameter(Mandatory)][int]$SupervisorId)

    $ids = @(Get-ProcessFleet -SupervisorId $SupervisorId)
    if ($ids -notcontains $SupervisorId) {
        throw "PTK supervisor $SupervisorId disappeared during resource measurement."
    }

    [long]$handles = 0
    [long]$privateBytes = 0
    $handleDetails = @{}
    $handleTypes = @{}
    $pipeDescriptors = @{}
    if ($IsWindows) {
        foreach ($processId in $ids) {
            $process = Get-Process -Id $processId -ErrorAction Stop
            try {
                $process.Refresh()
                $handles += $process.HandleCount
                $handleDetails[[string]$processId] = [long]$process.HandleCount
                $privateBytes += $process.PrivateMemorySize64
            }
            finally {
                $process.Dispose()
            }
        }
    }
    elseif ($IsMacOS) {
        $descriptorLines = @(
            & /usr/sbin/lsof -a -p ($ids -join ',') -d '0-999999' -F pft 2>&1
        )
        if ($LASTEXITCODE -ne 0) {
            throw "lsof failed: $($descriptorLines -join [Environment]::NewLine)"
        }
        $currentProcessId = $null
        foreach ($line in $descriptorLines) {
            if ($line -cmatch '^p(\d+)$') {
                $currentProcessId = $Matches[1]
                $handleDetails[$currentProcessId] = 0L
                $handleTypes[$currentProcessId] = @{}
                $pipeDescriptors[$currentProcessId] =
                    [Collections.Generic.List[int]]::new()
            }
            elseif ($line -cmatch '^f(\d+)$') {
                $currentDescriptor = [int]$Matches[1]
                $handles++
                if ($null -ne $currentProcessId) {
                    $handleDetails[$currentProcessId]++
                }
            }
            elseif ($line -cmatch '^t(.+)$' -and
                $null -ne $currentProcessId) {
                $type = $Matches[1]
                $currentTypes = $handleTypes[$currentProcessId]
                $currentTypes[$type] = 1L + [long](
                    $currentTypes[$type] ?? 0)
                if ($type -eq 'PIPE') {
                    $pipeDescriptors[$currentProcessId].Add(
                        $currentDescriptor)
                }
            }
        }
        # macOS does not expose useful Process.PrivateMemorySize64 values.
        # Physical footprint is the native per-process private-pressure metric.
        $privateBytes = Get-MacFootprintBytes -ProcessIds $ids
    }
    else {
        foreach ($processId in $ids) {
            $descriptorPath = "/proc/$processId/fd"
            if (-not (Test-Path -LiteralPath $descriptorPath -PathType Container)) {
                throw "Linux process $processId disappeared during fd measurement."
            }
            $processHandles = @(
                Get-ChildItem -LiteralPath $descriptorPath -Force
            ).Count
            $handles += $processHandles
            $handleDetails[[string]$processId] = [long]$processHandles
        }
        $privateBytes = Get-LinuxPrivateBytes -ProcessIds $ids
    }

    [pscustomobject]@{
        ProcessCount = $ids.Count
        HandleCount = $handles
        PrivateBytes = $privateBytes
        ProcessIds = $ids
        HandleDetails = $handleDetails
        HandleTypes = $handleTypes
        PipeDescriptors = $pipeDescriptors
    }
}

function Get-FleetResources {
    param([Parameter(Mandatory)][int]$SupervisorId)

    $deadline = [DateTimeOffset]::UtcNow + [TimeSpan]::FromSeconds(2)
    $lastFailure = $null
    do {
        try {
            $sample = Get-FleetResourcesOnce -SupervisorId $SupervisorId
            $afterIds = @(Get-ProcessFleet -SupervisorId $SupervisorId)
            if (($sample.ProcessIds -join ',') -ceq ($afterIds -join ',')) {
                return $sample
            }
            $lastFailure = 'the process fleet changed during measurement'
        }
        catch {
            $lastFailure = $_
        }
        Start-Sleep -Milliseconds 25
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "PTK fleet did not settle for resource measurement: $lastFailure"
}

function Get-SettledFleetResources {
    param([Parameter(Mandatory)][int]$SupervisorId)

    $samples = @(
        1..4 | ForEach-Object {
            $sample = Get-FleetResources -SupervisorId $SupervisorId
            if ($_ -lt 4) {
                Start-Sleep -Milliseconds 250
            }
            $sample
        }
    )
    $counts = @($samples.ProcessCount | Sort-Object -Unique)
    if ($counts.Count -ne 1) {
        throw "PTK fleet did not settle to one process count: $($counts -join ', ')"
    }
    $handleSample = $samples |
        Sort-Object HandleCount |
        Select-Object -First 1
    [pscustomobject]@{
        ProcessCount = $counts[0]
        HandleCount = $handleSample.HandleCount
        PrivateBytes = ($samples.PrivateBytes | Measure-Object -Minimum).Minimum
        ProcessIds = $samples[-1].ProcessIds
        HandleDetails = $handleSample.HandleDetails
        HandleTypes = $handleSample.HandleTypes
        PipeDescriptors = $handleSample.PipeDescriptors
    }
}

function Assert-NoMonotonicGrowth {
    param(
        [Parameter(Mandatory)][object[]]$Samples,
        [Parameter(Mandatory)][string]$Property
    )

    $tail = @($Samples | Select-Object -Last ([Math]::Min(20, $Samples.Count)))
    if ($tail.Count -lt 2) {
        return
    }
    $nondecreasing = $true
    for ($index = 1; $index -lt $tail.Count; $index++) {
        if ($tail[$index].$Property -lt $tail[$index - 1].$Property) {
            $nondecreasing = $false
            break
        }
    }
    if ($nondecreasing -and $tail[-1].$Property -gt $tail[0].$Property) {
        throw "$Property grew monotonically over the final $($tail.Count) reset cycles."
    }
}

function Test-ProcessAlive {
    param([Parameter(Mandatory)][int]$ProcessId)
    try {
        $process = [Diagnostics.Process]::GetProcessById($ProcessId)
        try {
            -not $process.HasExited
        }
        finally {
            $process.Dispose()
        }
    }
    catch [ArgumentException] {
        $false
    }
}

function Get-UnixProcessGroup {
    param([Parameter(Mandatory)][int]$ProcessId)

    $value = (& ps -o pgid= -p $ProcessId 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $value -notmatch '^\d+$') {
        throw "Could not read Unix process group for PID $ProcessId."
    }
    [int]$value
}

function Wait-ForProcessExit {
    param([Parameter(Mandatory)][int[]]$ProcessIds)
    $deadline = [DateTimeOffset]::UtcNow + $checkpoint
    do {
        $live = @($ProcessIds | Where-Object {
            Test-ProcessAlive -ProcessId $_
        })
        if ($live.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "PTK hard-kill left live process(es): $($live -join ', ')"
}

function New-SessionSetupScript {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$ModuleName,
        [Parameter(Mandatory)][string]$Directory
    )

    $tagLiteral = ConvertTo-PsLiteral $Tag
    $moduleLiteral = ConvertTo-PsLiteral $ModuleName
    $directoryLiteral = ConvertTo-PsLiteral $Directory
    @"
`$module = New-Module -Name $moduleLiteral -ScriptBlock {
    function Get-PtkOverlap { $tagLiteral }
    Export-ModuleMember -Function Get-PtkOverlap
}
Import-Module `$module -Force
`$global:PtkAcceptanceTag = $tagLiteral
`$env:PTK_ACCEPTANCE_TAG = $tagLiteral
Set-Location -LiteralPath $directoryLiteral
$tagLiteral
"@
}

function New-SessionProbeScript {
    param([Parameter(Mandatory)][string]$ModuleName)
    $moduleLiteral = ConvertTo-PsLiteral $ModuleName
    "`$global:PtkAcceptanceTag; Get-PtkOverlap; " +
        "`$env:PTK_ACCEPTANCE_TAG; (Get-Location).Path; " +
        "[bool](Get-Module -Name $moduleLiteral); `$PID"
}

function New-BlockingProcessTreeScript {
    param(
        [Parameter(Mandatory)][string]$Marker,
        [Parameter(Mandatory)][string]$BlockingStatement
    )

    $pwshPath = (Get-Command pwsh -ErrorAction Stop).Source
    $grandchildEncoded = ConvertTo-EncodedCommand 'Start-Sleep -Seconds 300'
    $childTemplate = @'
$grandchild = Start-Process -FilePath __PWSH__ -ArgumentList @('-NoProfile', '-EncodedCommand', '__GRANDCHILD__') -PassThru
[IO.File]::WriteAllText(__MARKER__, "$PID`n$($grandchild.Id)")
Start-Sleep -Seconds 300
'@
    $childEncoded = ConvertTo-EncodedCommand (
        $childTemplate.
            Replace('__PWSH__', (ConvertTo-PsLiteral $pwshPath)).
            Replace('__GRANDCHILD__', $grandchildEncoded).
            Replace('__MARKER__', (ConvertTo-PsLiteral $Marker))
    )
    $invokeTemplate = @'
$child = Start-Process -FilePath __PWSH__ -ArgumentList @('-NoProfile', '-EncodedCommand', '__CHILD__') -PassThru
while (-not [IO.File]::Exists(__MARKER__)) { Start-Sleep -Milliseconds 10 }
__BLOCK__
'@
    $invokeTemplate.
        Replace('__PWSH__', (ConvertTo-PsLiteral $pwshPath)).
        Replace('__CHILD__', $childEncoded).
        Replace('__MARKER__', (ConvertTo-PsLiteral $Marker)).
        Replace('__BLOCK__', $BlockingStatement)
}

function New-EscapingProcessTreeScript {
    param(
        [Parameter(Mandatory)][string]$Marker,
        [Parameter(Mandatory)][string]$Effect
    )

    $pwshPath = (Get-Command pwsh -ErrorAction Stop).Source
    $ready = "$Marker.ready"
    $gate = "$Marker.gate"
    $escaped = "$Marker.escaped"
    $failed = "$Marker.failed"
    $grandchildEncoded = ConvertTo-EncodedCommand 'Start-Sleep -Seconds 300'
    $childTemplate = @'
Add-Type -TypeDefinition @"
using System.Runtime.InteropServices;
public static class PtkAcceptanceEscapeNative
{
    [DllImport("libc", SetLastError = true)]
    public static extern int setsid();
}
"@
$grandchild = Start-Process -FilePath __PWSH__ -ArgumentList @('-NoProfile', '-EncodedCommand', '__GRANDCHILD__') -PassThru
[IO.File]::WriteAllText(__READY__, "$PID`n$($grandchild.Id)")
while (-not [IO.File]::Exists(__GATE__)) { Start-Sleep -Milliseconds 10 }
$sessionId = [PtkAcceptanceEscapeNative]::setsid()
if ($sessionId -le 0) {
    [IO.File]::WriteAllText(__FAILED__, "setsid=$sessionId error=$([Runtime.InteropServices.Marshal]::GetLastPInvokeError())")
    exit 69
}
[IO.File]::WriteAllText(__ESCAPED__, "$PID`n$sessionId")
Start-Sleep -Seconds 300
'@
    $childEncoded = ConvertTo-EncodedCommand (
        $childTemplate.
            Replace('__PWSH__', (ConvertTo-PsLiteral $pwshPath)).
            Replace('__GRANDCHILD__', $grandchildEncoded).
            Replace('__READY__', (ConvertTo-PsLiteral $ready)).
            Replace('__GATE__', (ConvertTo-PsLiteral $gate)).
            Replace('__ESCAPED__', (ConvertTo-PsLiteral $escaped)).
            Replace('__FAILED__', (ConvertTo-PsLiteral $failed))
    )
    $invokeTemplate = @'
[IO.File]::AppendAllText(__EFFECT__, 'once')
$child = Start-Process -FilePath __PWSH__ -ArgumentList @('-NoProfile', '-EncodedCommand', '__CHILD__') -PassThru
while (-not [IO.File]::Exists(__READY__)) { Start-Sleep -Milliseconds 10 }
Start-Sleep -Seconds 300
'@
    [pscustomobject]@{
        Script = $invokeTemplate.
            Replace('__EFFECT__', (ConvertTo-PsLiteral $Effect)).
            Replace('__PWSH__', (ConvertTo-PsLiteral $pwshPath)).
            Replace('__CHILD__', $childEncoded).
            Replace('__READY__', (ConvertTo-PsLiteral $ready))
        Ready = $ready
        Gate = $gate
        Escaped = $escaped
        Failed = $failed
    }
}

function Assert-Probe {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$ForeignTag,
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][int]$WorkerPid
    )
    if ([regex]::Matches(
            $Text,
            "(?m)^$([regex]::Escape($Tag))\r?$").Count -lt 3 -or
        $Text -match [regex]::Escape($ForeignTag) -or
        $Text -notmatch [regex]::Escape($Directory) -or
        $Text -notmatch '(?m)^True\r?$' -or
        $Text -notmatch "(?m)^$WorkerPid\r?$") {
        throw "Session probe did not preserve isolated state: '$Text'"
    }
}

New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$serverA = $null
$serverB = $null
$workerKillServer = $null
$hardKillServer = $null
$timeoutKnownIds = @()
$workerKillKnownIds = @()
$escapeKnownIds = @()
$hardKillKnownIds = @()
try {
    # Phase 1: public multi-session and multi-server isolation.
    $workA = Join-Path $testRoot 'work-a'
    $workB = Join-Path $testRoot 'work-b'
    $serverA = Start-PtkServer -Label 'server-a' -WorkingDirectory $workA
    $serverB = Start-PtkServer -Label 'server-b' -WorkingDirectory $workB
    Initialize-PtkServer $serverA
    Initialize-PtkServer $serverB
    if ($serverA.Process.Id -eq $serverB.Process.Id) {
        throw 'Independent PTK servers shared one supervisor PID.'
    }

    $openAOnPrem = Send-PtkTool $serverA 'ptk_session' @{
        action = 'open'; name = 'exchange-onprem'
    }
    $openAOnline = Send-PtkTool $serverA 'ptk_session' @{
        action = 'open'; name = 'exchange-online'
    }
    $openBOnPrem = Send-PtkTool $serverB 'ptk_session' @{
        action = 'open'; name = 'exchange-onprem'
    }
    $aOnPremPid = Get-SessionPid (
        Get-PtkToolText (Receive-PtkToolResult $serverA $openAOnPrem)
    ) 'exchange-onprem'
    $aOnlinePid = Get-SessionPid (
        Get-PtkToolText (Receive-PtkToolResult $serverA $openAOnline)
    ) 'exchange-online'
    $bOnPremPid = Get-SessionPid (
        Get-PtkToolText (Receive-PtkToolResult $serverB $openBOnPrem)
    ) 'exchange-onprem'
    if (@($aOnPremPid, $aOnlinePid, $bOnPremPid | Sort-Object -Unique).Count -ne 3) {
        throw 'Named sessions across two PTK servers did not receive three distinct workers.'
    }

    $aOnPremDirectory = Join-Path $testRoot 'state/a-onprem'
    $aOnlineDirectory = Join-Path $testRoot 'state/a-online'
    $bOnPremDirectory = Join-Path $testRoot 'state/b-onprem'
    New-Item -ItemType Directory -Path (
        $aOnPremDirectory,
        $aOnlineDirectory,
        $bOnPremDirectory
    ) -Force | Out-Null
    $tagAOnPrem = 'server-a-onprem-' + [guid]::NewGuid().ToString('N')
    $tagAOnline = 'server-a-online-' + [guid]::NewGuid().ToString('N')
    $tagBOnPrem = 'server-b-onprem-' + [guid]::NewGuid().ToString('N')
    $moduleAOnPrem = 'PtkAcceptanceAOnPrem'
    $moduleAOnline = 'PtkAcceptanceAOnline'
    $moduleBOnPrem = 'PtkAcceptanceBOnPrem'

    [void](Invoke-PtkScript $serverA 'exchange-onprem' (
        New-SessionSetupScript $tagAOnPrem $moduleAOnPrem $aOnPremDirectory
    ))
    [void](Invoke-PtkScript $serverA 'exchange-online' (
        New-SessionSetupScript $tagAOnline $moduleAOnline $aOnlineDirectory
    ))
    [void](Invoke-PtkScript $serverB 'exchange-onprem' (
        New-SessionSetupScript $tagBOnPrem $moduleBOnPrem $bOnPremDirectory
    ))

    Assert-Probe (
        Invoke-PtkScript $serverA 'exchange-onprem' (
            New-SessionProbeScript $moduleAOnPrem
        )
    ) $tagAOnPrem $tagAOnline $aOnPremDirectory $aOnPremPid
    Assert-Probe (
        Invoke-PtkScript $serverA 'exchange-online' (
            New-SessionProbeScript $moduleAOnline
        )
    ) $tagAOnline $tagAOnPrem $aOnlineDirectory $aOnlinePid
    Assert-Probe (
        Invoke-PtkScript $serverB 'exchange-onprem' (
            New-SessionProbeScript $moduleBOnPrem
        )
    ) $tagBOnPrem $tagAOnPrem $bOnPremDirectory $bOnPremPid

    $bOnlyPid = Open-PtkSession $serverB 'server-b-only'
    $listA = Get-PtkToolText (Invoke-PtkTool $serverA 'ptk_session' @{
        action = 'list'
    })
    $listB = Get-PtkToolText (Invoke-PtkTool $serverB 'ptk_session' @{
        action = 'list'
    })
    if ($listA -match '(?m)^session=server-b-only ' -or
        $listB -notmatch "(?m)^session=server-b-only state=ready worker_pid=$bOnlyPid ") {
        throw 'Independent PTK servers shared or lost a session registry entry.'
    }
    [void](Invoke-PtkTool $serverB 'ptk_session' @{
        action = 'close'; name = 'server-b-only'
    })

    $release = Join-Path $testRoot 'concurrent.release'
    $readyAOnPrem = Join-Path $testRoot 'concurrent.a-onprem'
    $readyAOnline = Join-Path $testRoot 'concurrent.a-online'
    $readyBOnPrem = Join-Path $testRoot 'concurrent.b-onprem'
    $concurrent = @(
        [pscustomobject]@{
            Server = $serverA
            Session = 'exchange-onprem'
            Ready = $readyAOnPrem
            Token = $tagAOnPrem
        },
        [pscustomobject]@{
            Server = $serverA
            Session = 'exchange-online'
            Ready = $readyAOnline
            Token = $tagAOnline
        },
        [pscustomobject]@{
            Server = $serverB
            Session = 'exchange-onprem'
            Ready = $readyBOnPrem
            Token = $tagBOnPrem
        }
    )
    foreach ($call in $concurrent) {
        $readyLiteral = ConvertTo-PsLiteral $call.Ready
        $releaseLiteral = ConvertTo-PsLiteral $release
        $tokenLiteral = ConvertTo-PsLiteral $call.Token
        $script = "[IO.File]::WriteAllText($readyLiteral, 'ready'); " +
            "while (-not [IO.File]::Exists($releaseLiteral)) { " +
            "Start-Sleep -Milliseconds 10 }; $tokenLiteral"
        $call | Add-Member NoteProperty RequestId (
            Send-PtkTool $call.Server 'ptk_invoke' @{
                script = $script
                route = 'pwsh'
                session = $call.Session
                timeoutSeconds = $TimeoutSec
            }
        )
    }
    Wait-ForFiles @($readyAOnPrem, $readyAOnline, $readyBOnPrem)

    $activeStateId = Send-PtkTool $serverA 'ptk_state' @{
        session = 'exchange-onprem'
        listAvailable = $false
    }
    $activeListId = Send-PtkTool $serverA 'ptk_session' @{
        action = 'list'
    }
    $activeState = Get-PtkToolText (
        Receive-PtkToolResult $serverA $activeStateId
    )
    $activeList = Get-PtkToolText (
        Receive-PtkToolResult $serverA $activeListId
    )
    if ($activeState -notmatch (
            "(?m)^ptk supervisor: pid=$($serverA.Process.Id) sessions=3/8`r?$"
        ) -or
        $activeState -notmatch (
            "(?m)^session=exchange-onprem state=ready worker_pid=$aOnPremPid " +
            "active=true warm_state_lost=false last_failure=none " +
            "reset_required=false`r?$"
        ) -or
        $activeState -notmatch (
            '(?m)^runspace: unavailable \(detail=session_busy\)\r?$'
        )) {
        throw "Active selected-session state was not prompt and truthful: '$activeState'"
    }
    foreach ($expected in @(
            "session=default state=cold worker_pid=none active=false ",
            "session=exchange-onprem state=ready worker_pid=$aOnPremPid active=true ",
            "session=exchange-online state=ready worker_pid=$aOnlinePid active=true "
        )) {
        if ($activeList -notmatch "(?m)^$([regex]::Escape($expected))") {
            throw "Active session list omitted '$expected' from: '$activeList'"
        }
    }
    if ($activeList -match [regex]::Escape([string]$bOnPremPid)) {
        throw "Active session list crossed the independent server boundary: '$activeList'"
    }

    [IO.File]::WriteAllText($release, 'release')
    foreach ($call in $concurrent) {
        $result = Receive-PtkToolResult $call.Server $call.RequestId
        $text = Get-PtkToolText $result
        if ($result.isError -or
            $text -notmatch "(?m)^$([regex]::Escape($call.Token))\r?$") {
            throw "Concurrent public invoke failed for $($call.Server.Label)/$($call.Session): '$text'"
        }
    }
    Write-Host (
        "public isolation passed: supervisors $($serverA.Process.Id)/$($serverB.Process.Id), " +
        "workers $aOnPremPid/$aOnlinePid/$bOnPremPid"
    )

    Stop-PtkServer $serverB

    # Phase 2: 100 replacement cycles with a warm sibling.
    $siblingPid = Get-SessionPid (
        Get-PtkToolText (Invoke-PtkTool $serverA 'ptk_session' @{ action = 'list' })
    ) 'exchange-onprem'
    $warmup = Get-PtkToolText (Invoke-PtkTool $serverA 'ptk_reset' @{
        session = 'exchange-online'
    })
    $victimPid = Get-SessionPid $warmup 'exchange-online'
    Start-Sleep -Milliseconds 500
    $baseline = Get-SettledFleetResources $serverA.Process.Id
    $samples = [Collections.Generic.List[object]]::new()

    for ($cycle = 1; $cycle -le $ResetCycles; $cycle++) {
        $resetText = Get-PtkToolText (Invoke-PtkTool $serverA 'ptk_reset' @{
            session = 'exchange-online'
        })
        $nextVictimPid = Get-SessionPid $resetText 'exchange-online'
        if ($nextVictimPid -eq $victimPid) {
            throw "Reset cycle $cycle reused worker PID $victimPid."
        }
        $victimPid = $nextVictimPid

        $sessionsText = Get-PtkToolText (Invoke-PtkTool $serverA 'ptk_session' @{
            action = 'list'
        })
        if ($sessionsText -notmatch (
            "(?m)^session=exchange-onprem state=ready worker_pid=$siblingPid active=false "
        )) {
            throw "Reset cycle $cycle changed or faulted sibling worker $siblingPid."
        }

        $sample = Get-FleetResources $serverA.Process.Id
        if ($sample.ProcessCount -ne $baseline.ProcessCount) {
            throw (
                "Reset cycle $cycle left $($sample.ProcessCount) PTK processes; " +
                "baseline was $($baseline.ProcessCount)."
            )
        }
        $samples.Add($sample)
        if ($cycle % 10 -eq 0 -or $cycle -eq $ResetCycles) {
            Write-Host (
                "reset stability: $cycle/$ResetCycles; processes=$($sample.ProcessCount); " +
                "handles/fds=$($sample.HandleCount); private/footprint=" +
                "$([Math]::Round($sample.PrivateBytes / 1MB, 1)) MiB"
            )
        }
    }

    $final = Get-SettledFleetResources $serverA.Process.Id
    $handleCeiling = [long]$baseline.HandleCount + 4
    $memoryAllowance = [Math]::Max(
        [long][Math]::Ceiling($baseline.PrivateBytes * 0.10),
        [long](32MB))
    $memoryCeiling = [long]$baseline.PrivateBytes + $memoryAllowance
    if ($final.ProcessCount -ne $baseline.ProcessCount) {
        throw "Final PTK process count $($final.ProcessCount) did not return to baseline $($baseline.ProcessCount)."
    }
    if ($final.HandleCount -gt $handleCeiling) {
        throw (
            "Final handle/fd count $($final.HandleCount) exceeded baseline " +
            "$($baseline.HandleCount) + 4; baseline per PID=$(
                $baseline.HandleDetails | ConvertTo-Json -Compress
            ); final per PID=$($final.HandleDetails | ConvertTo-Json -Compress); " +
            "baseline supervisor types=$(
                $baseline.HandleTypes[[string]$serverA.Process.Id] |
                    ConvertTo-Json -Compress
            ); final supervisor types=$(
                $final.HandleTypes[[string]$serverA.Process.Id] |
                    ConvertTo-Json -Compress
            ); baseline supervisor pipe fds=$(
                $baseline.PipeDescriptors[[string]$serverA.Process.Id] -join ','
            ); final supervisor pipe fds=$(
                $final.PipeDescriptors[[string]$serverA.Process.Id] -join ','
            )."
        )
    }
    if ($final.PrivateBytes -gt $memoryCeiling) {
        throw (
            "Final private/footprint bytes $($final.PrivateBytes) exceeded baseline " +
            "$($baseline.PrivateBytes) plus allowance $memoryAllowance."
        )
    }
    Assert-NoMonotonicGrowth $samples 'ProcessCount'
    Assert-NoMonotonicGrowth $samples 'HandleCount'
    Assert-NoMonotonicGrowth $samples 'PrivateBytes'

    # Phase 3: a timed-out, uncooperative pipeline must replace only its
    # worker and contain the native child/grandchild it started.
    $timeoutMarker = Join-Path $testRoot 'timeout-tree.txt'
    $timeoutScript = New-BlockingProcessTreeScript `
        -Marker $timeoutMarker `
        -BlockingStatement '[Threading.Thread]::Sleep(300000)'
    $timeoutRequest = Send-PtkTool $serverA 'ptk_invoke' @{
        script = $timeoutScript
        route = 'pwsh'
        session = 'exchange-online'
        timeoutSeconds = 1
    }
    Wait-ForFiles @($timeoutMarker)
    $timeoutMarkerIds = @(
        Get-Content -LiteralPath $timeoutMarker |
            Where-Object { $_ -match '^\d+$' } |
            ForEach-Object { [int]$_ }
    )
    if ($timeoutMarkerIds.Count -ne 2) {
        throw "Timeout child marker was invalid: '$(
            Get-Content -LiteralPath $timeoutMarker -Raw
        )'"
    }
    $timeoutKnownIds = @(
        Get-ProcessFleet -SupervisorId $victimPid
    )
    foreach ($expectedId in $timeoutMarkerIds) {
        if ($timeoutKnownIds -notcontains $expectedId -or
            -not (Test-ProcessAlive $expectedId)) {
            throw "Timeout process $expectedId was not a live worker descendant."
        }
    }

    $timeoutResult = Receive-PtkToolResult $serverA $timeoutRequest
    $timeoutText = Get-PtkToolText $timeoutResult
    if ($timeoutText -notmatch (
            '(?m)^\[ptk worker\] status=timed_out ' +
            'detail=execution_timed_out; '
        )) {
        throw "Uncooperative timeout returned the wrong terminal result: '$timeoutText'"
    }
    $timeoutRecovery = [regex]::Matches(
        $timeoutText,
        '(?m)^recovery=(.+)$'
    )
    if ($timeoutRecovery.Count -ne 1 -or
        $timeoutRecovery[0].Groups[1].Value -notmatch (
            '^available: ptk_output handle=ptko_[A-Za-z0-9_-]+$'
        )) {
        throw "Timeout recovery guidance was duplicated or unavailable: '$timeoutText'"
    }

    $replacementPid = $null
    $replacementList = ''
    $replacementDeadline = [DateTimeOffset]::UtcNow + $checkpoint
    do {
        $replacementList = Get-PtkToolText (
            Invoke-PtkTool $serverA 'ptk_session' @{ action = 'list' }
        )
        $replacementMatch = [regex]::Match(
            $replacementList,
            '(?m)^session=exchange-online state=ready worker_pid=(\d+) ' +
                'active=false '
        )
        if ($replacementMatch.Success -and
            [int]$replacementMatch.Groups[1].Value -ne $victimPid) {
            $replacementPid = [int]$replacementMatch.Groups[1].Value
            break
        }
        Start-Sleep -Milliseconds 25
    } while ([DateTimeOffset]::UtcNow -lt $replacementDeadline)
    if ($null -eq $replacementPid) {
        throw "Timed-out worker $victimPid was not replaced: '$replacementList'"
    }
    if ($replacementList -notmatch (
            "(?m)^session=exchange-onprem state=ready worker_pid=$siblingPid " +
            'active=false '
        )) {
        throw "Timeout recovery changed or faulted sibling worker $siblingPid."
    }
    Wait-ForProcessExit $timeoutKnownIds
    $timeoutKnownIds = @()

    $postTimeout = Get-SettledFleetResources $serverA.Process.Id
    if ($postTimeout.ProcessCount -ne $baseline.ProcessCount -or
        $postTimeout.HandleCount -gt $handleCeiling) {
        throw (
            "Timeout recovery left resources outside baseline: processes=" +
            "$($postTimeout.ProcessCount)/$($baseline.ProcessCount), " +
            "handles/fds=$($postTimeout.HandleCount)/$($baseline.HandleCount)."
        )
    }
    Write-Host (
        "timeout containment passed: worker $victimPid replaced by " +
        "$replacementPid; $($timeoutMarkerIds.Count) native descendants exited"
    )

    $siblingProbe = Invoke-PtkScript $serverA 'exchange-onprem' (
        New-SessionProbeScript $moduleAOnPrem
    )
    Assert-Probe $siblingProbe $tagAOnPrem $tagAOnline $aOnPremDirectory $siblingPid
    Write-Host (
        "reset stability passed: $ResetCycles replacements; baseline/final " +
        "processes=$($baseline.ProcessCount)/$($final.ProcessCount), " +
        "handles/fds=$($baseline.HandleCount)/$($final.HandleCount), " +
        "private/footprint=$([Math]::Round($baseline.PrivateBytes / 1MB, 1))/" +
        "$([Math]::Round($final.PrivateBytes / 1MB, 1)) MiB"
    )
    Stop-PtkServer $serverA

    # Phase 4: killing only an executing worker must contain its old process
    # group, report the in-flight outcome as unknown, replace only that worker,
    # and preserve a warm sibling.
    $workerKillWork = Join-Path $testRoot 'work-worker-kill'
    $workerKillServer = Start-PtkServer `
        -Label 'worker-kill' `
        -WorkingDirectory $workerKillWork
    Initialize-PtkServer $workerKillServer
    $workerKillVictimPid = Open-PtkSession $workerKillServer 'kill-target'
    $workerKillSiblingPid = Open-PtkSession $workerKillServer 'kill-sibling'
    $workerKillSentinel = 'worker-kill-sibling-' + [guid]::NewGuid().ToString('N')
    $workerKillSetup = Invoke-PtkScript `
        $workerKillServer `
        'kill-sibling' `
        ("`$global:PtkWorkerKillSentinel = $(ConvertTo-PsLiteral $workerKillSentinel); " +
            '$global:PtkWorkerKillSentinel; $PID')
    if ($workerKillSetup -notmatch (
            "(?m)^$([regex]::Escape($workerKillSentinel))`r?$"
        ) -or
        $workerKillSetup -notmatch "(?m)^$workerKillSiblingPid`r?$") {
        throw "Worker-kill sibling did not initialize: '$workerKillSetup'"
    }

    $workerKillMarker = Join-Path $testRoot 'worker-kill-tree.txt'
    $workerKillEffect = Join-Path $testRoot 'worker-kill-effect.txt'
    $workerKillScript = (
        "[IO.File]::AppendAllText($(ConvertTo-PsLiteral $workerKillEffect), 'once'); " +
        (New-BlockingProcessTreeScript `
            -Marker $workerKillMarker `
            -BlockingStatement 'Start-Sleep -Seconds 300')
    )
    $workerKillRequest = Send-PtkTool $workerKillServer 'ptk_invoke' @{
        script = $workerKillScript
        route = 'pwsh'
        session = 'kill-target'
        timeoutSeconds = $TimeoutSec
    }
    Wait-ForFiles @($workerKillMarker, $workerKillEffect)
    $workerKillMarkerIds = @(
        Get-Content -LiteralPath $workerKillMarker |
            Where-Object { $_ -match '^\d+$' } |
            ForEach-Object { [int]$_ }
    )
    if ($workerKillMarkerIds.Count -ne 2) {
        throw "Worker-kill process marker was invalid: '$(
            Get-Content -LiteralPath $workerKillMarker -Raw
        )'"
    }
    $workerKillKnownIds = @($workerKillVictimPid) + $workerKillMarkerIds
    $workerKillFleet = @(
        Get-ProcessFleet -SupervisorId $workerKillServer.Process.Id
    )
    foreach ($expectedId in $workerKillKnownIds) {
        if ($workerKillFleet -notcontains $expectedId -or
            -not (Test-ProcessAlive $expectedId)) {
            throw "Worker-kill process $expectedId was not a live PTK descendant."
        }
    }

    Stop-Process -Id $workerKillVictimPid -Force -ErrorAction Stop
    Wait-ForProcessExit $workerKillKnownIds
    $workerKillResult = Receive-PtkToolResult `
        $workerKillServer `
        $workerKillRequest
    $workerKillText = Get-PtkToolText $workerKillResult
    if ($workerKillText -cne (
            '[ptk invoke] status=outcome_unknown session=kill-target ' +
            'detail=worker_transport_closed; do not resubmit automatically; ' +
            'PTK did not retry the command.'
        )) {
        throw "Worker hard-kill returned the wrong invoke result: '$workerKillText'"
    }
    if ((Get-Content -LiteralPath $workerKillEffect -Raw) -cne 'once') {
        throw 'Worker hard-kill replayed the in-flight command.'
    }

    $workerKillList = ''
    $workerKillReplacementPid = 0
    $workerKillRecoveryDeadline = [DateTimeOffset]::UtcNow + $checkpoint
    do {
        $workerKillList = Get-PtkToolText (
            Invoke-PtkTool $workerKillServer 'ptk_session' @{ action = 'list' }
        )
        if ($workerKillList -match (
                '(?m)^session=kill-target state=ready worker_pid=(\d+) ' +
                'active=false warm_state_lost=true last_failure=worker_lost ' +
                'reset_required=false\r?$'
            )) {
            $workerKillReplacementPid = [int]$Matches[1]
            break
        }
        Start-Sleep -Milliseconds 25
    } while ([DateTimeOffset]::UtcNow -lt $workerKillRecoveryDeadline)
    if ($workerKillReplacementPid -le 0 -or
        $workerKillReplacementPid -eq $workerKillVictimPid) {
        throw "Worker hard-kill did not create a fresh worker: '$workerKillList'"
    }
    if ($workerKillList -notmatch (
            "(?m)^session=kill-sibling state=ready " +
            "worker_pid=$workerKillSiblingPid active=false warm_state_lost=false " +
            "last_failure=none reset_required=false`r?$"
        )) {
        throw "Worker hard-kill disturbed its sibling: '$workerKillList'"
    }
    $workerKillSiblingProbe = Invoke-PtkScript `
        $workerKillServer `
        'kill-sibling' `
        '$global:PtkWorkerKillSentinel; $PID'
    if ($workerKillSiblingProbe -notmatch (
            "(?m)^$([regex]::Escape($workerKillSentinel))`r?$"
        ) -or
        $workerKillSiblingProbe -notmatch "(?m)^$workerKillSiblingPid`r?$") {
        throw "Worker hard-kill lost sibling warm state: '$workerKillSiblingProbe'"
    }
    Write-Host (
        "worker hard-kill passed: victim $workerKillVictimPid replaced by " +
        "$workerKillReplacementPid; $($workerKillMarkerIds.Count) descendants " +
        "exited; sibling $workerKillSiblingPid stayed warm"
    )
    Stop-PtkServer $workerKillServer
    $workerKillKnownIds = @()

    # Phase 5: an observed Unix process-group escape must fail closed, block
    # worker reuse, and never replay a timed-out invocation.
    if (-not $IsWindows) {
        $escapeWork = Join-Path $testRoot 'work-escape'
        $escapeServer = Start-PtkServer -Label 'escape' -WorkingDirectory $escapeWork
        Initialize-PtkServer $escapeServer
        $escapeWorkerPid = Open-PtkSession $escapeServer 'escape-target'
        $escapeMarker = Join-Path $testRoot 'escape-tree'
        $escapeEffect = Join-Path $testRoot 'escape-effect.txt'
        $escapeTree = New-EscapingProcessTreeScript `
            -Marker $escapeMarker `
            -Effect $escapeEffect
        $escapeInvokeTimeout = [Math]::Max(
            4,
            [Math]::Min(12, $TimeoutSec - 1)
        )
        $escapeRequest = Send-PtkTool $escapeServer 'ptk_invoke' @{
            script = $escapeTree.Script
            route = 'pwsh'
            session = 'escape-target'
            timeoutSeconds = $escapeInvokeTimeout
        }
        Wait-ForFiles @($escapeTree.Ready, $escapeEffect)
        $escapeMarkerIds = @(
            Get-Content -LiteralPath $escapeTree.Ready |
                Where-Object { $_ -match '^\d+$' } |
                ForEach-Object { [int]$_ }
        )
        if ($escapeMarkerIds.Count -ne 2) {
            throw "Escape child marker was invalid: '$(
                Get-Content -LiteralPath $escapeTree.Ready -Raw
            )'"
        }
        $escapeChildPid = $escapeMarkerIds[0]
        $escapeGrandchildPid = $escapeMarkerIds[1]
        $escapeKnownIds = @(
            $escapeWorkerPid,
            $escapeChildPid,
            $escapeGrandchildPid
        )
        $escapeFleet = @(Get-ProcessFleet -SupervisorId $escapeServer.Process.Id)
        foreach ($expectedId in $escapeKnownIds) {
            if ($escapeFleet -notcontains $expectedId -or
                -not (Test-ProcessAlive $expectedId)) {
                throw "Escape process $expectedId was not a live PTK descendant."
            }
        }

        Start-Sleep -Milliseconds 750
        [IO.File]::WriteAllText($escapeTree.Gate, 'escape')
        $escapeDeadline = [DateTimeOffset]::UtcNow + $checkpoint
        while (-not (Test-Path -LiteralPath $escapeTree.Escaped)) {
            if (Test-Path -LiteralPath $escapeTree.Failed) {
                throw "Escape child failed: '$(
                    Get-Content -LiteralPath $escapeTree.Failed -Raw
                )'"
            }
            if ([DateTimeOffset]::UtcNow -ge $escapeDeadline) {
                throw 'Escape child did not leave the worker process group.'
            }
            Start-Sleep -Milliseconds 25
        }
        $escapedMarkerIds = @(
            Get-Content -LiteralPath $escapeTree.Escaped |
                Where-Object { $_ -match '^\d+$' } |
                ForEach-Object { [int]$_ }
        )
        if ($escapedMarkerIds.Count -ne 2 -or
            $escapedMarkerIds[0] -ne $escapeChildPid -or
            $escapedMarkerIds[1] -ne $escapeChildPid) {
            throw "Escape completion marker was invalid: '$(
                Get-Content -LiteralPath $escapeTree.Escaped -Raw
            )'"
        }

        $escapeWorkerPgid = Get-UnixProcessGroup $escapeWorkerPid
        $escapeChildPgid = Get-UnixProcessGroup $escapeChildPid
        $escapeGrandchildPgid = Get-UnixProcessGroup $escapeGrandchildPid
        if ($escapeChildPgid -eq $escapeWorkerPgid -or
            $escapeGrandchildPgid -ne $escapeWorkerPgid) {
            throw (
                "Escape topology was invalid: worker=$escapeWorkerPid/" +
                "$escapeWorkerPgid child=$escapeChildPid/$escapeChildPgid " +
                "grandchild=$escapeGrandchildPid/$escapeGrandchildPgid."
            )
        }
        Write-Host (
            "escape topology: worker=$escapeWorkerPid/$escapeWorkerPgid " +
            "child=$escapeChildPid/$escapeChildPgid " +
            "grandchild=$escapeGrandchildPid/$escapeGrandchildPgid"
        )

        Start-Sleep -Milliseconds 750
        $escapeResult = Receive-PtkToolResult $escapeServer $escapeRequest
        $escapeText = Get-PtkToolText $escapeResult
        if ($escapeText -notmatch (
                '(?m)^\[ptk worker\] status=timed_out ' +
                'detail=execution_timed_out; this session worker is being replaced; ' +
                'the command was not retried\.$'
            )) {
            throw "Escaped-worker timeout returned the wrong invoke result: '$escapeText'"
        }
        if ((Get-Content -LiteralPath $escapeEffect -Raw) -cne 'once') {
            throw 'Timed-out escape invocation was replayed.'
        }

        $escapeList = ''
        $faultDeadline = [DateTimeOffset]::UtcNow + $checkpoint
        do {
            $escapeList = Get-PtkToolText (
                Invoke-PtkTool $escapeServer 'ptk_session' @{ action = 'list' }
            )
            if ($escapeList -match (
                    '(?m)^session=escape-target state=faulted worker_pid=none ' +
                    'active=false warm_state_lost=true ' +
                    'last_failure=descendants_unknown reset_required=true\r?$'
                )) {
                break
            }
            Start-Sleep -Milliseconds 25
        } while ([DateTimeOffset]::UtcNow -lt $faultDeadline)
        if ($escapeList -notmatch 'last_failure=descendants_unknown') {
            throw "Process-group escape made a false containment claim: '$escapeList'"
        }
        Wait-ForProcessExit @($escapeWorkerPid)
        Wait-ForProcessExit @($escapeGrandchildPid)
        if (-not (Test-ProcessAlive $escapeChildPid)) {
            throw "Escaped child $escapeChildPid did not survive worker-group cleanup."
        }

        $blockedReset = Invoke-PtkTool $escapeServer 'ptk_reset' @{
            session = 'escape-target'
        }
        $blockedResetText = Get-PtkToolText $blockedReset
        if ($blockedResetText -notmatch (
                '(?m)^\[ptk reset\] refused session=escape-target ' +
                'detail=descendants_unknown; '
            )) {
            throw "Escaped descendant did not block worker reuse: '$blockedResetText'"
        }

        Stop-Process -Id $escapeChildPid -Force -ErrorAction Stop
        Wait-ForProcessExit @($escapeChildPid)
        $replacementReset = $null
        $replacementResetText = ''
        $replacementDeadline = [DateTimeOffset]::UtcNow + $checkpoint
        do {
            $replacementReset = Invoke-PtkTool $escapeServer 'ptk_reset' @{
                session = 'escape-target'
            }
            $replacementResetText = Get-PtkToolText $replacementReset
            if ($replacementResetText -match (
                    '(?m)^\[ptk reset\] completed\r?$'
                )) {
                break
            }
            Start-Sleep -Milliseconds 25
        } while ([DateTimeOffset]::UtcNow -lt $replacementDeadline)
        if ($null -eq $replacementReset -or
            $replacementResetText -notmatch '(?m)^\[ptk reset\] completed\r?$') {
            throw "Session remained blocked after escaped descendant exit: '$replacementResetText'"
        }
        $escapeReplacementPid = Get-SessionPid $replacementResetText 'escape-target'
        if ($escapeReplacementPid -eq $escapeWorkerPid -or
            (Get-Content -LiteralPath $escapeEffect -Raw) -cne 'once') {
            throw 'Escape recovery reused the old worker or replayed the command.'
        }
        Write-Host (
            "process-group escape passed: worker $escapeWorkerPid faulted " +
            "descendants_unknown; escaped child $escapeChildPid blocked replacement"
        )
        Stop-PtkServer $escapeServer
        $escapeKnownIds = @()
    }

    # Phase 6: kill only the public supervisor and let production containment
    # own its worker tree. Never use Process.Kill(entireProcessTree: true) here.
    $hardKillWork = Join-Path $testRoot 'work-hard-kill'
    $hardKillServer = Start-PtkServer -Label 'hard-kill' -WorkingDirectory $hardKillWork
    Initialize-PtkServer $hardKillServer
    $hardKillWorkerPid = Open-PtkSession $hardKillServer 'kill-target'
    $marker = Join-Path $testRoot 'hard-kill-tree.txt'
    $hardKillScript = New-BlockingProcessTreeScript `
        -Marker $marker `
        -BlockingStatement 'Start-Sleep -Seconds 300'
    [void](Send-PtkTool $hardKillServer 'ptk_invoke' @{
        script = $hardKillScript
        route = 'pwsh'
        session = 'kill-target'
        timeoutSeconds = $TimeoutSec
    })
    Wait-ForFiles @($marker)
    $markerIds = @(
        Get-Content -LiteralPath $marker |
            Where-Object { $_ -match '^\d+$' } |
            ForEach-Object { [int]$_ }
    )
    if ($markerIds.Count -ne 2) {
        throw "Hard-kill child marker was invalid: '$(
            Get-Content -LiteralPath $marker -Raw
        )'"
    }
    $hardKillKnownIds = @(
        Get-ProcessFleet -SupervisorId $hardKillServer.Process.Id
    )
    foreach ($expectedId in @($hardKillWorkerPid) + $markerIds) {
        if ($hardKillKnownIds -notcontains $expectedId -or
            -not (Test-ProcessAlive $expectedId)) {
            throw "Hard-kill process $expectedId was not a live PTK descendant."
        }
    }

    $hardKillSupervisorPid = $hardKillServer.Process.Id
    $hardKillServer.Process.Kill()
    if (-not $hardKillServer.Process.WaitForExit($TimeoutSec * 1000)) {
        throw "PTK supervisor $hardKillSupervisorPid survived its direct hard kill."
    }
    Wait-ForProcessExit ($hardKillKnownIds | Where-Object {
        $_ -ne $hardKillSupervisorPid
    })
    Write-Host (
        "hard-kill containment passed: supervisor $hardKillSupervisorPid and " +
        "$($hardKillKnownIds.Count - 1) owned descendants exited"
    )
    Stop-PtkServer $hardKillServer -CleanupOnly

    Write-Host 'PRODUCTION ACCEPTANCE PASSED'
}
finally {
    foreach ($server in $servers) {
        if (-not $server.Disposed) {
            try {
                Stop-PtkServer $server -CleanupOnly
            }
            catch {
                Write-Warning "Cleanup failed for $($server.Label): $_"
            }
        }
    }
    foreach ($processId in @(
            @($timeoutKnownIds) +
                @($workerKillKnownIds) +
                @($escapeKnownIds) +
                @($hardKillKnownIds) |
                Sort-Object -Unique
        )) {
        if (Test-ProcessAlive $processId) {
            try {
                Stop-Process -Id $processId -Force -ErrorAction Stop
            }
            catch {
                Write-Warning "Could not clean acceptance descendant PID ${processId}: $_"
            }
        }
    }
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
