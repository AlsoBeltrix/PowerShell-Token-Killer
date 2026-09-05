# mhi-13: Codex install cannot heal an orphaned PTK tool-policy table

**Severity:** HIGH — Codex refuses to start or run its own repair command.

**Status:** RESOLVED 2026-09-04 in the installer-repair commit.

**Source:** Owner's live report from Codex TUI 0.153.2/CLI 0.153.3, followed
by a local command-line reproduction.

## Evidence

An orphaned `[mcp_servers.ptk.tools.ptk_output]` table with no exact
`[mcp_servers.ptk]` base makes Codex report `invalid transport in
mcp_servers.ptk`. The same error reproduced without changing live config via:

```text
codex -c 'mcp_servers.ptk.tools.ptk_output.approval_mode="approve"' mcp list
```

The earlier mhi-12 repair swept these tables only after the Codex uninstall
path. A later install still called `codex mcp get ptk` first. Because every
Codex command parses configuration before running, that install path could not
repair or replace the registration.

## Repair

`scripts/ptk_init.ps1` now defines an orphan precisely as a PTK subtable with
no exact PTK base table. Before an install invokes Codex, it removes only those
orphaned PTK subtables and reports the repair. A valid custom PTK registration
and its per-tool policy remain byte-for-byte present. Dry-run reports the
pending repair without writing.

The original repair record incorrectly concluded that the unified TUI shell
surface could not be selectively hooked. Current official Codex documentation
establishes that unified `exec_command` is canonically exposed to hooks as
`Bash`, while MCP calls retain MCP tool names, and that the command arrives as
`tool_input.command`. The owner-approved mhi-14 follow-up therefore adds the
Codex redirect hook; this orphan-table repair remains otherwise unchanged.

## Guard proof

The new focused guard failed before the repair (`Expected 0, got 1`) because
the fake Codex CLI detected the orphan before accepting `mcp get` or `mcp add`.
It passed after implementation. The repair was then temporarily removed and
the same guard failed again, after which the implementation was restored.

The companion guard supplies a valid `[mcp_servers.ptk]` plus
`[mcp_servers.ptk.tools.ptk_output]` and verifies both survive the install
short-circuit.
