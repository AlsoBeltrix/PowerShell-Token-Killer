# Plan: base-level non-bypassable audit restoration

**Status: DRAFT 2026-08-10 — design mandate ruled by the owner; no slice
approved, no code authorized.** This plan supersedes, on the owner's
word, every prior instruction to keep the audit producer removed
(salvage decision 4, `.agents/decisions.md:624`, executed at `ddbb908`).
`.agents/state.md` §Now owns the correction record.

## Owner mandate (2026-08-10, verbatim)

> "I never consciously removed SIEM output. that's a p0 requirement."

> "this was part of the design from step 0, and it was all supposed to
> be built with auditing integrated at a base level and non-bypassable.
> that's what it needs to be."

## What the mandate means (interpretation — confirm before R2)

- Audit is core architecture, not a mode. Effectful invocations produce
  durable audit evidence; no configuration disables it.
- Non-bypassable means fail-closed: when the audit path cannot record,
  execution does not proceed. The salvage plan's "availability conflict"
  framing (`production-reliability-salvage.md` §Audit) is inverted by
  this mandate — fail-closed is the requirement, and the engineering
  duty is a reliable audit path with actionable failure modes, never an
  optional one.
- SIEM export is the custody leg and is P0. The `siem/` receiver
  (S1–S3+S3H landed, 247 tests, untouched by the removal) is the
  destination; the producer must come back.

## Inventory — everything is recoverable at `ddbb908^`

Producer surface deleted 2026-07-27: pre-effect admission guard and
runtime gate (`AuditPreEffectGuard`, `AuditRuntimeGate`, call-filter
integration), evidence spool (`AuditClosedSpoolExportPump`,
`AuditLiveSpoolReader`), OTLP exporter + record mapper + export
coordinator/loop/retry/ack observer, export configuration + identity,
retention, `PtkAuditAdmin` legs, `server/AUDIT-EXPORT.md` semantics,
producer-to-SIEM conformance suite (`AuditOtlpSiemConformanceTests`,
`SiemConformance/`), `FakeOtlpHttpsReceiver`, CI legs, dev-install
wiring. `SecureAuditStorage` was retained on master (OutputStore uses
it).

## Problems the restoration must solve, not bypass

1. **ARM64 Linux `Grpc.Tools`/protoc build crash** (recorded blocker
   that motivated part of the removal). Candidates: OTLP/HTTP JSON
   encoding (drops protobuf codegen; receiver S2 currently parses
   protobuf, so the receiver grows a JSON ingest path), vendored
   pre-generated protobuf code, or a real-hardware toolchain fix. Ruled
   in R1 with evidence (Q2).
2. **Audit-root/startup incidents**: an unknown artifact under the audit
   root used to refuse startup with poor diagnosability. Fail-closed
   stays; the failure must name itself and its repair path.
3. **Topology drift**: the deleted code predates the salvage topology
   (supervisor + named contained workers, `opr-53` structured verdicts,
   heartbeats). The gate is re-seated in the current architecture, not
   restored verbatim.
4. **The battery currently asserts audit absence** (handshake "audit
   disablement ok", direct product proof, CI). Each slice flips its
   assertions deliberately — those flips are the slice's guard, not
   collateral.
5. **Mini-SIEM S4 fixture gate**: producer-owned golden corpora become
   possible again. Its v3 corpus depended on the resilience line's v3
   record, which never landed on master — regate S4 to v1/v2 or land v3
   first (Q3).
6. **Public-release contract**: v0.2.x shipped without audit. Restoring
   mandatory fail-closed audit changes behavior for every installed
   user (evidence written per invocation; audit failure blocks
   execution). Release framing/versioning is an owner call at ship
   time.

## Product contract (owner, 2026-08-10, plain words)

The owner's requirements, verbatim intent: "it needs to log. if there's
siem connected, it needs to log there. there needs be dead-simple way
configure SIEM connection. we need WEB GUI see the logs. we need web
settings page where this can be configured."

1. **PTK always logs.** Durable, non-bypassable: cannot record → does
   not execute.
2. **SIEM connected → logs flow there** with durable acknowledgment.
3. **Dead-simple SIEM connection configuration.** One endpoint setting,
   not a certificate ceremony. The S2 mTLS surface stays available for
   hardened deployments but must not be the price of entry.
4. **Web GUI to see the logs.**
5. **Web settings page** where the SIEM connection (and audit settings)
   are configured.

This resolves former Q1 as: local logging always-on; export joins the
gate when a receiver is configured. The former Q2 (wire encoding) and
Q3 (S4 fixture scope) are engineering calls, settled inside R1 with
recorded evidence — not owner questions.

## Recommended shape (validated in R1 before any production code)

One log store and one web surface, not two: the `siem/` receiver — the
only component with a durable store (S1–S3+S3H, 247 tests) and a planned
dashboard (S5) — becomes the log destination in every deployment.
A default install runs it locally on loopback with zero-config
(auto-provisioned, no operator ceremony); "connecting a SIEM" means
pointing the settings page at a remote receiver instead. The web GUI is
the receiver's dashboard (S5) plus a settings page (new). PTK's producer
(restored from `ddbb908^`) spools locally and exports with
acknowledgment; fail-closed per the mandate. R1 must validate the
local-receiver lifecycle (who starts it, crash behavior under the
fail-closed rule) before this shape is confirmed.

## Slices (each needs its own explicit go)

- **R0** — Owner approves this plan; a decisions.md entry lands per the
  hold protocol (owner-landed).
- **R1 — Discovery, no production code.** Diff the `ddbb908^` audit
  surface against the current topology; produce the re-seating design;
  settle encoding (protobuf vs OTLP/HTTP JSON) and the S4 fixture
  regating with evidence; validate the local-receiver shape above.
- **R2 — Local mandatory audit.** Admission gate + evidence + retention,
  fail-closed, in the current worker topology; actionable failure
  diagnostics; flip the handshake/product-proof audit assertions.
- **R3 — Export leg.** Spool, exporter, mapper, acknowledgment gating;
  the dead-simple connection setting.
- **R4 — Web surface.** Receiver dashboard (mini-SIEM S5 executed under
  that plan's authority) + the settings page; the end-to-end "open a
  browser, see the logs" proof.
- **R5 — Conformance + alerts.** Producer-to-SIEM conformance and the
  golden-fixture serializer (unblocks mini-SIEM S4); S6 alerts as
  scheduled by the mini-SIEM plan.
- **R6 — CI, docs, packaging, release gates.** CI legs,
  `AUDIT-EXPORT.md`, READMEs, installer wiring for the local receiver,
  release-gate updates.

## Verification

Per-slice: the standing battery (`.agents/repo-guidance.md`
§Verification) plus that slice's deliberately flipped audit assertions.
Receiver-side proofs stay governed by
`.agents/plans/mini-siem-implementation.md` §Verification. End state:
the mini-SIEM S7 manual smoke — PTK driven at a receiver on another
host, events visible in the dashboard — is the "owner has seen it work"
gate this effort exists to reach.

## References

- `.agents/decisions.md:624` (the delegated settlement this supersedes)
- `ddbb908` / `ddbb908^` (removal commit / last commit with the producer)
- `.agents/plans/production-reliability-salvage.md` §Audit (the removal
  rationale; its engineering lessons stand, its direction is void)
- `.agents/plans/mini-siem-implementation.md`, `mini-siem-discovery.md`
