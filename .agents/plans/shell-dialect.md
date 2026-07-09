# Plan: shell dialect — bash-shaped input, honest fast failures, raw posture

**Status:** DRAFT awaiting owner approval. Sources: GitHub issue #3 (item 1
only) and issue #4 (problems 1-3), triaged 2026-07-09. No code before
approval; slice 0 runs first and freezes its results into this file.

## Problem

Verified against the code, not just the issue text:

1. **Bash-shaped scripts fail late and confusingly.** The hook redirect
   echoes the agent's command verbatim and agents compose bash by habit, so
   bash-shaped strings arrive at the router by design. The router is
   deliberately infallible — a script with parse errors returns unchanged
   into the pwsh runspace (`src/PwshTokenCompressor.psm1:523`) — so
   bash-only constructs are never refused at the door: they die later as
   pwsh parse errors, or worse, parse fine with different semantics
   (backticks are pwsh escape characters, so `` `cmd` `` substitution
   silently degrades to a literal; `export X=1` becomes a runtime
   CommandNotFound). Nothing in the chain names the dialect mismatch.
2. **`raw=true` is described neutrally and invisibly.** "Skip output
   compression and return plain formatted text"
   (`server/PtkMcpServer/Tools/InvokeTool.cs:28`) reads as a preference;
   issue #4 observed an agent using raw on 3/3 calls out of fidelity habit
   — complying with the redirect while zeroing the compression value.
   Nothing counts or surfaces raw usage.
3. **The deny and nudge texts invite the mismatch.** The hook deny names
   the "persistent warm PowerShell runspace" (`scripts/ptk-hook.ps1:57-64`)
   but never says bash-only syntax must be translated or wrapped; the
   ptk_init nudge block (`scripts/ptk_init.ps1:115-128`) says "use
   ptk_invoke for shell commands" with no dialect note.

**Unverified mechanism claim — MUST be probed before design freezes.**
Issue #3's repro (`cd /path && node scripts/build.mjs` failing with
"No such file or directory (os error 2)") carries Rust/rtk error phrasing,
but this build's resolver cannot route a `&&` chain to rtk: a pipeline
chain fails the single-`PipelineAst` check
(`src/PwshTokenCompressor.psm1:529`) and runs as valid pwsh, where `cd X
&& node Y` should simply work. Either the repro reached rtk another way,
the reporter's build differed, or the mechanism story in the issue is
wrong. Slice 0(a) pins this; nothing in the design below depends on the
unverified version.

## Goal

A bash-only script through `ptk_invoke` never fails silently-late or with
a mystery error: it is either detected and refused fast — with guidance
naming the offending construct and both recovery paths — or it runs
correctly (explicit `bash -lc '...'` is already a first-class, compressed
path today: bash is a native Application, so the string executes via the
rtk leg when constant or the pwsh leg otherwise, and output flows through
the compressor either way). `raw=true` reads and logs as a recovery
hatch, not a preference. The redirect and nudge texts stop inviting the
dialect mismatch at the source.

## Design principles

- **Routing never fails a call — unchanged.** Detection produces a
  classified, labeled refusal *result* (never a thrown routing error);
  anything undetected runs exactly as today. The refusal is the same
  honesty move as teach-at-timeout (greenfield D3): name the problem, name
  both recovery paths, cost one fast round-trip instead of one confusing
  late failure.
- **High precision over recall.** A false positive blocks a legitimate
  PowerShell script — strictly worse than a miss (a miss is only today's
  behavior). Only constructs that are (a) bash-only and (b) fatal or
  semantically treacherous in pwsh enter the detection list, and each
  entry earns its place with a slice-0 probe. Shared-dialect shapes
  (`&&`, pipes, `2>/dev/null`-style redirects, single-quote literalness)
  must never trip it.
- **No magic translation.** Auto-rewriting bash to pwsh is a semantic
  rabbit hole (quoting, expansions, exit-status wiring). The "run bash"
  machinery already exists and is compressed; the gap is detection and
  teaching, not execution.
- **The escape hatch stays simple.** `raw=true` keeps no-questions
  semantics — the D2 bounded-passthrough decision (adopted 2026-07-08)
  deliberately names it as the escape hatch. Posture changes are wording
  and visibility, not gates.

## Open decisions (owner)

- **D1 — remedy for detected bash-only shapes.**
  - (a) **RECOMMENDED:** refuse fast with guidance naming the construct
    and both recovery paths — rewrite in PowerShell, or wrap the whole
    script in `bash -lc '...'` (with the apostrophe-escaping note). An
    explicit `route=pwsh` bypasses detection (an explicit route choice is
    consent). Smallest change, no semantic surprises, platform-uniform.
  - (b) Issue #3's ask: auto-wrap detected scripts in `$SHELL -lc` on
    POSIX. Executes "as intended" but silently switches dialect
    per-platform, inherits bash quoting/env surprises into a tool whose
    contract is PowerShell, and Windows still needs (a).
  - Recommendation: (a); revisit (b) only if real use shows agents do not
    act on the refusal guidance.
- **D2 — raw posture scope.** Adopt the non-breaking subset: reword the
  `raw` description to recovery-only ("for recovering detail the
  compressed form lost — not a default; compressed output already
  preserves errors, exit codes, and structure") and make raw usage
  visible (server log line per raw call; candidate: a counter in
  `ptk_state`). Decline for now: first-use gating, justification strings,
  deny semantics — friction on a deliberate escape hatch; revisit only
  with evidence that rewording fails.
- **D3 — where the dialect line lands.** One added line in the hook deny
  text and the ptk_init nudge block (plus the README routing section):
  the runspace is PowerShell 7 — translate bash-only syntax or wrap it in
  `bash -lc '...'`. NOTE owner action on adoption: installed machines
  carry hook/nudge text from their last install; text changes reach a box
  only after a dev-install re-run (issue-2 lesson).

## Slices

0. **Probes — results frozen into this plan before any implementation.**
   (a) Issue #3's repro verbatim through `ptk_invoke` on this box under
   `route=auto|pwsh|rtk`: pin which leg fails with what error, plus the
   resolver's actual classification of the exact string. (b) Bash-only
   construct inventory from live probes — heredoc `<<`, `export X=`,
   leading `VAR=x cmd`, `[ -f x ]` tests, backtick substitution, `local`,
   `set -e`, `if/fi` — for each: pwsh parse error, runtime
   CommandNotFound, or silent semantic change? Only fatal-or-treacherous
   constructs enter the detection list; probe the false-positive set too
   (`&&`, pipes, redirects, subexpressions). (c) The `bash -lc` recovery
   path end to end: compression applied, exit code surfaces, cwd
   anchoring, both legs. (d) Snapshot current hook/nudge/README wording.
1. **Detector in the module** (`Test-PtcBashOnlyScript`, or folded into
   `Resolve-PtcInvokeScript`): anchored per-construct patterns from slice
   0. Pester tests per construct plus false-positive guards.
2. **Server wiring per D1**: detection flows to a labeled refusal result
   naming the construct and both recovery paths; `route=pwsh` bypasses.
   Guard tests; handshake (server-facing).
3. **raw posture per D2**: description reword + raw-usage visibility.
   dotnet tests where testable.
4. **Texts per D3**: the dialect line in hook deny + nudge block + README;
   docs slice; owner-action note for installed boxes.

Each slice: one commit, battery, codex loop per repo precedent.

## Out of scope

- Issue #3's MCP permission-bypass / policy+audit layer — its own plan,
  separately owner-gated.
- Issue #3 items 2-4 (README warm-runspace scoping, thin missing-exe
  error, timeout hint) — candidate small follow-up batch.
- Bash-to-PowerShell translation of any kind.
- PTK_DIRECT semantics.
- raw gating/justification (declined under D2 unless evidence reopens).

## Verification

Battery per slice (Pester; dotnet test and handshake when server-facing).
Live end-to-end at close: the #3 repro and one representative bash-only
construct through a real session — refusal text observed verbatim, then
the `bash -lc` recovery works with compression. After landing + owner
push, issues #3 (item 1) and #4 get fix references.