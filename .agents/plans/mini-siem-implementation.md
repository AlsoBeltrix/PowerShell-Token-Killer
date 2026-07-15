# Plan: mini-SIEM implementation — PTK-maintained OTLP receiver shipped as a separate product

## Status and authority

- The underlying decision was OPEN (`.agents/decisions.md:298-340`, appended by owner 2026-07-14, "No implementation is authorized" at `:300-302`).
- On 2026-07-15 the owner directed this session to move the mini-SIEM from R&D to planning and implementation, selected Option 1 (PTK-maintained receiver) with an expanded product scope, and answered the scope questions recorded in "Product decisions" below. This plan is the implementation counterpart the discovery plan's D5 gate anticipated (`.agents/plans/mini-siem-discovery.md:146-148`).
- The owner's instruction waives the D1 candidate-survey comparison (discovery plan `:99-112`): the standing recommendation to "compare the smallest existing durable OTLP deployment against a custom receiver" (`.agents/decisions.md:337-340`) is superseded by the owner's direct product decision to build.
- Slice S0 below drafts the decision-entry update. Per the reconciliation hold (`mini-siem-discovery.md:154-155`), only the owner lands edits to `.agents/decisions.md`; code slices S1+ are gated on that entry landing (or the owner explicitly delegating the append).

## Provenance

- Drafted 2026-07-15 on branch `plan/mini-siem-discovery`, worktree `.claude/worktrees/mini-siem`, cut from `master` @ `5ae154c`.
- Predecessor: `.agents/plans/mini-siem-discovery.md` (3-round codex review loop, findings msp-1..msp-9 all resolved).
- The resilience compatibility contract is consumed read-only from `git show 33d6a35:.agents/plans/mcp-resilience.md` (SIEM-relevant lines 34-37, 561-584, 686-690, 778-805, 845-880). This plan tracks that contract as it lands on master and never forks or restates it authoritatively.

## Binding inputs (inherited verbatim from discovery — not re-litigated)

1. **No new SIEM transport.** The receiver implements the exact authenticated one-record OTLP/HTTP protobuf request/ack/retry contract PTK already ships (`33d6a35` plan lines 561-584; `server/AUDIT-EXPORT.md`).
2. **Contract prohibitions 25-27** (`33d6a35` lines 853-856): never change the OTLP wire/envelope by destination or schema version; never drop or mistype a prior or `ptk.host.*` attribute; never let a receiver-side ack replace PTK's configured durable OTLP anchor semantics.
3. **Anchor boundary** (`server/AUDIT-EXPORT.md`): ack only after durable commit under a separately administered principal. A same-user sidecar or in-memory collector is not a meaningful anchor. A valid ack is the full valid/nonrejecting OTLP response per `server/AUDIT-EXPORT.md:87-97` — a bare `200` is not an ack.
4. **Conformance seam** (`33d6a35` lines 799-801; discovery `:50-54`): the mini-SIEM conformance fixture must be able to replace the resilience plan's fake durable receiver **without changing PTK bytes**.
5. **The four typed v3 attributes** (`33d6a35` lines ~567-575): `ptk.host.boot_id: string` (omitted when null), `ptk.host.generation: int64` (omitted when null), `ptk.host.state: string` (always present), `ptk.host.recovery_attempt: int64` (always present).

## Product decisions (owner, 2026-07-15 session)

| Topic | Decision |
|---|---|
| MVP surface | Receiver + durable store + query + alert rules + web dashboard |
| Storage | SQLite (`Microsoft.Data.Sqlite`), with receiver-side hash-chain tamper evidence in rows |
| Dev home | New top-level `siem/` directory with its own solution; separate product box |
| Code sharing | Share the vendored proto (`server/PtkMcpServer/Protos/audit_otlp.proto`) and golden v1/v2/v3 fixture bytes only; no shared runtime code |
| Dashboard | ASP.NET Core minimal API + embedded static htmx pages; no node toolchain |
| Platforms | Cross-platform (ubuntu/windows/macos); MSI as optional Windows component in a later packaging phase; tar/zip elsewhere |

## Architecture

New solution `siem/PtkSiem.slnx` (house `.slnx` format), all projects `net10.0`, `Nullable` + `ImplicitUsings` enable, no analyzers/AOT (matches `server/` conventions):

- **`siem/PtkSiemReceiver/PtkSiemReceiver.csproj`** (`Microsoft.NET.Sdk.Web`, Exe). Generic Host + `BackgroundService` composition mirroring `server/PtkMcpServer/Program.cs`. Two Kestrel endpoints:
  - **Ingest endpoint** (mTLS required): `POST /v1/logs`, OTLP/HTTP protobuf only (`Content-Type: application/x-protobuf`). Client certificates validated against the configured PTK client CA.
  - **Operator endpoint** (separate port, loopback-bound by default): query API + dashboard.
- **Wire layer**: compiles the canonical proto via `<Protobuf Include="../../server/PtkMcpServer/Protos/audit_otlp.proto" Link="Protos/audit_otlp.proto" GrpcServices="None" />` with `Google.Protobuf` 3.35.1 + `Grpc.Tools` 2.82.0 (`PrivateAssets=all`) — same packages/pattern as the producer, independently compiled, no `OpenTelemetry.*` NuGet. PTK's copy of the proto is never edited by this plan.
- **Ingest pipeline** (per request, in order):
  1. Parse `ExportLogsServiceRequest`; malformed/oversized ⇒ HTTP 400, never an ack. Request size cap configurable (default 1 MiB).
  2. Extract the single log record (producer sends one record per request); validate schema version (v1/v2/v3 accepted), retained attribute names/types, and the four `ptk.host.*` rules.
  3. Chain validation: verify `event_hash`/`prev_hash` continuity per boot chain against the store; a broken chain ⇒ OTLP rejection (`partial_success.rejected_log_records = 1` with reason), never a false ack.
  4. Duplicate detection by event ID + hash: identical replay ⇒ idempotent success ack without a second row (at-least-once delivery, discovery matrix row 3); same event ID with different bytes ⇒ rejection.
  5. **Durable commit**: single SQLite transaction inserting the raw request bytes + extracted columns; `PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;` so the commit is fsync-durable before the response is written.
  6. **Ack**: only after the transaction commits, write HTTP 200 + serialized `ExportLogsServiceResponse` with no `partial_success` rejection (the valid nonrejecting ack of `server/AUDIT-EXPORT.md:87-97`).
  - Disk-full / `SQLITE_FULL` / any commit failure ⇒ HTTP 503 (+ `Retry-After`) or OTLP rejection as appropriate — **never a false 200** (discovery `:132-134`). 503 is the backpressure signal PTK's existing retry schedule honors.
- **Store** (`siem/PtkSiemReceiver`, `Storage/`): SQLite file under a configurable data root. Tables: `events` (unique event ID, boot id, sequence, schema version, raw protobuf bytes, event hash, prev hash, received UTC, extracted query columns + indexes), `chains` (per-boot chain head state), `gaps`/`alerts`, `meta` (receiver identity, schema migration version). Raw bytes are the evidence of record; extracted columns are derived and rebuildable.
- **Query API + dashboard** (`Web/`): read-only minimal-API endpoints (events by time/type/session/boot filters, event detail with chain context, chain status, gap and alert lists) rendered by embedded static htmx pages. Operator auth: bearer token from the config file (0600-perm), endpoint loopback-bound unless explicitly configured otherwise — read authorization distinct from the ingest path (matrix rows 7/8).
- **Alert rules** (`Alerting/`): declarative rules in the config file — event-type/attribute match, chain-break, gap-detected, ingest-rate threshold. Actions: dashboard alert list + optional webhook POST with retry. Evaluated post-commit (never blocks the ack path).
- **Retention** (`Storage/RetentionService`): age/size-based purge as a `BackgroundService`; purge records tombstone chain summaries so chain verification over retained history still succeeds; retention settings in config (matrix row 8).
- **Config**: env var `PTK_SIEM_CONFIG` → one validated JSON file, frozen at startup, modeled on `AuditExportConfigurationLoader`/`AuditStartupConfiguration` (`server/PtkMcpServer/Audit/AuditExportConfiguration.cs`, `AuditStartupConfiguration.cs`): ingest bind address, server certificate + key, client CA, operator bind address + token, SQLite path, retention, alert rules. Strict validation, actionable failures, no fallback defaults for security-relevant fields.
- **Separate principal** (matrix rows 1/7; `server/AUDIT-EXPORT.md:205`): the receiver is documented and packaged to run as its own OS account, with the data root and config owned by that account (0700/0600). Install docs (S7) cover Windows service account, and dedicated users on macOS/Linux. `PtkAuditAdmin`-style same-user layouts are documented as non-anchoring.
- **`siem/PtkSiemReceiver.Tests/PtkSiemReceiver.Tests.csproj`**: xUnit 2.9.3 / runner 3.1.4 / Test SDK 17.14.1 / coverlet 6.0.4, `<Using Include="Xunit" />`, flat `*Tests.cs` files, nested `private sealed class XxxFixture : IDisposable`, `[CollectionDefinition(..., DisableParallelization = true)]` for process/port tests — the `server/PtkMcpServer.Tests` house style.

## Slices

- **S0 — Decision entry + governance.** Draft the decision-entry update (below) for the owner to land in `.agents/decisions.md`; commit this plan. Code slices are gated on the entry landing.
- **S1 — Skeleton.** `siem/PtkSiem.slnx`, receiver + tests projects, config loader with validation tests, CI job (`dotnet test siem/PtkSiem.slnx` added to `.github/workflows/ci.yml` across the existing three-OS matrix). No PTK files modified.
- **S2 — OTLP ingest surface.** Proto compilation, Kestrel mTLS ingest endpoint, request parsing/validation, response semantics (valid ack, rejection, 400, 503 backpressure, size caps). Tests drive the endpoint with real client certs from a `FakeOtlpPki`-style helper (pattern from `server/PtkMcpServer.Tests/FakeOtlpHttpsReceiver.cs`).
- **S3 — Durable store.** SQLite schema + migrations, durable-before-ack commit ordering, duplicate idempotence, chain validation, disk-full/commit-failure rejection paths, crash-consistency (WAL + synchronous=FULL) tests.
- **S4 — Conformance suite (the two barriers).** Golden v1/v2/v3 fixture bytes captured from the producer's test corpus (`server/PtkMcpServer.Tests/AuditCoreSchemaTestRecords.cs` lineage) checked in under `siem/PtkSiemReceiver.Tests/Fixtures/`; tests assert: exact v1/v2/v3 bodies accepted; every retained attribute name/type preserved; the four `ptk.host.*` rules; Unicode + timestamp fidelity; fixture bytes byte-identical to the producer corpus (PTK bytes unchanged). **Pre-commit barrier**: receiver process killed before commit has emitted no valid nonrejecting ack. **Post-ack barrier**: after any observed valid ack, immediate `kill -9` + restart still serves the record with intact chain state; duplicate replay after restart stays idempotent. **Discrimination**: the same suite runs against a deliberately non-durable configuration (`synchronous=OFF`, ack-before-commit test double) and must fail it (discovery `:163-170`).
- **S5 — Query API + dashboard.** Read-only endpoints + htmx pages, operator token auth, loopback default. Endpoint handler tests + one end-to-end ingest→query test.
- **S6 — Alerts.** Rule evaluation, alert persistence, webhook action with bounded retry, dashboard surfacing. Rule-unit + pipeline tests.
- **S7 — Ops + docs.** `siem/README.md`: install (per-OS separate-account guidance), backup/restore (SQLite online backup API), upgrade (schema migration policy), retention, threat-model summary mapping to matrix rows 1/6/7/8/10/11.
- **S8 — Packaging (follow-up gate).** `dotnet publish` release artifacts (tar/zip) wired into `release-distribution.md` conventions; MSI optional-component authoring is a separate later plan — explicitly out of scope here.

Each slice lands as its own commit(s) with tests green (AGENTS.md: commit each slice as it lands).

## Acceptance matrix mapping (discovery `:85-95` → this plan)

| Row | Requirement | Where satisfied |
|---|---|---|
| 1 | Threat model + separate service identity | S7 threat-model doc; separate-account packaging/config (Architecture: Separate principal) |
| 2 | Durable-before-`200` | S3 commit-before-ack ordering; S4 both barriers |
| 3 | Duplicate handling (at-least-once) | S3 idempotence; S4 replay tests |
| 4 | Event-ID/hash-chain validation | S2/S3 ingest chain validation; broken chain ⇒ rejection |
| 5 | Crash/disk-full/backpressure/restart | S3 failure paths (never false ack); S4 restart tests; 503 backpressure |
| 6 | mTLS or equivalent | S2 mTLS ingest endpoint, client CA validation |
| 7 | Receiver host storage protection | Separate account + 0700/0600 data root (S1 config, S7 docs) |
| 8 | Retention + read authorization | Retention service; operator token + loopback default (S5) |
| 9 | Minimum useful queries/alerts | S5 query set; S6 rules |
| 10 | Upgrade/backup/recovery ownership | S7 docs (SQLite backup API, migration policy) |
| 11 | Network-service patch burden | S7 doc: surface inventory (two endpoints), dependency list, update cadence |

## Drafted decision-entry update (S0 — owner lands this)

> ### ACTIVE (2026-07-15): mini-SIEM receiver build authorized — .agents/plans/mini-siem-implementation.md
> **Status:** Owner directed the move from discovery to implementation in-session on 2026-07-15, selecting Option 1 (PTK-maintained receiver) expanded to a separately installed product (receiver, SQLite durable store, query, alerts, web dashboard; cross-platform with later Windows MSI packaging). The owner waived the D1 existing-deployment comparison from the standing recommendation. The OPEN (2026-07-14) mini-SIEM entry's acceptance questions become the build's acceptance matrix via the implementation plan; that entry should be annotated as superseded by this one. Discovery evidence: `.agents/plans/mini-siem-discovery.md` (codex-reviewed, msp-1..9 resolved).

## Non-goals

- No changes to PTK: no edits under `server/` or `src/` (the proto is referenced read-only by relative path), no OTLP wire/envelope changes, no changes to `server/AUDIT-EXPORT.md` semantics. The only shared-tree edit outside `siem/` is the CI job addition in `.github/workflows/ci.yml` and the S0 decision entry (owner-landed).
- No edits to the resilience plan, its branch, or its conformance suite; the receiver plugs into the seam without changing PTK bytes.
- No shared runtime library between PTK and the receiver (proto + fixture bytes only).
- No Sentinel/Splunk/Collector adapters, no multi-tenant support, no remote admin plane, no MSI authoring in this plan (S8 gate).
- No daemon/service coupling into PTK sessions — the receiver is an independent product process.

## Verification

- `dotnet test siem/PtkSiem.slnx` — receiver unit + integration + conformance suites (S2-S6), green on ubuntu/windows/macos CI matrix.
- S4 barrier suite passes against the real receiver and **fails** the deliberately non-durable configuration (discrimination requirement).
- Fixture-fidelity check: golden fixture bytes byte-identical to the producer corpus at head; any producer-side contract change breaks the receiver's conformance suite loudly (tracking, never forking, the contract).
- Existing battery stays green and untouched: `dotnet test server/PtkMcpServer.slnx` (1484/1484 at baseline), `Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1` (141 passed / 2 Windows-only skips at baseline), `pwsh -NoProfile -File server/test-handshake.ps1` (HANDSHAKE PASSED).
- Manual smoke: run receiver with a sample config; drive PTK anchored mode at it end-to-end; confirm checkpoint advance on the producer side and event visibility in the dashboard.

## References

- `.agents/decisions.md:298-340` — the OPEN mini-SIEM entry (status `:300-302`, acceptance questions `:329-335`, recommendation `:337-340`).
- `.agents/plans/mini-siem-discovery.md` — matrix `:85-95`, grading rule `:106-112`, identity `:113-121`, barriers `:122-138`, fixtures `:134-138`, non-goals `:150-156`, discrimination `:163-170`.
- `server/AUDIT-EXPORT.md` — transport/ack/anchor contract (valid nonrejecting ack `:87-97`, interface-boundary precedent `:205`).
- `git show 33d6a35:.agents/plans/mcp-resilience.md` — transport `:561-584`, compat fixtures `:785-801`, prohibitions `:853-856`.
- Producer code (patterns, read-only): `server/PtkMcpServer/Protos/audit_otlp.proto`, `server/PtkMcpServer/Audit/AuditOtlpHttpExporter.cs`, `AuditOtlpRecordMapper.cs`, `AuditExportCheckpointStore.cs`, `AuditExportConfiguration.cs`, `AuditStartupConfiguration.cs`, `server/PtkMcpServer.Tests/FakeOtlpHttpsReceiver.cs`, `AuditCoreSchemaTestRecords.cs`.
