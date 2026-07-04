# rel1-2: Codex TOML snippet invalid for paths containing an apostrophe

**Severity**: LOW — affects only users whose home path contains a single
quote, but the printed snippet is then unparseable config.
**Status**: Verified (re-grade accepted 2026-07-04)
**Branch**: master (direct, per this repo's recorded codex-loop precedent)
**Commit**: `719fd85`

## Evidence
`Show-PtkCodexSnippet` emitted the binary path in a TOML literal
(single-quoted) string; TOML literal strings cannot contain a single quote,
so `/Users/O'Brien/.ptk/bin/PtkMcpServer` produced an unparseable line.
(The literal string itself was the fix for the earlier pre-commit finding
about backslash escapes in basic strings — the two constraints must be
solved together, not traded.)

## Predicted observable failure
A user with an apostrophe in their home path pastes the snippet into
`~/.codex/config.toml`; Codex fails to parse its config and cannot load the
ptk MCP server.

## What
Neither raw basic strings (backslash escapes) nor literal strings
(apostrophes) can hold every real path unescaped.

## Approach
Emit a TOML basic string with explicit escaping: backslashes doubled and
double quotes escaped. Valid for Windows paths, apostrophe paths, and both
combined.

## Files changed
- `scripts/dev-install.ps1` — Show-PtkCodexSnippet escapes the path

## Guard proof
Snippets generated for `C:\Users\O'Brien\...`, `/Users/O'Brien/...`, and a
plain path all parse under python tomllib and round-trip to the exact
original path.

## Coder dispute (if any)
None.

## Known gaps
None.

## Reviewer comments
codex (codex-cli 0.142.5), reviewed 10d4a1a against base d0e34d6,
2026-07-04 (UTC): finding returned. Re-grade at head 719fd85: resolution
ACCEPTED, zero new findings (no_findings=true).
