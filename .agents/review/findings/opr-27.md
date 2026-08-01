# opr-27: Structured broker start failures are reported as protocol corruption

**Severity**: LOW — a valid, already-contained broker startup failure loses its stage and native error and is surfaced as `unix_worker_broker_protocol_invalid`, directing diagnosis toward wire corruption.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan defines stable failure projection for the broker's structured startup stages and native error without weakening containment verification.

**Source**: Multi-pass no-tool Claude Opus 5 review of `server/PtkMcpServer/Worker/UnixWorkerProcessLauncher.cs` at `488e65354e1562c6b80f0adbdee8a07c53af08df`, followed by focused adjudication against `ptk_worker_broker.c` and final merge review.

## Evidence

The broker emits `StartFailed` with a stage and native error after `fail_started_worker` has contained the child. Managed `ParseEvent` decodes both fields. Each handshake step nevertheless calls `RequireKind` for only its expected success event; `RequireKind` treats `StartFailed` as any other unexpected kind and throws `unix_worker_broker_protocol_invalid`. Failures in child setup, identity capture, group arm/validation, gate release, or exec therefore become indistinguishable from malformed or version-incompatible protocol bytes. The later containment path independently verifies the broker exit, so this finding does not change containment or slot state.

## Predicted observable failure

The broker cannot exec the worker because its executable is absent or inaccessible. Instead of identifying the exec stage and native error, startup reports a broker protocol violation. Operators investigate framing/version corruption while the actual failure is a normal, structured launch error.

## Required guard

Add managed handshake tests that inject each valid `StartFailed` stage with representative native errors at every expected-event boundary. Assert the failure is not classified as protocol-invalid, preserves the stable stage-specific projection selected by the approved plan, and retains confirmed containment behavior. Add malformed-kind and malformed-payload controls that remain protocol-invalid. Temporarily revert only the repair, prove the structured-failure assertions fail, restore it, then run focused broker protocol/launcher tests and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `LOW`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- Containment and process provenance remain unchanged; this finding is diagnostic classification only.
