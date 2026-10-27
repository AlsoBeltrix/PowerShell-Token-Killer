# Plan: remove audit and SIEM

**Status:** DRAFT 2026-10-27, awaiting the owner's approval. Implementation
happens on another box (owner, 2026-10-27: "don't start code removal here,
that'll happen on the other box"); it needs an explicit go there. Owner
rulings R1–R3 below are pending.

## Authority and goal

Decision: `.agents/decisions.md`, ACTIVE (2026-10-27) "Audit and SIEM are
dead; their code is to be removed". The owner uses PTK at home for
low-stakes work and has no SIEM; the audit journal, audit export, the
mini-SIEM receiver and per-call attribution existed for an enterprise case
that is no longer pursued.

Goal: PTK without any audit or SIEM code, tests, proof gates, release
artifacts or documentation, and with every other behavior unchanged. This
plan is the only audit/SIEM work the decision allows. It is removal, not
redesign: do not refactor neighbouring code beyond what deleting a call
site needs, and do not rename things the plan does not name.

## Base facts (inventory at `5e790198`, 2026-10-27)

Gathered by a read-only sweep; line numbers are as of `5e790198` and drift.
Re-grep before editing.

**Removed whole**

- `server/PtkMcpServer/Audit/**` (~28.9k lines: journal, sink, spool and
  quota lease, chains, anchoring, boot lineage, script-evidence store and
  provider, call context and metadata filter, health, runtime gate,
  lifecycle, operator dispositions, `Export/*`, `Web/AuditWebUiService.cs`
  (loopback UI, port 8317), `AuditAlertWebhookService.cs`) — **except
  `SecureAuditStorage.cs`**, below.
- `server/PtkAuditAdmin/` (the `ptk-audit-admin` CLI; listed in
  `server/PtkMcpServer.slnx`, `InternalsVisibleTo` in `PtkMcpServer.csproj`).
- `server/AUDIT-EXPORT.md`, `scripts/ptk-audit-destination.ps1`.
- Audit fixtures in `server/Contracts/ResilienceR0/`: `audit-v3-host.jsonl`,
  `audit-v3-null.jsonl`, `sentinel-{dcr,event,table}.json`,
  `splunk-hec-event.jsonl`, `splunk-hec-fixture.yaml`, `adapter-pins.json`,
  `adapter-live-validation.json`. **Keep** `public-tool-contract.json` and
  its `.sha256` (`ToolSchemaConformanceTests`). Trim `.gitattributes`
  patterns that no longer match anything.
- `siem/**` (~25k lines: `PtkSiem.slnx`, receiver, test host, tests,
  `manage.ps1`, `test-manage.ps1`, `operator-workflow-proof.ps1`,
  `operator-readiness-acceptance.ps1`, `build-package.ps1`,
  `verify-package.ps1`, `test-verify-package.ps1`, receiver README). Its
  NuGet dependencies (Google.Protobuf, Grpc.Tools, Microsoft.Data.Sqlite,
  SQLitePCLRaw, Hosting.WindowsServices) go with it.
- Server tests that are audit-only (~20.1k lines, ~481 facts/theories):
  every `server/PtkMcpServer.Tests/Audit*.cs` (38 files),
  `FileAuditJournalSinkTests`, `ScriptEvidenceStoreTests`,
  `ScriptEvidenceLocalRetentionTests`, `ExportRetentionFloorTests`,
  `SiemConformanceGoldenTests` and `SiemConformance/*.golden.json`.

**Kept, not audit (do not delete)**

- `SecureAuditStorage.cs`: owner-only ACLs, exclusive create, protected
  delete and identity checks that `Execution/OutputStore.cs` and
  `Execution/OutputRootLease.cs` (the `ptk_output` store) depend on. It has
  no dependency on other audit types. Move it out of the audit namespace
  (for example `Execution/SecureStorage.cs`, class `SecureStorage`) and
  rename `SecureAuditStorageTests` to match.
- `RtkExecutableIdentity.AuditBinaryDigest` (`Execution/ExecutionPlan.cs`):
  the rtk binary-digest check in output shaping. Core; leave it (renaming is
  out of scope).
- Worker containment (`UnixWorkerContainmentRegistry`,
  `WindowsProcessTreeSupervisor`, `ProcessTreeContainment`), the
  worker-exit diagnostics (GitHub #13), `ToolOutcome` dispositions,
  `OperatorCli`, `PtkHook`, the PowerShell module `src/`, and
  `scripts/ptk_init.ps1`: no audit dependency beyond comments.
- `System.Security.Cryptography.Pkcs`/`Xml` pins in `PtkMcpServer.csproj`:
  CVE pins for packages that PowerShell's SDK pulls in, not audit.

**Coupling (call sites to delete)**

- `Program.cs`: audit settings, four of the five hosted services (runtime
  gate, web UI, alert webhook, export coordinator), `AuditCallFilter` as the
  outermost filter, the export status line, and the
  `Sessions/AuditScopedSessionOperations.cs` facade (delete the file;
  `ISessionOperations` resolves to `WorkerSupervisor`). What remains:
  `OutputStore`, `WorkerSupervisor.CreateDefault(callTimeout,
  maxCallTimeout)`, `SupervisorLifecycle`, `SupervisorCallFilter`.
- `Sessions/WorkerSupervisor.cs`: the `auditStateText` callback,
  `CompleteInvokeFromSupervisor`/`ToAuditInvokeResult`, and the `ptk_state`
  `audit:` line.
- `Sessions/SessionRuntime.cs`: the optional `AuditCallContext? audit`
  parameters (production never passes non-null).
- `RunspaceHost.cs`: `IInvocationAuthorizer` and its barrier calls (the only
  production implementer is `AuditCallContext`), the audited overload, and
  `InvokeResult.AuditDetailCode`. Delete the seam; rewrite the tests that use
  `TestInvocationAuthorizer` (InvokeToolTests, RunspaceHostTests) to observe
  the same behavior another way, or delete them when they only exercised the
  seam.
- `OutputStore.ReadExactForAudit`: delete (used only by audit).
- Mixed tests to edit: `RuntimePackageBoundaryTests` (the destination
  script's presence, `AuditJournalFactory.cs`, the hosted-service count 5 →
  1), `OutputStoreTests`/`OutputRootLeaseTests` (storage rename),
  `WorkerServerTests` (`AuditCallContextAccessor`),
  `SessionOperationsSeamTests` (a using), `StdioChildStdinTests`
  (`PTK_AUDIT_ROOT` isolation), `RunspaceHostTests` (audited overload).
- `AuditCallFilter` also rejects unknown argument fields per tool
  (`AuditCallMetadata.cs`) — see R1. The script-size, timeout-range and
  `ptk_output` bounds are already enforced elsewhere
  (`WorkerOperationProtocol.cs`, `OutputTool.cs`).

**Proof scripts with audit gates**

- `server/test-handshake.ps1`: `PTK_AUDIT_ROOT` setup, the `ptk_state`
  assertion `^audit: healthy mode=local-only`, the "audit journaling ok"
  block, the whole fail-closed leg, and the hard-kill leg's audit root.
  **The installer copies this script into every payload
  (`scripts/install.ps1:1241`) and runs it to validate installs**, so its
  audit assertions must leave in the same change as the server's audit.
- `server/direct-product-proof.ps1`: the audit root and gate 14 (journaled
  admissions, attribution, producer identity; build identity is still
  checked through `ptk_state`).
- `server/test-production-acceptance.ps1` (`PTK_AUDIT_ROOT`, `AuditRoot`),
  `server/test-downloaded-product.ps1` (receiver archive, `verify-package`,
  operator-workflow proof, destination tool),
  `server/invoke-windows-standard-user-proof.ps1` (copies `siem/`),
  `server/test-build-identity.ps1` (builds SIEM twice; release.yml SIEM
  provenance).

**CI and release**

- `ci.yml`: the whole `siem` job.
- `release.yml`: `test-manage` and receiver publish, codesign of
  `out/ptk-siem`, receiver smoke, notarization staging, receiver archive,
  operator-workflow proof, and the artifact count (`-ne 10` → 5).
- `scripts/assemble-draft-release.sh` (`for product in ptk
  ptk-siem-receiver`; `SHA256SUMS` 11 → 6 entries) and
  `scripts/test-assemble-draft-release.sh` ("eleven"/11 → 6);
  `scripts/verify-release-assets.py` and its test (12 assets → 7);
  `docs/release-notes-template.md` (SIEM column); `verify-release.yml`
  (step name only).

**Installer**

- `scripts/install.ps1:1235`: stop copying `ptk-audit-destination.ps1`;
  the comment near `:1174`.
- Uninstall and `ptk_init.ps1` never mention the audit root or receiver.
  Existing `~/.ptk/audit` data on users' machines — see R2.

**Docs and records**

- `README.md` ("Audit status" and scattered mentions), `server/README.md`
  ("Audit status" plus a stale "audit disabled" line), `PRIVACY.md`,
  `CONTRIBUTING.md`, `docs/known-limitations.md`, `docs/harness-support.md`,
  `.github/ISSUE_TEMPLATE/bug_report.yml`, `feature_request.yml`,
  `.github/pull_request_template.md`, `.agents/release-notes-next.md`.
- `.agents/repo-guidance.md`: the SIEM and audit battery entries (`dotnet
  test siem/PtkSiem.slnx`, `siem/test-manage.ps1`, the operator-workflow
  proof, the audit-journaling gate in the direct-product proof).
- Wholly audit/SIEM plans: `audit-restoration.md`,
  `audit-restoration-r1-discovery.md`, `buffered-audit-startup.md`,
  `mini-siem-discovery.md`, `mini-siem-implementation.md`,
  `siem-operator-readiness.md`, `siem-sentinel-validation.md`. Partly audit:
  `audited-harness-sessions.md`, `production-reliability-salvage.md`.
- `docs/history/*` stays as history.

## Owner rulings needed

- **R1 — unknown tool arguments.** Today the audit filter refuses a call
  that carries an argument field the tool does not define. Removing the
  filter loses that check (whether the MCP SDK refuses them itself is not
  verified). Recommendation: keep the check by moving only the
  unknown-field rejection into `SupervisorCallFilter`, with a guard test;
  it is protocol hygiene, not audit. Alternative: drop it.
- **R2 — existing `~/.ptk/audit` data.** Recommendation: the installer and
  uninstaller do not touch it (`-Uninstall -Purge` already removes all of
  `~/.ptk`); the release notes and README say it is no longer used and may
  be deleted, and that it can contain exact past scripts. Alternative: the
  installer deletes it on upgrade.
- **R3 — order against rc.6.** rc.6 (`.agents/plans/release-0.3.0-rc.6.md`)
  is reviewed and waits on rulings D1–D4. Recommendation: publish rc.6 as
  reviewed if you still want it, then ship this removal in the following
  release; the removal does not touch the installer's rtk work. Alternative:
  fold the removal into the next release and drop rc.6.

## Slices

Each slice is one commit (or a short series), leaves `master` green, and is
pushed before the next starts.

1. **Remove the SIEM receiver product.** Delete `siem/**`; remove the CI
   `siem` job; remove every receiver leg from `release.yml` and the artifact
   count change; update `assemble-draft-release.sh`,
   `verify-release-assets.py` and their tests to the PTK-only asset set;
   drop the receiver parts of `test-downloaded-product.ps1`,
   `invoke-windows-standard-user-proof.ps1` and `test-build-identity.ps1`;
   drop the SIEM column from the release-notes template. The server still
   exports to a configured destination at this point; nothing in the server
   changes.
2. **Remove export, the web UI, alerts, attribution and the admin CLI.**
   Delete `Audit/Export/**`, `Audit/Web/**`, the alert webhook,
   `PtkAuditAdmin`, `scripts/ptk-audit-destination.ps1` (and its installer
   copy and `RuntimePackageBoundaryTests` assertion), `AUDIT-EXPORT.md`, the
   export status line, the attribution parsing, the export-only fixtures and
   tests. The local journal and its fail-closed gate stay, so
   `test-handshake.ps1`'s journal assertions still pass unchanged.
3. **Remove the local journal and its gate.** Delete the rest of
   `Audit/**` after moving `SecureAuditStorage` (above); delete the call
   sites in `Program.cs`, `WorkerSupervisor`, `SessionRuntime`,
   `RunspaceHost` and `OutputStore`; apply R1; delete and edit the tests
   listed above; and **in the same commit** remove the audit legs and
   `PTK_AUDIT_ROOT` from `test-handshake.ps1`, `direct-product-proof.ps1`
   and `test-production-acceptance.ps1`, since installs run the handshake.
   `ptk_state` no longer prints `audit:` lines.
4. **Docs.** README, `server/README.md`, `PRIVACY.md`, `CONTRIBUTING.md`,
   known limitations, harness support, the GitHub templates and
   `release-notes-next.md`: describe PTK without audit, plus R2's note on
   leftover `~/.ptk/audit` data.
5. **Records.** Drop the SIEM/audit entries from the verification battery
   in `.agents/repo-guidance.md` (and its "dead" notice, replaced by one
   line pointing at the decision); archive the superseded and closed
   decision entries verbatim to `docs/history/decisions-archive.md` per the
   decision lifecycle; mark the wholly audit/SIEM plans superseded at their
   heads (or move them to `docs/history/`) and note in the partly-audit
   plans which sections are dead; update `.agents/state.md` and this plan's
   status.

## Verification

Per slice, from a plain shell (not a ptk session — see
`.agents/machines.md` on `PSModulePath`):

- `dotnet build` and `dotnet test server/PtkMcpServer.slnx` green, with the
  test count reported and the drop explained by the deleted audit tests.
- `pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"`.
- `pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand -TimeoutSec 90`
  (slices 2 and 3).
- Slice 1: `bash scripts/test-assemble-draft-release.sh`,
  `python scripts/test-verify-release-assets.py`, and `actionlint` when
  available.
- Slice 3: `server/test-staged-install.ps1` against a fresh layout, a
  checkout source install into a throwaway home, and a search proving no
  `Audit`, `SIEM`, `PTK_AUDIT_ROOT` or `8317` reference survives outside
  `docs/history/` and the records that describe the removal.
- `dotnet list server/PtkMcpServer.slnx package --vulnerable --include-transitive`
  stays clean.
- After each push, the exact-source CI run on `master` is green on all
  remaining jobs.
- Removal itself needs no new regression guard; R1's moved check, if
  approved, gets one (refuses an unknown field, accepts a known one).

## Not in scope

- Any other redesign, cleanup or rename (including
  `RtkExecutableIdentity.AuditBinaryDigest` and the `*Audited*` method
  names that survive as plain names).
- Rewriting `docs/history/`.
- A release: publishing the result is a separate owner go (see R3).
