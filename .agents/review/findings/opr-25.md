# opr-25: Unix exit observers convert probe faults into worker death

**Severity**: MEDIUM — a transient native observation failure can be presented as a real worker exit, poisoning a healthy warm session and losing its state.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan gives worker-exit observation an explicit indeterminate/fault state and preserves real task failures across the `WhenAny` boundary.

**Source**: Multi-pass no-tool Claude Opus 5 review of `server/PtkMcpServer/Worker/UnixWorkerProcessLauncher.cs` at `488e65354e1562c6b80f0adbdee8a07c53af08df`, followed by focused final adjudication against `ProcessSessionWorker` and existing `opr-15`.

## Evidence

`ContainedUnixWorker.ObserveWorkerExitAsync` polls `QueryIdentity`. It returns both when the identity changes and when any nonfatal query exception occurs, so its task cannot distinguish process exit from an indeterminate probe. `WaitForExitAsync` also awaits `Task.WhenAny` between that observer and the broker `waitpid` task without propagating a winning task's fault. `ProcessSessionWorker.ObserveExitAsync` treats either successful completion as an unexpected worker exit, completes its fatal latch, and poisons the transport. A transient `/proc` or `proc_pidinfo` read failure can therefore tear down a live warm worker; a faulted broker wait can produce the same false exit signal.

This is distinct from `opr-15`: that finding is containment-registry fail-open behavior that can release an alias while a descendant survives. This finding is steady-state exit observation that declares a healthy worker dead and loses session state. The two share a need for tri-state native identity evidence, but `opr-15` alone does not repair the fault-swallowing `WhenAny` path.

## Predicted observable failure

A live Unix worker experiences one transient identity-query failure. Its session immediately reports worker transport failure, replaces the worker, and loses warm state even though the worker process never exited. Separately, a broker wait task fault can complete the same exit signal without its exception reaching the session.

## Required guard

Add deterministic launcher/client tests where the first identity query throws and the next returns the original identity. Assert `WaitForExitAsync` and the client fatal latch remain incomplete until a true identity change or exit. Add a broker-wait fault case asserting the fault is observed and cannot masquerade as successful exit completion. Include a real-exit control. Temporarily revert only the repair, prove both fault-path assertions fail, restore it, then run focused launcher/client tests and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `MEDIUM`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- Containment fail-open behavior remains separately tracked by `opr-15`.
