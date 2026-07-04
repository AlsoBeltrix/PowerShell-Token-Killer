# Plan: Fix the verified findings from the 2026-07-03 external review

**Status:** DRAFT — awaiting owner approval. No code change until approved.
**Decision basis:** An external (GPT-5.5) review of the Phase 2 build surfaced five
findings; a 2026-07-03 verification session confirmed all five against the code,
with live repros for the two module findings. Four are unrecorded defects in
already-shipped, owner-approved work (warm-runspace server slices 1-6 and Phase 2
slices 0-3); the fifth is the known repo-root-sensitive Pester fixture already
sitting in `state.md`'s AWAITING OWNER GO list as item (b) — folded in here so one
go covers the set. This plan fixes defects in shipped scope only; it does not
touch anything paused behind the go/no-go (universal wrapper, destructive-cmdlet
gate).

## Goal

The server and module report what actually happened, and compression survives the
inputs it currently mishandles:

- A failed native command inside `ptk_invoke` is visible (`[exit] N`), matching
  the CLI path's existing behavior.
- A caller-canceled call is reported as canceled — not as a server timeout — and
  does not destroy warm state (the whole point of the server) when the pipeline
  stops cleanly.
- Mixed-type pipelines (e.g. FileInfo + string) compress instead of falling back
  to labeled raw output.
- `-MaxItems` caps property-less rows (scalars) like every other shape.
- The Pester suite is clean on any checkout (no repo-root-sensitive fixtures).

## Slices

One finding per slice, one commit per slice (Git Safety). Order: module fixes
first (small, independently verifiable), then server, then fixture.

1. **Module: `-MaxItems` for property-less rows** (review finding 4, Low).
   `Format-PtcTable`'s no-properties early return (psm1:166) bypasses both the
   cap and the overflow line. Apply `Select-Object -First $MaxItems` and append
   `'+N more'` in that branch, mirroring the table path.
   *Verify:* new Pester case `1..5 | Compress-PtcObject -MaxItems 2` → exactly 2
   values + `+3 more`; guard proof (revert fix → test fails); full suite.

2. **Module: mixed-type dispatch** (finding 2, Medium). The dispatch in
   `Compress-PtcObject` (psm1:737-756) matches type names across *any* item but
   property-checks only the *first*, so `(Get-Item README.md), 'done'` reaches
   `Compress-PtcFileSystem` and throws `PropertyNotFoundException` under strict
   mode (confirmed by repro; `Compress-PtcOutput` catches it but degrades to
   labeled raw, losing compression). Fix: a specialized route fires only when
   **every** item passes its property check (`Test-PtcHasProperty` over the
   stream, short-circuit on first failure); heterogeneous streams fall through to
   `Compress-PtcGenericObject`, whose property access is already null-safe.
   Applies to all four specialized routes (fs, MatchInfo, Process, Service), not
   just fs — same latent bug in each.
   *Verify:* new Pester cases: FileInfo+string mixed stream → no throw, generic
   compression, and `Compress-PtcOutput` emits no `[ptk:shape ERROR]`; a
   homogeneous fs stream still routes to the fs compressor (regression). Guard
   proof; full suite.

3. **Server: surface native exit codes** (finding 1, High). Nothing in the server
   reads `$LASTEXITCODE`, so `cmd /c exit 7` returns Success=true, no output, no
   errors. Mirror the CLI's solved pattern (`Invoke-PtcRun`, psm1:1035-1040,
   including its stale-guard — see `docs/history/decisions-archive.md`): in
   `InvokeAsync`, reset `$global:LASTEXITCODE = 0` before the user script and
   read it back after completion (both via small pipelines against the same
   runspace, inside the gate). Add `int? ExitCode` to `InvokeResult`; `Program.cs`
   renders a `[exit] N` block alongside `[errors]` when it is nonzero. `Success`
   semantics stay as documented (false only on terminating error/timeout) — the
   fix is visibility, not a redefinition.
   *Verify:* new dotnet test cases: native nonzero exit → `[exit] N` present;
   pure-PowerShell script after a failed call → no stale `[exit]` (the archived
   CLI bug, re-proven here); zero exit → no block. Guard proof; full dotnet
   suite + handshake script.

4. **Server: cancellation ≠ timeout** (finding 3, Medium; worst in practice —
   in Claude Code the caller token fires when the user hits Esc, so aborting one
   slow call currently destroys all warm state and blames a timeout).
   `InvokeAsync` passes the caller token to `Task.Delay(_callTimeout, token)`
   (RunspaceHost.cs:132); a canceled token completes the delay and takes the
   recycle path. Fix: separate the two signals. On real timeout: unchanged
   (recycle, TimedOut=true). On caller cancel: `ps.Stop()` with a short grace
   period; if the pipeline stops cleanly, **keep the runspace** (warm state
   survives) and return a result that says canceled (Errors text; TimedOut
   stays false); if Stop exceeds the grace, fall back to the existing
   abandon-and-recycle (a truly wedged pipeline still cannot hold the gate).
   *Verify:* new dotnet test: set a variable, start a slow call with a token
   canceled at ~100ms and a long timeout → result reports canceled, not
   TimedOut; the variable is still readable on the next call (warm state
   survived). Guard proof; full dotnet suite + handshake.

5. **Tests: deterministic fixture for the two repo-root-sensitive Pester tests**
   (finding 5 = AWAITING GO item (b)). The two tests assert README.md appears
   within `-MaxItems 10` of `Get-ChildItem <repo root>`, which breaks on any
   checkout whose root has >10 entries sorting before it. Replace the live repo
   root with a temp directory built in `BeforeAll` (known names, known count,
   including a real file to assert on), removed in `AfterAll`.
   *Verify:* full Pester suite passes clean (target: 40/40, zero known
   failures) on this box, where the old fixture currently fails.

6. **End-to-end + docs.** Full verification battery (Pester, `dotnet test`,
   handshake both variants) plus a live `ptk_invoke` spot-check of the two
   server changes (a failing native command shows `[exit]`; an Esc-aborted call
   leaves warm state intact). Update `state.md` (drop AWAITING-GO item (b),
   record outcomes); note the review disposition in the continuation decision
   entry if the owner wants it on record.
   *Verify:* all suites green; live checks observed; docs updated.

## Risks

- **Slice 3:** the exit-code read adds two tiny pipeline invocations per call on
  the warm runspace (~sub-ms each, measured pattern from the module's warm
  re-import numbers); acceptable against a ~3 ms round-trip, but re-measure in
  slice 6.
- **Slice 4:** `ps.Stop()` can block on a hostile pipeline — that is what the
  grace-then-recycle fallback is for; the deterministic test covers the clean
  path, the wedged path keeps the existing recycle behavior. Also confirm how
  the MCP SDK surfaces a canceled call to the client (result vs. exception) and
  match its convention rather than fight it.
- **Slice 2:** the all-items property check walks the full stream in the worst
  case (homogeneous large streams short-circuit nothing); it is one hashtable
  lookup per item and only on specialized-route candidates. If measurement in
  slice 6 shows real cost on `Get-ChildItem -Recurse`-sized streams, cap the
  check at a sample + defensive access in the compressors instead — decide on
  evidence, not now.
- **Slices 3+4** touch `InvokeAsync`, the hot path every live session exercises;
  the handshake script and live spot-checks in slice 6 are the backstop against
  a regression that unit tests miss.

## Non-goals

- No redefinition of `Success` (stays terminating-error-or-timeout, as the
  `InvokeResult` doc comment records).
- No universal wrapper, no destructive-cmdlet gate, no new tools — still paused
  behind the go/no-go.
- No change to the go/no-go criteria or timeline (~2026-07-20); these are
  defect fixes on shipped scope so the test measures the product working as
  designed.
- No change to `Compress-PtcObject`'s string[] sub-decision (still tied to the
  paused universal-wrapper entry).
