# Plan: Phase 2 — compression on ptk_invoke output (router v1)

**Status:** Approved by owner 2026-07-03 (in-session go on this exact scope after
the AskUserQuestion routing choice). Target: working before owner is back at work
~2026-07-20.
**Decision basis:** `.agents/decisions.md` → continuation decision, amendment of
2026-07-03 (owner unpaused Phase 2, scoped to compression riding on `ptk_invoke`;
the universal wrapper and the destructive-cmdlet gate remain paused). The settled
sub-decisions in the warm-runspace decision entry remain binding ("substrate vs
shaping stay separate"; module loads once into the warm runspace).

## Goal

`ptk_invoke` output routes by shape instead of blanket `Out-String`:

- **PowerShell objects** → `Compress-PtcObject`, in-process in the warm runspace
  (lossless-structured, instant — the module is already loaded there).
- **Log-shaped text** → `rtk log <tempfile>` when an rtk binary is available;
  labeled raw passthrough when it is not.
- **All other text** → full passthrough, verbatim, never truncated (owner call
  2026-07-03: no ollama/model leg; also keeps the router-branch finding that
  digesting already-compact text destroys it).
- **Escape hatch:** `ptk_invoke` gains an optional `raw` parameter that restores
  today's plain `Out-String` behavior.
- **Never-throw contract:** shaping can never fail a call. The routing function
  catches its own errors and returns labeled raw text; the server falls back to
  `Out-String` if the module failed to load.

## Slices

0. **Docs (this commit).** Record the supersession amendment in
   `.agents/decisions.md`, this plan, and the state update. *Verify:* n/a (docs).
1. **Module: `Compress-PtcOutput` + Pester tests.** New exported function taking
   pipeline input. Routing: any non-string, non-primitive item → object path
   (`Compress-PtcObject`); primitive scalars and strings → text path (join
   verbatim); text path checks `Test-PtcLogShaped` (ported from the router
   branch: timestamp/level-tag heuristic over the first 40 lines, ≥5 lines) and
   sends log-shaped text to `Invoke-PtcRtkLog` (ported: temp file → `rtk log`,
   `[ptk:log via rtk]` label, labeled raw fallback when rtk is missing). rtk
   discovery: `PTK_RTK_PATH` env override, else `Get-Command rtk`. Tests: objects
   compress; strings (including >MaxItems counts) pass through untruncated;
   log-shaped + stubbed rtk on PATH → rtk label; log-shaped without rtk →
   fallback label + full text; mixed output → object path; empty → empty; a
   thrown internal error → labeled raw, not an exception.
   *Verify:* Pester suite; prove new tests guard (fail without the function).
2. **Server: import module + route + `raw` param.** `CreateRunspace()` imports
   `src/PwshTokenCompressor.psd1` after open (resolution: `PTK_MODULE_PATH` env
   override → probe cwd → probe upward from `AppContext.BaseDirectory`); import
   failure logs to stderr and flips a flag so `InvokeAsync` appends `Out-String`
   as today (server must start and serve even without the module). `InvokeAsync`
   appends `Compress-PtcOutput` instead of `Out-String` unless `raw` is set.
   `ptk_invoke` tool gains optional `raw` (default false), description updated so
   the model knows raw output is available. Recycle/reset paths inherit the
   import automatically via `CreateRunspace()`.
   *Verify:* `dotnet test` (new cases: object script compressed, string script
   verbatim, `raw:true` restores Out-String, module-missing fallback), handshake
   script (dll + `-UseRegistrationCommand`).
3. **rtk on Windows (optional for the 20th).** No cargo on this box. Try a
   prebuilt Windows release of `rtk-ai/rtk`; if none, installing the Rust
   toolchain is an owner-visible machine change that needs its own go. Until rtk
   lands, the log leg degrades to labeled passthrough by design.
   *Verify:* with rtk present, a log-shaped `ptk_invoke` returns `[ptk:log via
   rtk]` output.
4. **End-to-end verification + measurement.** Full Pester + `dotnet test` +
   handshake; live `ptk_invoke` checks; a `Measure-PtcSavings`-based sample on
   realistic outputs (e.g. `Get-Process`, `Get-ChildItem -Recurse`) recorded in
   `state.md` — measured numbers, clearly labeled as tool-reported estimates
   (the go/no-go's benefit criterion stays experienced time saved, per the
   headroom trap).
   *Verify:* all suites green; results recorded.

## Risks

- `Compress-PtcObject` behavior on unusual object types — mitigated by the
  never-throw wrapper and the `raw` escape hatch.
- Module path resolution from the server across launch modes (`dotnet run` from
  repo root vs. `dotnet exec` on the dll) — mitigated by the three-step probe +
  explicit env override + graceful degradation.
- rtk CLI drift: `rtk log <file>` is the interface the router branch used;
  re-verify against the actual binary when it lands (slice 3).
- Handshake cross-call test asserts on `42` from an int result — slice 1 must
  keep primitive scalar output readable (text path), or the handshake and the
  model both get noise.

## Non-goals

- No ollama/local-model leg (owner dropped it 2026-07-03).
- No universal CLI wrapper; no destructive-cmdlet gate (still paused).
- No change to `Compress-PtcObject`'s own string[] handling for the CLI verbs
  (the recorded string[] sub-decision stays tied to the universal-wrapper
  decision; the server path avoids the bug via `Compress-PtcOutput`).
- No RunspacePool/parallelism; no interactive `Connect-*`.
