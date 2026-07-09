# mhi-8: codex leg's payload gate fires before the idempotency probe

**Severity**: MEDIUM — breaks the promised leave-as-is path for existing
(including custom) registrations; no config corruption.
**Status**: Verified
**Branch**: master (direct, per the recorded codex-loop precedent)
**Commit**: (filled in at commit)

## Evidence
`scripts/ptk_init.ps1` (Invoke-PtkCodexLeg, as committed in 7a068b9): the
`Test-Path $binary` payload check ran before `codex mcp get ptk`,
contradicting the plan's claim that an existing entry is left as-is.

## Predicted observable failure
A machine whose codex already has a working ptk registration (possibly a
custom one pointing somewhere other than `~/.ptk`) but no
`$PtkHome/bin/PtkMcpServer(.exe)` gets exit 1 with "no installed ptk
server" from `ptk_init.ps1 -Agent codex` — a bare detected-agent run can
fail even though codex is fully configured.

## What
The payload gate exists to prevent writing a broken config.toml entry —
i.e., it guards the ADD. Placed before the `get` probe, it also blocked the
no-op path that performs no write at all.

## Approach
Reordered: `codex mcp get ptk` probes first (CLI-present branch); an
answering registration short-circuits to leave-as-is regardless of payload;
only the add path requires the installed binary.

## Files changed
- `scripts/ptk_init.ps1` — Invoke-PtkCodexLeg registration branch order.

## Guard proof
- `tests/PwshTokenCompressor.Tests.ps1::leaves an existing codex
  registration as-is even without an installed payload` — fake codex shim
  on PATH answers `mcp get ptk` with exit 0, empty PtkHome; asserts exit 0
  and the leave-as-is message. FAILS with the gate-before-probe order
  reinstated; PASSES with the fix. (First behavioral codex-leg test — the
  shim removes the live-CLI dependency, so it runs on CI.)

## Coder dispute (if any)
None.

## Known gaps
None.

## Reviewer comments
Raised by codex (Codex v0.143.0, gpt-5.5, read-only) reviewing 7a068b9,
2026-07-09. Re-grade: see index.
