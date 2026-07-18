# rbc-7: OutputStore Read/Search can wedge the store lock on a slow filesystem

**Severity**: MAJOR
**Status**: Open (intake, awaiting owner triage)
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `server/PtkMcpServer/Execution/OutputStore.cs:584-638`

## Evidence

After sealing, the artifact's directory entry is removed and reads go
through the retained `FileStream.SafeFileHandle`. This is correct on
Unix (open fd survives unlink) and Windows (delete-pending).

However, `Read` (line 597) and `Search` (line 663) call `IsUtf8Boundary`,
which does `ReadExact(handle, offset, 1)` — a synchronous
`RandomAccess.Read` while holding `_gate`. On a wedged filesystem (the
exact scenario `TryStartForegroundOperation` is designed to isolate),
these synchronous reads block the lock and stall every other
`Status` / `Read` / `Search` / `TryReserve` caller.

The foreground-operation lane only protects seal/reserve; reads bypass
it.

## Predicted observable failure

A slow or stuck filesystem (NFS stall, disk full, I/O hang) causes a
single `ptk_output` read to block the entire `OutputStore` lock
indefinitely. Every subsequent `ptk_output` or `ptk_job` call that
touches the store queues behind the wedged read.

## What

Move `Read`/`Search` off the `_gate` lock, or add a bounded timeout to
the `RandomAccess.Read` calls (e.g., via `CancellationToken` or a
`Task.Run` with `Task.WaitAsync`). The retained-handle read does not
need to serialize against seal/reserve operations since the artifact is
already sealed and unlinked.

## Scope of fix

One or two methods in `OutputStore.cs`. No architectural change; the
seal/unlink invariant is preserved.

## Guard proof

Not yet written. A guard should inject a stalled `RandomAccess.Read`
(or a filesystem that hangs) and assert that a concurrent `Status` call
completes within a bounded time instead of queueing behind the wedge.

## Reviewer comments

Read-only review by Hermes subagent (execution/worker subsystem pass).
No external fixed-SHA review has been dispatched.