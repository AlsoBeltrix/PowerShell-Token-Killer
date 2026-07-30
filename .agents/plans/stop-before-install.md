# Plan: Stop all PTK runtime processes before install

**Status:** APPROVED 2026-07-30 — the owner directed PTK to require a complete
runtime stop before installing a new version and then said to continue.

## Goal

Make the existing install and uninstall precondition cover every shipped PTK
runtime process below the installed `~/.ptk` payload:

- `PtkMcpServer` on every platform, including Windows worker-mode processes;
- `PtkWorkerBroker` on Unix.

The installer refuses with the matching process names and PIDs. It does not
terminate processes automatically. The operator stops PTK, reruns the
installation, and restarts affected MCP client sessions.

## Scope

1. Rename the existing guard to describe the runtime-wide check.
2. Query the two fixed shipped runtime process names and retain the existing
   installed-path containment filter.
3. Keep the guard immediately before both ordinary install and uninstall.
4. Add a focused automated guard proving both names are covered.

## Non-goals

- side-by-side or retained-version installation;
- stable launcher or activation records;
- live-session continuity, migration, rollback, or pruning;
- automatic process termination;
- matching arbitrary `pwsh` or unrelated processes.

## Verification

Run the focused guard, then the repository Pester suite. Prove the new guard is
non-vacuous by temporarily removing `PtkWorkerBroker` from the checked names:
the focused guard must fail; restore it and require the focused and full Pester
suites to pass.
