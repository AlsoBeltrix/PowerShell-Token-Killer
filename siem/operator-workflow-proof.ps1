#!/usr/bin/env pwsh
#Requires -Version 7

<#
.SYNOPSIS
Runs S5's three operator workflow proofs against packaged receiver/PTK bits.

.DESCRIPTION
The external destination in this deterministic gate is an OTLP adapter sink, not
a claim of real-SIEM product acceptance. It proves the external-only path installs
no mini-SIEM and the explicit multiple-destination path delivers independently.
S6 remains responsible for a named real SIEM product query-back acceptance.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PtkServerPath,
    [Parameter(Mandatory)][string]$PackageDir,
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$ChecksumFile,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-arm64')]
    [string]$Rid,
    [string]$RtkPath,
    [string]$DestinationToolPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$destinationTool = if ([string]::IsNullOrWhiteSpace($DestinationToolPath)) {
    Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/ptk-audit-destination.ps1'
} else {
    [IO.Path]::GetFullPath($DestinationToolPath)
}
$manager = Join-Path ([IO.Path]::GetFullPath($PackageDir)) 'manage.ps1'
$profileRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$proofRoot = Join-Path $profileRoot (
    '.ptk-siem-s5-workflow-' + [guid]::NewGuid().ToString('N'))
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
$previousRtkPath = $env:PTK_RTK_PATH

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Send-Rpc([Diagnostics.Process]$Process, [hashtable]$Message) {
    $Process.StandardInput.WriteLine(($Message | ConvertTo-Json -Depth 16 -Compress))
    $Process.StandardInput.Flush()
}

function Read-Rpc([Diagnostics.Process]$Process, [int]$Id, [int]$TimeoutSeconds = 30) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $remaining = [Math]::Max(
            1,
            [int]($deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds)
        $read = $Process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait($remaining)) { throw "PTK RPC $Id timed out." }
        $line = $read.GetAwaiter().GetResult()
        if ($null -eq $line) {
            throw "PTK exited before RPC ${Id}: $($Process.StandardError.ReadToEnd())"
        }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $message = $line | ConvertFrom-Json -Depth 32
        if ($message.PSObject.Properties['id'] -and $message.id -eq $Id) {
            return $message
        }
    }
    throw "PTK RPC $Id timed out."
}

function Start-PtkProducer([string]$AuditRoot, [int]$UiPort) {
    $server = [IO.Path]::GetFullPath($PtkServerPath)
    $start = [Diagnostics.ProcessStartInfo]::new()
    if ($server.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
        $start.FileName = 'dotnet'
        $start.ArgumentList.Add('exec')
        $start.ArgumentList.Add($server)
    } else {
        $start.FileName = $server
    }
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['PTK_AUDIT_ROOT'] = $AuditRoot
    $start.Environment['PTK_AUDIT_UI_PORT'] = [string]$UiPort
    $start.Environment['PTK_OUTPUT_ROOT'] = Join-Path $AuditRoot 'proof-output'
    if (-not [string]::IsNullOrWhiteSpace($env:PTK_RTK_PATH)) {
        $start.Environment['PTK_RTK_PATH'] = $env:PTK_RTK_PATH
    }
    $process = [Diagnostics.Process]::Start($start)
    $processes.Add($process)
    Send-Rpc $process @{
        jsonrpc = '2.0'; id = 1; method = 'initialize'
        params = @{
            protocolVersion = '2025-06-18'
            capabilities = @{}
            clientInfo = @{ name = 'ptk-siem-s5-proof'; version = '1' }
        }
    }
    $initialized = Read-Rpc $process 1
    if ($initialized.PSObject.Properties['error']) {
        throw "PTK initialize failed: $($initialized.error.message)"
    }
    Send-Rpc $process @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
    $tokenPath = Join-Path $AuditRoot 'ui-token'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf) -and
        [DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "PTK exited before UI startup: $($process.StandardError.ReadToEnd())"
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) {
        throw 'PTK UI token was not created.'
    }
    return $process
}

function Invoke-PtkMarker([Diagnostics.Process]$Process, [int]$Id, [string]$Marker) {
    Send-Rpc $Process @{
        jsonrpc = '2.0'; id = $Id; method = 'tools/call'
        params = @{
            name = 'ptk_invoke'
            arguments = @{
                script = "Write-Output '$Marker'"
                route = 'pwsh'
                timeoutSeconds = 30
            }
            _meta = @{
                'io.github.also-beltrix.ptk/call-context/v1' = @{
                    agent_name = 'ptk-siem-s5-proof'
                    task_name = 'operator workflow proof'
                    run_id = $Marker
                }
            }
        }
    }
    $response = Read-Rpc $Process $Id 45
    $toolFailed = $response.PSObject.Properties['result'] -and
        $response.result.PSObject.Properties['isError'] -and
        $response.result.isError
    if ($response.PSObject.Properties['error'] -or $toolFailed) {
        throw "PTK marker invocation '$Marker' failed."
    }
}

function Start-AdapterSink([int]$Port, [string]$Token, [string]$Name) {
    $sinkRoot = Join-Path $proofRoot "sink-$Name"
    [void](New-Item -ItemType Directory -Path $sinkRoot)
    $state = Join-Path $sinkRoot 'state.json'
    $stop = Join-Path $sinkRoot 'stop'
    $script = Join-Path $sinkRoot 'sink.ps1'
    $sinkSource = @'
param([int]$Port,[string]$Token,[string]$StatePath,[string]$StopPath)
$ErrorActionPreference='Stop'
$listener=[Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
$posts=0
$options=0
[IO.File]::WriteAllText($StatePath,'{"posts":0,"options":0}')
try {
  while (-not (Test-Path -LiteralPath $StopPath)) {
    $pending=$listener.GetContextAsync()
    while (-not $pending.Wait(200) -and -not (Test-Path -LiteralPath $StopPath)) { }
    if (-not $pending.IsCompleted) { break }
    $context=$pending.GetAwaiter().GetResult()
    try {
      if ($context.Request.Headers['Authorization'] -cne "Bearer $Token") {
        $context.Response.StatusCode=401
      } elseif ($context.Request.HttpMethod -ceq 'OPTIONS') {
        $options++
        $temporary="$StatePath.tmp"
        [IO.File]::WriteAllText($temporary,(@{posts=$posts;options=$options}|ConvertTo-Json -Compress))
        [IO.File]::Move($temporary,$StatePath,$true)
        $context.Response.StatusCode=204
      } elseif ($context.Request.HttpMethod -ceq 'POST' -and
          $context.Request.Url.AbsolutePath -ceq '/v1/logs') {
        $reader=[IO.StreamReader]::new($context.Request.InputStream,$context.Request.ContentEncoding)
        try { $body=$reader.ReadToEnd() } finally { $reader.Dispose() }
        $posts++
        $temporary="$StatePath.tmp"
        [IO.File]::WriteAllText($temporary,(@{posts=$posts;options=$options;body=$body}|ConvertTo-Json -Compress))
        [IO.File]::Move($temporary,$StatePath,$true)
        $context.Response.StatusCode=200
      } else {
        $context.Response.StatusCode=404
      }
    } finally { $context.Response.Close() }
  }
} finally { $listener.Close() }
'@
    [IO.File]::WriteAllText($script, $sinkSource)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command pwsh -ErrorAction Stop).Source
    foreach ($argument in @(
        '-NoProfile', '-File', $script, '-Port', [string]$Port,
        '-Token', $Token, '-StatePath', $state, '-StopPath', $stop)) {
        $start.ArgumentList.Add($argument)
    }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    $processes.Add($process)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $state) -and
        [DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "Adapter sink failed: $($process.StandardError.ReadToEnd())" }
        Start-Sleep -Milliseconds 50
    }
    return [pscustomobject]@{
        process = $process
        state = $state
        stop = $stop
        port = $Port
        endpoint = "http://127.0.0.1:$Port/v1/logs"
        token = $Token
    }
}

function Get-SinkPostCount([object]$Sink) {
    try {
        return [int](Get-Content -LiteralPath $Sink.state -Raw |
            ConvertFrom-Json).posts
    } catch { return -1 }
}

function Get-SinkOptionCount([object]$Sink) {
    try {
        return [int](Get-Content -LiteralPath $Sink.state -Raw |
            ConvertFrom-Json).options
    } catch { return -1 }
}

function Wait-SinkPostCount([object]$Sink, [int]$Minimum, [int]$TimeoutSeconds = 30) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $posts = Get-SinkPostCount $Sink
        if ($posts -ge $Minimum) { return $posts }
        Start-Sleep -Milliseconds 100
    }
    throw "Adapter sink did not reach $Minimum posts."
}

function Stop-ProcessSafe([Diagnostics.Process]$Process) {
    if (-not $Process.HasExited) {
        try { $Process.StandardInput.Close() }
        catch { Write-Verbose "Process stdin was already unavailable: $($_.Exception.Message)" }
        if (-not $Process.WaitForExit(2000)) {
            $Process.Kill($true)
            [void]$Process.WaitForExit(5000)
        }
    }
}

try {
    [void](New-Item -ItemType Directory -Path $proofRoot)
    if (-not (Test-Path -LiteralPath $destinationTool -PathType Leaf)) {
        throw "PTK destination tool was not found at '$destinationTool'."
    }
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode(
            $proofRoot,
            [IO.UnixFileMode]::UserRead -bor
            [IO.UnixFileMode]::UserWrite -bor
            [IO.UnixFileMode]::UserExecute)
    }
    if ([string]::IsNullOrWhiteSpace($RtkPath)) {
        $rtk = Get-Command rtk -ErrorAction Stop
        $RtkPath = $rtk.Source
    }
    $env:PTK_RTK_PATH = [IO.Path]::GetFullPath($RtkPath)

    # External-only: no receiver package is copied or started in this root.
    Write-Information 'S5 proof: external-SIEM-only workflow' -InformationAction Continue
    $externalRoot = Join-Path $proofRoot 'external-only'
    [void](New-Item -ItemType Directory -Path $externalRoot)
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode(
            $externalRoot,
            [IO.UnixFileMode]::UserRead -bor
            [IO.UnixFileMode]::UserWrite -bor
            [IO.UnixFileMode]::UserExecute)
    }
    $externalAudit = Join-Path $externalRoot 'audit'
    $externalUiPort = Get-FreePort
    $externalSink = Start-AdapterSink (Get-FreePort) (('e' * 32)) 'external'
    $adapterProbe = Invoke-WebRequest -Method Options -Uri $externalSink.endpoint `
        -Headers @{ Authorization = "Bearer $($externalSink.token)" } `
        -SkipHttpErrorCheck -TimeoutSec 5
    Assert-True ($adapterProbe.StatusCode -eq 204) `
        "External adapter preflight returned HTTP $($adapterProbe.StatusCode)."
    $externalProducer = Start-PtkProducer $externalAudit $externalUiPort
    try {
        $externalAdd = & $destinationTool -Action Add -AuditRoot $externalAudit `
            -UiUri "http://127.0.0.1:$externalUiPort/" `
            -OperatorLabel 'external only' -Kind otlp_http `
            -Endpoint $externalSink.endpoint -IngestToken $externalSink.token
    } catch {
        throw "External-only destination Add failed: $($_.Exception.Message)"
    }
    Assert-True (-not $externalAdd.ptk_restart_required) `
        'External destination incorrectly required a PTK restart.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $externalRoot 'mini-siem'))) `
        'External-only workflow created a mini-SIEM root.'
    Invoke-PtkMarker $externalProducer 2 ('PTK-S5-EXTERNAL:' + [guid]::NewGuid())
    [void](Wait-SinkPostCount $externalSink 1)

    # Explicit second destination: one can fail while the first advances.
    Write-Information 'S5 proof: explicit multiple-destination workflow' -InformationAction Continue
    $secondSink = Start-AdapterSink (Get-FreePort) (('m' * 32)) 'second'
    $unconfirmedSecondRefused = $false
    try {
        & $destinationTool -Action Add -AuditRoot $externalAudit `
            -UiUri "http://127.0.0.1:$externalUiPort/" `
            -OperatorLabel 'second unconfirmed' -Kind otlp_http `
            -Endpoint $secondSink.endpoint -IngestToken $secondSink.token
    } catch {
        $unconfirmedSecondRefused = $_.Exception.Message -match `
            'sensitive_duplication_confirmation_required'
    }
    Assert-True $unconfirmedSecondRefused `
        'Second destination did not require explicit sensitive duplication confirmation.'
    Assert-True ((Get-SinkOptionCount $secondSink) -eq 0) `
        'Unconfirmed second destination was contacted before refusal.'
    try {
        $secondAdd = & $destinationTool -Action Add -AuditRoot $externalAudit `
            -UiUri "http://127.0.0.1:$externalUiPort/" `
            -OperatorLabel 'second explicit' -Kind otlp_http `
            -Endpoint $secondSink.endpoint -IngestToken $secondSink.token `
            -ConfirmSensitiveDuplication
    } catch {
        throw "Multiple-destination Add failed: $($_.Exception.Message)"
    }
    Assert-True (-not $secondAdd.ptk_restart_required) `
        'Second destination incorrectly required a PTK restart.'
    Invoke-PtkMarker $externalProducer 3 ('PTK-S5-MULTI:' + [guid]::NewGuid())
    $externalPosts = Wait-SinkPostCount $externalSink 2
    $secondPosts = Wait-SinkPostCount $secondSink 1
    [IO.File]::WriteAllText($secondSink.stop, 'stop')
    Stop-ProcessSafe $secondSink.process
    Invoke-PtkMarker $externalProducer 4 ('PTK-S5-INDEPENDENT:' + [guid]::NewGuid())
    $externalPostsAfterFailure = Wait-SinkPostCount $externalSink ($externalPosts + 1)
    Assert-True ($externalPostsAfterFailure -gt $externalPosts) `
        'Healthy destination did not advance independently of failed destination.'
    $failureDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    $secondDelivery = $null
    do {
        $deliveryStatus = & $destinationTool -Action List -AuditRoot $externalAudit `
            -UiUri "http://127.0.0.1:$externalUiPort/"
        $secondDelivery = @($deliveryStatus.destinations | Where-Object {
            [guid]$_.destination_id -eq [guid]$secondAdd.destination.destination_id
        } | Select-Object -First 1).delivery
        if ($null -eq $secondDelivery) {
            Start-Sleep -Milliseconds 100
            continue
        }
        if ($secondDelivery.consecutive_failures -gt 0 -or
            $secondDelivery.pending_event_records -gt 0 -or
            $secondDelivery.pending_evidence_records -gt 0) {
            break
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $failureDeadline)
    $failureObserved = $null -ne $secondDelivery -and
        ($secondDelivery.consecutive_failures -gt 0 -or
         $secondDelivery.pending_event_records -gt 0 -or
         $secondDelivery.pending_evidence_records -gt 0)
    Assert-True $failureObserved `
        'Failed destination did not retain independent failure/backlog state.'
    $secondRecovery = Start-AdapterSink $secondSink.port $secondSink.token 'second-recovery'
    $secondRecoveredPosts = Wait-SinkPostCount $secondRecovery 1 30

    # Mini-SIEM-only: verified archive install, explicit Add, named query-back doctor.
    Write-Information 'S5 proof: mini-SIEM-only workflow and query-back doctor' -InformationAction Continue
    $miniRoot = Join-Path $proofRoot 'mini-only'
    $installRoot = Join-Path $miniRoot 'program'
    $configRoot = Join-Path $miniRoot 'config'
    $manifestPath = Join-Path $miniRoot 'deployment/deployment.json'
    $serviceKind = if ($IsWindows) { 'windows' } elseif ($IsMacOS) { 'launchd' } else { 'systemd' }
    $serviceIdentity = if ($IsWindows) {
        [Security.Principal.WindowsIdentity]::GetCurrent().Name
    } else {
        [Environment]::UserName
    }
    $miniInstall = & $manager -Action Install `
        -PackageDir $PackageDir -ArchivePath $ArchivePath -ChecksumFile $ChecksumFile `
        -ExpectedVersion $Version -ExpectedRid $Rid `
        -InstallRoot $installRoot `
        -ConfigurationPath (Join-Path $configRoot 'receiver.json') `
        -ManifestPath $manifestPath `
        -ServiceDefinitionPath (Join-Path $miniRoot 'deployment/native-service.definition') `
        -ServiceKind $serviceKind -ServiceName 'ptk-siem-s5-proof' `
        -ServiceIdentity $serviceIdentity `
        -DataDirectory (Join-Path $miniRoot 'data') `
        -WitnessDirectory (Join-Path $miniRoot 'witness') `
        -IngestBindAddress 127.0.0.1 -IngestPort (Get-FreePort) `
        -OperatorBindAddress 127.0.0.1 -OperatorPort (Get-FreePort) `
        -GenerateCredentials -GenerateSelfSignedTls -TlsDnsName 127.0.0.1 `
        -UseIngestTlsForOperator `
        -RetentionMaxAgeDays 1
    Assert-True (-not $miniInstall.anchored) `
        'Local mini-SIEM proof was incorrectly reported as anchored.'
    Assert-True (-not $miniInstall.ptk_destination_selected) `
        'Mini-SIEM install silently selected itself as a destination.'
    $miniManifest = Get-Content -LiteralPath $manifestPath -Raw |
        ConvertFrom-Json -Depth 32
    $receiverStart = [Diagnostics.ProcessStartInfo]::new()
    $receiverStart.FileName = $miniManifest.executable
    $receiverStart.ArgumentList.Add('--config')
    $receiverStart.ArgumentList.Add($miniManifest.configuration_path)
    $receiverStart.UseShellExecute = $false
    $receiverStart.RedirectStandardOutput = $true
    $receiverStart.RedirectStandardError = $true
    $receiver = [Diagnostics.Process]::Start($receiverStart)
    $processes.Add($receiver)
    $connection = & $installRoot/manage.ps1 -Action ConnectionInfo `
        -ManifestPath $manifestPath
    $miniAudit = Join-Path $miniRoot 'producer-audit'
    $miniUiPort = Get-FreePort
    [void](Start-PtkProducer $miniAudit $miniUiPort)
    $receiverDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    $statusFailure = $null
    do {
        try {
            $miniStatus = & $installRoot/manage.ps1 -Action Status `
                -ManifestPath $manifestPath
        } catch {
            if ($receiver.HasExited) {
                throw "Packaged receiver failed: $($receiver.StandardError.ReadToEnd())"
            }
            $statusFailure = $_.Exception.Message
            $miniStatus = $null
            Start-Sleep -Milliseconds 100
        }
    } while ($null -eq $miniStatus -and [DateTimeOffset]::UtcNow -lt $receiverDeadline)
    Assert-True ($null -ne $miniStatus) `
        "Packaged receiver did not become healthy. Last status failure: $statusFailure"
    $originalManifest = [IO.File]::ReadAllText($manifestPath)
    $wrongPinRejected = $false
    try {
        $wrongPinManifest = $originalManifest | ConvertFrom-Json -Depth 32
        $wrongPinManifest.server_certificate_sha256 = '0' * 64
        [IO.File]::WriteAllText(
            $manifestPath,
            ($wrongPinManifest | ConvertTo-Json -Depth 32))
        try {
            & $installRoot/manage.ps1 -Action Status -ManifestPath $manifestPath
        } catch {
            $wrongPinRejected = $true
        }
    } finally {
        [IO.File]::WriteAllText($manifestPath, $originalManifest)
    }
    Assert-True $wrongPinRejected `
        'Packaged manager accepted the operator HTTPS endpoint with the wrong certificate pin.'
    $unconfiguredDoctorRefused = $false
    try {
        & $destinationTool -Action Doctor -AuditRoot $miniAudit `
            -UiUri "http://127.0.0.1:$miniUiPort/" `
            -DestinationId ([guid]::NewGuid()) `
            -Endpoint $connection.ingest_endpoint `
            -ReceiverOperatorUri $connection.receiver_operator_uri `
            -ReceiverConfigurationPath $connection.receiver_configuration_path `
            -PtkServerPath $PtkServerPath -DoctorTimeoutSeconds 10
    } catch {
        $unconfiguredDoctorRefused = $_.Exception.Message -match 'not explicitly configured'
    }
    Assert-True $unconfiguredDoctorRefused `
        'Doctor did not refuse an unconfigured destination before inspection.'
    try {
        $miniAdd = & $destinationTool -Action Add -AuditRoot $miniAudit `
            -UiUri "http://127.0.0.1:$miniUiPort/" `
            -OperatorLabel 'mini only' -Kind otlp_http `
            -Endpoint $connection.ingest_endpoint `
            -ReceiverConfigurationPath $connection.receiver_configuration_path `
            -ServerCertificateSha256 $connection.server_certificate_sha256
    } catch {
        throw "Mini-SIEM destination Add failed: $($_.Exception.Message)"
    }
    $doctor = & $destinationTool -Action Doctor -AuditRoot $miniAudit `
        -UiUri "http://127.0.0.1:$miniUiPort/" `
        -DestinationId $miniAdd.destination.destination_id `
        -Endpoint $connection.ingest_endpoint `
        -ReceiverOperatorUri $connection.receiver_operator_uri `
        -ReceiverConfigurationPath $connection.receiver_configuration_path `
        -PtkServerPath $PtkServerPath -DoctorTimeoutSeconds 90
    Assert-True ($doctor.command_acknowledged -and $doctor.response_acknowledged) `
        'Mini-SIEM doctor did not query back complete command/response evidence.'

    $summary = [pscustomobject]@{
        schema = 'ptk.siem.s5.workflow-proof/1'
        external_only = [ordered]@{
            mini_siem_installed = $false
            delivered_posts = Get-SinkPostCount $externalSink
            ptk_restart_required = $externalAdd.ptk_restart_required
        }
        multiple_destinations = [ordered]@{
            explicitly_confirmed = $true
            destination_count = 2
            first_advanced_while_second_failed = $true
            first_posts = Get-SinkPostCount $externalSink
            second_posts_before_failure = $secondPosts
            second_posts_after_recovery = $secondRecoveredPosts
        }
        mini_siem_only = [ordered]@{
            archive_sha256 = $miniInstall.archive_sha256
            anchored = $miniInstall.anchored
            activity_id = $doctor.activity_id
            command_acknowledged = $doctor.command_acknowledged
            response_acknowledged = $doctor.response_acknowledged
            ptk_restart_required = $doctor.ptk_restart_required
        }
    }
    $summary | ConvertTo-Json -Depth 8
} finally {
    foreach ($process in $processes) {
        Stop-ProcessSafe $process
        $process.Dispose()
    }
    if ($null -eq $previousRtkPath) {
        Remove-Item Env:PTK_RTK_PATH -ErrorAction SilentlyContinue
    } else {
        $env:PTK_RTK_PATH = $previousRtkPath
    }
    if (Test-Path -LiteralPath $proofRoot) {
        Remove-Item -LiteralPath $proofRoot -Recurse -Force
    }
}
