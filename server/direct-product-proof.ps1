#!/usr/bin/env pwsh
#Requires -Version 7
<#
.SYNOPSIS
Slice 7.5 direct product proof: drives an INSTALLED ptk candidate over real
MCP stdio and checks the release contract end to end.

.DESCRIPTION
The handshake script proves the transport works. This proves the product
promises hold on the packaged bits a user actually receives: the five tools,
warm named sessions, object compression, text preservation, bounded-output
recovery, timeout recovery, reset/close, and the RTK startup gate.

Records commands and outcomes. No scores, no derived metrics.

.EXAMPLE
pwsh -File server/direct-product-proof.ps1 -ServerPath ~/.ptk/bin/PtkMcpServer.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerPath,
    [int]$TimeoutSec = 120
)

$ErrorActionPreference = 'Stop'
$script:failures = @()
$script:checks = 0

function Report {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    $script:checks++
    if ($Ok) {
        Write-Host ("  ok   {0}" -f $Name)
    }
    else {
        Write-Host ("  FAIL {0}{1}" -f $Name, $(if ($Detail) { " -- $Detail" } else { '' }))
        $script:failures += $Name
    }
}

# --- MCP stdio client ------------------------------------------------------

$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = (Resolve-Path $ServerPath)
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$server = [Diagnostics.Process]::Start($psi)
$stdin = $server.StandardInput
$stdout = $server.StandardOutput
$script:nextId = 0

function Send {
    param([string]$Method, [hashtable]$Params, [switch]$Notify)
    $body = @{ jsonrpc = '2.0'; method = $Method }
    if ($Params) { $body.params = $Params }
    if (-not $Notify) { $body.id = ++$script:nextId }
    $stdin.WriteLine(($body | ConvertTo-Json -Depth 12 -Compress))
    $stdin.Flush()
    if ($Notify) { return $null }

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSec)
    while ([datetime]::UtcNow -lt $deadline) {
        $line = $stdout.ReadLine()
        if ($null -eq $line) { throw 'server closed stdout' }
        if (-not $line.StartsWith('{')) { continue }
        $msg = $line | ConvertFrom-Json
        if ($msg.PSObject.Properties.Name -contains 'id' -and $msg.id -eq $script:nextId) {
            return $msg
        }
    }
    throw "timed out waiting for a response to $Method"
}

function InvokeTool {
    param([string]$Script, [string]$Session = 'default', [hashtable]$Extra = @{})
    $args = @{ script = $Script; session = $Session }
    foreach ($k in $Extra.Keys) { $args[$k] = $Extra[$k] }
    $r = Send -Method 'tools/call' -Params @{ name = 'ptk_invoke'; arguments = $args }
    if ($r.error) { return "ERROR: $($r.error.message)" }
    ($r.result.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n"
}

try {
    Write-Host "Direct product proof against: $ServerPath"

    $init = Send -Method 'initialize' -Params @{
        protocolVersion = '2024-11-05'
        capabilities    = @{}
        clientInfo      = @{ name = 'ptk-direct-proof'; version = '1' }
    }
    Report 'server initializes' ($null -ne $init.result)
    Send -Method 'notifications/initialized' -Notify | Out-Null

    # 3. exactly the five supported tools
    $tools = (Send -Method 'tools/list').result.tools.name | Sort-Object
    $expected = @('ptk_invoke', 'ptk_output', 'ptk_reset', 'ptk_session', 'ptk_state')
    Report 'exposes exactly the five tools' (-not (Compare-Object $tools $expected)) ($tools -join ',')

    # 4. named session retains warm state across invocations
    # Assert the open actually succeeded: silently discarding this result let a
    # wrong argument name masquerade as a warm-state product failure.
    $opened = Send -Method 'tools/call' -Params @{
        name = 'ptk_session'; arguments = @{ action = 'open'; name = 'proof' }
    }
    $openedText = ($opened.result.content | Where-Object { $_.type -eq 'text' } |
        ForEach-Object { $_.text }) -join "`n"
    Report 'opens a named session' (-not $opened.error -and $openedText -notmatch 'refused') $openedText
    InvokeTool -Script '$global:proofValue = 4242' -Session 'proof' | Out-Null
    $warm = InvokeTool -Script '$global:proofValue' -Session 'proof'
    Report 'named session keeps warm state' ($warm -match '4242') $warm

    # 5. PowerShell objects are compressed rather than formatted to text
    $objects = InvokeTool -Script '1..40 | ForEach-Object { [pscustomobject]@{ Row = $_; Name = "r$_" } }'
    Report 'compresses object output' ($objects -match 'objects: 40') ($objects -split "`n")[0]

    # Slice 7.0: a type outside the shaper allowlist keeps its value.
    $culture = InvokeTool -Script 'Get-Culture'
    Report 'renders a trusted type instead of dropping it' `
        ($culture -notmatch 'active member not evaluated') ($culture -split "`n")[0]

    # Issue #42: the check above passed against a payload missing 185 of its
    # 296 assemblies, because CultureInfo needs none of them. Process does --
    # its ETS members reach System.Collections.NonGeneric -- so a truncated
    # payload fails here with an ExtendedTypeSystemException wrapping a
    # FileNotFoundException, or returns nothing at all. This is the cheap
    # end of the check; the installer's own staged-vs-installed comparison is
    # the real guard.
    $process = InvokeTool -Script '$p = Get-Process -Id $PID; "proof_pid=" + $p.Id'
    Report 'materializes a type needing the wider assembly set' `
        ($process -match 'proof_pid=\d+') ($process -split "`n")[0]

    # 6. plain text survives as text
    $text = InvokeTool -Script "'plain-sentinel-text'"
    Report 'preserves plain text' ($text -match 'plain-sentinel-text')

    # 7. bounded large output is recoverable through ptk_output
    $big = InvokeTool -Script '1..500 | ForEach-Object { "line-$_" }'
    $handle = if ($big -match 'handle=(ptko_[A-Za-z0-9_\-]+)') { $Matches[1] } else { $null }
    Report 'bounded output advertises a recovery handle' ($null -ne $handle)
    if ($handle) {
        $rec = Send -Method 'tools/call' -Params @{
            name = 'ptk_output'; arguments = @{ handle = $handle; action = 'read'; maxBytes = 4096 }
        }
        $recText = ($rec.result.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n"
        Report 'recovers the captured output' ($recText -match 'line-1\b')
    }

    # 8. a timeout does not prevent the next invocation
    $timedOut = InvokeTool -Script 'Start-Sleep -Seconds 30' -Session 'proof' -Extra @{ timeoutSeconds = 2 }
    Report 'reports the timeout' ($timedOut -match 'timed out|timeout|not executed|NOT executed') ($timedOut -split "`n")[0]
    # A timed-out session replaces its worker; until that lands, calls report
    # session_recovering and execute nothing. The contract is that a later
    # invocation succeeds, not that the very next one wins the race.
    $after = ''
    $recoveryDeadline = [datetime]::UtcNow.AddSeconds(60)
    do {
        $after = InvokeTool -Script '7 * 6' -Session 'proof'
        if ($after -match '\b42\b') { break }
        Start-Sleep -Milliseconds 500
    } while ([datetime]::UtcNow -lt $recoveryDeadline -and $after -match 'session_recovering')
    Report 'a later invocation succeeds after the timeout' ($after -match '\b42\b') $after

    # 9. reset and close a named session
    $reset = Send -Method 'tools/call' -Params @{ name = 'ptk_reset'; arguments = @{ session = 'proof' } }
    Report 'resets a named session' ($null -ne $reset.result -and -not $reset.error)
    $closed = Send -Method 'tools/call' -Params @{
        name = 'ptk_session'; arguments = @{ action = 'close'; name = 'proof' }
    }
    Report 'closes a named session' ($null -ne $closed.result -and -not $closed.error)

    # 11. a compound native command routes through RTK
    $compound = InvokeTool -Script 'git --no-pager log --oneline -3 && git status --short'
    Report 'routes a compound native command' ($compound -notmatch 'ERROR:') ($compound -split "`n")[0]
}
finally {
    try { $stdin.Close() } catch { }
    if (-not $server.WaitForExit(15000)) { $server.Kill($true) }
}

# 12. with RTK absent, startup fails with the actionable message
$gate = [Diagnostics.ProcessStartInfo]::new()
$gate.FileName = (Resolve-Path $ServerPath)
$gate.UseShellExecute = $false
$gate.RedirectStandardError = $true
$gate.EnvironmentVariables.Clear()
if ($IsWindows) { $gate.EnvironmentVariables['SystemRoot'] = $env:SystemRoot }
$gp = [Diagnostics.Process]::Start($gate)
$gateText = $gp.StandardError.ReadToEnd()
if (-not $gp.WaitForExit(60000)) { $gp.Kill($true) }
Report 'refuses to start without RTK (exit 78)' ($gp.ExitCode -eq 78) "exit=$($gp.ExitCode)"
Report 'refusal names PTK_RTK_PATH' ($gateText -match 'PTK_RTK_PATH')

Write-Host ''
if ($script:failures.Count -gt 0) {
    Write-Host ("DIRECT PROOF FAILED: {0} of {1} checks" -f $script:failures.Count, $script:checks)
    $script:failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host ("DIRECT PROOF PASSED: {0} checks" -f $script:checks)
