# Native redirect hook

Owner approved implementation on 2026-09-08 after asking why every shell
denial starts PowerShell. Existing MCP sessions must remain running.

## Approved scope

Replace the PowerShell redirect handler with a small standalone NativeAOT
executable. Preserve the deny JSON, guidance, cwd escaping, case-insensitive
PTK_DIRECT escape hatch, fail-open handling of malformed input, and process
presence hint. Never execute the submitted command or launch PowerShell.
Update canonical packaging and all supported hook registrations, recognizing
legacy registrations during repair and uninstall. Ship no PowerShell fallback.

Verify behavior, native execution without PowerShell on PATH, legacy migration,
and packaged registration. Run the repository verification entry points.
Deploy only the hook to a separate local path and update the existing handler
command without replacing MCP binaries, stopping sessions, changing MCP
registrations, or changing disabled/trust settings. Commit and push the slice
with evidence and current-state records.

## Status

Implementation and local deployment complete. `server/PtkHook` publishes the
standalone native handler; canonical layouts carry `bin/ptk-hook[.exe]` and
no longer ship `scripts/ptk-hook.ps1`. Setup recognizes both old and new
registrations, preserves similarly named foreign handlers, and refuses to
register a missing native hook. CI and release Linux runners install AOT
build prerequisites; release's existing native signing steps cover the new
executable. The frozen rc.2 artifacts were not changed.

The existing script's use of PowerShell was an implementation dependency,
not a hook-protocol requirement. Native execution still starts one small
handler process per call; it starts no child processes and does no command
execution. Existing denial text and PTK_DIRECT semantics remain intact.

## Verification (2026-09-08, macOS arm64)

- Regression proof: the new setup assertion forbidding `pwsh`/`.ps1` failed
  against the original registration before the implementation changed and
  passed afterward. A separate ownership regression failed for the initial
  broad native-name match, then passed after matching the complete filename.
- Pester: 118 passed, 3 platform skips. Includes native execution with no
  shell on PATH, command non-execution, legacy migration, idempotence, foreign
  handler preservation, and cwd/apostrophe guidance.
- Server solution: 1,385 passed, including ten new hook input/response cases;
  only the two existing xUnit2002 warnings. SIEM: 357 passed. Mini-SIEM
  lifecycle passed.
- Canonical osx-arm64 layout with `-Validate` passed; staged installation
  passed native-hook checks with no shell on PATH before and after activation
  plus both complete MCP handshakes. Direct checkout registration handshake
  passed. All six server projects had no known vulnerable dependencies.
  `actionlint` and `git diff --check` passed.
- Fourteen old/native comparisons (seven inputs under forced up/down
  liveness) returned equivalent parsed responses or identical empty output.
  Twenty-five alternating calls each, with real process detection and no
  liveness override: old PowerShell median **303.63 ms** (299.69–326.81 ms),
  native median **7.52 ms** (6.97–8.17 ms). This measures handler launches,
  not total harness scheduling or MCP initialization.
- [CI run 34267204938](https://github.com/AlsoBeltrix/PowerShell-Token-Killer/actions/runs/34267204938)
  passed all six Windows/Linux/macOS product and SIEM jobs at `2247aae`,
  including native hook publishing and Pester behavior checks. It ran from
  19:08:24 to 19:17:42 UTC (9 minutes 18 seconds). The release-only arm64
  Windows/Linux RIDs were not rebuilt by this CI run.

Host deployment paths, hashes, local toolchain setup, backup, and continuity
evidence are canonical in `.agents/machines.md`. Initial deployment preserved
the hook's disabled/trust configuration. The separately approved activation
below supersedes that initial disabled state. No running session was forced
to reload; a harness retaining old configuration may continue to use it until
its normal reload.

## Owner-approved live activation (2026-09-08)

After CI passed, the owner explicitly approved enabling/trusting the native
hook and checking denial, PTK_DIRECT, and MCP startup in a fresh Codex session
while preserving existing sessions. Codex CLI 0.153.4's generated app-server
schema and `hooks/list` supplied the exact hook definition and current trust
hash. `config/batchWrite` changed only that hook's `enabled` and `trusted_hash`
fields, with no trust-bypass flag. Fresh `hooks/list` returned enabled/trusted.
The [official hook documentation](https://learn.chatgpt.com/docs/hooks#review-and-trust-hooks)
describes this exact-definition trust requirement.

An ephemeral Codex thread in `/Users/michael/Dev/roon-controller`
(`01a08295-cb1d-7f91-b0c3-aea4e6342a9a`) proved:

- The ordinary `printf` probe was blocked by PreToolUse with PTK guidance;
  Codex's hook completion event measured **16 ms**, with status `blocked`.
- The otherwise equivalent `PTK_DIRECT` probe was allowed; its hook event
  measured **7 ms**, the shell exited 0, and stdout was exactly
  `PTK_DIRECT_OK_25cb` plus newline.
- `ptk_invoke` completed and returned `PTK_MCP_OK_25cb`. The registered fixed
  MCP server became ready in **8.005 seconds** from its startup event.
- The model's `ptk_state` call was rejected by the proof's own `never`
  approval setting. A follow-up model probe reported the tool unavailable
  despite a later successful ready event; it did not execute the health tool.
  These attempts do not count as successful model-level health-tool checks.
  A targeted, owner-authorized `mcpServer/tool/call` through a fresh Codex
  app-server thread then returned `ptk_state`: repaired build identity,
  `audit: healthy mode=local-only`, `last_failure=none`, and
  `reset_required=false`. The default session was cold, as expected before
  any execution in that connection.
- The two additional health-probe connections became ready in **11.791**
  and **16.474 seconds**. All three started within the former 30-second
  timeout, but latency remains variable; this evidence does not establish
  consistent five-second initialization or diagnose that variation.

All 29 original PTK processes retained PID, start time, and command after
activation and testing. This connection's worker and warm continuity marker
survived. Both installed MCP DLL hashes and the native hook hash were
unchanged. Only proof-created app-server processes were closed. No product
code changed during activation; verification for the record update is
`git diff --check`, with product CI evidence above retained.
