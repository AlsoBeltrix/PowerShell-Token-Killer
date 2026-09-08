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
- Other native platforms have not been run locally; the CI matrix exercises
  Windows/Linux/macOS, and the release matrix builds all five RIDs.

Host deployment paths, hashes, local toolchain setup, backup, and continuity
evidence are canonical in `.agents/machines.md`. The hook's disabled/trust
configuration was preserved; changing its command does not enable it or
approve a new trust hash. A harness retaining its old hook configuration may
continue to use it until configuration reload; no running session was forced
to reload.
