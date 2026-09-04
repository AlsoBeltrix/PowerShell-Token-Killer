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

## Purpose

Provide a cold-agent checklist for deciding whether PTK is ready for a global
public release without treating release work as the current product priority.
Until activation, current product defects and the live GitHub issue backlog take
precedence.

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
