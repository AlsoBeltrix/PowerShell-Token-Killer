# opr-36: Reserved-byte accounting overflows at the 2 GiB boundary

**Severity**: MEDIUM — an allowed multi-gigabyte audit configuration can throw during admission and leak reservation slots when concurrent reservations cross 2 GiB.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan widens reservation-byte arithmetic before state mutation and preserves exact admission and recovery accounting.

**Source**: Complete bounded no-tool Claude Opus 5 review of `server/PtkMcpServer/Audit/AuditJournal.cs` at `5840ce8392c4cdeca471435e6d3b3c61a64d9537`, followed by configuration-bound and production-caller adjudication.

## Evidence

`ReservedBytesLocked` returns `long` but evaluates `checked(_reservedSlots * _options.MaxRecordBytes)` as an `int` product. `AuditOptions` permits `MaxRecordBytes` of 65,536 and aggregate capacity up to 1 TiB, so 32,768 outstanding record slots are a valid capacity state but overflow the product at 2 GiB.

`TryReserve` correctly widens the requested-slot product, verifies sink capacity, increments `_reservedSlots`, and creates the reservation before `UpdateHealthMetricsLocked` calls the faulty helper. A reservation that crosses the boundary therefore mutates accounting and then throws instead of returning its lease. Later admission and recovery also call the same helper and fail while the outstanding count remains above the boundary.

The largest production call profile reserves 11 record slots. Roughly 2,979 simultaneous largest-profile calls can reach the boundary; no global request-concurrency cap or lower configuration bound excludes that state. The precondition is extreme but supported.

## Impact

Admission can surface an unexpected exception, lose the newly constructed reservation lease while retaining its slot count, and keep the journal unavailable until enough other reservations drain. Repeated boundary failures can accumulate leaked slots and make the capacity refusal persistent despite available configured storage.

## Required guard

Use an allowed audit configuration above 2 GiB with 65,536-byte records and a capacity-capable sink. Hold production-sized reservations until one crosses 32,768 slots, then prove admission returns a usable lease, `ReservedBytes` reports the exact `long` total, release restores the exact count, and capacity recovery remains available. Temporarily revert only the arithmetic-width repair, prove the crossing admission throws after mutating the count, restore it, and run focused journal, health, file-sink, and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `MEDIUM`; confidence `medium` because the supported trigger requires extreme concurrency.
- `guard_confirmed=false`; no repair implemented or tested.
- This finding is distinct from `opr-35`: it concerns arithmetic width in reservation accounting, not evidence reconciliation after ambiguous persistence.
