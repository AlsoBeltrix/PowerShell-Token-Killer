#!/usr/bin/env pwsh
#Requires -Version 7

<#
.SYNOPSIS
Configures and verifies PTK SIEM destinations through PTK's protected S3 API.

.DESCRIPTION
This script is part of the PTK package. It does not install a receiver and it
does not edit destinations.json directly. Every change goes through the live
PTK destination API, including its transactional endpoint/authentication
probe. Additional destinations require an explicit duplication confirmation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('List', 'Add', 'Update', 'Enable', 'Disable', 'Remove', 'Doctor')]
    [string]$Action,

    [string]$AuditRoot = (Join-Path `
        -Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) `
        -ChildPath '.ptk/audit'),

    [uri]$UiUri = 'http://127.0.0.1:8317/',

    [string]$UiToken,

    [guid]$DestinationId,

    [ValidateSet('otlp_http', 'splunk_hec')]
    [string]$Kind = 'otlp_http',

    [string]$OperatorLabel,

    [uri]$Endpoint,

    [string]$IngestToken,

    [string]$IngestTokenFile,

    [string]$ServerCertificateSha256,

    [switch]$ConfirmSensitiveDuplication,

    [uri]$ReceiverOperatorUri,

    [string]$ReceiverOperatorToken,

    [string]$ReceiverOperatorTokenFile,

    [string]$ReceiverConfigurationPath,

    [string]$PtkServerPath,

    [ValidateRange(10, 600)]
    [int]$DoctorTimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Initialize-PinnedTlsValidation {
    if ($null -ne ('Ptk.DestinationPinnedTls' -as [type])) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ptk
{
    public static class DestinationPinnedTls
    {
        private static readonly HttpRequestOptionsKey<string> PinOption =
            new HttpRequestOptionsKey<string>("ptk.destination.operator-certificate-sha256");

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

function Get-UiToken {
    if (-not [string]::IsNullOrWhiteSpace($UiToken)) { return $UiToken }
    $tokenPath = Join-Path ([IO.Path]::GetFullPath($AuditRoot)) 'ui-token'
    if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) {
        throw "PTK UI token was not found at '$tokenPath'. Start PTK, or pass -UiToken."
    }
    $token = [IO.File]::ReadAllText($tokenPath).Trim()
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "PTK UI token at '$tokenPath' is empty."
    }
    return $token
}

function Join-EndpointUri {
    param([uri]$BaseUri, [string]$RelativePath)
    return [uri]::new($BaseUri, $RelativePath)
}

function Invoke-JsonRequest {
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PUT')][string]$Method,
        [Parameter(Mandatory)][uri]$Uri,
        [Parameter(Mandatory)][string]$BearerToken,
        [hashtable]$Body,
        [string]$CertificateSha256
    )

    $handler = [Net.Http.HttpClientHandler]::new()
    if (-not [string]::IsNullOrWhiteSpace($CertificateSha256)) {
        if ($Uri.Scheme -cne 'https') {
            throw 'A receiver certificate pin requires an HTTPS operator URI.'
        }
        $candidate = $CertificateSha256.Trim().Replace(':', '')
        if ($candidate -notmatch '^[0-9a-fA-F]{64}$') {
            throw 'Receiver certificate SHA-256 pin is invalid.'
        }
        $normalizedPin = $candidate.ToUpperInvariant()
        Initialize-PinnedTlsValidation
        $handler.ServerCertificateCustomValidationCallback =
            [Ptk.DestinationPinnedTls]::Callback
    }

    $client = [Net.Http.HttpClient]::new($handler, $true)
    $requestMessage = $null
    $response = $null
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(15)
        $requestMessage = [Net.Http.HttpRequestMessage]::new(
            [Net.Http.HttpMethod]::new($Method),
            $Uri)
        $requestMessage.Headers.Authorization =
            [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $BearerToken)
        if (-not [string]::IsNullOrWhiteSpace($CertificateSha256)) {
            [Ptk.DestinationPinnedTls]::ApplyPin($requestMessage, $normalizedPin)
        }
        if ($null -ne $Body) {
            $json = $Body | ConvertTo-Json -Depth 8 -Compress
            $requestMessage.Content = [Net.Http.StringContent]::new(
                $json,
                [Text.Encoding]::UTF8,
                'application/json')
        }
        $response = $client.Send($requestMessage)
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $payload = if ([string]::IsNullOrWhiteSpace($content)) {
            $null
        } else {
            $content | ConvertFrom-Json -Depth 32
        }
        $statusCode = [int]$response.StatusCode
        if ($statusCode -lt 200 -or $statusCode -ge 300) {
            $failure = if ($null -ne $payload -and $payload.PSObject.Properties['error']) {
                $payload.error
            } else {
                "http_$statusCode"
            }
            throw "Destination operation failed: $failure."
        }
        return $payload
    } finally {
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $requestMessage) { $requestMessage.Dispose() }
        $client.Dispose()
    }
}

function Get-PtkStatus {
    param([string]$Token)
    Invoke-JsonRequest -Method GET -Uri (Join-EndpointUri $UiUri '/api/status') `
        -BearerToken $Token
}

function Get-SelectedDestination {
    param([object]$Status)
    Assert-ValuePresent DestinationId $DestinationId
    $configuredMatches = @($Status.destinations | Where-Object {
        [guid]$_.destination_id -eq $DestinationId
    })
    if ($configuredMatches.Count -ne 1) {
        throw "Destination '$DestinationId' is not explicitly configured in this PTK audit root. No destination was contacted."
    }
    return $configuredMatches[0]
}

function Get-DestinationBody {
    Assert-ValuePresent OperatorLabel $OperatorLabel
    Assert-ValuePresent Endpoint $Endpoint
    $resolvedCredential = Read-SecretValue `
        IngestToken $IngestToken IngestTokenFile $IngestTokenFile
    if ([string]::IsNullOrWhiteSpace($resolvedCredential) -and
        -not [string]::IsNullOrWhiteSpace($ReceiverConfigurationPath)) {
        $resolvedCredential = [string](Get-ReceiverConfiguration).ingest.token
    }
    $body = @{
        operator_label = $OperatorLabel
        kind = $Kind
        endpoint = $Endpoint.AbsoluteUri
        credential = $resolvedCredential
        confirm_sensitive_duplication = [bool]$ConfirmSensitiveDuplication
    }
    if (-not [string]::IsNullOrWhiteSpace($ServerCertificateSha256)) {
        $body.server_certificate_sha256 = $ServerCertificateSha256
    }
    return $body
}

function Read-SecretValue {
    param(
        [string]$ValueName,
        [string]$Value,
        [string]$FileName,
        [string]$FilePath
    )
    if (-not [string]::IsNullOrWhiteSpace($Value) -and
        -not [string]::IsNullOrWhiteSpace($FilePath)) {
        throw "Pass either -$ValueName or -$FileName, not both."
    }
    if (-not [string]::IsNullOrWhiteSpace($FilePath)) {
        $fullPath = [IO.Path]::GetFullPath($FilePath)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "$FileName was not found at '$fullPath'."
        }
        $Value = [IO.File]::ReadAllText($fullPath).Trim()
    }
    if ($null -eq $Value) { return '' }
    return $Value
}

function Get-ReceiverConfiguration {
    $fullPath = [IO.Path]::GetFullPath($ReceiverConfigurationPath)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "ReceiverConfigurationPath was not found at '$fullPath'."
    }
    return Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json -Depth 16
}

function Send-Rpc {
    param([Diagnostics.Process]$Process, [hashtable]$Message)
    $Process.StandardInput.WriteLine(($Message | ConvertTo-Json -Depth 16 -Compress))
    $Process.StandardInput.Flush()
}

function Read-Rpc {
    param(
        [Diagnostics.Process]$Process,
        [int]$Id,
        [int]$TimeoutSeconds
    )
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $remaining = [Math]::Max(
            1,
            [int]($deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds)
        $read = $Process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait($remaining)) {
            throw "Timed out waiting for PTK doctor RPC response $Id."
        }
        $line = $read.GetAwaiter().GetResult()
        if ($null -eq $line) {
            $stderr = $Process.StandardError.ReadToEnd()
            throw "PTK doctor process exited before response $Id. $stderr"
        }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $message = $line | ConvertFrom-Json -Depth 32
        if ($message.PSObject.Properties['id'] -and $message.id -eq $Id) {
            return $message
        }
    }
    throw "Timed out waiting for PTK doctor RPC response $Id."
}

function Invoke-SyntheticPtkCall {
    param([string]$Marker)
    Assert-ValuePresent PtkServerPath $PtkServerPath
    $server = [IO.Path]::GetFullPath($PtkServerPath)
    if (-not (Test-Path -LiteralPath $server -PathType Leaf)) {
        throw "PTK server was not found at '$server'."
    }
    $doctorOutputRoot = [IO.Path]::GetFullPath(
        ([IO.Path]::GetFullPath($AuditRoot).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)) +
        '-doctor-output-' + [guid]::NewGuid().ToString('N'))

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
    $start.Environment['PTK_AUDIT_ROOT'] = [IO.Path]::GetFullPath($AuditRoot)
    $start.Environment['PTK_AUDIT_UI_DISABLED'] = '1'
    $start.Environment['PTK_OUTPUT_ROOT'] = $doctorOutputRoot
    $process = [Diagnostics.Process]::Start($start)
    try {
        Send-Rpc $process @{
            jsonrpc = '2.0'; id = 1; method = 'initialize'
            params = @{
                protocolVersion = '2025-06-18'
                capabilities = @{}
                clientInfo = @{ name = 'ptk-siem-doctor'; version = '1' }
            }
        }
        $initialize = Read-Rpc $process 1 30
        if ($initialize.PSObject.Properties['error']) {
            throw "PTK doctor initialize failed: $($initialize.error.message)"
        }
        Send-Rpc $process @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
        Send-Rpc $process @{
            jsonrpc = '2.0'; id = 2; method = 'tools/call'
            params = @{
                name = 'ptk_invoke'
                arguments = @{
                    script = "Write-Output '$Marker'"
                    route = 'pwsh'
                    timeoutSeconds = 30
                }
                _meta = @{
                    'io.github.also-beltrix.ptk/call-context/v1' = @{
                        agent_name = 'ptk-siem-doctor'
                        task_name = 'destination delivery doctor'
                        run_id = $Marker
                    }
                }
            }
        }
        $invocation = Read-Rpc $process 2 45
        $toolFailed = $invocation.PSObject.Properties['result'] -and
            $invocation.result.PSObject.Properties['isError'] -and
            $invocation.result.isError
        if ($invocation.PSObject.Properties['error'] -or $toolFailed) {
            throw 'PTK doctor synthetic invocation failed.'
        }
    } finally {
        if ($null -ne $process -and -not $process.HasExited) {
            $process.StandardInput.Close()
            if (-not $process.WaitForExit(5000)) { $process.Kill($true) }
        }
        if ($null -ne $process) { $process.Dispose() }
        if (Test-Path -LiteralPath $doctorOutputRoot) {
            Remove-Item -LiteralPath $doctorOutputRoot -Recurse -Force
        }
    }
}

function Invoke-Doctor {
    param([string]$Token)
    Assert-ValuePresent Endpoint $Endpoint
    Assert-ValuePresent ReceiverOperatorUri $ReceiverOperatorUri
    $resolvedOperatorToken = Read-SecretValue `
        ReceiverOperatorToken $ReceiverOperatorToken `
        ReceiverOperatorTokenFile $ReceiverOperatorTokenFile
    if ([string]::IsNullOrWhiteSpace($resolvedOperatorToken) -and
        -not [string]::IsNullOrWhiteSpace($ReceiverConfigurationPath)) {
        $resolvedOperatorToken = [string](Get-ReceiverConfiguration).operator.token
    }
    Assert-ValuePresent ReceiverOperatorToken $resolvedOperatorToken

    $status = Get-PtkStatus $Token
    $destination = Get-SelectedDestination $status
    $configuredAuthority = $Endpoint.GetLeftPart([UriPartial]::Authority)
    $statusAuthority = ([string]$destination.endpoint_summary) -replace '/[^/]+$', ''
    if ($configuredAuthority -cne $statusAuthority) {
        throw "Endpoint '$configuredAuthority' does not match explicitly configured destination '$statusAuthority'. No receiver was contacted."
    }
    if ($ReceiverOperatorUri.Host -cne $Endpoint.Host) {
        throw 'Receiver operator host does not match the explicitly configured ingest host. No receiver was contacted.'
    }
    if (-not $destination.enabled) {
        throw "Destination '$DestinationId' is configured but disabled. No receiver was contacted."
    }

    $health = Invoke-JsonRequest -Method GET `
        -Uri (Join-EndpointUri $ReceiverOperatorUri '/api/health') `
        -BearerToken $resolvedOperatorToken `
        -CertificateSha256 $destination.server_certificate_sha256
    $marker = 'PTK-SIEM-DOCTOR:' + [guid]::NewGuid().ToString('D')
    Invoke-SyntheticPtkCall $marker

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($DoctorTimeoutSeconds)
    $activity = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $query = [uri]::EscapeDataString($marker)
        $result = Invoke-JsonRequest -Method GET `
            -Uri (Join-EndpointUri $ReceiverOperatorUri "/api/activities?query=$query&limit=10") `
            -BearerToken $resolvedOperatorToken `
            -CertificateSha256 $destination.server_certificate_sha256
        $activity = @($result.activities | Where-Object {
            $_.command.preview -like "*$marker*"
        } | Select-Object -First 1)
        if ($activity.Count -eq 1 -and
            $activity[0].command.availability -ceq 'destination' -and
            $activity[0].response.availability -ceq 'destination') {
            $activity = $activity[0]
            break
        }
        $activity = $null
        Start-Sleep -Milliseconds 500
    }
    if ($null -eq $activity) {
        throw "Destination '$DestinationId' did not acknowledge and return the named doctor activity within $DoctorTimeoutSeconds seconds."
    }

    $detail = Invoke-JsonRequest -Method GET `
        -Uri (Join-EndpointUri $ReceiverOperatorUri "/api/activities/$($activity.activity_id)") `
        -BearerToken $resolvedOperatorToken `
        -CertificateSha256 $destination.server_certificate_sha256
    [pscustomobject]@{
        destination_id = [string]$DestinationId
        configured_destination_only = $true
        health_status = if ($health.integrity.status -ceq 'intact' -and
            $health.custody.status -ceq 'healthy') {
            'healthy'
        } else {
            'attention_required'
        }
        synthetic_marker = $marker
        activity_id = $activity.activity_id
        activity_state = $detail.activity.state
        command_acknowledged = $detail.activity.command.availability -ceq 'destination'
        response_acknowledged = $detail.activity.response.availability -ceq 'destination'
        ptk_restart_required = $false
    }
}

$token = Get-UiToken
switch ($Action) {
    'List' {
        Get-PtkStatus $token
    }
    'Add' {
        $result = Invoke-JsonRequest -Method POST `
            -Uri (Join-EndpointUri $UiUri '/api/destinations') `
            -BearerToken $token -Body (Get-DestinationBody)
        [pscustomobject]@{
            operation = 'add'
            destination = $result.destination
            ptk_restart_required = $false
        }
    }
    'Update' {
        Assert-ValuePresent DestinationId $DestinationId
        $result = Invoke-JsonRequest -Method PUT `
            -Uri (Join-EndpointUri $UiUri "/api/destinations/$DestinationId") `
            -BearerToken $token -Body (Get-DestinationBody)
        [pscustomobject]@{
            operation = 'update'
            destination = $result.destination
            ptk_restart_required = $false
        }
    }
    { $_ -in 'Enable', 'Disable', 'Remove' } {
        Assert-ValuePresent DestinationId $DestinationId
        $operation = $Action.ToLowerInvariant()
        $result = Invoke-JsonRequest -Method POST `
            -Uri (Join-EndpointUri $UiUri "/api/destinations/$DestinationId/$operation") `
            -BearerToken $token -Body @{}
        [pscustomobject]@{
            operation = $operation
            destination = $result.destination
            ptk_restart_required = $false
        }
    }
    'Doctor' {
        Invoke-Doctor $token
    }
}
