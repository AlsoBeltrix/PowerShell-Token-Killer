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
    [int]$TimeoutSec = 120,

    # Slice 7.5 check 10 — uninstall, and prove the installed launch path is
    # gone. Destructive, so it is opt-in and takes the home to remove
    # explicitly rather than inferring one: a proof script must never be able
    # to delete the operator's real ~/.ptk by default. Pass the same throwaway
    # home the candidate was installed into.
    [string]$UninstallHome
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
# Audit is base-level and non-bypassable (audit-restoration R6): the packaged
# product must journal every call, and this proof judges that positively on
# its own throwaway root rather than writing into the operator's real one.
# Under $HOME, not the temp dir: protected-path admission refuses symlinked
# components, and macOS's /var temp path is one (the handshake's precedent).
$script:auditRoot = Join-Path $HOME `
    ('.ptk-proof-audit-' + [guid]::NewGuid().ToString('N'))
$psi.Environment['PTK_AUDIT_ROOT'] = $script:auditRoot
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

    # 13 (plan Slice 7.5): a fresh session exposes the shipped `ls` alias and
    # autoloads no user module. PSModuleAutoloadingPreference=None is what
    # keeps two sessions on one machine from diverging by whatever was
    # invoked first; a regression here is silent and only shows up as
    # inconsistent behaviour much later.
    # An alias, not a function shadowing it: an autoloaded user function does
    # NOT override a built-in alias, so a bare "ls resolves" test would pass
    # in a session that had already drifted.
    $alias = InvokeTool -Script @'
$c = Get-Command ls
"{0}|{1}|{2}" -f $c.CommandType, $c.Definition, $c.ResolvedCommand.Source
'@
    if ($IsWindows) {
        Report 'exposes the shipped ls alias' `
            ($alias -match 'Alias\|Get-ChildItem\|Microsoft\.PowerShell\.Management') `
            ($alias -split "`n")[0]
    }
    else {
        # POSIX PowerShell deliberately ships no ls alias: a clean session
        # binds ls to the native application, and an alias or function here
        # means the session drifted. The unconditional Windows assertion
        # above failed all three POSIX legs of the first per-RID gate run
        # (rc.3, run 31184268679) -- the check was authored on the win-x64
        # leg and had never executed on POSIX.
        Report 'binds ls to the native ls application' `
            ($alias -match 'Application\|/(usr/)?bin/ls\|') `
            ($alias -split "`n")[0]
    }

    # Compared as a string: the preference is an enum the shaper renders as
    # an object, so a naive -match on the raw response reads the type name.
    $autoload = InvokeTool -Script '"pref=[$PSModuleAutoloadingPreference]"'
    Report 'autoloads no user module' ($autoload -match 'pref=\[None\]') ($autoload -split "`n")[0]
}
finally {
    try { $stdin.Close() } catch { }
    if (-not $server.WaitForExit(15000)) {
        $server.Kill($true)
        # The root is inspected and deleted below; a killed server must be
        # fully gone first or deletion races its open journal handles.
        $null = $server.WaitForExit(15000)
    }

    # 14 (audit-restoration R6): the packaged product journaled the calls
    # above. Positive and CALL-level: lifecycle records (server.started) are
    # journaled independently of call auditing, so their presence proves
    # nothing about the call filter (cr9-1). The gate demands a call
    # admission, a terminal call outcome, and a record naming this proof's
    # own named session. Inside the finally (cr9-5) so a mid-proof
    # exception still judges and removes the throwaway root.
    $auditArtifacts = if (Test-Path -LiteralPath $script:auditRoot) {
        @(Get-ChildItem -LiteralPath $script:auditRoot -Recurse -Force -File |
            Where-Object Length -gt 0)
    }
    else {
        @()
    }
    Report 'journals the proof''s calls under the audit root' `
        ($auditArtifacts.Count -gt 0) "artifacts=$($auditArtifacts.Count)"
    $sawAccepted = $false
    $sawTerminal = $false
    $sawProofSession = $false
    foreach ($artifact in $auditArtifacts) {
        $text = Get-Content -LiteralPath $artifact.FullName -Raw
        if ($text -match '"event_type":"call\.accepted"') { $sawAccepted = $true }
        if ($text -match '"event_type":"call\.(completed|failed)"') { $sawTerminal = $true }
        if ($text -match '"name":"proof"') { $sawProofSession = $true }
    }
    Report 'journal carries call admissions and terminal outcomes' `
        ($sawAccepted -and $sawTerminal) "accepted=$sawAccepted terminal=$sawTerminal"
    Report 'journal attributes calls to the proof''s named session' $sawProofSession
    if (Test-Path -LiteralPath $script:auditRoot) {
        try {
            Remove-Item -LiteralPath $script:auditRoot -Recurse -Force -ErrorAction Stop
        }
        catch {
            # Never silent: the root lives under the operator's HOME.
            Write-Host "  WARNING: proof audit root not removed: $($script:auditRoot) -- $_"
        }
    }
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

# Plan Slice 7.5, Windows leg: scan the exact packaged bits with current
# Defender. GitHub #7's false positive on PtkMcpServer.dll is unresolved
# pending Microsoft's WDSI verdict, and quarantine of the supported Windows
# artifact is a release blocker in its own right — independent of that
# verdict, because a quarantined artifact cannot launch after install.
#
# Judged by whether the payload SURVIVES, not by the scanner's summary text:
# quarantine is the failure that matters. No EICAR control is written here —
# a release gate must not manufacture antivirus detections on the operator's
# machine.
if ($IsWindows) {
    $mp = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
    $binDir = Split-Path -Parent (Resolve-Path $ServerPath)
    # The gate must prove a scan happened, not merely that nothing was
    # quarantined: an absent or failing scanner leaves the payload trivially
    # "intact", and the survival check alone would pass without anything
    # having been scanned (r806-4). MpCmdRun exit 0 is a clean verdict and 2
    # is a threat verdict -- both mean the scan completed and the survival
    # check below judges the outcome; any other exit, or no MpCmdRun.exe at
    # all, fails the gate on the one platform whose artifact claims Defender
    # proof.
    if (Test-Path -LiteralPath $mp) {
        $before = @(Get-ChildItem -LiteralPath $binDir -File -Force).Count
        $scanOut = & $mp -Scan -ScanType 3 -File $binDir 2>&1 | Out-String
        $scanExit = $LASTEXITCODE
        $scanTail = ($scanOut -split "`r?`n" | Where-Object { $_.Trim() } |
            Select-Object -Last 1)
        Report 'Defender scan completes (#7)' ($scanExit -in 0, 2) `
            "exit=$scanExit $scanTail"
        Start-Sleep -Seconds 2
        $after = @(Get-ChildItem -LiteralPath $binDir -File -Force -ErrorAction SilentlyContinue).Count
        Report 'Defender leaves the packaged payload intact (#7)' `
            (($after -eq $before) -and (Test-Path -LiteralPath $ServerPath)) `
            "files before=$before after=$after"
    }
    else {
        Report 'Defender scan completes (#7)' $false 'MpCmdRun.exe not present'
    }
}

# 10 (plan Slice 7.5): uninstall, and prove the installed launch path is gone.
# Runs last because it removes the thing every check above needs. The install
# transaction was rewritten wholesale for issue #42 and had no uninstall guard
# at all, which is exactly the code least safe to leave hand-checked.
if ($UninstallHome) {
    $home_ = [IO.Path]::GetFullPath($UninstallHome)
    $server_ = [IO.Path]::GetFullPath((Resolve-Path $ServerPath))
    # Containment is a path-component test, not a string-prefix test: a
    # sibling '/proof/.ptk-old/bin' starts with the string '/proof/.ptk' yet
    # is not inside it, and a prefix check would let this destructive step
    # uninstall a home that does not contain the server under proof
    # (r806-3). GetRelativePath also compares with the platform's own case
    # sensitivity, where OrdinalIgnoreCase over-matched on POSIX.
    $rel_ = [IO.Path]::GetRelativePath($home_, $server_)
    if ([IO.Path]::IsPathRooted($rel_) -or $rel_ -eq '..' -or
        $rel_.StartsWith('..' + [IO.Path]::DirectorySeparatorChar)) {
        throw "Refusing to uninstall '$home_': it does not contain the server under proof ($server_)."
    }
    # The child below derives HOME from this path's parent and the installer
    # always targets $HOME/.ptk, so a home not literally named '.ptk' would
    # aim the uninstall at a different directory than the one just validated.
    if ((Split-Path -Leaf $home_) -ne '.ptk') {
        throw "Refusing to uninstall '$home_': the installer targets " +
            "`$HOME/.ptk, so the home under proof must be a directory named '.ptk'."
    }
    $installer = Join-Path $home_ 'scripts' 'install.ps1'
    if (-not (Test-Path -LiteralPath $installer)) {
        throw "Refusing to uninstall: no installed scripts/install.ps1 under '$home_'."
    }

    # The proof's own server holds an open handle on the binary, and on
    # Windows that makes the payload delete fail while the installer still
    # reports success. Wait for the process this script started to be gone
    # before judging the uninstall, or the check measures our own lock rather
    # than the product.
    foreach ($p in @($server) + @($gp)) {
        if ($p -and -not $p.HasExited) {
            try { $p.Kill($true) } catch { }
        }
        if ($p) { try { $null = $p.WaitForExit(30000) } catch { } }
    }
    Start-Sleep -Seconds 2

    # The installer resolves its home from $HOME. On Windows PowerShell
    # derives $HOME from HOMEDRIVE+HOMEPATH, not USERPROFILE, so setting
    # USERPROFILE alone silently leaves the child pointed at the real home
    # and the uninstall no-ops against a payload that is not there.
    $parent_ = Split-Path -Parent $home_
    $env_ = @{ USERPROFILE = $parent_; HOME = $parent_ }
    if ($IsWindows) {
        $env_['HOMEDRIVE'] = [IO.Path]::GetPathRoot($parent_).TrimEnd('\')
        $env_['HOMEPATH'] = $parent_.Substring($env_['HOMEDRIVE'].Length)
    }
    $psiU = [Diagnostics.ProcessStartInfo]::new([Environment]::ProcessPath)
    foreach ($a in '-NoProfile', '-File', $installer, '-Uninstall') { $psiU.ArgumentList.Add($a) }
    foreach ($k in $env_.Keys) { $psiU.EnvironmentVariables[$k] = $env_[$k] }
    $psiU.UseShellExecute = $false
    $psiU.RedirectStandardOutput = $true
    $psiU.RedirectStandardError = $true
    $pu = [Diagnostics.Process]::Start($psiU)
    $uOut = $pu.StandardOutput.ReadToEnd() + $pu.StandardError.ReadToEnd()
    if (-not $pu.WaitForExit(180000)) { $pu.Kill($true) }

    Report 'uninstall completes' ($pu.ExitCode -eq 0) "exit=$($pu.ExitCode)"
    Report 'uninstall removes the installed launch path' `
        (-not (Test-Path -LiteralPath $server_)) $server_
}


Write-Host ''
if ($script:failures.Count -gt 0) {
    Write-Host ("DIRECT PROOF FAILED: {0} of {1} checks" -f $script:failures.Count, $script:checks)
    $script:failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host ("DIRECT PROOF PASSED: {0} checks" -f $script:checks)
