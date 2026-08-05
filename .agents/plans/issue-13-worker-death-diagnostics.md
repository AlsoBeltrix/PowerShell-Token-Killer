# Plan: issue #13 — name the cause when a session worker dies

**Status:** APPROVED by owner 2026-08-05 (session goal: "fix all the issues.
stop asking me to approve the obvious fixes. plan, code, review with codex").
No open owner decisions; implement as written.

## Problem (verified in current source, not from the issue text)

When a session worker process dies mid-invocation, `ptk_invoke` returns
`status=outcome_unknown detail=worker_transport_closed` and nothing else. The
caller cannot distinguish a worker defect from its own script killing the
process, cannot tell whether the command's side effects landed, and has
nothing to report but "ptk broke".

The evidence needed to name the cause exists at the moment of death and is
then discarded:

- The dying worker already writes exactly one bounded ASCII diagnostic to its
  own stderr — `ptk_worker_exit kind=<kind> detail=<detail>\n`, capped at
  `WorkerProcessExit.MaximumDiagnosticBytes` (256) — and exits with a
  per-class code: 64 invocation, 80 bootstrap, 81 initialize, 82 protocol, 83
  transport, 84 runtime (`server/PtkMcpServer/Worker/WorkerProcessExit.cs:11`).
- The supervisor reads the worker's stdout and stderr into a discard loop
  (`DrainAsync`, `server/PtkMcpServer/Worker/SessionWorkerClient.cs:311`), so
  the diagnostic is consumed and dropped.
- `IWorkerContainedProcess`
  (`server/PtkMcpServer/Worker/WorkerProcessAuthority.cs:29`) exposes
  `WaitForExitAsync` but no exit code, so no layer above can observe one.
- `ObserveExitAsync`
  (`server/PtkMcpServer/Worker/SessionWorkerClient.cs:792`) turns an
  unexpected exit into a bare
  `EndOfStreamException("Worker process exited unexpectedly.")`, which
  `InvocationFailureCode` (same file, line 725) maps to
  `worker_transport_closed` — the same code a genuine pipe failure produces.

Consequence: the three cases a caller must separate — the worker broke, the
transport broke, the caller's own command killed the process — are
indistinguishable at the tool surface. A recurrence therefore cannot advance
the investigation, which is the state issue #13 is stuck in.

## Non-goals

- No change to the worker's diagnostic format, its exit codes, or the
  protocol. Both already exist and are tested
  (`server/PtkMcpServer.Tests/WorkerProcessExitTests.cs`).
- No streaming or retention of general worker stdout/stderr. Only the last
  bounded stderr line is retained, and only for the abnormal-exit path.
- No retry, no automatic resubmission, no change to warm-state-loss
  semantics. A dead worker is still replaced and its warm state still lost.
- No new tool, argument, or configuration surface.

## Slices

Each slice is one commit, verified before the next begins.

### Slice 1 — retain the last bounded worker stderr line

`DrainAsync` becomes a bounded tail retainer instead of a discard loop, for
the worker's stderr only. Worker stdout keeps discarding: the worker is not
supposed to write there at all, and retaining it would be an unbounded
channel for caller output.

- Retain at most `WorkerProcessExit.MaximumDiagnosticBytes` bytes: keep a
  rolling last-N-bytes buffer, never a growing one. The worker's contract
  caps the diagnostic at that size, so a longer stream means something other
  than the diagnostic is talking and the tail is the useful part regardless.
- Decode ASCII only, strip trailing newlines, and reject any retained text
  containing a byte > 0x7f or a control character other than CR/LF —
  matching the write-side guard at `WorkerProcessExit.cs:163`. A rejected
  tail is treated as absent, never surfaced.
- Expose it on `ProcessSessionWorker` as a nullable `string LastStandardError`
  read under the existing `_gate`.

Guard: feed a fake process stderr stream more than 256 bytes ending in a
valid diagnostic; assert only the tail is retained. Feed non-ASCII; assert
absent. Prove by reverting the bound and watching the test fail on retained
length.

### Slice 2 — observe the worker's exit code

`IWorkerContainedProcess` gains `int? ExitCode { get; }` — null until the
process has exited and whenever the platform cannot answer.

- Unix: the launcher already awaits `WaitForExitCodeAsync(brokerProcessId)`
  (`server/PtkMcpServer/Worker/UnixWorkerProcessLauncher.cs:183`). Surface
  that completed task's value; null while the task is incomplete.
- Windows: `NativeProcessHandle` holds a `SafeProcessHandle`
  (`server/PtkMcpServer/Worker/WindowsWorkerNative.cs:743`). Add
  `GetExitCodeProcess` to the existing `NativeMethods` `DllImport` block
  (same file, line 1337) and read it after the wait completes. Treat
  `STILL_ACTIVE` (259) as null rather than reporting it as an exit code — a
  worker can legitimately exit with 259.
- Both: any failure to query returns null. Never throw from this property; a
  diagnostic path that can fail is worse than one that says nothing.

Guard: a fake contained process reports a code; assert it reaches the client.
Windows and Unix launcher tests assert null before exit and the real code
after. Prove by returning a constant and watching the assertion fail.

### Slice 3 — carry both to the failure surface

`ObserveExitAsync` currently poisons with a bare `EndOfStreamException`.
Replace that with a dedicated `WorkerExitException` carrying the retained
diagnostic and the exit code, so `InvocationFailureCode` can classify instead
of collapsing every case to `worker_transport_closed`:

- A `ptk_worker_exit` diagnostic present → detail code
  `worker_exit_<kind>` (for example `worker_exit_runtime_failure`), the
  worker's own vocabulary rather than a transport guess.
- No diagnostic but a non-zero exit code → `worker_exited_unexpectedly`.
- Neither → `worker_transport_closed`, unchanged. A real pipe failure with a
  live worker still reads exactly as it does today.

`WorkerSupervisor.FormatInvocationFailure`
(`server/PtkMcpServer/Sessions/WorkerSupervisor.cs:314`) appends the facts to
the existing `status=outcome_unknown` line — one line, no new block:

```
[ptk invoke] status=outcome_unknown session=default detail=worker_exit_runtime_failure
  worker=exit_code=84 diagnostic="ptk_worker_exit kind=runtime_failure detail=runtime_failure";
  do not resubmit automatically; PTK did not retry the command.
```

Omit each fact that is absent rather than printing a placeholder; when both
are absent the line is byte-identical to today's. The existing
"do not resubmit automatically" guidance stays: knowing the cause does not
make the outcome known.

The same facts go to `AuditCallContext`, which already carries an `exitCode`
parameter on `Append`/`TryAppend`
(`server/PtkMcpServer/Audit/AuditCallContext.cs:622`) and currently receives
nothing on this path.

Guard: an end-to-end test with a worker that exits abnormally asserts the
detail code, the exit code, and the diagnostic reach the tool response; a
transport failure with a live worker asserts the unchanged
`worker_transport_closed` text. Prove both by reverting slice 3's
classification and watching each fail.

### Slice 4 — close the loop

Comment on #13 with the change and what a future recurrence will now show,
and close it. The issue's own recorded blocker is the absence of this
evidence; shipping the evidence path is the fix available without a
reproduction.

## Verification

Full battery per `.agents/repo-guidance.md` §Verification — Pester, both
dotnet suites, dependency audit — plus the handshake, since this touches the
server's worker boundary. Every new guard proved by sabotaged revert. Codex
review after the code lands, per the session process.

## Risk

The failure path is the one place where a defect is hardest to see, because
it runs only when something has already gone wrong. Two specific hazards:

- A retainer that can throw turns a recoverable worker death into a
  supervisor fault. Every new read is inside the existing failure handling
  and returns null on error.
- Retaining worker output at all is a step toward an unbounded channel. The
  bound is the worker's own documented diagnostic cap, enforced on the read
  side, on stderr only, and only surfaced on abnormal exit.
