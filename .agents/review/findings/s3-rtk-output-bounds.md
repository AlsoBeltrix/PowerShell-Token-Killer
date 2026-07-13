# s3-rtk-output-bounds: RTK output bypasses PTK text bounds and ANSI stripping

**Severity**: HIGH — the default terminal-native route can return unbounded,
ANSI-bearing text and contradict the tool's core bounded-output contract.
**Status**: Verified
**Branch**: `fix/s3-rtk-output-bounds`
**Commit**: `7d8b2a0`, guard-strengthening follow-up `bda3562`

## Evidence

`server/PtkMcpServer/RunspaceHost.cs:2008` runs `Compress-PtcOutput` only when
the dispatch provenance is `PowerShellObjects`. Every RTK plan is required to
carry `RtkUnknown`, so RTK output receives only `Out-String`.

Claude reproduced the regression at fixed head `669ce6e` with a fake RTK that
emitted 1,000 lines plus ANSI color. The reviewed head returned 1,002 lines /
8,903 characters with the escape sequence intact and no elision marker. Base
`0c08379` returned 401 lines / 3,559 characters, stripped ANSI, and included
the labeled elision marker.

## Predicted observable failure

A common RTK-routed command can flood model context beyond the adopted 400-line
/ 40-KB bound and retain terminal control sequences even though the tool
description promises both cleanup and labeled bounding.

## What

Slice 3 correctly prevents RTK-origin output from entering a second generic
`rtk log` pass, but does so by bypassing the entire PTK output module. The
approved plan requires PTK text cleanup/bounding to remain while provenance
suppresses only the lossy second RTK log optimization.

## Approach

Every non-raw result with compression available now enters
`Compress-PtcOutput`, with the immutable execution provenance passed as a
machine code. The module uses RTK provenance to suppress only
`Invoke-PtcRtkLog`; ANSI stripping and bounded passthrough still apply. Direct
PowerShell log shaping continues to require the plan's audited pinned RTK
identity.

## Files changed

- `server/PtkMcpServer/RunspaceHost.cs`
- `server/PtkMcpServer.Tests/InvokeToolTests.cs`
- `src/PwshTokenCompressor.psm1`

## Guard proof

- `Rtk_routed_output_is_not_shaped_by_rtk_a_second_time` emits 1,001
  ANSI-colored lines through a fake RTK and requires ANSI-free bounded output,
  the labeled elision marker, at most 402 lines, no `[ptk:log` second-shaping
  marker, and exactly one fake RTK invocation.
- The reviewer independently restored the old host gate and observed the guard
  fail on retained ANSI/unbounded output. Independently disabling only the
  module provenance skip made it fail on the second-shaping marker. Both
  restorations passed at the exact reviewed head.

## Coder dispute (if any)

None. The finding is independently admitted.

## Known gaps

None currently.

## Reviewer comments

Claude Code 2.1.207 (`claude-opus-4-8`) reviewed
`0c08379a02c796b8ea0e1779c196840c6a9b1269..669ce6ea47c520a9c3bb73411192630d56ed519b`
with `guard_confirmed=true` and verdict `reopened`, recorded
2026-07-13T01:48:24Z. The required correction above is the reviewer's
evidence-backed disposition.

Claude Code 2.1.207 (`claude-opus-4-8`) reviewed
`89b83b78ed02142bc93ee16b1256ab31585498eb..bda3562c5340619c8c1bb41404ec73bbba7c7902`
with `guard_confirmed=true` and verdict `accepted`, recorded
2026-07-13T03:53:56Z. At the exact reviewed head, Claude passed 1,018/1,018
.NET tests, 139 Pester tests with two platform skips, and the stdio handshake;
both review trees were clean and removed.
