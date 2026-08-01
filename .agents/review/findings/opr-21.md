# opr-21: Cleanup failure overwrites worker initialization diagnosis

**Severity**: LOW — a second containment failure during failed initialization replaces the primary timeout or cancellation detail with a generic initialization error.

**Status**: Accepted; unplanned. Product and test changes are blocked until an approved plan defines primary-versus-cleanup failure classification and a deterministic double-failure guard.

**Source**: Four-pass no-tool Claude Opus 5 review of `server/PtkMcpServer/Worker/SessionWorkerClient.cs` at `bb2c53e79b6f8a535e4a095e44990ac2710aba78`, followed by production-caller adjudication against named-session startup diagnostics and containment cleanup.

## Evidence

In the client-present failure branch of `ProcessSessionWorkerFactory.StartAsync`, initialization has already failed and the factory calls `client.StopAsync` for launch-failure containment. If that cleanup also throws, the catch replaces the primary exception variable with `new AggregateException(primary, containmentFailure)`. Only afterward does the factory call `InitializationFailureCode(exception, deadlineUtc)`.

That classifier recognizes `OperationCanceledException` and `TimeoutException`, but the new outer `AggregateException` matches neither and falls through to `worker_initialize_failed`. The factory correctly retains both exceptions and reports containment unknown; the defect is that cleanup failure changes the already-known primary failure classification. `SessionWorkerStartException.DetailCode` flows into named-session `LastFailure` and public startup diagnostics.

## Predicted observable failure

When a worker initialization deadline expires or startup is canceled and the containment provider simultaneously fails, the user sees `worker_initialize_failed` instead of the primary timeout or cancellation detail. The containment result still says cleanup is unconfirmed, but the diagnostic no longer explains why initialization first failed, complicating remediation of the double-failure event.

## Required repair boundary

Classify and preserve the primary initialization failure before attempting cleanup. A containment failure may be retained as secondary exception evidence and must keep containment unconfirmed, but it must not rewrite the primary startup detail code. Keep this finding separate from the broader deadline-versus-caller-cancellation discriminator tracked independently.

## Required guard

Add a deterministic factory test that injects a timeout or cancellation during initialization and makes the cleanup process's `ContainAsync` throw a nonfatal exception out of `client.StopAsync`. Assert the resulting `SessionWorkerStartException` preserves the primary detail code, reports the process as launched and containment as unconfirmed, exposes the containment-empty task, and retains both causes. A returned `DescendantsUnknown` result does not exercise this branch. Temporarily revert only the repair and prove the detail assertion fails, restore it, then run focused factory tests and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `LOW`; confidence `high`.
- `guard_confirmed=false`; no repair implemented or tested.
- The finding is diagnostic-only and requires both initialization and cleanup to fail; containment itself already remains fail-closed.
