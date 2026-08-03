# Plan: Minimum viable public release

**Status:** DRAFT 2026-08-03 — owner approval is required before any code or release work begins. This draft authorizes no implementation, tagging, publication, or external review.

## Objective

Ship the smallest public release that delivers PTK's existing product value:

- exact PowerShell execution in named warm sessions;
- compact PowerShell object and bounded text output;
- immutable recovery of truncated output;
- timeout, crash, reset, and next-invocation recovery; and
- a five-tool MCP surface that can be installed, registered, used, and removed.

The release is not a vehicle for new features, infrastructure, reviewer-driven hardening, or qualification programs.

## Authority and supersession

If approved, this plan supersedes `.agents/plans/release-readiness.md` and the unfinished release work in `.agents/plans/release-distribution.md`. Their landed implementation remains usable evidence; their activation gates, topology, matrices, build-identity scheme, automation, and review requirements do not carry forward.

Implementation proceeds one product slice at a time. Each slice is committed, pushed under the repo's push policy, and closed before the next begins.

No model or human review is part of this plan. Every review invocation requires a separate, explicit owner approval naming that review.

## Proposed release contract — Decision 1

The candidate exposes exactly these tools:

- `ptk_invoke`
- `ptk_output`
- `ptk_state`
- `ptk_reset`
- `ptk_session`

Supported behavior:

1. `ptk_invoke` executes submitted PowerShell text exactly once in the selected session.
2. Named sessions retain ordinary PowerShell state until closed, reset, crashed, or timed out.
3. PowerShell objects are compressed before formatting; plain text remains text.
4. Bounded responses may return an immutable `ptk_output` handle for the same invocation.
5. A timeout or worker crash cannot prevent a later invocation from succeeding.
6. `route=auto` may use RTK only for one unambiguous native application command with constant arguments. Every ambiguous or unsupported shape falls back to exact PowerShell without advice or retry.
7. `route=pwsh` always means exact PowerShell. `route=rtk` remains an explicit assertion of the same narrow native-command eligibility rules.

Removed behavior:

- automatic Bash syntax detection, refusal, validation, or delegation;
- post-success suggestions that rewrite the submitted command;
- any other model coaching based on inferred user intent.

Users who want Bash invoke `bash` explicitly as an ordinary native application from PowerShell. PTK does not infer another shell.

## Explicit non-goals

The first release does not add or qualify:

- SIEM, audit administration, enterprise evidence export, or enterprise acceptance;
- Kimi or another new harness integration;
- automatic shell-hook installation or model-facing redirect hooks;
- Outlook/COM active-getter evaluation;
- new routing modes, retry behavior, or command rewriting;
- Winget, package-manager publication, signing infrastructure, or a release workflow;
- telemetry, performance scorecards, soak targets, flake-count targets, mutation programs beyond a required regression proof, or unique identity on every rebuild;
- broad backlog cleanup, periodic code review, or reviewer approval gates.

Audit and SIEM stay disabled and are not advertised, configured, or included as separately supported artifacts. Existing source may remain, but defects reachable only through those excluded configurations do not block this release.

## Release-blocking rule

A defect blocks the candidate only when it is reproducible in the approved release contract and does at least one of the following:

- executes a different command, loses user data, or repeats execution;
- prevents install, launch, registration, ordinary use, or uninstall;
- breaks a named session or prevents recovery after timeout or crash;
- makes one of the five tools return materially wrong or unrecoverable output;
- exposes a security defect in the default supported configuration; or
- causes the supported Windows artifact to be quarantined by current Windows Defender.

Excluded features, unsupported platforms, dormant optional subsystems, cosmetic diagnostics, and speculative review findings do not block the release.

## Slice 1 — remove post-success command advice

Delete the `PostSuccessGuidance` feature end to end from:

- `server/PtkMcpServer/Execution/ExecutionPlanner.cs`
- `server/PtkMcpServer/Execution/ExecutionPlan.cs`
- `server/PtkMcpServer/RunspaceHost.cs`
- its focused tests and public documentation

Preserve exact execution and ordinary output from the original mixed pipeline. Add only a regression proving successful execution never appends a rewritten command suggestion. Close `opr-58` as removed behavior.

**Complete when:** no production or public-documentation reference to post-success guidance remains, the regression fails against the pre-removal behavior, and the affected server tests pass.

## Slice 2 — remove automatic shell inference

Remove automatic Bash detection, refusal, validation, and delegation from the shipped invocation path:

- remove `Get-PtcShellDialectFinding` from the module implementation and exports;
- remove dialect-specific production use from `TrustedPreflightClassifier`, retaining or moving only command-resolution data needed by narrow RTK routing;
- remove `BashProcessRunner`, Bash identity/protocol fields, and their now-unreachable tests after confirming no remaining production caller;
- make `ptk_invoke` documentation describe PowerShell execution and explicit native `bash` invocation only; and
- remove harness guidance that promises automatic dialect handling.

Do not replace the classifier with another heuristic. Close accepted findings whose production path disappears, including the shell-classifier and Bash-runner family; do not implement their symptom-level repairs.

**Complete when:** ordinary valid PowerShell reaches PowerShell unchanged, parse errors are reported by PowerShell, explicit `bash ...` runs as an ordinary native command, and no production code can infer or delegate a shell.

## Slice 3 — narrow native RTK routing

Keep RTK routing only where the submitted script is one native application invocation with constant arguments and a stable executable identity. For every disputed AST, redirection, trap, relative-drive, uncertain identity, or platform-ambiguous case, execute the original text in PowerShell instead of adding resolution machinery.

Repair only state directly reachable in the retained route, including restoring `$LASTEXITCODE` correctly after a no-start result and rejecting an explicit unknown route value at the live tool boundary.

Add focused regressions for the retained boundary. Do not broaden eligibility to preserve marginal routing coverage.

**Complete when:** retained native routing still reduces output through RTK, all ambiguous cases fall back before a user process starts, and exact PowerShell execution remains the sole fallback.

## Slice 4 — core session reliability

Land two independent fixes, one commit each:

1. A canceled or timed-out read-only `ptk_state` call before pipe publication must not replace, poison, or kill an otherwise healthy named session (`opr-20`).
2. Normal server/session disposal must send and observe the worker shutdown handshake before forced containment cleanup; force remains the bounded fallback (`opr-19`).

Guard the user-visible result only: healthy session state survives a canceled query, and graceful shutdown runs cleanup while a hung worker is still forcibly contained.

**Complete when:** both focused regressions fail against their prior behavior, pass with the fixes, and the next invocation succeeds after each recovery path.

## Decision 2 — supported release platforms

Choose the smallest useful platform/RID set before platform-specific work. The minimum recommendation is one primary platform for the first candidate, with other platforms explicitly unsupported until a later release.

- If Windows is selected, a current Defender quarantine check is a product gate.
- If Linux is selected, repair the live-descendant identity/containment failure represented by `opr-15` before packaging.
- If macOS is selected, repair both `opr-14` and `opr-15` before packaging.

Do not build or test an unselected RID.

## Decision 3 — Outlook/COM boundary

Recommended first-release contract: materialized, selected, and deserialized values are supported; PTK does not invoke active/lazy/COM getters merely to enrich output. Document the limitation and leave GitHub issue #8 open for real-environment follow-up. If the owner instead makes active Outlook/COM values mandatory, that becomes a separately planned product slice and blocks packaging.

## Decision 4 — release version

Choose one release version. The same value must appear in every user-visible version surface; no separate build-number system is added.

## Slice 5 — coherent version and minimum package

After the owner chooses the release version:

1. Set the same version in the module manifest, server assembly/package metadata, installed metadata, and user-visible diagnostics.
2. Put the source commit in the assembly informational version. Do not create per-rebuild version uniqueness or a provenance system.
3. Reuse `scripts/dev-install.ps1`, `scripts/ptk_install_transaction.psm1`, and the existing staged-package implementation.
4. Add one public `install.ps1` entry point usable through `pwsh` on every selected platform. It installs a prebuilt package transactionally and supports uninstall.
5. Package only the MCP server, its required runtime/native files, the shaping module, license/readme material, and a registration command. Do not package SIEM, audit-admin tools, tests, review records, hooks, or development helpers.
6. Print the generic stdio MCP registration command; do not edit harness configuration automatically.

Build candidate artifacts manually on the matching selected platform/RID. Produce SHA-256 checksums. Do not add release CI.

**Complete when:** install and uninstall operate outside the repository checkout, every user-visible version agrees, and a new shell can launch the installed server.

## Slice 6 — direct product proof

During implementation, run only focused tests for changed behavior. Once the candidate is assembled, run the existing module and server suites once, then perform this direct check on one clean host for each selected platform:

1. install the candidate;
2. launch it through the printed registration command;
3. list exactly the five supported tools;
4. open a named session and prove state survives a second invocation;
5. compress a representative PowerShell object;
6. preserve representative plain text;
7. recover bounded large output through `ptk_output`;
8. time out one invocation and prove the next invocation succeeds;
9. reset and close a named session; and
10. uninstall and prove the installed launch path is gone.

For Windows, also scan the exact packaged bits with current Defender. Record commands and outcomes, not scores or derived quality metrics.

Do not add a test framework, new matrix, soak run, performance threshold, repeated-run requirement, or review step. A failure in direct product behavior is repaired within its owning slice. An unrelated infrastructure failure is recorded and does not expand this plan without owner approval.

## Decision 5 — publish

Present the exact candidate version, commit, selected platform artifacts, checksums, direct-check outcomes, and known limitations. Tagging and creating a GitHub release require the owner's explicit final go.

## Ordered execution

1. Approve Decision 1.
2. Execute Slices 1–4.
3. Resolve Decisions 2 and 3, one at a time.
4. Resolve Decision 4 and execute Slice 5.
5. Execute Slice 6.
6. Resolve Decision 5 and publish only on explicit go.
