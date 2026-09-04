# State Archive

Rotated verbatim from `.agents/state.md` by the `handoff` and `drift`
operators; current state lives there. Entries below are historical record,
newest rotation first.

## Rotated 2026-09-04 (drift based on c215515)

### From `## Now`

**FIXED: source-install version fallback was hardcoded to `0.2.0-dev` (known-broken,
2026-09-01).** `Get-PtkVersion` in `scripts/install.ps1` (no `-Version`, no `-FromRelease`)
stamped the literal `"0.2.0-dev.g$sha"` regardless of actual repo state, so a from-source
install of current HEAD (61 commits past the published `v0.3.0-rc.1` tag) reported an
apparently-older version than the latest release and read as installing stale code. Fixed to
derive the base from `git describe --tags --abbrev=0` (falls back to `0.0.0` with no reachable
tag, e.g. a shallow clone). Guard: `server/test-version-fallback.ps1` (wired into
`.github/workflows/ci.yml`), red before fix / green after; also confirmed live —
`-LayoutOnly` on this checkout now stamps `0.3.0-dev.ge987d6c` instead of `0.2.0-dev...`.

**ISSUE #30 WARM ON-PREM REMOTING ACCEPTANCE EXECUTED AND PASSED (owner go, 2026-08-31).**
The operator-gated acceptance ran against an owner-approved production-but-empty on-prem
Exchange 2019 endpoint (host detail in `.agents/machines.md` §`ASHBIAMWEB1`) through installed
PTK `0.3.0-rc.1`, Kerberos as the operator's own account, and a three-command read-only
allowlist (`Get-ExchangeServer`, `Get-MailboxDatabase`, `Get-ServerComponentState`). Ruling:
**#28 recommendation A holds** — the existing five-tool surface carried the whole workflow
(warm `PSSession` across calls, allowlist-only `Import-PSSession` proxies usable across calls,
first-class remote errors with no retry, worker reset reporting `warm_state_lost` with no
replay and an unaffected sibling session, and zero local or server-side sessions left after
cleanup). No new MCP schema is needed. Step 7 (induced disconnect) was not separately
authorized and did not run. One finding: `.agents/review/findings/i30-1.md` — verbose and
information records vanish from both shaped output and the recovery artifact on the installed
build (warnings/errors first-class; progress-drop is by design); reconcile against current
source before treating as a live defect. Redacted result posted to issue #30.

**MODULE AUTOLOADING IS BACK ON IN AGENT SESSIONS (owner go, 2026-08-31).** The
`PSModuleAutoloadingPreference = None` session default from `d2ca2f16` (2026-08-03,
rtk-router-delegation Slice 0) is removed; fresh sessions behave like stock PowerShell 7 for
module loading, and the `$PROFILE` exclusion stays. The owner ruled the off-default cost more
than it bought: undiscoverable, trivially flipped in-session by any call, and it taught agents
to bypass the warm runspace (observed live: an agent reimplemented DNS/TCP checks in .NET
rather than importing the modules). Canonical record:
`.agents/decisions.md` §"agent sessions keep module autoloading on". Two replacement guards in
`RunspaceHostTests` (`A_user_module_on_the_module_path_autoloads_on_first_use`,
`An_autoloaded_module_stays_warm_across_calls`) are sabotage-proved fail-under-None/
pass-restored; the explicit-import warm guard is retained; the release proof check flipped to
`leaves module autoloading on` (count unchanged). Note the fix reaches users through the
*installed* server — existing installs keep the old behavior until upgraded.

**ENVIRONMENT-SCOPED SIEM DEFERRAL ON `ASHBIAMWEB1`; PRODUCT DIRECTION UNCHANGED (owner,
2026-08-31, correcting `0468dd42`).** The owner deferred SIEM beyond local logging for
this implementation, on this server, at this company, on this day — an operational,
environment-scoped deferral only. This host's logs are swept to Splunk by Cribl Edge
(owner-reported); environment detail lives in `.agents/machines.md` §`ASHBIAMWEB1`. This
is NOT a product-direction change: Decision D, `.agents/plans/siem-sentinel-validation.md`,
and its S0 recommendation stand unchanged — S0 was declined "not now" this session, not
withdrawn. Commit `0468dd42` over-recorded this as a permanent environment ruling, marked
the Sentinel plan deferred, and withdrew S0; that recording was wrong and is reverted
here. **Next item:** unchanged — Decision D via the S0 gate remains the queued owner
decision, at the owner's timing.

**WINDOWS SERVER HANDOFF (2026-08-15, as of initial handoff `7a0c9d8`).** The Unix case-distinct environment repair is complete (`3805869` code, `724132d` installation proof). The owner ended Mac development and will continue on Windows Server. The owner reported PTK uninstalled on the Mac, but a handoff-time read-only check still found the installed payload and user-level registrations; machine-specific evidence is in `.agents/machines.md`. No further Mac cleanup or product change was attempted.

**UNIX WORKER STARTUP WITH CASE-DISTINCT ENVIRONMENT NAMES REPAIRED, INSTALLED, AND VERIFIED (2026-08-15, `3805869`).** PTK inherited both upper- and lower-case proxy names from Claude-launched clients; Unix permits those as distinct variables, but `WorkerLaunchCommand` and `UnixWorkerProcessLauncher` copied them through case-insensitive dictionaries. The installed `0.3.0-rc.1` control started with one casing and failed with both, first as `worker_factory_failed` and then, after the first repair, as `worker_launch_failed`. Command construction now follows platform name semantics (case-insensitive on Windows, case-sensitive on Unix), and the Unix broker launch preserves both names. Platform-semantics and real production-broker guards are mutation-proven. Checkout and installed-package registration handshakes with duplicate-case HTTP/HTTPS proxy names passed; the full server suite passed 1,354/1,354; all five server projects reported no vulnerable direct or transitive packages; diff hygiene passed. Source install `0.2.0-dev.g3805869` was activated at `/Users/michael/.ptk/bin/PtkMcpServer` and registered user-wide for Claude, Codex, Grok, agy, and Kimi. A fresh Claude health check under duplicate-case proxies reported `Connected`; Codex reported the installed stdio registration enabled. During diagnosis, invoking the checkout handshake from inside a PTK worker terminated the other live PTK client servers; those already-open MCP connections remained `Transport closed` and required reconnection, but fresh sessions connected and no data or repository files were removed. Publication and current Mac-install status are superseded by the Windows Server handoff entry above.

**MICROSOFT SENTINEL REAL-SIEM VALIDATION PLANNED; NO AZURE ACCESS OR RESOURCES AUTHORIZED
(2026-08-15).** Direct use showed the mini-SIEM activity response collapses the immutable event
stream, evidence artifacts, and investigation projection into recursive JSON/base64 and even
projects admission duration (16 ms) instead of terminal duration (1,218 ms). The new canonical
future-work plan is `.agents/plans/siem-sentinel-validation.md`: preserve forensic source data,
create a typed searchable Sentinel/KQL activity projection, validate decoded command/output and
client-asserted model attribution in a real product, and defer mini-SIEM settings/dashboard growth
until observed use. Microsoft documents that an Azure subscription can host Sentinel with adequate
RBAC, but Trusted Signing grants no included Sentinel consumption; Sentinel/Log Analytics is
separately billed, with a conditional new-workspace 10-GB/day, 31-day trial. Decision D is narrowed
to one next gate: S0 read-only Azure feasibility discovery. No Azure account inspection, resource
creation, implementation, release, tag, or push is authorized. Owner-requested Claude Fable 5
openreview over `8d1d39c..0a1206e` was refused before repository access or output; canonical record
is `.agents/review/siem-operator-readiness-fable5-r3-refused.md`, and no Fable judgment is claimed.

**SIEM INVOKE COMPLETION EVIDENCE FIXED AND LIVE-PROVED (2026-08-15, `dfcda26`; producer boundary `b89ef46`).** `b89ef46` carries actual dispatch cwd, execution-start state, and available output recovery across the contained-worker/supervisor boundary; the request-scoped audit capability now travels through a scoped operations facade instead of becoming MCP input. Terminal `ptk.audit/6` records derive repository root/relative path and publish `submitted_command`, `caller_response`, and `captured_output`. A live exact-head capture then exposed and `dfcda26` fixed the mini-SIEM projection combining terminal values with earlier `not_dispatched` reasons. Full server 1,353/1,353, SIEM 357/357, Pester 112 passed/3 platform-skipped; server and SIEM dependency scans list no vulnerable packages. Regression mutations fail when output recovery/cwd mapping or projection reconciliation is removed. Exact-head `osx-arm64` activity `01a005e1-d491-7717-82a0-a5aec0cc6d07` identifies `codex` / `openai` / `gpt-5`, reports requested/effective/repository cwd with null unavailable reasons, and exposes byte/digest-verified 125-byte command, 33,358-byte caller response, and 166,000-byte captured output. Producer delivery is healthy with zero pending/refused/missing records; receiver evidence is complete. Protected token-free report and live-process details are in `.agents/machines.md`. No release was performed.

**MINI-SIEM PROSPECTIVE-DESTINATION DEADLOCK FIXED AND LIVE-PROVED (2026-08-15, `e0aea02`).** A current-source isolated capture exposed a real S3/S4 defect: when a receiver first saw sequence 2 after a live prospective destination add, it quarantined that valid gap opener but continued the same OTLP batch, allowing sequence 3 to overtake it. Producer isolation could then never replay sequence 2 and remained stuck at `export.evidence_refused`. `e0aea02` stops a JSON ingest batch after a permanent refusal of a validated record, while still allowing independent invalid poison records to be quarantined without stopping later records. The regression failed before the fix and passed after it; focused gap/JSON ingest passed 19/19 and the full SIEM suite passed 357/357. Exact-commit `osx-arm64` live proof delivered one fully attributable activity with both `call.accepted` and `call.completed`, command/response evidence, zero pending/refused records, and healthy producer delivery. The receiver intentionally retains one visible prospective-prefix gap and its opening quarantine attempt instead of deadlocking. Host evidence is in `.agents/machines.md`. The two completion-evidence defects found by this proof are repaired by b89ef46 and dfcda26; the entry above is current.

**S5 FINAL CLOSURE VERIFIED (2026-08-15, `ebfbd4b`).** Exact-head CI run
`31865095280` passed all six Linux, macOS, and Windows product/SIEM jobs. Rebuilt
`osx-arm64` `0.3.0-s5` packages passed release-bound source verification as
`0.3.0-s5+ebfbd4b` and the packaged external-only, explicit multi-destination
failure/recovery, pinned-HTTPS rejection, and mini-SIEM Doctor query-back workflows.
The final hashes and Windows repair evidence are in `.agents/machines.md`; they
supersede the implementation-head package hashes recorded below. No release or tag
was created. S6-S7 remain unapproved and Decision D remains open.

**OPERATOR-READINESS S5 EXECUTED; S6-S7 NOT APPROVED (2026-08-14).** The signed five-RID
`0.3.0-rc.1` prerelease predates S2-S5 and is not operator-ready.
S0 is executed: `siem/operator-readiness-acceptance.ps1` verifies published
artifact identity in a fresh isolated home and names the release gate;
`siem/test-verify-package.ps1` guards release-bound source identity. Authentic
`0.3.0-rc.1` evidence passed eight artifact/provenance requirements and failed
23 operator-readiness requirements. S3 is executed in current source: one protected versioned destination set,
prospective per-record obligations, independent delivery cursors/status, explicit bounded backfill and abandonment,
conservative retention, and a producer status-only UI. S4 is executed at `a022fa3`: the separately deployed mini-SIEM exposes one attributable activity per PTK call, exact command/response/output drill-down, stable filters/pagination, task/run and execution context, raw events, human health, alert/gap actions, and protected quarantine evidence. S5 is executed at `a8cf759`: PTK remains installer-separated from the mini-SIEM; packaged operator commands explicitly deploy, select, validate, query back, upgrade, and remove it, or select an external SIEM without installing it. One destination is explicit; additional destinations require sensitive-duplication confirmation and keep independent delivery state. Pinned TLS covers preflight, delivery, manager status, and Doctor without trust-store mutation. The deterministic external sink proves adapter workflow only, not a real external-SIEM product. S6-S7 remain unapproved; Decision D remains
open. Canonical plan: `.agents/plans/siem-operator-readiness.md`.
Current admissions use `ptk.audit/6` and destination-bound `ptk.evidence/2`;
historical v1-v5 core and `ptk.evidence/1` readers remain intact.

S2 adds full-fidelity forensic export without changing destination policy: every configured
destination receives `ptk.audit/5` core plus exact command, caller-response, and captured-output
evidence in replay-stable `ptk.evidence/1` envelopes. The producer advances only after the whole
logical unit is acknowledged. Receiver schema v11 persists and correlates either arrival order,
survives replay/restart, reports completeness, and provides token-protected exact retrieval with
retention, custody, backup, and restore coverage. OTLP and Splunk protocol conformance are proven;
Decision D still prevents calling this real-external-SIEM acceptance. Verification and mutation
evidence is in `.agents/machines.md`.

Two owner-authorized Claude Fable 5 openreview attempts produced no verdict and
were not retried under the owner's expensive-review rule; the canonical record
is `.agents/review/siem-operator-readiness-fable5-r2-refused.md`. A later
owner-requested Kimi review over the pre-decision plan pins returned
`best_approach` with no findings; its scope and record are
`.agents/review/siem-operator-readiness-kimi-r1.md`. It did not approve
implementation and does not cover the later Decision A-C amendments.

The owner has settled Decisions A-C. Every configured destination must receive
every possibly relevant fact/evidence artifact PTK captures, including exact
commands and complete captured output/error evidence. Supported clients supply per-call
agent/model identity when technically possible; PTK records provenance and
explicit absence, never guesses. PTK setup defaults to one operator-chosen
destination and never forces another. Operators may explicitly opt into
multiple destinations; every configured destination receives the full stream
and has independent delivery/backlog/error accounting. PTK never automatically
installs or selects the mini-SIEM and never silently duplicates evidence. The
mandatory local fail-closed journal is only the disclosed
admission/replay/delivery source, not a SIEM destination or investigation
dashboard. Decision D—the first real external-SIEM acceptance target—is the
only remaining owner gate. The owner has no access to Splunk or another SIEM
test instance; Decision D must settle both a product and an authorized,
reproducible access path without assuming owner-provided infrastructure. This
constraint does not choose a replacement or waive real-product validation. Only
S0-S5 are implemented; S6-S7 are not approved.

A live disposable published-artifact proof remains under
`~/.ptk-siem-live-proof` on loopback ports 19418/19443 (plus TLS-validating
forwarder 19466) for owner inspection; it did not alter or restart the installed
PTK. Host evidence is in `.agents/machines.md`.
S1 added strict per-call attribution and execution context through namespaced
MCP `_meta` `io.github.also-beltrix.ptk/call-context/v1`, with every supplied
value labeled `client_asserted` and omitted identity labeled
`not_supplied_by_client`. Effective working directory is captured at dispatch;
repository root/relative path is bounded and derived from `.git` markers only.
Audit schema `ptk.audit/4`, strict spool recovery, OTLP/Splunk export, and the
standalone receiver support the fields. Historical v1/v2 bytes/readers remain
unchanged; v3 remains the distinct host-state contract. Supported registrations
currently cannot inject selected agent/model/task metadata, so absence is
reported explicitly instead of guessed. Full verification and mutation evidence
is in `.agents/machines.md`; operator contract is in `server/AUDIT-EXPORT.md` and
the capability gap is in `docs/harness-support.md`.

**Next item:** settle Decision D with an authorized, reproducible real external-SIEM
product/access path before S6 implementation can be approved. No S6 implementation is
authorized yet.

**Historical S1-S8 backend evidence follows; it is not an operator-readiness
claim.**
S7 replaced the receiver's implementation-oriented README with a standalone
operator guide: per-OS dedicated-account layouts and exact path protection,
complete configuration, asynchronous producer export/cursor semantics,
operator routes, retention/capacity, witness/manual attestation, SQLite online
backup, witness-aware restore reconciliation, forward-only schema migration,
threat-model acceptance rows 1/6/7/8/10/11, network/dependency inventory, and
patch cadence. The two-host-equivalent manual proof used separately published
receiver/producer roots under macOS sandboxes with mutual cross-root read/write
denial and no permanent trust-store mutation. A real `ptk_invoke` advanced the
accepted producer cursor from sequence 1/offset 2506 to sequence 3/offset 8148;
receiver dashboard returned 200, API event detail returned the exact
`call.completed` event `019ff7fe-235c-77ee-b5a2-157b0f1027b5`, custody stayed
healthy with an anchor, and all disposable roots were removed. Exact hashes and
host evidence live in `.agents/machines.md`. The sample JSON parses, `git diff
--check` is clean, the full SIEM suite passes 329/329, and all three SIEM
projects report no vulnerable direct or transitive packages.

S8 now has one package builder and one independent verifier used by local,
ordinary CI, and release paths. The layout carries coherent requested-version
plus source-commit assembly identity, `VERSION`, the operator guide, Apache-2.0
project license, and vendored OpenTelemetry proto license; it fails closed if
any is absent or stale and proves no-config startup exits 1 naming
`PTK_SIEM_CONFIG`. Exact implementation head `a9576c2` produced and re-read a
local osx-arm64 archive with test-only version `0.2.3-rc.1+a9576c2`; removing
version stamping and removing the guide independently failed their intended
guards. Exact artifact hash and host evidence are in `.agents/machines.md`.
`actionlint`,
PSScriptAnalyzer, and `git diff --check` pass. Standing verification is green:
Pester 112 passed/3 skipped, server 1,306/1,306, SIEM 329/329, registered
handshake, and server/SIEM dependency audits. Hosted run `31650818998` at
`b94bae5` passed all six Ubuntu/Windows/macOS jobs, including the native SIEM
package builder/verifier on each OS. The owner selected `0.3.0-rc.1`; first
draft attempt `31654128120` at `0c05a81` reached all five native runners but
the receiver publish on `ubuntu-24.04-arm` reproduced upstream's known
`Grpc.Tools` 2.69.0-2.82.0 Linux ARM64 regression: bundled `protoc` exited 139.
The package pin is now 2.83.0, the upstream-fixed release. Local verification
passes 329/329, the dependency audit is clean, and an osx-arm64
`0.3.0-rc.1` package builds and independently verifies. Repair commit
`0c8ed87` is pushed; exact-head run `31654609624` passed all five native RID
jobs and draft assembly, including Windows Trusted Signing and macOS Developer
ID signing/notarization before package gates. Owner authorization ruled
Decision 5 and release `v0.3.0-rc.1` was published as a GitHub prerelease at
2026-08-13T01:38:18Z. Its tag points exactly to full source SHA
`0c8ed87635ef37db548d086ada78a2020c4b390f`; its ten expected archives plus
`SHA256SUMS` remain uploaded, and every manifest entry matches GitHub's asset
digest. The workflow passes `$GITHUB_SHA` as the release target so later branch
movement cannot change the artifact/tag provenance.

**First `0.3.0-rc.1` installation feedback found two upgrade defects.** A real
upgrade over July audit data first failed closed as `evidence.storage`: twelve
retained artifacts used the legacy `GUID.script` name, while current inventory
requires `GUID.sha256.script`. Owner-approved transactional recovery renamed
those exact one-link, owner-only files from their computed SHA-256 without
changing their bytes. Startup then reached `evidence.reconciliation` and
exposed a separate compatibility bug: every authentic `ptk.audit/1` record
predates `producer.previous_supervisor_boot_id`, but the scanner used the V2
producer shape for both versions. The scanner now has exact V1/V2 producer
property sets; the V1 test converter removes the post-V1 field, and both
producer-owned V1 SIEM wire goldens were deliberately regenerated while V2
goldens remained byte-identical. The focused scanner scope passes 5/5, removing
the repair fails the original-V1 guard, the full server suite passes 1,306/1,306,
both staged and installed package handshakes pass, and the live MCP reports a
healthy local-only audit under version `0.3.0-rc.1.local-reconciliation`.
The product now also migrates the observed legacy names during evidence-store
construction: only canonical UUIDv4 `GUID.script` direct children are admitted,
their retained protected bytes determine the SHA-256 destination, migration runs
under the cross-process evidence quota lock, and any same-ID artifact makes the
whole preflight fail closed before a rename. The focused store scope passes
23/23, removing migration activation fails its exact guard, and the full server
suite passes 1,308/1,308.

**The unversioned release installer now includes prereleases (2026-08-13).**
GitHub's `/releases/latest` endpoint selected stable `v0.2.2` even though the
newest published release was `v0.3.0-rc.1`. Unversioned `-FromRelease` now
enumerates the first 100 releases, excludes drafts and unpublished entries, and
selects the unique greatest `published_at`; explicit `-Version` remains
tag-exact, and malformed or tied responses fail closed. The deterministic
selector test passes; live GitHub selection returned `v0.3.0-rc.1`; a real
osx-arm64 archive was downloaded, checksum-verified, and read back with layout
`VERSION` `0.3.0-rc.1` without installing. Runtime package-boundary tests pass
7/7, Pester passes 112 with 3 platform skips, `actionlint` and `git diff
--check` pass, and PSScriptAnalyzer is unavailable on this host. Three
independent mutations prove the guard catches return to `/releases/latest`,
draft inclusion, and prerelease exclusion. **Next item:** codereview the landed
`0.3.0-rc.1` upgrade-compatibility range with Claude Opus 5 xhigh.

**cr14 upgrade-compatibility review CLOSED (2026-08-13).** Claude Code
2.1.229 / claude-opus-5 / xhigh returned one valid CRITICAL finding over
`b7853d7..ed2e406`: published v0.2.x wrote `ptk.audit/2` before
`producer.previous_supervisor_boot_id` existed, but the first compatibility
repair permits the old producer set only under `ptk.audit/1`. Tag and shared
startup-code inspection independently confirm cr14-1. Its repair permits only
the two exact historical/current v2 producer sets; v1 and every other object
remain exact. The genuine pre-lineage-v2 guard fails with the original
incomplete-object exception when the compatibility arm is removed and passes
restored; unknown v2 producer fields remain rejected. Focused scanner tests
pass 7/7. A PTK-inherited `PSModulePath` first caused only the four
repo-recorded `StateToolTests` failures (1,306 others passed); removing that
environment contamination made those four pass 4/4 and the full server suite
pass 1,310/1,310. Final round 2 routed frontier under T2 and independently
reproduced fail-under-sabotage/pass-restored in a disposable worktree; verdict
`accepted`, `guard_confirmed=true`, `capability_ok=true`, exact SHA pins. The
canonical-tree process slip was independently audited clean; cr14 closed at
its two-round cap. `.agents/review/index.md` owns the record. **Next item:**
select the next major queued section from current repository and GitHub state.

**cr15 S8 packaging review CLOSED (2026-08-13).** Claude Code 2.1.229 /
claude-opus-5 / xhigh reviewed the unreviewed S8 range
`22ca2ab..0c8ed87`. Its otherwise concrete three-candidate payload was
formally invalid because candidate 2 omitted required severity; the one allowed
same-session schema recovery failed before inference and the review was not
rerun. Independent intake admitted cr15-1: failed test-host startup omits the
SQLite pool clear that normal disposal requires on Windows, so cleanup can mask
the real exception. The PDB claim and ARM64-per-PR-CI claim were declined with
evidence in `.agents/review/cr15-declined.contested.md`. cr15-1 now routes both
normal and failed disposal through one pool-clear-before-delete primitive. Its
real-store injected-start-failure guard fails under exact clear removal and
passes restored. Full SIEM passes 330/330; changed-file formatting and diff
hygiene pass. Final round 2 independently reproduced the exact sabotage
failure, restored clean bytes, passed focused 1/1 and SIEM 330/330; verdict
`accepted`, `guard_confirmed=true`, `capability_ok=true`, exact SHA pins.
Hosted CI `31675780336` passed all six jobs, including native Windows SIEM.
cr15 closed at its two-round cap; `.agents/review/index.md` owns the record.

**cr16 release-signing review CLOSED at its two-round cap (2026-08-13).** Claude Code 2.1.229 /
claude-opus-5 / xhigh returned four valid candidates over the path-bounded
signing/notarization range `05b5df7..648d264`; exact SHA pins and capability
proof pass. Independent intake admitted three: cr16-1's one-shot Apple status
re-read false-fails healthy `In Progress`, cr16-3's zero-identity output parses
as signing identity `valid`, and cr16-4's unqualified signed-binary docs include
unsigned Linux assets. cr16-2's timeout claim was declined: exact hosted macOS
jobs reach notarization in about one minute, leaving ~44 minutes around the
30-minute notary allowance. cr16-1 is repaired with a durable submission-id
helper that resumes `notarytool wait` within one bounded allowance instead of
failing a healthy `In Progress`; local fake-Apple tests cover transport-drop
recovery, rejection, and missing/malformed ids without a real submission. The
load-bearing `In Progress` arm is mutation-proved fail-before/pass-restored;
Bash syntax, ShellCheck, `actionlint`, and diff hygiene pass.
cr16-3 now selects a Developer ID Application identity by content and admits
only its 40-hex fingerprint before any `codesign` call; its static selector
guards zero identities, unrelated-first ordering, valid Developer ID, and
malformed/short fingerprints. Separate validation and identity-type mutations
fail the intended zero/unrelated guards; restored suite, Bash syntax,
ShellCheck, `actionlint`, and diff hygiene pass. cr16-4 now qualifies the public
release contract by platform: Windows Authenticode, macOS Developer ID plus
Apple notarization, and Linux `SHA256SUMS` integrity without publisher code
signing. A cross-platform static documentation guard rejects the former
unqualified claims and is mutation-proved fail-before/pass-restored. Its first
hosted run exposed CRLF-only Windows false failure; `bfa2cd0` normalizes input
and its explicit CRLF-copy guard fails without the repair. Hosted run
`31687784932` passed all six jobs. Local Pester 112/112 plus 3 skips, server
1,310/1,310 under clean `PSModulePath`, and SIEM 330/330 pass. The second/final
Claude call timed out at 3,600 seconds without any verdict and was not rerun by
owner rule. `.agents/review/cr16-final-failed.md` owns that failure record;
`.agents/review/index.md` owns closure status. Do not claim final reviewer
acceptance.

First cr16 repair CI run `31682414857` passed five of six jobs but the Windows
test leg stopped at the new signing-doc guard: checkout CRLF made its exact-line
`$` assertions falsely miss the present Windows Authenticode contract. Linux
and macOS test legs, all three SIEM legs, and the two macOS shell guards passed.
The docs guard now normalizes line endings and accepts an explicit README path
so a local CRLF-copy proof covers the Windows condition. Hosted re-proof
`31687784932` passed every job.

First exact-head hosted run `31649960173` proved Ubuntu and macOS native SIEM
packages, but Windows stopped before its package gate on test-host cleanup:
after the application disposed, Microsoft.Data.Sqlite's idle pool still held
`siem.db`, so recursive fixture cleanup failed on Windows (POSIX had hidden the
leak by allowing unlink of an open file). The test host now clears idle pools
after application disposal; checked-out connections remain valid. The same run
also exposed separately repaired protected-file fixture creation in six Windows
server tests. These are CI/test-lifetime defects, not shipped receiver behavior.
The export-settings and retention-floor helpers now apply the same
`SecureAuditStorage.ProtectExistingFile` boundary their assertions require,
including the malformed-config fixture; focused server scope passes 48/48.

**S4b COMPLETE (2026-08-12).**
The custody/retention and independent-witness/restore sections are closed at
their two-round review caps (cr10/cr11). The final durability-barrier section
now runs the real standalone receiver in a separately contained process and
kills its whole process tree: a deterministic pre-commit kill returns no valid
nonrejecting ack and leaves no event, chain, or custody rows after restart; an
immediate post-ack kill preserves the exact request/body, producer-chain head,
healthy witnessed custody head, and idempotent replay. A separate test-only
host supplies the narrow pre-commit hold and deterministic ack-before-commit
double without adding an activation path to the shipped receiver entry point.
The latter produces a valid ack but no event after restart, while an injected
`synchronous=OFF` writer is refused with `storage_policy`. Independent
sabotage moved the hold after `Commit()` (pre-commit guard failed with one
durable event), substituted the early-ack double into the positive post-ack
proof (event detail became 404), and removed the FULL comparison (OFF mutant
escaped with no exception). Restored focused guards pass 4/4 in Debug and
Release; the full SIEM suite passes 329/329. `dotnet format` is clean for every
changed C# file except the already-recorded pre-existing indentation block in
`SqliteIngestStore.cs` (now shifted to lines 1313-1420). cr12 closed on its
first valid generation round: Claude Code 2.1.228 / `claude-opus-5` / `xhigh`
returned `clean`, capability proof true, with exact pins
`1be29fc..9b14a7e`; no findings. The codereview playbook requires stopping on
a valid clean result, so the two-round cap was not consumed further. Standing
battery after the slice: server 1,306/1,306 (with the documented PTK-session
`PSModulePath` correction), Pester 112 passed + 3 platform skips, and all five
server projects have no vulnerable packages.

**cr11 CLOSED at its two-round cap:** the S4b independent-witness/restore major section
landed at `87818e4` over base `9c6f89c`; see `.agents/review/index.md`. After
two foreground transport timeouts returned no verdict, owner-authorized
background Claude Code 2.1.228 / claude-opus-5 / xhigh generation round 1
returned five candidates with valid pins and capability proof. Intake is in
progress; cr11-1's routed-path custody-gate bypass and cr11-2's periodic-scan
writer serialization are admitted, independently reproduced, and locally
repaired. cr11-3's unbounded-file-count candidate is declined as an unmeasured
capacity concern whose proposed repair would weaken approved immutable-history
semantics. cr11-4's nullable background mutation gates are admitted and both
alert-evaluation and retention pause guards independently mutation-proved.
cr11-5's macOS case-alias candidate is declined after the runtime's
case-insensitive relative-path behavior falsified its trigger. Final round 2
accepted cr11-1, cr11-2, and cr11-4 after independently reproducing every
named sabotage; restored full SIEM passed 325/325. No barrier-section code was
included in this review scope. Next S4b section is the whole-process pre-
commit/post-ack barriers and independent discriminators.

**cr10 CLOSED at its two-round cap:** the final Claude round accepted cr10-1
and reopened cr10-2; `.agents/review/index.md` owns the verdict record. The
remaining failure was repaired without a prohibited third review: age-selected
typed deletes now run in 128-parameter batches, so they cannot exceed SQLite's
host-parameter ceiling. Its 130-subject guard failed under exact repair
reversion and passed restored; SIEM suite passed 316/316.

**cr10-1 fixed, guard proved biting (2026-08-12):** subject verification now
exempts only ledger-v1 receipts whose exact evidence is unavailable, allowing
them to reach the existing explicit `LegacyUnverifiedReceipts` count. Every
v2 receipt and every v1 receipt backfilled from a live event/quarantine source
still takes full verification. The guard rebuilds a faithful v7 schema with an
orphaned event plus gap/alert lifecycle receipts; removing the exemption
reproduces `custody_integrity_subject`, restoring it passes.

**cr10-2 fixed through locally proved post-cap repair (2026-08-12):** startup preloads event,
quarantine, latest-lifecycle, alert, gap, and schema-v10 restore state in six set reads rather
than querying per receipt; schema v9 adds the custody subject covering index.
Retention uses one typed parameterized delete per selected set, with indexed
event/quarantine lookups. A 64-receipt guard fails if the snapshot is loaded
inside the receipt loop; a 23-subject guard fails if deletion returns to one
statement per subject; query-plan guards pin both primary and covering index
use. Both original guards were independently confirmed biting. The final
review found unbounded age-selected sets could exceed SQLite's host-parameter
ceiling; typed deletion now chunks at 128 parameters. The new 130-subject guard
failed on repair reversion and passed restored. No further review round is
authorized for this major section.

**OWNER CORRECTION (2026-08-10): SIEM output is a P0 requirement; its
removal was never consciously owner-approved.** The owner's words: "I
never consciously removed SIEM output. that's a p0 requirement." The
2026-07-27 delegated settlement of salvage decision 4
(`.agents/decisions.md:624`, executed at `ddbb908`) removed the anchored
OTLP exporter under a "stop asking low-level technical questions"
delegation — a product-scope call misclassified as an engineering
choice. That settlement is countermanded as authority: "never restore
the producer" notes elsewhere in this file and in the decisions log are
void as prohibitions, though the engineering lessons stand (the old
design gated execution on audit health — an availability conflict — and
its Grpc.Tools protobuf path broke ARM64 Linux builds; the salvage plan
itself directed any future exporter be an explicit opt-in mode or
sidecar with its own availability contract). The full producer exists in
history at `ddbb908^` (exporter, spool pump, mapper, conformance suite,
fake receiver); the `siem/` receiver and its 247-test suite were never
touched. **The owner ruled the shape the same day:** "this was part of the design
from step 0, and it was all supposed to be built with auditing
integrated at a base level and non-bypassable. that's what it needs to
be." Plan of record for the restoration:
`.agents/plans/audit-restoration.md` (DRAFT). The anchor question is
settled by owner rulings recorded in the plan: local durable logging is
the only execution gate; SIEM export is asynchronous and never gates.
An openreview pass (oar1, `.agents/review/index.md`) returned `replace`;
all rulings resolved same day (journal-backed local UI; one
endpoint+token exporter contract for Splunk/Sentinel/OTLP/receiver
alike, no pairing machinery; receiver retention fixed before shipping).
**R0 approved and R1 discovery executed 2026-08-10**
(`.agents/plans/audit-restoration-r1-discovery.md`): the deleted design
already implemented local-always/export-additive — restoration is a
re-seating at the current single call-filter anchor, supervisor-side;
encoding settled (vendor generated protobuf C#, drop Grpc.Tools from
both projects — kills the ARM64 protoc blocker); S4 regated to v1/v2
corpora; receiver gets a token auth mode (no pairing); receiver
retention (rbc-11) is a blanket-covered prerequisite.

**R2 EXECUTED 2026-08-10 (owner go; core `ed1582a`, quarantine follow-on
commit): PTK journals again.** The four deleted gate files are restored
against the retained audit library; admission is the outermost call
filter (o53-3 supervisor filter inner); the gate is the first hosted
service. Contract behaviors, all guard-proved: healthy root → invoke
served AND journaled (handshake: 14 artifacts from a live session);
unwritable root → transport up, every invoke refused fail-closed with
`[operation not started]`, `ptk_state` serves the emergency diagnosis,
admission retries so a repaired root heals without restart; corrupt
host identity → quarantined under `<root>/quarantine/` with original
bytes preserved, fresh identity minted, service continues
(fail-before/pass-after proven by stash-revert). `ptk_state` reports
real audit health (`audit: healthy mode=local-only`); the worker
runtime no longer claims `audit: disabled`. Handshake audit legs
flipped and PASSED. Known R2 residuals, deliberate: audit refusals
carry text+isError but no o53-3 structured verdict (they originate in
the outer filter — align in R3); quarantine-and-continue covers the
host-identity artifact class, the historic incident; corrupt journal
*segment* handling still fail-closes and extends under the same
pattern when R3 touches the sink; the release-gate product proof gains
its positive journaling check in R6. **The StateToolTests quartet
fails from a ptk session on this host (recorded PSModulePath quirk)
and passes 15/15 from a plain shell — not a product failure.**

**R2 was codereviewed and is CLOSED clean (cr2, 2026-08-10):** codex
found four defects over the whole R2 range, all admitted, fixed one
commit each, then batch-verified `accepted` with every guard
independently re-proved — cr2-1 HIGH (Windows *repaired* rather than
validated a retained `host.id`, silently adopting a foreign or
over-permissive identity instead of quarantining it), cr2-2 HIGH (every
call journaled `session.name=default`, misattributing named-session
activity), cr2-3 MEDIUM (audit admission rejected `ptk_output` requests
carrying the schema's own defaults), cr2-4 MEDIUM (quarantines were
stderr-only, absent from the journal — the plan's rule-3 reporting
surface (a)). `.agents/review/index.md` §cr2 owns the record. Battery at
`98083cc`: server 1,220/1,220 from a plain shell, Pester 112 + 3
skipped.

**R3 EXECUTED 2026-08-10 (owner go), in two commits — PTK now exports to
a real SIEM.** R3a (`836cbb7`): one export contract with thin
destination adapters (endpoint + credential; Splunk HEC, any OTLP/HTTP
collector, and the PTK receiver configured identically), a background
service draining the JSONL spool at least once behind a durable cursor,
health in `ptk_state`. Two design calls, both recorded: the anchored
ack-gating machinery deleted at `ddbb908` is deliberately NOT restored
(it gated execution on delivery acknowledgment, which contract rule 2
forbids), and **OTLP/HTTP JSON replaces protobuf, amending R1's
vendor-the-generated-code decision** — no protoc/Grpc.Tools path at all
on the producer, which kills the recorded ARM64 Linux build blocker
while staying a standard OTLP encoding. Plaintext HTTP is accepted only
for loopback; credentials never reach journal, logs, or `ptk_state`.
The load-bearing guard is end-to-end: with the SIEM endpoint a closed
port, invokes still execute, health shows the outage, records stay
journaled. R3b (`af8a229`): rbc-11 CLOSED — receiver retention is
enforced by a background sweep that never touches custody receipts or
chain heads; the README's "do not deploy" warning is replaced by the
retention contract. Battery: server 1,236/1,236, SIEM 252/252,
handshake PASSED.

**R3 was codereviewed (cr3): five findings, four CLOSED, one in its
fifth verification round — as of `b23499b`.** codex found five defects
over `1a7a71f..98a3625`. Closed: cr3-5 (transient 408 skipped whole
batches; now retried, and a refused batch is isolated record-by-record),
cr3-3 and cr3-4 (receiver retention held the ingest writer lock across
its whole sweep; and measured freed-but-unreclaimed pages, deleting
fresh in-window records) — both accepted only after a **pin correction**
(round 2 reported "no bite" because the dispatch named a base at which
those fixes had already landed; the correct base is `98a3625`).
cr3-1 is accepted as PARTIAL by design: read failures are now visible
(`export.segment_unreadable`) instead of silently "healthy", but the
live-tail read is NOT fixed — the writer's `FileShare.None` is
load-bearing (`IsLockedSegment` classifies live vs closed by
openability), so the coordinated-reader fix is R3d.

**cr3-2 was reopened SIX times, each for a real silent-loss path. All
FIFTEEN paths were found across ten codex rounds plus a Fable-5 second
opinion; FOURTEEN are fixed and ONE is open by design. Detection work was halted at `a330279` by the stopping
rule set before round six; **the owner overrode that halt ("you can do 3
more rounds")**. All three authorized rounds (7-9) are spent, each
confirming the prior guard AND finding a real path: round 7, a parseable
but schema-less ledger passing as legitimately empty (closed by
requiring a schema marker); round 8, a gap INSIDE one delivery batch
(closed by walking the whole batch); round 9, a gap held only in memory
when the ledger alone was unwritable while the cursor advanced (closed
by parking the evidence on the cursor). Round 9 answered the closing question explicitly:
detection was NOT complete. **The owner then dispatched round 10 (codex
plus a Fable 5 second opinion), which found TWO more — including that
the round-9 commit's written claim that parked counters "flush into the
ledger when it recovers" was FALSE (no flush existed), and that its
residual argument ("if both metadata paths are unwritable, execution
stops first") was also false: the spool stays writable, so delivery
continues and evidence dies at restart.** Both are fixed at head: the
flush is implemented, and export now PAUSES with
`export.metadata_unwritable` rather than delivering with nothing durable
behind it. **A Fable 5 agent, dispatched as a second opinion and told it shared the
author's blind spots, found four more with failing tests — two codex
never saw.** Three are fixed (corrupt ledger behind a healthy cursor
erasing proved gaps; export now pausing before delivery when no metadata
can be persisted; refusals as a durable REFUSED_RECORDS counter, with
"healthy" no longer possible while any permanent loss is recorded).
**One is OPEN and unfixable in the exporter: a wholly vanished
supervisor boot is structurally invisible because boot ids are random
UUIDv4 with no lineage — closing it needs a PRODUCER SCHEMA change (boot
lineage), which is R3d work.** Its reproduction is preserved as a
skipped test. Fifteen paths, fourteen fixed — the case for R3d is now
overwhelming: stop deleting undelivered records and most of this class
cannot arise. Durable lesson: **never state a mechanism in a commit
message that no test exercises** (the round-9 "flush" claim was false).
The arc, worth knowing before touching this code: file bookkeeping could
not distinguish "deleted after delivery" from "deleted with a tail
outstanding" (round 1: false alarms + process-local state), end-of-file
proved transient (round 2: append→rotate→delete between drains), boot
changes bypassed comparison entirely (round 3: new-boot prefix and
old-boot suffix), and a cursor-less first read inspected nothing
(round 4), and boot memory living only on the cursor meant losing the
cursor hid an erased boot's undelivered tail (round 5 — closed by
mirroring the chain position into the durable ledger). Detection now
rests on the records' own per-boot contiguous
`sequence` with `producer.supervisor_boot_id`, plus a deliberate
proved/unverified split: `EXPORT_GAPS` means records provably lost,
while an old boot ending without its `server.stopped` terminal raises
`unverified_boot_boundaries` instead — suspicion never counted as
proof. Two accepted limits, documented not papered over: an unparseable record
contributes nothing to detection, and destroying the audit root
(ledger included) leaves nothing knowable about prior delivery. **Durable lesson: a guard must assert only through surface
that exists in the OLD revision — twice a "proof" reverted into a
compile error, which proves nothing.**

**R3d is UNDERWAY: acknowledgment-aware retention landed.** Journal
retention now consults the export cursor through `ExportRetentionFloor`:
**age-based cleanup can no longer delete a segment the exporter has not
delivered** — the structural fix that makes most of the cr3-2 class
impossible instead of merely detectable. A missing or unreadable cursor
yields no floor and the prior behaviour, because the journal must never
depend on the exporter's bookkeeping. **Capacity pressure remains the
one case that may still evict undelivered records** — the alternative is
refusing to journal, which would let a SIEM outage stop execution
(forbidden by rule 2) — so delivered segments are always evicted first
and an undelivered eviction is announced on stderr, with the exporter's
chain detection proving the loss. Guard proved fail-before/pass-after
against the pre-floor sink (`7aa03ff`). An undelivered eviction is now
reported in `ptk_state` as `SPOOL_EVICTED_UNDELIVERED=<n>` — the stderr
line it originally used was invisible in practice, the same weakness the
owner called out for quarantine reporting (`a2dcece`).

**Producer boot lineage landed 2026-08-10 — the last open cr3-2 path is
CLOSED; all fifteen are now closed.** Every audit record carries
`producer.previous_supervisor_boot_id`: the id of the last predecessor
boot that journaled at least one record, read at journal open from an
owner-only `boot-lineage.json` in the audit root and published on the
boot's FIRST durable append (never at open — so a boot that opened and
crashed before writing anything neither appears in lineage nor alarms,
and a boot with zero records lost zero records). The exporter's chain
walk reads the attestation and reports a claimed predecessor that
delivery never observed as `unverified_boot_boundaries` — deliberately
suspicion, not EXPORT_GAPS: the vanished boot's record count is
uncountable and a stale lineage entry (whose writer could not publish)
produces the same mismatch. Benign shapes are pinned silent: a truthful
attestation (ordinary restart) and records carrying no lineage at all
(pre-lineage producers — an upgrade manufactures no alarms). The
`ptk.audit/2` version string is unchanged: the field is additive inside
`producer`, the receiver's strict root-property whitelist is untouched,
and its validator accepts absent/null but rejects garbage (absent ≠
null matters there: `OptionalString` fails on a missing property, which
one first cut got wrong and 29 receiver tests caught). A corrupt lineage
artifact is quarantined and journaled; the pending-startup-quarantine
channel became a list because host identity and lineage can both
quarantine on one startup and the single slot silently dropped one.
The formerly skipped reproduction is un-skipped and passing; sabotage
proofs bit at all three layers (exporter detection, producer publish,
receiver validation). Battery: server 1,270/1,270, SIEM 256/256, Pester
112+3 skip, handshake PASSED, dependency audit clean.

**The coordinated live-tail read landed 2026-08-11 — cr3-1 is now fully
fixed, not PARTIAL.** The writer's `FileShare.None` on the live segment
stays untouched (it remains load-bearing for `IsLockedSegment`); the
exporter instead reads the live tail through the writer's OWN handle via
`AuditJournal.ReadCommittedSpool` — the mode-agnostic primitive the
restored library already carried (the anchored `AuditLiveSpoolReader`
machinery above it stays deliberately unrestored). Wiring: the gate
exposes a `_gate`-locked `JournalForLiveExport` snapshot; Program.cs
hands the export service a `liveJournalSource` delegate; when a file
read fails, the exporter asks the journal for the durably committed
prefix (the flush watermark always sits on a record boundary, so no
torn records), delivers it, and advances the same file-byte cursor —
rotation hands over to the ordinary closed-file read seamlessly, proved
by a continuity test (live drain → rotation → drain; every record
delivered exactly once, no gaps, no boundaries). A drained live tail is
now QUIET instead of a permanently reported `export.segment_unreadable`;
that failure code still fires for a segment that is unreadable and NOT
the live journal's (the old pin still passes, with no live source
configured). Any live-read fault degrades to exactly the pre-existing
reported-failure behaviour. Sabotage proof: live read disabled → both
new tests fail. Battery: server 1,272/1,272, SIEM 256/256, handshake
PASSED.

**R4 IS CODE-COMPLETE (2026-08-11, `a9eaa4a` + `3996e6a`), codereview
in flight — see §Next.** Three surfaces landed, all four reporting
surfaces of contract rule 3 now exist: (a) `audit.spool_evicted` is a
first-class journal record emitted from emergency capacity on the append
after an undelivered eviction, so the fact reaches the SIEM through the
ordinary export leg; writing its guard exposed a real pre-existing hole,
fixed in the same commit — the rotation-time physical-allocation path
(`EnsurePhysicalAllocationAvailable`) deleted closed segments with NO
floor consultation and NO undelivered accounting, silently destroying
undelivered records; (b) the loopback web UI
(`Audit/Web/AuditWebUiService.cs`, plain `HttpListener` — no ASP.NET in
the trimmed server): journal-backed log view (closed segments as files,
own live tail through the writer's handle), quarantine evidence,
audit+export health, and a settings page that writes `export.json`
through the loader's own validation (a UI write the next start would
refuse is impossible; the credential is preserved on endpoint-only
updates and never echoed). Auth is a bearer token in an owner-only
`ui-token` file plus loopback/Host pinning; one UI per root, supervisors
race for the port (default 8317, `PTK_AUDIT_UI_PORT`,
`PTK_AUDIT_UI_DISABLED`), losers stand by and take over; (c) the
edge-triggered alert webhook (`AuditAlertWebhookService`, optional
`alert_webhook` in `export.json` or `PTK_AUDIT_ALERT_WEBHOOK`, same
https-or-loopback rule) — conditions post when they appear or grow,
never repeat unchanged, and an undeliverable webhook keeps the edge
pending. The composition boundary pin moved 3 → 5 hosted services.
Battery: server 1,287/1,287, handshake PASSED, SIEM 270/270 unchanged.

**R3c EXECUTED 2026-08-11 (owner goal "finish the whole things",
2026-08-11): PTK can now reach its OWN fallback receiver.** The receiver
gained (1) a bearer-token auth mode — optional `ingest.token` in the
protected config (min 16 chars; its SHA-256 is the custody credential
identity, mirroring the mTLS thumbprint; TLS flips to AllowCertificate
only when a token is configured, and a presented certificate is still
validated exactly as before) — and (2) an `application/json` OTLP ingest
path accepting the generic-collector shape PTK's exporter actually
emits, batched. Key design calls: the ENVELOPE is transport (lenient,
proto3-JSON empty-array omission tolerated, unknown decorations
ignored) while each record's JSONL body is the custody evidence and
passes the SAME extracted validation core as the protobuf path
(event-hash recomputation included); the two indexing hints PTK writes
(`ptk.event_type`/`ptk.event_id`) are cross-checked so a decoration
cannot contradict its evidence; per-record raw evidence is the exact
log-record JSON, NOT the whole request — the producer regroups batches
across retries, and request-level bytes would have made honest replays
look like "same event, different bytes" (quarantine) besides storing
each envelope up to 256 times. Batch responses aggregate to the
producer's existing contract: first transient stops the pass (replay is
idempotent), any permanent yields 400 so the producer isolates
record-by-record. 401 is used for auth failures, which the producer
already classifies as retryable. All 256 pre-R3c receiver pins pass
unchanged; 13 new tests, three sabotage proofs (auth bypass, TLS-mode
flip, hint check) — one test was strengthened when its first sabotage
did NOT bite (EnsureSuccessStatusCode throws HttpRequestException on
401 too; the test now demands a transport-level failure with
`exception.StatusCode == null`). Known gap, deliberate: the receiver
serves HTTPS with operator-provided certs, and PTK's HttpClient
validates server trust normally — reaching the receiver still requires
its server cert to be OS-trusted (or a real CA); the trust-bootstrap
story is an R6 docs/packaging question, not an auth question.
Receiver README documents the new contract; drift between the
receiver-side copy of the producer envelope shape and the real exporter
is R5's conformance suite's job.

**R3d note (historical):** the coordinated live-tail read and
acknowledgment-aware retention both landed — see the R3d entries above.

**Hook anchor advice fixed (2026-08-10, owner report, blanket fix
authorization).** The owner observed agents prefixing every `ptk_invoke`
call with `Set-Location`, treating the warm runspace as stateless. Root
cause: `scripts/ptk-hook.ps1`'s deny guidance said unconditionally "anchor
the command: prefix it with: Set-Location '<cwd>';" — models complied on
the replay and then generalized the prefix to every call they composed.
The advice is now a persistence fact plus a conditional suggestion ("The
warm runspace keeps its current directory across calls; if needed,
prefix: Set-Location '<cwd>';"). Two owner corrections shaped it the same
day: the first rewrite's "if this session is not already there … do not
re-anchor later calls" was rejected as wordy, and the terse "on the first
call only" was rejected as wrong — it read as forbidding later
Set-Location. The cwd test pins the final phrasing and rejects both bad
shapes, proven fail-before/pass-after against the original wording. Note the fix reaches users through the *installed*
hook — `~/.claude` hooks on existing installs carry the old wording until
reinstalled/re-inited.

**The `10.1.10.173` install failure had two legs; both are explained and
the product one is fixed (2026-08-07/08).**

Leg 1 — by design, resolved first: with any harness session's ptk
connected, `scripts/install.ps1` refuses at its line-181 live-process
guard; the named PIDs were the working session's own MCP supervisor and
worker (`$PID` match confirmed). Any agent-driven install self-blocks
this way. Note the harness auto-restarts the MCP server, so killing
`PtkMcpServer` helps only until the next ptk tool call — close or idle
ptk-connected sessions for the duration of an install.

Leg 2 — product defect, fixed in this commit: the owner's local
`-FromRelease -AllAgents` re-run (after stopping ptk processes)
downloaded v0.2.1 and failed the package smoke with "PTK requires RTK …
could not find it" while `rtk` 0.44.2 ran fine on PATH. Cause: winget
exposes rtk as a `WinGet\Links` symlink; a Windows symlink reports its
own `FileInfo.Length` as 0; and `ExecutableFileIdentity.TryCapture`
bounded the length *before* resolving the link, so the RTK startup gate
(added 2026-08-03, `e6e718d`) rejected every winget-installed rtk on
Windows — a `PTK_RTK_PATH` pointed at the shim failed identically.
Running sessions kept working because the installed `0.2.0-dev.gecd3a4c`
build predates the gate entirely (`RtkDependency.cs` absent at that
commit); CI never caught it because its Windows leg extracts rtk as a
plain exe, never a symlink. Fix: bound the resolved target, never the
link. Guard:
`RtkDependencyTests.A_symlinked_configured_executable_resolves_through_its_target`,
proven fail-before/pass-after on this box; pre-fix HEAD also reproduced
the owner's exact handshake stderr here, and the post-fix battery and
handshake results are recorded in this commit's message.

**v0.2.2 shipped the fix: published 2026-08-08 04:39 UTC on the owner's
explicit go, tagged at `5f76583`, run `31239078883` green on all five
RIDs, marked Latest.** The release also carries #40's Store-package
module diagnostics — the only other product change aboard. Pending
confirmation: the owner's unmodified `-FromRelease` install test on
`10.1.10.173`, with ptk-connected sessions closed/idle during it (leg 1).
That run's first attempt failed only its osx-arm64 leg on a
notarization-check flake: the `--wait` poll stream died on a transient
HTTP timeout after Apple had already accepted the submission, and the
acceptance check grepped only that stream. Fixed forward at `648d264` —
a missing acceptance now re-reads the authoritative `notarytool info`
before the step fails (`bash -n` verified; end-to-end proof rides the
next `v*` tag). The published draft came from a clean re-run of that
leg.

**Capture-marker candidate defect: diagnosed 2026-08-10; the text-loss
half no longer reproduces, the false-positive half is real and
owner-gated.** The 2026-08-07/08 observation (seven lines of
`@{Value=[active member not evaluated]}`, refusal text lost) was made
through the then-installed pre-gate build; live probes through the
current installed build show merged-stream text preserved — native
stderr under `*>&1` renders its message (`931dccb`'s ErrorRecord
projection), and `Write-Host`/`Write-Warning` through `*>&1` survive
too, all under reason `passive_projection_lossy`. What remains is a
**false "capture incomplete" alarm on every `Select-Object` result**:
`RunspaceHost.cs` (`ProjectOutput`, the `PSCustomObject` branch) sets
`_activeMemberOmitted` for any custom object whose TypeNames are not the
bare defaults, and `Select-Object` always prepends `Selected.<Type>` —
so `... | Select-Object a,b` reports
`[ptk:capture incomplete reason=active_member_not_evaluated retained=N
total=N]` although no `Selected.*` type data exists by default and every
note property was copied; nothing was omitted. The gate is deliberate
honesty (type-table ScriptProperties on a custom typename are user code
the capture must not run). **FIXED 2026-08-10 on the owner's explicit go
("go", ruling the recommended TypeTable-lookup option):** the capture now
carries a passive presence probe over the runspace type table's
`_extendedMembers` ConcurrentDictionary (reflection, fail-conservative:
an unanswerable probe keeps the flag), and the `PSCustomObject` branch
flags omission only when a non-default type name actually has registered
type data. The same predicate extends nested-note composition, fixing a
second live loss: a nested `Select-Object` result fell through to
`PSCustomObject`'s empty `ToString()` and vanished silently. Guards:
three new tests (top-level Select-Object completes clean; nested
composes; pre-registered type data still flags with the getter never
run), each sabotage-proved in both directions, and
`Late_type_data_cannot_turn_a_captured_type_name_into_executable_output`
now asserts complete — at projection time (the synchronous `DataAdded`
drain) the name had no type data, so nothing was omitted; its security
assertions are unchanged. Battery: server 1,214/1,214, SIEM 247/247,
handshake passed, Pester 112+3 skip (pre-change same session; only C#
changed after).

**Also open, unrelated and owner-gated: D1 of
`.agents/plans/package-manager-distribution.md`** (the shape of the `ptk`
CLI entry point). The plan is DRAFT, no slice approved, and every slice is
gated behind D1. The recommendation put to the owner was option (a), verb
the existing binary.

**o53-3 (HIGH, 2026-08-07): the opr-53 structured verdict hid all output in
Claude Code.** Field-reported ("returns output only via handles now") and
reproduced firsthand: CC renders `structuredContent` INSTEAD of text, so
every completed `ptk_invoke` showed only the verdict JSON — no output, no
recovery handles. Fixed same day under the owner's explicitly delegated
design call ("This is consumed by agents. You're an agent. What do YOU
need?"): completed responses are bare text again and absence is the
completed verdict; non-completed responses keep the structured verdict plus
a full `text` mirror; the call filter's text matcher is gated to
`ptk_output` by tool identity so worker text cannot forge `isError` back.
Guard-proved by two sabotages; finding record
`.agents/review/findings/o53-3.md`. **This host's `~/.ptk` was repaired by
the owner the same day** (killed all ptk processes, reinstalled) — the old
nested-payload caveat is history. **CONFIRMED live 2026-08-07 after the
owner reinstalled the fixed build and reconnected:** completed calls show
full output and recovery handles again; a refusal renders as a flagged
error with its complete reason. The rc.3 draft predates this fix — a fresh
draft (or the tag itself, which builds from its own commit) picks it up.

**Handoff 2026-08-06, head `46f8254`. Owner ruled the next release is
`v0.2.0` (not 1.0).**

In flight: **one owner decision, nothing half-done.** The tree is clean, all
**rc.3 is GREEN: the re-run (`31184671731`, head `9e1790e`) passed all
five RIDs and assembled the `v0.2.0-rc.3` draft.** Slice 7.5 is closed on
every leg; the per-RID table in
`.agents/plans/github-release-packaging.md` owns the record. The first
dispatch (`31184268679`, head `587b6e5`) proved the gate real: both
Windows legs passed — including the hardened Defender scan-completes
check, so hosted runners can scan — and the three POSIX legs correctly
blocked the draft over the proof's check 13, which asserted the
Windows-only `ls` alias unconditionally (POSIX ships none; a clean session
binds the native `/usr/bin/ls`). Check defect, not product defect; fixed
platform-conditionally at `ccd6d1d`, proved locally against both observed
shapes. An earlier dispatch mistakenly landed on the `roethlar` fork
because `gh` defaults to the `github` remote — run canceled, `-R` rule
recorded in repo-guidance §Remotes.

**Package-manager distribution is PLANNED, not started (2026-08-07):**
`.agents/plans/package-manager-distribution.md` (DRAFT). Owner asked for "a
real binary release so people don't have to clone the repo" plus winget,
brew, AUR "and any other package managers that make sense". The plan owns
the detail; do not restate it here. Two verified facts drive it: **there is
no `ptk` command on PATH at all** (the installer creates no shim; harnesses
get absolute `~/.ptk/bin/PtkMcpServer` paths, and the binary has no CLI
verbs), and **the documented public install is circular** — the README says
"without cloning this repository" then tells the user to run
`scripts/install.ps1`, which only exists in a clone. D1 (the shape of the
`ptk` entry point) blocks every slice and is with the owner.

**Signing reaches users through `-FromRelease`, proved end-to-end
2026-08-07.** The README's public install path downloads the release asset
(the no-flag path builds the checkout and is inherently unsigned — a
developer path). Signatures survive download+extract+stage, verified on the
*published* v0.2.1 assets, not on build output: win-arm64 on the VM reports
`Get-AuthenticodeSignature Status=Valid`, signer `CN=Michael Coelho`, issuer
`Microsoft ID Verified CS EOC CA 03`, timestamped; osx-arm64 on the Mac
passes `codesign --verify --strict` with the full Developer ID → Apple Root
chain and a timestamp. `spctl -t exec` on a bare CLI reports "does not seem
to be an app" — expected, not a failure. README §Signing now states this;
its previous "the v0.2.0 binaries are not publisher-signed or
Apple-notarized" was true when written and became false at v0.2.1.

**Decision 5 is EXECUTED IN FULL. PTK v0.2.0 was PUBLISHED 2026-08-07
16:35 UTC on the owner's explicit two-step go ("ship" → tag; "go" →
publish):**
https://github.com/AlsoBeltrix/PowerShell-Token-Killer/releases/tag/v0.2.0
— tagged at `3a9cbeb`, tag run `31197966252` green on all five RIDs, five
artifacts + SHA256SUMS, marked Latest. The stale `v0.2.0-rc.2`/`rc.3`
drafts were deleted on the same go. First post-release effort, in flight: **artifact signing on both
platforms** (owner direction 2026-08-07, "get cert … pass smartscreen and
defender").
- macOS: **DONE — proven end-to-end 2026-08-07.** Run `31205119195`
  (`0.2.1-rc.2` dispatch, head `47095f2`): all 18 Mach-Os signed with the
  owner's Developer ID (log asserts `Authority=Developer ID Application:
  MICHAEL COELHO`), every gate passed on the signed hardened bits (the
  entitlements keep the JITting runtime alive), and Apple notarization
  returned `status: Accepted` (submission `f0ac18a9-…`). The first attempt
  (`31204854349`) failed only on the authority grep (`-dv` prints no
  Authority lines; needs `-dvv`), fixed at `47095f2`. The `0.2.1-rc.2`
  draft on GitHub is proof-run debris, harmless, deletable at will. Every
  future `v*` tag ships signed+notarized macOS artifacts automatically.
  Historical wiring detail below. The owner's Developer ID identity
  ("Developer ID Application: MICHAEL COELHO (27R2KCAHN7)", SHA1
  `38549307…`) is verified against the exported `.p12`
  (`~/Dev/certs/songr-developer-id.p12` — one identity signs all the
  owner's apps; the songr naming is cosmetic). Four secrets are set on
  canonical (`MACOS_CERT_P12_BASE64`, `MACOS_CERT_PASSWORD`,
  `APPLE_APP_SPECIFIC_PASSWORD`, `APPLE_TEAM_ID`); **`APPLE_ID` (the
  owner's Apple-account email) is the one missing piece**, then a
  dispatch proves sign+notarize end-to-end. The release workflow signs
  every Mach-O before the gates (so the gates exercise signed bits, incl.
  that the .NET entitlements keep the hardened runtime alive) and
  notarizes after them, asserting `status: Accepted` from output because
  notarytool's exit code is unreliable. Local-machine lesson recorded: a
  Developer ID identity is invalid to codesign until Apple's G2
  intermediate is present — the CI step imports it into its throwaway
  keychain for that reason.
- Windows: **DONE — proven end-to-end 2026-08-07.** Run `31221274583`
  (`0.2.1-rc.4`, head `ebd80c6`): both Windows RIDs signed every
  `.exe`/`.dll` via Azure Trusted Signing (account
  `roethlar-app-signing`, profile `public-trust`, `github-signing` app
  registration, secrets `AZURE_*`) and passed all gates on the signed
  bits; draft assembled, all six jobs green. Two failures on the way,
  both diagnosed and fixed: win-arm64 needed an x64 .NET runtime for the
  x64-only C++/CLI signing dlib (`4ac8312` — verified against the
  1.0.95 package: no arm64 build exists), and rc.3's osx leg was a hung
  runner mid-smoke-test (flake — signed fine, passed clean in rc.4; jobs
  now bounded at 45 min, `ebd80c6`). **The published `v0.2.0` artifacts
  predate all signing** — the next tag ships signed+notarized bits on
  every platform automatically; master also carries the user-facing #43
  and o53-3 fixes, so a `v0.2.1` is genuinely warranted whenever the
  owner says ship. The `0.2.1-rc.2`/`rc.4` drafts are proof-run debris,
  deletable at will.
**#7 is CLOSED 2026-08-07 on observed behavior:** current definitions no
longer flag the DLL — the v0.2.0 tag run's hardened Defender gate
(scan-completes + payload-survival, both Windows runners, exact shipped
bits) plus repeated clean Server 2019 installs. No explicit WDSI verdict
email ever arrived; the quarantine-detection mitigation (`51ce880`) stays
shipped, and the per-RID gate re-proves survival on every future Windows
artifact. #43 is CLOSED 2026-08-07 on the owner's
verification: the install completes on the reporting Server 2019 host
from master at head.**

**#44 is CLOSED (fixed `7a1aac2`, 2026-08-07):** the four session tools
now emit a progress heartbeat every 30s for the lifetime of a call
(SDK-injected reporter, no-op without a client progressToken), so
`timeoutSeconds` holds past any client idle window; `ptk_output` stays
bare (bounded, synchronous). Guards: heartbeat unit tests, a wiring guard
through the real `InvokeTool` (fails unwired), and a schema pin proving
the injected parameter never reaches the wire schema (`RawUsageTests`
surface pin updated to include `progress` deliberately). The cancellation
half was already honored by the call filter's linked token. Battery
1,204/1,204 + handshake. Landed after the `v0.2.1` tag — ships in the
next release. Live confirmation rides the next naturally long call
through an installed build.

**`v0.2.1` DRAFT is built and green — RECUT on the owner's word at
`0b43ea9` (run `31225914784`) so it carries #44 too: the first fully
signed release** — Windows Authenticode on both RIDs, macOS signed +
notarized, all gates on signed bits. Carries the #43 install fixes, the
o53-3 output fix, and the #44 heartbeat. The first tag (at `3f35774`,
never published) was deleted and re-pointed on the owner's explicit
recut instruction. **PUBLISHED 2026-08-07 23:22 UTC on the owner's word —
now Latest:**
https://github.com/AlsoBeltrix/PowerShell-Token-Killer/releases/tag/v0.2.1

**#40 is CLOSED (2026-08-07, both legs; fix `3bbe15d`). Only #30 remains
open on the tracker.** Its Windows ARM64 leg is root-caused and is not a ptk
defect.** Reproduced on the owner's new Windows 11 ARM64 VM
(`.agents/machines.md` §`10.1.10.212`) against the published `v0.2.1`
artifact. Both hypotheses in the issue — containment Job Object, worker
token — are **falsified with evidence**: worker and control have identical
integrity, elevation, and file/directory access, and the worker can read
the very bytes it is denied. The denial is specific to assembly/image
loading from the MSIX package tree by a non-packaged CoreCLR host; the same
module copied to an ordinary directory imports fine in the worker. Two
supporting facts: ptk ships no `$PSHOME\Modules`, and the worker retains
the inherited `PSModulePath`, which on a Store-installed host points into
the package. **Not ARM64-specific** — the same should occur on x64 Windows
with a Store-installed PowerShell. Workaround (proved): use the MSI/winget
PowerShell, or relocate the affected modules. The issue comment carries the
evidence table. **Owner field correction, same day: the VM's MSIX PowerShell
came from `winget`, not the Store** — `winget show Microsoft.PowerShell` on
ARM64 resolves to `Installer Type: msix` and `--installer-type msi` is
refused (`No applicable installer found`), while the same command on x64
installs the MSI. So **ARM64's default, recommended install path reproduces
this**, which makes the finding considerably more important than
"Store-installed users" implied, and made the first draft of the guidance
(«use winget instead of the Store») actively wrong. Corrected in the hint,
README, and machines record; a test now pins that the hint never reads as
"use winget instead". Lesson: do not infer an install method from a package
format.

**Owner chose document+explain (2026-08-07):** shipped as a
`[ptk hint]` on the exact failure signature (assembly load + access denied +
a `WindowsApps` path, all three required, path matched case-insensitively
because the bench reported it lowercased) naming the cause and both fixes,
plus a README section. Guard-proved in both directions by sabotage: a
case-sensitive match drops the real bench string, and dropping the
cause marker misfires on an unrelated package-tree load. Filtering MSIX
paths out of `PSModulePath` was considered and rejected — script modules
there load fine and would be lost.

**#40's macOS half NO LONGER REPRODUCES (2026-08-07).** The report's exact
120-tick / `timeoutSeconds=150` pipeline ran to completion through this
harness against a head build (`0.2.0-dev.g2b9ef73`): all 120 ticks present,
no `-32001`, and `ptk_state` immediately after showed
`warm_state_lost=false last_failure=none` — the opposite of the report's
`warm_state_lost=true last_failure=worker_lost`. **Attribution to #44 is
plausible but NOT isolated:** the original report came from a different
harness, and no A/B against a pre-#44 build in this harness was run, so
"the heartbeat fixed it" stays an inference, not a proof.

**#30's on-prem Exchange leg is SCHEDULED (2026-08-07): the owner will run
it on an Exchange-capable machine this repo cannot reach.** The owner-run
procedure is `docs/exchange-onprem-acceptance.md` — setup, a paste-block
for the agent on that box, and the pass criteria. #30 closes on its
results; bad rendering becomes its own finding.

**#43 (filed by the owner 2026-08-07, verified against the code the same
day): install fails on Windows Server 2019 because the handshake's
`Assert-LiveOutputRoot` counts a delete-pending artifact name.** Classic
NTFS keeps a deleted-but-open name enumerable; the product primitive
documents exactly that (`SecureAuditStorage.cs:176-179`) and gates its own
namespace check off Windows, but the test helper asserts raw counts
(`server/test-handshake.ps1:185`, used at `:601`/`:728`/`:822`). A
test-gate portability defect, not a product regression — the issue itself
proves containment holds on that host. Secondary: the installer threw away
the handshake's failing line. **Both halves FIXED 2026-08-07 under the
owner's blanket fix authorization** (recorded in repo-guidance §Earned
Practices): `4de1c89` (the live-root assertion probes artifact names on
Windows — access-denied means delete-pending and is excluded, anything
else still counts; POSIX and all post-exit checks unchanged) and `b811f05`
(the installer tees the handshake and carries its last 25 lines in the
thrown error). Proved here: probe classification for all three outcome
classes, both scripts parse, tee preserves the exit code, and the full
handshake passes on macOS. **The issue stays open pending the one proof
this repo cannot run: an install re-run on the reporting Server 2019 host
(owner action) — classic delete-pending semantics exist nowhere else we
can reach** (CI's `windows-latest` is Server 2022, POSIX-delete default).
Trap: the `v0.2.0-rc.3` draft was assembled at `9e1790e`, before these
fixes — a `-FromRelease` install of rc.3 on that host still carries the
old gate and will still fail. Re-test from the repo at head (or cut a
fresh rc if a release-artifact test is wanted there). The eventual
`v0.2.0` tag builds from its own commit, so tagging at/after `a0a2517`
picks the fixes up automatically.
Round 2 (2026-08-07): the owner's re-run from head passed every
previously failing gate — first fix confirmed on real classic-delete
NTFS — then failed the hard-kill leg's OWN raw live artifact count,
a fourth site the first sweep missed. Fixed in `a0a2517`: the probe is
now one helper (`Test-LiveArtifactEntry`) used by both live-phase
counts; a full-script sweep shows no other artifact-name counts except
the two post-exit ones, raw on purpose. Still open pending one more
Server 2019 install run from `a0a2517`+.

**What closed this session (release-gate work, after `opr-53`):**

- Slice 7.5's three hand-run checks are automated into
  `server/direct-product-proof.ps1`: check 10 uninstall (opt-in
  `-UninstallHome`, refuses unless the home contains the server under proof),
  check 13 `ls` alias + `PSModuleAutoloadingPreference=None`, and the Windows
  Defender payload scan. All three guard-proved by sabotage.
- **win-x64 Slice 7.5 re-run PASSED 22/22** at `935b8b2` against a real
  install into an isolated home, plus the full battery.
- `release.yml` now runs the product proof on every packaged RID before
  archiving (`46f8254`), so a release cannot be drafted from an artifact that
  fails its own contract. **Untested until the workflow actually runs** —
  that is what the dispatch above would prove.
- Recorded that the plan's "all slices executed" was stale by ~50 commits;
  `.agents/plans/github-release-packaging.md` now carries a per-RID table.

**Do not tag or push a `v*` ref without an explicit go.** The owner named the
version, not the tag.

**Caution, this host:** an EICAR control file written during a #7 scan check
tripped Defender and left a "Threat blocked" entry in the owner's Protection
history on a domain-joined machine (2026-08-06 10:07). It was the standard
harmless AV test string, removed automatically, nothing else touched — but it
may draw a security review. **Never write scanner-triggering test files
here.** The release gate's Defender check deliberately writes no EICAR and
judges by payload survival instead.


- **RTK is a required dependency (owner, 2026-08-03):** "rtk was never optional. rtk was always stated requirement when I asked for this." PTK is a compression router; the thing it routes to is not optional. A missing RTK is a startup error (exit 78) with an actionable message, never a silent degraded mode. Do not build a without-RTK product tier, capability matrix, or per-call degradation reporting. CI installs rtk on all three platforms.

- **Routing authority: RTK decides (owner, 2026-08-03).** PTK submits the exact submitted text to `rtk hook check --agent ptk`; a rewrite executes, a decline runs the original unchanged. PTK no longer judges eligibility from the PowerShell AST and no longer resolves executables against PATH. Three guards protect the boundary, each mutation-proved: the accepted rewrite binds the startup-pinned executable (a bare `rtk` head would resolve through PATH at run time and could execute a different binary than the one PTK hashed); a rewrite must reduce to the submitted text once `rtk ` prefixes are stripped; and every wrapped name must bind to an `Application` in the session.

- **The agent session is a clean PowerShell 7 (owner, 2026-08-03).** `PSModuleAutoloadingPreference = None` in the initial session state. `PSModulePath` is retained so AD/Exchange stay discoverable and explicit `Import-Module` is unaffected. Previously, referencing any name a user module exported autoloaded that whole module — one reference pulled in 44 commands — and because autoload is lazy, two sessions on one machine diverged by whatever was invoked first. Scope correction on the record: an autoloaded function does *not* override a built-in alias, so the earlier claim that autoload rebinds `ls` was wrong.

- **PTK infers no shell (2026-08-03).** Automatic Bash detection, refusal, validation, and delegation are deleted. Bash reaches RTK the way every other native command does: the user writes `bash -lc '...'`. The `ptk_invoke` description states the dialect is PowerShell 7 and names that escape hatch.

- **Every `opr-*` finding is dispositioned (2026-08-03).** `.agents/review/dispositions.md` is the disposition of record and owns the per-bucket counts; it reports zero open blockers. Do not restate its buckets here. The "accepted and plan-gated" state is retired — it let a finding be recorded without ever being resolved. A defect found during implementation is now fixed in its slice or dispositioned.

- **Known gap, `opr-20`:** its fail-closed half is guarded; its pre-write half is argued from the code path and unguarded. No available test seam reaches the pre-write window (each client owns its writer, the operation lease observes cancellation before the try block, and the stream seam sits downstream of the first-write callback). A vacuous guard was removed rather than shipped.

- **Two non-blocking findings are worth a look before release** — named and explained at the end of `.agents/review/dispositions.md` §"remaining, not blocking", which owns that call. Both are cheap and outside the router plan's scope. Both were **re-confirmed live at `c5a0bb2`** on 2026-08-05 and are planned in `.agents/plans/pre-release-opr-10-opr-53.md`: the timeout predicate accepts `1e400`, `1.5`, `0.5` and `86401` where all four should fall back, and a script that prints PTK-shaped lines has its forged `[ptk worker] status=` and `recovery=` handle preserved verbatim beside the genuine ones. `opr-53` carries an owner decision (escape the grammar, or move control information to structured content) and cannot be implemented until it is ruled.

### From `## Next`

**Resume on Windows Server:** fetch the published `master`, re-ground from this file, and run the Windows verification entry point before the next development slice. Treat Mac PTK uninstall state as irrelevant to Windows work unless the owner explicitly returns to Mac cleanup; do not infer that the remaining Mac payload or registrations should be removed.

**The cr5 loop over R4 is CLOSED — all eight findings VERIFIED
(2026-08-11).** `.agents/review/index.md` §cr5 owns the record. Fixes
landed one commit each with fail-before/pass-after guard proofs;
verification ran as two batches (HIGHs frontier per T2, MEDIUMs
standard) and returned THREE real reopens, each repaired and
re-accepted at frontier: cr5-2 round 2 (the construction instant kept
sub-millisecond ticks while quarantine filenames carry milliseconds —
a same-millisecond artifact silently never paged; floored to the
shared granularity, `e253c12`), cr5-3 round 2
(`DirectoryNotFoundException` classed as benign retention — a vanished
spool directory omitted every closed segment at `partial:false`; now
an explicit decision table with load-bearing arm order, `459039a`),
and cr5-4 round 2 (the live tail appended after every closed segment
regardless of chronology — under the shared-root topology a quiet bind
winner's stale live records outranked a busy peer's newer rotated
evidence; record units now ordered by segment recency, `124ba4c`).
Notable fix shapes: the UI bearer token is now per-bind (bind first,
mint fresh, publish only while owning the listener, delete on stop —
a harvested token dies at the next bind, `ee0ac2f`); webhook
"new quarantine" is judged from the filename-embedded instant, so the
constructor does no filesystem work at all (`5a7d895`). **Reviewer
transport lesson: codex 0.147.0's workspace-write sandbox denies the
VSTest testhost socket (round 1 returned guard_confirmed:false with no
test run); dispatch guard-proof reviews with
`-c 'sandbox_workspace_write.network_access=true'` — with it the
reviewer ran every proof independently.** Battery at `124ba4c`: server
1,301/1,301, SIEM 270/270, Pester 112+3 skip, handshake PASSED.

**R5a EXECUTED and its review loop CLOSED (2026-08-11, `be80e59` +
cr6-1 fix `4d564c4`): the producer-owned conformance gate exists — the
mini-SIEM S4 fixture gate is UNBLOCKED and its first half executed.**
Producer side (the additive test-only commit the mini-SIEM plan
authorized, executed under R5's approval + the standing go): golden
request corpora captured through the REAL `HttpAuditDestination` path
(OTLP JSON + Splunk HEC × v1 + v2; three-record chains — complete,
minimal, Unicode — distinct event ids, version-consistent hash links),
byte-compared every run in `SiemConformanceGoldenTests`, regenerated
only under `PTK_WRITE_GOLDEN=1`. Receiver side: `ProducerConformanceTests`
POSTs the exact golden bytes — accepted, stored byte-identically
(`exact_json_body`), idempotent on replay, value-level Unicode and
timestamp fidelity — and the fixture locator fails closed, stopping at
this checkout's `.git` boundary (cr6-1, found by the slice's codereview:
the walk originally continued past the repo root and could adopt an
ancestor checkout's stale corpus; §`.agents/review/index.md` cr6).
The gate bit twice while landing, both real: the first corpus reused
one event id for every record (the receiver rightly 400'd it —
duplicate_mismatch/chain_gap; corpus fixed with distinct ids), and an
encoder sabotage failed exactly the two OTLP goldens. Battery: server
1,306/1,306 (plain shell), SIEM 275/275. **Test-dispatch lesson: run
the full server suite from a plain shell — BOTH warm ptk sessions on
this host now carry the truncated `PSModulePath` and reproduce the
recorded four-probe StateToolTests failure; earlier same-day a default-
session run was clean, so the truncation is worker-inheritance
roulette, exactly as `.agents/machines.md` records.**

**R5b EXECUTED and its review loop CLOSED (2026-08-11): mini-SIEM S5
exists.** `1422bec`: read-only operator query API + dashboard
(`siem/PtkSiemReceiver/Web/OperatorEndpoints.cs`) on its own Kestrel
listener with a connection-marker feature so neither surface can serve
the other's routes; distinct operator credential; Host pinning on
plain-HTTP loopback; per-request read-only SQLite; parameterized
filters with limit clamps; embedded plain-JS dashboard (recorded
deviation: no htmx). **The cr7 codereview over `47fd8e2..1422bec`
returned five findings, all admitted, all fixed one commit each
(cr7-1 `89f5284` query-string auth removed — evidence endpoints are
header-only, the zero-evidence page serves token-free and takes the
pasted token into sessionStorage; cr7-2 `47c33e6` `token_reuse`
loader refusal; cr7-3 `4011f44` bounded chains list + non-overlapping
poll; cr7-4 `a7edc20` parsed canonical time filters; cr7-5 `99c5c7f`
canonical lowercase event-id binding), and ONE frontier verification
batch CONFIRMED all five with every guard independently re-proved.**
`.agents/review/index.md` §cr7 owns the table. SIEM suite 285/285.
Receiver corpus note: event IDs must be UUIDv7, boot/host IDs v4 —
a v4 event id 400s at ingest.

**R5c EXECUTED (2026-08-11), codereview cr8 dispatched over
`b093dea..782d345`.** Two commits, mini-SIEM S6 complete:
- `53aa635` gap-disposition state machine: schema v2 in-place
  migration (`events.post_gap`, `gaps`, one active gap per boot); a
  chain_gap rejection opens durable gap evidence with its quarantine
  row; while a gap is open/dispositioned, otherwise-valid records
  beyond it commit flagged post-gap with sub-chain continuity while
  the head stays frozen; POST /api/gaps/{id}/disposition
  (resolved|accepted-loss) is the SOLE resumption authority —
  stored sub-chain resumes immediately at its tail, otherwise the
  next anchor record resumes; every transition custody-chained with
  the operator token's SHA-256 as actor. Tests include the plan's
  reject → disposition → restart → resume ordering and a real v1→v2
  migration. Knock-on: the cr3-4 retention guard was recalibrated
  (96 old records; the v2 fixed page overhead had flipped its
  marginal half-size bound) and re-proved biting against the
  raw-page-count sabotage.
- `782d345` alert pipeline: schema v3 (durable `alert_queue` written
  in the ingest transaction stamped with the frozen rule-config
  SHA-256, persisted `alert_cursor`, custody-chained `alerts`);
  config `alerts` section (event_match/chain_break/gap_detected/
  ingest_rate, strict per-type parameters, webhook
  HTTPS-or-loopback); evaluation commits alerts + custody + cursor
  atomically (exactly once, crash-replay at startup, both config
  hashes recorded across a rule change); webhook fires after commit,
  bounded 3 attempts, never a gate; lifecycle API open→acknowledged→
  closed only. Kill test uses the deterministic
  `alertEvaluationHoldForTests` seam; enqueue-drop and
  transition-unguard sabotages both bit. SIEM suite 294/294.

**The cr8 loop is CLOSED — all seven findings VERIFIED (2026-08-11).**
`.agents/review/index.md` §cr8 owns the table. Seven fixes one commit
each; verification one frontier batch; cr8-4 took FOUR frontier
rounds (three real reopens, each a deeper migration-backfill hole,
ending at the v7 custody-ledger adjacency link — the opener's
quarantine receipt immediately precedes its gap:opened receipt,
instant-independent; the reviewer checked the adjacency invariant
across the whole shipped history). Notable design call recorded in
cr8-1: verified arrival HEALS a gap automatically (hash proof needs
no human); operator disposition remains the sole authority for
accepting loss. Store schema is now v7 with in-place migrations from
v1. SIEM suite 305/305; server suite 1,306/1,306 (plain shell)
re-confirmed after the R5c base commits.

**R6 EXECUTED (2026-08-11), codereview cr9 dispatched over
`a43e4e4..c9b41c8` — the LAST audit-restoration slice.** Four
commits: `31e81cb` the release-gate positive journaling check
(direct-product-proof runs its server on an isolated HOME-rooted
`PTK_AUDIT_ROOT` — temp dirs are refused on macOS, /var is a symlink
— and asserts nonempty artifacts carrying real records; bite proved
both ways by env-var sabotage; proof is now 23 checks Windows / 21
elsewhere); `eb6c6f9` AUDIT-EXPORT.md rewritten from the false
"audit disabled" claim to the restored contract (every claim
verified against the code); `7a1f8f7` README Audit status truth fix
+ hook clarification; `c9b41c8` the receiver ships signed from every
release leg as its own artifact `ptk-siem-receiver-<version>-<rid>`
(second Trusted Signing invocation, macOS codesign+notarize envelope
covers both payloads, no-config smoke naming PTK_SIEM_CONFIG, draft
gate now ten artifacts — end-to-end proof rides the next `v*`
tag/dispatch, an owner action). CI already carried the SIEM legs.

**The cr9 loop is CLOSED — all five findings VERIFIED (2026-08-11).**
`.agents/review/index.md` §cr9 owns the table. The generation pass
caught three HIGHs the slice itself missed: the journaling gate was
satisfiable by lifecycle records alone (now demands call.accepted, a
terminal call outcome, and the proof's session name — 24 checks
Windows / 22 elsewhere), server/README.md still said auditing was
disabled, and the ACTIVE exact-script evidence store was documented
as legacy-only (both docs now require protecting `~/.ptk/audit` as
sensitive data). One frontier verification batch confirmed all five.

**EVERY PTK audit-restoration slice (R0–R6) is EXECUTED with its
codereview loop CLOSED.** The separately approved mini-SIEM S4b/S7 remainder
subsequently completed on 2026-08-12; current evidence is at the top of this
file. Release-artifact packaging/proof remains S8 and needs a separate owner
go; no `v*` tag, dispatch, package, or release was created by S7.
The codex verification recipe that works: `-s workspace-write -c
'sandbox_workspace_write.network_access=true'` (VSTest testhost needs
the socket), per-server MCP `enabled=false` overrides, `-o <file>`
for the verdict.

**Housekeeping note (2026-08-11):** `git stash` holds one pre-existing
entry — an *hcc-7* prompt-flush WIP for `scripts/ptk_init.ps1`
(Read-Host prompt unflushed through the install pipe). It predates this
session, was accidentally popped during a guard proof, and was restored
to the stash untouched with a descriptive message. It is uncommitted
work from another effort; do not drop it silently.

**Goal in force (owner, 2026-08-11): "finish the whole things.
codereview codex per slice."** That is the go for the remaining
audit-restoration slices in order, each followed by a codex codereview.
R3d is DONE (cr3-2's fifteen paths all fixed; cr3-1 fixed in full).
R3c is EXECUTED and its review CLOSED. R4 is code-complete with its
review OPEN (eight findings, above). **The cr4 codereview loop over
`abc3292..8ab189a` (boot lineage + live-tail + R3c) is ACTIVE:** codex
returned five findings, all admitted, all centered on one blind spot —
multiple supervisors sharing one audit root (one per MCP connection is
the deployment norm). All five fixes are LANDED and sabotage-proved
(`9acd89d`..`5ddbaef`; §`.agents/review/index.md` cr4 owns the table):
non-v4 lineage id now quarantines instead of poisoning every append;
failed lineage publishes are visible (`LINEAGE_UNPUBLISHED` in
ptk_state); lineage resolves+publishes atomically at FIRST append under
the cross-process spool quota lease (concurrently opened boots chain in
first-append order — plus a caught self-deadlock: the lease had to be
released before the metrics update, which reacquires it); the export
leg gained a cross-process single-exporter lease, boot-grouped
traversal (no false gaps from interleaved boots), halt-at-unreadable
(the cursor can never pass undelivered records), and a
delivery-order-aware retention floor; and the receiver's duplicate
identity is now the exact record body, never transport bytes (honest
cross-encoding replays idempotent). Note the reviewer transport lesson:
codex-cli 0.147.0 silently ignores `-c 'mcp_servers={}'` — the first
generation dispatch failed fail-closed on `capability_ok:false`;
per-server `enabled=false` overrides work and are cached.
**Verification round 1: cr4-1/2/3/5 ACCEPTED (guards independently
confirmed; HIGHs at frontier per T2). cr4-4 REOPENED, correctly:** the
first fix's boot-group order was keyed on the earliest segment STILL
PRESENT — a mutable order; deleting a delivered segment re-sorted an
undelivered boot before the cursor (skipped forever) and the floor
would age-delete it as delivered. **Repair landed `ae7ca0a`: per-boot
durable positions replace the linear cursor entirely** (cursor v2 +
migration; per-boot ledger chain memory; stable boot-id group order —
no cross-boot order is load-bearing anymore; per-boot halt, which also
removes the first fix's lag cost; per-boot retention floor —
unrecorded boot keeps everything, terminal-delivered boot keeps
nothing; boundary heuristics moved from crossing-order to LINEAGE
ATTESTATION, strictly stronger). Contract change recorded: pre-lineage
records cannot attest a predecessor, so the ended-without-terminal
suspicion now requires the successor's lineage claim (two boundary
tests updated to carry it). One test-support lesson: the walk rightly
refuses records contradicting their segment's boot, so the test
helpers now derive segment names from the records' embedded boot.
**The cr4 loop is CLOSED — all five findings VERIFIED.** cr4-4 took
four frontier rounds: round 2 reopened on drain-scoped attestations
(claims read once at delivery went silent when a blocked predecessor's
tail vanished later — fixed by persisting the claim on the successor's
durable cursor position, re-judged every drain, `97ee6d1`); round 3
reopened again by replaying the cr3-2 round-5 attack (evidence living
only on the loss-tolerant bounded cursor — fixed by mirroring
attestations into the gap ledger with heal-on-judgment, `940dc3c`;
the guard's sabotage did not bite at first because re-delivery re-read
the claim from still-present segments, so the test now deletes the
delivered segments as retention would); round 4 accepted. Battery at
close: server 1,281/1,281, SIEM 270/270, handshake PASSED. Next: **R4** (receiver token auth + JSON ingest —
without it PTK cannot reach its OWN fallback receiver, though Splunk,
Sentinel and any OTLP collector work today), **R4** (the loopback web
GUI + settings page — the slice that finally lets the owner SEE the
logs, and which also owes the journaled eviction/quarantine events and
the webhook surface), R5 conformance/alerts, R6 CI/docs/packaging. Plan:
`.agents/plans/audit-restoration.md`; R1 discovery record alongside it.

**Two durable review lessons from this session, both earned:** a guard
must assert only through surface that exists in the OLD revision (twice
a "proof" reverted into a compile error, which proves nothing), and a
verification dispatch must pin the base at which the fix actually
landed (one round reported "no bite" purely from a wrong pin). And one
process failure worth not repeating: **never state a mechanism in a
commit message that no test exercises** — the round-9 "flush" claim was
false when written.

**Goal in force (owner, 2026-08-05): finish the app to a 1.0-ready state.**
Review significant code changes with codex (default settings), work GH issues
periodically. Under it, #13 was closed and #42 filed and planned.

**Landed under the goal, 2026-08-05.** #13 closed (round-3 verdict
`accepted`). #42 filed, planned, and its code slices 1, 2, 3, 3b landed with
guards, plus both codex findings (i42-1 HIGH, i42-2 LOW) fixed. `opr-10`
fixed and guard-proved. Battery at `eca0891`: server 1,164/1,164, Pester 111
+ 1 skip, dependency audit clean, handshake passed, direct product proof
17/17 against a complete payload.

**#42 is CLOSED (2026-08-05).** Slice 4 did not need this host's install
repaired: an install into an isolated `HOME` (`USERPROFILE`/`HOME`/`HOMEPATH`
set on the child process, since `$HOME` itself is read-only) exercises the
real transaction, activation, validation, and registration without touching
`~/.ptk`. `Assert-PtkRuntimeNotRunning` filters by path prefix, so it scopes
to the sandbox and does not see the live servers. **Use this technique for
any future install-path work** — it is the difference between simulating the
defect and reproducing it.

**Two durable lessons from this issue, both earned the hard way:**

- **`Copy-Item -Recurse` and `Move-Item -Force` both put a directory *inside*
  an existing same-named directory.** Neither is a safe way to replace a
  directory. Only an explicit recursive merge, or a rename onto a
  proven-absent path, is. This single mistake produced the original bug and
  then reappeared inside two successive fixes for it (i42-1, i42b-1).
- **A guard for a merge must supply a destination that already contains a
  same-named child.** The first guard written for i42b-1 passed against the
  broken code, because driving the merge through activation meets an empty
  destination. Test the helper the way the failing caller calls it.
- **File-lock behaviour is Windows-only.** POSIX renames and unlinks open
  files happily, so a test that asserts a throw under an open handle fails on
  ubuntu/macOS. CI caught two such tests that passed locally; they are now
  `-Skip:(-not $IsWindows)`, with the platform-independent half split out so
  it still runs everywhere.

Doing so found three further defects the unit tests could not see, all fixed
in `1ff20c8`: activation deleted in place (destroying the payload before
discovering a lock), the undo used `Move-Item -Force` (which nests a
directory into a same-named directory, the very defect being fixed), and
rollback aborted before restoring because its own delete threw. Proof:
baseline 387 files, locked reinstall, still 387, no `bin/bin`, rescued
install passes the release gate 17/17. Before that commit the same run left
208.

**This host's own `~/.ptk` is still nested** and still runs the stale
111-file payload. It is not a code problem any more — the installer now
refuses to install over it and says how to repair. Repair means closing every
ptk process (including the server the working agent runs through) and
reinstalling: owner action.

**`opr-53` is FIXED (owner ruled option (b), 2026-08-06).** PTK's own
verdict now travels in the protocol's `structuredContent` (`disposition`,
`executed`, `safe_to_resubmit`, `detail`) and `isError`, which worker output
cannot reach; the response text is unchanged byte-for-byte. The four session
tools return `CallToolResult`; `ptk_output` still returns a string and keeps
the text matcher. This also closed the deferred refusal→`isError` follow-up.
Commits `11eafee`, `c40404a`, `18d76e8`. Two follow-on defects were found in
the same effort, one by self-review and one by codex — both were PTK stating
a *false* verdict in the new trusted channel, which is the same mistake as
the original finding one layer in. `.agents/review/index.md` §o53 owns that
lesson.

**Review loop r806 is CLOSED (2026-08-07): all five fixes landed,
battery-verified, and verification-accepted.** One commit per finding, each
sabotage-proved: r806-3 `adb9f7a`, r806-4 `49a5cc7`, r806-1 `024fa66`,
r806-2 `9d8aec7`, r806-5 `5862041`. Battery green at `5862041` (server
1,191/1,191, Pester 112+3 skip, SIEM 247/247, audit clean, handshake
passed). Verification was one owner-directed batch dispatch (frontier,
esc:T2) — all five accepted, guard_confirmed true;
`.agents/review/index.md` §r806 owns the record. The r806-4 fix has a
consequence for the rc: a hosted Windows runner that cannot complete a
Defender scan now fails the per-RID gate visibly (by design); adapting the
workflow or runner is an owner call if it fires.

Otherwise every open GitHub issue is gated on hardware (#40), the owner
(#30), or Microsoft (#7). Decision 5 — tag `v0.2.0` and publish — is
terminal and owner-only.

**Next action:** get the owner's go for the `workflow_dispatch`
(`0.2.0-rc.3`) described in `## Now`. If it passes, four of the five Slice
7.5 legs close and only the tag and publish remain. If it fails, the failure
is in the new per-RID gate step (`46f8254`) or in a platform the win-x64 leg
could not exercise — read that job's log first, not the product.

**Historical, for the record:**

1. **`opr-53` — needed an owner ruling before implementation.** The finding was
   re-confirmed live (a script that prints PTK-shaped lines has its forged
   `[ptk worker] status=` and `recovery=` handle preserved verbatim beside
   the genuine ones). The choice is (a) escape the reserved grammar inside
   the text channel — smallest change, but mutates legitimate user output —
   or (b) return supervisor control information as structured content —
   larger, touches the tool surface, and also fixes the deferred
   refusal→`isError` mapping. Recommendation: (b). Plan:
   `.agents/plans/pre-release-opr-10-opr-53.md`.

   Not taken unilaterally, for two recorded reasons rather than caution:
   `.agents/review/dispositions.md` dispositions it **not release-blocking**,
   and option (b) changes all five tools from `Task<string>` to a structured
   return — a design fork with user-visible consequences either way, which
   the plan explicitly gates on the owner.

**Working the test-report backlog** (owner, 2026-08-05): fix the reported
issues, one codex review per completed fix, maximum two rounds. The backlog
itself is closed — every issue it raised is fixed and closed (#33–#38, #41)
or filed and blocked on matching hardware (#40); the landed detail is
rotated to `docs/history/state-archive.md`. The review cadence it set still
governs the loops that follow it.

**Reviewer dispatch is fixed on this host.** codex is API-only via Portkey;
there is no `codex login`. Dispatch with
`codex exec --cd <repo> -s read-only --color never -c 'mcp_servers={}' - < <prompt>`
— without the MCP override, `codex exec` auto-denies its own configured ptk
tools and the reviewer burns its budget retrying them. The recurring
`refresh token was revoked` log line is noise.

**Known follow-up, deliberately deferred:** the refusal → `isError` mapping
reads the response text, because the tools return `Task<string>` and the
structured `InvokeDisposition` is flattened before the filter sees it. Codex
round 2 argued for carrying the outcome as data instead, and it is right —
text matching cannot be made airtight, only well-pinned. Every marker the
matcher accepts is covered by a test, including the false-positive shapes.
Threading a structured result through the tool surface is the real fix and
is a larger change than this batch.

### The queue

Open work, ordered. An open issue is queued work: "backlog complete" is not
the same as "nothing queued", and this section exists so the two are never
conflated again.

1. **#42 — CLOSED 2026-08-05.** Every slice landed and the issue carries the
   close-out. Commits: `8801281`, `b0573ee`, `210f865`, `8a1bf2a`, the two
   codex findings `044d53b` and `7761b75`, and `1ff20c8` for the three
   defects only a real locked install exposed. The detail is above and in the
   issue; do not restate it here. Original filing: filed
   2026-08-05 from this host's own broken install. `Move-Item` of a directory
   onto a surviving directory nests instead of replacing
   (`scripts/ptk_install_transaction.psm1:279`), so a failed or raced
   `Remove-PtkInstallPath` silently produces `bin/bin/` and leaves the old <!-- lint: allow (runtime nested directory created by bug, not a repo path) -->
   payload registered. Here that is 111 files against a correct publish's 296
   — 185 missing, including `System.Collections.NonGeneric.dll` and
   `Microsoft.PowerShell.SDK.dll`. `Assert-PtkPayloadIntact`
   (`scripts/install.ps1:199`) name-checks five paths, all present in the
   stale payload, so it passes; the package smoke test passes too, because
   the server starts and handshakes fine. The issue owns the detail and the
   three-part repair; do not restate it here.

   This is the root cause of the previously uninvestigated live defect: an
   ordinary read-only `Get-Service`/`Get-Process` is refused with "Trusted
   pre-execution isolation failed" **and the warm runspace is recycled**,
   because ETS member access throws `FileNotFoundException` for the missing
   assembly. `ConvertFrom-Json` is broken in the same worker for the same
   reason. Release-blocking for 1.0: an install can look healthy and then
   destroy warm session state on a read-only command.

2. **#13 — worker death diagnostics — CLOSED 2026-08-05.** Slice 4 executed:
   the issue carries the close-out comment and the design record (nothing on
   the worker-death path is forgery-proof, so PTK asserts only the
   unrequested exit and labels the exit code and stderr tail untrusted).
   Round-3 verification over `4f9284f..e2c2902` returned `accepted` /
   `guard_confirmed: true`, SHAs matched; `.agents/review/index.md` owns the
   verdict and the one accepted residual risk. Historical detail below is
   retained until the next rotation.

   The
   owner's 2026-08-04 comment closed the speculative-fix path and named what
   the next recurrence needs: the worker stderr diagnostic, or the crash
   event/exit code. PTK currently discards both, so a recurrence is
   structurally guaranteed to dead-end again. Evidence: worker stdout and
   stderr are drained into a discard loop
   (`server/PtkMcpServer/Worker/SessionWorkerClient.cs:311`), and
   `IWorkerContainedProcess`
   (`server/PtkMcpServer/Worker/WorkerProcessAuthority.cs:29`) exposes no exit
   code — while the worker already writes a bounded
   `ptk_worker_exit kind=… detail=…` line and a distinct exit code per failure
   class (`server/PtkMcpServer/Worker/WorkerProcessExit.cs:13`). The work is
   to retain that last bounded stderr line plus the exit code and surface them
   on the `outcome_unknown` path
   (`server/PtkMcpServer/Sessions/WorkerSupervisor.cs:314`) instead of
   dropping them. Plan:
   `.agents/plans/issue-13-worker-death-diagnostics.md` (the plan owns the
   detail, do not restate it here). Slices 1-3 landed; codex found three
   defects, all fixed, and its verification pass reopened two of the fixes,
   both repaired at `e2c2902` — still the last code commit as of `78b2dbb`;
   everything after it is docs and `.gitignore`.

   **Reviewer dispatch on this host — settled 2026-08-05.** The two dead
   round-2 dispatches were not a transport problem and not the work. codex's
   `~/.codex/config.toml` registers the `ptk` MCP server, and under
   `codex exec` (non-interactive, `approval: never`) every `ptk_invoke` /
   `ptk_session` call is auto-denied with `user cancelled MCP tool call`;
   the reviewer burned its whole budget retrying a tool it could never call.
   Adding `-c 'mcp_servers={}'` fixed it — round 3 returned a verdict in
   168s on 51k tokens. codex here is API-only through Portkey; there is no
   `codex login` to run, and the `refresh token was revoked` line it logs
   continuously is confirmed noise (a `pong` probe answered in ~3s while
   logging it). The dispatch recipe lives in `.agents/review/index.md`.

   **Durable lesson from this loop:** on the worker-death path nothing is
   forgery-proof, because the caller's script runs *inside* the worker — it
   controls both the worker's standard error and its exit code. PTK
   therefore asserts only that the process exited unrequested, and presents
   the exit code and retained stderr line as explicitly untrusted evidence.
   Do not reintroduce a per-kind classification here without a
   supervisor-owned authenticated channel; two successive attempts to
   classify (from text, then from the exit code) were both forgeable.
2. **#7 — Defender false positive.** Gated on Microsoft's WDSI verdict
   (submitted 2026-07-20). No local action until it lands.
3. **#40 — macOS long-pipeline worker loss; Windows ARM64 MSIX module
   imports.** Gated on matching hardware; neither reproduces here.
4. **#30 — on-prem Windows remoting acceptance.** Owner-gated by the issue's
   own terms: it does not authorize a probe, and needs the owner to name host,
   auth path, identity, command surface, and cleanup authority first.
5. **Decision 5 — tag `v0.2.0` and publish.** Terminal and owner-only.
6. Unqueued candidates below (POSIX bootstrap, smoke-test narrowing, the
   refused read-only pipeline) are not on this list until the owner puts them
   there.

Review dispatch remains owner-gated throughout and happens only on the
owner's explicit word.

Unqueued candidates — not on the queue above until the owner puts them there,
in no particular order:

- A POSIX bootstrap so macOS/Linux can install without `pwsh` already
  present.
- Narrowing the install-time smoke test. It is a full product handshake run
  twice per install, opening worker sessions and writing under `~/.ptk`; a
  failure of the second one rolls back an otherwise-good install, so a flake
  reverts a working installation. Initialize plus `tools/list` would catch a
  broken payload without that exposure.
- ~~One live defect seen during this session and not investigated~~ —
  investigated and root-caused 2026-08-05; it is **#42**, the nested-payload
  install, not a router or classifier bug. See queue item 1. The refusal
  reproduces on any command whose objects need a trimmed-away assembly
  (`Get-Process`, `Get-Service`), so it will disappear on a repaired install.

**Release plan:** `.agents/plans/github-release-packaging.md`, which executes
Slice 7 of the router plan and supersedes the packaging mechanics of
`.agents/plans/release-distribution.md`. The release-packaging plan owns the
ruled decisions (2, and A–D), the executed slice status, and the proved
`v0.2.0-rc.2` draft release; do not restate them here. Every slice of it is
executed. Decision 5 — tag
and publish — is terminal, owner-only, and the last thing left *in that
plan*; the queue above, not this line, is the repo's open work. Do not tag or
push a `v*` ref without an explicit go.

Landed release-slice detail (Slices 7.0–7.5, the one-installer
consolidation, per-harness consent, the kimi leg, the fixture rename, and
the closed routing investigation) is rotated verbatim to
`docs/history/state-archive.md`.

**Agent test plan: `docs/testplan.md` (`c73b434`).** ~70 numbered stress
tests an agent runs against a ptk it is already connected to, filing one
GitHub issue. Written for a cold reader on any machine: no install, no
checkout, no session context, no chat residue. It is a document only — **do
not run it and do not spawn agents against it** without an explicit owner go.
Earlier drafts that put this in `.github/workflows/` and a `.agents/` plan
were removed as wrong-shaped.

**Process constraints stay in force** (`.agents/plans/rtk-router-delegation.md`
§Process constraints): no reviewer invocation without a separate explicit
owner approval naming it; no file-by-file source review; fix-or-dispose, never
record a new gated finding.

## Rotated 2026-08-05 (drift based on 78b2dbb)

### From `## Next`

Fixed: #37 (shadowed-name routing), the version gap, the elision/recovery
contradiction, the missing effective-route label, refusals arriving as MCP
success, the misapplied uncertainty rider, the passive object-shaping loss
(the headline complaint), the lossy-recovery overclaim, a hint for the
`1>&2` dialect trap, and the bounded-capture prefix claim (#35 F4).

**Object shaping (`b3604f1`, `72a864e`).** A trusted type collapsed to one
`ToString()`; `Get-Culture` returned `en-US` and nothing else. Scalar
properties of trusted types are now projected by name. Codex round 1 caught
a HIGH in that first cut and it was real, reproduced before fixing: the
getter was called *before* the value was judged, so `[Lazy[string]]` — which
is `System.Private.CoreLib`, hence trusted — ran the caller's factory
delegate during capture. Gating now happens on the property's **declared**
type and declaring container, never on what came back. `Task<T>.Result` is
the same shape and is denied for the same reason.

Guard rails worth keeping in mind for future work here: the no-user-code
invariant cannot be enforced after a getter runs, and an over-strict rule is
its own defect — rejecting all virtual getters removed `CultureInfo.Name`
and `DisplayName`, the exact values the fix exists to restore.

**Investigated and closed without a code change:** Linux `2>&1` ordering
(#36). Reproduced correct order on Windows; the reported inversion comes
from bash block-buffering stdout while stderr is unbuffered, before PTK sees
either byte. Recorded on the issue.

**Filed, not fixed — needs the matching hardware (#40):** macOS long-pipeline
worker loss (#35 F2, not reproducible on Linux) and Windows ARM64 MSIX module
imports denied inside workers (#34 F3).

**Every issue from the four platform test reports is now fixed, or filed with
the investigation that could not finish here.** Open issues carrying that
work:

- **#40** — macOS long-pipeline worker loss and Windows ARM64 MSIX module
  imports. Both need the matching hardware; neither reproduces here.
- **#41 fixed and closed (2026-08-05).** The recorded instrumentation probe
  settled it in one observation: `CopyPassiveInstanceNotes` reaches the
  nested note, and `TryRenderTrustedText` claims the hollow
  `System.Management.Automation.PSCustomObject` base object — trusted
  assembly, empty `ToString()` — returning `''` and discarding the value
  before any later logic runs. That short-circuit also explains why both
  earlier compose variants "stayed empty on a verified-current build": a
  compose branch placed after the trusted-text render is unreachable for
  this shape. The fix composes a nested custom object's exact
  `PSNoteProperty` members as `Name=value; Name=value` text **before** the
  trusted-text render, bounded to 3 object levels (which also terminates
  self-referencing graphs) and the existing 2048-character render cap. The
  composed string survives the shaping module end-to-end — the downstream
  fear recorded in the issue does not reproduce on a fresh build. Guards:
  the #41 repro (`MARKER` must surface) and the depth bound, both proved
  failing with the compose branch disabled.

Recurring lesson worth carrying: on this shaper, a stale build tree produces
convincing wrong answers. Several investigations here chased behaviour that
had already been fixed. Kill build-tree `PtkMcpServer` processes and rebuild
before trusting a live probe.

The test-report backlog is complete: every issue it raised is fixed and
closed (#33–#38, #41) or filed and blocked on matching hardware (#40). (#32,
the kimi leg, was closed 2026-08-05: it landed at `ad1665e` and the tracker
was out of sync. The close comment records one deviation from the issue's
design sketch — no `startupTimeoutMs` is written, and live verification
passed without it.) One new machine observation from this pass (ptk-session
`PSModulePath` truncation failing four StateToolTests) is recorded in
`.agents/machines.md`, not filed as an issue — owner's call whether it is
product-visible.

Session-close fact (2026-08-05): the #38 and #41 fixes landed **without a
reviewer dispatch** — codex was undispatchable at the time (its configured
headroom gateway was not running and the owner was out of frontier-model
credits), and the owner declined an automatic dispatch for these fixes. Suite
counts belong to `.agents/repo-guidance.md` §Verification, which also records
the plain-shell caveat.

Decision 5 — tag `v0.2.0` and publish — remains owner-only, untouched, and is
now downstream of this backlog. Do not tag or push a `v*` ref without an
explicit go.

**Decision 2 is RULED — five RIDs** (owner, 2026-08-03): `win-x64`,
`win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`. No `osx-x64`. GitHub
Actions covers the matrix; each RID builds on its own native runner because
`Assert-PtkNativeBuildRid` refuses cross-RID layout builds.

**Slice 7.1 is complete (2026-08-04): both Unix HIGH findings are repaired
and Unix packaging is unblocked.** Selecting Linux and macOS activated two
findings a Windows-only release would have dodged:

- `opr-15` at `cd00276` — identity observation is now tri-state (exact,
  confirmed-absent, indeterminate). Previously every query failure read as
  "process dead", so one transient `/proc` error could release a session
  alias while an escaped descendant still ran. Indeterminate now fails
  closed and retries, matching `ProcessGroupExists`.
- `opr-14` at `67d37dd` — `FD_CLOEXEC` is set with `ioctl(FIOCLEX)` instead
  of a fixed-signature P/Invoke to variadic `fcntl`, which is wrong on
  Apple arm64. Also atomic, closing the get/set inheritance window.
  **`FIOCLEX` differs per platform** (Darwin `0x20006601`, Linux `0x5451`,
  both verified against xnu and Linux uapi headers) — a shared constant
  would break Linux.

`opr-14`'s guards skip on Windows, so it was proved on branch
`ci/opr-14-cloexec`: all six CI jobs green including `macos-latest`, which
is Apple arm64 — the exact platform the ABI bug affects. Merged fast-forward
and the branch deleted. The rest of
`.agents/review/dispositions.md` §"deferred to platform selection" is
MEDIUM/LOW and does not block.

**Decision A RULED (owner, 2026-08-04): win-arm64 ships with the emulated
x64 rtk.** rtk v0.44.2 publishes only `rtk-x86_64-pc-windows-msvc.zip`, so
the RID runs it under Windows ARM64 x64 emulation. CI must prove the
emulated rtk answers `hook check`, not merely `--version`; the installer
must fail at install time if it does not.

**Decision B RULED (owner, 2026-08-04): Apache-2.0.** `LICENSE` now exists
at the repo root; it was previously absent. Slice 7.2 packages it into every
artifact.

**Slice 7.0 landed at `bf1fc0b` (owner: "yeah fix it").** The object shaper
recognized six types and returned `[active member not evaluated]` for every
other one — `Get-Culture` came back with no data on a host with no Outlook
and no Exchange. GitHub #8 framed this as an Outlook/COM problem; it was not,
and Decision 3 is **withdrawn as mis-scoped** rather than ruled. Types from
trusted assemblies (the framework directory plus the two PowerShell
assemblies) now render via `ToString()`; dynamic and location-less
assemblies — what `Add-Type` produces — are never trusted, so both
no-live-getter guards pass unedited. The exception-message gate is widened
the same way (#8's secondary complaint). Rendering is capped at 2048 chars
and marked `passive_projection_lossy`, not `active_member_not_evaluated`.
Do not reopen this as a COM-getter question: executing active getters at
shaping time stays rejected.

**Routing is not defective — a suspected bug was investigated and closed
(2026-08-04).** `git ... 2>&1` appeared to fall back to PowerShell. It does
not: RTK declines `git --version` (nothing to compress) with or without a
redirection, and rewrites `git status --short` either way. The regression
that would catch a real redirection gate is pinned at `24eff27`. When
reproducing shaper behavior, use a bare cmdlet — a native command PTK runs
directly because the script was prefixed with a cmdlet is a property of the
call, not of the shaper.

**Decisions C and D RULED (owner, 2026-08-04):** release version `v0.2.0`;
RTK reaches users by fetch-on-install (installer downloads the matching rtk,
checksum-verified against rtk's own `checksums.txt`, into `~/.ptk/bin`; an
rtk already on PATH is used as-is and never touched; a marker file records
what the installer placed so uninstall removes only that).

**Every slice of the release plan is executed.** 7.2 version/licence
packaging (`5b19260`, `fb6d951`), 7.3 `.github/workflows/release.yml`
(`ecc5df4`), 7.4 installers (`141793d`, `eec2ccd`, since consolidated), 7.5
`server/direct-product-proof.ps1` (`db5601c`).

**One installer: `scripts/install.ps1` (`3109ec1`, 2026-08-04).** Three
existed. The root `install.ps1`/`install.sh` registered claude only and never
called `ptk_init.ps1`, so anyone installing from a release never got the
codex, grok, or agy legs — those legs are written and live-verified, and ran
only from the dev script. Merged into the dev script, which already had the
transaction, rollback, ARP entry, harness init, and uninstall; it gained
`-FromRelease` (download the asset, verify against `SHA256SUMS`), rtk
resolution before registration, and `-Purge`. Root scripts deleted, net −491
lines. Modes: `-FromRelease`, bare (build checkout), `-Uninstall [-Purge]`,
`-LayoutOnly -OutputDir` (release CI). Verified at that commit: server
1,068/1,068, Pester 84 with 1 platform skip, layout build correct.

Consequence to keep in view: **macOS and Linux now need `pwsh` to install**,
because `install.sh` is gone. The installed payload still embeds its own
PowerShell and does not need one. No POSIX bootstrap was written — deliberate
under the owner's one-installer instruction, not an oversight.

**Per-harness consent landed (2026-08-04, owner-directed: pacman-style).** A
detection-mode `ptk_init` install asks once: a numbered `Found:` list of
detected harnesses and a single skip selection (`1,3`; `2-4`; `0`=skip all;
Enter=install all). Declines and `-SkipAgent` exclusions print a
manual-setup blurb (the harness's `mcp add` command or config snippet plus
the re-run command). `-AllAgents` answers yes to everything; a
non-interactive session wires all with a notice. `-Uninstall`/`-Show` never
ask. Claude's registration moved from `install.ps1` into the claude leg, so
one consent covers registration + hook + nudge and leg failure fails the
install into rollback as before. `install.ps1` gained `-Agent`/`-SkipAgent`/
`-AllAgents` pass-throughs. Upgrades re-prompt every time — no consent
store; add one only on evidence. Pester 99 passed, 1 platform skip; the
skip-filter mutation is caught by the new tests.

**Kimi harness leg landed (2026-08-04).** `ptk_init.ps1` now covers claude,
codex, grok, agy, and kimi. The kimi leg merges `mcpServers.ptk` into
`~/.kimi-code/mcp.json` (no scriptable CLI surface exists), installs the same
`ptk-hook.ps1` as a `[[hooks]]` PreToolUse `matcher = "Bash"` entry in
`config.toml` (owner-approved in the mandate; kimi's protocol accepts the
script's stdout deny JSON verbatim), and writes the shared nudge block to
`~/.kimi-code/AGENTS.md`. Live-verified on this box (docs/harness-support.md):
a fresh `kimi -p` session was denied on Bash with the ptk guidance and got an
answer from `ptk_state`.

**Fixture sessions renamed `exchange-*` → `sample-*` (`e2d31db`).** The
install-time smoke test printed `named worker topology ok: exchange-online
pid=..., exchange-onprem pid=...` — two processes named after an admin's mail
infrastructure, during a tool install. Nothing Exchange-related ran; the
names were flavour resembling ptk's target workflows. The installer now also
announces the smoke test before running it (local workers, no network).

**A `v0.2.0-rc.2` draft release exists and is proved** (run `30940515893`,
head `fb6d951`): five RIDs each built, handshake-smoked, and RTK-gate-proved
on their own native runner; 16/16 direct product checks against the
installed win-x64 candidate; Defender scan clean with the payload intact.
The emulated x64 rtk on `windows-11-arm` answered `hook check`, so Decision
A is proved rather than assumed. Full evidence is in the plan's status
table — do not restate it here.

**The only thing left is Decision 5: tag and publish.** That is terminal and
owner-only. CI assembles drafts; nothing tags a `v*` ref or publishes
without an explicit separate go.

## Rotated 2026-08-05 (drift based on f99a92b)

### From `## Now`

- **#38 fixed and closed (2026-08-05, owner ruling).** PowerShell's engine
  invokes an exception's `Message` override while building the error record,
  upstream of capture — ruled **outside** PTK's boundary. The invariant in
  force promises only that the capture itself executes no user code. An
  untrusted exception now surfaces its type name and base-constructor
  message via a field read of `System.Exception`'s backing field (executes
  nothing); the `Message` override is never invoked by capture and its
  computed text is never reported. Ruling recorded in `.agents/decisions.md`;
  invocation-counting guards in `RunspaceHostTests.cs`, proved failing
  against the old behavior.

## Rotated 2026-08-05 (drift based on a2a8a9a)

### From `## Now`

- **hcc review loop closed (2026-08-04):** the codex generation pass over
  the kimi/consent range produced five findings; those plus owner-reported
  hcc-6 (**install rolled back on claude-less machines** — release-relevant,
  fixed at `553450c`) are all fixed one-commit-each, guard-proved, and
  reviewer-verified accepted (hcc-6 at frontier via owner-named codex pair;
  the claude frontier is undispatchable on this machine — org subscription
  disabled). Details: `.agents/review/index.md`.

- **Four platform test reports landed 2026-08-04/05** (#33 Windows x64, #34
  Windows ARM64, #35 macOS arm64, #36 Arch Linux x64), run against
  `docs/testplan.md`. All four are now closed: ten findings fixed, three
  filed as #38/#40/#41. Verified at `008172e`: server 1,129/1,129 (from
  1,068), Pester 107 with 1 platform skip, working tree clean, master in
  sync with origin.

  Every fix was mutation-proved and reviewed by codex at defaults, two rounds
  maximum per the owner's instruction. The reviews found 14 real defects
  across the rounds — each reproduced before acting — including two the
  reports never saw: `Lazy<T>` and `ThreadLocal<T>` are trusted-assembly
  types whose getters run a caller's delegate, and capture was invoking them.
  The last review returned clean.

- **#37 fixed and closed (`963195d`, `0056128`, `16638e0`).** The rtk rewrite
  ran the real binary when the submitted script defined a shadowing function
  in that same submission — the preflight command snapshot is captured before
  the worker runs, so the function did not exist yet and its name still read
  as `Application`. The submitted AST is now the authority. macOS caught it;
  #33 and #34 marked the same test passing because they tested a
  *pre-existing* function, which the snapshot does see.

  Two codex rounds found **eight** further bypasses of the first fix, all
  closed: switch parameters consuming the alias name, scope- and
  module-qualified names, `Import-Alias` recording a path, dot-sourcing and
  `Import-Module`, `Function:`/`Alias:` provider writes, and unreadable alias
  names. One predated #37: the binder emitted a double call operator for any
  rewritten `& git status`, a parse error rather than the command submitted.

- **`ptk_state` reports the build version (`a7624fa`).** Every report hit
  this: the engine version was shown, nothing identifying ptk, and the Unix
  binaries carry blank file-version fields, so testers named inferred git
  SHAs instead. First line now reads `ptk <version>: pid ...`.

### From `## Open / Parked`

- GitHub #8 is an owner field report from 2026-07-23. Its older
  installed runtime dropped script/lazy/COM values. The replacement server now
  preserves synthetic EXO-style selected/deserialized values, evaluates an
  explicitly selected script property exactly once in the user pipeline, and
  surfaces the tested terminating-error message; it still truthfully labels
  uninspected active type data as incomplete. Real EXO selected values now pass.
  Keep #8 open until a real Outlook item retains selected values without extra
  shaping-time getter execution and a real non-core EXO/Outlook terminating
  exception returns its exact message. The verified progress and gate are posted
  on issue #8; Opus accepted holding without a speculative active-getter change.

  _Rotation note: falsified — `gh issue view 8` confirms #8 was closed
  2026-08-05 06:58:47 UTC. The condition this entry was waiting on was met and
  the issue closed; the parked entry above is preserved verbatim as the
  evidence trail, not as current status._

## Rotated 2026-08-04 (drift based on 19201a1)

### From `## Now`

- **RTK router delegation plan is executed through Slice 6 (2026-08-03).** Plan: `.agents/plans/rtk-router-delegation.md`. Slices 0-6 landed; Slice 7 (version, package, direct proof) is unstarted and gated on Decisions 2-5. Per-slice commit table is in the plan's status block.

  Re-verified as of `a3112f3` (docs-only over code head `f637ad0`) on `ASHBIAMWEB1`: the whole battery passes. Counts live in `.agents/repo-guidance.md` §Verification, refreshed in the same pass; they moved during this work because ~6,500 lines and their tests were deleted. Local SIEM is 226/247 on this host only — the 21 pre-existing symlink-privilege cases recorded in `.agents/machines.md`, not a product failure.

  **Hosted CI is green at `a3112f3`** — all six jobs, and the RTK install step succeeded on ubuntu, windows, and macos, so the previously unexercised install path is now proven. SIEM is 247/247 in CI, where symlink creation is permitted.

  _Rotation note: superseded — Slice 7 was subsequently executed by the release-packaging plan (Slices 7.0-7.5 landed, `v0.2.0-rc.2` draft proved), which current `state.md` records; the commit-pinned CI/verification claims above stand as historical evidence of that pass._

## Rotated 2026-08-03 (drift based on a3112f3)

Landed `## Now` and falsified `## Blockers` entries, preserved verbatim.
Current state lives in `.agents/state.md`; canonical detail lives in
`.agents/review/dispositions.md`, `.agents/review/findings/rbc-5.md`, and
`.agents/machines.md`.

### From `## Now`

- **Implementation reviewed (2026-08-03):** openreview codex over `87d03d8..076626f` returned `acceptable_with_changes` and endorsed the architecture unchanged, including the pinned-path binding. Three material changes and four findings, all adopted and fixed with mutation-proved guards: the startup RTK gate used `File.Exists` while the runtime pins via `TryCapture` (HIGH — a path passing the weaker check let the server start and then run native commands unfiltered); the module still exported the pre-Slice-2 `Resolve-PtcInvokeScript` AST rewriter with no caller and a manifest entry for a deleted function; rewrite acceptance normalized whitespace, so a rewrite altering text inside a quoted argument was accepted; and `server/README.md` documented deleted Bash and post-success behavior. Record: `.agents/review/openreview-rtk-router-codex-r2.md`.

### From `## Blockers`

- **rbc-5/rbc-6 containment WIP is unlocated in this clone (2026-08-03).** The
  recorded carrier `fix/rbc-6-unix-sigkill-escalation` @ `2b3ce1a` does not
  resolve: no such branch locally or on `origin`, and `2b3ce1a` is an ordinary
  `master` ancestor. Either the branch lives only on another remote/clone or it
  was deleted. Do not recreate or re-derive the WIP; an owner ruling is needed
  on whether it still exists anywhere before the `## Now` preservation
  instruction can be acted on.

  _Rotation note: re-checked at `a3112f3` — the branch is still absent, and the
  premise is falsified. `.agents/review/findings/rbc-5.md` §Disposition (owner,
  2026-07-19) already rejected that WIP ("Do not continue or commit it"), so
  nothing was lost and no owner ruling is outstanding._

- **Plan-record drift, reported but not edited in this narrow state pass:**
  the warm-runspace plan still says slice 7 is paused behind the already
  decided GO, and the shared-runspace idea still assumes the rejected policy
  gate. Explicit owner calls, uncontested decisions, and live repo evidence
  named above control. Both re-confirmed still stale 2026-08-03.

  _Rotation note: both source documents were corrected in this drift pass
  (`.agents/plans/warm-runspace-mcp-server.md` status block;
  `.agents/plans/shared-persistent-runspace.md` gotcha 3), so the drift this
  entry tracked no longer exists._

## Rotated 2026-08-03 (drift based on e22d619)

Landed or self-labelled-superseded `## Now` entries, preserved verbatim.
Canonical detail for these records lives in `.agents/review/index.md`,
`.agents/machines.md`, and the named plans; current state lives in
`.agents/state.md`.

### From `## Now`

- **Historical first closure of `ci-slow-seal-2` (2026-08-01; superseded by recurrence):** test-only commit `5180d0b` retained the two-second seal limit with independent three-second elapsed bound below the five-second caller budget. Mutation proof failed at `3.1491933s`; restored focused and 1,221/1,221 full server tests passed. Exact-head run `30748054339` passed all six jobs plus macOS-only rerun jobs `91497781569` and `91498323805`. Historical plan: `.agents/plans/ci-slow-seal-elapsed-headroom.md`.

- **Program complete-source review closed (2026-08-02):** all 49 lines reviewed at blob `ba01d79` in one complete Opus composition pass with worker entry, stdin guard, lifecycle/filter, DI aliases, disposal, and real stdio behavior; focused tests passed 68/68 and exact-head CI `30782965551` passed all six jobs including three-platform handshakes. Existing `opr-1`, `opr-8` through `opr-10`, and `opr-42` remained excluded. Opus found no current defect; no product or test change.

- **ExecutionPlanner complete-source review closed (2026-08-02):** all 675 lines reviewed at blob `234c3833` in three bounded passes plus whole-file Opus integration; focused tests passed 103/103. Accepted HIGH `opr-58`, MEDIUM `opr-55`/`opr-56`, LOW `opr-57`; prior repaired and adjacent findings remained excluded. Exact target-visible probes resolved the sole integration dispute in favor of `opr-55`. Prior limited record expanded; no product or test change.

- **ExecutionPlanner `opr-58` intake (2026-08-01):** HIGH accepted plan-gated at `a3b7994`: successful mixed-dataflow guidance can recommend redirecting a fully buffering producer into its own input. Disposable-file proof preserved 13 characters under the completed pipeline but the suggested shape truncated the target to zero. No product or test change.

- **ExecutionPlanner `opr-57` intake (2026-08-01):** LOW accepted plan-gated at `1de0286`: redirected `CommandExpressionAst` pipelines return `PowerShell` before redirections are checked, so audit routing metadata mislabels file-writing expression forms. Execution remains PowerShellDirect. No product or test change.

- **ExecutionPlanner `opr-56` intake (2026-08-01):** MEDIUM accepted plan-gated at `3628487`: eligibility, domain classification, and guidance ignore `EndBlock.Traps`; RTK dispatch drops the handler. A warm native-error probe proved direct PowerShell enters the trap while the constructed RTK argv cannot. No product or test change.

- **ExecutionPlanner `opr-55` intake (2026-08-01):** MEDIUM accepted plan-gated at `3548eb8`: constant `CommandParameterAst` arguments, including whitespace-separated colon syntax, are merged into one RTK argv element while real PowerShell Standard and Windows native probes send separate prefix and operand elements. Existing test pins the wrong merged vector. No product or test change.

- **ExecutionPlan complete-source record revalidated (2026-08-01):** all 590 byte-unchanged lines rechecked at `6a66e20` in two bounded passes plus whole-file Opus integration; focused planner, dispatch, and shell-dialect tests passed 103/103. Existing `s3-rtk-preference-isolation`, `opr-4`, and `opr-48` through `opr-51` excluded. Three production-unreachable candidates retained only as reactivation notes; Bash original-script provenance verified explicit and guarded. Prior limited record expanded; no product or test change.

- **AuditExportCheckpoint complete-source record revalidated (2026-08-01):** all 527 byte-unchanged lines rechecked at `497e0b2` in two bounded passes plus whole-file production-reachability integration; focused tests passed 103/103. Existing `opr-37`, `opr-38`, and repaired Windows durability were excluded. The only caller candidate belongs to removed exporter completion and remains a reactivation review note, not a current finding. Prior limited record expanded; no product or test change.

- **WorkerSupervisor whole-file review closed (2026-08-01):** all 381 lines reviewed at source-equivalent `c9a7f51` in two bounded passes plus whole-file Opus integration against named sessions, protocol, tools, and real stdio behavior; focused tests passed 29/29. Accepted MEDIUM `opr-53`, LOW `opr-54`, and extended existing MEDIUM `opr-11`; existing MEDIUM `opr-42` remained excluded. Prior limited `3cd2482` record expanded. No product or test change.

- **`opr-11` runtime-boundary extension (2026-08-01):** real shipped-server stdio evidence proves generated DataAnnotations are advisory schema rather than enforced input validation. The existing MEDIUM route-fallback repair must validate in the active runtime path and make `WorkerSupervisor.ParseRoute` refuse explicit unknown values; schema-only repair is insufficient. No product or test change.

- **WorkerSupervisor `opr-54` intake (2026-08-01):** LOW accepted and plan-gated at `c9a7f51`: generated session-name schema constraints are not enforced by the shipped server, and rejected raw names are echoed into PTK directive lines. A real stdio probe injected a forged status line and started no worker. No product or test change.

- **WorkerSupervisor `opr-53` intake (2026-08-01):** MEDIUM accepted and plan-gated at `c9a7f51`: worker-controlled invocation and state text can forge PTK-authored retry, status, and recovery directives because both share one unframed response channel. A real stdio invoke preserved forged status and recovery lines beside the genuine recovery line. No product or test change.

- **AuditEvidenceRetentionAudit whole-file review closed (2026-08-01):** all 188 lines reviewed at `2c6bb7a` with evidence-store deletion, exact content/identity proof, event validation, and focused-test integration; tests passed 15/15. Existing HIGH `opr-35` was excluded as pre-selection scope. Prior limited `735000e` review expanded; no additional distinct finding and no product or test change.

- **WorkerProcessExit whole-file review closed (2026-08-01):** all 179 lines reviewed at `c82d804` with process entry, server exit producers, both bootstrap implementations, and focused-test integration; tests passed 78/78. Accepted LOW `opr-52`; prior limited `a930e27` review expanded. No product or test change.

- **WorkerProcessExit `opr-52` intake (2026-08-01):** LOW accepted and plan-gated at `e13bb8a`: four bounded production protocol/bootstrap detail codes are absent from terminal normalization allowlists, so correct exit classes lose actionable identity, incarnation, containment-group, or handle-direction detail. Focused tests pass 78/78 but do not guard the four mappings. No product or test change.

- **SupervisorLifecycle complete-source record revalidated (2026-08-01):** all 139 byte-unchanged lines rechecked at `4306716` as the named subject with filter, registration, shutdown, lifetime, and recent focused-test integration; tests passed 21/21. Prior limited `2ac1cd4` plus dependency-level filter coverage promoted to explicit complete-source coverage. Opus found no current defect; no product or test change.

- **AuditSpoolRecordCodec whole-file review closed (2026-08-01):** all 127 lines reviewed at `9ac4960` with live/closed readers, sink recovery, scanner, envelope shape, and focused-test integration; tests passed 42/42. Prior limited `4c39b9f` review expanded; Opus found no current defect and no product or test change.

- **WorkerSession whole-file review closed (2026-08-01):** all 125 lines reviewed at `6e2c1d4` with runtime, worker server, construction, artifact capture/codec, and focused-test integration; tests passed 38/38. Existing MEDIUM `opr-4` was excluded without extension. Prior limited `5f2e1fb` review expanded; no additional distinct finding and no product or test change.

- **AuditSpoolSegmentIdentity whole-file review closed (2026-08-01):** all 116 lines reviewed at `1a0d80f` with scanner, checkpoint, writer, retirement, event-validation, and focused-test integration; tests passed 41/41. Production parse consumers gate output before use. Prior limited `b4ffe87` review expanded; Opus found no current defect and no product or test change.

- **AuditEvidenceOrphanReconciler whole-file review closed (2026-08-01):** all 101 lines reviewed at `a352bc2` with both active static startup callers, provider, protected spool, and focused-test integration; tests passed 11/11. Existing MEDIUM `opr-6` and LOW `opr-34` were excluded. Instance cadence API is dormant; static pre-writer proof is live. Prior limited `77a324e` review expanded; no additional current finding and no product or test change.

- **AuditAdminDispositionFailure whole-file review closed (2026-08-01):** all 89 lines reviewed at `cea2ff8` with active disposition administration, typed stage/effect classification, and focused-test integration; tests passed 22/22. Verified `s2-admin-disposition-failures` remained intact and was excluded. Prior limited `888914d` review expanded; no current finding and no product or test change.

- **WorkerLaunchCommand whole-file review closed (2026-08-01):** all 68 lines reviewed at `a565184` with command factory, both platform launchers, and focused-test integration; tests passed 14/14. Existing MEDIUM `opr-29` was excluded after its Unix launcher repair boundary was recorded separately. Prior limited `d847df2` review expanded; no additional distinct finding and no product or test change.

- **AuditOutputRequestProtector whole-file review closed (2026-08-01):** all 67 lines reviewed at `6b349b8` with metadata capture, output-handle generation, and focused-test integration; tests passed 14/14. Repo-wide C# inventory found no production construction or capture caller, correcting the queued active-caller wording. Prior limited `a2c343f` review expanded. Opus found no current defect; no product or test change.

- **InvokeTool whole-file review closed (2026-08-01):** all 65 lines reviewed at `77aa9a9` with assembly registration, session seam, production adapter, schema, and runtime integration; focused tests passed 90/90. Prior limited `f0418b0` review expanded. Opus found no current defect; no product or test change.

- **AuditAdminFailure whole-file review closed (2026-08-01):** all 58 lines reviewed at `11bad9d` with evidence-admin classification, failure publication, and fault-injection integration; focused tests passed 20/20. Prior limited `618f007` review expanded. Opus found no current defect; no product or test change.

- **SupervisorCallFilter whole-file review closed (2026-08-01):** all 55 lines reviewed at `f1cf11d` with server registration, lifecycle/lease, shutdown, and cross-platform integration; focused tests passed 21/21. Prior limited `6675e37` review expanded. Opus found no current defect; no product or test change.

- **AuditEffectiveIdentity whole-file review closed (2026-08-01):** all 30 lines reviewed at `92a60aa` with audit-admin construction, event schema/serialization, and cross-platform integration; focused tests passed 55/55. Prior limited `c1d83e1` review expanded. Opus found no current defect; no product or test change.

- **BashExecutableIdentity whole-file review closed (2026-08-01):** all 26 lines reviewed at `9d5a443` with executable identity, production startup resolution, planner/runner, and focused-test integration; sequential tests passed 102/102. Prior limited `385db4c` review expanded to complete-source coverage. Opus found no current defect; no product or test change.

- **RawUsageCounter complete-source record revalidated (2026-08-01):** all 17 byte-unchanged lines rechecked at `10a3547` against both increment boundaries, state reporting, lifecycle, and focused tests; 6/6 passed. Prior `469959c` no-findings result remains valid, including atomicity and inert signed-wrap adjudication. Semantic inventory now accepts explicit complete-source wording, not only `whole-file` tokens. No product or test change.

- **AuditStartupConfiguration whole-file review closed (2026-08-01):** all 71 lines reviewed at `8648f37` with `PtkAuditAdmin`, options, checkpoint-reader, and test integration; focused tests passed 3/3. Existing MEDIUM `opr-5` remains the only finding. Post-record filename inventory has no missing production `.cs` basename, so coverage work now audits abbreviated records rather than trusting filename mentions. No product or test change.

- **ColdCommandResolution whole-file review closed (2026-08-01):** all 268 lines reviewed at `ca5384f` in two bounded passes and whole-file integration against planner, plan invariants, executable identity, focused tests, upstream source, and platform probes; tests passed 95/95. Accepted MEDIUM `opr-48`, `opr-49`, `opr-50`, and LOW `opr-51`; existing `opr-2` and refuted-as-defect `rbc-13` remained excluded. No product or test change.

- **ColdCommandResolution `opr-51` intake (2026-08-01):** LOW accepted and plan-gated: Windows target matching uses case-sensitive record equality despite platform-aware identity policy, so casing-only resolution changes spuriously no-start. No product or test change.

- **ColdCommandResolution `opr-50` intake (2026-08-01):** MEDIUM accepted and plan-gated: Windows drive-relative command names bypass the bare-name guard and resolve against server drive state instead of child location semantics. No product or test change.

- **ColdCommandResolution `opr-49` intake (2026-08-01):** MEDIUM accepted and plan-gated: Windows rooted or drive-relative PATH entries bind server process drive state instead of the audited child working directory. No product or test change.

- **ColdCommandResolution `opr-48` intake (2026-08-01):** MEDIUM accepted and plan-gated: Unix resolver tests the union of raw execute bits rather than real-identity `X_OK`, so cold prepare and commit can bind a PATH file PowerShell would skip. No product or test change.

- **BashProcessRunner whole-file review closed (2026-08-01):** all 803 lines reviewed at `94ff698` in three bounded passes plus whole-file integration against containment, RTK parity, invoke models, dispatch, and production callers; focused tests passed 27/27. Accepted MEDIUM `opr-47`; extended existing `opr-4` and `opr-40`. No product or test change.

- **BashProcessRunner `opr-40` extension (2026-08-01):** Bash execution uses the same two eager 4 MiB capture allocations as direct RTK, roughly 8 MiB of large-object-heap storage before output. Accepted scope of the existing LOW plan-gated allocation-shape defect. No product or test change.

- **BashProcessRunner `opr-4` extension (2026-08-01):** Bash pre-start budget classification can combine `TimedOut=true` with cancellation audit detail because the guard snapshots cancellation but `BudgetFailure` re-reads the deadline. Accepted LOW scope of the existing MEDIUM plan-gated immutable-cause defect. No product or test change.

- **BashProcessRunner `opr-47` intake (2026-08-01):** MEDIUM accepted plan-gated: a slow successful validator-start audit flush can consume the fixed `bash -n` process budget and replace an already-determinate exit verdict with `TimedOut`. No product or test change.

- **AuditJournal whole-file review closed (2026-08-01):** all 897 lines reviewed at `54822ee` in three bounded passes plus whole-file integration against serializer, health, factory, live-reader, and production caller evidence; focused tests passed 59/59. No additional distinct finding; direct `opr-35` and `opr-36` remain unchanged. No product or test change.

- **WindowsProcessTreeSupervisor whole-file review closed (2026-08-01):** all 621 lines reviewed at `32c5748` in three bounded passes plus whole-file integration against native implementation, authority, and production callers; focused tests passed 85/85. No additional distinct finding; existing `opr-23` and the separate `rbc-5` boundary remain unchanged. No product or test change.

- **UnixWorkerProcessLauncher re-review closed (2026-08-01):** all 1,207 lines reviewed at `431183f` in four bounded passes plus whole-file integration against broker, registry, authority, and production callers; focused tests passed 20/20. No additional distinct finding; existing `opr-14`, `opr-24` through `opr-29`, and `opr-46` remain unchanged. No product or test change.

- **UnixWorkerContainmentRegistry re-review closed (2026-08-01):** all 515 lines reviewed at `afbf64f` in two bounded passes plus whole-file integration; focused tests passed 12/12. One current LOW finding, `opr-46`, was accepted; lifecycle/API candidates were rejected as reference-safe or production-unreachable. No product or test change.

- **UnixWorkerContainmentRegistry `opr-46` intake (2026-08-01):** LOW accepted and plan-gated: PID reuse between identity and group probes can latch false escape and transiently report `descendants_unknown`; exact background proof still releases registry. No product or test change.

- **SessionWorkerClient re-review closed (2026-08-01):** all 888 lines reviewed at `c8e6c4e` in three bounded passes plus whole-file integration; focused tests passed 80/80. No additional distinct finding; existing HIGH `opr-19` and LOW `opr-22` gained scoped repair/guard extensions. No product or test change.

- **SessionWorkerClient `opr-22` scope extension (2026-08-01):** existing LOW finding now covers both directions of late wall-clock classification: first-use delay can label timeout canceled, while successful cleanup crossing deadline can label caller cancellation timed out. No product or test change.

- **SessionWorkerClient `opr-19` scope extension (2026-08-01):** existing HIGH finding now also requires stop repair to coordinate prompt worker exit with `_stopping` and preserve containment on every failed graceful exchange. No product or test change.

- **TrustedPreflightClassifier re-review closed (2026-08-01):** all 448 lines reviewed at `864cfe2` in two bounded passes plus whole-file integration; focused tests passed 88/88. Exact runtime probes admitted HIGH `opr-43`, MEDIUM `opr-44`, LOW `opr-45`; one invalid-dual-dialect candidate was rejected. No product or test change.

- **TrustedPreflightClassifier `opr-45` intake (2026-08-01):** LOW accepted and plan-gated: nested local definitions are flattened across script-block scopes, suppressing valid top-level Bash builtin evidence that PowerShell cannot resolve. No product or test change.

- **TrustedPreflightClassifier `opr-44` intake (2026-08-01):** MEDIUM accepted plan-gated: named Bash options after `set -o` are missed unless the literal is `pipefail`, causing valid Bash to fail under PowerShell `Set-Variable`. No product or test change.

- **TrustedPreflightClassifier `opr-43` intake (2026-08-01):** HIGH accepted plan-gated: fatal PowerShell parse handling returns before recovered trusted Bash command evidence is scanned, so valid Bash can fall through to PowerShell failure. No product or test change.

- **NamedSessionSupervisor re-review closed (2026-08-01):** all 1,231 lines reviewed at `ca7fe85` in three bounded passes plus whole-file integration; focused tests passed 36/36. One current finding, MEDIUM `opr-42`, was accepted in the active `WorkerSupervisor` caller; all supervisor-local candidates were rejected. No product or test change.

- **NamedSessionSupervisor integration intake `opr-42` (2026-08-01):** MEDIUM accepted plan-gated: `WorkerSupervisor.StateAsync` releases the session lease, then uses two registry snapshots; concurrent removal can fault, same-name replacement can mix incarnations, and registry change can tear the count. No product or test change.

- **DefaultSessionRuntimeFactory re-review closed (2026-08-01):** all 44 lines reviewed at `c5f9536` with active callers and protocol validation; focused tests passed 26/26. One candidate merged into existing MEDIUM `opr-10`; no distinct new finding and no product or test change.

- **DefaultSessionRuntimeFactory `opr-10` extension (2026-08-01):** finite fractional or sub-millisecond positive timeout values violate the downstream whole-second protocol contract and abort startup. Merged into existing MEDIUM `opr-10`; no new finding ID and no product or test change.

- **RtkProcessRunner re-review closed (2026-08-01):** all 442 lines reviewed at `2debaf6` in two bounded passes plus whole-file dispatch integration; focused tests passed 76/76. Outcomes: LOW `opr-40`, MEDIUM `opr-41`, LOW extension to existing MEDIUM `opr-4`, and Opus-clean comment correction `e83c209`. No runtime behavior or test change.

- **RtkProcessRunner `opr-41` intake (2026-08-01):** MEDIUM accepted plan-gated: a canceled or expired RTK dispatch can start no process yet persist a fabricated zero `$LASTEXITCODE`; the reset pipeline can also replace prior `$?` with success. No product or test change.

- **RtkProcessRunner `opr-4` extension (2026-08-01):** pre-start budget classification can combine `TimedOut=true` with a cancellation audit detail because the decision snapshots cancellation but re-reads the deadline. Accepted LOW as the existing immutable-cause defect; `opr-4` remains MEDIUM and plan-gated. No product or test change.

- **RtkProcessRunner review intake (2026-08-01):** `opr-40` LOW accepted and plan-gated: every direct RTK execution eagerly allocates two 4 MiB capture buffers, roughly 8 MiB of large-object-heap storage before any output is read. No product or test change.

- **OutputRootLease current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 433 lines in two bounded passes plus one whole-file active-caller integration pass; focused tests passed 27/27. One distinct LOW finding, `opr-39`, was accepted; verified `opr-3` was excluded and a closed-descriptor candidate was rejected. No product or test change.

- **ChildStdinGuard current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 68 lines in one complete source/startup-caller/test pass; focused tests passed 22/22. Outcome `no_additional_current_findings`; existing `opr-1`, `opr-8`, and verified `rbc-1` overlap were excluded. No product or test change.

- **ScriptEvidenceStore current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 1,613 lines in eight bounded passes plus three thematic integration passes; focused tests passed 89/89. Outcome `no_additional_current_findings`; verified `s2-admin-evidence-failures` and existing `opr-35` overlap were excluded, and nine pass candidates were independently rejected after current-call-path adjudication. No product or test change.

- **ScriptEvidenceStoreProvider current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 243 lines in three bounded source/contract/test passes plus one current-production call-graph pass; focused tests passed 50/50. Outcome `no_additional_current_findings`; existing `opr-6` was excluded, and four pass candidates were independently rejected as current-inert or production-unreachable. No product or test change.

- **AuditSpoolQuotaLease current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 254 lines in two bounded passes plus one active-caller integration pass; focused tests passed 10/10. Outcome `no_additional_current_findings`; existing `opr-7` was excluded. No product or test change.

- **AuditEvidenceSpoolScanner current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 731 lines in four bounded passes plus one active-caller integration pass; focused tests passed 31/31. Outcome `no_additional_current_findings`; existing `opr-34` and `opr-35` overlap was excluded. No product or test change.

- **AuditCallMetadata current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 710 lines in four bounded passes plus one production-reachability pass; focused tests passed 14/14. Outcome `no_additional_current_findings`; `AuditCallMetadataCapture.TryCapture` has no production caller after intentional runtime-audit removal — only tests and its own definition remain — and existing `opr-11`, `opr-12`, and verified `s2-job-id-audit-poison` were excluded. No product or test change from this review.


- **SessionRuntime current-head full-file re-review complete (2026-08-01):** Claude Opus 5 reviewed all 491 lines in four bounded passes plus one production-call-graph pass; focused tests passed 109/109. Outcome `no_additional_current_findings`; existing `opr-18` was excluded and a factory-only `opr-10` rediscovery was rejected. No product or test change.

- **FileAuditJournalSink full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,707 lines in eleven bounded passes plus four thematic cross-boundary passes; focused tests passed 50/50. Outcome `no_current_findings`; two same-user substitution candidates and one Linux raw-descriptor lifetime candidate were rejected against the quota call graph, threat boundary, and sole caller lifetime. No product or test change.

- **SecureAuditStorage full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,619 lines in seven bounded passes plus two split cross-boundary passes; focused tests passed 15/15. Outcome `no_current_findings`; Windows durability-seam and macOS ACL candidates were rejected on exact semantics, caller ordering, and threat boundary. No product or test change.

- **AuditEvent full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,227 lines in five bounded passes plus two split cross-boundary passes; focused tests passed 13/13. Active admin and journal event validation/serialization is clean; dormant call-context paths were excluded. No product or test change.

- **AuditOperatorDispositionOutcome full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,110 lines in five bounded passes plus two split cross-boundary passes; focused tests passed 22/22 and 13/13. Outcome `no_current_findings`; no product or test change.

- **AuditOperatorDispositionIntent full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,057 lines in five bounded passes plus cross-boundary adjudication; focused tests passed 22/22. Outcome: one current finding, `opr-38` LOW, already recorded plan-gated; no product or test change.

- **Review intake:** `opr-38` LOW accepted plan-gated: `AuditOperatorDispositionProof` accepts one trailing LF in an acknowledged-gap reason, durably creating an out-of-grammar proof whose clean spelling conflicts on retry.

- **AuditOptions full review complete (2026-08-01):** Claude Opus 5 reviewed all 208 lines plus the complete production call graph; focused baselines passed 10/10 and 3/3. Outcome `no_current_findings` beyond existing `opr-5`; the newline-permissive options regex is unreachable behind the checkpoint codec's strict identity validation. No product or test change.

- **AuditCompletedChainRetirement full review complete (2026-08-01):** Claude Opus 5 reviewed all 933 lines in five bounded passes plus cross-boundary adjudication; focused retirement tests passed 13/13 and preparation tests 22/22. Active `RecoverUnderQuota` is clean; removed exporter initiation remains dormant. No product or test change.

- **AuditExportCheckpointStore full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,659 lines in seven bounded passes plus cross-boundary adjudication; focused tests passed 40/40. Outcome `no_current_findings`; removed exporter-only block/retry paths require re-review if reactivated. No product or test change.

- **AuditAnchoredWriterPreparation full review complete (2026-08-01):** Claude Opus 5 reviewed all 493 lines in three bounded passes plus cross-boundary adjudication; focused tests passed 22/22 and 13/13. Outcome: one current finding, `opr-37` HIGH, already recorded plan-gated; no product or test change.

- **Review intake:** `opr-37` HIGH accepted plan-gated: crash-truncated canonical initial-checkpoint temporary is non-authoritative but rejected by exact-byte-only recovery, permanently denying anchored and administration startup.

- **AuditAdminOperations full review complete (2026-08-01):** Claude Opus 5 reviewed all 1,124 lines in five bounded passes plus cross-boundary integration at `bfd571e`; evidence-access tests passed 20/20 and disposition tests 22/22. Two candidates were rejected by exact ownership and mutation-tested publication semantics. Outcome `no_current_findings`; no product or test change.

- **Windows scheduler cancellation recurrence is closed:** the earlier
  drain-based repair `8588374` remained vulnerable because the test's disposable
  token callback could be removed during cancellation unwind. Test-only repair
  `9e66a35` witnesses cancellation in the operation's own unwind path before
  drain; Opus accepted the exact SHA with `guard_confirmed=true`. PR #31 run
  `30703723050` passed all six jobs and merged as `c9e5a44`.
- **GitHub issue #12 is closed as superseded by the per-connection worker
  topology:** bounded Windows probes against both the source-built server and a
  self-contained `win-x64` apphost completed the full outer-worker → child
  client → inner-supervisor/worker → nested-`ptk_invoke` path in 6.4/6.5
  seconds. Four distinct process identities were observed, nested output was
  `42`, and the outer session remained ready. Opus accepted closure; no code
  change or regression guard was warranted. Reopen only with exact current
  build identity and server/worker exit diagnostics from a recurrence.
- **GitHub issue #11 is closed after real-Codex transport-boundary validation:**
  installed `0.2.0-dev.g12e1ff5` recovered a path-verified killed worker on the
  same MCP connection, reported `warm_state_lost`, and invoked through a new
  worker. Killing the path-verified public supervisor made the old client fail
  immediately with `Transport closed`; a fresh Codex session launched a new
  supervisor and invoked successfully. Opus accepted the qualified worker/public
  boundary docs; PR #26 passed all six hosted jobs and merged as `3b570ff`.
- **GitHub issue #10 is closed as resolved by retiring the audit startup gate:**
  installed `0.2.0-dev.g12e1ff5` initialized with `PTK_AUDIT_ROOT` beneath a
  regular file, reported audit disabled before and after reset, invoked, reset,
  created no audit root, and exited gracefully. The current startup guard passed
  1/1 and the hosted handshake diagnostic remains green. Opus accepted closure
  with `guard_confirmed=true`; reopen only on current-build audit gating.
- **GitHub issue #9 is closed after exact-client validation of both reported
  stages:** current Claude Code path-verified and killed its installed public
  supervisor, then transparently restarted PTK and returned `ptk_state` promptly
  on one post-kill call. Current Codex instead failed immediately with
  `Transport closed` and succeeded from a fresh session; neither client hung.
  The audit wedge is the retired gate closed under #10. Opus accepted closure;
  the original older-client observation remains valid history.
- **GitHub issue #3 is closed after current-product reconciliation:** shell
  dialect guidance, cross-platform warm-state isolation, 300-second default
  timeout/recovery, and an actionable live missing-command diagnostic cover its
  ergonomics items. The permission proposal is resolved by the documented
  non-authorization-boundary/client-prompt posture and the owner's explicit
  rejection of the bypassable policy-file gate. Opus accepted closure.
- **HISTORICAL — superseded 2026-07-30: Non-disruptive side-by-side upgrade plan
  drafted at `5f5d00d`; valid Claude
  Opus 5 openreview intake is complete.** `.agents/plans/mcp-side-by-side-upgrade.md`
  records the captured retry over `c4bd2af..caf467e`: ten candidates, eight
  admitted and two declined, with one admitted metadata finding already
  resolved. The owner settled `ssu-1` on 2026-07-29: managed registrations use a
  native self-contained stable launcher and retain the no-`pwsh` distribution
  contract. The owner also settled `ssu-3`: migration is transactional and
  harness-specific; Codex avoids its unsafe remove command, Grok requires a
  disposable-config CLI proof, and every changed harness rolls back
  byte-for-byte on failure. The owner settled `ssu-4`: install-home `~/.ptk/launcher/` is never a
  wholesale payload entry or removed after registration; launcher changes use
  validated sibling-file replacement and fail closed if the stable path cannot
  remain continuously present. The owner settled `ssu-5`: Windows uses
  handle-based `FileRenameInfoEx` replacement with delete-sharing readers and no
  delete-first fallback; Unix uses `rename(2)` plus a parent-directory flush.
  The owner settled `ssu-6`: install/reuse performs full runtime hashing, while
  normal launch reads at most 266240 control-file bytes and never enumerates or
  hashes the payload tree. One other plan/product finding and one citation
  correction remain open at
  `.agents/review/mcp-side-by-side-upgrade-opus5-r2.md`. The next owner gate is
  `ssu-7`. Implementation is not authorized. Codereview remains deferred until
  Slice 0 has an implemented fix and deterministic revert-fails/restore-passes
  guard proof.

- **HISTORICAL — superseded 2026-07-30: MCP upgrade-continuity openreview
  completed on 2026-07-29; owner ruling
  pending.** Claude Opus 5 at max effort reviewed
  `d32f2a06e451df02cfa9b63d5c0dda570d1262ec..a308bdadbbda954340f48d63772e72787e9f6990`.
  Intake admitted five findings and declined one overstatement; see
  `.agents/review/mcp-upgrade-continuity-opus5-r1.md` and `muc-1` through
  `muc-6` in `.agents/review/index.md`. Current recommendation is
  side-by-side immutable versions below the single `~/.ptk` home, with active
  connections left on their pinned old version until natural exit. Same-client
  transparent adoption remains unsolved and would require an explicit owner
  reversal before reopening the discarded guardian or daemon architectures.

- **Windows enterprise validation landed local fixes `36146a1` and `56b562c`
  on `ASHBIAMWEB1` (2026-07-29).** Hosted workers now discover modules beside
  installed `pwsh`, and Windows runspaces now use STA for COM automation. The
  real exact-account EXO metadata read and warm-session reuse passed; its
  explicitly selected values were retained and the remaining incomplete marker
  truthfully denotes uninspected type data. Graph never authenticated or read
  API data. STA removed the Outlook COM hang, but the local Outlook profile has
  no current user, so no Inbox item was read. No candidate was installed during
  those field probes and no probe process was left running; detailed host
  evidence is in `.agents/machines.md`.
- **Exact-head Windows x64 PTK runtime/package acceptance passed at
  `7eaf8a0cbe391abda7185e23e621fe7b01028886` on `ASHBIAMWEB1`
  (2026-07-28).** Direct Windows execution found and separately committed
  acceptance witness startup/CRLF defect `6ccdaa7` and real worker
  initialization deadline-classification race `7eaf8a0`. The final head
  passed 1,212/1,212 server tests, 144 Pester tests with one platform skip,
  direct and packaged handshakes, disposable staged activation, and packaged
  100-cycle worker replacement/Job Object timeout/crash/cleanup acceptance.
  The ordinary token still cannot create Windows symlinks, so the independent
  SIEM suite remains 226/247 with all 21 failures during privileged link
  setup before product assertions. No candidate was installed or registered;
  host evidence and package hashes are in `.agents/machines.md`.
- **Windows installer ACL repair landed locally at `bb2349a` (2026-07-28).**
  The install transaction now normalizes its Windows payload root before staged
  validation to a protected, current-user-only, inheritable full-control ACL.
  This prevents a sandbox-created non-inheriting `~/.ptk` DACL from making
  installed `~/.ptk/bin/` unreadable after activation. The regression failed with the
  exact access denial when the fix was removed and passed after restoration;
  the live ACL-only repair restored `ptk_invoke`. Installed payload bytes were
  not replaced and nothing was pushed. Host evidence is in
  `.agents/machines.md`.
- **Handoff checkpoint (2026-07-28): all currently available Linux x86_64
  acceptance is complete; the remaining Windows and enterprise gates are
  separate.** Exact committed head
  `37b7d94dacdfd7fb03b52ce95d4409031e6e6699` passed Pester on `gabrielle`
  and the full server, SIEM, dependency-audit, native-package, handshake, and
  100-cycle staged-package production-acceptance battery on `magneto`.
  Evidence is committed at `14daad6`; the read-only installed-payload rollback
  baseline is committed at `3f5dc11`. All disposable validation roots were
  removed, and nothing was installed, registered, deployed, or pushed. The
  current Codex installation remains the old `0.2.0-dev.g6db333c` job-API
  build, not the validated five-tool named-session candidate. `NETWATCH-01`
  can close only generic Windows packaging/process/Job Object acceptance; it
  is a personal gaming machine with no company AD, on-prem Exchange, Exchange
  Online, or Outlook administration access. Those real workflow gates require
  a separate company-connected supported Windows admin host with the required
  modules, network access, and authentication.
- **Exact-head staged-package acceptance now also passes on macOS ARM64.**
  Committed head `920944ef3a4491edbf1d2c6a3915a5e39106a8fb`
  produced genuine Mach-O ARM64 `PtkMcpServer` and `PtkWorkerBroker`
  executables, passed the five-tool staged handshake, and passed the complete
  100-cycle production-acceptance matrix from the packaged executable. Process
  and file-descriptor counts remained 5/5 and 531/531 across all replacements;
  timeout, direct worker death, process-group escape, and supervisor hard-kill
  containment all passed. No process remained, the disposable root was
  removed, and nothing was installed or registered.
- **One immutable Linux x86_64 package now passes on all three available Linux
  hosts, including both hosts without a .NET SDK.** `magneto` built and
  validated the exact `d243c82d135b390b69fd93b25d01135f4d791a58`
  package; `gabrielle` and `altiera` independently verified the same source
  and layout hashes, confirmed the SDK was absent, and passed both the complete
  public handshake and 100-cycle production acceptance from those package
  bytes. Each host contained timeout and direct-kill descendants, refused the
  process-group escape, preserved the sibling, and removed all descendants
  after supervisor hard-kill. No package process remained, all disposable
  roots were removed, and nothing was installed or registered.
- **Production-reliability Slice 11 is complete and integrated on local
  `master`.** Exact documentation/integration commit
  `2c96e842618d73ac59fa37d5492a5ae92a0d163d` publishes the implemented
  one-supervisor/many-session-workers contract, reduces audit documentation to
  retained administration/receiver boundaries, marks the discarded guardian
  plan as historical, and reconciles the release-plan pointer. Verification
  found that build-on-launch `dotnet run` wrote NuGet warnings onto MCP stdout;
  the checkout path now builds first and launches with
  `--no-build --no-launch-profile`. The original registration handshake failed
  on that non-JSON stdout and the corrected exact command passed the complete
  five-tool/named-session handshake. Pester passed 141 with two platform skips,
  the server suite passed 1,212/1,212, and the retained SIEM receiver passed
  247/247. Current `origin/master`
  `c9b11bcb0b4e41a11110c5870562b4980c0b86b3` was the exact merge base; local
  `master` was fast-forwarded and its content diff against the verified salvage
  branch was empty. Nothing was pushed, installed, registered, or deployed.
- **The five high-severity XML-crypto dependency advisories are closed locally
  at exact commit `b6fcbcdd9a81bdd5ce9e9ba8dde087a2adc02ff3`.**
  `Microsoft.PowerShell.SDK` 7.6.3 still requests vulnerable
  `System.Security.Cryptography.Xml` 10.0.6, so the server now directly pins
  patched 10.0.10. The transitive vulnerability audit changed from five
  advisories on every server project to no vulnerable packages; the complete
  Pester/server/SIEM/handshake battery and 100-cycle production acceptance
  passed with zero build warnings. A validated self-contained layout resolved
  only 10.0.10 and passed the complete public handshake. A replacement-server
  diagnostic also preserved all values on an EXO-style selected/deserialized
  object, evaluated an explicitly selected script property exactly once, and
  surfaced a terminating error's message. This is useful synthetic evidence,
  not a substitute for the pending real EXO/Outlook workflow on Windows.
- **Cross-RID package generation now fails closed at exact commit
  `e2beda3c35b6bc4d1a6b1d0191e43ed9957a19f0`.** A macOS probe of the advertised
  `linux-arm64` layout path found a Mach-O ARM64 `PtkWorkerBroker` inside the
  package rather than a Linux ELF binary. PTK has no proved cross-target native
  compiler contract, so `dev-install.ps1 -LayoutOnly` now refuses a target RID
  that differs from the build host before creating the output directory. The
  new guard failed when the check was removed and passed when restored; Pester
  passed 142 with two platform skips, server 1,212/1,212, SIEM 247/247, the
  direct-checkout handshake, and a same-RID self-contained package handshake.
  Exact committed head `37b7d94dacdfd7fb03b52ce95d4409031e6e6699`
  subsequently passed the complete Linux x86_64 battery on real hosts: Pester
  passed 142 with two platform skips, server passed 1,212/1,212, SIEM passed
  247/247, the transitive vulnerability audit was clean, direct and
  staged-package handshakes passed, both public executables were genuine x86-64
  ELF binaries, and the 100-cycle production-acceptance matrix passed. This
  prevents a mislabeled release artifact and proves the matching-host
  `linux-x64` path; it does not replace the pending build and execution on a
  real ARM64 Linux host. UTM is not used.
- **Production-reliability salvage Slice 0 is recorded on
  `impl/production-reliability-salvage`.** The unchanged product base is exact
  `origin/master` commit `c9b11bc`; only the reviewed plan/review records sit
  between that product tree and the implementation branch head. The Pester
  suite passed 141 tests with two platform skips, the SIEM suite passed
  247/247, the handshake passed, and the third full server run passed
  1,587/1,587. The first two full server runs exposed three different
  intermittent failures that each passed immediately in isolation; they remain
  recorded production blockers rather than being normalized away. Two
  independent ephemeral Codex agents received distinct PTK server PIDs `9595`
  and `9596`, proving the production harness gives them separate MCP server
  processes/connections. No product file changed during the baseline.
- **The owner-approved Slice 1 R0 retirement is implemented on
  `impl/production-reliability-salvage`.** The frozen guardian/private-host
  contract, recovery/package schemas and examples, native acquisition
  inventory, `PtkResilienceTestFixture`, and its two consuming test classes are
  removed with their project references. The retained audit/SIEM vectors now
  have a smaller `AuditInteropContractTests` guard, and
  `ToolSchemaConformanceTests` freezes the exact live five-tool direct-server
  surface. The real `PtkContainmentTestFixture` and both Windows containment
  test files are byte-identical to the base. Focused contract/schema tests,
  Pester, SIEM, formatting, and the stdio handshake pass. Three bounded
  post-change server-suite attempts each passed 1,556/1,557 but hit a different
  already-isolated timing failure. Diagnosis then proved that none of the five
  implicated runtime or test files differs from the product base, all five
  implicated classes pass together under default parallelism, and the complete
  suite passes 1,557/1,557 when xUnit collections are serialized. A paired
  default-parallel full run also passed 1,557/1,557, confirming intermittent
  load sensitivity. The 16-logical-CPU Mac was under unrelated load averages
  around 24/45/47, including a long-running Headroom proxy using about two
  cores; no PTK, pwsh, testhost, or sleep-process leak remained. Slice 1a is
  now implemented with test-runner JSON and no timeout or product change. The
  reviewed assembly attribute was discarded after the exact runner and an
  isolated reproduction both ignored it; the runner JSON setting proved
  effective and remains explicitly overrideable. Three consecutive ordinary
  server runs passed 1,557/1,557, followed by green Pester, SIEM, and stdio
  handshake checks. This removes invalid cross-collection testhost contention;
  it does not erase the anchored-evidence ordering race, fixed watchdog residue,
  carried Windows failures, or the lack of current cross-platform CI evidence.
  Product decisions 3-4 and any `ci/**` push or PR remain independently gated.
  Exact machine evidence is in `.agents/machines.md`.
- **Production-reliability salvage Slice 2 is complete in the local
  checkout.** Ordinary invoke no longer opens or depends on audit storage;
  `ptk_state` reports audit disabled. `WorkerSupervisor`,
  `SupervisorLifecycle`, and `SupervisorCallFilter` now own admission,
  cancellation/drain, and ordered runtime shutdown without the idle watchdog.
  The anchored OTLP producer, its runtime protobuf dependencies, and its
  producer-to-SIEM conformance surface are removed; the relocated receiver
  proto, standalone SIEM, legacy `PtkAuditAdmin`, and local legacy-state
  primitives remain. The runtime installer excludes `PtkAuditAdmin`. Exact
  candidate tree `faf8c2338a1a729fc5c46d87376475265148a5b0` passed the full
  macOS ARM64 battery and a clean Linux x86_64 battery over SSH on `magneto`.
  A deliberate startup regression made the unwritable-audit-root integration
  guard fail, byte-exact restoration made it pass, and the runtime-package
  boundary guard was separately proved red-to-green. ARM64 Linux is untested;
  UTM is not to be used. Exact evidence is in `.agents/machines.md`.
- **Production-reliability salvage Slice 3 is complete locally at code head
  `bfa335ec223609df9ce0dbfcfd9efe99382203d4`.** The prepared
  prepare/descriptor/commit/abort protocol, cold-job worker codec, and their
  staging-only tests are removed. One strict v2 worker protocol now has only
  initialize/ready, foreground invoke, nonblocking state, targeted cancel,
  bounded artifact transfer, one terminal, and shutdown/stopped messages.
  Request IDs are monotonic, stale session/incarnation frames and unsolicited
  terminals are fatal, frames and logical fields are bounded, and invalid
  worker-owned output is classified as a runtime failure rather than blaming
  supervisor input. A real unwired fixture proves warm variables, prompt busy
  state, cancellation with one terminal, and isolation between two differently
  identified worker servers. Deliberate mutations proved the message union,
  no-background boundary, artifact length/digest, capacity, shutdown
  correlation, warm-state, cross-routing, and unsolicited-terminal guards.
  The exact commit passed 1,223/1,223 server tests, Pester 141 with two platform
  skips, SIEM 247/247, formatting, and the public handshake on macOS ARM64, then
  the same behavior battery in a clean disposable checkout on Linux x86_64
  `magneto`. Public MCP behavior is unchanged. The slice is not pushed or
  installed; exact evidence is in `.agents/machines.md`.
- **Production-reliability salvage Slice 4 is complete locally at code head
  `75505b98021229efebc338597d38450059da9294`.** Disposable workers now use
  the worker-only Unix broker or Windows creation-time Job Object containment,
  with independent per-worker teardown and unchanged public MCP behavior.
  Direct Windows validation found and repaired four concrete issues: Job Object
  termination now retains the handle until emptiness is proved; route-consent
  testing no longer depends on host aliases; a failed kill request cannot kill
  the root through descendants-only cleanup; and a wedged first containment
  request cannot race away the bounded observer's independent retry. The last
  retry guard passed 30 consecutive Windows runs. Exact-head full batteries
  passed on macOS ARM64, Linux x86_64 `magneto`, and Windows x64 `NETWATCH-01`:
  server 1,240/1,240, Pester 141 passed with two platform skips, SIEM 247/247,
  and the full stdio handshake. No test process survived and every disposable
  remote checkout was removed. The slice is not pushed or installed; exact
  evidence is in `.agents/machines.md`.
- **Production-reliability salvage Slice 5 is complete locally at code head
  `001c4ebe23d48e1804f2bb169f09a8f0e0c0dd2a`.** One MCP connection now owns
  a fixed maximum of eight internal named-session slots, including lazy
  `default`, with one isolated contained worker process and warm runspace per
  session. Lifecycle admission, shared startup, per-session foreground
  serialization, automatic timeout/crash/transport recovery, confirmed-empty
  replacement, cached PID health, sealed-output survival, close/reset, and
  concurrent connection shutdown are covered behind the internal fixture.
  The real-process guard proves overlapping commands, variables, modules,
  environment, directories, and processes remain isolated and that workers
  inherit the supervisor's launch directory. Deliberate mutations proved the
  launch-directory, containment-proof, stopped-worker PID, late-start
  shutdown, and invoke/state transport-recovery guards. Exact-commit batteries
  passed on macOS ARM64, Linux x86_64 `magneto`, and Windows x64
  `NETWATCH-01`: server 1,264/1,264, SIEM 247/247, platform-appropriate Pester
  counts, and the public stdio handshake. No candidate process or disposable
  remote checkout survived. The public MCP surface remains unchanged; the
  slice is not pushed or installed. Exact evidence is in
  `.agents/machines.md`.
- **Production-reliability salvage Slice 6 is locally implemented and committed
  at code head `6c9bdacc84685af54055b22627666bfb8231c2d1`; Windows validation
  remains pending.** Production now exposes exactly `ptk_invoke`,
  `ptk_output`, `ptk_reset`, `ptk_session`, and `ptk_state`. Cold jobs and the
  in-process runspace path are gone; every command executes only in the
  explicitly resolved named session's contained worker, while reset, state,
  lifecycle, and sealed output remain supervisor-owned. Twelve deliberate
  guard mutations failed independently and were restored byte-for-byte. The
  exact commit passed 1,167/1,167 server tests, Pester 141 with two platform
  skips, SIEM 247/247, the public handshake, formatting, and package smoke on
  macOS ARM64; the same server/Pester/SIEM/handshake battery passed from the
  verified archive on Linux x86_64 `magneto`. `NETWATCH-01` was unreachable at
  both its current DNS address and its previously used address, so Windows x64
  remains pending. No candidate Linux process survived. The slice is not
  pushed or installed; exact evidence is in `.agents/machines.md`.
- **Production-reliability salvage Slice 7 is locally implemented and committed
  at code head `51594735e40c6d50a2aaf94c84a9e55a70f63b50`; macOS ARM64 and
  Linux x86_64 are verified, and Windows x64 remains pending.** It classifies
  failure before the first invoke pipe-write call as `not_started` and every
  failure after write-call entry and before a complete terminal as
  `outcome_unknown`. A complete terminal remains authoritative; recovery makes
  one automatic replacement attempt; a failed replacement projects
  `reset_required=true`; and sibling and separate-supervisor isolation remain
  intact. Six deliberate regressions failed for their intended reason and
  every source was restored byte-for-byte. Exact-commit batteries passed on
  macOS and Linux: server 1,180/1,180, SIEM 247/247, platform-appropriate
  Pester counts, and the public stdio handshake; macOS additionally passed
  scoped formatting and self-contained publish smoke. No candidate process or
  disposable Linux checkout survived. `NETWATCH-01` timed out again after the
  Linux run. The local Slice 6 and Slice 7 archives remain available for
  Windows validation. The owner directed continued local implementation
  without waiving either Windows gate. The slice is not pushed or installed;
  exact evidence is in `.agents/machines.md`.
- **Production-reliability salvage Slice 8 is locally implemented and committed
  at product code head `4270487c891d3c2cb0976f25eb3082f90c7ac630`;
  test-only guard descendant `4779cb2f7000306cc11f24fe017a4671b5b16cbf`
  closes a mutation-discovered late-publication test gap. macOS ARM64 and Linux
  x86_64 are verified; Windows x64 remains pending.** Optional recovery now
  reserves full per-invocation quota against immutable session identity before
  dispatch, uses one fixed capture buffer and one bounded connection-wide
  storage lane, validates the complete worker artifact protocol while
  discarding, publishes only immutable complete handles, and safely reclaims
  abandoned owned output roots without touching live roots. Fifteen deliberate
  production regressions failed for their intended reasons and were restored;
  the restored focused battery passed 82/82 and the complete server suite
  passed 1,197/1,197 on the test-hardened macOS head. The product commit also
  passed Pester 141 with two platform skips, SIEM 247/247, formatting, public
  built and published handshakes, and ARM64 self-contained publish smoke on
  macOS; its verified archive passed the full server/Pester/SIEM/handshake
  battery on Linux x86_64 `magneto`. No candidate process or disposable Linux
  checkout survived. The slice is not pushed or installed; exact evidence is
  in `.agents/machines.md`.
- **Production-reliability salvage Slice 9 is locally implemented and committed
  at code head `46710afd2e90911f2402019ff9ab4ddc10695845`; macOS ARM64 and
  Linux x86_64 are verified, and Windows x64 remains pending.** The development
  installer now publishes only the single public supervisor/internal-worker
  package and required Unix broker, runs the complete public handshake before
  and after activation, snapshots the installer-owned payload and known harness
  registrations, changes registrations last, and restores and verifies exact
  bytes plus Unix modes after activation or registration failure. Registration
  initialization runs as a checked child, so its deliberate `exit 1` cannot
  bypass rollback. Unconfirmed rollback retains a readable recovery manifest.
  Six deliberate regressions proved file restoration, recovery-snapshot
  retention and manifest, checked child registration, current named-session
  guidance, and same-named function isolation. The exact commit passed the
  complete Linux x86_64 battery on `magneto`: server 1,199/1,199, Pester 141
  with two platform skips, SIEM 247/247, built handshake, transaction matrix,
  local-RID layout publish/validation, and staged plus activated package
  handshakes. The matching macOS package smokes and behavior batteries passed;
  one exact-final full-suite repeat hit the already-recorded unchanged 500 ms
  Bash pipe-drain fixture under host load and passed immediately in isolation.
  No installed payload or real registration changed, and no candidate process
  or disposable Linux checkout survived. Exact evidence is in
  `.agents/machines.md`.
- **mini-SIEM S1-S3 are complete and incorporated on local `master`; the S3 durable
  store head is `eb51f2e` and its producer-conformance compatibility head is
  `9f53831`.** S1 supplies the solution skeleton and strict startup config; S2
  supplies the independently compiled canonical proto, bounded exact
  one-record validator, real Kestrel mTLS, and frozen response table. S3 replaces
  the fail-closed placeholder with a startup-migrated SQLite store: one
  serialized nondeferred transaction re-reads and conditionally advances the
  producer chain, stores exact raw request evidence, and atomically appends the
  versioned custody ledger under asserted WAL/FULL writer policy. Byte-identical
  replay is idempotent even after head advance; duplicate mismatch, chain
  failure, and strict-validator failure commit quarantine evidence before a
  permanent response; any commit/quarantine failure remains retryable and can
  never false-ack. The SQLite package's vulnerable native minimum is overridden
  to its patched bundle, and the dependency audit is clean. The isolated
  producer conformance project remains source-compatible and its ordinary fake
  receiver path remains unchanged. Producer-owned exact v1/v2 fixtures exist at
  `1f6d485`; the serialized v3 OTLP request fixture remains absent and is never
  invented from R0's JSONL vector. Local evidence and guard proofs are recorded
  in `.agents/machines.md`. Combined local verification of merge `374f164`
  passed on macOS, exact integration tip `1ad195e` passed direct Windows
  checkout validation, and exact snapshot `a473ca3` passed the direct Linux
  behavior battery after manual generation around an ARM64 MSBuild-only
  `protoc` crash; the exact commands, contexts, counts, and clean-build caveat
  are recorded there. Hosted run `29520427103` at `72c6103` passed all three
  SIEM jobs and the complete Ubuntu/macOS product batteries; its Windows
  server failure is addressed by the CI portability follow-up below.
- **Audited-harness Slices 7a-7f and the Windows wait-ownership prerequisite
  are complete and landed on local `master`; Slice 7f code head is
  `a9e757e`.**
  The strict bounded v1 worker protocol freezes all nine wire kinds, enforces
  strict UTF-8 NDJSON with duplicate/unknown/version rejection, caps encoded
  frames at 1 MiB and JSON depth at 32, preserves fragmented and coalesced
  input, serializes writes, latches ambiguous writer failure, and clears
  pooled script-bearing buffers. Claude Code 2.1.209 accepted exact range
  `a88d605..f86de26` with `guard_confirmed=true` after six independent
  mutations and the full battery. Code head `e70089f` then adds the
  behavior-preserving tool-to-provider operations seam and the separately
  disposable ordered lifetime seam; Claude accepted exact range
  `2eca287..e70089f` with `guard_confirmed=true` after four mutations and the
  full battery. Code head `56734e3` then adds the platform-neutral worker
  lifecycle core with strict boot/phase/correlation checks, deadline and host
  cancellation races, and exactly-once lifetime cleanup; Claude accepted
  exact range `cfaee5f..56734e3` with `guard_confirmed=true` after seven
  independent mutations and the full battery. Code head `bbc2a0e` then adds
  the deliberately unwired Windows creation-time containment primitive:
  exact `KILL_ON_JOB_CLOSE` Job Object configuration, one five-handle
  `CreateProcessW`/`STARTUPINFOEX` launch with `JOB_LIST` and `HANDLE_LIST`,
  closed Unicode environment, proof-only suspension, job-first rollback, and
  direct runnable/pre-resume/tree-death Windows fixtures. Claude accepted exact
  range `3348167..bbc2a0e` with `guard_confirmed=true` after seven mutations
  and the full battery; the exact tree also passed direct `NETWATCH-01`
  validation. Code head `d1cca1b` closes cancellable-wait ownership using a
  per-wait noninheritable duplicated process handle, with guards covering both
  the owning constructor and active async call path. The preliminary `5ea7d60`
  review was reopened after independent preflight found its active-path guard
  vacuous; only the corrected fixed-SHA acceptance is final. Code head
  `12617cc` then adds the Windows-only managed `--worker` lifecycle entry,
  exact bootstrap-variable removal and noninheritable pipe ownership, stable
  managed exit mapping, and bounded allow-listed abnormal diagnostics while
  leaving default MCP operations in-process. Independent preflight reopened
  preliminary head `e2cbfb5` for two guard vacuities; test-only commits
  `e9421cc` and `12617cc` close the concrete environment-removal and global
  diagnostic-destination gaps. Claude accepted exact range
  `eec7ed1..12617cc` with `guard_confirmed=true` after eleven independent
  mutations and the full battery; the exact tree also passed direct
  `NETWATCH-01` lifecycle and containment validation. Code head `a9e757e` then
  adds strict private request/cancel parsing, response encoding, and a bounded
  standalone scheduler with targeted cancellation, exactly-one terminal
  ownership, and fatal peer cleanup while remaining deliberately unwired from
  production.
  Claude accepted exact range `3580e67..a9e757e` with
  `guard_confirmed=true` after independent mutation proof and the full battery.
  Completion records are committed at `83ca3b1`; the accepted feature history
  was fast-forwarded to local `master`, content arrival was verified by direct
  branch diff, and the feature branch was removed. Canonical review and Windows
  evidence is in `.agents/review/index.md` and `.agents/machines.md`.
- **Audited-harness Slice 7g is complete and landed on local `master` at code
  head `eef38cb`.** It adds only strict transport-neutral value codecs for
  foreground invoke, job controls, and state, with strict logical UTF-8
  script/result limits. It does not bind invoke to ordinary request transport
  or add runtime execution, prepare/commit, background start, audit/output
  transfer, job-ID allocation,
  reset, proxy wiring, or MCP behavior. Claude Code 2.1.210 accepted exact
  range `a83e2e6..eef38cb` with `guard_confirmed=true` after ten independent
  mutations and the full battery. Completion records are committed at
  `8428f17`; the accepted feature history was fast-forwarded to local `master`,
  content arrival was verified by direct branch diff, and the feature branch
  was removed. Canonical evidence is in the audited-harness plan and
  `.agents/review/index.md`.
- **Audited-harness Slice 7h is complete and landed on local `master` at code
  head `8f5c57c`.** It adds only strict unwired prepare,
  prepared-correlation, commit, and abort values for foreground invoke. It
  freezes canonical UUIDv4 plan ID, exact strict-UTF-8 script digest, worker
  generation, and original absolute deadline correlation without adding
  reservation behavior, execution, the final prepared descriptor, audit/output
  transfer, background IDs, server wiring, or MCP behavior. Claude Code
  2.1.210 accepted exact range
  `1179ed0..8f5c57c` with `guard_confirmed=true` after eleven independent
  mutations and the full battery. Completion records are committed at
  `c07a958`; the accepted feature history was fast-forwarded to local `master`,
  content arrival was verified by direct branch diff, and the feature branch
  was removed. Canonical evidence is in the audited-harness plan and
  `.agents/review/index.md`.
- **The owner-approved two-layer MCP resilience sequence is implemented
  through every locally actionable R5 Windows barrier; the exact verified
  code/test head is `195e7e6`. R0-R4 are complete.** R5 now owns real Windows
  private-host launch, containment, monitoring, exact-generation restart,
  lifecycle audit, delivery truth, declared-state restoration, job/output
  tombstones, and recovery-aware state. Real apphost tests cover startup and
  replacement crashes, request/effect/response barriers, native descendant
  cleanup, idle persistence, lost jobs, and ambiguous reset requiring explicit
  repair. The complete repository battery and stdio handshake pass at that
  head; exact machine evidence is in `.agents/machines.md`. R5 is not
  cross-platform complete: production Unix still deliberately refuses startup
  until the approved native outer broker exists. The target keeps one public
  stdio guardian alive while it
  restarts an exact-version private host, and makes a healthy host replace an
  unexpectedly lost session worker. It never replays ambiguous work, changes
  generation on every replacement, recreates only frozen declared bootstrap
  state, and keeps guardian-local health/output surfaces usable. The canonical
  draft is `.agents/plans/mcp-resilience.md`; it narrowly supersedes the older
  explicit-restart target without weakening audit, containment, or session
  isolation. The owner additionally accepted the guardian crash boundary and
  fail-fast model-retry contract: no server queue or saved-state authoring tool
  in this stage; only proved-no-start errors direct the model to poll state and
  submit a new request, while `outcome_unknown` is never retried. The owner
  requested a fresh Claude Fable 5 maximum-effort review of that update.
  Claude Code 2.1.210 accepted the complete exact range
  `5ae154c..b4a2c0c` with `guard_confirmed=true` and no comments; its result
  metadata reported `claude-fable-5` plus CLI helper usage and no Opus model.
  Canonical review evidence is in `.agents/review/index.md`. For packaging,
  the owner confirmed nothing
  has shipped and only this development environment is in use, then delegated
  the choice: keep the current registration usable through R6, perform one R7
  cutover to the matched guardian package, and preserve no direct-server
  migration or compatibility layer. An eligible alias also recovers
  automatically to its fresh declared baseline after an execution-timeout
  terminal and confirmed old-tree death; the timed-out call is never replayed.
  Retryable recovery refusals carry phase, attempt, poll delay, and an exact
  readiness gate: delay expiry permits only `ptk_state`, and a fresh operation
  waits for readiness plus pre-write dispatch revalidation. After private
  write begins, the existing `outcome_unknown`/no-replay boundary still wins.
  Exact R0 verification and direct macOS/Windows evidence are recorded in
  `.agents/machines.md`. The in-repo independent audit found no R0 blocker.
  After the compression proxy was removed, Claude Code 2.1.211 routed directly
  to `claude-fable-5` at maximum effort and accepted exact range
  `215e10f..c1d809f` with matching SHAs and `guard_confirmed=true`. Its model
  metadata reported Fable plus the Haiku helper and no Opus model. It proved
  the Sentinel projection, post-write `outcome_unknown`, and Unix hard-
  containment guards red-to-green, restored its clean detached tree, and
  reported only one non-blocking fixture-hygiene advisory. Canonical review
  evidence is in `.agents/review/index.md`; the earlier fail-closed attempts
  and their resolution remain in
  `.agents/review/mcp-resilience-r0-review.contested.md`.
- **CI portability Slices 11-16 are complete at final test-only head
  `5642376`; hosted run `29541559607` passed all six jobs at exact pushed
  descendant `7bc08aa`.** Run
  `29536074900` proved Slices 11-12 closed their R0 checkout and marker races,
  then exposed four older scheduler, reprime-setup, Unix deadline, and Windows
  retry-notification assumptions. Each repair changes only its test; the
  review closure additionally freezes the native deadline helper without
  restoring a scheduler-latency ceiling. Focused red/green proofs, the complete
  macOS battery, direct Linux/Windows batteries at the runtime-equivalent
  preceding head, exact final-head platform guards, and independent review all
  passed, followed by green Ubuntu/macOS/Windows product and SIEM jobs. Slices
  1-10 remain complete with green hosted evidence. Canonical details are in
  `.agents/plans/ci-portability-repair.md` and
  `.agents/machines.md`; RTK distribution remains a separate decision.
- **Audited-harness Slice 6 is complete locally.** Code
  head `7999328` moves invoke/job/state/reset behavior and session-lifetime
  caches behind one owning `SessionRuntime`, leaves audit and output
  capabilities per operation, keeps MCP adapters thin and schema-compatible,
  and preserves jobs-before-runspace audited shutdown. Claude Code 2.1.208
  accepted exact range `aca20a6..7999328` with `guard_confirmed=true` in a
  clean detached worktree after independent cache-isolation, ownership,
  adapter, and reset-lifetime mutation proofs plus the full battery. Completion
  records are committed at `67b900d`. The accepted feature history was
  fast-forwarded to local `master`, content arrival was verified independently,
  and the feature branch was removed. Canonical evidence is in
  `.agents/review/index.md`.
- **Audited-harness Slice 5 is complete locally.** Final code head
  `fc61be6` completes audited cold-background planning, typed exactly-once
  dispatch/fallback, provenance-
  aware polling, and path-free output recovery. Eligible direct-text jobs
  reserve output capacity before start, seal one immutable bounded supervisor
  artifact, and publish its opaque handle before terminal notification;
  seam-absent RTK jobs remain explicitly unrecoverable, and model-facing job
  surfaces expose no internal path. Claude Code 2.1.208 and Grok 0.2.93 each
  accepted exact range `ee21f16..fc61be6` with `guard_confirmed=true` in clean
  detached worktrees after independent guard proofs and the full verification
  battery. Completion records are committed at `bbb1742`. The accepted
  feature history was fast-forwarded through handoff tip `de8dc53` to local
  `master`, content arrival was verified independently, and the feature branch
  was removed. Canonical evidence is in `.agents/review/index.md`.
- **Audited-harness Slice 4 is complete locally.** Final product
  head `76d4f0c` integrates the supervisor-owned output store and audited
  `ptk_output`, bounded two-stage same-invocation capture/recovery, anonymous
  retained artifacts, truthful recovery hints, behaviorally inert legacy
  `raw`, explicit `route=pwsh` consent, and the narrow unshaped state probe.
  Claude accepted exact integrated range `9c89abf..76d4f0c` with
  `guard_confirmed=true` after eight independent cross-slice red-to-green
  proofs and the full local battery. The accepted feature history was
  fast-forwarded to local `master`, content arrival was verified independently,
  and the feature branch was removed. Canonical review evidence is in
  `.agents/review/index.md`; host-specific evidence is in `.agents/machines.md`.
- **Audited-harness Slice 3 is complete locally.** Final
  integrated code head `b78d9c6` completes structured foreground routing,
  audited RTK/Bash dispatch, bounded preference-independent RTK capture,
  exact-original pre-start fallback, and post-success mixed-domain guidance.
  Claude accepted the exact fixed head with `guard_confirmed=true`; all six
  admitted material findings are closed. The accepted feature history was
  fast-forwarded to local `master`, content arrival was verified independently,
  and the feature branch was removed. Canonical review and platform evidence
  is in `.agents/review/index.md` and `.agents/machines.md`.
- **Audited-harness Slice 2 is complete locally.** The final
  integrated code head `3d3739a` completes job/control audit, local-only and
  anchored OTLP export, evidence administration and retention, permanent
  operator disposition, and the strict `ptk.audit/2` extension. Claude
  accepted the exact fixed head with `guard_confirmed=true`; all four material
  integrated-review findings are closed, and the accepted feature history was
  fast-forwarded to local `master`. Canonical review and platform evidence are
  in `.agents/review/index.md` and `.agents/machines.md`.
- **Audited-harness slices 0-4 are complete locally.** Slice 1 commit
  `460c106` adds the mandatory current-server audit foundation, exact-script
  evidence, capacity reservation, protected local storage, and fail-closed
  pre-effect guards. Claude accepted the fixed-SHA implementation after an
  independent red→green guard proof; canonical review evidence is in
  `.agents/review/index.md` and platform evidence is in `.agents/machines.md`.
  `.agents/plans/audited-harness-sessions.md` combines
  mandatory PTK-owned/SIEM-exportable audit, private harness-scoped
  process-per-session workers, internal PTK→RTK routing, same-invocation
  output recovery, and no-retry mixed-domain handling. Its frozen slice-0
  results record the absent RTK capture seam, local-only/anchored OTLP
  contracts, exact profile/protocol bounds, carried routing guards, and live
  passing Windows/Unix parent-death probes. Both plan reviewers accepted the
  same approved pre-implementation content. Canonical pre-implementation
  review evidence and fixed-head guards live in `.agents/review/index.md`.
- **Slice 10 production acceptance is in progress on
  `impl/production-reliability-salvage`.** `c2f67d3` added the unshipped
  production-acceptance stress harness and package-boundary guards; `9151e57`
  made selected-session state and the supervisor-local session list prove
  prompt, truthful behavior while three real calls remain active across two
  servers. Linux acceptance then reproduced an intermittent
  `descendants_unknown` reset refusal even though broker and registry
  containment were confirmed. Root cause was an asynchronous forwarding race:
  the Unix registry's exact empty-domain proof was complete, but the public
  worker proof task could still be pending when the supervisor checked it.
  `156aac2` publishes that already-completed exact proof before returning
  `ConfirmedEmpty`; the supervisor's fail-closed rule is unchanged. The guard
  failed with the synchronization removed and passed after restoration.
  Verification at `156aac2`: 1,204 server tests, 141 Pester tests (2 skipped),
  247 SIEM tests, handshake, macOS 100 replacement cycles, and the Linux
  diagnostic reproducer's 100 replacement cycles all passed.
- **Slice 10 timeout containment is committed and directly verified at
  `ef1657b22b9847f24dc991e6a9c0a58aa8624281`.** One absolute admission
  deadline now reaches the runspace; a dedicated watchdog stays live while
  PowerShell cancellation or terminal transport is blocked; timed-out
  incomplete output remains recoverable under the supervisor's single recovery
  handle; and production fail-fast replaces only the unresponsive worker. Five
  targeted regressions failed for their intended reason and passed after
  byte-exact restoration. Complete macOS ARM64 and exact-archive Linux x86_64
  batteries passed, including 100 replacements, timeout child/grandchild death,
  sibling survival, resource return, and supervisor hard-kill containment.
  Exact host identities, commands, counts, archive/log hashes, and cleanup are
  recorded in `.agents/machines.md`. Nothing was installed, deployed, or
  pushed.
- **Slice 10 Unix process-group escape acceptance is committed at
  `a626c3b8e993575ad916058bc33959c02b4daae3` and verified on macOS ARM64 and
  exact-archive Linux x86_64.** The public harness proves a real `setsid`
  escape reports `descendants_unknown`, retains the escaped PID as unconfirmed,
  blocks worker reuse, does not replay the timed-out command, and permits a
  different worker only after exact escaped-PID cleanup. Making the registry
  ignore process-group changes produced a false `ready` replacement and made
  the new guard fail; byte-exact restoration passed reduced and full
  acceptance. Full host evidence and cleanup hashes are in
  `.agents/machines.md`.
- **Slice 10 direct-worker-death containment is committed at
  `8b9644ae8937b33c9bd9de1498dbef6ee7cd88d2` and verified on macOS ARM64 and
  exact-archive Linux x86_64.** The Unix broker now treats reaping the worker as
  the immediate trigger to contain any remaining process-group members;
  descendants can no longer keep the broker waiting indefinitely by retaining
  inherited pipes. The new low-level guard failed against the old broker with
  both descendants still live and passed after the fix. Public acceptance
  directly kills a worker during an in-flight command, proves both descendants
  exit, reports `outcome_unknown` without replay, replaces only that worker,
  and preserves the sibling PID and warm variable. Complete macOS and Linux
  batteries passed; exact counts, log hashes, host split, and cleanup are in
  `.agents/machines.md`. A focused nine-case pass makes the full hard-kill
  boundary mapping explicit: deterministic transport-death guards cover before
  write, write-call entry, during result, and after terminal decode; real
  worker/process guards cover during execution and after an observable effect.

## Rotated 2026-07-11 (drift based on 78779b0)

The previous current-work sections are preserved verbatim below. Live
reverification found the clone and canonical remote synchronized at
`78779b0`, issues #5/#6 closed, and the Mac install current for all product
files; the replacement state records the remaining held conflicts.

### From `## Now`

- **SECURITY: question OPEN, owner-led consultation in progress — read
  `.agents/plans/security-layer.md` top section BEFORE any security
  work.** Two distinct things, do not conflate: (1) **DECIDED** — the
  declarative policy-file gate is rejected as the answer (owner: "brittle
  nonsense — alias it, use python, edit the rules file"); its slices are
  prior art, do not implement. (2) **NOT a decision** — the "ptk gets
  zero consideration / low-friction bypass gated on one careless yes /
  missing the `Remove-Mailbox CEO@company.com Y/n?` check" statement is
  the owner's FRAMING for an outreach consultation he is running
  himself, recorded verbatim in the plan. It is the problem any shape
  must answer, not a verdict already delivered. Do not cite it as a
  settled call. **Live candidate, UNVERIFIED: MCP elicitation**
  (server-initiated user confirmation mid-call) — verify spec support,
  per-harness client support, headless behavior (a prompt no client
  renders fails OPEN — worse than none). From the neutral cross-harness
  consultation, still open as candidates: ConstrainedLanguage profile,
  authenticated-session TTL/process-teardown, control-action gating
  placement above `ptk_reset`/`ptk_job kill` (reset kills jobs before
  any check could fire). **Secret redaction was raised by all three and
  REJECTED** (owner's delta test: `bash -c 'cat secret.txt'` leaks the
  same secret today; verified ptk_state emits env NAMES only, never
  values). Process lessons recorded in the plan: (a) shape review BEFORE
  plan review — a review loop cannot question its own premise; (b) apply
  the DELTA TEST to every proposed control — what does routing through
  ptk make worse than the path that already exists? Three models
  agreeing is not evidence; they pattern-match the same way.
- **Two DRAFT plans, codex-reviewed, NEITHER APPROVED — no code written
  for either.** (a) `.agents/plans/rtk-rewrite-routing.md` — route all
  native work through `rtk rewrite` (owner direction 2026-07-10:
  "everything that isn't powershell should route through rtk"); loop
  CLOSED CONVERGED after 7 rounds, 15 findings, rounds 20→7→6→2→1→1→1.
  One item (rrp-15) closed by coder disposition, not reviewer grade —
  **flagged for owner ratification**: routing by substitution means
  name-keyed session hooks (a `git` breakpoint) never fire on a routed
  segment; inherent to substitution, already true of shipped
  single-command routing, disclosed in docs with `route=pwsh` as escape.
  (b) `.agents/plans/security-layer.md` — see the rejection above; its
  12 findings were all resolved before the premise fell.
- **`.agents/decisions.md` is UNDER HOLD** (owner, in-session 2026-07-10:
  "don't update decisions until we're done talking"; a premature entry
  was reverted at `6ee6f9e` — reverted, never rewritten). The rtk-routing
  direction and the security reframe both still need decision entries
  when the owner releases the hold.
- **~30 commits local-ahead of `origin/master`** (both plan drafts, all
  loop fixes, the loop records). Owner pushes himself.
- **issue-5/6 batch COMPLETE, loop CLOSED (2026-07-10)**: the
  implementation review ran three rounds (10 findings → 3 reopened + 5
  new → clean NO-FINDINGS close at `9a894e1`); all 25 findings fixed one
  per commit, record in `.agents/review/index.md`. Battery at head:
  dotnet 100/100, Pester 133/1 skipped, handshake PASSED, live stdio
  issue-repro checks 11/11 (canonical counts). Owner PUSHED 2026-07-10
  (`master` == `origin/master` at `f12dd46`); issues #5 and #6 got fix
  references and were CLOSED the same day. GitHub reports the remote
  renamed to `PowerShell-Token-Killer` (capital W) — the configured URL
  still works via redirect. Owner grant (2026-07-10, in-session):
  persistent approval to handle GitHub issues on this repo as
  appropriate (comment/close/triage without per-action asks).
  Machine-local note: this session's ptk server died during the slice-0
  incident and the installed payload predates the whole batch — a
  dev-install re-run is needed for live sessions to pick it up.
- Batch details (`.agents/plans/issue-5-6-invoke-semantics.md`, APPROVED
  in-session): slice 0 root cause — the "900s call never answered"
  incident was system sleep vs monotonic timers (harness MCP log +
  pmset evidence, recorded in the plan), no code defect; slice 1 neutral
  `[stderr]` by invocation provenance; slice 2 total wall-clock budget
  (queue + preflight + execution, sleep-safe deadline re-checks, fast
  queue expiry without executing); slice 3 never-queueing ptk_state.
  Plan loop i56p-1..11 CLOSED CONVERGED the same day. Also banked:
  codex calls ptk_invoke from inside its read-only sandbox (issue-3
  permission-surface datum). Process note (owner, in-session): approval
  requests now go to the owner as ≤50-word plain-English summaries; the
  broader plan-length fix will come via the governance toolkit, not this
  repo.
- **shell-dialect plan COMPLETE 2026-07-10**
  (`.agents/plans/shell-dialect.md`; decision entry + the sd1-4 and
  sd3-1 amendments in `.agents/decisions.md`). All slices done and
  codex-loop-closed: slice 0 (probes frozen), slice 1 (detector;
  sd1-1..7, owner-ratified), slice 2 (server wiring `8c234e8`;
  sd2-1..6), slice 3 (raw posture `fa1b23c` + elision-hint redesign
  `0840d13`; sd3-1 owner-adjudicated, sd3-2..4 RESOLVED), slice 4 (D3
  texts `8bb96b1`; sd4-1..2). The plan's live end-to-end Verification
  pass ran over real MCP stdio against the built server: **11/11**
  (refusals verbatim on both paths, bash -lc recovery with compression,
  raw counter + log line positive and negative, rtk-absent seam).
  Battery as of `e576962`: dotnet 80/80, Pester 133 passed / 1 skipped
  (canonical counts), handshake PASSED. Loop records:
  `.agents/review/index.md`; findings: `.agents/review/findings/sd*-*.md`.
  Plan tail EXECUTED 2026-07-10: the owner pushed mid-session (remote
  `master` reached `8bb96b1`), so the approved fix references were
  posted — issue #3 got the item-1 comment (stays open for items 2-4),
  issue #4 got its comment and was CLOSED. Only the sd4-fix tail
  (`428ac82`, `e576962`, the loop-close records) remains local-ahead.
- **Owner decisions recorded 2026-07-09 (in-session, post-handoff):**
  (a) slice-1 convergence close RATIFIED (above); (b) the push
  happened — `master` == `origin/master` == remote HEAD at `c71ea70`,
  so the "29 commits local-ahead / push go" item is CLOSED; (c) issues
  **#5** and **#6** triaged **after current work** — finish shell-dialect
  slices 2-4 first, then take #5/#6 as the next batch (not parked, not
  preempting); (d) the release-plan hook-default question stays
  DELIBERATELY OPEN by owner choice ("decide later") — re-present it
  before release slice 4 (installers) starts; (e) owner approved posting
  fix references on issue #3 (item 1) and issue #4 — and closing #4 —
  but execution is DEFERRED until the fixing slices land and are pushed
  (per the plan's Verification section: #3 item 1 needs slice 2; #4
  needs slices 3-4). Do not post before then.
- GitHub status as of 2026-07-10: issues #1, #2, and #4 CLOSED (#4
  closed this date with its fix reference); #3 open with the item-1 fix
  comment posted (items 2-4 remain a candidate small follow-up batch;
  the MCP permission-bypass ask is its own future owner-gated plan);
  #5/#6 open, triaged after-current-work.
- Standing flags carried forward: the release-distribution plan's
  slice 3 (`release.yml`) is now unblocked (shell-dialect complete), and
  its hook-default decision must close before its slice 4
  (`.agents/plans/release-distribution.md`); the remote `ci/slice-2`
  branch was DELETED 2026-07-09 (owner go in-session) — flag retired;
  the machine-local dev-install note lives in `## Next`.

### From `## Next`

1. **Security (highest, owner-driven — but the owner is running his own
   outreach on the framing; do not pre-empt his consultation with a
   plan).** Useful agent work while that runs: verify the
   MCP-elicitation candidate as FACT-FINDING only (spec support; which
   harnesses render server-initiated prompts; headless failure mode — a
   prompt nobody renders fails OPEN and is worse than none). Report
   findings; do not draft a plan or a shape until the owner's
   consultation returns. Do NOT re-derive the policy file.
2. **rtk-routing plan:** owner approval + ratification of the rrp-15
   disposition (see `## Now`). No code until then.
3. Owner push go for the local-ahead tail (~30 commits).
4. Owner releases the decisions.md hold, then record: the rtk-routing
   direction and the security reframe.
- Owner push go for the local-ahead docs tail (shared-runspace idea
  plan + its two review loops' records + drift fixes; ask-first
  policy). The issue-5/6 approval/push bullet that lived here is DONE —
  approved, built, pushed, issues closed (see `## Now`); caught stale by
  the grok reviewloop pass 2026-07-10.
- Remaining owner decision: hook-default (blocks release slice 4 only;
  owner chose "decide later" 2026-07-09 — re-present before that slice).
- Machine-local (owner's Mac + Windows box): the installed `~/.ptk`
  payload and the ptk_init-written nudges/hook predate the whole
  shell-dialect plan — a dev-install re-run (+ ptk_init) is needed for
  live sessions to pick up slices 1-4 and the new texts.
- Slice-7 test matrix (proposed, not yet in the decision entry): (1) AD module
  native import inside ptk_invoke + warm reuse across calls; (2) build and HOLD an
  Exchange implicit-remoting session in the warm runspace, Get-Queue latency call
  1 vs call N; (3) EXO/Graph via unattended cert auth (plan constraint: no
  interactive Connect-*). Server knobs for these tests (Program.cs): per-call
  timeout default 300s (`PTK_CALL_TIMEOUT_SECONDS`), idle self-exit default 4h
  orphan backstop (`PTK_IDLE_EXIT_SECONDS`).
- ~~go/no-go test (~2026-07-20)~~ — DECIDED: unqualified GO 2026-07-08,
  ahead of the window (`docs/history/decisions-archive.md`); this bullet
  was stale (caught by the shared-runspace plan review, spr-1). The
  Windows-box real-usage evaluation intent lives on only as the slice-7
  test matrix above and the shared-host measured-pain criterion in
  `.agents/decisions.md`.
- ~~Interim security posture: keep ptk_invoke on ask-per-call in the
  harness; build the policy-file gate if blanket-allow pressure
  appears.~~ — SUPERSEDED 2026-07-11: the criterion fired AND the
  response was rejected. Current posture: ask-per-call remains the only
  control, the owner considers that insufficient (`## Now`), and the
  replacement shape is unresolved. The decision entry stays OPEN until
  the hold lifts.

### From `## Blockers`

- None. (Handoff re-verification note: the 2026-07-09 governance-refresh
  flag "`.agents/repo-map.json` / `.agents/artifact-manifest.json` are
  retired-but-locally-modified, remove by hand" is now stale — the tree
  is clean and both files are tracked unmodified; deleting them remains
  an owner option, not a blocker.)

## Rotated 2026-07-09 (handoff at d352e66)

### From `## Now`

- **2026-07-09 (night, latest): shell-dialect slice-1 codex re-grade
  round 1 RECORDED.** At head `acb0f39` (codex, Codex v0.144.0,
  gpt-5.6-sol, read-only): sd1-2 RESOLVED; sd1-1 and sd1-3 held NOT
  RESOLVED — each fix covered one half of its finding (ambient-only
  resolution; comment-only blanking). 4 new findings, every claim
  independently verified in-session before triage (repros re-run at
  head): sd1-6 (MEDIUM, space-filler blanking SYNTHESIZES bash shapes —
  new FP class, disproves sd1-3's "never an over-match" claim) and
  sd1-7 (LOW, error IDs pair with shape evidence globally) ADMITTED;
  sd1-5 DECLINED (miss inside sd1-3's recorded known gap); sd1-4
  CONTESTED (alias-shadowed `set` — contests the frozen slice-0 `set`
  exemption, OWNER CALL). Reviewer battery at head: Pester 119/1
  skipped, clean tree. FIX ROUND 2 LANDED same night: `bc5638d`
  (sd1-1 script-local definitions), `4e6a223` (sd1-3 Generic-fragment
  blanking), `5f4b3fa` (sd1-6 non-bridging blank filler), `ef9f3ed`
  (sd1-7 error/evidence locality) — one commit + red/green guard each;
  battery at head: Pester 123/1 skipped, dotnet 59/59. RE-GRADE ROUND 2
  (head `293eda6`): sd1-6 RESOLVED; sd1-1/sd1-3/sd1-7 held on residual
  tails (no new IDs; every tail master-verified at head). FIX ROUND 3
  LANDED: `9b5e326` (sd1-1 Set-Alias tracking + lexical ordering),
  `20ba7fd` (sd1-3 escape-aware fragments), `f30ddde` (sd1-7
  command-position keywords). Battery: Pester 127/1 skipped, dotnet
  59/59. RE-GRADE ROUND 3 (head `374666b`): all three held on strictly
  more crafted tails (no new IDs, third consecutive round; all
  master-verified). FIX ROUND 4 LANDED: `eb5e193` (recursion counts),
  `f5229a7` (fragments span newlines), `0c43b05` (defined/resolving
  keywords are not evidence). **LOOP CLOSED CONVERGED** per the ccc9686
  contrived-tail precedent — every named repro across four rounds is
  guarded. **Canonical counts: Pester 130 (+1 skip), dotnet 59.**
  SLICE 1 DONE. NEXT: slice 2 (server wiring — labeled refusal result on
  both execution paths). OWNER: (a) ~~sd1-4 adjudication~~ DONE — owner
  unparked in-session, fixed at `c43360c` (set -e flags only while set
  still means stock Set-Variable; Pester 132/1 skip, dotnet 59/59;
  decision amendment recorded), (b) ratify the convergence close or
  order re-grade round 4, (c) push go. Records:
  `.agents/review/index.md`, `.agents/review/findings/sd1-{1..7}.md`.
- **2026-07-09 (night): shell-dialect plan APPROVED — owner,
  in-session.** D1 = (a) refuse-fast with platform-aware guidance; D2 =
  non-breaking raw-posture subset; D3 = dialect line. The #4 comment's 4
  acceptance suggestions reconciled into D2 at approval (adopted:
  no-preemptive-raw rewording, `route=pwsh`+`raw=false` taught as "exact
  execution, shaped output", `ptk_state` raw telemetry; declined:
  reason/cost gate — attribution recorded in the plan). Decision entry
  in `.agents/decisions.md`. Slice 0 probes RAN same night, results
  frozen into the plan: the #3 repro does not reproduce on this build
  (the resolver never rtk-wraps a `&&` chain — pwsh leg, runs fine);
  detection list pinned at 12 constructs IN, trailing-`\` OUT
  (Windows-path false-positive risk); `bash -lc` recovery verified end
  to end (cwd anchor, `[exit] N`, `[ptk:log via rtk]` compression, both
  legs); D2/D3 wording baseline snapshotted in the plan. NEXT: slice 1
  (token-aware detector in the module), then 2-4 — one commit + battery
  + codex loop each. #5/#6 remain UNTRIAGED (owner call).
- **2026-07-09 (evening refresh): OWNER PUSHED master through
  `5e3cd70`; GitHub issues #1 and #2 CLOSED (~15:35Z). THREE NEW GitHub
  items since (all `roethlar`, ~19:28-19:33Z):** issue **#5** (ptk_invoke
  labels successful exit-0 native stderr as `[errors]` — medium; asks
  for a neutral stderr label, PowerShell Write-Error kept
  distinguishable, 4-case regression matrix), issue **#6**
  (`timeoutSeconds` excludes queue wait behind the serialized runspace —
  a 1s-budget call can wait arbitrarily and still run; `ptk_state`
  blocks behind the busy runspace it should diagnose — medium; asks for
  wall-clock budget semantics + prompt busy/active-call-age/waiter-count
  reporting), and a **comment on issue #4**: cross-model confirmation
  (GPT-5.6-Sol/Codex governance audit, 2026-07-09) of the
  `raw=true`-as-habit problem — raw set preemptively on most inspection
  calls, bought nothing (README byte-identical raw vs shaped; 86%/95.6%
  reduction forfeited) — with 4 acceptance suggestions (no preemptive
  raw; teach route=pwsh+raw=false as "exact execution, shaped output";
  reason/cost gate on unjustified raw; raw-usage telemetry in
  ptk_state). **#5/#6 are UNTRIAGED — no plan, no code; owner
  prioritization needed.** The #4 comment bears directly on the DRAFT
  shell-dialect plan's raw-posture leg — reconcile before approval.
  Local state: working tree clean, 4 unpushed commits, all
  shell-dialect plan text (`3227607` draft, sources = issue #3 item 1 +
  issue #4; `1d7f38b` sd-1..sd-10 fixes; `13599a6` sd2-1..sd2-5;
  `809e0d0` sd3-1). Plan status: DRAFT awaiting owner approval; slice 0
  (probes) runs first; no code before approval.
- **2026-07-09: slices 3-6 review loop CLOSED (converged).**
  Re-grade round 1 (codex read-only, head `3ec608b`) cleared mhi-9 and
  mhi-11 and held mhi-10; round 2 (head `d58be68`, base `3ec608b`)
  graded the mhi-10 completion and mhi-12 RESOLVED, guard_confirmed,
  NO NEW FINDINGS (codex-cli 0.142.5). Verdicts recorded in
  `.agents/review/findings/mhi-{9,10,11,12}.md`; index updated. ~~All
  slice 3-6 + review-loop commits remain unpushed pending the owner's
  master push go.~~ RESOLVED same day: owner pushed master through
  `5e3cd70` (origin confirmed at that head, 2026-07-09 evening).
- **2026-07-09: slices 3-6 review loop — fixes landed; re-grade closed
  (see latest bullet).** Codex loop over `86b51ae..6134a2f` produced
  mhi-9/10/11 (fixed: `ce0caf2`, `fa3620a`, `6c1d025`); re-grade round 1
  held mhi-10 NOT RESOLVED — completion `e8363f3` (no-CLI codex uninstall
  now reads the config and names the manual removal). mhi-12 self-found
  live (HIGH): `codex mcp remove ptk` orphans `[mcp_servers.ptk.tools.*]`
  subtables and bricks the codex CLI — this box's config repaired
  in-session; sweep fix `9d00c6e`. **Canonical counts: Pester 85, dotnet
  59.** Master is local-ahead of origin; push is the owner's call. NEXT:
  codex re-grade round 2 at `e8363f3` (mhi-10 completion + mhi-12), then
  record the verdicts.
- **2026-07-09 (latest): revert miscommunication resolved; end-state
  build resumed.** The harness-file writes stay undone; the
  nudge-standard-layer script change (60cd9f3) is RESTORED by reverting
  82b8c51. Operative decisions live in the plan's two 2026-07-09
  amendments (`.agents/plans/multi-harness-init.md`): nudge is a
  standard layer, machine changes only through the complete owner-run
  install process, slice 5 = default dev-install chaining, agy hook
  deferred, no live installer runs during development. Building slices
  3-6 to completion. Owner pushed through a0dceb8; everything after is
  local. **Canonical counts: Pester 75, dotnet 59.**
- **2026-07-09 (latest): GITHUB ISSUE #2 FIXED and codex-closed** (plan:
  `.agents/plans/issue-2-stale-hook-registration.md`; owner mid-session
  go). ptk_init's claude leg registers the INSTALLED hook copy
  (`~/.ptk/scripts/ptk-hook.ps1`) so checkout moves cannot strand
  registrations; `-Show` flags STALE entries (missing file OR directory);
  installs name what they replaced; dev-install refreshes an existing
  hook entry when it also registered the server. Loop: i2-1 (MEDIUM,
  consent via parsed PreToolUse entry, 665d99a), i2-2 (LOW docs,
  ea95d48), i2-3 (LOW -PathType Leaf, 69a2b13) — all re-graded RESOLVED.
  **This box's live stale entry (src\ path — fail-open since unknown) was
  HEALED; hook effective next Claude Code session.** NOTE: ~/.ptk's
  payload is the owner's 2026-07-08 install — a dev-install re-run picks
  up the new hook text (neutral naming + liveness) and installer.
  **Canonical counts: Pester 75, dotnet 59.** ~~Comment/close issues
  #1+#2 after the owner pushes.~~ DONE 2026-07-09: both CLOSED on
  GitHub (~15:35Z). NEXT: multi-harness slice 3 (grok leg).
- **2026-07-09 (later): GITHUB ISSUE #1 FIXED and codex-closed** (plan:
  `.agents/plans/issue-1-mixed-stream-shaping.md`, owner-approved "go";
  taken ahead of multi-harness slice 3 by the same go). Shipped, one
  commit each: caa7714 (string-bearing mixed streams render as text —
  the repro class), 66a53df (heterogeneous header + ToString samples),
  c2d8a4a (i1-1: header type-list bounded), 86f990e (Select-Object
  -First stamps Selected.* into live TypeNames — generic path de-mutated,
  self-caught), d3f4569 (i1-3: MaxItems 0 wraparound), 1e1ab99 (i1-2:
  Select-PtcFirst helper, no object-row Select-Object anywhere — the
  deserialized/remoting exposure). Loop record: `.agents/review/index.md`
  (two rounds, final NO NEW FINDINGS). **Canonical counts: Pester 71,
  dotnet 59; handshake PASSED.** Comment/close issue #1 after the owner
  pushes. NEXT (owner instruction mid-session): GitHub issue #2 — stale
  global hook registration fails open silently — then multi-harness
  slice 3 (grok leg).
- **2026-07-09: CONCURRENT GOVERNANCE REFRESH interleaved with the
  slice-2 build session** (602ee45/03d9162/719c200/bd6ff02, toolkit
  ce0db15, owner identity, ~00:17-00:18): AGENTS.md + skills + CLAUDE.md
  + repo .claude/settings.json refreshed mid-build. bd6ff02 ("reset
  governance") swept one in-flight, comment-only `scripts/ptk_init.ps1`
  edit from the build session's working tree into its commit — content
  verified intact at HEAD (Pester 66/66 at 3caa78f; no governance file
  touched by the build commits, no build file damaged by the refresh).
  REFRESH FLAGS awaiting owner: `.agents/repo-map.json` and
  `.agents/artifact-manifest.json` are retired-but-locally-modified
  ("remove by hand if intended"). Process hazard worth remembering: a
  refresh that commits working-tree sweeps must not run while a build
  session has in-flight edits — a guard-proof sabotage state could get
  committed under a refresh message.
- **2026-07-09: MULTI-HARNESS SLICE 2 EXECUTED — codex leg.**
  `ptk_init.ps1 -Agent codex`: idempotent registration (existing entry
  left as-is via `codex mcp get`; else `codex mcp add ptk -- <installed
  exe>`, payload-gated), nudge block in `~/.codex/AGENTS.md`
  (`-NudgePath` now binds to the single selected leg). LIVE ON THIS BOX:
  real run hit the already-registered short-circuit, nudge installed
  after the owner's `@RTK.md` include, and a fresh `codex exec` quoted
  the block verbatim — codex nudge home VERIFIED
  (`docs/harness-support.md`). Codex loop on 7a068b9 CLOSED 2026-07-09:
  mhi-8 (MEDIUM — registration probe now runs before the payload gate,
  preserving leave-as-is for existing/custom entries; guard test uses a
  fake codex shim, 3caa78f) fixed and re-graded RESOLVED, no new
  findings. **Canonical counts: Pester 66, dotnet 59.** Details in the
  plan. NEXT: slice 3 (grok leg).
- **2026-07-08 (later night): MULTI-HARNESS SLICE 1 EXECUTED — installer
  framework + Claude leg.** `ptk_init.ps1` is the per-agent framework
  (`-Agent claude|codex|grok|agy|all`, detected-set default, stub legs for
  2-4), user-level by default with loud flip note, `-Local` warned opt-in,
  `-Nudge` block in `~/.claude/CLAUDE.md`, registration gate (refuses the
  hook without a `~/.ptk` payload), harness-neutral hook text, and the
  mhi-2 liveness check (down-server wording in the deny; wording only).
  Details + in-slice decisions in the plan
  (`.agents/plans/multi-harness-init.md`). **Canonical counts now: Pester
  62, dotnet 59** (11 new Pester tests, all guard-proven). README +
  server/README hook sections updated. OWNER NOTE: the flip means a bare
  `ptk_init.ps1` run now patches `~/.claude/settings.json`, and
  `dev-install.ps1 -Hook` prints a Claude-leg-only note. Codex loop on
  057a5ee CLOSED 2026-07-09: mhi-6 (MEDIUM, dev-install -Hook now gated
  on actual registration, 1e06351) and mhi-7 (LOW, surgical byte-exact
  nudge strip, ec6e094) both fixed and re-graded RESOLVED, no new
  findings (`.agents/review/index.md`). NEXT: slice 2 (codex leg).
- **2026-07-08 (night): v2 LIVE ON THIS BOX; MULTI-HARNESS PLAN APPROVED;
  SLICE 0 EXECUTED — ALL PROBES GREEN.** Owner completed dev-install +
  global hook + push; the session's own tool calls then hit the redirect
  and re-issued via ptk_invoke (both matchers verified live). Fresh
  headless Claude session: deny quoted verbatim → unprompted re-issue →
  task done — the standing hooked-check gate is CLOSED. grok and agy
  registered and live-verified (grok: ptk__ naming, no Claude-hook
  spillover, nudge home = ~/.claude/CLAUDE.md VERIFIED by marker probe;
  agy: mcp_config.json entry, unprefixed names, headless auth worked);
  codex entry was missing, re-added, tools list, headless auto-deny
  re-confirmed (interactive is codex's path). Durable table:
  `docs/harness-support.md`. Slice-0 probes ran THROUGH ptk background
  jobs. NEXT: slice 1 (installer framework + Claude leg), codex loop per
  slice. **Dogfood findings for the backlog:** (1) mixed string/object
  streams hit the generic table and LOSE the string lines (twice in live
  use — real shaping gap); (2) same class: string+MatchInfo mix rendered
  a Length-only table.
- **2026-07-08 (evening): SETUP DOCS UPDATED; MULTI-HARNESS INIT PLAN
  DRAFTED, codex-closed, AWAITING owner approval + the manual agy
  interview.** `.agents/plans/multi-harness-init.md`: per-harness
  registration/enforcement/nudge legs modeled on rtk init; evidence
  frozen (codex/grok CLI surfaces verified, self-reports marked as
  probe targets; agy headless interview failed — owner is asking it
  interactively with a prompt that demands VERIFIED vs RECALLED
  labeling). Slice 0 = live probes, headlined by the STANDING GATE both
  repos want: the live Claude hooked deny-and-reissue check. README/
  server-README now carry the cross-repo findings (global-first hook,
  content-tracking warning, truthful hook failure semantics — a down
  server still denies, PTK_DIRECT escapes; liveness-aware hook is a
  recorded slice-1 candidate). Review loop mhi-1..5 closed
  (`.agents/review/index.md`; two HIGH docs claims corrected). Commits
  local from 419503c onward; push owner-gated as always.
  in-session).** ptk continues as an active product; the continuation-gate
  entry is archived (`docs/history/decisions-archive.md`), the
  destructive-cmdlet parking survives as its own Open entry, and
  warm-runspace slice 7 (AD/EMS/EXO validation) plus greenfield D5
  (CLI-face retirement) are unblocked by the gate's closure. The shared
  multi-client host stays parked on its own measured-pain criterion (not
  part of this go).
- **2026-07-08 (post-GO build): v2-FEEDBACK FIXES + D5 RETIREMENT BUILT
  and codex-closed** (`.agents/plans/v2-feedback-fixes.md`; loop record
  in `.agents/review/index.md`). What shipped:
  - **Slice 1 (56b1af3): the os-error-6 class is FIXED.** Probe-diagnosed
    root cause: ChildStdinGuard's NUL handle was NON-INHERITABLE
    (File.OpenHandle default), so children got a stdin handle value
    absent from their handle table — rustup shims (cargo/rustc/codex)
    died duplicating it. SetHandleInformation(HANDLE_FLAG_INHERIT) fixes
    it; live-verified: cargo/rustc work on route=pwsh, auto, raw, jobs.
    The e2e now asserts stdin-reading natives SUCCEED (the missing
    assertion that hid the bug) and spawns CreateNoWindow.
  - **Slice 2 (9cc74de): UTF-8 native output decoding** — Console
    OutputEncoding pinned BOM-less UTF-8 in RunspaceHost (mojibake class
    dead; OEM-emitting tools now mojibake instead, escape hatch = job
    logs, NOT raw=true).
  - **Slice 3 (4f957ab): timeout message warns resolution can differ
    after recycle and points at ptk_state; rtk install nag filtered at
    error collection (specific banner only); stderr-swallow report
    probed NULL** (both routes return identical real stderr — details
    frozen in the plan).
  - **Slice 4 / greenfield D5 (bfc6323): CLI face RETIRED.** Module =
    server shaping library (exports: Compress-PtcObject,
    Compress-PtcOutput, Resolve-PtcInvokeScript; 1374→622 lines);
    ptk.ps1, docs/usage.md, CLI tests and fixtures removed; README
    single-surface story + setup corrected (d5-1: .mcp.json is empty,
    dev-install or explicit registration are the paths).
  - Codex loops: v2fb-1, v2fb-2, d5-1 (all LOW) fixed one commit each;
    v2fb re-grades RESOLVED; loop closed converged.
  - **Canonical counts now: Pester 51, dotnet 59.** Owner pushed master
    through d881d37 on 2026-07-08 and CI run 28985350456 is GREEN on all
    three OSes at that head. The installed 0.2.0 binary still serves the
    OLD surface — stop the ~/.ptk servers, rerun scripts/dev-install.ps1
    (it refuses while one runs, by design), then /mcp reconnect to go
    live on v2. Owner fixed the codex config (now points at the
    installed binary), closing the repo-bin exe-lock annoyance.
- **2026-07-08 (after the build): SECOND LIVE-USE FEEDBACK BATCH recorded
  (owner-shared notes from heavy real use of the CURRENT installed v1,
  `F:\notes\PTK\vela_session_notes.md` — machine-local, essentials
  captured here). Assessment against the just-built v2, candidate work
  awaiting owner prioritization (no code authorized):**
  1. **HIGH — "The handle is invalid (os error 6)" for rustup-shimmed
     binaries (cargo, rustc, codex) via route=auto; the session's single
     biggest time sink.** Workaround used live: `cmd /c "... < nul"` under
     route=pwsh. Code fact: `ChildStdinGuard` NUL-backs STDIN only
     (handle -10); stdout/stderr (-11/-12) of the console-less server
     were never guarded, so console-handle-probing shims can still hit
     invalid handles. NEEDS A PROBE on this box (which handle, foreground
     vs rtk-routed vs jobs — job children get a closed-pipe stdin, not
     NUL) before any fix. This failure class is invisible to the current
     test suite.
  2. **HIGH value/effort — mojibake in native output (`ΓÇö` for em-dash):
     OEM-codepage (cp437/850) vs UTF-8 mismatch on the capture side.**
     Candidate: pin `[Console]::OutputEncoding`/native decoding to UTF-8
     in the server/runspace. Pollutes all native tool output today.
  3. **Timeout recycle surprised live use with changed command/PATH
     resolution in the fresh runspace.** v2 already improves this
     (ptk_state drift; teach-at-timeout), and env restore was
     deliberately reset-only — but the timeout message should also say
     command resolution may differ and point at ptk_state. Small.
  4. **Minor:** rtk's "no hook installed" nag rides along in routed
     output (candidate strip); route=auto sometimes swallowed a failed
     native's stderr where route=pwsh showed it (probe).
  5. **Validation, not work:** the note's #3 ("long work has no story",
     pattern hand-rolled 6-7 times) is exactly what D3 built; warm-state
     reliability and shaping fidelity got explicit praise.
- **2026-07-08 (latest): GREENFIELD SLICES D1/D2/D4/D3 BUILT and
  codex-closed.** `.agents/plans/greenfield-design.md` (approved same day,
  adoption entry in `.agents/decisions.md`) executed in full except D5
  (CLI-face retirement — deferred until after the go/no-go window by the
  plan's own call). What shipped:
  - **D1** (dfcc4f0): ANSI/control sequences stripped at text ingest in
    `Compress-PtcOutput`, before log-shape classification.
  - **D2** (c573a08): every text leg bounded — `Limit-PtcPassthrough`,
    400 lines / 40 KB head+tail with explicit elision markers naming
    raw=true; the old never-truncate contract test reconciled under the
    adoption decision.
  - **D4** (247fe72): `ptk_state` (engine/PID/uptime/cwd/modules/jobs +
    env DRIFT vs post-priming baseline, PATH as entry diff, variable
    count; `listAvailable` cached only on clean probes) SUBSUMES
    `ptk_modules` + `ptk_ping`, which are REMOVED. `ptk_reset` now
    restores the process environment to its server-start baseline
    (factory-state semantics; timeout recycles deliberately do not).
  - **D3** (d3efc2d): background jobs — `ptk_invoke background=true`
    (child pwsh, self-redirected log under `~/.ptk/jobs/`, session cwd,
    -ExecutionPolicy Bypass, parse errors land in the log, exit 64),
    `ptk_job` (status/output/kill/list; shaped bounded offset-paged
    polls), per-call `timeoutSeconds` capped by new
    `PTK_MAX_CALL_TIMEOUT_SECONDS` (default 3600), teach-at-timeout
    error naming both recovery paths, reset/graceful-shutdown kill jobs.
  - Codex loops: 7 findings (d1-1, d2-1, d2-2, d4-1(+b), d3-1..d3-3 —
    two MEDIUM), all fixed one commit each with guard proofs, final
    re-grade RESOLVED x4 / NO NEW FINDINGS (`.agents/review/index.md`).
  - **Canonical counts now: Pester 76, dotnet 57.** Handshake asserts the
    four-tool surface (ptk_invoke, ptk_job, ptk_state, ptk_reset) and
    calls ptk_state instead of the removed ptk_ping.
  - **Tool-surface break, owner action on next release/install:** the
    installed 0.2.0 binary still serves the old tools; a rebuild/
    dev-install is needed for the new surface. CI ci.yml runs the same
    battery unchanged.
  - **Environment finding (owner action):** `~/.codex/config.toml` still
    registers ptk as `dotnet run` on this repo — every `codex exec`
    (including this session's review loops) spawns/builds it and can
    leave a repo-bin `PtkMcpServer.exe` running, locking the build. The
    Claude Code registration already points at `~/.ptk/bin`; recommend
    updating codex's to match. Recovery that worked all session:
    `Get-Process PtkMcpServer | Where Path -like '*Powershell-Token-Killer*' | Stop-Process`.
  - All commits local/unpushed (master push stays owner-gated).
  Release-plan slice 3 unaffected, still queued. D5 execution note for
  the future session: closes `ptk` CLI verbs, `docs/usage.md`, their
  tests; README single-surface rewrite.
- **2026-07-08 (later): release-plan SLICE 2 DONE and codex-closed.**
  `.github/workflows/ci.yml` landed on master (74a2604): ubuntu/windows/
  macos matrix, current action majors (checkout@v7, setup-dotnet@v5),
  Pester `-CI` + `dotnet test` + default-mode handshake. Its first run
  caught a REAL product bug: the hosted runspace honors Windows execution
  policy and the server never set one, so any Windows box with no policy
  configured (CI runners, fresh user installs) got the hosted default
  (Restricted), which blocked the module import and silently degraded
  shaping/routing to Out-String — owner boxes had always passed only
  because they have a policy configured; Linux/macOS unaffected (SMA
  hardcodes Unrestricted off-Windows). Owner-approved fix (dddbb6b, a
  recorded scope addition to the release plan): pin
  `InitialSessionState.ExecutionPolicy = Bypass` on Windows in
  `RunspaceHost.CreateRunspace` — rationale: ptk_invoke runs script text,
  it replaces a harness tool that itself runs `pwsh -ExecutionPolicy
  Bypass`, and ptk is not a security boundary. Guard proven: the new
  regression test (forces Restricted process policy) fails without the
  fix, 37/37 with it, including under the Restricted repro
  (`$env:PSExecutionPolicyPreference='Restricted'; dotnet test ...`).
  Server suite canonical count is now 37. CI green on all three OSes at
  the branch head (run 28971482704); codex loop closed NO FINDINGS first
  pass on both commits (`.agents/review/index.md`). Branch bookkeeping:
  commits were cherry-picked from `ci/slice-2` (831bcc3/30f283d) to
  master; local branch deleted; REMOTE `ci/slice-2` deletion was blocked
  by the harness permission classifier and awaits the owner confirming
  (`git push origin --delete ci/slice-2`) — the no-lingering-branches
  condition is not yet satisfied for the remote. Master push (now 7 local
  commits) stays owner-gated. Next: slice 3 (release workflow).
- **2026-07-08: Windows battery GREEN; dev-install verified on this box
  (handoff items 1-2).** Pester 70/70 (0 skipped — the shim test runs here,
  ls stays unrouted), dotnet test 36/36, handshake passes in all three modes
  (default, `-UseRegistrationCommand`, and `-ServerCommand` against
  `~\.ptk\bin\PtkMcpServer.exe` from a neutral cwd; installed binary reports
  0.2.0.0). dev-install had already been run on this box (VERSION
  `0.2.0-dev.g9ec73fe`): ARP entry present and `winget list` surfaces it
  (`ARP\User\X64\ptk`), user-scope registration live. NOT verified:
  `-Uninstall` round-trip and the elevated/running-server refusals.
  Notes: `claude mcp list` flagged ptk defined in BOTH user scope (installed
  exe) and project scope (`.mcp.json` dotnet run) with different endpoints —
  both servers were live. RESOLVED 2026-07-08: owner removed the
  project-scope registration and the emptied `.mcp.json` is committed; the
  installed user-scope binary is the endpoint, and a checkout needs
  `scripts/dev-install.ps1` to get a server (the Mac has no install after
  the slice-1 round-trip test). Both instances were killed to unblock
  `dotnet test` (precedented exe lock); `/mcp` respawned on the installed
  binary.
- **2026-07-08: ptk MCP server live-use feedback recorded.**
  After ~10 calls in a real session, the owner reported the MCP server was the
  right tool and that warm runspace/state persistence is the standout feature,
  but also the main isolation hazard: variables and `$env:PATH` persist across
  calls, including test shims such as a fake `npm` prepended to PATH. Long work
  should use the background process + redirected output + polling pattern so
  each MCP call stays under timeout and preserves the live server. Output shaping
  preserved useful signal, including compile warnings, final artifact lines, and
  stderr as `[errors]`; minor polish gap: raw ANSI color sequences from tools
  such as vite surfaced unfiltered. Native command routing through rtk was
  transparent. Treat this as adoption evidence and open feedback items.
- **HANDOFF 2026-07-04 (end of day): owner moving to the WINDOWS box for
  testing; master pushed through the handoff commit (explicit owner go).**
  Everything below in this entry's sibling bullets is the day's context;
  the Windows session starts with `git pull`, then:
  1. **Battery:** Pester suite (70 tests; the 1 Unix skip — the .cmd/.bat
     shim test — RUNS on Windows, and the new ls platform test takes its
     `$IsWindows` branch: `ls` must stay unrouted there), `dotnet test`
     (36/36 expected), handshake default + `-UseRegistrationCommand`.
  2. **dev-install on Windows (the paths this Mac could not verify):**
     `pwsh -File scripts/dev-install.ps1` → check the Add/Remove Programs
     entry appears (HKCU uninstall key; `winget list` should surface it),
     user-scope registration works, handshake `-ServerCommand
     "$HOME\.ptk\bin\PtkMcpServer.exe"` passes from a neutral cwd, then
     `-Uninstall` removes payload+registration+ARP and leaves user files.
     The install refuses elevated shells and refuses while a `~/.ptk`
     server is running (clear message with the PID) — both by design.
  3. **Owner items for the go/no-go window:** install the hook
     (`scripts/ptk_init.ps1 -Global` or `dev-install.ps1 -Hook`), run the
     live hooked check in a fresh session (Bash + PowerShell tool calls
     should come back denied with ptk guidance), start the friction log.
  4. **Next build item:** slice 2 (`.github/workflows/ci.yml`, three-OS
     matrix; iterate on a `ci/*` branch — granted scope, delete after —
     master push per-go). Slice 3 after. Hook-default decision still open,
     needed before slice 4.
- **2026-07-04 (latest): release-plan SLICE 1 DONE and codex-closed; slice
  0 fully closed (CI probe ran — see the plan's probe results).** Slice 1:
  module discovery flipped to binary-dir-first (35fd472 + dc26c30, guard
  tests prove both the order and the cwd-fallback through the real
  composition) and `scripts/dev-install.ps1` landed (10d4a1a + b11eb66 +
  719fd85): publish→`~/.ptk` install, user-scope registration
  (remove-then-add), Codex snippet, Windows ARP entry, `-Hook`,
  `-Uninstall`, `-LayoutOnly -OutputDir` for release CI. Process: 3-lens
  pre-commit subagent review (10 findings fixed in-tree) + codex loop
  (rel1-1 tag-version normalization, rel1-2 TOML escaping — both fixed,
  re-grade NO FINDINGS; `.agents/review/index.md`). Full battery green on
  this Mac: Pester 69/0/1, dotnet 36/36, handshake all modes, install/
  uninstall round-trip leaves the machine in its pre-test state
  (`~/.ptk` removed, no user-scope registration, settings.json md5
  unchanged). NOT verified here: Windows ARP paths and a live `-Hook`
  install (slice 7 / owner's Windows box). Next slice: 2 (CI workflow) —
  iterate on `ci/*` (granted scope), the workflow file itself lands on
  master locally; the master push stays owner-gated.
- **2026-07-04 (later): release-plan slice 0 is DONE except the CI probe.**
  `-ServerCommand` mode landed in `server/test-handshake.ps1` (a8553dc,
  pre-commit multi-lens review fixes folded in) and the osx-arm64 probe
  results are frozen into the plan (8161af2): 45 MB tar.gz asset (129 MB
  unpacked), apphost ad-hoc signed, curl download quarantine-free and runs
  clean, quarantined-fresh-copy contrast SIGKILLed + `spctl` rejected
  (this box), published binary removes the dotnet-run build check from
  session start, module loads position-independently from the canonical
  layout via the BaseDirectory upward probe, `claude mcp add`/`remove`
  syntax confirmed on Claude Code CLI 2.1.201. Codex review loop on the
  slice: see `.agents/review/index.md`. PUSH GO GRANTED 2026-07-04 for
  `ci/*` + `v0.2.0-rc.*`, with the owner's hard condition: NO branches
  may linger once the coding is done — the agent deletes every `ci/*`
  branch (local and remote) as soon as its facts/workflows land on
  master; the owner never has to handle branches. Probe branches fork
  from origin/master (last pushed commit), NOT local HEAD, so unpushed
  master work is not published through a side branch. `master` pushes and
  the final `v0.2.0` tag stay per-explicit-go.
- **2026-07-04 (later): master's Pester battery was RED on this Mac —
  pre-existing, NOT slice 0 — now FIXED (owner go same day).** Clean
  master had 65 passed / 3 failed / 1 skipped: two "read README"
  assertions expected `pwsh_token_compressor` in the LIVE README (removed
  by the a43897a docs-pass rewrite; broke on every platform), and "leaves
  aliases and cmdlets on the PowerShell path" asserted `ls` stays
  unrouted, which only holds on Windows — on macOS/Linux `ls` is the
  native Application and the resolver routes it to rtk BY DESIGN
  (owner-ratified 2026-07-04: ls IS the shell command where a native one
  exists; Get-ChildItem is the PowerShell way; models shouldn't lean on
  aliases). Fixes: 849081d (deterministic temp fixtures) and d0e34d6
  (gci for the cross-platform alias case + a new test pinning the ls
  platform split via $IsWindows). Codex loop: NO FINDINGS first pass.
  Suite on this Mac: 69 passed / 0 failed / 1 skipped (70 tests — the
  canonical count going forward; the earlier "69/69" phrasing predates
  the added test). Lesson recorded: the old 69/69 record had not
  reproduced on identical code — suite greenness was machine-dependent
  until these fixtures were made deterministic.
- **2026-07-04 (late): RELEASE-DISTRIBUTION PLAN APPROVED — next action is
  slice 0.** `.agents/plans/release-distribution.md`, approved by owner
  in-session 2026-07-04 after question resolution and a codex review loop on
  the plan text (3 LOW doc fixes, all accepted — `.agents/review/index.md`).
  All commits from 7494edf onward are UNPUSHED (push needs owner go), and
  the plan's requested standing push scope (`ci/*` + `v0.2.0-rc.*`) was NOT
  separately confirmed — ask explicitly before the first CI push. Slice 0 =
  local osx-arm64 publish probe, `test-handshake.ps1 -ServerCommand` mode,
  CI runner probe (needs that push go). Owner set a first public release
  target of **2026-07-25**: prebuilt self-contained per-RID binaries on
  GitHub Releases + `install.ps1`/`install.sh` one-liners (tier 3);
  publish-and-register script and .NET-tool packaging are dev-only. Decision
  amendment recorded in `.agents/decisions.md` (continuation entry). Owner
  resolved the plan's open questions in-session the same day: **5 RIDs**
  (win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64 — owner has
  hardware for all five), **v0.2.0**, **one `~/.ptk` home** (payload+config,
  every platform and install method, no `--dir`), **winget = eventual
  primary Windows path** (installer-type only; v0.2.0 builds readiness: ARP
  uninstall entry, binary-hostable install logic, binary-relative module
  discovery — probe-order flip is an approved scope addition). STILL OPEN
  (owner: "decision for later", must close before slice 4): the public
  installer's hook default — tension recorded in the plan's Resolutions.
  Resume point: formal plan approval + the scoped push go (`ci/*` branch,
  `v0.2.0-rc.*` tags), then slice 0 (local publish probe, handshake
  `-ServerCommand` mode, CI runner probe incl. ARM runners). No code before
  approval.
- **2026-07-04 (earlier): docs pass PUSHED through a43897a.** README now
  leads with the MCP server as the primary use and documents rtk routing
  (four shaping legs, install-rtk encouragement, credits); server/README
  introduces rtk with the in-runspace rewrite detail; usage.md documents the
  `[exit] N` and PTC_TEMP-concurrency facts of `Invoke-PtcRun`. App name
  corrected everywhere: **PowerShell Token Killer** (`ptk`), named after
  rtk (Rust Token Killer); `PwshTokenCompressor` is only the module's
  on-disk name (repo-guidance mission line aligned).
- **UNIFIED SHELL ROUTING: BUILT 2026-07-04** — all five slices of the
  approved plan are committed and verified, each through the codex review
  loop (details in the plan and the commit messages):
  - Slice 0 probe results are recorded in the plan (rtk fidelity, cwd,
    chains, the Windows `rtk ls` gap, hook mechanics, latency).
  - Slice 1 (aa0ff12 + fixes): `Resolve-PtcInvokeScript` rewrites a single
    bare native-Application command with constant args to run through rtk;
    everything else runs as PowerShell unchanged; `route=auto|pwsh|rtk`
    argument on ptk_invoke; `raw=true` skips routing. Codex loop closed NO
    FINDINGS after fixes: $Error pollution (twice — resolver and
    Get-PtcRtkCommand), .cmd/.bat shim exclusion, Unix test stub, NUL handle
    lifetime.
  - **Load-bearing discovery (0a31364): natives that read stdin hung forever
    over stdio.** PowerShell hands a native command with no pipeline input
    the process stdin — the idle-but-open MCP JSON-RPC pipe — so bare git
    (any MSYS binary, sort, ssh) blocked until session end. Predates
    routing; never seen because no one had run a bare MSYS binary through
    ptk_invoke over stdio (live checks used cmdlets; rtk-routed calls mask
    it by wiring their own child stdio). Fix: ChildStdinGuard captures the
    transport streams, then points process stdin at NUL so children inherit
    EOF. Regression e2e spawns the real server over idle pipes and runs a
    stdin-reading native (60s hang without the guard, ~600ms with).
  - Slices 2-3 (e8ff3d7, 0f84988 + fixes): ptk_invoke repositioned as the
    single shell tool; `scripts/ptk-hook.ps1` (PreToolUse deny-with-guidance
    redirect for Bash+PowerShell, PTK_DIRECT escape hatch, fail-open,
    cwd-anchoring advice with apostrophe escaping) and `scripts/ptk_init.ps1`
    (rtk-init-style installer: local default/-Global/-Show/-Uninstall/
    -DryRun, idempotent, preserves foreign hooks surgically). Codex loop:
    cwd drop (High), shared-entry deletion, param-description mismatch,
    apostrophe escaping — all fixed with guard proofs; final one-line escape
    fix declared converged (replicates a codex-cleared pattern).
  - Verified 2026-07-04 (final battery): Pester 69/69, dotnet 34/34, both
    handshake variants, live stdio spot-check with real rtk (`[ptk:log via
    rtk]` + `[exit] 7` together), live in-harness routed `git status`.
  - **NOT DONE / OWNER ACTIONS:** (a) the hook is NOT installed anywhere —
    install with `pwsh -File scripts/ptk_init.ps1 -Global` (next session
    start), then run the live hooked check: a Bash and a PowerShell tool
    call should come back denied with ptk guidance and the model should
    re-issue via ptk_invoke; start the friction log the amended go/no-go
    needs. (b) ~~28 local commits unpushed~~ RESOLVED 2026-07-04: owner
    pushed everything through a43897a; only the plan commit 7494edf remains
    unpushed (see the release-plan entry above). (c) `/mcp` restart to
    respawn the live server on the final build (the last live instance was
    killed for the final rebuild) — not verifiable from the 2026-07-04 docs
    session; check whether it happened before reading a quiet ptk day as
    non-adoption.
  - Process notes for the record: two review-fix tests rode along in
    earlier commits instead of their own (5202756 carried the shim-test
    skip; 58990b1 carried the shared-entry test) — content correct, history
    not rewritten. Claude Code auto-respawns the ptk server after kills and
    each /mcp reconnect can leave an extra instance; every server rebuild
    this session needed a `Stop-Process -Name PtkMcpServer` first.
    PROPOSED (no go yet): a shadow-copy launcher so builds never collide
    with live servers.
- (COMPLETE — superseded by the BUILT entry above; kept as the approval
  record) Unified shell routing was the active work item:
  Owner-approved plan `.agents/plans/unified-shell-routing.md` (2026-07-04):
  ptk becomes the single shell tool — PowerShell → warm runspace, ANY
  non-PowerShell command line → rtk unconditionally (rtk passes through what
  it doesn't filter), log-shaped output → rtk log (exists) — plus a PreToolUse
  redirect hook on the harness Bash AND PowerShell tools, shipped via a
  `ptk_init.ps1` installer mirroring `rtk init` semantics. Decision basis: the
  2026-07-04 amendment in `.agents/decisions.md` (go/no-go now evaluates this
  product with the hook installed; operative criterion is experienced benefit
  + owner not disabling the hook). All plan open questions are resolved; the
  next action is slice 0 (rtk fidelity + hook mechanics probe, which freezes
  the design). Process: codex review loop after each slice (owner-set,
  2026-07-04): `codex exec --sandbox read-only` reviews each commit, real
  findings get fixed one-commit-each with guard proofs, iterate to NO
  FINDINGS or convergence (contrived-only Lows). Verified rtk facts for slice
  0: rtk's own global hook is installed on this box (`~/.claude/settings.json`
  PreToolUse, matcher `Bash` only — the PowerShell tool is uncovered),
  `rtk hook claude` is a native binary reading tool-call JSON from stdin,
  `rtk init --show` / `--dry-run` are safe read-only probes.
- 2026-07-04: the round-2 review (two findings on a8d3d02..HEAD) was FIXED
  under the approved plan `.agents/plans/review-fixes-2026-07-round2.md` with
  the codex-review loop per slice: (1) High — `Invoke-PtcRtkLog` snapshots and
  restores the caller's `$LASTEXITCODE` around the native rtk leg (the rtk
  invocation was clobbering the user script's exit code before the server read
  it; snapshot the VALUE, not the live PSVariable) — commit a798094, codex:
  NO FINDINGS; (2) Medium — dispatch guards completed in three codex rounds:
  every route guards all properties its compressor dereferences (ae1b9d6),
  files must have a KNOWN Length (null value or missing → generic; only
  directories legitimately lack it) (f86da5a), and every item must match the
  route's type name — one genuine FileInfo can no longer drag look-alike
  shapes of other types onto the fs route (ccc9686). All fixes guard-proven
  (revert → predicted failure → restore). Verified 2026-07-04: Pester 49/49,
  dotnet 30/30, both handshake variants pass, and a live stdio spot-check
  against the real winget rtk shows a log-shaped `exit 7` script rendering
  BOTH `[ptk:log via rtk]` and `[exit] 7`. The final codex pass on ccc9686
  landed after the handoff commit: one Low, self-labeled adversarial
  (calculated property returning a non-numeric `Length`/`WorkingSet64` value
  passes the guards, and direct `Compress-PtcObject` throws on the numeric
  conversion; the server path is already contained by `Compress-PtcOutput`'s
  never-throw shape-error fallback). Per the convergence rule the loop is
  CONVERGED and closed with this finding consciously not fixed — value-TYPE
  validation of every guarded property is out of proportion for a display
  heuristic with a raw escape hatch. Reopen only if a real (non-crafted)
  stream ever hits it.
- NOTE (2026-07-04): the live ptk MCP server was killed again this session to
  unblock `dotnet test` (PID 7696 held the exe lock — same precedented
  recovery); the owner needs an `/mcp` restart to respawn it on the current
  build. ~~Local commits through efe94c1 UNPUSHED~~ RESOLVED: owner pushed
  through a43897a on 2026-07-04.
- Owner pushed the day's work (a0a4819..4f943ea, including Phase 2) to origin on
  2026-07-03; only docs commits after 4f943ea may be local. On the push, the remote
  reported the repo MOVED to `AlsoBeltrix/PowerShell-Token-Killer` (capital S — the
  URL already recorded in `.agents/repo-guidance.md`); owner updated the local
  `origin` URL to match the same day. Pester: 31/31 on the Mac (2026-07-02); 29/31
  on the Windows box (2026-07-03) — the 2 failures were a pre-existing test-fixture
  sensitivity, FIXED later the same day (see the review-fixes bullet below).
- 2026-07-03 (night): five findings from an external (GPT-5.5) review of the Phase 2
  build were verified and FIXED under the approved plan
  `.agents/plans/review-fixes-2026-07.md`, one commit per finding: (1) ptk_invoke
  now surfaces nonzero native exit codes as an `[exit] N` block (with the CLI
  path's stale-guard, reset-before/read-after, mirrored into the server);
  (2) `Compress-PtcObject` dispatches to a specialized compressor only when EVERY
  item passes its property check, so mixed streams (FileInfo + string) compress
  via the generic path instead of degrading to `[ptk:shape ERROR]` raw fallback;
  (3) caller cancellation (user Esc) is now distinguished from timeout — the
  pipeline is stopped with a 5s grace and the warm runspace SURVIVES a clean
  stop (only a truly wedged pipeline still recycles), and the error says
  "canceled", not "timeout"; (4) `-MaxItems` + `+N more` now apply to
  property-less rows (scalar streams); (5) the two repo-root-sensitive Pester
  tests use deterministic temp-dir fixtures. Verified: Pester 43/43 (first fully
  clean run on this box), dotnet test 29/29, both handshake variants pass, plus
  an end-to-end stdio check of `[exit] 7` + stale-guard; guard proofs done for
  every behavior fix. Warm round-trip re-measured with the exit-code
  bookkeeping: avg 1.7 ms / max 3.5 ms over 20 stdio calls — no regression vs
  the ~3 ms baseline. Note: the live ptk servers were killed to unblock the
  rebuild (the exe was file-locked), so the session's MCP tools are down until
  an `/mcp` restart, which will respawn on the fixed build; a live Esc-abort
  spot-check in a real session is the one check not run headlessly (the
  mechanism is unit-tested).
- A 2026-06-27 design session explored a "universal PowerShell wrapper" rearchitecture
  (triggered by `ptk Get-ChildItem` printing help instead of running). No product code
  was written; the owner deferred the build decision. Recorded as an Open decision
  (b1e0550, docs-only). See `.agents/decisions.md`.
- A follow-on 2026-06-27 exploration looked at giving ptk a session-persistent
  warm-runspace backend (a stdio MCP server owning a `Runspace` that loads heavy
  modules / authenticated connections once). Recorded as a second Open decision in
  `.agents/decisions.md`. The core requirement is warm module load with no reload
  tax; unattended (cert-based) auth is the pattern for connection-bearing modules
  like EXO, not itself the requirement (owner correction 2026-07-02).
- 2026-07-02: owner selected the warm-runspace MCP server as the active work item and
  approved `.agents/plans/warm-runspace-mcp-server.md`. Slices 1-6 are built and
  verified: `server/` holds a net10.0 stdio MCP server (ModelContextProtocol 1.4.0 +
  Microsoft.PowerShell.SDK 7.6.3) with ptk_ping / ptk_invoke / ptk_modules /
  ptk_reset over a single warm runspace (serialized calls, timeout recycle, idle
  self-exit), registered in `.mcp.json` as `dotnet run -v q --project
  server/PtkMcpServer` (verified byte-clean stdout on cold build).
- Measured reload tax on this machine (Pester as the heavy module): cold per-call
  pwsh ≈ 460-500 ms every call; warm server pays 402 ms once, then re-import and
  module use round-trip at ~3 ms per tool call.
- Owner intent that frames future work: ptk is a personal/team tool complementing the
  owner's `headroom` PoC on Windows/PowerShell work, not an org-wide tool. The build
  trigger is measured benefit on real daily Windows usage, not faith. See
  `.agents/repo-guidance.md` for the generalized framing.
- 2026-07-02: governance refreshed from the AgentGovernanceBootstrap toolkit
  (`AGENTS.md` reconciled to the current template; repo-specific content carved into
  the new `.agents/repo-guidance.md` and `.agents/push-policy.md`).

- 2026-07-02 (late): owner stepped back and PAUSED all further building pending a
  go/no-go test. Evidence: headroom stopped (its context rewrites caused prompt-cache
  re-billing, net negative); rtk not adopted reliably via AGENTS.md instructions.
  Full reasoning and the test definition live in `.agents/decisions.md` ("Whether ptk
  continues at all"). Phase 2 compression, the universal wrapper, and the
  destructive-cmdlet gate are all behind that gate. No new plans until the test runs.
- 2026-07-03: owner UNPAUSED Phase 2 compression (amendment recorded in the
  continuation decision entry): build compression on ptk_invoke before the go/no-go
  so the test evaluates the full product. Scope: objects → Compress-PtcObject;
  log-shaped text → rtk when an rtk binary is present; all other text full
  passthrough; ollama leg dropped. ACTIVE plan (approved 2026-07-03):
  `.agents/plans/phase2-compression.md`. The universal wrapper and the
  destructive-cmdlet gate stay paused. Owner back at work ~2026-07-20 (was ~07-16).
- 2026-07-03 (later): Phase 2 slices 0-2 BUILT and committed. Module:
  `Compress-PtcOutput` (objects → Compress-PtcObject; log-shaped text → rtk with
  labeled raw fallback; all other text verbatim passthrough, never truncated;
  never-throw contract). Server: every runspace (create/reset/recycle) is primed
  with the module (`PTK_MODULE_PATH` override, else upward probe; import failure →
  stderr log + Out-String fallback) and ptk_invoke output is shaped unless the new
  `raw=true` argument is set. Verified: Pester 38 passing (+ the 2 known
  repo-root-fixture failures), dotnet test 25/25, both handshake variants pass;
  guard proofs done for module and server tests. Measured savings (chars/4
  estimate, TOOL-REPORTED — explicitly not the go/no-go benefit metric):
  Get-Process 2096→492 tok (76.5%), Get-Service 3689→861 (76.7%), Get-ChildItem
  -Recurse repo 47139→530 (98.9%), Get-Command 1737→1034 (40.5%); README.md text
  passthrough byte-identical. Object savings are summary-by-truncation (top-N +
  count) by design; `raw=true` is the escape hatch.
- Slice 3 (rtk binary) DONE 2026-07-03: owner installed rtk 0.43.0 via winget
  (resolves to `%LOCALAPPDATA%\Microsoft\WinGet\Links\rtk.exe` on the user PATH;
  an earlier agent-downloaded copy was refused by the permission classifier and
  discarded — owner install superseded it). Verified: `rtk log <file>` matches the
  interface the module uses (dedup summary + errors/warnings); rtk's "no hook
  installed" nag goes to stderr, which `Invoke-PtcRtkLog` already suppresses; the
  module log leg verified end-to-end with real rtk (`[ptk:log via rtk]` output).
  The SERVER discovers rtk via `Get-Command rtk` on its own PATH — true once
  Claude Code restarts from an environment that has the post-install user PATH;
  if it ever isn't, the leg degrades to its labeled raw fallback (visible, not
  silent), and `PTK_RTK_PATH` is the explicit override.
- Failure-mode drill result (observed during the rebuild): killing the server
  process mid-session bricks the ptk tools for the rest of the session — the
  harness marks them disconnected and does NOT respawn the server; only a session
  restart brings them back. Interpretation rule for the go/no-go weeks: a quiet
  ptk day needs a server-alive check before it is read as non-adoption. This
  session's live server was killed for the rebuild; the owner's `/mcp` restart
  respawned it on the new build without a full session restart (a second, gentler
  recovery path worth remembering alongside the drill finding above).
- 2026-07-03 (evening): Phase 2 LIVE CHECK PASSED in a real Claude Code session
  on the new build: objects → compact `fs:` summary; strings → verbatim
  passthrough; log-shaped output → `[ptk:log via rtk]` through the real winget
  rtk (the live server's PATH sees it — no PTK_RTK_PATH override needed);
  `raw=true` → plain Out-String table. Phase 2 is fully operational end to end.

- 2026-07-03 session findings (recorded here so they survive without chat):
  - PowerShell 5.1 compatibility, measured on this box: the module BODY imports and
    runs under Windows PowerShell 5.1.26100 — zero parse errors; fs, text, and
    MatchInfo paths work; only the process-object path fails ("Exception getting
    CPU": 5.1's ETS computes CPU from TotalProcessorTime, which throws on protected
    processes where 7 returns null). The manifest floor (`PowerShellVersion = '7.2'`)
    is the only import gate. So a 5.1 CLI backport is bounded (lower floor + one
    defensive access + dual-engine test runs; Pester 5.8.0 already sits in this
    box's 5.1 user scope). A 5.1 WARM SUBSTRATE would be new architecture — no
    NuGet package embeds 5.1; it would need a .NET Framework host or a persistent
    powershell.exe child. Parked, and capped by the decom horizon below. Note: a
    self-contained publish of the existing server xcopy-deploys to boxes with no
    .NET installed (engine is still 7.6).
  - Module-compat map for the go/no-go environment: on-prem Exchange management
    tools/EMS are 5.1-only through 2019/SE and will never load in the 7.6 runspace
    (owner-confirmed); the viable route is implicit remoting held inside the warm
    runspace (`New-PSSession -ConfigurationName Microsoft.Exchange` +
    `Import-PSSession` — plain WSMan/Kerberos, unofficial on 7, UNTESTED in this
    environment and the single most valuable slice-7 check). ActiveDirectory module:
    assumed PS7-native with current RSAT — unverified, no RSAT on this box. EXO and
    MSGraph: 7-native (owner-confirmed).
  - Decom horizon (owner, 2026-07-03): Exchange 2019 decommissions next year; the
    on-prem need lasts roughly 6-12 more months. This caps any 5.1-specific build —
    post-decom the workload is EXO+Graph, fully 7-native.


### From `## Next`

- **NEXT ACTION (2026-07-10): shell-dialect slice 2 — server wiring.**
  Slice 1 (detector) is DONE and its codex loop CLOSED CONVERGED
  (`.agents/review/index.md`). Slices 2-4 land one commit + battery +
  codex loop each.
- Review loop: shell-dialect slice-1 loop CLOSED CONVERGED 2026-07-09
  after four rounds (`.agents/review/index.md`); sd1-4 owner-unparked
  and fixed (`c43360c`) — all seven sd1 findings closed.
  Owner decisions still open: (a) prioritize new issues **#5**
  (`[errors]` mislabels exit-0 native stderr) and **#6** (queue-wait
  excluded from `timeoutSeconds`; `ptk_state` blocks behind a busy
  runspace) — both medium, untriaged, adjacent to but outside the
  shell-dialect plan's scope, (b) push go for the local commits
  (`3227607..809e0d0` plus the approval-recording commit).
- Slice 2 DONE (top Now entry). Next: slice 3 (release workflow,
  `.github/workflows/release.yml` on `v*` tags — per-RID publish on native
  runners, draft release; iterate on `ci/*`, granted scope). Still needed
  along the way: the hook-default decision before slice 4. Loose end from
  slice 2: the REMOTE `ci/slice-2` branch still exists pending owner
  confirmation of the delete. ~~test fixes~~ DONE 2026-07-04
  (owner go; battery green). The other questions (RIDs, version, install
  root, winget posture) are RESOLVED — do not re-raise them.
- ~~Execute unified-shell-routing slices~~ DONE 2026-07-04 (see Now). Next
  actions are the OWNER items in the routing entry: install the hook
  (`scripts/ptk_init.ps1 -Global`), run the live hooked check in a fresh
  session, start the friction log, push on go, `/mcp` restart.
- ~~Codex verdict on ccc9686~~ CLOSED 2026-07-04 (converged; disposition
  recorded in the round-2 entry above).
- 2026-07-02 headless adoption dry-run on this Mac (Sonnet, 19 trials, neutral cwd,
  ptk pre-approved via --allowedTools, tasks PowerShell-shaped but never mentioning
  ptk): **0/13 unprompted ptk usage**. The model used the harness's native PowerShell
  tool every time; the 6 control trials correctly used Bash. Even when the permission
  system blocked `Import-Module` in the native tool, it retried rather than discover
  ptk. Structural finding: the harness defers MCP tools behind ToolSearch (they are
  not in the upfront tool list — confirmed reachable when explicitly requested), so
  ptk's descriptions are invisible unless the model actively searches; against a
  native PowerShell tool it never does. This reproduces the rtk non-adoption pattern
  and is directly relevant to the 7/16 go/no-go: on the Windows box, expect adoption
  only if the native tool path is painful (cold EXO/AD auth per call) or the tools
  are surfaced/allowlisted prominently. Harness artifacts (runner + transcripts) were
  session-scratch, not kept in-repo.
- 2026-07-02 Codex condition: ptk registered in `~/.codex/config.toml` (dotnet run,
  absolute project path; Headroom proxy config removed the same day). Unlike Claude
  Code, Codex loads MCP tool descriptions upfront — gpt-5.5 found and called ptk_ping
  correctly. Headless `codex exec` auto-denies MCP tool calls ("user cancelled", ~1ms
  pre-flight; not overridable via --full-auto or approval_policy=never), so automated
  trials were not possible; interactive verification by owner succeeded (pong, after
  one-time allow). Asked directly, gpt-5.5 self-reported it would prefer ptk for
  multi-step/heavy-module PowerShell work (75-90%) but not one-offs (30-40%) —
  recorded as a self-report only: per the continuation decision, self-reported
  benefit is explicitly not evidence; observed unprompted usage on the Windows box
  remains the go/no-go criterion. No fresh-session behavioral trial run on Codex yet.
- 2026-07-03: Windows box brought up and the server validated there (the
  prerequisite for the go/no-go, NOT slice 7 itself — no AD/EMS/EXO modules were
  exercised). Two machine gaps found and fixed: the user-level NuGet.Config had an
  EMPTY packageSources list, so every restore failed NU1100 instantly — registered
  the default nuget.org feed; Pester 5 was not visible to pwsh (only inbox 3.4.0) —
  installed 5.8.0 CurrentUser via Install-PSResource. With dotnet SDK 10.0.301 and
  pwsh 7.6.3: `dotnet test` 19/19; stdio handshake PASSED both via the built dll and
  via the exact `.mcp.json` registration command from a cold build (stdout
  byte-clean); warm cross-call state verified. Module Pester suite 29/31: the two
  failures ("compresses filesystem objects before formatting" and "still routes real
  (deserialized) filesystem objects to the fs compressor") assert README.md survives
  `Get-ChildItem <repo root> | Compress-PtcObject -MaxItems 10`, but this box's root
  has 12 entries (a machine-local `.claude/` dir among them), so README.md falls
  past the cap. The fixture is the live repo root — environment-sensitive by design,
  pre-existing, not a Windows defect. FIXED 2026-07-03 (review-fixes plan, slice
  5): both tests now use deterministic temp-dir fixtures; suite 43/43 on this box.
- 2026-07-03 (later): owner enabled the MCP server on this box and the live-session
  check PASSED in Claude Code on Windows — full parity with the Mac check:
  ptk_ping → pong; ptk_invoke shares state across calls (same PID, variable set in
  call 1 read back in call 2, engine PS 7.6.3); warm module load confirmed (Pester
  cold import 545.7 ms, warm re-import 2 ms); ptk_modules lists loaded modules;
  script errors surface in an `[errors]` block; ptk_reset clears variables and
  modules without restarting the process. This box is now a fully working ptk
  environment; the remaining pre-07-16 items are the AWAITING OWNER GO list above.
- AWAITING OWNER GO, proposed 2026-07-03: ~~(a) push the local commits~~ DONE —
  owner pushed a8d3d02..9804eed (the review fixes and docs) to origin
  2026-07-03; ~~(b) deterministic temp-dir fixture~~ DONE 2026-07-03 via the
  review-fixes plan (slice 5); (c) fold the slice-7 test matrix below into the
  continuation decision entry; (d) pre-07-16 tests on this box (bring-up + warm-load
  measurement DONE 2026-07-03 — see the live-check bullet below): ToolSearch
  discoverability probe (do the ptk tool descriptions rank for
  "powershell"-shaped queries?); failure-mode drills (kill
  the server mid-session — do the tools respawn or brick? wedge a call past a
  short `PTK_CALL_TIMEOUT_SECONDS` — does the recycle keep the session usable?);
  and a headless "nudge ladder" adoption experiment (bare registration →
  allowlisted → one neutral CLAUDE.md line → explicit rule; small N,
  PowerShell-shaped tasks that never name ptk; burns API tokens). Related protocol
  proposal: run the go/no-go two-phase — week 1 pure/unprompted (the recorded
  criterion), then if adoption is zero add the minimal effective nudge and watch
  PERSISTENCE (this tests the experienced-benefit hypothesis); adopting it means
  amending the continuation decision entry, not quietly moving the goalpost.
- ~~Live-session check~~ DONE 2026-07-02: all four tools appeared in a live Claude
  Code session on this Mac. Verified: ptk_ping → pong; ptk_invoke shares state across
  calls (same PID, variable set in call 1 read back in call 2); module warmth matches
  the recorded reload tax (Pester cold import 448 ms, warm re-import 6.9 ms);
  ptk_modules lists loaded modules; script errors surface in an `[errors]` block;
  ptk_reset clears variables and modules without restarting the process. The go/no-go
  test on the real Windows box (below) is still the open item.
