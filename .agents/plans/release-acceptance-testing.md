# Plan: v0.2.0 release acceptance testing

**Status:** APPROVED 2026-08-04 (owner: "I want to have some agents run it
through some tests ... have the agents add a github issue with the results").
This is the explicit approval the router and packaging plans require before
any unattended review or agent-driven validation runs.

Scope: find defects in the `v0.2.0` candidate before it is tagged. This plan
authorizes **testing and reporting only**. It does not authorize product
changes, a tag, or a publication. A defect found here is written up, not
silently patched — the owner decides what blocks the release.

## Why this exists

Every slice of `.agents/plans/github-release-packaging.md` is executed and
the candidate passes its own gates. But the gates that ran are narrower than
the product's claims, and the gaps are concentrated in exactly the code a
first-time user hits first. This plan enumerates what is genuinely unproved
and assigns each item to an agent.

## Known-unproved inventory

Written as claims a test must falsify or confirm. Anything already proved is
excluded — see the packaging plan's status table for what is proved and how.

### A. Install has never run end to end

The single largest gap. `install.ps1` and `install.sh` were verified for
**uninstall** behavior against synthetic payloads, and for argument handling
and syntax. **No install has ever executed against a real release asset on
any platform.**

- A1. Fresh install from the draft release completes on each of the five
  RIDs and leaves a server that starts.
- A2. The checksum verification actually rejects a corrupted asset. Never
  exercised in the failing direction on either script.
- A3. The rtk fetch-on-install path (`Resolve-PtkRtk` / `ensure_rtk`) works.
  It has never downloaded anything: every local run found rtk already on
  PATH and took the early-return branch.
- A4. Installing **over an existing install** (upgrade) preserves user files
  and replaces the payload. Never tested in either direction.
- A5. The snapshot/restore path restores the prior payload when activation
  fails part-way. Never exercised — inject a failure after payload copy and
  before registration.
- A6. `install.sh` runs under a real POSIX `sh` (dash), not just bash.
  Syntax was checked with `sh -n`; execution was via Git-for-Windows bash.
- A7. Uninstall after a **real** install (not a synthetic payload) leaves no
  payload, no registration, and no ARP entry.

### B. Direct product proof covered one RID of five

`server/direct-product-proof.ps1` ran 16/16 against the installed win-x64
candidate. `linux-x64`, `linux-arm64`, `osx-arm64`, and `win-arm64` received
only the stdio handshake and the RTK startup gate in CI.

- B1. The 16 checks pass on each remaining RID, against an installed
  candidate rather than a checkout.
- B2. `win-arm64` specifically: the whole product path under x64 rtk
  emulation, not just the `hook check` probe.

### C. `opr-14`'s guards are not mutation-proved

The repair is sound and the guards pass on Linux and macOS arm64. But the
defect was an **ABI mismatch that only manifests on Apple arm64**, and the
guards were never shown to fail against the pre-repair code on that
platform. A guard that passes both before and after is vacuous.

- C1. Restore the fixed-signature `Fcntl(int, int, int)` F_SETFD call on a
  branch and run `UnixCloseOnExecTests` on `macos-latest`. Either the guard
  fails (repair proved) or it passes (guard is vacuous and must be replaced
  with one that observes descriptor inheritance across `exec`).
- C2. If C1 shows the guard is vacuous, the stronger test is the one
  `opr-14` originally asked for: prove a command child cannot observe the
  worker's duplicated protocol descriptors.

### D. Slice 7.0's trusted-type rendering is new and broad

The fallback now calls `ToString()` on any type from the framework directory
or the two PowerShell assemblies. That is a much larger surface than the six
types it replaced.

- D1. A framework type whose `ToString()` is expensive or blocking does not
  stall capture. The rendering happens on the producer callback.
- D2. A framework type whose `ToString()` has observable side effects. Find
  one if it exists; the safety claim is "host code, not user code", which is
  weaker than "no side effects".
- D3. A user type that **subclasses** a trusted framework type — does it
  inherit trust it should not? The trust test reads
  `value.GetType().Assembly`, so a subclass in a dynamic assembly should be
  rejected. Confirm, do not assume.
- D4. A trusted generic type parameterized over a user type
  (`List<UserType>`, `Nullable<UserStruct>`): whose `ToString()` runs?
- D5. Rendering under the projection budget: many large renderings in one
  invocation must not blow the bound.
- D6. The `passive_projection_lossy` marker appears rather than
  `active_member_not_evaluated`, and recovery via `ptk_output` still yields
  the full value.

### E. Concurrency and load

The product claims eight named sessions with isolated warm state and
serialized calls per session.

- E1. Eight sessions open simultaneously; the ninth is refused cleanly.
- E2. Concurrent invocations across different sessions genuinely run in
  parallel and do not cross-contaminate warm state.
- E3. Serialized calls within one session: a queued call whose budget
  expires while waiting fails fast without executing.
- E4. Repeated timeout/recovery cycles on one session — does it recover
  every time, or degrade?
- E5. Large output under concurrency: several sessions each producing
  bounded-but-large output, with `ptk_output` recovery on each.
- E6. Worker crash (kill the worker process directly) followed by a normal
  invocation on that session.

### F. Routing edges

Routing authority is RTK's, and PTK binds the rewrite. The binding rules are
guarded by unit tests; the live path has less coverage.

- F1. A session-defined `function git` must not be wrapped by an RTK rewrite
  (`TryBindRewrite` requires an `Application` binding). Prove live.
- F2. Quoted arguments containing spaces survive a rewrite byte-exactly.
- F3. A command RTK declines executes unchanged with correct exit code and
  stderr.
- F4. `route=pwsh` never routes, `route=rtk` on an ineligible shape reports
  the labeled fallback without retrying.

### G. Platform findings deferred as MEDIUM/LOW

Eleven findings stay deferred in `.agents/review/dispositions.md`. They were
judged not to meet the release-blocking rule. That judgement was made on
paper, not by attempting to trigger them.

- G1. Pick the Unix containment items (`opr-24` through `opr-31`, `opr-46`)
  and attempt to reproduce each on Linux and macOS. Report which are real
  and whether any actually meets the release-blocking rule.

### H. Security posture of the packaged artifact

- H1. Defender scanned clean on one Windows host. Re-scan the exact
  published assets, and check SmartScreen behavior on a browser-downloaded
  archive (the README claims the one-line paths are the tested route).
- H2. macOS Gatekeeper: the artifact is ad-hoc signed, not notarized.
  Confirm what a user actually sees when installing via `install.sh` and
  whether the documented path works without a right-click override.
- H3. The installer refuses to run elevated. Confirm on each platform.

## What a finding looks like

One GitHub issue for the whole run, so the owner reads one thing. Inside it,
per finding:

- the claim tested and the exact command run;
- observed vs expected, with the actual output;
- platform and RID;
- whether it meets the release-blocking rule in
  `.agents/plans/minimum-viable-release.md` §"Release-blocking rule" — and
  if the agent is unsure, say unsure rather than guessing;
- no proposed patch. This plan does not authorize product changes.

Report confirmations too. "A3 exercised, rtk downloaded and verified" is a
result; silence is not.

## Non-goals

- No product changes, no commits to `master` beyond this plan and test
  assets.
- No tag, no publication, no release edit.
- No new features, no refactors, no "while I was in there" fixes.
- Do not repair a deferred platform finding; report it.
