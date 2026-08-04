# ptk v0.2.0 release acceptance — agent runbook

You are testing an **installed** ptk before it is tagged and published. Your
job is to find defects, not to confirm the build is good. A clean report that
missed a real bug is a failure; a report that says "I could not test X" is a
success.

Read this whole document before starting. Everything you need is here — you do
not need a repository checkout.

## Scope and hard constraints

- **Test only. Change nothing.** No edits to ptk's source, scripts, or
  installed payload. Write throwaway probes in a temp directory.
- **Do not fix anything you find.** Report it. The owner decides what blocks
  the release.
- **Do not tag, publish, or edit a release.**
- One GitHub issue at the end, for the whole run. Not one per finding.
- If a check cannot run on your machine, say so and why. Never mark a check
  passed because it was skipped.

## Preconditions

You need:

- ptk installed (`~/.ptk` exists with `bin/`, `src/`, `VERSION`)
- rtk on `PATH`, or at `~/.ptk/bin/rtk` — ptk exits 78 without it
- PowerShell 7 (`pwsh`)
- `gh` CLI authenticated against
  `AlsoBeltrix/PowerShell-Token-Killer`, for the final report only

Record your environment first; every finding is meaningless without it.

```powershell
$ptk = Join-Path $HOME '.ptk'
$exe = if ($IsWindows) { "$ptk/bin/PtkMcpServer.exe" } else { "$ptk/bin/PtkMcpServer" }
[pscustomobject]@{
    OS        = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    Arch      = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    Pwsh      = $PSVersionTable.PSVersion.ToString()
    PtkVersion = Get-Content "$ptk/VERSION"
    Assembly  = [Diagnostics.FileVersionInfo]::GetVersionInfo("$ptk/bin/PtkMcpServer.dll").ProductVersion
    Module    = (Import-PowerShellDataFile "$ptk/src/PwshTokenCompressor.psd1").ModuleVersion
    Rtk       = (Get-Command rtk -ErrorAction SilentlyContinue).Source
    RtkVersion = (& rtk --version 2>$null)
} | Format-List
```

## How to drive ptk

Two ways. Use whichever fits your harness; say which one you used.

**(a) You are an MCP client with ptk tools.** Call `ptk_invoke`, `ptk_output`,
`ptk_state`, `ptk_reset`, `ptk_session` directly. Open your own named session
first and use it throughout, so you do not disturb anything else:
`ptk_session action=open name=acc1`, then pass `session=acc1`.

**(b) Drive the server over stdio.** Start `$exe` as a child process and speak
JSON-RPC on stdin/stdout: `initialize`, then the
`notifications/initialized` notification, then `tools/call`. One JSON object
per line. This is the only way to test startup behaviour and crash recovery.
Minimal client:

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

Note `ptk_session` takes **`name`**, not `session`, for its open/close target.

## The claims to test

Each item is a claim. Try to falsify it. Where a command is given it is a
starting point, not the whole test — probe around it.

### A. Install and uninstall

**Never run an installer against a `~/.ptk` that a live ptk is serving from.**
If your own harness is using ptk, do installer tests against an isolated fake
home instead, and say that you did.

- **A1.** A fresh install from the release asset completes and leaves a server
  that starts. Run the real `install.ps1` (Windows) or `install.sh`
  (macOS/Linux) — not a manual unzip. This has never been executed end to end
  on any platform.
- **A2.** A corrupted asset is rejected. Truncate or edit a downloaded asset
  and confirm the installer refuses it rather than installing it.
- **A3.** The rtk fetch path works. On a machine with **no rtk on PATH**, the
  installer must download it, verify it against rtk's `checksums.txt`, and
  confirm it answers. This branch has never downloaded anything — every prior
  run found rtk already present and returned early.
- **A4.** Installing over an existing install replaces the payload and
  preserves user-owned files under `~/.ptk` (e.g. a `policy.psd1` you create).
- **A5.** A failure part-way through activation restores the prior payload
  byte-identically. Inject a failure after the payload is copied and before
  registration finishes.
- **A6.** `install.sh` runs under a real POSIX `sh` (dash), not just bash.
- **A7.** Uninstall after a real install leaves no payload, no MCP
  registration, and on Windows no Add/Remove Programs entry — while leaving
  user-owned files unless `--purge`/`-Purge`.
- **A8.** Uninstall must not delete an rtk the user already had. The installer
  records what it placed in `~/.ptk/.ptk-installed-rtk`; only that copy is
  removable.

### B. Product contract on your platform

Only `win-x64` has had the full product proof run against an installed
candidate. Every other platform has had the transport handshake and the RTK
startup gate, nothing more.

Check each of these against the **installed** server:

- **B1.** Exactly five tools are exposed: `ptk_invoke`, `ptk_output`,
  `ptk_state`, `ptk_reset`, `ptk_session`.
- **B2.** A named session retains warm state across invocations
  (`$global:x = 42` then read it back).
- **B3.** PowerShell objects are compressed, not formatted to text — 40
  objects should summarise, not dump.
- **B4.** Plain text passes through as text.
- **B5.** Large output returns a `ptk_output` handle and the handle recovers
  the content.
- **B6.** An invocation that times out (`Start-Sleep -Seconds 30` with
  `timeoutSeconds=2`) reports the timeout, and a later invocation on that
  session succeeds. It may report `session_recovering` briefly first — retry
  for up to a minute before calling it a failure.
- **B7.** `ptk_reset` and `ptk_session action=close` both work.
- **B8.** With rtk absent, the server refuses to start: exit code 78 and a
  message naming `PTK_RTK_PATH`. Test with a genuinely empty environment
  block — merging an empty `PATH` into an inherited environment does not hide
  rtk, and the check passes vacuously.

If the repository is available to you,
`pwsh -File server/direct-product-proof.ps1 -ServerPath <exe>` runs B1–B8 in
one shot. Reporting its output is fine; reporting it without running it is
not.

### C. Trusted-type rendering (new, and the broadest change in this release)

The output shaper used to recognise six types and return
`[active member not evaluated]` for everything else, discarding the value.
It now calls `ToString()` on any type from the .NET runtime directory or from
`System.Management.Automation` / `Microsoft.PowerShell.Commands.Utility`.

**The safety rule that must not break:** user code must never be executed
during output capture. A type from an `Add-Type` assembly — dynamic, or with
no on-disk location — must still get the placeholder, never a `ToString()`
call.

- **C1.** `Get-Culture` returns the culture, not a placeholder.
- **C2.** A user type from `Add-Type` whose `ToString()` increments a counter:
  the counter must stay at zero and the output must be the placeholder.
- **C3.** A user type that **subclasses** a framework type. Does it inherit
  trust it should not? The check reads `value.GetType().Assembly`, so it
  should be rejected — confirm rather than assume.
- **C4.** A trusted generic parameterised over a user type
  (`List<UserType>`, `Nullable<UserStruct>`): whose `ToString()` runs?
- **C5.** A framework type whose `ToString()` is slow or blocking. Rendering
  happens on the producer callback; does it stall capture?
- **C6.** Many large renderings in one invocation — the projection budget must
  hold and the output must stay bounded.
- **C7.** The marker is `passive_projection_lossy`, not
  `active_member_not_evaluated`, when a value was rendered rather than
  skipped.
- **C8.** A framework exception surfaces its message; an `Add-Type` exception
  still shows the "not safe to inspect" text.

### D. Sessions under load

- **D1.** Eight named sessions open at once; the ninth is refused cleanly.
- **D2.** Concurrent invocations in different sessions run in parallel and do
  not contaminate each other's warm state (same variable name, different
  values).
- **D3.** Calls within one session serialise; a queued call whose budget
  expires while waiting fails fast **without executing**.
- **D4.** Repeated timeout-and-recover cycles on one session — does it recover
  every time, or degrade?
- **D5.** Kill a worker process directly, then invoke on that session.
- **D6.** Several sessions each producing large output, each recovered through
  `ptk_output`.

### E. Routing

ptk submits your exact script text to `rtk hook check --agent ptk`. If rtk
returns a rewrite, ptk validates it before executing: the rewrite must bind
the absolute rtk path pinned at startup, stripping `rtk ` prefixes must
reproduce your text byte-for-byte, and every wrapped name must resolve to a
native application in the session.

- **E1.** Define `function git { 'MINE' }` in a session, then invoke
  `git status`. The function must run. If the real git runs instead, that is a
  different command than the one submitted — a release blocker.
- **E2.** Quoted arguments with spaces and embedded quotes survive
  byte-exactly through a rewrite.
- **E3.** A command rtk declines (`rtk hook check` exits non-zero) executes
  unchanged, with the right exit code and stderr.
- **E4.** `route=pwsh` never routes. `route=rtk` on a shape rtk declines
  reports a labelled fallback and does not retry.
- **E5.** Adversarial: can you construct a script containing the literal token
  `rtk` where prefix-stripping reproduces the original but the executed
  command differs in meaning?

### F. Security posture

- **F1.** The installer refuses to run as root / Administrator.
- **F2.** Windows: scan the installed payload with current Defender. Issue #7
  is a tracked false positive on `PtkMcpServer.dll`; report whether it
  reproduces, and whether the file survives the scan.
- **F3.** macOS: the binaries are ad-hoc signed, not notarised. Report exactly
  what a user sees installing via `install.sh` — does the documented path work
  without a Gatekeeper override?
- **F4.** The warm runspace inherits the harness's privileges. Confirm the
  installed server does not escalate beyond the invoking user.

### G. Known-vacuous-guard check (macOS ARM64 only)

`opr-14` was an ABI bug: `FD_CLOEXEC` was set through a fixed-signature
P/Invoke to libc's variadic `fcntl`, which is wrong on Apple arm64. It was
replaced with `ioctl(FIOCLEX)`. The guard passes — but it was never shown to
**fail** against the old code on Apple silicon, so it may be vacuous.

If you are on macOS ARM64 and have a checkout and .NET SDK: restore the old
`fcntl(F_SETFD)` call and run the `UnixCloseOnExec` tests. If they still pass,
the guard proves nothing and a stronger test is needed — one that proves a
command child cannot inherit the worker's protocol descriptors across `exec`.
Report which outcome you saw.

## Reporting

One issue, at the end. Judge each finding against this rule — a defect blocks
the release only if it:

- executes a different command than submitted, loses user data, or repeats
  execution;
- prevents install, launch, registration, ordinary use, or uninstall;
- breaks a named session, or prevents recovery after a timeout or crash;
- makes one of the five tools return materially wrong or unrecoverable
  output;
- exposes a security defect in the default configuration; or
- causes the Windows artifact to be quarantined by current Defender.

If you are unsure whether something qualifies, write **unsure**. Do not guess,
and do not inflate a cosmetic issue into a blocker or talk a real one down.

Report confirmations too. "A3 exercised — rtk downloaded, checksum verified,
answered `hook check`" is a result. Silence is not.

```powershell
gh issue create `
  --repo AlsoBeltrix/PowerShell-Token-Killer `
  --title "Release acceptance: v0.2.0 on <rid> (<os>)" `
  --label "" `
  --body-file results.md
```

Use this shape for `results.md`:

```markdown
## Environment
<the table from Preconditions>
How ptk was driven: <MCP client | stdio>
Installed from: <release tag / asset / manual>

## Summary
<n> checks run, <n> passed, <n> failed, <n> skipped.
Blockers: <count, or none>

## Blockers
### <ID> — <one-line claim>
Command:
    <exact command>
Expected: <...>
Observed: <actual output>
Blocking rule: <which clause it meets>

## Non-blocking findings
<same shape, plus why it does not block>

## Unsure
<findings you could not classify, with your reasoning>

## Confirmed working
<IDs and a line each>

## Not tested
<IDs and why — missing hardware, no checkout, etc.>
```

If another agent has already filed a report for this release, add yours as a
comment on that issue rather than opening a second one, and say which platform
you covered.
