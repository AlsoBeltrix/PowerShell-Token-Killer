# opr-28: Internal Unix handshake timeout reports caller cancellation

**Severity**: LOW — the launcher's own broker-handshake deadline is surfaced as `worker_start_canceled`, falsely attributing a stalled broker to caller or supervisor cancellation.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan preserves cancellation provenance across the private handshake timeout and the authoritative startup deadline.

**Source**: Multi-pass no-tool Claude Opus 5 review of `server/PtkMcpServer/Worker/UnixWorkerProcessLauncher.cs` at `488e65354e1562c6b80f0adbdee8a07c53af08df`, followed by focused adjudication against `ProcessSessionWorkerFactory`, existing `opr-22`, and final merge review.

## Evidence

`CompleteHandshakeAsync` creates a private five-second `BrokerHandshakeTimeout` token and links it with the caller token. Every handshake read and write receives only the linked token. When the private timer expires, the operation throws `OperationCanceledException`; no exception or token identifies the private timeout. `ProcessSessionWorkerFactory.LaunchFailureCode` classifies cancellation as `worker_start_timed_out` only when wall-clock time has reached the separate overall startup deadline. Whenever that deadline is later than five seconds, a real broker-handshake timeout is deterministically classified `worker_start_canceled`.

This is independent of `opr-22`. That finding concerns the supervisor startup token expiring against a later deadline after first-use factory construction. This finding is a separate launcher-internal timer and needs its own stalled-handshake guard, although both expose the same missing cancellation-provenance boundary.

## Predicted observable failure

The broker spawns but stalls before `Hello`, `ChildGated`, `Armed`, or `Released`. Five seconds later the launcher times out, contains the domain, and reports `worker_start_canceled` even though neither the caller nor supervisor canceled. Operators investigate cancellation or shutdown rather than a wedged broker handshake.

## Required guard

Add deterministic launcher/factory tests with an overall startup deadline longer than five seconds and a broker event read that remains pending until the private handshake timer expires. Assert the public detail is `worker_start_timed_out`. Add caller-cancellation and supervisor-deadline controls that preserve their respective classifications. Temporarily revert only the repair, prove the internal-timeout assertion fails, restore it, then run focused launcher/factory tests and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `LOW`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- `opr-22` remains a separate timeout source and guard.
