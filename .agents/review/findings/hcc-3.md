# hcc-3: Apostrophe in the ptk path produces invalid kimi TOML

**Severity**: MEDIUM — writes a config.toml kimi cannot load (harness config breakage) for a path containing an apostrophe.
**Status**: In progress
**Branch**: —
**Commit**: (pending)

## Evidence
`scripts/ptk_init.ps1:867` — `$kimiHookBlock` embeds `$hookCommand` in a TOML
literal string (`command = '...'`). TOML literal strings cannot contain a
single quote; `$hookCommand` carries the user-controlled home/payload path.

## Predicted observable failure
For a profile like `C:\Users\O'Brien`, install writes an invalid
`~/.kimi-code/config.toml`; kimi fails to load its configuration.

## What
Wrong TOML string kind for user-controlled content.

## Approach
(pending)

## Files changed
(pending)

## Guard proof
(pending)

## Coder dispute (if any)
—

## Known gaps
The staleness read-back (`Get-PtkKimiHookTarget`) must unescape whatever the
writer escapes, or Windows paths (backslashes) would read back as stale.

## Reviewer comments
(intake) Reviewer: codex / gpt-5.6-sol / xhigh (inline, session-only) / standard — generation pass over 19201a1..092df3b, codex-cli 0.146.0, verdict findings (5), capability_ok true, 2026-08-04.
