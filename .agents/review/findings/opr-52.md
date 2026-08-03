# opr-52: Worker terminal diagnostics collapse four classified causes

**Severity**: LOW — deterministic identity, incarnation, containment-group, and handle-direction failures keep the correct nonzero exit class but lose their specific terminal detail, making four actionable startup/protocol causes indistinguishable from generic failures.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan restores exact bounded terminal detail without broadening the diagnostic vocabulary.

**Source**: Complete-source Claude Opus 5 review of `server/PtkMcpServer/Worker/WorkerProcessExit.cs` at `e13bb8a`, integrated with `WorkerProcessEntry`, `WorkerServer`, both bootstrap implementations, and focused tests.

## Evidence

`WorkerServer.ValidateBeforeReady` emits `session_identity_mismatch` and `worker_incarnation_mismatch` as protocol failures. `WorkerProcessEntry.OpenProductionBootstrap` emits `containment_group_invalid`, and Unix descriptor access validation emits `handle_direction_invalid`, as bootstrap failures. `WorkerProcessExit.NormalizeProtocolDetail` omits the two protocol codes, while `NormalizeBootstrapDetail` omits the two bootstrap codes. All four therefore fall through to `protocol_error` or `bootstrap_failure` even though their producers already supply bounded machine codes.

## Predicted observable failure

A pre-ready frame targets the wrong session or incarnation, the Unix worker cannot enter its owned containment group, or inherited descriptor 3/4 has the wrong direction. The worker exits with the correct class code (82 for protocol, 80 for bootstrap), but stderr reports only the generic bucket. Operators cannot distinguish stale/misdirected frames, a security-relevant containment-arm failure, a descriptor misconfiguration, and an unexpected boundary failure from the terminal diagnostic.

## Required guard

Add the four exact self-mappings to the matching normalization allowlists. Extend process-exit tests so each producer code yields its specific bounded ASCII detail, hostile/unknown values still collapse to the generic bucket, graceful exits remain silent/zero, and the diagnostic remains within 256 bytes. Temporarily revert only the mappings, prove the four exact-detail guards fail, restore them, then run focused process-exit/entry/bootstrap/server tests and full server verification.

## Reviewer

Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`max`, no-tool).

- Verdict: `accepted`; severity `LOW`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- Exit selection, sanitization, byte bounds, single-write behavior, and graceful-exit semantics were reviewed as correct; impact is diagnostic truthfulness only.
