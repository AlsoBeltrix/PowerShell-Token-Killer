# opr-30: Healthy containment evidence is not bound to the released worker interval

**Severity**: MEDIUM — a process-table observation taken while the worker is still gated or only after it has died can satisfy the registry's armed-evidence gate, allowing an escaped descendant to survive a false `ConfirmedEmpty` result.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan binds healthy observations to the interval in which the released, live worker could create descendants.

**Source**: Three-pass no-tool Claude Opus 5 review of `server/PtkMcpServer/Worker/UnixWorkerContainmentRegistry.cs` at `d30eb1562e9aff843d17f45d92af78b28d527031`, followed by complete-source final adjudication and a focused scope-boundary review.

## Evidence

`ObserveOnce` increments `HealthyObservationCount` for every non-null process-table snapshot. `CanConfirmEmpty` requires only that an armed registration have a nonzero count. `RegisterArmedAsync` starts `ObserveAsync`, whose first `ObserveOnce` runs synchronously before the worker is released; that child is still gated and cannot yet have descendants, but its snapshot permanently satisfies the gate. At the other end, `CompleteAsync` and `ConfirmEventuallyAsync` can take the first successful snapshot only after the worker and broker have died; the dead root cannot reveal descendants created during the live interval, but that snapshot also satisfies the gate. If process-table reads fail throughout the released worker's lifetime, either outside-window observation can authorize `ConfirmedEmpty` after an unobserved descendant leaves the original process group.

This is independent of `opr-26`: the snapshot may be fresh and captured after registration arming. `opr-26` is stale cached evidence from before arming; this finding is failure to bind evidence to the released, live descendant-creation interval.

## Predicted observable failure

The registry records one healthy snapshot while a newly armed worker is still gated. After release, process-table collection fails while the worker creates a descendant that leaves its group. The worker and broker later exit and the group empties. The pre-release count satisfies the proof gate, so the registry completes `Empty` and releases the session alias while the descendant still runs. The same outcome is possible when the only healthy snapshot arrives postmortem.

## Required guard

Add deterministic registry/launcher tests that separately cover pre-release and postmortem snapshots. In each, allow no successful observation while the released worker is live, create an out-of-group descendant during that interval, and assert the outside-window snapshot cannot permit `ConfirmedEmpty`. Add a control with a healthy in-interval observation that discovers the descendant. Temporarily revert only the repair, prove both outside-window assertions fail, restore it, then run focused Unix registry/launcher tests and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `MEDIUM`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- The separately scoped never-observed daemonized-descendant boundary remains unchanged.
