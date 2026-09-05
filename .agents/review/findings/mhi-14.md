# mhi-14: Codex installer omits its supported shell redirect hook

**Severity:** HIGH — an installed PTK registration does not enforce shell
routing, so ordinary Codex shell calls bypass PTK silently.

**Status:** RESOLVED 2026-09-04 in the owner-approved Codex-hook follow-up.

**Source:** Owner's live report after rerunning setup, checked against current
[official Codex hooks documentation](https://developers.openai.com/codex/hooks).

## Evidence

The live behavioral probe executed instead of being denied, the session exposed
no PTK MCP tools, and `~/.codex/hooks.json` contained only Headroom entries.
The installer source explained the hook result directly: its Codex leg
explicitly shipped registration and a nudge but no hook, based on the stale
assumption that this TUI's unified shell tool could not be matched selectively.

Official Codex documentation establishes the missing contract:

- user hooks load from `~/.codex/hooks.json`;
- unified `exec_command`, including nested code-mode calls, matches `Bash`;
- `PreToolUse` receives the command in `tool_input.command`; and
- MCP calls keep their MCP tool names, so a `Bash` matcher does not intercept
  `ptk_invoke`.

Headroom was not the remover. Its installed session hook only ensures the proxy
profile is running. Its separate Codex configuration writers preserve unrelated
MCP tables, and its `hooks.json` read/merge/write path preserves foreign hooks.
The live Codex config timestamp also remained at the owner's manual-repair time,
so the subsequent rerun did not rewrite or then lose a PTK registration. There
is no evidence that Headroom removed one.

## Repair

`scripts/ptk_init.ps1` now merges one PTK-owned user-level `PreToolUse` entry
with matcher `Bash` into `~/.codex/hooks.json`. It removes stale and duplicate
PTK handlers, preserves unrelated top-level data, event groups, entries, and
handlers (including Headroom), and removes only PTK's handler on uninstall. It
points at the stable installed `~/.ptk/scripts/ptk-hook.ps1` payload and installs
only after Codex registration can answer. The installer reports that Codex will
review the non-managed hook for trust in the next session; it never writes trust
state itself.

## Guard proof

The two focused guards failed before implementation because `ptk_init.ps1` had
no `CodexHooksPath` or Codex hook writer. After implementation they pass and
prove idempotent merge, stale-entry replacement, preservation of a foreign
handler sharing an entry, Headroom preservation, exact `Bash` matching, stable
installed-payload targeting, and PTK-only uninstall.

The complete PowerShell module/installer suite then passed 116 tests with 3
platform skips. Both changed PowerShell scripts also parsed cleanly, and
`git diff --check` passed. The server regression suite passed 1,360/1,360 with
the two existing xUnit analyzer warnings. Its first sandboxed attempt failed
before tests because MSBuild could not bind its local IPC socket; the approved
unsandboxed rerun produced the passing result.

Live next-session trust plus deny/reissue behavior remains a verification step
after the owner installs this repaired checkout; it is not claimed by the local
guard.
