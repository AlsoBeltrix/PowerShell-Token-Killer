# Unpublished candidate verification: `0.3.0-rc.2`

## Scope and immutable source

- Owner authority: 2026-09-04 build/test go, recorded in `.agents/decisions.md`.
  Do not publish, create an explicit tag, close issues, replace the owner's
  installed payload, or stop existing PTK sessions.
- Current candidate source: `6ab7040bde61adcf6722830dbb5807ac295ec166`.
- Relative to the verified baseline, the candidate includes the `rr-5` staged
  Windows fixture correction and `rr-6` Windows SIEM deployment-permission
  repair. Concurrent unrelated uncommitted harness edits are not included.
- Current release workflow: [33935468032](https://github.com/AlsoBeltrix/PowerShell-Token-Killer/actions/runs/33935468032),
  dispatched 2026-09-05 01:12:46 UTC with version `0.3.0-rc.2`. The run API
  confirmed the exact source SHA above.
- Before dispatch, no canonical tag or release record used `v0.3.0-rc.2`.
  Existing `v0.3.0-rc.1` records were left untouched.

## Final result: draft built; portable-checksum gate failed

Run `33935468032` passed all five native jobs and draft job `101226565157`
at clean source `6ab7040`, completing 2026-09-05 01:42:03 UTC. Both Windows
jobs passed private-ACL lifecycle, signing, staged installation, all 32
product/Defender checks, and all three packaged SIEM workflows.

Draft `383097364` has exact title `ptk 0.3.0-rc.2`, tag name
`v0.3.0-rc.2`, target source SHA above, `draft=true`, and
`published_at=null`. No remote tag ref was created. All twelve assets were
downloaded fresh and verified: eleven unique archive/installer entries, every
available GitHub digest (all twelve), safe extraction paths, exact installer
bundle contents, ten distinct clean source/version/RID/build-time identities,
and binary/provenance version agreement. Final Mac archives are byte-identical
to the workflow downloads that passed local runtime/signature checks.

The canonical exact inventory is
[release-candidate-0.3.0-rc.2.assets.json](release-candidate-0.3.0-rc.2.assets.json).
Verification root: `/Users/michael/ptk-rc2-candidate4.hOfcTm`; detached
`source` is clean at the exact candidate commit.

**Do not publish this candidate:** standard
`shasum -a 256 -c SHA256SUMS` fails on the downloaded manifest. Four Windows
entries retain CRLF and the installer entry has one separator space. `rr-8`
records a canonical-output repair for future assemblies; it does not alter this
draft. The bytes themselves match their hashes. A new version/run requires an
owner go; rc.3 with the newer harness fix is a recommendation, not a decision.

The disposable verifier initially assumed exactly two spaces, then preserved
the supported one/two-space input while retaining exact-name/count/hash checks.
It also initially let PowerShell turn JSON UTC strings into DateTime objects,
which a second parse interpreted with a local offset. Reading provenance with
`ConvertFrom-Json -DateKind String` fixed that helper-only failure. The partial
extraction was moved aside before a fresh extraction; all integrity/provenance
checks then exited zero. Neither helper correction excuses the separately
confirmed standard-consumer failure.

### Attempt-four local clean-source and downloaded Mac proof

All six ordinary CI jobs in `33935459185` passed exact source `6ab7040`:
the PTK and SIEM jobs on Linux, macOS, and Windows.

At clean source `6ab7040`, local Pester passed 113 tests with 3 platform skips;
the server suite passed 1,360/1,360 and the SIEM suite exited zero. Registration
handshake, mini-SIEM lifecycle, four-build uniqueness/dirty-source guard,
release-assembly regression, release-selection and signing-documentation
helpers, ShellCheck, and actionlint passed. Both dependency scans reported no
vulnerable packages. The detached verification worktree remained clean.

The fresh Mac download from `33935468032` passed both archive checksums,
exact clean provenance, all 34 Mach-O strict signatures/Developer ID checks and
forced online notarization-ticket checks. Apple accepted submission
`36773eae-cacf-44da-9031-69da7f35c062`.

Current Mac archive hashes and build identities are recorded once in the
linked machine-readable inventory.

Staged activation and both handshakes passed. Separate actual installation
into a disposable `.ptk` passed all 32 direct-product checks, including
packaged uninstall. SIEM package verification and all three operator workflows
passed; the orchestration exited zero. This is not real external-SIEM
acceptance. Original installed supervisors 7123 and 7562 remained running.
Final draft downloads matched these tested Mac archives byte-for-byte.

Concurrent live-install observation, 2026-09-05 around 01:30 UTC: the original
supervisor PIDs are no longer running. Current supervisors include 89113 and
90634; the installed provenance now identifies `0.3.0-dev.geff24a5`, source
`eff24a51c1776c1ee29a6cf270ac04d379c8e57a`, dirty-source flag true, build
`00de667392f345df8edd16b39dba687d`, built 01:21:26 UTC. This was not performed
by the candidate build/test work. Do not claim the old live processes remained
unchanged through final handoff, and do not install this older frozen candidate
over the newer harness change without an explicit owner ruling. All candidate
installation/uninstall actions remained confined to the disposable proof home.

## Attempt-three result: native success; draft assembly failed

Run `33933424682` at `a57ae57` completed with all five native jobs successful.
Both Windows jobs passed signing, staged-install activation, all 32
product/Defender checks, and all three packaged SIEM workflows. This is exact
signed-package green evidence for `rr-6`, now closed. All six ordinary CI jobs
in `33933206344` also passed this source.

Draft job `101221277126` failed before GitHub mutation because its checksum
parser retained the carriage return in native Windows CRLF checksum files.
Downloaded win-x64 checksum bytes confirm `0D-0A` line endings. Finding
`rr-7` records the narrowly scoped repair and red/green guard. No
`v0.3.0-rc.2` draft or tag was created. A fresh canonical workflow and fresh
downloaded-package identities are required before candidate completion.

Attempt-three scratch root: `/Users/michael/ptk-rc2-candidate3.2URb1u`. The
Mac proof below belongs only to attempt three.

## Attempt-three downloaded macOS ARM64 proof

The downloaded `ptk-osx-arm64` workflow artifact from run `33933424682`
passed fresh local verification on macOS ARM64. These results identify the
current candidate, not either earlier attempt.

| Product | Archive SHA-256 | Build identity |
| --- | --- | --- |
| PTK | `e8bb6d67f6d87e9ee24bcfd5c1b52fea0bad2b5383c3e44af821c627f85d3a8c` | `190a2a039597449ab3f60a6a44bcc256` |
| SIEM receiver | `8d0a8bb8af4aaaf74cd07f0ea20af7ceabba41fb7585c595316b67157e7ddcb1` | `546950b7771442cb85d29a00627f338a` |

- Both archive checksums and clean source/version/RID provenance matched.
- All 34 Mach-O strict signature, Developer ID authority, and forced online
  notarization-ticket checks passed. Apple's accepted submission:
  `1924a6a3-b40d-4550-94f8-20ae860e7cc5`.
- Packaged staged-install proof passed both handshakes. Separate activation
  into a disposable `.ptk` passed staged/installed handshakes and all
  32 direct-product checks, including actual packaged uninstall.
- SIEM package verification and all three packaged operator workflows passed.
  This is not real external-SIEM product acceptance.
- Local orchestration exited zero. The two original live supervisors
  (PIDs 7123 and 7562) remained running; their installation was untouched.
- Final draft archives still require byte comparison with these tested bytes.

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

## Remaining proof and authority

- `rr-8`: a separately authorized new candidate must prove the repaired
  uploaded manifest with both byte verification and standard checksum tools.
- Downloaded Windows/Linux archives were integrity/provenance-checked here,
  but were not installed/uninstalled on matching native hardware. Their
  packaged activation, runtime, and SIEM workflow checks ran on native CI
  runners before upload; that is not the same evidence.
- Independent downloaded Windows Authenticode checks, full candidate-grade
  public-installer/upgrade/refusal lifecycle across every RID, real
  cross-account service handoff, and real external-SIEM acceptance remain
  unperformed. Failed SSH probes to recorded Windows hosts did not modify them.
- Public security/support policy, final release notes, publication, and live
  unauthenticated bootstrap smoke retain their explicit gates.
- The separate `eff24a5` harness fix is newer than the frozen candidate and
  already appears in the live installation. Include it in any recommended
  replacement candidate; do not silently downgrade the installed copy.
- No draft was deleted, no uploaded asset replaced, and nothing was published.
  Scratch roots and downloaded evidence remain available; the only uninstalls
  in this task targeted disposable proof homes.
