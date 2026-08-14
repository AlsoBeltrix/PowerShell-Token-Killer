# Per-harness support record

Dated, live-verified facts about how each agent harness on the owner's
machines reaches ptk. This is the durable home for probe outcomes
(multi-harness-init plan, slice 0). Record CAPABILITIES with dates —
never volatile invocation syntax like model ids; the installer probes
those live.

## Verified 2026-07-08 (slice-0 probes, this Windows box)

| Harness | Registration | Tool naming | Enforcement (redirect hook) | Nudge home |
| --- | --- | --- | --- | --- |
| Claude Code | user-scope, via dev-install (`claude mcp add --scope user` → installed binary). LIVE | `mcp__ptk__ptk_invoke` | **VERIFIED end to end**: global PreToolUse hook denied Bash AND PowerShell tool calls in a live session; a fresh headless session quoted the deny guidance verbatim, re-issued via `ptk_invoke` unprompted, and completed the task | `~/.claude/CLAUDE.md` |
| codex | `codex mcp add ptk -- <exe>` → `~/.codex/config.toml`. LIVE (tools listed). Installer leg shipped 2026-07-09 (`ptk_init.ps1 -Agent codex`, idempotent: an existing entry is left as-is) | `mcp__ptk.ptk_state` (dot separator) | none shipped (hooks trust-gated; unresolved) | `~/.codex/AGENTS.md` — **VERIFIED 2026-07-09** by marker probe: a fresh `codex exec` session quoted the installed ptk-guidance block verbatim from its loaded context. Nudge installed on this box the same day |
| grok | `grok mcp add -s user ptk <exe>` → `~/.grok/config.toml`. LIVE (state call answered). Installer leg shipped 2026-07-09: config-presence short-circuit (leave-as-is), add only when absent; uninstall attempts `grok mcp remove -s user` (a MIRROR of the verified add form, not itself live-verified — reports honestly on failure) | `ptk__ptk_state` | **No spillover**: grok did NOT honor the `~/.claude/settings.json` hook (a third-party ledger claimed it scans that file — wrong for this build). No grok hook shipped | **VERIFIED**: grok session-loads `~/.claude/CLAUDE.md` (marker probe quoted back) — the claude leg's guidance block covers grok |
| agy | `~/.gemini/config/mcp_config.json` `mcpServers` entry. LIVE (state call answered; headless auth worked this time). Installer leg shipped 2026-07-09: user-level plugin dir (`~/.gemini/config/plugins/ptk/`) carrying registration (omitted when the global entry exists, which is left as-is) + rules; plugin auto-discovery is an OPEN PROBE for the first owner install run | unprefixed (`ptk_state`) | documented deny-with-guidance via customization-root/plugin `hooks.json` — live firing NOT yet demonstrated, so the shipped plugin carries NO `hooks.json`; enforcement lands after a live install run meets the verify-once bar | plugin `rules/ptk.md` (shipped by the leg) |

## Verified 2026-08-04 (kimi leg, owner's macOS box)

| Harness | Registration | Tool naming | Enforcement (redirect hook) | Nudge home |
| --- | --- | --- | --- | --- |
| kimi | `~/.kimi-code/mcp.json` `mcpServers.ptk` entry — direct JSON merge by `ptk_init.ps1 -Agent kimi` (no scriptable CLI surface; `/mcp-config` is TUI-only). Leave-as-is when an entry exists, payload gate otherwise; `$KIMI_CODE_HOME` honored. **LIVE**: a fresh `kimi -p` session's `ptk_state` call answered | `mcp__ptk__ptk_invoke` (same scheme as Claude Code) | **VERIFIED end to end**: `[[hooks]]` `PreToolUse` matcher `"Bash"` in `~/.kimi-code/config.toml` running the same `ptk-hook.ps1` — a fresh headless session's Bash call was denied with the ptk guidance quoted verbatim. Kimi's hook protocol accepts the script's stdout deny JSON unchanged; hook failure is fail-open by kimi's design | `~/.kimi-code/AGENTS.md` (documented global instruction file, moves with `KIMI_CODE_HOME`) |

## Known limitations (dated)

- 2026-08-13 — PTK's Claude Code, Codex, grok, agy, and kimi registration
  adapters configure how to launch the MCP server; none currently injects
  PTK's per-call `_meta` attribution namespace. Omitted agent/model/task/run
  values are exported as `not_supplied_by_client`, never inferred. A client or
  proxy that can populate MCP `tools/call.params._meta` may use the contract in
  [`server/AUDIT-EXPORT.md`](../server/AUDIT-EXPORT.md). Official OpenAI Codex
  MCP documentation lists server launch, transport, authentication, and tool
  policy configuration, but no selected-model or task/run metadata injection
  setting: <https://learn.chatgpt.com/docs/extend/mcp?surface=cli>.

- 2026-07-08 — codex `exec` (headless) auto-denies MCP tool calls ("user
  cancelled", pre-flight): re-confirmed on Codex v0.143.0, consistent with
  the 2026-07-02 record. Interactive codex calls work after a one-time
  allow (verified 2026-07-02 on the dotnet-run registration; re-verify
  interactively on the installed binary in normal use).
- 2026-07-08 — agy cannot read its own `~/.gemini` config (sandbox
  boundary); agy-side probes must run from outside agy.

## Naming note for hook/guidance text

Three naming schemes exist for the same tool (`mcp__ptk__ptk_invoke`,
`mcp__ptk.ptk_invoke`, `ptk__ptk_invoke`, unprefixed) — redirect/deny text
must say "the ptk_invoke MCP tool", never a harness-specific id, except in
per-harness material.
