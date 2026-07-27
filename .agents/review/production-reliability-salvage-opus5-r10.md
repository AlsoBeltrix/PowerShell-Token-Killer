# Production reliability salvage — Claude Opus 5 Slice 1a closure, round 10

**Status:** `ACCEPT` — all four round-9 findings are closed, both verification
addenda are complete, and no blocking or major finding remains.

## Review identity

- Reviewed commit:
  `dbac8d57066819d1595e94797eaa0b05c13a52c4`
- Reviewed plan blob:
  `5607af749af0235e431f50d18fe1d0478189d2b9`
- Reviewer: Claude Code `2.1.220`, invocation pinned to canonical model
  `claude-opus-5`, effort `max`, with no fallback configured
- Prompt:
  `.agents/review/production-reliability-salvage-opus5-r10.prompt.md`
- Prompt SHA-256:
  `ec9cd7aab68e84647e54e581852822a1d4c81b6d8c95b7facf661872aefccfa6`
- Invocation: read-only `Read`, `Glob`, and `Grep` tools only; headless; no
  session persistence; safe mode; strict empty MCP configuration; detached
  clean worktree
- Result: exit 0, 29 turns, 394,356 API milliseconds, 389,564 wall
  milliseconds, no permission denial, result UUID
  `251f2ce9-c752-46a6-9d9e-6135c9c8e398`
- The unrelated Headroom proxy replaced the returned review text with immutable
  CCR handle `59f85e71704e`. The exact same-invocation original was retrieved
  once through the proxy's loopback read endpoint; the review was not rerun.
- Preflight and postflight confirmed the exact reviewed SHA, exact plan blob,
  and a clean review worktree. The worktree was then removed.

## Verdict

`ACCEPT`

All four round-9 findings are closed. The plan now names the real static
runspace-creation-lock convoy, carries independent residual failures instead of
claiming determinism, gives cross-platform CI an executable owner-gated path
without hiding the known Windows blockers, and rewrites rather than erases the
intermittent-suite record after verification.

Both verification addenda are complete: each of the three ordinary proof runs
records host load and stops on any failure, and mechanical runner diagnostics
prove both the default disabled state and an explicit diagnostic override back
to enabled without adding another configuration file or source mechanism.

No round-8 closure is reopened. No production file, runtime, timeout,
assertion, public schema, recovery mechanism, scheduler abstraction, retry, or
additional configuration mechanism is authorized.

## Round-9 closures

1. **Concrete mechanism:** closed. The plan identifies
   `RunspaceHost`'s static process-wide creation lock held across unbounded
   `Runspace.Open()` and the two `RunspaceHostTests` hooks that deliberately
   block while holding it. Default cross-class scheduling can therefore
   withhold runspace creation from unrelated classes past fixed watchdogs.
2. **Truthful exit claim:** closed. The plan claims only removal of
   cross-collection scheduling contention. It explicitly retains the
   anchored-evidence publication/removal ordering race, fixed watchdogs, and a
   recurring bounded-observer `JobManager.Dispose` failure as independent
   signals.
3. **Executable cross-platform gate:** closed. The normal workflow does not
   trigger for `impl/**`; on a separate owner go, the exact commit is pushed to
   `ci/**` or placed in a PR to `master`. The matrix permits no new or
   scheduling-class failure, carries the two known Windows worker/Job Object
   failures explicitly, and remains described as macOS-only until that run
   exists.
4. **Blocker disposition:** closed. After implementation verification,
   `.agents/state.md` is rewritten rather than deleted to retain the diagnosed
   cause, anchored-evidence race, fixed watchdog residue, and current
   Windows/CI status.

## Mechanism and scope

The reviewer independently confirmed that `RunspaceHost.CreationLock` is taken
before the test hook and remains held through `Runspace.Open()`. Exactly two
hook assignments in `RunspaceHostTests` block inside that lock. The class has
no collection annotation, so default xUnit scheduling permits a same-testhost
convoy that does not represent the settled process-per-session production
topology.

The implementation remains one new file in the non-packable main server test
project with one assembly-level
`CollectionBehavior(DisableTestParallelization = true)` attribute. The linked
SIEM conformance project cannot inherit the file. Explicit concurrency inside
tests and later multi-process/multi-session acceptance gates remain active.

## Non-blocking advisories

- When cross-platform CI runs, record the exact names of any two recurring
  Windows kill-path failures so the no-new-failure comparison is
  mechanically decidable.
- Existing `ProcessEnvironment` and disabled-parallelism collection annotations
  must remain. They document shared-state ownership and become active again
  during the explicit inverse-override diagnostic.

Neither advisory changes the accepted plan or justifies another review round.

## Owner decision

The smallest approval now needed is:

Approve Slice 1a and give the implementation go for one test-only commit that
adds the assembly-level
`CollectionBehavior(DisableTestParallelization = true)` attribute under
`server/PtkMcpServer.Tests/`, with no production, timeout, assertion, schema, or
existing collection-annotation change.

This approval is independent of pending product decisions 3-4 and does not
authorize them. A `ci/**` push or PR remains a separate outward-action ask
after local proof runs.

## Production confidence

Acceptance establishes that the amendment is coherent, minimal, and ready for
owner approval. It does not establish that the implementation clears the
intermittency; the three stopped-on-failure local runs still have to prove that.
It closes no anchored-evidence, Windows containment, vulnerable dependency,
ARM64 build, or production-readiness blocker.

The next action after owner approval is to add the one attribute, prove runner
off/on behavior, run the ordinary server suite three consecutive times with
load recorded before each, stop on any failure, and rewrite the blocker record.
