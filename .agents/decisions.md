# Agent Decisions

Record durable repo decisions here. Do not use this as a chat log. Each entry should make
sense without conversation history and should name superseded guidance when relevant.

Keep this file to what is currently in force or still open. When a decision is
closed - superseded, or settled and retained only as the rationale for a rule that
now lives in its canonical home elsewhere - move it verbatim, in that same change,
to an archive under `docs/history/` (for example `docs/history/decisions-archive.md`);
never summarize or drop wording, the exact text is the record. Keep a single
pointer to the archive at the top of this file, not a stub per entry. The archive
is the provenance log; this file is what is in force or still open.

**Archive:** `docs/history/decisions-archive.md`

## Decision lifecycle

A decision moves through these states:

- **Open** - a finding has been assessed but not yet acted on. It lives in the
  `## Open Decisions` queue below, with the verified evidence, the options, and a
  standing recommendation. The process is unchanged until it is adopted; an agent
  records it rather than implementing on the spot.
- **Active** - a decision that is in force now.
- **Adopted YYYY-MM-DD** - an Open finding that has been acted on: its rule now
  lives in its canonical home (a procedure, template, or invariant). Note where the
  rule landed; the finding is retained in place as the rationale that led to it,
  until it is archived.
- **Superseded** - replaced by a later decision; name the replacement.

When an entry becomes purely historical rationale - Adopted or Superseded, with the
live rule now owned elsewhere - archive it per the rule above: move it verbatim to
`docs/history/`, do not leave a stub.

## Decisions

### ACTIVE (2026-08-06): PTK's own verdict travels as structured data, never as text

**Status:** Active — ruled by the owner on 2026-08-06 (finding `opr-53`,
option (b) over option (a)).

Supervisor status and recovery information must not share a channel with
worker output. A script that printed `[ptk worker] status=refused ...` or a
fake `recovery=available: ptk_output handle=...` had those lines preserved
verbatim beside PTK's genuine ones, indistinguishable to the model reading
them — a forged non-start invites resubmitting an already-executed mutating
command.

- PTK's decisions travel in the MCP protocol's `structuredContent`
  (`disposition`, `executed`, `safe_to_resubmit`, `detail`) and `isError`,
  which worker output cannot reach. The four session tools return
  `CallToolResult`; `ptk_output` still returns a string.
- **The response text is never escaped, framed, or rewritten to make the
  distinction.** Escaping mutates legitimate user output, and any escaping
  scheme is simply another grammar to forge. Text stays byte-for-byte.
- Only a **proved** non-start sets `isError` or `safe_to_resubmit`. An
  unknown outcome is not a non-start; saying "nothing ran" about work that
  may have run is the hazard this decision exists to remove.
- When adding a disposition at a new site, enumerate every producing path
  and ask of each whether the work had already begun. `NamedSessionException`
  is not uniformly a non-start (`descendants_unknown` is raised after the
  close acted), and a normal return is not uniformly a completion
  (`WorkerResultStatus.Refused` comes back without throwing).

This closes the deferred refusal→`isError` follow-up: `SupervisorCallFilter`
no longer re-derives a verdict from response text for these tools.

Landed at `11eafee`, `c40404a`, `18d76e8`. Detail:
`.agents/review/findings/opr-53.md`; review lessons:
`.agents/review/index.md` §o53.

### ACTIVE (2026-08-05): The capture invariant covers PTK's capture, not PowerShell's engine

**Status:** Active — ruled by the owner on 2026-08-05 (GitHub #38).

PowerShell's error-record construction invokes an exception's `Message`
override (twice, measured by the #38 guard) before PTK's output capture ever
sees the record. That engine behavior is outside PTK's boundary: the
invariant in force promises only that **the capture itself executes no user
code**.

- `TryFreezeErrorRecord` reads an untrusted exception's base-constructor
  message from `System.Exception`'s backing field — a field read executes
  nothing — and reports the type name. The `Message` override is never
  invoked by capture and its computed text is never reported.
- Do not build engine-side suppression to chase the upstream `Message`
  calls; they are PowerShell's own, not PTK's.
- Guarded by the invocation-counting #38 tests in
  `server/PtkMcpServer.Tests/RunspaceHostTests.cs`.

This supersedes the Slice 7.0-era reading under which an untrusted
exception's text was omitted wholesale.

### ACTIVE (2026-07-31): Release readiness is parked; every build gets a new identity

**Status:** Active — directed by the owner on 2026-07-31.

The global public release remains a future product target, not current work.
Do not ask for or settle release licensing, hook defaults, signing, publication,
or similar release decisions until the owner explicitly reactivates release
readiness. Current work returns to product defects and the GitHub issue backlog.

Every build must report a new user-visible build identity. Rebuilding the same
commit must not produce the same reported version, because operators must be
able to distinguish what is running on each host. The identity must be visible
from runtime state and installed/build metadata. Exact allocation mechanics are
deferred to implementation planning; this requirement is not optional.

The parked activation gates and later work are canonical in
`.agents/plans/release-readiness.md`. This decision narrows, but does not cancel,
the 2026-07-30 global-public-release target.

### ACTIVE (2026-07-30): PTK targets a global public release

**Status:** Active — directed by the owner on 2026-07-30.

PTK is a public product intended for global release, not a personal/team-only
tool. Product, documentation, packaging, security, compatibility, and support
decisions must be evaluated for unaffiliated users installing it on supported
platforms.

This target supersedes every current-file statement that limits PTK to personal,
team, or owner-only use. Historical archives retain their original wording as
provenance. The global target does not itself authorize a tag, public release,
push, or other outward action; those remain separately gated.

### ACTIVE (2026-07-30): Installed upgrades require a full PTK stop

**Status:** Active — directed by the owner on 2026-07-30.

Before installing a new PTK version, all PTK processes must be stopped. The
installer must refuse to replace the installed payload while PTK is running;
the operator then restarts affected MCP client sessions after installation.
That disruption is accepted product behavior.

Do not retain old runtimes, add a stable native launcher, introduce
`active.json`, or build transparent live-session cutover, rollback, or prune
machinery. `.agents/plans/mcp-side-by-side-upgrade.md` is abandoned and retained
only as historical review evidence.

Prefer the smallest design that solves a demonstrated recurring problem.
Continuity, resilience, compatibility, or migration architecture beyond the
current need requires concrete evidence and explicit owner approval; speculative
"keep it running at all costs" mechanisms are not product goals.

This decision supersedes the five 2026-07-29 side-by-side decisions below
(`ssu-1`, `ssu-3`, `ssu-4`, `ssu-5`, and `ssu-6`).

### SUPERSEDED (2026-07-29): Side-by-side registration uses a native stable launcher

**Status:** Superseded by the 2026-07-30 full-stop install decision. Originally
approved by the owner in-session on 2026-07-29 as the
`ssu-1` plan disposition.

Every PTK-managed harness registration must point to a stable, per-RID native
launcher below `~/.ptk/launcher/`, not to `pwsh`, `dotnet`, or a versioned
runtime path. The packaged launcher must require no separately installed
PowerShell or .NET runtime, preserving the existing self-contained distribution
contract. On Unix it replaces itself with the selected immutable runtime. On
Windows it must inherit the client stdio handles and prove kill-on-close
containment for the selected runtime before that child is resumed. Slice 0 must
prove stdout transparency, teardown, exit-code propagation, path handling, and
a complete registered-command handshake with `pwsh` unavailable; failure stops
the architecture.

Canonical implementation detail and guard scope live in
`.agents/plans/mcp-side-by-side-upgrade.md`. This decision settles only `ssu-1`
plan design. It does not authorize implementation, installation, push, or any
other outward action.

### SUPERSEDED (2026-07-29): Registration migration is transactional per harness

**Status:** Superseded by the 2026-07-30 full-stop install decision. Originally
approved by the owner in-session on 2026-07-29 as the
`ssu-3` plan disposition.

PTK must not use one blind remove/add sequence for every harness. Before changing
a recognized managed registration, the installer snapshots the exact
registration files and proves the native launcher independently. Codex is
updated in place without `codex mcp remove ptk`, preserving its PTK
tool-approval subtables and unrelated TOML. Grok's installed CLI must first pass
add/remove behavior against a disposable user-scoped configuration; failure
preserves the live registration and aborts before activation. Claude CLI
replacement and Agy's PTK-owned/global JSON forms each use their own
fixture-backed transaction. Every changed registration must complete a
registered-command five-tool handshake or the installer restores all changed
harness files byte-for-byte. A custom registration is preserved.

Canonical mechanics and guards live in
`.agents/plans/mcp-side-by-side-upgrade.md`. This decision settles only `ssu-3`
plan design. It does not authorize implementation, installation, push, or any
other outward action.

### SUPERSEDED (2026-07-29): The registered launcher path is never removed

**Status:** Superseded by the 2026-07-30 full-stop install decision. Originally
approved by the owner in-session on 2026-07-29 as the
`ssu-4` plan disposition.

The stable native launcher has its own persistent `~/.ptk/launcher/` directory,
which is excluded from wholesale payload-directory replacement. After a managed
registration names the launcher, install, upgrade, and rollback must never
delete or rename that containing directory and must never expose an absent
launcher path. A launcher update is staged and validated as a sibling file, then
replaces only the launcher file in one platform-specific operation. A failed
pre-replacement update leaves the old launcher untouched; rollback uses the same
file-level protocol. Ordinary runtime upgrades do not touch unchanged launcher
bytes and commit only by replacing `active.json`.

The exact Windows replacement primitive and contention semantics remain the
separate `ssu-5` decision. If a running mapped launcher cannot be replaced while
preserving the stable-path invariant, the installer must leave it unchanged and
fail closed; it must not fall back to remove-then-move. Canonical mechanics and
guards live in `.agents/plans/mcp-side-by-side-upgrade.md`. This decision settles
only `ssu-4` plan design and authorizes no implementation or outward action.

### SUPERSEDED (2026-07-29): Control-file replacement uses named OS primitives

**Status:** Superseded by the 2026-07-30 full-stop install decision. Originally
approved by the owner in-session on 2026-07-29 as the
`ssu-5` plan disposition.

`active.json` and stable-launcher updates use one canonical same-directory
replacement helper. On Windows it mirrors the repository's existing protected
file publication: flush the sibling temporary file, then call
`SetFileInformationByHandle(FileRenameInfoEx)` with
`FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS`.
Activation readers open with read/write/delete sharing and consume one bounded
file handle, so concurrent readers see complete old or new bytes. A sharing or
replacement error gets no delete-first fallback and no retry: the old
destination remains selected and the transaction fails before activation. On
Unix the helper flushes the sibling temporary file, calls same-directory
`rename(2)`, then flushes the parent directory.

Kernel replacement success is the commit point. A later failure never rolls
back by deleting the destination; startup/recovery validates whichever complete
record is present. This adds no stronger arbitrary-power-loss guarantee than the
plan already claims. Canonical mechanics and guards live in
`.agents/plans/mcp-side-by-side-upgrade.md`. This decision settles only `ssu-5`
plan design and authorizes no implementation or outward action.

### SUPERSEDED (2026-07-29): Runtime launch verification is constant-bounded

**Status:** Superseded by the 2026-07-30 full-stop install decision. Originally
approved by the owner in-session on 2026-07-29 as the
`ssu-6` plan disposition.

The installer performs the complete manifest inventory and per-file hash check
when publishing or reusing an immutable runtime. A normal client launch does not
enumerate or hash that runtime tree. It reads the bounded `active.json`, reads
and hashes one bounded canonical `manifest.json` to match the selected digest,
validates containment and no-link/reparse-point rules, and checks that the
selected server executable is a regular file. This keeps launch I/O bounded
independently of runtime size.

The explicit tradeoff is that post-install runtime-file contents, including the
selected server executable bytes, are not rehashed on every connection. The
protected per-user install directory is the primary boundary; complete
verification remains mandatory at install/reuse time. Canonical bounds and
guards live in
`.agents/plans/mcp-side-by-side-upgrade.md`. This decision settles only `ssu-6`
plan design and authorizes no implementation or outward action.

### ACTIVE (2026-07-09): shell-dialect plan approved — `.agents/plans/shell-dialect.md`

**Status:** Active — approved by owner in-session 2026-07-09. The plan's
decision points stand as recommended: D1 = (a) detected bash-only shapes get
a fast labeled refusal naming the construct and the platform-aware recovery
paths (no auto-translation; `route=pwsh` and `raw=true` bypass as consent);
D2 = non-breaking raw posture (reword every model-visible raw surface to
recovery-only, raw-usage visibility via server log line + `ptk_state`
counter at the user-call boundary only; gating/justification declined);
D3 = one dialect line in hook deny + ptk_init nudge + README routing
section.

**#4 reconciliation at approval:** the cross-model comment's four
acceptance suggestions were folded into D2 — adopted: no-preemptive-raw
(the recovery-only rewording), teaching `route=pwsh` + `raw=false` as
"exact execution, shaped output" (joins the reword inventory with slice-3
assertions), raw telemetry in `ptk_state`; declined: reason/cost gate on
unjustified raw (friction on a deliberate escape hatch; revisit only with
evidence that rewording fails). Slice 0 probe results freeze into the plan
before implementation; slices 1-4 land one commit + battery + codex loop
each.

**Amendment (2026-07-09, owner unparked sd1-4 in-session):** the slice-0
`set` exemption is narrowed — `set -e/-u/-x/-o pipefail` is flagged only
while `set` still resolves to the stock `Set-Variable` alias; an ambient
re-pointing or a preceding script-local `Set-Alias`/definition suppresses
the finding (fix `c43360c`). Rationale: the exemption predated the
detector's resolution-guard machinery and had become the lone exception to
the uniform "a name that works in this session is never bash evidence"
rule, in conflict with the plan's precision-first principle.

**Amendment (2026-07-10, sd3-1 adjudication — owner delegated the call
in-session):** D2's "every reworded surface that describes raw also names
the route=pwsh + raw=false pairing" is scoped to surfaces serving the
FIDELITY motive (tool and parameter descriptions, the nudge block, the
README/server-README override sections — all of which now teach it). The
in-band elision markers and the sentences that describe them advise only
`raw=true`, because elision applies on every shaped route: the pairing
cannot recover an elided middle, and teaching it at that moment would be
false advice (live proof in `.agents/review/findings/sd3-1.md`). Decided
under the owner's agent-experience principle, recorded in
`.agents/repo-guidance.md` (Earned Practices).

### ACTIVE (2026-07-08): Greenfield design adopted — `.agents/plans/greenfield-design.md`

**Status:** Active — approved by owner in-session 2026-07-08 after the codex
review loop on the plan text closed (gfd-1..gfd-4 fixed, re-grade RESOLVED
x4 / NO NEW FINDINGS; `.agents/review/index.md`). The plan's three
decision-point calls stand unoverridden: passthrough bounds 400 lines /
40 KB with `raw=true` unbounded; background jobs are child `pwsh` processes
(no warm session state); D5 (CLI-face retirement) executes after the
go/no-go window.

**What it changes, durably:**

- **Amends the Phase 2 passthrough contract** (2026-07-03 amendment in the
  continuation entry below): plain-text output of `ptk_invoke` is no longer
  "full passthrough, never truncated" — every text leg is bounded by a
  generous labeled head+tail window; completeness moves to the explicit
  `raw=true` escape hatch. Rationale: boundedness outranks
  completeness-by-default in a tool whose one job is protecting context
  (plan, principle P3).
- **Closes the universal-wrapper open decision by dissolution** —
  `ptk_invoke` is the universal surface, so "should the CLI dispatch any
  cmdlet" no longer has an object. The CLI face itself is retired by D5
  (deferred post-window). The entry moved verbatim to
  `docs/history/decisions-archive.md` in this change.
- **Execution scope:** D1 (ANSI strip at text ingest), D2 (bounded
  passthrough), D4 (`ptk_state` drift report subsuming
  `ptk_modules`/`ptk_ping`), D3 (background jobs), in that order, each
  slice committed and codex-looped; D5 deferred.

**Unchanged:** the go/no-go gate itself (decided GO 2026-07-08 and archived
to `docs/history/decisions-archive.md`), the release-distribution plan
(slice 3 still queued), the destructive-cmdlet pause, and the
not-a-security-boundary threat model.

## Open Decisions (deferred - not yet adopted)

### OPEN (2026-07-08): Destructive-cmdlet policy gate (carried out of the archived go/no-go entry)

**Status:** Open — parked on its own criterion, which survives the
2026-07-08 GO decision. The full three-iteration design record (two
rejected variants and the tentatively acceptable declarative policy file:
outside-workspace config, default read-only, classification via
SupportsShouldProcess/ConfirmImpact + alias resolution, fail-closed on
unknowns/natives) lives verbatim inside the archived continuation entry in
`docs/history/decisions-archive.md`.

**Criterion (unchanged):** keep `ptk_invoke` on ask-per-call in the
harness; build the policy gate only if real usage creates the desire to
blanket-allow `ptk_invoke`. All variants are guardrails against model
sloppiness, NOT security boundaries (recorded threat model).

### OPEN (2026-07-08): Whether to build a shared multi-client warm host (+ shared signals)

**Status:** Open — recorded from owner-shared design notes
(`F:\notes\PTK\shared-warm-runspace.md` and `shared-warm-runspace-plan.md`,
machine-local; decision-relevant core captured here). No code authorized;
the notes' own slice 0 is "approval and probe".

**Question:** Should ptk grow an optional long-lived host with a local
multi-client transport (named pipe / Unix socket), so multiple harness
sessions attach to ONE warm PowerShell session — modules, connections, cwd,
env shared across clients — plus a structured ephemeral signal store
(`ptk_signal`: add/list/update/close with actor, kind, scope, TTL) for
agent-to-agent coordination that does not abuse PowerShell variables?

**What it would bring:** heavy imports and unattended connects (AD, EXO
cert, Graph, implicit remoting) paid once per machine, reused by every
attached agent; warm state survives harness lifecycle (attach/detach, not
cold-start per chat); one place for drift/reset hygiene; fast
reviewer/implementer/verifier handoff via signals.

**Dominant gotchas (the notes' own analysis):** runspaces stay
single-pipeline, so sharing serializes agents behind each other — shared is
not faster; one timeout recycle evicts warm state for EVERY client (the
biggest operational hazard); not-a-security-boundary becomes cross-agent
lateral movement (any client reads/mutates every other's state — same OS
user, local-only, explicit opt-in are hard prerequisites); cwd/env/PATH are
one namespace, so unrelated-project agents mostly want isolation, not
share; reset semantics change from "fix my mess" to "evict everyone".

**The notes' recommendation, adopted as this entry's standing
recommendation:** do NOT make shared mode the default; build it only if
real use shows repeated reauth/reimport across sessions on one box (or
real multi-agent handoff need) — measured pain, not anticipated. If built:
attach-only hard share first (one host, one serialized runspace, full
shared state, loud shared timeout/reset messages, client identity in
ptk_state), signals in that same first version, private mode unchanged as
default. Named sessions and any private-variable multi-tenancy only if the
narrow form earns it.

**AMENDED 2026-07-10 (owner adjudication, in-session): the staging above
is superseded.** The owner explicitly set the notes' attach-only-first
preference aside: the enabler (standing host + attach-by-key) is the same
for both features, so persistence ships FIRST — GUID-keyed sessions,
process-per-key, ONE client per key — and sharing (a second client on an
existing key, opt-in) ships second as the increment that adds the
between-calls contract. Staged sketch and hard-problem mapping live in
`.agents/plans/shared-persistent-runspace.md`. Unchanged: private mode
stays the default, the measured-pain criterion still gates any build, and
no build is approved yet.

**Gate interaction:** behind the go/no-go like everything else, and behind
its own measured-pain criterion even after a go. The v2 greenfield design
(2026-07-08 adoption entry above) is the private-session product this
would extend; nothing in it blocks or presumes sharing.

### OPEN (2026-06-27): Whether to give ptk a session-persistent warm-runspace backend

**Status:** Open - selected as active work by owner 2026-07-02 and BUILT the same
day: slices 1-6 of `.agents/plans/warm-runspace-mcp-server.md` are complete,
verified, and pushed (server in `server/`). Slice 7 (Windows AD/EMS/EXO module
validation) was paused behind the go/no-go gate; that gate was decided **GO
2026-07-08** (owner, in-session — entry archived to
`docs/history/decisions-archive.md`), so slice 7 is unblocked open work.
This is the **substrate** counterpart to
the universal-wrapper decision
above: that entry settled that the universal path MUST run in-process to preserve a
warm host; this entry asks where that warm host should come from when the harness does
not happen to provide one.

**Question:** Should ptk own a persistent PowerShell host - a single long-lived
runspace that loads heavy modules (`ActiveDirectory`, `ExchangeOnlineManagement`) and
establishes their authenticated connections **once**, then serves many agent tool
calls from that warm state for the life of a coding session? And if so, in what form?

**What triggered it:** The universal-wrapper evidence showed the owner's on-prem
`Get-Queue` workflow only works because the agent happens to be running *inside* an
already-open EMS host whose implicit-remoting PSSession persists in that process. That
is incidental, not architectural: it is not portable, not reproducible from a cold
harness, and covers only the modules/connections that ambient host already loaded.
Cost driver is concrete - a cold per-call `pwsh` reloads modules and re-authenticates
every call (on-prem EMS connect is 30s+; EXO/Graph connects cost auth round-trips). The
question is whether ptk can provide that warm host *deterministically* instead of
depending on an ambient one.

**Verified evidence gathered this session (keep - expensive to re-establish):**

- **A stdio MCP server is the one Claude Code mechanism that gives a session-scoped
  warm process.** It is launched once and runs as a single long-lived child process
  for the whole session; tool calls are JSON-RPC to that same process, so an in-memory
  .NET object / PowerShell `Runspace` it creates persists across calls. (claude-code-guide
  agent, citing Claude Code MCP docs at code.claude.com.)
- **Per-tool-call timeout is generous.** `MCP_TOOL_TIMEOUT` default is ~28h; a per-server
  `timeout` (ms) in `.mcp.json` overrides it. There is a hard wall-clock cap per call
  and progress notifications do not extend it, but there is ample headroom for module
  load / connection setup. The 5-minute idle timeout applies to remote HTTP/SSE servers,
  not stdio. (same Claude Code MCP docs.)
- **The Bash-daemon alternative fights the harness.** The Bash tool is not a persistent
  shell - each call is a separate process, env vars do not persist, and background
  processes started via Bash are killed on session end or orphaned (open Claude Code
  issues #25188, #43944). `SessionEnd` hooks are non-blocking and not guaranteed to run
  on crash / Ctrl-C, so they cannot be relied on to tear a daemon down. The dedicated
  PowerShell tool (`CLAUDE_CODE_USE_POWERSHELL_TOOL=1`) has the same per-call-process
  model and does not help. (tools-reference.md, hooks-guide.md, the two issues.)
- **No official PowerShell MCP SDK.** The practical path is a .NET stdio server hosting
  `System.Management.Automation` and owning the `Runspace` in-process (tightest fit), or
  a Node/Python stdio server shelling into a persistent `pwsh`. (claude-code-guide.)
- **Headless EXO auth must be certificate-based app-only.** `Connect-ExchangeOnline`
  with MFA is interactive and cannot run inside a non-interactive server process;
  app-registration + certificate (`-CertificateThumbprint -AppId -Organization`) is the
  supported unattended path and the direction Microsoft is steering tenants toward.

**Settled sub-decisions (conditional on building it at all):**

- **Transport = stdio MCP server, not a Bash-spawned daemon.** Claude Code owns the
  lifecycle (start at session start, kill at session end), which sidesteps the
  background-process persistence/teardown bugs above. The Bash-daemon option is rejected
  on reliability grounds, not just cleanliness.
- **Implementation = .NET stdio server hosting `System.Management.Automation`**, owning a
  single `Runspace` in-process. The `PwshTokenCompressor` module loads once in that same
  runspace.
- **Core requirement = modules load once with no reload tax across calls.** Heavy
  modules (`ActiveDirectory`, `ExchangeOnlineManagement`, etc.) import into the warm
  runspace on first use and stay loaded. For connection-bearing modules, unattended
  auth (e.g. app-registration + certificate for EXO) is the supported pattern — no
  interactive `Connect-*` in the server. EXO is an example, not the defining case.
  (Corrected 2026-07-02: an earlier version of this entry recorded cert-based EXO
  auth itself as the hard requirement; owner clarified the requirement is warm module
  load generally.)
- **One serial runspace, not a pool.** Cmdlets and implicit-remoting PSSessions are not
  thread-safe; serialize calls. A per-call timeout recycles the runspace on a wedge
  rather than hanging the session. Reach for a `RunspacePool` only if real parallelism is
  ever proven necessary.
- **Module strategy = enumerate `Get-Module -ListAvailable` at startup, lazy-load + cache
  on first use.** Expose `ptk_modules` (available/loaded) and `ptk_reset` (recycle the
  runspace / clear leaked `$global:` / cwd / `$PSDefaultParameterValues` state).
- **Substrate vs shaping stay separate.** The runspace is *where* a command runs; output
  still flows through `Compress-PtcObject` (objects, lossless) before return. The
  `experiment/ptk-router` branch (rtk for logs, ollama for prose, deterministic text
  filter otherwise) is the *shaping* layer behind a `ptk_invoke { <scriptblock> }` tool -
  it is complementary, not an alternative.
- **Lifetime is managed inside the server** (idle self-timeout + idempotent
  startup cleanup), never via `SessionEnd`, which is not guaranteed to fire.

**Relationship to the universal-wrapper decision:** complementary, not competing
(and see the 2026-07-02 continuation decision below, which now gates all further
work on both entries). The universal wrapper is the *surface* (`ptk <cmdlet>`); the persistent runspace is the
*substrate* (a deterministic warm host). The MCP tool is the portable replacement for
"the agent happens to live in a warm EMS host." If both are built, `ptk_invoke` runs the
cmdlet inside the owned runspace.

**Historical deferral rationale (superseded 2026-07-30):** this originally
treated PTK as a personal/team complement to `headroom` and required measured
benefit on the owner's daily Windows work. The current global-public-release
decision replaces that audience constraint. The historical evidence threshold
does not limit work required for a safe, supportable public product.

**Standing recommendation (for whoever picks this up):** Do not build the server first.
(Superseded in practice 2026-07-02: the owner chose to build the server without the
step-1 measurement; it is built. Retained for the record.)
(1) Quantify the pain - count cold `Import-Module` / `Connect-*` invocations and their
latency over a week of real sessions; if the ambient-warm-host accident already covers
the daily workflow, the deterministic host may not pay for itself yet. (2) If material,
prototype the smallest possible .NET stdio MCP server exposing one tool
`ptk_invoke { <scriptblock> }` against a single warm `Runspace` with cert-based EXO
preconnect, returning `Compress-PtcObject` output. (3) Only then add the module map,
`ptk_reset`, and the router shaping layer. Each step is a separate authorized change
requiring its own go.

### OPEN (2026-07-14): Whether PTK should ship a mini SIEM receiver for external audit custody

**Status:** Open — explicitly appended by the owner to the end of the decision
queue. No implementation is authorized, and this scoped addition does not
release the hold on broader decision-log reconciliation.

**Question:** When Microsoft Sentinel, Splunk, or another robust SIEM is
unavailable, should PTK ship or maintain a small external receiver so anchored
audit records leave both the PowerShell runspace and the PTK source machine,
receive secure durable custody, and remain useful for basic investigation and
alerting?

**Current evidence:** PTK already exports one immutable core record at a time
over authenticated OTLP/HTTP HTTPS and advances its checkpoint only after a
valid nonrejecting acknowledgment. `server/AUDIT-EXPORT.md` makes the configured
receiver the observable anchor boundary and requires durable commit under a
separately administered principal before success. A same-user sidecar or an
in-memory collector is not a meaningful anchor; a default in-memory
OpenTelemetry pipeline therefore does not answer this question.

**Options to assess:**

1. Ship a PTK-maintained minimal OTLP receiver with durable-before-ack storage,
   authentication, chain/event validation, bounded query, retention, and basic
   alerts.
2. Ship only a hardened deployment profile and validation harness for existing
   lightweight components that together provide the same external durable
   boundary.
3. Ship no fallback receiver and require an independently operated SIEM or
   durable OTLP service for anchored mode.

**Acceptance questions before any build:** define the threat model and separate
service identity; durable-before-`200` semantics; duplicate handling for PTK's
at-least-once delivery; event-ID/hash-chain validation; crash, disk-full,
backpressure, and restart behavior; mTLS or equivalent authentication; receiver
host storage protection; retention and read authorization; minimum useful
queries/alerts; upgrade/backup/recovery ownership; and the security patch burden
created by exposing a network service.

**Standing recommendation:** discovery first, not implementation. Compare the
smallest existing durable OTLP deployment against a custom receiver using the
criteria above. Build PTK-specific receiver code only if no supportable existing
shape provides the required external boundary at acceptable operational cost.

### DECIDED (2026-07-15): mini-SIEM — Option 1 approved; implementation authorized (S0)

**Status:** Decided — the owner authorized implementation in-session on
2026-07-15; this entry was recorded by the working session at explicit owner
direction ("implementation is authorized. record it"). It resolves the OPEN
(2026-07-14) entry above for this item only and does not release the hold on
broader decision-log reconciliation.

**Decision:** Option 1 — PTK ships and maintains a minimal OTLP receiver as a
separate product under `siem/` (`siem/PtkSiem.slnx`), with the expanded product
scope recorded in the approved plan.

**Plan of record:** `.agents/plans/mini-siem-implementation.md` @ `87e4206`,
approved by the owner as reviewed (codex review loop, 3 rounds; post-loop
remediation of msi-7, msi-15, msi-20..msi-24 recorded in-document with explicit
tradeoffs, including the msi-15 residual-risk acceptance).

**Authorized shared-tree edits (exhaustive):** the CI job addition in
`.github/workflows/ci.yml`, and one additive test-only commit under
`server/PtkMcpServer.Tests` (opt-in exporter endpoint override + golden
v1/v2/v3 fixture serializer), owner-approved before merge. All other work stays
under `siem/`.

**Effect:** Code slices S1+ are authorized as of this entry. The OPEN entry's
acceptance questions are answered by the plan's S1-S7 sections and threat-model
matrix; the standing "discovery first" recommendation was satisfied by
`.agents/plans/mini-siem-discovery.md` before this authorization.

## 2026-07-19: Triage delegation for the rbc review batch

**Decision:** The owner (non-developer) delegated finding triage to the
maintaining agent. Protocol: adversarial self-verification against the
code at a fixed SHA; contested findings go to a codex MCP follow-up
capped at 3 turns; escalate to the owner only on no-consensus.

**Applied:** rbc-8..rbc-14 at master `ec4d292` — dispositions recorded
in `.agents/review/index.md` (triage log) and the finding records.
Contested: rbc-8 (downgrade+defer) and rbc-13 (refute as by-design);
codex consensus AGREE on both at turn 1 (thread
`019f7cb9-c587-79c1-994b-a28e8d7b1ba1`).

## 2026-07-19: SIEM receiver deployability gated on S3H (rbc-11)

**Decision:** Master builds of `siem/PtkSiemReceiver` are not
deployable for unattended ingest: retention options are parsed but not
enforced, so the SQLite store grows without bound (rbc-11, MAJOR).
Deployment guidance is gated on the owner's land/park decision for the
isolated branch `plan/mini-siem-storage-hardening` (S3H).

**Interim:** deployment warning added to `siem/PtkSiemReceiver/README.md`
(this change). If S3H lands, retention sweeps enforce the limits and
the warning is removed; if parked, the warning stands and disk usage
must be bounded operationally. See `.agents/review/findings/rbc-11.md`.

## 2026-07-27: Production salvage decisions delegated to the implementing agent

**Decision:** The owner directed the implementing agent to stop asking
low-level technical questions and to make the engineering choices that advance
the stated product: reliable token-compressed PowerShell execution with warm,
isolated sessions. The owner also directed continued work and ended further
Claude Opus reviews because Claude credits are exhausted.

**Applied to the active salvage plan:** Decisions 3-4 are settled in favor of
the plan's recommendations. The first production surface removes cold
`ptk_job` and `ptk_invoke(background=true)` behavior because it has no warm
state. The runtime removes mandatory exact-script audit admission and the
anchored OTLP exporter/build dependency while the separately built SIEM
receiver remains parked and independently tested. Future compliance export is
new, separately approved scope.

**Authorization boundary:** This is the go for the local implementation slices
already specified in `.agents/plans/production-reliability-salvage.md`. It does
not authorize a push, PR, merge, installation, deployment, canary, branch
deletion, history rewrite, or any other outward/destructive action. The older
broad decision-log reconciliation hold remains in force outside these two
salvage decisions.
