# Plan: Unified shell routing — one tool, hook-enforced

**Status:** Draft — awaiting owner approval. No code until approved.
**Decision basis:** 2026-07-04 owner amendment to the continuation decision in
`.agents/decisions.md`: ptk becomes the single tool surface for all shell work,
enforced by a harness redirect hook, ahead of the ~2026-07-20 go/no-go (which now
evaluates this product with the hook installed).

## Goal

The model has exactly one shell tool worth reaching for, and compression happens
for everything that supports it:

- **PowerShell scripts** run in the warm runspace (existing `ptk_invoke` path;
  objects → `Compress-PtcObject`, log-shaped text → rtk, other text verbatim).
- **Simple native command lines** (git, npm, docker, cargo, ...) route through
  rtk so its per-command filters apply (the 60-90% savings live there, not in
  `rtk log`).
- **A PreToolUse hook** on the harness's Bash and PowerShell tools redirects
  shell work to ptk, so adoption does not depend on model discipline (the
  0/13 dry-run and rtk instruction-decay evidence).

## Design commitments (from prior decisions, not renegotiated here)

- The rtk leg executes **inside the warm runspace** as a script rewrite
  (`<cmd>` → `rtk <cmd>` or the probe-chosen form), never a separate child
  process manager: cwd/state live in the runspace, and the existing timeout,
  cancellation, exit-code reporting ($LASTEXITCODE snapshot/restore), and
  shaping machinery apply unchanged.
- **No maintained command list.** The owner rejected "maintained debt list"
  designs in the universal-wrapper exploration. rtk passes through commands it
  does not support (owner, 2026-07-04), so the routing rule is unconditional:
  **any non-PowerShell command line goes to rtk** — filtered where rtk has a
  filter, passed through where it doesn't. No support detection anywhere.
- ptk is **not a security boundary** (recorded threat model); the hook is an
  adoption device, not a guard. The destructive-cmdlet gate stays paused.

## Slices

0. **Probe, then freeze the design.** Evidence-gathering only, results recorded
   in this plan before any implementation:
   - rtk fidelity: verify the owner-attested unsupported-command passthrough
     behaves as expected on this box, and that rtk propagates the wrapped
     command's exit code and stderr faithfully (filtered and passthrough
     paths); cwd semantics; `rtk proxy` as the raw escape hatch. No
     supported-command enumeration needed — routing is unconditional.
   - Hook mechanics (partly verified 2026-07-04 by inspecting the installed
     rtk hook): rtk ships a global PreToolUse hook, matcher `Bash`, command
     `rtk hook claude` (native binary, reads tool-call JSON from stdin,
     rewrites in place) — installed on this box via `rtk init -g`. Remaining
     probes: matcher name for the PowerShell tool; whether a hook can rewrite
     a Bash/PowerShell call or only deny-with-guidance toward ptk_invoke; how
     reliably the model switches to ptk_invoke after a deny; per-call hook
     LATENCY (fires on every shell call — native rtk pays ~0, a `pwsh -File`
     hook pays interpreter startup; candidate: a `hook` mode on the existing
     C# server binary); and whether ptk's Bash matcher supersedes or coexists
     with rtk's zero-token in-place rewrite.
   - Loop/friction cases: agent sessions in THIS repo (hook would fire on our
     own verification commands); interactive one-liners; commands ptk cannot
     run (true bash-isms on Windows).
1. **Routing leg (module/server).** Detect whether the input is PowerShell
   (AST: cmdlets, pipelines, variables, script syntax) — if so it runs in the
   warm runspace as today; any other command line routes to rtk unconditionally
   (rtk filters what it knows, passes through what it doesn't), rewritten to
   the probe-chosen rtk form and executed in the warm runspace. Route override argument on the
   tool (`route=auto|pwsh|rtk|raw`) for explicit control; `raw=true` keeps its
   current meaning (no shaping). Guard: routed and non-routed calls covered by
   Pester + dotnet tests incl. exit-code fidelity through the rtk leg.
2. **Tool surface.** Reposition `ptk_invoke`'s name/description as the single
   shell tool ("run any shell command; output comes back compressed") so the
   surface matches the hook's redirect text. Guard: handshake + live check.
3. **Redirect hook + installer.** PreToolUse hook on Bash/PowerShell tool use
   that redirects to ptk (rewrite if the harness supports it, else
   deny-with-guidance), shipped with a `ptk_init.ps1` installer mirroring
   `rtk init` semantics (owner decision 2026-07-04): global `-g` vs local
   default, `-Show`, `-Uninstall`, `-DryRun`, settings.json auto-patch. Scope
   is an install-time flag, not a design decision. Includes an escape hatch
   for calls ptk cannot serve. Guard: live-session check (hooked Bash and
   PowerShell calls land in ptk_invoke); friction log started for the
   go/no-go.
4. **Docs + battery.** Full suites, handshake, live spot-checks; update
   `.agents/state.md`, the decision entry (probe results + any scope
   corrections), `README.md`/`server/README.md` surface description.

## Open questions — all resolved 2026-07-04 (owner)

- ~~Hook scope~~ RESOLVED: not a decision — `ptk_init.ps1` mirrors `rtk init`
  (global `-g` vs local default), so scope is an install-time flag. rtk's own
  hook is already installed globally on this box (matcher `Bash` only; the
  PowerShell tool is uncovered — that gap is ptk's to close).
- ~~True bash syntax~~ RESOLVED: any non-PowerShell input routes to rtk,
  bash-isms included — rtk shells out itself and passes through what it does
  not filter. The slice-0 probe still verifies this works through the warm
  runspace on Windows (what shell rtk uses for bash-isms here).
- ~~Naming~~ RESOLVED: keep `ptk_invoke` — owner indifferent, and renaming
  would reset harness allow-decisions and orphan recorded live checks for no
  benefit.

## Risks

- **Hook friction is the product risk:** a deny-per-call redirect adds a
  round trip every time the model reaches for Bash. If that friction makes the
  owner disable the hook, the amended go/no-go criterion fails on its own
  terms — the friction log in slice 3 exists to catch this early.
- **rtk fidelity:** if rtk swallows exit codes, reorders stderr, or mangles
  interactive/ANSI output, the rtk leg silently degrades tool output the model
  acts on. Slice 0 exists to find this before the design freezes; the round-2
  review history (rtk clobbering $LASTEXITCODE) is the precedent.
- **Windows PATH/environment drift:** the server's PATH sees rtk only after a
  session restart post-install (recorded 2026-07-03); the hook must not
  redirect to a ptk that cannot see rtk — degrade visibly, never silently.

## Non-goals

- The `ptk <cmdlet>` CLI universal wrapper (separate open decision, stays
  paused). The destructive-cmdlet gate (stays paused). Any change to the
  go/no-go date. ollama/local-model shaping (dropped 2026-07-03).
