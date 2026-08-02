# opr-42: State summary races the registry and can fault or mix snapshots

**Severity**: MEDIUM — an expected concurrent close or shutdown can turn a read-only state request into an uncaught operation fault, and independent registry snapshots can make the diagnostic self-contradictory.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan defines one point-in-time state result that keeps worker state, named-session identity, and registry count coherent across lease release.

**Source**: Whole-file Claude Opus 5 integration review of `NamedSessionSupervisor` and its active `WorkerSupervisor` caller at `ca7fe85`.

## Evidence

`server/PtkMcpServer/Sessions/WorkerSupervisor.cs:81-116` first awaits `_sessions.StateAsync`. `NamedSessionSupervisor.StateAsync` releases its operation lease in `server/PtkMcpServer/Sessions/NamedSessionSupervisor.cs:288-290` before the wrapper resumes. The wrapper then calls `_sessions.List().Single(...)` at `WorkerSupervisor.cs:92-93` and calls `_sessions.List()` independently again for the count at `:98`.

After lease release, a concurrent `CloseAsync` can remove the slot at `NamedSessionSupervisor.cs:801`, or `ShutdownAsync` can clear all slots at `:418`. `Single` then throws `InvalidOperationException`. The wrapper catches `NamedSessionException`, `WorkerProcessException`, and `WorkerProtocolException` at `WorkerSupervisor.cs:118-128`, but not `InvalidOperationException`, so the expected race escapes the operation boundary as an unformatted fault. If the name is closed and reopened, old worker state can be paired with the new incarnation's session snapshot. If another session opens or closes between the two list calls, the rendered session snapshot and `sessions=N/8` count come from different registry states.

## Predicted observable failure

One client requests state while another closes the same named session or the connection begins shutdown after the worker-state query completes. Instead of one coherent point-in-time state result, the operation can fault with an uncaught sequence exception. A close followed by reopening the same name can pair old worker state with a new incarnation's session snapshot, while a concurrent change to another session can make the count describe a different registry state from the snapshot printed below it.

## Required repair

Return a compound result from `NamedSessionSupervisor.StateAsync` containing the worker state plus the named-session snapshot and registry count captured under `_gate` after the worker query but before the operation lease is released. `WorkerSupervisor.StateAsync` must render only that compound result and perform no post-lease registry lookup. Do not hold `_gate` across the worker query or weaken close/shutdown concurrency.

Add a test-only barrier after the supervisor state result returns but before wrapper rendering. While the barrier is held, close the session and reopen the same name as a new incarnation, then release it and prove rendering uses the already-captured original point-in-time result without exception or cross-incarnation mixing. Cover shutdown at the same seam as the removal-only case. Instrument registry snapshot reads and assert summary construction performs no post-lease `List()` call; the current two-call implementation must fail that guard. Assert the rendered count and selected snapshot originate from the compound result's single registry observation. Prove the guards red against current code and green after repair, run the repository verification entry point, and obtain fixed-SHA Claude Opus 5 review.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 1,231 lines of `NamedSessionSupervisor.cs` at `ca7fe85` in three bounded source/caller/test passes plus one whole-file lifecycle/concurrency integration pass. Focused supervisor, lifecycle, artifact-capture, and real-process suites passed 36/36. Existing `opr-19` through `opr-23`, `gh-16-1`, and `gh-16-2` were excluded. Two independent adjudication stages and the integration pass accepted this wrapper-side race at MEDIUM and rejected the supervisor-local candidates as bounded, invariant-safe, fail-closed, current-inert, or unreachable through the concrete production worker. No product or test file changed in this finding slice.
