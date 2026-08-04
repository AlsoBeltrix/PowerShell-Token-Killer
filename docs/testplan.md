# ptk release acceptance

A test specification for an installed ptk. Work through the claims, try to
falsify each one, and file a single report.

## Constraints

- Testing only. Do not modify ptk's source, scripts, or installed payload.
  Put probes in a temp directory.
- Report defects; do not fix them.
- Do not tag, publish, or edit a release.
- A check that cannot run on your machine is "not tested", never "passed".

## Orientation

Before testing, establish from the running server what ptk is, what each tool
accepts, and what it claims to do. The tool descriptions and `ptk_state` are
the source. If you cannot arrive at an accurate picture from those, stop and
report that: a test suite written against a misunderstanding is worse than no
suite, and the descriptions are themselves part of what ships.

State your understanding in the report. Where a claim below rests on a detail
you could not confirm from the server, say so.

## Environment

Record this before anything else.

```powershell
$ptk = Join-Path $HOME '.ptk'
$exe = if ($IsWindows) { "$ptk/bin/PtkMcpServer.exe" } else { "$ptk/bin/PtkMcpServer" }
[pscustomobject]@{
    OS         = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    Arch       = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    Pwsh       = $PSVersionTable.PSVersion.ToString()
    PtkVersion = Get-Content "$ptk/VERSION"
    Assembly   = [Diagnostics.FileVersionInfo]::GetVersionInfo("$ptk/bin/PtkMcpServer.dll").ProductVersion
    Module     = (Import-PowerShellDataFile "$ptk/src/PwshTokenCompressor.psd1").ModuleVersion
    Rtk        = (Get-Command rtk -ErrorAction SilentlyContinue).Source
    RtkVersion = (& rtk --version 2>$null)
} | Format-List
```

## Driving the server

Either drive it as an MCP client, or over stdio. State which you used. Work in
your own named session so a shared installation is not disturbed.

Startup, refusal, and crash behaviour are only observable over stdio: start
`$exe` and exchange one JSON object per line — `initialize`, the
`notifications/initialized` notification, then `tools/call`.

```powershell
$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $exe
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$srv = [Diagnostics.Process]::Start($psi)
$id = 0
function Rpc($method, $params) {
    $body = @{ jsonrpc = '2.0'; method = $method; id = ++$script:id }
    if ($params) { $body.params = $params }
    $srv.StandardInput.WriteLine(($body | ConvertTo-Json -Depth 12 -Compress))
    $srv.StandardInput.Flush()
    while ($true) {
        $line = $srv.StandardOutput.ReadLine()
        if ($null -eq $line) { throw 'server closed stdout' }
        if ($line.StartsWith('{')) {
            $m = $line | ConvertFrom-Json
            if ($m.id -eq $script:id) { return $m }
        }
    }
}
function Invoke-Ptk($script, $session = 'default', $extra = @{}) {
    $a = @{ script = $script; session = $session }
    $extra.GetEnumerator() | ForEach-Object { $a[$_.Key] = $_.Value }
    $r = Rpc 'tools/call' @{ name = 'ptk_invoke'; arguments = $a }
    ($r.result.content | Where-Object type -eq 'text' | ForEach-Object text) -join "`n"
}
Rpc 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'acc'; version = '1' } } | Out-Null
```

## A. Install and uninstall

Installer tests mutate `~/.ptk`. If a live ptk is serving from it, run them
against an isolated fake home and record that you did.

- **A1** A fresh install from the release asset, using `install.ps1` or
  `install.sh` rather than unpacking the archive by hand, produces a server
  that starts.
- **A2** An asset whose bytes do not match `SHA256SUMS` is refused.
- **A3** On a machine with no rtk on `PATH`, the installer downloads rtk,
  verifies it against rtk's `checksums.txt`, and confirms it answers before
  reporting success.
- **A4** Installing over an existing install replaces the payload and leaves
  user-owned files under `~/.ptk` intact.
- **A5** A failure between payload activation and registration restores the
  prior payload byte-identically.
- **A6** `install.sh` executes under dash, not only bash.
- **A7** Uninstall removes the payload, the MCP registration, and on Windows
  the Add/Remove Programs entry, while keeping user-owned files unless
  `--purge` / `-Purge`.
- **A8** Uninstall removes only an rtk the installer placed, recorded in
  `~/.ptk/.ptk-installed-rtk`. An rtk the user already had is left alone.
- **A9** The installer does not report success on a machine where the server
  will refuse to start.

## B. Product contract

Against the installed server.

- **B1** Exactly five tools are exposed.
- **B2** A named session retains warm state across invocations.
- **B3** Object output is compressed rather than formatted — 40 objects
  summarise rather than dumping.
- **B4** Plain text is returned as text.
- **B5** Output over the inline bound returns a `ptk_output` handle, and the
  handle recovers the content.
- **B6** An invocation that exceeds its timeout reports the timeout and does
  not prevent a later invocation on that session from succeeding. A session
  whose worker is being replaced reports `session_recovering` and executes
  nothing; poll until it clears.
- **B7** `ptk_reset` and `ptk_session action=close` both succeed.
- **B8** With no rtk resolvable, the server exits 78 and names
  `PTK_RTK_PATH` on stderr. Launch it with a cleared environment block —
  setting `PATH` to empty in an inherited environment does not necessarily
  remove rtk from resolution.

With a checkout, `server/direct-product-proof.ps1 -ServerPath <exe>` covers
B1–B8.

## C. Output shaping

The shaper renders values from trusted assemblies — the .NET runtime
directory, `System.Management.Automation`, and
`Microsoft.PowerShell.Commands.Utility` — by calling `ToString()`. Values from
any other assembly yield `[active member not evaluated]`.

The invariant: capture must never execute user code. A type from a dynamic or
location-less assembly, which is what `Add-Type` produces, must never have a
getter or `ToString()` invoked.

- **C1** A framework type such as `[System.Globalization.CultureInfo]` renders
  its value.
- **C2** An `Add-Type` type whose `ToString()` increments a static counter:
  the counter stays at zero and the output is the placeholder.
- **C3** An `Add-Type` type deriving from a framework type is treated as
  untrusted.
- **C4** A trusted generic parameterised over a user type — `List<UserType>`,
  `Nullable<UserStruct>` — does not execute the user type's members.
- **C5** A framework type with a slow or blocking `ToString()` does not stall
  capture. Rendering runs on the producer callback.
- **C6** Many large renderings in one invocation stay within the projection
  budget and the response stays bounded.
- **C7** A rendered value marks the capture `passive_projection_lossy`; a
  skipped active member marks it `active_member_not_evaluated`.
- **C8** An exception from a trusted assembly surfaces its message; an
  exception from an `Add-Type` assembly does not.
- **C9** Native command stderr redirected with `2>&1` arrives as text, not as
  a placeholder.

## D. Sessions

- **D1** Eight sessions open concurrently; the ninth is refused with a clear
  reason.
- **D2** Invocations in different sessions run concurrently and do not share
  warm state, including for identically named variables and functions.
- **D3** Invocations within one session serialise. A queued call whose budget
  expires before it starts fails without executing.
- **D4** Repeated timeout-and-recovery cycles on one session recover each
  time.
- **D5** After a worker process is killed, the next invocation on that session
  succeeds.
- **D6** Several sessions producing large output concurrently each yield a
  usable `ptk_output` handle.
- **D7** `ptk_state` on a healthy session does not disturb it, including when
  the call is cancelled.

## E. Routing

ptk accepts a rewrite from rtk only when it binds the absolute rtk path
pinned at startup, stripping `rtk ` prefixes reproduces the submitted text
byte-for-byte, and every wrapped name resolves to a native application in the
session.

- **E1** A session-defined `function git` is not displaced by a rewrite of
  `git status`. The function runs.
- **E2** Quoted arguments containing spaces and embedded quotes survive a
  rewrite unchanged.
- **E3** A script rtk declines executes unchanged, with its exit code and
  stderr intact.
- **E4** `route=pwsh` never routes through rtk. `route=rtk` on a script rtk
  declines reports a labelled fallback and executes the original once.
- **E5** A script containing the literal token `rtk` cannot be made to reduce
  correctly while executing something other than what was submitted.
- **E6** A compound command routes and returns compressed output.

## F. Security

- **F1** The installer refuses to run as root or Administrator.
- **F2** Windows: scan the installed payload with current Defender. Report
  detections, and whether the payload survives. Issue #7 tracks a prior false
  positive on `PtkMcpServer.dll`.
- **F3** macOS: the binaries are ad-hoc signed and not notarised. Report what
  a user encounters installing via `install.sh`, and whether Gatekeeper
  requires an override.
- **F4** The server runs with the invoking user's privileges and does not
  escalate.
- **F5** A command child cannot observe the worker's protocol descriptors.

## G. Mutation checks

These verify that a guard fails when the behaviour it guards is broken. Each
needs a checkout and the .NET SDK. Revert the change afterwards.

- **G1** macOS ARM64. In `server/PtkMcpServer/Worker/UnixCloseOnExec.cs`,
  route `TrySet` back through a fixed-signature P/Invoke to variadic `fcntl`
  with `F_SETFD` instead of `ioctl(FIOCLEX)`, then run the
  `UnixCloseOnExec` tests. Report whether they fail. If they pass, the guard
  does not detect the ABI mismatch it exists for, and the behaviour needs a
  test that observes descriptor inheritance across `exec` instead.

## Reporting

One report for the run. A defect blocks the release only if it:

- executes a different command than submitted, loses user data, or repeats
  execution;
- prevents install, launch, registration, ordinary use, or uninstall;
- breaks a named session, or prevents recovery after a timeout or crash;
- makes one of the five tools return materially wrong or unrecoverable
  output;
- exposes a security defect in the default configuration; or
- causes the Windows artifact to be quarantined by current Defender.

Classify each finding against that rule, or as unsure. Include confirmations
and untested items; both change what the release knows about itself.

```powershell
gh issue create `
  --repo AlsoBeltrix/PowerShell-Token-Killer `
  --title "Release acceptance: <version> on <rid>" `
  --body-file results.md
```

```markdown
## Environment
<the table above>
Driven via: <MCP client | stdio>
Installed from: <asset or method>

## Summary
<n> run, <n> passed, <n> failed, <n> not tested. Blockers: <n>

## Blockers
### <ID> — <claim>
Command:
    <exact command>
Expected:
Observed:
Rule:

## Non-blocking
<same shape, with why it does not block>

## Unsure
<finding and reasoning>

## Passed
<IDs, one line each>

## Not tested
<IDs and why>
```

If a report already exists for this version, comment on it with your platform
rather than opening another.
