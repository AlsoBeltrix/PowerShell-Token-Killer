# rbc-11: SIEM receiver parses retention options but never enforces them

Severity: MAJOR. Source: rbc review batch, 2026-07-19. Status: CLOSED
(fixed 2026-08-10 under the blanket fix authorization, in the
audit-restoration R3 slice; oar1-1 made it a prerequisite for any slice
that ships or documents deploying the receiver).

## Evidence

`siem/PtkSiemReceiver/Configuration/SiemReceiverConfiguration.cs` parsed
and validated `RetentionMaxAgeDays` / `RetentionMaxTotalBytes`, and no
code on `master` ever applied them — a grep for a retention service
returned nothing. `events`, `quarantine`, and `custody` store full
`raw_request` BLOBs, so the store grew without bound.

## Predicted observable failure

An unattended receiver's SQLite store grows until the disk fills; once
writes fail the receiver must reject records, losing audit custody
exactly when it matters. The README warned against deploying a master
build for this reason.

## What

`SqliteIngestStore.EnforceRetentionAsync` plus a
`Storage/RetentionService` background sweep (15-minute interval,
registered in `ReceiverApplication`). Age bound deletes events and
quarantine attempts older than the cutoff; size bound trims oldest-first
in bounded batches, vacuuming so the file actually shrinks and the sweep
converges. Two exclusions are deliberate and guarded: **custody receipts
are never swept** (append-only evidence — deleting it would destroy what
retention protects), and **chain heads are never swept** (a later record
must still validate against its predecessor's hash). A sweep failure is
logged and retried; retention housekeeping never takes ingest down.

## Guard proof

`siem/PtkSiemReceiver.Tests/RetentionEnforcementTests.cs` (5 tests): age
sweep removes old events while custody count is unchanged; records inside
the window survive; unconfigured retention removes nothing; a size bound
shrinks the database (measured through the same PRAGMA-derived size the
sweep uses, since WAL makes the main file's length meaningless); the
service sweeps on demand and returns null instead of throwing when the
store is gone. Two sabotages, each proved to fail exactly these tests:
(1) the age delete made a no-op, (2) the sweep extended to delete custody
rows. SIEM suite 252/252 after restore.

## Files changed

- `siem/PtkSiemReceiver/Storage/SqliteIngestStore.cs`
- `siem/PtkSiemReceiver/Storage/RetentionService.cs` (new)
- `siem/PtkSiemReceiver/Storage/SiemRetentionOutcome.cs` (new)
- `siem/PtkSiemReceiver/Ingest/ReceiverApplication.cs`
- `siem/PtkSiemReceiver/README.md` (deployment warning replaced by the
  retention contract)
- `siem/PtkSiemReceiver.Tests/RetentionEnforcementTests.cs` (new)

## Known gaps

The 2026-07-19 decision entry gated deployment guidance on an S3H
land/park ruling. S3H's storage hardening landed separately; this fix
closes the retention half on `master` directly, so that gate no longer
blocks receiver deployment guidance. The receiver still needs its
token-auth ingest path (R3c) before PTK's own exporter can reach it.
