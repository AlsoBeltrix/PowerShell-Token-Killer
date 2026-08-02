# opr-31: Indeterminate probe permanently untracks a reparented descendant

**Severity**: MEDIUM — one transient identity or process-group probe failure can remove a live reparented descendant from registry tracking, allowing a later group escape to survive a false `ConfirmedEmpty` result.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan distinguishes confirmed absence/incarnation change from indeterminate observation when reconciling tracked descendants.

**Source**: Three-pass no-tool Claude Opus 5 review of `server/PtkMcpServer/Worker/UnixWorkerContainmentRegistry.cs` at `d30eb1562e9aff843d17f45d92af78b28d527031`, followed by complete-source final adjudication and a focused scope-boundary review.

## Evidence

`ObserveOnce` carries `registration.Descendants` into `previouslyTracked` so descendants that have reparented away from the worker subtree remain observable. It adds a PID to the current `observations` map only when both `QueryIdentity` and `GetProcessGroup` succeed with valid evidence; any nonfatal exception is swallowed. The reconciliation loop then treats every previously tracked PID absent from `observations` as dead or replaced and removes it from `registration.Descendants`. A still-live reparented descendant removed after one indeterminate probe is no longer reachable through `DescendantClosure`, so it is never rediscovered. If it later leaves the worker process group, the registry never adds it to `EscapedDescendants` and can confirm the original group empty while the process survives.

This is distinct from `opr-15`. That finding treats an indeterminate final liveness query for an already recorded escaped descendant as confirmed death. This finding silently discards a tracked, not-yet-escaped descendant and thereby prevents its future escape from ever reaching the final liveness gate.

## Predicted observable failure

The registry observes a descendant and retains it after it reparents. One later process-group or identity query fails transiently, so the registry removes the still-live PID. The descendant then changes process group. After the original worker group drains, containment reports `ConfirmedEmpty`, completes the observer, and releases the session alias while that descendant continues running.

## Required guard

Add a deterministic registry test that observes a descendant, reparents it away from the worker closure, injects one indeterminate identity or group probe, then moves the descendant out of the worker group. Assert the descendant remains tracked through the transient failure and containment stays unknown until exact absence or a different incarnation is proved. Add confirmed-death and confirmed-replacement controls that still remove tracking. Temporarily revert only the repair, prove the transient-failure assertion fails, restore it, then run focused Unix registry tests and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `MEDIUM`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- The separately scoped never-observed daemonized-descendant boundary remains unchanged; this finding concerns a descendant already tracked before the indeterminate probe.
