# Plan: Microsoft Sentinel real-SIEM validation and searchable PTK activity projection

## Status and authority

**DEFERRED — owner, 2026-08-31: Microsoft Sentinel will not be used in this environment;
the direction is Cribl Edge sweeping logs to Splunk (see `.agents/state.md`). The S0
recommendation is withdrawn — do not re-ask it. This plan is retained for reference in
case a Sentinel target returns.**

**DRAFT — planning authorized 2026-08-15; no Azure account inspection, resource creation,
product implementation, release, tag, or push is approved.** This plan supersedes S5R-S7 of
`.agents/plans/siem-operator-readiness.md` for future work. That plan remains the execution record
for completed S0-S5 backend, capture, custody, and mini-SIEM work.

The owner identified Microsoft Sentinel as the first real-SIEM candidate after direct use showed
that the mini-SIEM dashboard exposes storage internals rather than a searchable investigation
surface. The owner has an Azure subscription used for Azure Trusted Signing. That proves an Azure
tenant/subscription and an app-registration/RBAC path exist; it does **not** include Microsoft
Sentinel consumption or establish that the current identity may deploy or use Sentinel.

Decision D is narrowed but remains open: authorize read-only discovery of the configured Azure
subscription for Microsoft Sentinel feasibility. Provisioning is a later, separately costed go.

An owner-requested Claude Fable 5 openreview over the preceding corrective plan was attempted at
`8d1d39c..0a1206e`. Anthropic refused the request before repository access or capability proof, so
there is no Fable judgment to adopt. The canonical record is
`.agents/review/siem-operator-readiness-fable5-r3-refused.md`.

## Settled product constraints

- Every supported destination receives every possibly relevant fact or evidence artifact PTK
  captures. Sensitive evidence is access-controlled, not silently omitted.
- Agent/model attribution is recorded only when supplied, with source and strength. A
  `client_asserted` model name is searchable metadata, not authenticated model identity.
- Destinations are explicitly operator-selected: one by default, more only by deliberate opt-in.
- The mini-SIEM stays separately deployed. PTK never silently installs or selects it.
- Local durable audit remains the execution gate. SIEM delivery stays asynchronous and cannot stop
  PowerShell execution merely because a remote SIEM is unavailable.
- A protocol mock, HTTP 200, raw JSON page, or mini-SIEM API response is not real-SIEM acceptance.

## Current evidence and correction

Exact-head activity `01a005e1-d491-7717-82a0-a5aec0cc6d07` proved the backend retained a
125-byte submitted command, 33,358-byte caller response, and 166,000-byte captured output with
Codex/OpenAI/GPT-5 client-asserted attribution and repository context. It also exposed the product
shape failure:

- the activity endpoint recursively embeds a summary, repeated evidence descriptors, complete raw
  events, chunk manifests, hashes, and inline base64 payloads;
- the top-level projection reports 16 ms from `call.accepted` instead of the terminal event's
  1,218 ms;
- the command has a decoded detail preview, while complete output remains behind evidence storage;
  neither is exposed through a deliberate full-text investigation contract;
- the model name exists but its `client_asserted` trust boundary is easy to miss;
- `post_gap` is integrity state, not command failure, but the response does not make that boundary
  queryable without understanding the storage schema.

This is not merely a dashboard styling defect. Three data products were collapsed into one:

1. **Immutable event stream** — audit/evidence envelopes, chain hashes, custody, and replay state.
2. **Evidence store** — exact command, caller response, and captured-output artifacts/chunks.
3. **Investigation projection** — one compact, indexed activity record plus searchable evidence.

The first two remain forensic source material. The third is the missing SIEM contract.

## Microsoft Sentinel basis

Current Microsoft documentation establishes:

- an active Azure subscription plus sufficient RBAC is required; enabling Sentinel requires
  contributor permission on the subscription containing the workspace, and normal use requires a
  Microsoft Sentinel Reader or Contributor role on the resource group;
- Microsoft Sentinel is enabled on a Log Analytics workspace and is a separately paid service;
- an eligible newly enabled workspace receives up to 10 GB/day of Analytics-tier ingestion free
  for 31 days, but other Azure resources and capabilities may still charge;
- pay-as-you-go is the default after or beyond the trial unless a commitment tier is selected;
- Azure Monitor Logs Ingestion accepts JSON through a Data Collection Rule endpoint, requires
  Microsoft Entra authentication and RBAC on the DCR, and can target custom Log Analytics tables.

Sources, to be rechecked immediately before any Azure mutation:

- <https://learn.microsoft.com/en-us/azure/sentinel/quickstart-onboard>
- <https://learn.microsoft.com/en-us/azure/sentinel/billing>
- <https://learn.microsoft.com/en-us/azure/sentinel/roles>
- <https://learn.microsoft.com/en-us/azure/azure-monitor/logs/logs-ingestion-api-overview>

The Azure Trusted Signing app registration has only the Certificate Profile Signer role recorded
in this repository. Do not reuse or expand it for Sentinel. The pilot uses a distinct least-
privilege identity and secret/certificate boundary if provisioning is approved.

## Architecture under validation

PTK's generic destination contract remains vendor-neutral. Do not add Azure authentication or
Sentinel-specific payload shaping to the PowerShell execution path merely to complete this pilot.

The expected pilot path is:

```text
PTK audit/evidence export
        -> documented pilot adapter
        -> Azure Monitor Logs Ingestion API / DCR
        -> Log Analytics custom tables
        -> KQL activity function and saved investigations
        -> Microsoft Sentinel
```

This is a hypothesis, not an implementation decision. S2 must first prove whether an existing,
maintained collector can preserve PTK envelopes and acquire Microsoft Entra tokens. If not, the
smallest versioned adapter owns token acquisition, retry, and the exact mapping. It never receives
authority to invoke PTK or mutate receiver configuration.

## S0 — subscription feasibility discovery

**Gate:** separate owner go for read-only use of the already configured Azure CLI/account. No
resource provider registration, role assignment, workspace creation, Sentinel onboarding, secret
creation, or billing change belongs to this slice.

1. Confirm the active tenant, subscription offer/state, and current principal without recording
   subscription IDs, tenant IDs, or credentials in committed files.
2. Inspect whether `Microsoft.OperationalInsights`, `Microsoft.Insights`, and
   `Microsoft.SecurityInsights` are registered. An unregistered provider is reported, not changed.
3. Inspect current principal roles relevant to creating a dedicated resource group, Log Analytics
   workspace, DCR, custom tables, and enabling/using Sentinel.
4. List existing Log Analytics/Sentinel workspaces only to avoid collision and determine whether a
   new-workspace trial appears eligible. Do not inspect unrelated customer data.
5. Produce a cost envelope for one isolated pilot: region, expected PTK bytes, trial eligibility,
   pay-as-you-go rate after trial, retention, ancillary resources, cost alert, and teardown date.

Exit evidence is a redacted yes/no feasibility report with the exact missing permission or expected
maximum cost. It does not say the signing subscription “includes Sentinel.”

## S1 — isolated, cost-bounded Sentinel pilot

**Gate:** separate owner go naming the Azure subscription, maximum spend, lifetime, region, and
provision/teardown authority. Silence never authorizes billable resources.

1. Create a dedicated resource group and new Log Analytics workspace using pay-as-you-go, never a
   commitment tier. Confirm the portal's actual trial status before sending data.
2. Enable Microsoft Sentinel on only that workspace. Create a cost alert and the shortest retention
   compatible with the acceptance window; record services whose charges a workspace cap does not
   prevent.
3. Create distinct custom tables, DCR, and least-privilege pilot identity. Store credentials only in
   an approved secret boundary and never in PTK configuration output, process arguments, logs,
   repository files, or evidence.
4. Record every created resource ID in a protected, non-repository teardown manifest. Committed
   evidence uses redacted names and immutable query/result identifiers only.

Teardown is a separately explicit destructive go over the dedicated manifest. The final report
states accrued Azure cost before deletion.

## S2 — ingestion compatibility spike

1. Post a hand-built, non-sensitive PTK audit envelope and evidence envelope directly through the
   Logs Ingestion API using the documented client-credential flow. Prove DCR authentication, schema,
   size limits, retry classification, and KQL retrieval before changing PTK.
2. Test the largest current evidence chunk shape and representative Unicode, multiline PowerShell,
   null exit code, unavailable-reason, and `client_asserted` attribution fields. No test may rely on
   truncating or dropping unknown fields.
3. Evaluate an existing maintained collector/adapter against the complete contract. Reject it if it
   cannot preserve raw envelopes, exact payload text, chunk ordering, digests, and destination-
   specific durable acknowledgement.
4. If a custom adapter is necessary, amend this plan with its authentication, retry, local queue,
   packaging, upgrade, and secret-rotation boundaries before code work. The spike does not smuggle
   an undocumented proxy into release acceptance.

Exit evidence is one queryable audit record and one queryable evidence chunk in the real workspace,
with exact byte/digest comparison to the submitted non-sensitive fixtures.

## S3 — searchable Sentinel projection

Use append-only custom tables for source records and KQL functions for correlation. Do not copy the
mini-SIEM's recursive activity JSON into one dynamic column and call it integrated.

### Source tables

- `PTKAudit_CL`: one row per audit envelope with event/call/session IDs, event type/time, terminal
  state, duration, exit code and unavailable reason, tool/route, client/agent/model attribution,
  requested/effective/repository context, chain status, schema version, and the preserved raw
  envelope.
- `PTKEvidence_CL`: one row per evidence chunk with call/evidence/artifact IDs, kind, chunk index/
  count/offset, encoding, byte counts, artifact/chunk digests, retention/capture state, decoded
  `PayloadText`, and the preserved raw envelope.

Every projected field remains typed and independently searchable. Raw envelopes are retained for
forward compatibility and forensic reconstruction, not used as the ordinary investigation view.

### `PTKActivities()` KQL contract

The versioned function returns one row per PTK call with at least:

`TimeGenerated`, `ActivityId`, `State`, `ClientName`, `AgentName`, `ModelProvider`, `ModelName`,
`AttributionSource`, `AttributionStrength`, `Tool`, `Route`, `CommandText`, `Repository`,
`RepositoryRelativePath`, `ExitCode`, `ExitCodeUnavailableReason`, `DurationMs`, `ResponseBytes`,
`OutputBytes`, `IntegrityStatus`, and evidence completeness/count fields.

Rules:

- terminal values win over admission placeholders; the known 16 ms/1,218 ms error is a mandatory
  regression;
- null remains null and carries its unavailable reason; no success is inferred from `completed`;
- `post_gap` stays independently filterable integrity state and does not rewrite command outcome;
- attribution strength is never hidden by a friendly model label;
- decoded command and evidence text participate in full-text searches;
- duplicate/replayed envelopes collapse by stable IDs without discarding conflicting-digest alerts.

Saved KQL investigations cover command substring, output substring, agent/model/strength, client,
repository, state/exit code, duration, missing facts, incomplete evidence, chain gaps, and replay or
digest conflicts.

## S4 — real operator investigation pack

Create a versioned Sentinel workbook or equivalent saved-query pack only after S3 queries work. Its
primary result is a sortable activity grid, not generated prose:

`Time | State | Client | Agent | Model | Attribution | Command | Repository | Exit | Duration | Output`

Selecting a row opens decoded command, caller response, captured output, and evidence integrity.
Raw envelopes, hashes, chunk metadata, and custody events remain a secondary technical-evidence
view. Every visible column has a corresponding KQL field and filter; no UI-only summary is accepted.

## S5 — published-artifact real-SIEM acceptance

1. Start from a published PTK candidate in a fresh isolated home and configure only the documented
   Sentinel pilot destination path.
2. Run recognizable successful and failing PowerShell commands with unique markers in command text,
   output, and error output. Include one output large enough to span evidence chunks.
3. From Sentinel, find the activity independently by time, command marker, output marker, model,
   repository, state, and integrity status. Demonstrate that `client_asserted` remains visible.
4. Compare terminal duration, exit-code/null reason, command bytes, response bytes, output bytes,
   chunk ordering, and reconstructed artifact SHA-256 against PTK's retained evidence.
5. Stop the adapter/Sentinel path, run another command, and prove PTK execution continues while the
   destination backlog becomes visible. Restore it and prove replay closes only that destination's
   backlog without duplicates or digest conflicts.
6. Record exact PTK artifact SHA, adapter version, Azure service/API versions, KQL text, returned
   event/call IDs, evidence digests, trial status, and accrued cost.

Protocol mocks remain deterministic CI. This manual gate is the only basis for claiming Microsoft
Sentinel integration.

## Mini-SIEM disposition

The mini-SIEM remains useful as a separately deployed reference receiver, custody/evidence store,
deterministic test oracle, and small offline diagnostic. It is not called a substitute for Microsoft
Sentinel or operator-ready SIEM while its primary activity response exposes recursive storage
internals.

Before the Sentinel pilot, mini-SIEM work is limited to verified correctness defects that would
invalidate evidence or the external mapping, including terminal-duration projection. The withdrawn
settings/admin-plane proposal is not an implementation gate. Search UI, settings UI, and broad
dashboard redesign wait until the real-SIEM pilot supplies observed operator requirements.

## Verification and records

- Planning/review changes: `git diff --check`.
- Any implementation slice: focused tests with fail-before/pass-after mutation proof plus the full
  entry points in `.agents/repo-guidance.md`.
- Adapter/source-table tests preserve unknown fields, Unicode, chunking, null/unavailable semantics,
  replay, and digest conflicts.
- Azure account/resource facts and costs are environment evidence in `.agents/machines.md`, redacted
  to avoid identifiers and credentials. Stable status only lives in `.agents/state.md`.
- No implementation, Azure mutation, release, tag, or push follows from approval of this draft
  alone.

## Next owner decision

Authorize or decline **S0 read-only Azure feasibility discovery**. Recommendation: authorize S0.
It can establish permissions, trial eligibility, and expected spend without creating resources or
changing the subscription. The later provisioning question will be asked separately with the exact
cost envelope.
