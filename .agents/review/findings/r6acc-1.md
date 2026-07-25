# r6acc-1: `ptk_state` fails on the real apphost while an alias is recovering

**Severity**: HIGH — it blocks R6's acceptance matrix line "One worker crash
affects only one alias" (`.agents/plans/mcp-resilience.md`). If it is a product
defect rather than a test defect, a model loses its only state probe for the
whole recovery window, which is precisely when it needs it.
**Status**: **Fixed 2026-07-25**, mutation-proven. The acceptance test it
blocked now passes end to end, including the one-alias isolation assertions
that had never executed.
**Branch**: `feature/mcp-resilience-r1`
**Commit**: see the `ClearRecoveryMetadata` call added to
`FrozenDefaultSessionState.GrantWorkerCreateCapability`.

## Root cause (2026-07-25)

**A worker create issued while automatic recovery is in flight left the
previous recovering event's phase attached to an alias the same call had just
moved to `Starting`.** `Starting` is a manual state, which the public contract
forbids from carrying recovery facts, so the next snapshot threw:

```
System.ArgumentException: Nonautomatic session state cannot carry a recovery phase.
   at PtkSharedContracts.PublicSessionStateSnapshot.ValidateRecoveryPhasePairing
   at PtkSharedContracts.PublicSessionStateSnapshot..ctor
   at PtkMcpGuardian.Standalone.FrozenDefaultSessionState.ProjectSession
   at PtkMcpGuardian.Standalone.FrozenDefaultSessionState.SnapshotSessions
   at PtkMcpGuardian.Standalone.GuardianHostSupervisor.SnapshotState
   at PtkMcpGuardian.Standalone.GuardianHostSupervisor.EncodeStateSnapshot
   at PtkMcpGuardian.Standalone.GuardianHostSupervisor.DispatchAsync
```

`ptk_state` and the guardian-local `ptk_session list` are pure snapshot reads
(`GuardianHostSupervisor.DispatchAsync`, the `ptk_state` branch), so both failed
outright for the whole recovery window — exactly when a model needs them.

`GrantWorkerCreateCapability` set `State`, `ReadyForEffects` and
`BootstrapState` but not the recovery facts, unlike
`ObserveSessionRecoveryUnknown` and `ObserveSessionOperationResult`, which
already cleared them. The fix adds the same `ClearRecoveryMetadata(state)` call.

**The "what has been excluded" list below is wrong on its third bullet.** The
guardian projection *was* the cause. The passing unit guard cited there covered
a `Recovering` alias that had never had a worker create applied on top, so it
could not see this. Treat "excluded by a passing test" as excluded only for the
exact shape that test builds.

**How it was found, after four inconclusive elimination cycles:** by taking this
finding's own advice — make the exception visible rather than narrow further. A
temporary `catch` around the `ptk_state` branch of `DispatchAsync`, logging to a
file, produced the stack above on the first run. It cost minutes.

## Guard proof

`FrozenDefaultSessionStateTests.Worker_create_during_recovery_clears_the_recovery_facts_it_cannot_carry`
— applies a recovering lifecycle, then grants a worker create, then snapshots.
Reverting only the `ClearRecoveryMetadata` call reddens it with the exact
production message above; restoring it returns green.

## Second reproducer, simpler than the acceptance test

`r6acc-1-selfkill-probe.patch` in this directory: a single default alias, one
foreground `ptk_invoke` that kills its own worker
(`[System.Diagnostics.Process]::GetCurrentProcess().Kill()`), then poll
`ptk_state`. Fails on the first poll in ~3 s, with no second alias and no
crash-isolation scaffolding. It was written while checking whether `r6x-2` #2
was platform-neutral, and finding this instead is what closed the finding —
worth keeping because it is far cheaper to run than the acceptance test.

## Evidence

Writing the R6 acceptance test for one-alias crash isolation on the real
apphost produced a deterministic failure. After the scratch alias's real worker
process is killed, the next `ptk_state` call returns
`isError: true` with the generic body:

```
An error occurred invoking 'ptk_state'.
```

The failure is at the poll immediately after the kill. Everything before the
kill passes: initialize, `ptk_session open` on `scratch`, warm-sentinel set on
`default`, `$PID` captured for both aliases (confirmed distinct), and a
`ptk_state` that decodes cleanly with both sessions ready.

This is new behaviour introduced by this session's own work: before `9045b7b`
nothing emitted a nonterminal session lifecycle, so an alias never projected
`Recovering` at all. The regression window is `ba99972..02b924c`.

## What has been excluded

Each of these was checked and is NOT the cause:

- **The public-state schema.** `recovering` is `paired_but_nullable` for worker
  identity in `server/Contracts/ResilienceR0/contract.json`
  (`session_identity_by_state`), and the `oneOf` groups in
  `public-state.schema.json` admit `state: "recovering"` with a paired
  identity, `recovery_phase` in `{containment, attempting}`, `recovery_attempt
  >= 1`, `retry_after_ms` in `[250, 60000]`, `ready_for_effects: false`.
- **`PublicSessionStateSnapshot` construction and `ValidateRetry`.** The exact
  values the host emits (`Containment`, attempt 1, 250 ms) satisfy both.
- **The guardian projection and the public codec.** Pinned by the new passing
  test `Recovering_dynamic_alias_projects_and_encodes_through_the_public_contract`
  (`FrozenDefaultSessionStateTests`), which declares a dynamic alias, brings it
  ready, applies the exact recovering lifecycle the host emits, then projects
  and round-trips it through `PublicStateCodec`. It passes, so the failure is
  not in `FrozenDefaultSessionState.ProjectSession` or the codec.
- **stderr.** `standardError` is empty at the point of failure, so nothing is
  logged; the MCP tool wrapper swallows the underlying exception message.
- **The audit.** A dump of audit records matching fail/error/lifecycle produced
  nothing useful at the point of failure.

## Where to look next

The guardian-side state layer is proven fine in isolation, so the remaining
surface is the wire and the supervisor's event pump:

1. `GuardianHostSupervisor.HandleHostEventUnderAuthorityAsync` calls
   `_sessionSource.ObserveSessionLifecycle(sessionLifecycle)` directly
   (`GuardianHostSupervisor.cs:2724-2729`). Any throw there propagates onto the
   pump and would fault the host client, which would then make a later
   `ptk_state` throw. Confirm whether `ObserveSessionLifecycle` throws for the
   real event — the two candidate guards are the shared transition-version
   check and the recovering branch's "names an unknown worker" check added in
   `b336c60`.
2. `GuardianHostClient` correlation for an unsolicited event
   (`RequestId: null`). `GuardianHostClient.cs:1692-1695` correlates a
   `SessionLifecycleEvent` against an in-flight request using
   `LifecycleReasonMatches(value.Reason, request.Operation.Kind)`. A
   `Recovering`/`WorkerExit` event arriving while an unrelated request is in
   flight is a shape that never existed before this session.
3. `IsBootstrapInboundEvent` (`GuardianHostClient.cs:1810-1821`) admits exactly
   one session-lifecycle shape. It is scoped to bootstrap, but confirm it is
   not consulted post-ready.

The fastest decisive step is to make the underlying exception visible rather
than continue narrowing by elimination — the generic wrapper is what made four
diagnostic cycles inconclusive.

## Whether the test itself is wrong

Not established. It is possible the acceptance test should tolerate a
transient `ptk_state` error during recovery, but that reading contradicts the
plan: "Guardian-local state/list/output and MCP ping/tools remain prompt during
startup, containment, every backoff delay, circuit-open, and half-open."
Under that contract `ptk_state` must keep answering while an alias recovers, so
the test asserts the right thing and the product is the likelier defect. Do not
"fix" this by relaxing the assertion without settling that first.

## Reproducer

`Composition_isolates_one_alias_worker_crash_from_a_second_alias` is now a
committed suite test rather than a saved patch — it passes, so the reason it was
held out no longer applies. `r6acc-1-repro.patch` was deleted with that landing;
the test itself is the canonical copy.

## Coder dispute

None.

## Known gaps

- Fixed and verified on macOS only. Not yet run on Linux or Windows.
- The one-alias isolation assertions after recovery (default alias keeps its
  PID, generation, warm sentinel; scratch returns on a later generation with
  `warm_state_lost`) now execute and pass — they were unverified before this
  fix because the test died at the first poll.
- The fix clears the recovery facts when an alias enters `Starting`. It does
  **not** decide whether a worker create during automatic recovery *should*
  instead project `Bootstrapping` and keep a `Bootstrap` phase. The contract
  makes `Starting` incapable of carrying a phase, and the host re-announces
  recovery on its own lifecycle events, so clearing is correct under the
  contract as frozen; changing which state is projected would be a contract
  question, not a bug fix, and is left open deliberately.
