# Machine State

Machine-specific, nonportable facts only. Date each verification; prune stale
entries during a `drift` pass.

## `nagatha.local` — Michael's Mac

_Last verified 2026-07-11 against repo base `78779b0`._

- The live server runs from `~/.ptk/bin/PtkMcpServer`; installed version is
  `0.2.0-dev.g6db333c`.
- Installed `ptk_init.ps1`, `ptk-hook.ps1`, and
  `PwshTokenCompressor.psm1` hash-match the checkout. No product file
  changed between `6db333c` and `78779b0`.
- The Claude and Codex guidance blocks contain the current PowerShell-dialect
  and raw-recovery wording, and the Claude hook points at the installed copy.
  No dev-install or `ptk_init` rerun is pending on this Mac.
- On 2026-07-11 at plan base `2a83723`, disposable Darwin fork/pipe probes
  proved the audited-session Unix topology at the mid-creation, armed/gated,
  and released-with-descendant barriers. The broker observed supervisor
  liveness EOF, prevented pre-gate work, terminated each process group, and
  left no survivor. This proves the Darwin primitives/topology, not the later
  .NET implementation.

## `NETWATCH-01` — Michael's Windows machine

_Verified 2026-07-11 for audited-session slice 0 at repo base `2a83723`._

- SSH ran as ordinary identity `NETWATCH-01\michael` on Windows NT
  10.0.26200.0 x64, Windows PowerShell 5.1.26100.8737, and .NET SDK 10.0.301
  / runtime 10.0.9. The probe did not read or modify the existing `F:\dev`
  repositories.
- A disposable native probe used
  `CreateProcessW`/`STARTUPINFOEX`/`PROC_THREAD_ATTRIBUTE_JOB_LIST`, queried
  back exact `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (`0x2000`), and proved
  specific worker membership before `ResumeThread`. Its source SHA-256 was
  `ace621f6a2f27bc2a634d4d54c43150aa6c6df1ad93efd95d2a9b025616aeb4c`;
  the executed assembly SHA-256 was
  `89d7f134667257e603d4bf32789bbbe91f1800ed7d88ca7eaf1bf7f6bab39b90`.
- The gated case proved no workload signal before release and worker death
  after raw supervisor-only termination. The released case proved an ordinary
  descendant was alive and in the same exact job, then both held process
  handles signaled after the supervisor's sole noninherited job handle closed.
  The probe contained no `AssignProcessToJobObject` or `TerminateJobObject`
  symbol/path.
- No `PtkWindowsJobProbe` process existed before or after the final run. Its
  temporary directory was removed, and no persistent host configuration was
  changed.

## Windows payload/install status

_Not verified by the 2026-07-11 containment probe._

- Current Windows payload and guidance status remains unknown, including on
  `NETWATCH-01`; the probe deliberately did not inspect `F:\dev` or installed
  PTK files. The former combined Mac/Windows reinstall claim was falsified on
  the Mac and is not evidence for any Windows host; verify the target payload
  directly before taking install action.
