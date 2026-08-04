# ptk test plan

Test the ptk MCP server and find defects in it. Report what you find; change
nothing.

## How to run the thing you are testing

You may already be connected to a ptk MCP server. Do not test that one. It is
whatever version happens to be installed on this machine, another session may
depend on it, and you cannot reinstall or restart it from inside your own
connection.

Instead, install a candidate into a directory you own and drive that binary
yourself as a child process over stdio. Everything in this plan is testable
that way, including install, startup refusal, and crash recovery, none of
which are reachable through an existing MCP connection.

```powershell
# A private home for the candidate. Never ~/.ptk.
$root = Join-Path ([IO.Path]::GetTempPath()) "ptk-test-$(Get-Random)"
$env:HOME = $root          # installers install here
$env:USERPROFILE = $root   # Windows
```

Get a candidate. In order of preference:

1. A published release: run `install.ps1` (Windows) or `install.sh`
   (macOS/Linux) from the repository root. This is the only option that tests
   the installer, so prefer it whenever a release exists.
2. A release archive you were given: unpack it into `$root/.ptk`. Section A is
   then not tested — say so.
3. A checkout with the .NET SDK:
   `pwsh -File scripts/dev-install.ps1 -LayoutOnly -OutputDir $root/.ptk`.
   Section A is not tested — say so.

The server needs rtk on `PATH` or at `$root/.ptk/bin/rtk`; it exits 78
without one.

Speak JSON-RPC on its stdin/stdout, one object per line: `initialize`, the
`notifications/initialized` notification, then `tools/call`.

```powershell
$exe = if ($IsWindows) { "$root/.ptk/bin/PtkMcpServer.exe" } else { "$root/.ptk/bin/PtkMcpServer" }
if (-not $IsWindows) { chmod +x $exe }

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
function Call($tool, $arguments) {
    $r = Rpc 'tools/call' @{ name = $tool; arguments = $arguments }
    if ($r.error) { return "ERROR: $($r.error.message)" }
    ($r.result.content | Where-Object type -eq 'text' | ForEach-Object text) -join "`n"
}
Rpc 'initialize' @{
    protocolVersion = '2024-11-05'; capabilities = @{}
    clientInfo = @{ name = 'test'; version = '1' }
} | Out-Null
Rpc 'notifications/initialized' $null | Out-Null   # notification: no id, no reply
```

Record before testing:

```powershell
[pscustomobject]@{
    OS         = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    Arch       = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    Pwsh       = $PSVersionTable.PSVersion.ToString()
    Candidate  = Get-Content "$root/.ptk/VERSION"
    Assembly   = [Diagnostics.FileVersionInfo]::GetVersionInfo("$root/.ptk/bin/PtkMcpServer.dll").ProductVersion
    Module     = (Import-PowerShellDataFile "$root/.ptk/src/PwshTokenCompressor.psd1").ModuleVersion
    Rtk        = (Get-Command rtk -ErrorAction SilentlyContinue).Source
    RtkVersion = (& rtk --version 2>$null)
    Obtained   = '<installer | archive | checkout>'
} | Format-List
```

## Constraints

- Do not modify ptk's source, scripts, or any installed payload.
- Do not touch `~/.ptk`. Work only inside your private root.
- Report defects; do not fix them.
- Do not tag, publish, or edit a release.
- A check you could not run is "not tested", never "passed".

## Orientation

Establish from the running server what ptk is and what each tool accepts.
`tools/list` and `ptk_state` are the source. The tool descriptions ship to
model callers, so if you cannot form an accurate picture from them, that is a
finding — report it rather than working around it.

State your understanding in the report, and flag any claim below that rests on
a detail you could not confirm.

## A. Install and uninstall

Only if you installed from a release. Everything here runs against your
private root.

- **A1** A fresh install completes and the installed binary starts.
- **A2** An asset whose bytes do not match `SHA256SUMS` is refused.
- **A3** With no rtk on `PATH`, the installer obtains one, verifies it against
  rtk's `checksums.txt`, and confirms it answers before reporting success.
- **A4** Installing over an existing install replaces the payload and leaves
  user-owned files in the ptk home intact.
- **A5** A failure between payload activation and registration restores the
  prior payload byte-identically.
- **A6** `install.sh` runs under dash, not only bash.
- **A7** Uninstall removes the payload, the MCP registration, and on Windows
  the Add/Remove Programs entry, keeping user-owned files unless
  `--purge` / `-Purge`.
- **A8** Uninstall removes only an rtk the installer placed, recorded in
  `.ptk-installed-rtk`. An rtk that was already present is untouched.
- **A9** The installer does not report success on a machine where the server
  will refuse to start.

## B. Product contract

- **B1** Exactly five tools are exposed.
- **B2** A named session retains warm state across invocations.
- **B3** Object output is compressed, not formatted — 40 objects summarise
  rather than dumping.
- **B4** Plain text comes back as text.
- **B5** Output past the inline bound returns a `ptk_output` handle that
  recovers the content.
- **B6** An invocation that exceeds its timeout reports the timeout and does
  not stop a later invocation on that session from succeeding. While the
  worker is being replaced the session reports `session_recovering` and
  executes nothing; poll until it clears.
- **B7** `ptk_reset` and closing a named session both succeed.
- **B8** With no rtk resolvable, the server exits 78 and names
  `PTK_RTK_PATH` on stderr. Launch it with a cleared environment block —
  emptying `PATH` in an inherited environment does not necessarily hide rtk.

## C. Output shaping

The shaper renders values from trusted assemblies — the .NET runtime
directory, `System.Management.Automation`, and
`Microsoft.PowerShell.Commands.Utility` — with `ToString()`. Values from any
other assembly yield `[active member not evaluated]`.

The invariant: capture must never execute user code. A type from a dynamic or
location-less assembly, which is what `Add-Type` produces, must never have a
getter or `ToString()` invoked.

- **C1** A framework type such as `[System.Globalization.CultureInfo]` renders
  its value.
- **C2** An `Add-Type` type whose `ToString()` increments a static counter:
  the counter stays at zero and the output is the placeholder.
- **C3** An `Add-Type` type deriving from a framework type is untrusted.
- **C4** A trusted generic parameterised over a user type — `List<UserType>`,
  `Nullable<UserStruct>` — does not execute the user type's members.
- **C5** A framework type with a slow or blocking `ToString()` does not stall
  capture. Rendering runs on the producer callback.
- **C6** Many large renderings in one invocation stay within the projection
  budget and the response stays bounded.
- **C7** A rendered value marks the capture `passive_projection_lossy`; a
  skipped active member marks it `active_member_not_evaluated`.
- **C8** An exception from a trusted assembly surfaces its message; one from
  an `Add-Type` assembly does not.
- **C9** Native stderr redirected with `2>&1` arrives as text, not a
  placeholder.

## D. Sessions

- **D1** Eight sessions open at once; the ninth is refused with a clear
  reason.
- **D2** Invocations in different sessions run concurrently and share no warm
  state, including identically named variables and functions.
- **D3** Invocations within one session serialise. A queued call whose budget
  expires before it starts fails without executing.
- **D4** Repeated timeout-and-recovery cycles on one session recover every
  time.
- **D5** After its worker process is killed, the next invocation on that
  session succeeds.
- **D6** Several sessions producing large output concurrently each yield a
  usable `ptk_output` handle.
- **D7** `ptk_state` on a healthy session does not disturb it, including when
  the call is cancelled.

## E. Routing

ptk submits the exact script text to `rtk hook check --agent ptk` and accepts
a rewrite only when it binds the absolute rtk path pinned at startup,
stripping `rtk ` prefixes reproduces the submitted text byte-for-byte, and
every wrapped name resolves to a native application in the session.

- **E1** A session-defined `function git` is not displaced by a rewrite of
  `git status`. The function runs.
- **E2** Quoted arguments with spaces and embedded quotes survive a rewrite
  unchanged.
- **E3** A script rtk declines executes unchanged, with its exit code and
  stderr intact.
- **E4** `route=pwsh` never routes through rtk. `route=rtk` on a declined
  script reports a labelled fallback and executes the original once.
- **E5** A script containing the literal token `rtk` cannot be made to reduce
  correctly while executing something other than what was submitted.
- **E6** A compound command routes and returns compressed output.

## F. Security

- **F1** The installer refuses to run as root or Administrator.
- **F2** Windows: scan the payload with current Defender. Report detections
  and whether the payload survives. Issue #7 tracks a prior false positive on
  `PtkMcpServer.dll`.
- **F3** macOS: binaries are ad-hoc signed, not notarised. Report what a user
  encounters, and whether Gatekeeper requires an override.
- **F4** The server runs with the invoking user's privileges and does not
  escalate.
- **F5** A command child cannot observe the worker's protocol descriptors.

## G. Mutation check

Needs a checkout and the .NET SDK. Revert afterwards.

- **G1** macOS ARM64. In `server/PtkMcpServer/Worker/UnixCloseOnExec.cs`,
  route `TrySet` back through a fixed-signature P/Invoke to variadic `fcntl`
  with `F_SETFD` instead of `ioctl(FIOCLEX)`, then run the `UnixCloseOnExec`
  tests. Report whether they fail. If they pass, the guard does not detect the
  ABI mismatch it exists for, and the behaviour needs a test that observes
  descriptor inheritance across `exec`.

## Reporting

One report per run. A defect blocks release only if it:

- executes a different command than submitted, loses user data, or repeats
  execution;
- prevents install, launch, registration, ordinary use, or uninstall;
- breaks a named session, or prevents recovery after a timeout or crash;
- makes one of the five tools return materially wrong or unrecoverable
  output;
- exposes a security defect in the default configuration; or
- causes the Windows artifact to be quarantined by current Defender.

Classify each finding against that rule, or as unsure. Include passes and
untested items.

```powershell
gh issue create `
  --repo AlsoBeltrix/PowerShell-Token-Killer `
  --title "Test report: <candidate version> on <os-arch>" `
  --body-file results.md
```

```markdown
## Environment
<the table above>

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
<same shape, plus why it does not block>

## Unsure
<finding and reasoning>

## Passed
<IDs, one line each>

## Not tested
<IDs and why>
```

If a report already exists for this candidate, comment on it with your
platform instead of opening another.
