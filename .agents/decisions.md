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

No durable decisions recorded yet.

## Open Decisions (deferred - not yet adopted)

### OPEN (2026-06-27): Whether to build the "universal PowerShell wrapper" rearchitecture

**Status:** Open - deferred by owner to decide later. No code change authorized. This
entry records the design exploration so a future session can resume without
re-deriving it.

**Question:** Should ptk grow a universal pass-through path - `ptk <any cmdlet ...>`
runs the command and compresses whatever it returns - replacing the current
fixed-verb dispatch? And if so, in what form?

**What triggered it:** `ptk Get-ChildItem` prints the help screen instead of running,
because the dispatch is a `switch -Regex` (`Invoke-Ptc`, src/PwshTokenCompressor.psm1)
whose arms match only short aliases (`ls|dir|gci|list`), with a `default` arm that
prints usage. Owner rejected three narrower fixes in turn (add cmdlet names to the
regex; a hashtable dispatch + alias table; an AST allowlist) as variations on a
"maintained debt list."

**Verified evidence gathered this session (keep - expensive to re-establish):**

- **RTK is text/stream-first and cannot compress PowerShell objects.** RTK (`../rtk`,
  upstream `rtk-ai/rtk`) has ~100 hand-written per-command proxies plus a universal
  `rtk summary <cmd>` that runs any command via `sh -c` / `cmd /C` and filters the
  **text** output. By the time RTK sees PowerShell output it is already
  `Format-Table` text; the objects are gone. ptk's reason to exist is the README's
  claim: compress objects *before* formatting. (Confirmed by reading RTK source:
  `src/main.rs`, `src/cmds/system/summary.rs` `detect_output_type`,
  `src/core/runner.rs` `run_passthrough`.)
- **Both agent harnesses run a fresh, cold PowerShell process per tool call by
  default.** Confirmed directly: Codex (`codex exec`, self-report) =
  `pwsh -Command "<string>"` per call, no persistence except an explicitly retained
  PTY (`write_stdin`/`session_id`). Claude Code (claude-code-guide agent, citing
  code.claude.com/docs) = fresh process per call; dedicated PowerShell tool
  auto-detects `pwsh.exe`/`powershell.exe`, `-ExecutionPolicy Bypass`, profiles not
  loaded; no REPL. Neither persists env/modules/sessions between default calls.
- **Owner's real Exchange workflow works because of a warm HOST process, not harness
  state.** Hybrid Exchange; `Get-Queue` is on-prem only; on-prem EMS takes 30s+ to
  connect to the CAS server; owner's `$PROFILE` auto-connects only EXO, nothing
  on-prem. Yet the agent returns on-prem queue data in ~2s, repeatedly. Deduction:
  the agent is running inside an already-open EMS host whose implicit-remoting
  PSSession persists in that process. **Consequence:** if ptk's universal path
  spawned a child `pwsh` (today's `Invoke-PtcRun` string path does), `ptk Get-Queue`
  would launch a cold process with no on-prem session and either fail or eat the 30s
  reconnect - breaking a workflow that currently works. Therefore the universal path,
  if built, MUST run in-process (in ptk's own host session), never a grandchild. The
  README design goal "use `pwsh -NoProfile -NonInteractive` for command-string
  execution" is wrong for this case.
- **Threat model: ptk is not a security boundary.** `ptk <cmd>` would run the same
  string in the same process the harness already spawns for raw PowerShell - identical
  blast radius. Prompt-injection defense belongs at the harness sandbox, not in a
  token compressor. (Matches RTK's stance: `rtk summary` runs arbitrary strings.) So
  no AST allowlist / injection guard on the universal path is warranted; the previous
  session's injection-guard tests on the string path would need deliberate
  reconciliation, not silent deletion, if that path is retired.

**Settled sub-decisions (conditional on building it at all):**

- Drop the `ls`/`ps`/`service`/`grep` verbs - redundant `<cmdlet> | Compress-PtcObject`
  bash-crutch wrappers, subsumed by a universal path. Keep `read`/`smart`/`savings`
  (text/file work, the RTK-derived path), `run`, `compress`.
- Fix `Compress-PtcObject` so a homogeneous `string[]` (e.g. `Get-Content` output)
  routes to the text filter at `minimal` (gentlest, never-worse-guarded) instead of
  being truncated as an object list. Escape hatch = reuse `read`'s
  `-Level`/`-MaxLines`/`-Tail`. This also fixes the standalone
  `ptk run { Get-Content }` truncation bug.

**Why deferred (owner's framing):** ptk is a personal/team tool complementing the
owner's `headroom` PoC on Windows/PowerShell work - not an org-wide tool. headroom
already saved ~39.5M tokens in one day without ptk or RTK. The universal path is the
large, risky piece (in-session execution; retiring hardened code). The owner's
build trigger is "if it shows real benefit," i.e. a measurement condition. Bar to
clear is "real savings on my daily Windows work," not org scale.

**Standing recommendation (for whoever picks this up):** Stage it. (1) Fix the
`string[]` truncation bug and (2) drop the bash-crutch verbs - both small and
clearly worth it on their own. Then (3) measure object-first savings on a week of
real Windows usage (the `savings`/`Measure-PtcSavings` primitive exists). Only build
(4) the universal in-process wrapper if the data justifies it. Sequences the cost
behind the proven benefit. Each of (1)-(4) is a separate authorized change requiring
its own go.
