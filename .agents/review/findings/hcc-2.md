# hcc-2: Claude payload gate accepts an empty bin directory

**Severity**: MEDIUM — can replace a working claude registration with a dead one and arm a deny hook pointing at a server that cannot start.
**Status**: Verified
**Branch**: —
**Commit**: `39d6064`

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
The claude leg's `$payloadPresent` now tests the platform server binary as
a leaf (`bin/PtkMcpServer[.exe]`, `-PathType Leaf`) instead of the `bin/`
directory — the same gate shape the codex/grok/kimi legs already used. The
suite fixtures that masked this (`fakeHome`, `homeWithScripts`) now carry a
stub binary, and a new test pins the dir-without-binary refusal.

## Files changed
- `scripts/ptk_init.ps1` (claude leg payload gate) — leaf test
- `tests/PwshTokenCompressor.Tests.ps1` — fixtures + hcc-2 guard test

## Guard proof
- `tests/PwshTokenCompressor.Tests.ps1::'refuses registration and hook when bin/ exists but the binary does not (hcc-2)'` — with the fix stashed the directory passes the gate and the test FAILS (exit 0, settings written); restored it PASSES (verified 2026-08-04).

## Coder dispute (if any)
—

## Known gaps
—

## Reviewer comments
(intake) Reviewer: codex / gpt-5.6-sol / xhigh (inline, session-only) / standard — generation pass over 19201a1..092df3b, codex-cli 0.146.0, verdict findings (5), capability_ok true, 2026-08-04.

(verification) Reviewer: codex / gpt-5.6-sol / high / standard — codex-cli 0.146.0 (model from the -m pin; the JSONL stream emits no model id). reviewed_sha 39d60645e1b92798caa2fbd51ba84b9f7268a642, base_sha 11df2909c97021df1bdcdca964b8aea88ebffd62, guard_confirmed true, capability_ok true, verdict **accepted**, 2026-08-04T23:27Z. Comments: "Guard proof behaved as required: fixed PASS, base implementation produced the expected failed assertion, restored fix PASS." / "Focused '*ptk_init*' set passed 48/48 with no adjacent regression." / "The binary-leaf gate closes the recorded empty/leftover bin-directory failure before Claude registration or hook installation." Record committed as part of the verification history.
