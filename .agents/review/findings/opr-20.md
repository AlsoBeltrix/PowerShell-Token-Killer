# opr-20: Pre-write state cancellation destroys a healthy warm session

**Severity**: HIGH — cancellation of a read-only state query before any pipe-write attempt can poison and replace a healthy worker, losing the named session's warm state.

**Status**: Accepted; unplanned. Product and test changes are blocked until an approved plan defines the state-query write-attempt boundary and a deterministic pre-write cancellation guard.

**Source**: Four-pass no-tool Claude Opus 5 review of `server/PtkMcpServer/Worker/SessionWorkerClient.cs` at `bb2c53e79b6f8a535e4a095e44990ac2710aba78`, followed by production-contract adjudication against `WorkerProtocolWriter`, named-session recovery, and focused client tests.

## Evidence

`ProcessSessionWorker.StateAsync` sets `writeAttempted = true` before it calls `WriteRequiredAsync`. `WorkerProtocolWriter.WriteAsync` has two cancellation points before it offers any byte to the pipe: its write-gate wait and an explicit token check before encoding. A cancellation at either point is proved pre-write, but the state catch sees the eager flag, calls `Poison`, and sends a best-effort cancel for a request the worker never observed.

The adjacent invoke path already uses the writer's `onWriteAttempt` callback, invoked immediately before the underlying stream write, to distinguish proved-not-started from ambiguous transport state. State queries omit that boundary even though they use the same writer. Production state calls carry the MCP caller cancellation token. `NamedSessionSupervisor` observes the poisoned worker as unusable, begins automatic recovery, and replaces it.

## Predicted observable failure

An idle named session can hold variables, loaded modules, and live connections. If its caller cancels `ptk_state` after the client acquires the operation lease but before the protocol writer attempts the pipe write, the query has had no worker-visible effect, yet the supervisor tears down and replaces that healthy worker. The next invocation sees an empty replacement session and lost warm state solely because a read-only diagnostic was canceled.

## Required repair boundary

State-query failure may poison the transport and send cancellation only after the protocol writer proves that a pipe-write attempt began. A cancellation before that boundary must leave the worker usable and must not emit a cancel frame for an unseen request. Preserve current fail-closed behavior for cancellation or failure at or after the first write attempt, because the stream and request outcome are then ambiguous.

## Required guard

Add a deterministic client test with a controlled writer boundary that cancels after `StateAsync` acquires its operation lease but before `WorkerProtocolWriter` invokes the first-write callback. Prove the state call is canceled, no state or cancel frame is emitted, the client remains transport-usable, and a subsequent request succeeds on the same worker. Add the at-write counterpart proving cancellation still poisons and sends one correlated cancel. Temporarily revert only the repair and prove the pre-write guard fails, restore it, then run focused client tests and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `HIGH`; confidence `high`.
- `guard_confirmed=false`; no repair implemented or tested.
- The finding is limited to the proved pre-write window; poisoning after a write attempt remains required and is not contested.
