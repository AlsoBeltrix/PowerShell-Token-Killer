## Outcome

Describe the user-visible result and link the issue or finding it resolves.

## Evidence

List the exact checks run, platforms exercised, and red/green regression proof.
State every relevant check that was not run.

## Compatibility and data impact

Describe changes to public tools, routing, output, sessions, installation,
configuration, audit/evidence data, network destinations, or supported RIDs.
Write “none” only after checking each surface.

## Checklist

- [ ] The change is focused and does not include unrelated generated files.
- [ ] New or changed behavior has a focused test that was proved to fail without the repair.
- [ ] Exact-once execution, output recovery, audit fail-closed behavior, and containment remain intact where applicable.
- [ ] Public documentation and durable current-state records are updated where applicable.
- [ ] Logs, fixtures, screenshots, and commits contain no credentials, customer data, or sensitive evidence.
- [ ] I have listed all relevant verification not run.

