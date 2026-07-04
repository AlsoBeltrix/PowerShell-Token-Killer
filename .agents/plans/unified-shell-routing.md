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
   - Hook mechanics on this harness/box: PreToolUse matcher for Bash and the
     PowerShell tool; deny-with-guidance vs command-rewrite capability; where
     it lives (user-level `~/.claude/settings.json` vs per-project); how the
     model behaves after a deny (does it reliably switch to ptk_invoke).
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
3. **Redirect hook.** PreToolUse hook on Bash/PowerShell tool use that blocks
   with guidance to use ptk (or rewrites, if the probe shows the harness
   supports it), with an explicit off switch and an escape hatch for commands
   ptk cannot serve. Deliverable includes install location + uninstall note.
   Guard: live-session check (hooked Bash call lands in ptk_invoke); friction
   log started for the go/no-go.
4. **Docs + battery.** Full suites, handshake, live spot-checks; update
   `.agents/state.md`, the decision entry (probe results + any scope
   corrections), `README.md`/`server/README.md` surface description.

## Open questions (owner input at approval)

- **Hook scope:** user-global (all projects on this box) or per-project? A
  global hook fires in this repo's own dev sessions too — including agent
  verification commands like `dotnet test`; the escape-hatch semantics decide
  whether that is friction or fine.
- ~~True bash syntax~~ RESOLVED by owner 2026-07-04: any non-PowerShell input
  routes to rtk, bash-isms included — rtk shells out itself and passes through
  what it does not filter. The slice-0 probe still verifies this works through
  the warm runspace on Windows (what shell rtk uses for bash-isms here).
- **Naming:** keep `ptk_invoke` (continuity with recorded live checks) or
  rename (`ptk_run`/`ptk_shell`) for the one-tool story? Renaming invalidates
  recorded harness allow-decisions.

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
