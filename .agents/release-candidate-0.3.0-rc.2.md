# Unpublished candidate verification: `0.3.0-rc.2`

## Scope and immutable source

- Owner authority: 2026-09-04 build/test go, recorded in `.agents/decisions.md`.
  Do not publish, create an explicit tag, close issues, replace the owner's
  installed payload, or stop existing PTK sessions.
- Current candidate source: `eb3f99903f76543ab58d00e0e11d20d975c16d7c`.
- Shipped product code is unchanged from verified product commit `0c9328a`;
  attempt two adds only the `rr-5` staged-install test-fixture correction and
  `.agents/` records.
- Current release workflow: [33930714689](https://github.com/AlsoBeltrix/PowerShell-Token-Killer/actions/runs/33930714689),
  dispatched 2026-09-04 at 23:46:08 UTC with version `0.3.0-rc.2`.
  The run API confirmed the exact source SHA above.
- Before dispatch, no canonical tag or release record used `v0.3.0-rc.2`.
  Existing `v0.3.0-rc.1` records were left untouched.

## Evidence status

The second run is in progress on all five native RIDs. No candidate-grade pass
is claimed yet. After the fixture correction, local verification passed:
Pester 113/3 platform skips, server 1,360/1,360 (two known analyzer warnings),
SIEM 357/357, transaction helper, staged-install handshakes, release-assembly
guards, PowerShell parse, and `git diff --check`. Windows native green proof is
still required. Attempt-two download/proof root:
`/Users/michael/ptk-rc2-final-verification.TJev9J`.

## Attempt-one failure

Original source: `c8b084fbb79c9d73965dbfc632163919c29e50dd`; original run:
[33928685705](https://github.com/AlsoBeltrix/PowerShell-Token-Killer/actions/runs/33928685705),
dispatched 2026-09-04 at 23:13:43 UTC. GitHub rejected a raw-SHA dispatch ref
without creating a run; dispatching `master` then resolved to that exact SHA.

Run `33928685705` completed with failure: both Linux jobs and macOS succeeded;
both Windows jobs built and signed both products, then failed the packaged
install fixture's ownership precondition. Draft assembly was skipped and no
`v0.3.0-rc.2` release record exists. Finding `rr-5` records the test-only repair;
a clean rebuilt candidate is required. The original source/run and Mac proof
below are attempt-one evidence, not evidence for a future rebuilt identity.

Independent six-job CI run `33929388848` passed at `36b1558`, whose product and
test tree is identical to attempt one's source (only `.agents/` records differ).
That does not excuse the distinct native candidate fixture failure.

## Attempt-one downloaded macOS ARM64 proof (supporting evidence only)

Downloaded the completed macOS job's uploaded `ptk-osx-arm64` workflow artifact
while Windows continued building. These are downloaded uploaded bytes, not the
runner's staging directory. The rebuilt candidate receives new identities and
requires its own downloaded-package proof; these results cannot be attributed
to the rebuilt draft assets.

| Product | Archive SHA-256 | Build identity |
| --- | --- | --- |
| PTK | `6d77a91c45dd48591d73e3087d5d8b5b426490be68ec60ab2a09157368021837` | `42763dd4555545948148e77ab4ad44a4` |
| SIEM receiver | `a530e625e8d0f7d06118c6a18a203f79718332f8ded905ddcdfad7d1fb56a54f` | `012d4069086146c08deb0285653121a2` |

Both archives matched their uploaded SHA-256 files and carried clean
`0.3.0-rc.2`, `osx-arm64` provenance for attempt one's source. On macOS
26.6.2 ARM64:

- Every one of the 34 Mach-O files passed `codesign --verify --strict
  --check-notarization` and reported Developer ID Application authority.
  The native workflow also recorded Apple's `Accepted` notarization result,
  submission `beb73fd3-7a95-41a6-b62e-c97a20b46052`.
- Packaged transaction proof passed both complete handshakes.
- A separate disposable `.ptk` was activated through the downloaded package's
  transaction module, with staged and installed handshakes. The installed
  direct-product proof passed all **32 checks**, including stream recovery,
  exact runtime/audit identity, the RTK refusal gate, and actual uninstall.
- The downloaded SIEM package verifier passed. All three packaged operator
  workflows passed: external-only, independent multiple destinations, and
  explicit mini-SIEM with query-back doctor. This is not real external-SIEM
  product acceptance.
- No live PTK process was stopped or replaced. The original two supervisors
  and their workers/brokers remained at the same PIDs after all local proofs.

The temporary orchestration helper initially failed before starting runtime
checks because this Mac exposes two `pwsh` command paths. Selecting the first
application path repaired that helper; all runtime checks then passed. The
shipped package and its tests were unchanged.

Verification files currently live in
`/Users/michael/ptk-rc2-verification.VIbwgF`; the disposable installed server
was removed by its successful uninstall proof. Archive and extracted copies
are retained temporarily for final draft byte comparison.

## Local isolation

Read-only process inspection found two live installed supervisors, PIDs 7123
and 7562, using `/Users/michael/.ptk/bin/PtkMcpServer`, with their workers and
brokers. No process was stopped and no installed file was replaced. GitHub
builds run on separate native runners. Local follow-up must use disposable
roots and restrict uninstall to the disposable `.ptk` being tested.

## Remaining proof

Follow `.agents/plans/release-readiness.md`'s candidate procedure: completed
five-RID gates, unpublished draft metadata and exact asset inventory,
downloaded SHA-256/digest agreement, unique PTK/SIEM provenance, native
installation/lifecycle and product/SIEM checks, and signing/notarization
evidence. Record any unperformed check explicitly; source-test success is not
a substitute for candidate evidence.
