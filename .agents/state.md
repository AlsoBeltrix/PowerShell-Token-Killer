# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

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

**cr3-2 was reopened FIVE times, each time for a real silent-loss path;
the fifth repair is landed at `449cea2` and awaits verification.**
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

**R3c and R3d are the honest remainder, neither started.** R3c: the
receiver still ingests protobuf over mTLS only, so PTK reaches Splunk,
Sentinel and any OTLP collector today but NOT its own fallback receiver;
it needs a token-auth + OTLP-JSON ingest path, and that touches the mTLS
boundary 255 receiver tests pin. R3d: the coordinated live-tail read
(cr3-1) and **acknowledgment-aware journal retention** (cr3-2's complete
fix) — four reopens argue that not deleting undelivered records beats
detecting the loss afterwards; detection should become the backstop, not
the primary defense.

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

**Immediate (audit restoration): await the cr3-2 round-6 verdict**
(codex, base `b23499b`, head `449cea2`). Five rounds have each found a
real path; if round six finds another, consider halting detection work
and going straight to R3d, whose acknowledgment-aware retention removes
the class rather than detecting it. Then, in order: **R3d** (acknowledgment-aware
retention + coordinated live-tail read), **R3c** (receiver token auth +
JSON ingest), **R4** (the loopback web GUI + settings page — the slice
that finally lets the owner SEE the logs), R5 conformance/alerts, R6
CI/docs/packaging. Plan: `.agents/plans/audit-restoration.md`;
R1 discovery record alongside it.

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

- Mini-SIEM S4's fixture gate remains intentionally closed: producer-owned
  exact v1/v2/v3 OTLP byte corpora must all exist before S4 begins. The
  producer-side serializer for current v1/v2 records is landed at `1f6d485`,
  but R0 supplies only JSONL v3 contract vectors, not a producer-owned
  serialized v3 OTLP request; do not synthesize one in the receiver or treat
  its hand-authored S2 structural test as a golden producer fixture.
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
