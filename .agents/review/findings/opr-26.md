# opr-26: Stale process snapshot can satisfy post-arm containment proof

**Severity**: MEDIUM — a cached process table captured before a worker was armed can count as healthy post-arm evidence, allowing an unobserved out-of-group descendant to survive a false `ConfirmedEmpty` result.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan binds healthy containment observations to snapshot capture time or generation relative to registration arming.

**Source**: Multi-pass no-tool Claude Opus 5 review of `server/PtkMcpServer/Worker/UnixWorkerProcessLauncher.cs` at `488e65354e1562c6b80f0adbdee8a07c53af08df`, followed by focused final adjudication against `UnixWorkerContainmentRegistry` and `ProcessTableSnapshot`.

## Evidence

`UnixWorkerNative.TryTakeProcessTable` returns `ProcessTableSnapshot.TryTakeShared` with a 250 ms cache lifetime. The returned list carries no capture timestamp. `UnixWorkerContainmentRegistry.ObserveOnce` increments `HealthyObservationCount` for every non-null snapshot, and `CanConfirmEmpty` requires only that an armed registration have at least one such observation. A snapshot captured for another registration before the current worker was armed can therefore satisfy the gate. A descendant created after that snapshot is absent from the closure and never enters the tracked descendant sets. If it leaves the worker process group before containment, the original group can become empty and the registry can return `ConfirmedEmpty` while that descendant remains alive.

This is independent of `opr-15`: all native identity queries may succeed. The defect is temporal freshness of otherwise healthy evidence, not a probe exception being treated as process death.

## Predicted observable failure

One containment observation populates the shared snapshot cache. Within 250 ms, another worker arms and creates a descendant that leaves its process group. Its first registry observation reuses the older snapshot, counts it as healthy, misses the new descendant, and later releases the session alias as confirmed empty while the descendant still runs.

## Required guard

Add a deterministic registry/cache test with a controllable clock: cache a snapshot before registration arming, create an out-of-group descendant only afterward, and prove the stale snapshot cannot increment the healthy-observation count or permit `ConfirmedEmpty`. Supply a fresh post-arm snapshot and prove it can satisfy the gate. Temporarily revert only the repair, confirm the stale-snapshot assertion fails, restore it, then run focused Unix containment tests and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `MEDIUM`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- Existing exact-proof fail-closed policy remains required; this finding prevents stale evidence from being accepted as that proof.
