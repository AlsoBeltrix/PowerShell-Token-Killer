# Production reliability salvage — Claude Opus 5 Slice 1a review, round 9

**Status:** `REVISE` — the one-attribute test-only mechanism is accepted; four
plan-text and verification-gate corrections are required before implementation.

## Review identity

- Reviewed commit:
  `17469d9b4ff9e9eae6a2dad13903c7294d09724d`
- Reviewed plan blob:
  `0b7e7a2966c02b38685ee2dbc2fe7a459b438ab5`
- Reviewer: Claude Code `2.1.220`, invocation pinned to canonical model
  `claude-opus-5`, effort `max`, with no fallback configured
- Prompt:
  `.agents/review/production-reliability-salvage-opus5-r9.prompt.md`
- Prompt SHA-256:
  `d67f8a406f4b7de54854c5aea7398961224284b84f3fcbb5506ada343462564e`
- Invocation: read-only `Read`, `Glob`, and `Grep` tools only; headless; no
  session persistence; safe mode; strict empty MCP configuration; detached
  clean worktree
- Result: exit 0, 39 turns, 657,623 API milliseconds, 654,457 wall
  milliseconds, no permission denial, result UUID
  `aeb94206-5704-4a52-92c1-99c92b32afcc`
- The unrelated Headroom proxy replaced the returned review text with immutable
  CCR handle `f61ca47aac2a`. The exact same-invocation original was retrieved
  once through the proxy's loopback read endpoint; the review was not rerun.
- Preflight and postflight confirmed the exact reviewed SHA, exact plan blob,
  and a clean review worktree. The worktree was then removed.

## Verdict

`REVISE`

One assembly-level
`CollectionBehavior(DisableTestParallelization = true)` attribute is the
smallest safe repair. It changes only the non-packable test assembly, preserves
every explicit within-test concurrency proof, and matches the settled topology:
supported named sessions own separate worker processes and runspaces rather
than opening several production runspaces inside one process.

The diagnosis is stronger than the plan states because the repository contains
a concrete convoy mechanism. `RunspaceHost` holds one static process-wide
creation lock across unbounded `Runspace.Open()`. Two `RunspaceHostTests`
deliberately block through a hook invoked while that lock is held, and the class
has no collection annotation. Default cross-class scheduling can therefore
withhold runspace creation from unrelated tests past their fixed watchdogs.

No round-8 finding is reopened. No new runtime, recovery path, containment
mode, caller identity, persistence, template, daemon, shared session, or output
lane is introduced.

## Accepted conclusions

### Causation supported, within explicit limits

- The R0 retirement changed none of the five implicated product or test paths.
- Five implicated classes passed 125/125 together under ordinary parallelism.
- The complete suite passed 1,557/1,557 with collection parallelism disabled,
  and a paired default-parallel control also passed 1,557/1,557. Those results
  confirm intermittency rather than a deterministic R0 regression.
- The testhost was concurrently subject to extreme unrelated host load, which
  amplifies the in-assembly convoy.
- `StateToolTests.Path_drift_reports_an_entry_level_diff` does not assert that
  its invoke succeeded before it asserts the resulting state change.
- `AuditAnchoredRuntimeTests` retains a separate intra-test publication/removal
  ordering race. Serialization narrows but cannot close that race.
- If the bounded-observer `JobManagerTests` case recurs under serialization,
  particularly during `JobManager.Dispose`, it remains a product signal rather
  than a scheduling failure.
- The two audit failures are additionally bounded by Slice 2, which removes the
  anchored export runtime and its tests if audit decision 4 is approved.

### Scope is test-only

- The planned attribute lives only in
  `server/PtkMcpServer.Tests/`, whose project is not packable.
- No existing `AssemblyInfo.cs` exists, so the implementation is one new test
  file containing one assembly attribute.
- The separate SIEM conformance test project explicitly links a closed source
  list and cannot inherit the new parent-directory file.
- No runtime, package, public tool schema, timeout, assertion, or production
  scheduling behavior changes.

### Smallest fix accepted

- Raising timeouts weakens live bounds and can hide wedges.
- Retrying tests adds machinery and normalizes failures.
- More per-class collection annotations continue an incomplete, expanding
  scheduling patch and do not express aggregate process-global contention.
- An arbitrary maximum thread count is the same configuration size but keeps
  invalid same-process contention and makes the result machine-dependent.
- `xunit.runner.json` needs both a new content file and reliable output-copy
  metadata; the compiled attribute cannot silently fail to reach the runner.
- The measured cost was 9m37s serialized versus 7m58s default-parallel on the
  loaded diagnostic Mac. Correctness costs about 20% there, not a
  many-fold slowdown.

### Concurrency coverage preserved

Assembly-level collection serialization does not serialize tasks, threads,
processes, or multiple hosts created inside one test. Existing explicit tests
continue to cover same-session serialization, process-wide runspace creation,
concurrent audit repair and appends, atomic storage, protocol writer
serialization, and non-queueing state calls. Later plan gates still require
concurrent named sessions, independent worker PIDs/runspaces, sibling survival,
and two simultaneous server processes.

The existing `ProcessEnvironment` annotations and disabled-parallelism
collections must remain. They document unsafe sharing and become active again
if the assembly attribute is removed or explicitly overridden.

## Required corrections

1. **Name the mechanism.** The plan currently says only aggregate same-testhost
   pressure. Cite the static `RunspaceHost` creation lock held across unbounded
   `Runspace.Open()` and the two test hooks that deliberately block while that
   lock is held.
2. **Do not claim literal determinism.** Replace the Slice 1a Exit claim with
   the narrower result that default cross-collection scheduling contention has
   been removed. Carry the pre-existing `AuditAnchoredRuntimeTests`
   publication/removal ordering race as separate residue.
3. **Make cross-platform CI executable and honest.** State that the exact
   commit requires an owner-approved push to `ci/**` or a PR to `master`;
   ordinary `impl/**` pushes do not trigger the workflow. Require no new or
   scheduling-class failure on any OS while carrying the two known Windows
   worker/Job Object failures explicitly rather than demanding an impossible
   all-green result or silently ignoring them. Until that run exists, Slice 1a
   evidence is macOS-only.
4. **Disposition the blocker record.** After the slice, rewrite rather than
   delete the blocker entry with the diagnosed cause, retained audit ordering
   race, fixed watchdog residue, and current Windows/CI status.

The verification steps also need to record the load average at the start of
each of the three consecutive proof runs. If those runs occur only on an idle
machine, they cannot discriminate the previously observed combination of
in-assembly contention and unrelated host load.

## Production confidence

This amendment can make a red server suite meaningful again by removing its
largest self-inflicted scheduling confound. It does not make the product
production-ready, close the retained audit ordering race, resolve Windows
containment failures, remove vulnerable dependencies, or prove cross-platform
behavior.

The next action is to make the four corrections above, obtain fixed-blob
closure review, and put the corrected test-only Slice 1a to the owner as one
decision independent of pending product decisions 3-4.
