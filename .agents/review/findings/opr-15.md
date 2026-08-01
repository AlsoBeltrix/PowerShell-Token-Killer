# opr-15: Unix identity-probe errors fail open during containment confirmation

**Severity**: HIGH — a transient supervisor-side identity-query failure can release a session alias while an observed escaped descendant is still alive.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan defines a tri-state native identity contract and fail-closed retry behavior.

**Source**: Bounded Claude Opus 5 review of `UnixWorkerContainmentRegistry`, validated against the native implementation, production launcher sequence, approved containment boundary, and focused tests.

## Evidence

`server/PtkMcpServer/Worker/UnixWorkerContainmentRegistry.cs` uses `IsIdentityLive` for the broker, worker, and every previously observed escaped descendant. That helper catches every nonfatal exception from `_native.QueryIdentity(processId)` and returns `false`, making absence and an indeterminate query indistinguishable.

The production Linux query reads `/proc/<pid>/stat`; the Darwin query calls `proc_pidinfo`. Both can fail for transient I/O, access, or resource conditions as well as because the process is absent. `ProcessGroupExists` separately fails closed, but it cannot cover an escaped descendant after the original worker group is gone.

## Predicted observable failure

After PTK observes and records a process-group escape, `CompleteAsync` correctly returns `descendants_unknown` and starts `ConfirmEventuallyAsync`. Once the worker and broker exit and the original group becomes empty, a transient identity-query failure for the still-live escaped process is treated as death by `CanConfirmEmpty`. The background confirmation loop then calls `CompleteRegistration`, clears the active registration, completes its emptiness task, and permits replacement of the session while that process continues running.

## Required repair

Make the native identity boundary distinguish exact identity, confirmed absence, and indeterminate failure. Only confirmed absence or an observed different incarnation may satisfy liveness; indeterminate results must keep containment unconfirmed and retry through the existing observer. Add a deterministic fault-injection guard in which `CompleteAsync` first returns `descendants_unknown` for an observed escaped descendant, then the worker group becomes empty while one escaped-descendant identity query fails transiently. Current code must falsely complete the registry emptiness task in the background; the repair must keep that task incomplete through both the transient failure and a later exact live observation, completing it only after an exact dead or different-incarnation observation.

## Review disposition

Reviewer: owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, no-tool transport; exact head `5187eba3e36a68496056a19d0cbe5f819396d662`. Initial and evidence-bound adjudication verdict: `finding`; `guard_confirmed=false`. No product-change guard claim.

- Rejected candidate: fast deliberate `setsid`/double-fork escape is an explicitly accepted partial-coverage boundary and already preserved as a separately scoped, unaccepted daemonized-descendant question.
- Rejected candidate: permanent quarantine when no healthy armed process-table snapshot exists is the approved fail-closed behavior, not a liveness defect.
- Rejected candidate: an arm-time probe failure occurs before the still-gated worker is released, so the unarmed registration may safely confirm without a descendant observation after cleanup; only diagnostic specificity is lost.
