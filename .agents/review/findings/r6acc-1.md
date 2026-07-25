# r6acc-1: `ptk_state` fails on the real apphost while an alias is recovering

**Severity**: HIGH — it blocks R6's acceptance matrix line "One worker crash
affects only one alias" (`.agents/plans/mcp-resilience.md`). If it is a product
defect rather than a test defect, a model loses its only state probe for the
whole recovery window, which is precisely when it needs it.
**Status**: Open — reproduced deterministically on macOS, cause not yet
identified
**Branch**: `feature/mcp-resilience-r1`
**Commit**: none. The reproducer is the patch below; it is deliberately NOT in
the suite, because it is red and would block the macOS battery.

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

Apply to `server/PtkMcpGuardian.Tests/ProductionGuardianCompositionTests.cs`
and run
`Composition_isolates_one_alias_worker_crash_from_a_second_alias`. It is
cross-platform (Windows launcher or compiled Unix broker) and fails on macOS in
about 4 seconds. The saved patch is `r6acc-1-repro.patch` in this directory.

## Guard proof

Not applicable yet; the failing test is itself the reproducer, and it is not in
the suite.

## Coder dispute

None.

## Known gaps

- Only reproduced on macOS. Not yet run on Linux, and Windows is already
  blocked behind `r6x-2`/`r6x-3`.
- The one-alias isolation assertions after recovery (default alias keeps its
  PID, generation, warm sentinel; scratch returns on a later generation with
  `warm_state_lost`) have never executed, because the test dies at the first
  poll. They are unverified, not passing.
