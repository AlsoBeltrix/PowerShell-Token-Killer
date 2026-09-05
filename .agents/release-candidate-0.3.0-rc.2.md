# Unpublished candidate verification: `0.3.0-rc.2`

## Scope and immutable source

- Owner authority: 2026-09-04 build/test go, recorded in `.agents/decisions.md`.
  Do not publish, create an explicit tag, close issues, replace the owner's
  installed payload, or stop existing PTK sessions.
- Current candidate source: `a57ae57583b71bcfe6168428a64d6e05530c0147`.
- Relative to the verified baseline, the candidate includes the `rr-5` staged
  Windows fixture correction and `rr-6` Windows SIEM deployment-permission
  repair. Concurrent unrelated uncommitted harness edits are not included.
- Current release workflow: [33933424682](https://github.com/AlsoBeltrix/PowerShell-Token-Killer/actions/runs/33933424682),
  dispatched 2026-09-05 at 00:34:29 UTC with version `0.3.0-rc.2`.
  The run API confirmed the exact source SHA above.
- Before dispatch, no canonical tag or release record used `v0.3.0-rc.2`.
  Existing `v0.3.0-rc.1` records were left untouched.

## Evidence status

Attempt three is running on all five native RIDs. The new Windows lifecycle
guard failed on the original permissions (`8ff9949`, job `101214936893` in
`33932859920`), then passed with `a57ae57` in job `101215931624` of CI
`33933206344`. That Windows job passed all 357 receiver tests, exact owner/DACL
checks, extra-reader rejection, same-SID handoff/consent checks, lifecycle, and
native receiver packaging. Mac and Linux SIEM jobs also passed. Final signed
candidate and independent download checks are still required. All six jobs of
CI `33933206344` subsequently passed at exact source `a57ae57`. Attempt-three
scratch root: `/Users/michael/ptk-rc2-candidate3.2URb1u`.

## Attempt-two failure

Source `eb3f99903f76543ab58d00e0e11d20d975c16d7c`, run `33930714689`,
dispatched 2026-09-04 at 23:46:08 UTC.

The second run completed with failure. All five RIDs passed packaged
installation and core product checks; both Windows jobs then failed the
packaged SIEM workflow with `siem_receiver_configuration_invalid:
config_protection`. Draft assembly was skipped. No candidate-grade pass is
claimed yet. After the fixture correction, local verification passed:
Pester 113/3 platform skips, server 1,360/1,360 (two known analyzer warnings),
SIEM 357/357, transaction helper, staged-install handshakes, release-assembly
guards, PowerShell parse, and `git diff --check`. Native Windows evidence closes
`rr-5`; the separate SIEM permissions failure needs repair. Independent six-job
CI `33930834519` passed at `8b8f61d`, whose product/test tree matches `eb3f999`.
Attempt-two download/proof root:
`/Users/michael/ptk-rc2-final-verification.TJev9J`.

## Attempt-two rebuilt macOS ARM64 proof (supporting evidence only)

The completed Mac job in run `33930714689` uploaded artifact `9958499271`.
Its downloaded archives passed SHA-256 agreement, exact clean source/version/
RID provenance, and a fresh local macOS 26.6.2 ARM64 test pass:

| Product | Archive SHA-256 | Build identity |
| --- | --- | --- |
| PTK | `b2b1e048315a47afdf4fec6f41705b25fb8f5ee105158029bb4c03943071a584` | `f22d99caeb6d4efbb313a48c79e3b649` |
| SIEM receiver | `1d26f120fc9b94e703274764b81479895d5ff4931a795c9240a6f2da57fd2d49` | `b1961d814f5f4cfb856ac9f117b45b43` |

- All 34 Mach-O files passed strict signature verification, Developer ID
  authority checks, and forced online notarization-ticket checks. Apple's
  accepted submission is `017e8e1d-4f1f-46e8-9e06-f1306f03f6bf`.
- The corrected packaged-install test passed both handshakes. Separate actual
  activation into an isolated `.ptk` passed staged/installed handshakes and all
  32 direct-product checks, including successful removal by the packaged
  uninstaller.
- SIEM package verification and all three packaged operator workflows passed.
  Real external-SIEM acceptance is not claimed.
- Only disposable roots were modified. The owner's two supervisors and their
  workers/brokers stayed at the original PIDs.

These hashes and identities belong to attempt two, not the current candidate.
Attempt three requires its own fresh downloaded-package proof.

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
