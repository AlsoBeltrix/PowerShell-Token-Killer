# rbc-10: SIEM receiver Kestrel MaxRequestBodySize disabled (defense-in-depth gap)

**Severity**: MAJOR
**Status**: Open (intake, awaiting owner triage)
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `siem/PtkSiemReceiver/Ingest/ReceiverApplication.cs:40`

## Evidence

`kestrel.Limits.MaxRequestBodySize = null` removes Kestrel's built-in
body-size limit entirely. The custom `ReadBoundedAsync` enforces
`options.MaxRequestBytes` for the `/v1/logs` endpoint, but this removes
a layer of defense.

If a future endpoint is added that reads the request body differently
(or via model binding), it would have no size limit at all, creating a
memory-exhaustion vector.

## Predicted observable failure

A future SIEM endpoint (e.g., a query API or a health-check that
deserializes a body) accepts an unbounded request, exhausting memory
under load. The custom `ReadBoundedAsync` only protects `/v1/logs`.

## What

Set `MaxRequestBodySize` to `options.MaxRequestBytes` (or a slightly
higher bound) at the Kestrel level. This provides defense-in-depth
without changing current behavior for the `/v1/logs` endpoint.

## Scope of fix

One line in `ReceiverApplication.cs`. No architectural change.

## Guard proof

Not yet written. A guard should assert that a request body larger than
`MaxRequestBodySize` is rejected by Kestrel before reaching the
endpoint, regardless of which endpoint handles it.

## Reviewer comments

Read-only review by Hermes subagent (SIEM receiver pass). No external
fixed-SHA review has been dispatched.