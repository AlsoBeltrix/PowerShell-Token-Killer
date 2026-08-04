# hcc-2: Claude payload gate accepts an empty bin directory

**Severity**: MEDIUM — can replace a working claude registration with a dead one and arm a deny hook pointing at a server that cannot start.
**Status**: In progress
**Branch**: —
**Commit**: (pending)

## Evidence
`scripts/ptk_init.ps1:333` — `$payloadPresent = Test-Path ... (Join-Path $PtkHome 'bin')`
tests the bin DIRECTORY. The folded-in registration then remove-then-adds
`$PtkHome/bin/PtkMcpServer[.exe]` without verifying that file exists. Every
other leg tests the binary leaf (e.g. kimi at line 905). The test suite's
`fakeHome` fixture creates only `bin/`, masking this.

## Predicted observable failure
With a leftover/damaged `~/.ptk/bin` (directory exists, binary absent),
ptk_init removes a possibly-working custom claude registration, registers a
nonexistent executable, and installs the blocking hook — every shell call is
then denied toward an MCP server that cannot start.

## What
The payload gate predates registration living in the leg and was never
tightened when registration folded in.

## Approach
(pending)

## Files changed
(pending)

## Guard proof
(pending)

## Coder dispute (if any)
—

## Known gaps
—

## Reviewer comments
(intake) Reviewer: codex / gpt-5.6-sol / xhigh (inline, session-only) / standard — generation pass over 19201a1..092df3b, codex-cli 0.146.0, verdict findings (5), capability_ok true, 2026-08-04.
