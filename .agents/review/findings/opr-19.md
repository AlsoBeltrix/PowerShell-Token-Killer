# opr-19: Client stop bypasses graceful worker shutdown

**Severity**: HIGH — every supported stop path skips the worker shutdown handshake and kills the process without running session shutdown.

**Status**: Accepted; unplanned. Product and test changes are blocked until an approved plan defines shutdown/fatal ordering and a deterministic client-level handshake guard.

**Source**: Four-pass no-tool Claude Opus 5 review of `server/PtkMcpServer/Worker/SessionWorkerClient.cs` at `bb2c53e79b6f8a535e4a095e44990ac2710aba78`, followed by production-contract adjudication against the worker server, protocol, supervisor, and focused client tests.

## Evidence

`ProcessSessionWorker.StopAsync` sets `_stopping = true`, completes `_fatal`, acquires `_operation`, and then calls `NextRequestId(allowStopping: true)`. `NextRequestId` lets that flag bypass only the stopping check; it still throws `IOException` whenever `_fatal.Task.IsCompleted`. The same `StopAsync` invocation completed `_fatal` immediately beforehand, so request allocation fails deterministically. Its enclosing nonfatal catch swallows the exception, no `shutdown` frame is written, no `stopped` frame is read, and `_stopped` is never set. Control always falls through to forced containment.

The approved worker protocol and reliability plan define `shutdown` / `stopped` as the ordinary graceful fast path. `WorkerServer` drains operations, runs session `ShutdownAsync`, and emits `stopped` only after receiving that frame. Direct protocol and server tests exercise a caller-supplied shutdown frame, but `SessionWorkerClientTests` has no client-level `StopAsync` handshake guard, so the dead path remains invisible.

## Predicted observable failure

Closing, resetting, replacing, or disposing any initialized named-session worker never invokes worker-side session shutdown. The supervisor forcibly contains the process instead, skipping normal in-worker teardown for runspace resources and live connections. The public stop result is containment-derived even when an idle worker could have acknowledged a clean shutdown.

## Required repair boundary

Keep new operation admission closed as soon as stopping begins, but preserve exactly one bounded shutdown request/response attempt before forced containment. Fatal notification, request-ID allocation, clean-exit observation, and concurrent stop/dispose ordering must not make that handshake self-reject or misclassify the acknowledged exit as unexpected. Containment remains the final descendant sweep after either graceful acknowledgement or handshake failure.

## Required guard

Add a deterministic `ProcessSessionWorker` test that initializes a scripted process, calls `StopAsync`, observes one correlated `shutdown` request, returns `stopped`, and proves the graceful exchange completes before containment. Add the failure-path counterpart proving a missing or invalid acknowledgement still reaches bounded containment. Temporarily revert only the repair and prove the graceful guard fails, restore it, then run the focused client tests and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `HIGH`; confidence `high`.
- `guard_confirmed=false`; no repair implemented or tested.
- Related cancellation, writer-abandonment, post-seal, and stopped-request-ID candidates were rejected by production caller and protocol evidence.
