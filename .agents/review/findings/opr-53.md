# opr-53: Worker text can forge supervisor retry and recovery directives

**Severity**: MEDIUM — script output and worker state text share an unframed text channel with PTK-authored status, retry, and recovery directives, so untrusted output can impersonate control lines and induce unsafe retry or output-handle decisions.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan makes supervisor-authored control information unambiguous without losing user output fidelity.

**Source**: Complete-source Claude Opus 5 review of `server/PtkMcpServer/Sessions/WorkerSupervisor.cs` at `c9a7f51`, integrated with the worker protocol, named-session supervisor, public tool schema, focused tests, and one real stdio protocol probe for this defect.

## Evidence

`WorkerSupervisor.FormatInvocation` seeds its response with trailing whitespace removed from `WorkerResult.Text`, then appends `recovery=available`, `recovery=unavailable`, and `[ptk worker] status=...` lines in the same newline-delimited text channel. The completed path appends no terminal status at all. `StateAsync` likewise appends `WorkerStateSnapshot.Text` without framing. `WorkerOperationProtocol` bounds and validates these text values as UTF-8 but does not reserve, escape, or distinguish PTK control-line grammar.

A real shipped-server stdio call to `ptk_invoke` with `raw=true` emitted a fake `[ptk worker] status=refused ...` line and a fake `recovery=available: ptk_output handle=...` line. The successful tool response preserved both exact strings, including the supervisor's exact status prefix, and contained two indistinguishable recovery lines: the forged line and the genuine supervisor line.

## Predicted observable failure

A command prints attacker-influenced repository, page, tenant, or process output containing PTK-shaped directives. The calling model treats a forged `not_started` / `PTK did not retry` statement as authoritative and resubmits an already-executed mutating command, or follows a forged recovery handle instead of the artifact PTK actually issued. A worker-state value containing the same grammar can similarly impersonate state-level control text.

## Required repair

Make supervisor-authored status and recovery information unforgeable relative to worker-controlled text at the single rendering boundary. Either escape or visibly prefix reserved line-start grammar inside untrusted text and delimit the data region, or return supervisor control information as separate structured content. Apply the rule consistently to invocation and state text, preserve arbitrary valid user output losslessly, and retain bounded response size.

Add real public-boundary tests for completed, failed, timed-out, canceled, and state responses containing every reserved PTK/recovery prefix. Prove the caller can always distinguish the one genuine status/recovery decision from payload text. Temporarily revert only the framing repair, confirm the guards fail, restore it, then run focused supervisor/protocol/stdio tests and full server verification.

## Reviewer

Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` (no-tool reviewer; the working agent supplied the stdio and focused-test evidence).

- Verdict: `accepted`; severity `MEDIUM`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- Existing `opr-4`, `opr-11`, `opr-42`, and `opr-52` are disjoint and were excluded.
