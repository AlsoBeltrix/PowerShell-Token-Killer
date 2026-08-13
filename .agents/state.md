# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

**MINI-SIEM S1-S8 COMPLETE; SIGNED FIVE-RID `0.3.0-rc.1` PRERELEASE
PUBLISHED (2026-08-12).**
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

**cr16 release-signing review ACTIVE (2026-08-13).** Claude Code 2.1.229 /
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
ShellCheck, `actionlint`, and diff hygiene pass. cr16-4 remains pending; one
Claude verification round remains.
`.agents/review/index.md` owns the loop.

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
work is committed and pushed, CI was green on all six jobs at `7cd4972`.

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

## Next

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

## Open / Parked

- Warm-backend slice 7 is unblocked open work, currently unscheduled, and
  remains owner-run Windows validation: AD native import/warm reuse; Exchange
  implicit remoting with first-vs-repeated `Get-Queue` latency; EXO/Graph
  unattended certificate auth. Its plan status was corrected 2026-08-03.
- Durable checkout and shared runspaces are removed from the candidate build
  scope by the owner's 2026-07-11 direction. Their older open-decision entry
  remains stale while `.agents/decisions.md` is under hold; the idea plan is
  retained as history/evidence, not current implementation direction.
- Fable's accepted R0 review noted one non-blocking test-fixture hygiene risk:
  if the testhost dies before its `finally`, the Unix guardian broker fixture's
  TERM-immune paused process group can remain until its fixture guardian is
  killed. This cannot make the guard falsely pass or affect an R0 product
  contract; consider an stdin-EOF guardian watch in a later scoped test-hygiene
  slice.

## Blockers

- **The invalid same-testhost cross-collection contention is repaired, but its
  residual signals remain open.** Before Slice 1a, six default-parallel runs
  exposed five intermittent fixed-watchdog/PATH failures while a serialized
  control passed. Slice 1a disables default collection parallelism
  (`server/PtkMcpServer.Tests/xunit.runner.json`,
  `parallelizeTestCollections: false`, re-confirmed present as of `78b2dbb`)
  without changing explicit concurrency tests, product deadlines, or
  assertions. This closes the scheduling artifact, not every underlying risk. A
  recurrence of the anchored-evidence publication/removal ordering race or
  `JobManager.Dispose` bounded-observer failure in a serialized run is still a
  real signal, and fixed watchdog sensitivity remains.

  The "hosted-CI evidence is absent" half of this item is falsified, and
  stays falsified as of `78b2dbb`: all six CI jobs are green on
  ubuntu/windows/macos. Suite counts
  belong to `.agents/repo-guidance.md` §Verification; the historical
  1,557-test figure this entry used to carry predates the router-delegation
  deletions and is dropped rather than refreshed. Later plan slices and the
  deployment gates still stand before any production-ready claim.
- **Direct ARM64 Linux build/execution validation needs a matching real host.**
  The prior UTM VM is not in use by owner direction. Cross-publishing from
  macOS is now correctly refused because it produced a Mach-O worker broker
  inside a `linux-arm64` layout. Do not claim this gate from cross-build output;
  run it on real ARM64 Linux when such a host is available. Historical UTM/
  `Grpc.Tools` evidence remains in `.agents/machines.md`, not as the current
  execution path.

- **Current Slice 10 generic Windows validation is pending — but no longer on
  `NETWATCH-01`'s return.** Basis corrected 2026-08-03: `ASHBIAMWEB1` ran the
  Windows leg on 2026-07-28 at head `7eaf8a0` — Windows x64 runtime/package,
  Job Object, and 100-cycle packaged production acceptance all passed
  (`.agents/machines.md` §`ASHBIAMWEB1`). What remains open is only that this
  is not the current head; rerun Windows packaging/process/Job Object/timeout/
  crash/cleanup acceptance at the head being shipped. That host's ordinary
  token still cannot close the SIEM symlink-protection cases.
- **The real AD/Exchange/EXO/Outlook workflow gate is partly closed; on-prem
  Exchange and Outlook remain open.** Basis corrected 2026-08-03. `ASHBIAMWEB1`
  is domain-joined to `ad.analog.com` and closed the EXO leg on 2026-07-29:
  app-only auth, an identity-bound `Get-EXOMailbox` read, warm-reuse latency,
  and retained selected properties (`.agents/machines.md` §Enterprise field
  validation). Still unclosed there: on-prem Exchange (no `ExchangeInstallPath`,
  no `RemoteExchange.ps1`, no `Get-Queue`), Graph (absent credential file, no
  parent window for WAM, device code expired), and Outlook (COM initializes
  after the STA fix but the namespace exposes no current user). The earlier
  `NETWATCH-01`-is-a-gaming-machine framing is retired — that host was never
  the only candidate.
- **Decision-log conflict, correction blocked by the owner hold:**
  `.agents/decisions.md` still describes the policy-file gate as the open
  response after its criterion fires, while the later explicit owner call in
  `.agents/plans/security-layer.md` rejects that response. Its shared-host
  entry stages durable GUID sessions followed by sharing, while the owner's
  later direction removes both from the candidate build. Do not implement
  either stale direction. Its mini-SIEM entry's **Current evidence** paragraph
  (the one citing `server/AUDIT-EXPORT.md` and the acknowledged OTLP export)
  also describes producer behavior that the corrected plan removes; treat that
  as known stale evidence after audit decision 4, never as authority to restore
  the producer. Preserve these decision-log conflicts until the hold is
  released. (A prior "line 312" pointer here was stale — line numbers drift;
  the anchor above does not.)
- **GitHub #7 closure is gated on Microsoft's WDSI verdict** on the submitted
  `PtkMcpServer.dll` (owner-submitted 2026-07-20). Interim quarantine-detection
  mitigation is landed (`51ce880`); no further local action on #7 until the
  verdict lands.
- **rbc-5 stays open, gated on resilience R7** landing its creation-time worker
  containment plus the Windows hard-supervisor-death background-descendant
  guard, with proof (`.agents/review/findings/rbc-5.md`). It is not gated on any
  local branch.

## Verification

- Automated verification entry point: `.agents/repo-guidance.md`
  (Verification). Review-loop evidence lives in `.agents/review/index.md`;
  do not duplicate volatile counts here.
- Audited-session Slices 0-6, Slices 7a-7h, and the Windows wait-ownership
  prerequisite are complete locally. Canonical fixed-head acceptance evidence
  lives in `.agents/review/index.md`; host-specific verification records live
  in `.agents/machines.md`.

## Active Sources

- `.agents/plans/github-release-packaging.md` (DRAFT; executes router Slice 7
  behind Decisions A-D, 3, and 5) — supersedes the packaging mechanics of
  `.agents/plans/release-distribution.md` slices 3, 4, 6, 7
- `.agents/plans/rtk-router-delegation.md` (APPROVED; Slices 0-6 executed,
  Slice 7 handed to the release-packaging plan) — supersedes the
  minimum-viable-release plan
- `.agents/plans/minimum-viable-release.md` (superseded; its release-blocking
  rule, non-goals, and packaging/proof slices are retained by reference from
  the router-delegation plan)
- `.agents/review/dispositions.md` (disposition of record for every `opr-*`)

- `AGENTS.md`
- `.agents/repo-guidance.md`
- `.agents/plans/production-reliability-salvage.md`
- `.agents/plans/release-distribution.md` (release outcomes remain active; its
  guardian/R7 mechanics are superseded at the top of the file)
- `.agents/plans/defender-fp-submission.md`
- `.agents/plans/mini-siem-discovery.md`
- `.agents/plans/mini-siem-implementation.md`
- `.agents/review/index.md`
- `.agents/machines.md`
- `.agents/decisions.md` (under owner hold; named stale entries are evidence,
  not current implementation authority)

The security-layer, broad RTK-rewrite, audited-harness, guardian-resilience,
warm-runspace, and shared-persistent plans are retained prior-art/history, not
active implementation pointers. The production-reliability plan controls where
their text conflicts with the implemented replacement.

## Unrecorded Repo Memory

- None known.

History: rotated entries live verbatim in `docs/history/state-archive.md`.
