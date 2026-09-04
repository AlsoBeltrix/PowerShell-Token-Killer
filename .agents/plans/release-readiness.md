# Plan: Global release readiness

**Status:** ACTIVATION AUDIT IN PROGRESS — owner reactivated release-readiness
work on 2026-09-04. Local repairs and verification are authorized; release
workflow dispatch, tag, publish, push, and other outward actions retain their
separate owner gates.

Activation gate 2 and readiness item 3 are implemented in the 2026-09-04
unique-build-identity slice. Every PTK and SIEM package build receives a fresh
identity recorded with full source commit, clean/dirty state, UTC build time,
and RID. Package manifests, binary informational versions, MCP initialize,
`ptk_state`, audit producer records, SIEM startup logs, and SIEM health output
carry the matching identity. The guard builds both products twice from one
commit, proves four distinct identities, and probes dirty-source detection.

Readiness item 5's immutable-asset requirement is repaired in the 2026-09-04
release-immutability slice. Draft assembly now refuses every pre-existing tag
record (draft or published), fails closed when the paginated release query
fails, and has no clobber path. See `.agents/review/findings/rr-1.md`.

Readiness item 5's public bootstrap gap is repaired by attaching one
checksum-listed `ptk-installer.zip` containing the existing cross-platform
installer and its two required modules. The README no longer claims a
single-file checkout command is a no-clone public install.
Draft assembly recalculates all eleven artifact hashes, requires one exact
manifest entry per artifact, and excludes unvalidated extra files from the
GitHub release command.

Readiness item 7's policy-independent public operations baseline now includes
factual privacy and known-limitations documentation, contribution guidance,
bug/feature/PR templates, and an immutable-version release
recovery/withdrawal procedure plus release-notes template. Security reporting
channel and support expectations remain owner decisions; this slice does not
invent either.

## Activation audit — 2026-09-04

| Readiness item | Status | Current evidence / remaining gate |
| --- | --- | --- |
| 1. Freeze candidate contract | **OPEN** | The stream-retention contract is implemented locally. Version, security-reporting channel, and support expectations remain unset. |
| 2. Reconcile product backlog | **COMPLETE technically** | Live canonical GitHub query found only issue #30. Its remoting acceptance passed; `i30-1` is fixed on `origin/master` with red/green, packaged proof, and exact-head six-job CI run `33924847924`. Administrative issue closure remains a separate outward action. |
| 3. Build identity and provenance | **COMPLETE locally** | `4c636fe` plus `ec3034b`: four-build uniqueness/dirty detection and clean PTK/SIEM package provenance passed. |
| 4. Settle release policy | **PARTIAL** | Apache-2.0, five RIDs, fetch-on-install, signing, immutable assets, and factual privacy posture are settled or implemented. Security reporting, support, version, and final publication remain owner gates. |
| 5. Build distribution path | **COMPLETE locally** | Transactional installer/package path plus `fa3d476` immutable release assembly and `be05b29` checksum-verified standalone bootstrap. |
| 6. Validate exact candidate | **NOT YET POSSIBLE** | Requires the frozen clean commit, canonical push, owner-authorized five-RID workflow run, downloaded signing/notarization/hash checks, and exact-version install/lifecycle evidence. Prior local/hosted runs are supporting evidence, not the final candidate proof. |
| 7. Prepare public operations | **PARTIAL** | `c55169f` adds privacy, limitations, contribution/community templates, release notes, and withdrawal recovery. Security reporting and support boundary await owner policy. |
| 8. Final owner gate | **NOT STARTED** | Requires one exact candidate recommendation after items 1–7 close; tag and publish remain separately authorized actions. |

Live GitHub has both a published prerelease and a stale draft named
`v0.3.0-rc.1`. Release immutability forbids repairing or reusing that version.
The next candidate must use a new version; the standing recommendation is
`0.3.0-rc.2`, subject to the owner's version ruling. Canonical `origin/master`
contains product commit `0c9328a`; exact-product-tree CI run `33924847924`
passed all six jobs at `72ccd90` before docs-only record updates. Any later
candidate product change must follow the repository's push policy and be
revalidated.

### Outstanding owner rulings (recommendations, not adopted)

1. **Version.** Use `0.3.0-rc.2`. Both a published prerelease and a stale draft
   already use `v0.3.0-rc.1`, so immutable-version policy rules out reuse. A
   different choice must be another unused SemVer version.
2. **Security reporting.** Enable GitHub private vulnerability reporting and
   name it as the confidential channel in `SECURITY.md`; keep public issues for
   non-sensitive bugs only. The live setting is disabled, and the current
   credential has push/triage but not admin permission, so an owner/admin must
   enable it. If rejected, the owner must name another privately monitored
   channel before `SECURITY.md` can truthfully ship.
3. **Support.** Promise best-effort support through GitHub issues with no
   response or resolution SLA, and route vulnerabilities to the private
   security channel. A stronger SLA requires the owner to specify coverage and
   resourcing before `SUPPORT.md` can promise it.

Canonical GitHub's community-profile API reports 71% after the public-operations
baseline reached `master`; `SECURITY.md`, `SUPPORT.md`, and the disabled private
reporting setting are the policy-dependent remaining public-operations gaps.

The exact-RID workflow now also requires packaged activation proof after
platform signing: `server/test-staged-install.ps1` runs complete handshakes
before and after the package's own transaction module activates into a
disposable home (`rr-3`). This strengthens item 6 but does not replace the
final candidate's public installer, upgrade/refusal, or uninstall evidence.

The public bootstrap now follows the installer's published-release selection
contract instead of GitHub's stable-only `/releases/latest/download` alias. It
selects the newest published stable or prerelease, fetches the bundle and
manifest from that exact tag, and pins the installer payload version (`rr-4`).
The follow-up executes the README block against GitHub's non-enumerated
REST-array shape and a real checksum-verified fixture; that proof caught and
removed a nested-array wrapper after `d40228c`.

Clean local product head `0c9328a` has now passed the complete macOS battery
plus a fresh provisional `osx-arm64` layout, packaged activation, isolated
public source install, 32-check installed-product proof, and uninstall,
including the repaired information/verbose stream contract. This closes
the remaining local evidence gap but remains supporting evidence for item 6:
the final versioned candidate still requires its canonical five-RID run and
downloaded-artifact verification.

## Purpose

Provide a cold-agent checklist for deciding whether PTK is ready for a global
public release without treating release work as the current product priority.
Until activation, current product defects and the live GitHub issue backlog take
precedence.

## `i30-1` stream-capture repair

The owner's 2026-09-04 direction to continue through release readiness clears
the local repair gate for the confirmed silent-loss defect. The release-safe
contract is:

- capture `Write-Host`/information and verbose records for every completed or
  interrupted PowerShell invocation while continuing to drop progress records;
- render captured records in labeled `[information]` and `[verbose]` response
  sections and retain the same labeled bytes in the immutable `ptk_output`
  artifact;
- keep capture bounded by the existing passive/artifact limits and never call
  user-defined formatting, getters, or `ToString()` merely to retain a stream
  record; an unsafe information payload receives an explicit omission marker;
- carry both streams through the worker/supervisor artifact protocol without
  weakening exact-once dispatch, timeout recovery, or backward decoding; and
- prove the defect red before repair, then cover direct host capture, response
  shaping, immutable recovery, worker transport, and packaged direct-product
  behavior before marking `i30-1` fixed locally. The repair reached canonical
  CI in successful exact-head run `33924847924`; close GitHub issue #30 only
  under its separate outward gate.

## Activation gate

This plan activates only after an explicit owner instruction and all of the
following evidence exists:

1. The live GitHub backlog has been freshly triaged. Correctness, data-integrity,
   unrecoverable-execution, installation, and supported-platform blockers have
   an implemented and verified disposition; do not freeze an issue count here.
2. Every build receives a new user-visible build identity. Rebuilding the same
   commit cannot report the same version. Runtime state, package manifest,
   installed metadata, logs, and diagnostics expose enough identity to tell
   exactly what runs on each host.
3. The intended runtime and installer behavior has a current, passing automated
   baseline on the supported development hosts.
4. No active product plan or decision says the runtime topology, public tool
   contract, installer transaction, or supported platform set is still being
   replaced.

Failure of any gate leaves this plan parked.

## Readiness work after activation

### 1. Freeze the candidate contract

- identify the exact candidate commit and immutable build identity;
- freeze supported operating systems, architectures, clients, PowerShell
  versions, public tools, configuration, upgrade boundary, and uninstall
  behavior;
- identify explicitly unsupported environments and workflows.

### 2. Reconcile the product backlog

- query GitHub at activation time rather than trusting a copied issue list;
- classify every open issue as release-blocking, scheduled follow-up,
  documented limitation, external dependency, duplicate, or invalid;
- require direct guard proof for every release-blocking fix;
- update stale issues that describe discarded architectures before relying on
  their acceptance criteria.

### 3. Prove build identity and provenance

- verify one build always advances the visible build identity, including
  repeated builds of one commit and dirty local builds;
- make the identity consistent across binaries, manifests, installed metadata,
  runtime state, diagnostic output, checksums, and release assets;
- record source commit, build identity, target RID, build time, and dirty/clean
  provenance without embedding secrets or machine-specific paths;
- prove two independently produced builds cannot be mistaken for one another.

### 4. Settle release policy one decision at a time

Only after activation, present owner decisions individually for licensing,
security reporting, public installer defaults, artifact signing, support
expectations, telemetry/privacy posture, version number, release channels, and
publication. Silence authorizes none of them.

### 5. Build the distribution path

- produce public installers and uninstall behavior from immutable release
  assets rather than a repository checkout;
- generate checksums and any approved signatures;
- make install, upgrade, repair, and uninstall transactional and diagnosable;
- prevent development-only registration or hook behavior from leaking into the
  public path;
- generate a release workflow only after the candidate contract is frozen.

### 6. Validate the exact candidate

- run the complete repository verification battery on the exact candidate;
- build and execute each supported RID on matching hardware;
- test clean install, same-version handling, upgrade, stopped-process refusal,
  uninstall, client registration, first invoke, restart boundary, and recovery;
- test Windows security-product handling and any approved signing path;
- verify documentation from a clean unaffiliated-user environment;
- run a bounded soak representative of long, high-output, stateful work.

### 7. Prepare public operations

- publish install, upgrade, uninstall, troubleshooting, security-reporting,
  support-boundary, privacy, and known-limitations documentation;
- prepare release notes and a rollback/withdrawal procedure;
- define how a bad artifact is revoked and how users identify an affected build;
- verify repository metadata and community files match the settled policy.

### 8. Final owner gate

Present one bottom-line release recommendation tied to the exact commit, build
identity, artifact hashes, completed validation matrix, open limitations, and
rollback procedure. Tagging, pushing release refs, publishing artifacts, package
index submission, and public announcement each require explicit authority under
the repository push/outward-action policy.

## Retained non-goals

- no release date before exact-candidate validation;
- no claim that the product is release-ready before the final evidence gate;
- no tag, publication, package submission, or announcement without its
  explicit outward-action authority;
- no speculative product changes merely to make the repository look
  release-shaped.
