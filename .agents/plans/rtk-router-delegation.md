# Plan: RTK router delegation and minimum viable release

**Status:** DRAFT 2026-08-03. Decision 1 is unruled; no slice below is
authorized. This plan supersedes `.agents/plans/minimum-viable-release.md`
on approval, retaining that document's release-blocking rule, non-goals,
and Slices 5–6 by reference.

## Product definition

PTK is two things, equally load-bearing:

1. **A warm PowerShell runspace.** Named sessions, one contained worker
   process each, ordinary PowerShell state persisting across calls.
2. **A compression router.** Every invocation is compressed. PowerShell
   objects are compressed by PTK itself (`Compress-PtcObject`), because
   that must happen before PowerShell formats objects to text. Everything
   else routes to RTK, which owns native-command filtering and log
   compression.

PTK does not reimplement what RTK already does. Where RTK can decide, RTK
decides.

## Upstream contract (validated 2026-08-03, rtk 0.44.2, source `../rtk`)

`rtk hook check --agent <agent> <command>` is the router entry point. It
calls `rewrite_command` (`../rtk/src/discover/registry.rs:569`).

- Input: one shell command string, positional.
- Success: exit 0, rewritten command on **stdout**, one line.
- Decline: exit 1, `No rewrite for: <cmd>` on **stderr**, no stdout.
- A hook-not-installed advisory may appear on stderr. Ignore stderr for
  routing decisions; read stdout only.

Observed behavior:

| Input | stdout |
| --- | --- |
| `git status && cargo test` | `rtk git status && rtk cargo test` |
| `cd /tmp && git status && weirdthing` | `cd /tmp && rtk git status && weirdthing` |
| `FOO=bar git status` | `FOO=bar rtk git status` |
| `git diff HEAD~1` | `rtk git diff HEAD~1` |
| `rtk git status` | `rtk git status` (idempotent) |
| `git log --oneline \| head -20` | (declines) |
| `cat <<EOF…` | (declines) |
| `for f in *.txt; do …; done` | (declines) |
| `npm test` | (declines) |

The rewriter decomposes `&&`, `||`, `;` and rewrites each segment
independently, preserving segments it cannot handle. It normalizes env
prefixes, `sudo`, absolute binary paths, and git global options, and is
quote- and line-continuation-aware. It declines heredocs, `$((`,
pipelines, and multi-line block constructs.

`rtk run -c` is **not** a compression path — it execs `sh -c`/`cmd /C`
with inherited stdio and no filtering. Never route through it.

`rtk log <file>` is a separate text filter, already used by
`Invoke-PtcRtkLog` (`src/PwshTokenCompressor.psm1:606`). Unchanged by this
plan.

## Decision 1 — routing authority (UNRULED; blocks every slice)

Proposed: RTK's rewriter becomes PTK's routing authority for non-PowerShell
work. PTK submits the exact script text to `rtk hook check`; a rewrite is
executed as PowerShell (the rewritten text is a shell command line that
PowerShell runs natively); a decline executes the original text unchanged.

PTK stops deciding routing eligibility from its own PowerShell AST walk and
stops resolving executable identity against PATH.

Consequences if approved:

- Routing coverage widens: compound commands and env-prefixed commands
  become routable, which they are not today.
- ~2,000 production lines are deleted.
- ~18 accepted findings close as removed code rather than being repaired.
- RTK stops being optional for native-command compression. Absent RTK, PTK
  still delivers warm state, object compression, ANSI cleanup, bounded
  text, and same-invocation recovery; native commands execute exactly and
  unfiltered. Startup must report which mode is active.

## Deferred decisions

Carried unchanged from `.agents/plans/minimum-viable-release.md`: supported
platforms (Decision 2), Outlook/COM boundary (Decision 3), release version
(Decision 4), publish (Decision 5). Each requires its own separate go.

## Release-blocking rule

Use the rule in `.agents/plans/minimum-viable-release.md` §"Release-blocking
rule" verbatim. Findings reachable only through disabled audit, the SIEM
receiver, `PtkAuditAdmin`, or an unselected platform do not block.

## Slice 1 — delete post-success command advice

Remove `PostSuccessGuidance` end to end:

- `server/PtkMcpServer/Execution/ExecutionPlanner.cs` (17 references)
- `server/PtkMcpServer/Execution/ExecutionPlan.cs` (8 references, incl. the
  `PostSuccessGuidance` record)
- `server/PtkMcpServer/RunspaceHost.cs` (1 reference)
- `server/PtkMcpServer.Tests/ExecutionPlannerTests.cs` (10 references)
- any public documentation naming it

Preserve exact execution and ordinary output of the original mixed
pipeline. Add one regression proving a successful mixed-dataflow invocation
returns no rewritten-command suggestion.

Closes `opr-58` (HIGH) as removed behavior.

**Complete when:** no production or documentation reference remains, the
new regression fails against the pre-removal build, and the server suite
passes.

## Slice 2 — delegate routing to the RTK rewriter

Add a rewriter client that invokes the startup-pinned RTK identity as
`hook check --agent ptk <script>`, reads stdout only, and applies a bounded
timeout independent of the call budget. Treat non-zero exit, empty stdout,
timeout, or a missing RTK binary as *decline*, never as failure.

Rewrite acceptance rules — a rewrite is used only when all hold:

1. RTK exited 0 with non-empty stdout.
2. stdout differs from the submitted script (identity rewrites are a
   decline; nothing to gain).
3. stdout contains no newline the submitted script did not contain.

Otherwise execute the original text unchanged.

Delete, after confirming no remaining production caller:

- `Resolve-PtcInvokeScript` and its export
  (`src/PwshTokenCompressor.psm1:686`, export list line 1245)
- `server/PtkMcpServer/Execution/ColdCommandResolution.cs` (268 lines)
- `ColdCommandTargetIdentity` capture/revalidation and the
  `ResolutionContext.Cold` RTK-eligibility branch in `ExecutionPlanner`
- `TryCreateRtkArgumentVector` and `SupportsDirectArgumentPassing`
  eligibility gating, and the `rtkArgumentVector` dispatch shape if no
  retained path uses it

Retain: `RtkProcessRunner` process mechanics, `RtkExecutableIdentity`
startup pinning, `ExecutionPath.Rtk`, and output provenance so
RTK-produced output is not sent through `rtk log` a second time.

Closes `opr-48`, `opr-49`, `opr-50`, `opr-51` (PATH/drive/casing
resolution) and `opr-55` (argv boundary divergence) as removed code — all
five exist only because PTK resolved targets itself.

**Complete when:** a compound command (`git status && cargo test`) routes
through RTK, a declined command (`npm test`) executes exactly as
PowerShell, an absent RTK binary executes every command exactly, and no
production code resolves an executable against PATH for routing.

## Slice 3 — delete shell inference

Remove automatic Bash detection, refusal, validation, and delegation:

- `Get-PtcShellDialectFinding` and its export
  (`src/PwshTokenCompressor.psm1:772`, export list line 1244)
- `AssessShellDialect` and dialect members of
  `server/PtkMcpServer/TrustedPreflightClassifier.cs`; retain any
  command-fact capture still used by a retained path
- the `checkDialect` block in `RunspaceHost.cs` (~lines 3162–3234),
  including `FormatDialectRefusal` and
  `FormatBashDelegationUnavailable`
- `server/PtkMcpServer/Execution/BashProcessRunner.cs` (803 lines),
  `BashExecutableIdentity.cs`, `ExecutionPlanner.CreateBash`,
  `ExecutionPath.BashViaRtk`, `ExecutionDomain.Bash`,
  `PreExecutionValidation.BashSyntax`, and the `_bashExecutableIdentity`
  field and its startup resolution
- `server/PtkMcpServer.Tests/ShellDialectWiringTests.cs` and dialect cases
  in `TrustedPreflightClassifierTests.cs`
- harness guidance text promising automatic dialect handling

Bash reaches RTK the same way every other native command does: the user
writes `bash -lc '...'`, and Slice 2's rewriter sees it. PTK infers no
shell and refuses no input for dialect reasons.

Closes `opr-17`, `opr-32`, `opr-33`, `opr-43`, `opr-44`, `opr-45` (false
dialect refusals) and `opr-47` (validator budget) as removed code.

**Complete when:** valid PowerShell always reaches PowerShell, parse errors
are reported by PowerShell itself, `bash -lc '...'` runs as an ordinary
native command, and no production code classifies dialect.

## Slice 4 — native error-record fidelity

Native command stderr captured as `ErrorRecord` objects is currently
shaped by the object compressor, which emits
`@{Value=[active member not evaluated]}` and discards the message text.
Reproduce with any native command writing to stderr under `2>&1`.

Shape `ErrorRecord` and `ManagementBaseObject`-style records by their
textual message, not by property enumeration. Preserve object compression
for ordinary objects.

Guard: a native command emitting known stderr text returns that text.

This is a release blocker under the release-blocking rule — a supported
tool returns materially wrong output.

**Complete when:** the guard fails against the current build, passes after
the fix, and object compression of ordinary objects is unchanged.

## Slice 5 — session reliability

Two independent fixes, one commit each:

1. `opr-20` (HIGH): a canceled or timed-out read-only `ptk_state` before
   pipe publication must not replace, poison, or kill an otherwise healthy
   named session.
2. `opr-19` (HIGH): normal disposal must send and observe the worker
   shutdown handshake before forced containment cleanup; force remains the
   bounded fallback.

Guard user-visible results only: healthy session state survives a canceled
query, and graceful shutdown runs cleanup while a hung worker is still
forcibly contained.

**Complete when:** both regressions fail against prior behavior, pass with
the fixes, and the next invocation succeeds after each recovery path.

## Slice 6 — close the finding backlog

Single commit, records only, no code. For every accepted `opr-*` finding
not closed by Slices 1–5, record one disposition in
`.agents/review/index.md`:

- **closed-removed** — its production path no longer exists.
- **closed-out-of-scope** — reachable only through disabled audit, the
  SIEM receiver, `PtkAuditAdmin`, or an unselected platform.
- **open-blocker** — meets the release-blocking rule and is not yet fixed.
  Any finding landing here needs its own slice and an owner go.

Delete `## Next` review-intake entries from `.agents/state.md` for closed
findings; `.agents/review/index.md` owns dispositions.

No finding may remain "accepted and plan-gated". That state is what
produced a 59-item backlog with no repairs, and it is retired by this
plan.

**Complete when:** every `opr-*` has exactly one disposition and
`.agents/state.md` carries no review-intake queue.

## Slice 7 — version, package, direct proof

Execute `.agents/plans/minimum-viable-release.md` Slices 5 and 6 unchanged,
behind Decisions 2 and 4. Add to its direct-check list:

11. a compound native command routes through RTK and returns compressed
    output;
12. with RTK absent from PATH, the same command executes exactly and the
    startup report says native filtering is unavailable.

## Process constraints

These bind the implementing agent.

- **No reviewer invocation.** No `codereview`, `openreview`, or unattended
  review of any kind until the release ships. Each review requires a
  separate explicit owner approval naming that exact invocation.
- **Fix or close, never record.** A defect found during implementation is
  either fixed in the slice that found it or given a Slice 6 disposition.
  Do not create a new gated finding.
- **No file-by-file source review.** Reviewing production files in sequence
  to enumerate defects is prohibited by this plan.
- Focused tests during implementation; the full battery
  (`.agents/repo-guidance.md` §Verification) once per slice before commit.
- One slice per commit, pushed under `.agents/push-policy.md`.
- `Audit/` (20,916 lines), `siem/`, and `PtkAuditAdmin` are untouched by
  every slice. They are excluded from the release, not deleted; deletion is
  a later decision needing its own evidence.

## Ordered execution

1. Decision 1.
2. Slices 1–3 (deletions and the router swap).
3. Slice 4, then Slice 5.
4. Slice 6.
5. Decisions 2 and 4, then Slice 7.
6. Decision 5 and publish only on explicit go.
