# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- **Security question OPEN; owner-led consultation in progress.** Read the
  STOP section of `.agents/plans/security-layer.md` before any security
  work. The declarative policy-file gate and secret redaction are rejected;
  the former implementation slices are prior art only. The owner's
  architecture-review language is outreach framing, not a settled verdict.
  MCP elicitation remains an unverified candidate: fact-finding is allowed,
  but no security shape, plan, or code is authorized.
- **RTK rewrite routing is a reviewed DRAFT, not approved.**
  `.agents/plans/rtk-rewrite-routing.md` has a closed converged review loop,
  but no code was written. Approval must resolve the plan's explicit owner
  calls and ratify or reopen rrp-15, whose name-keyed-hook divergence was
  closed by coder disposition rather than reviewer grade.
- **`.agents/decisions.md` is UNDER HOLD** (owner, 2026-07-10: do not
  update it until the discussion is complete). The security reframe and RTK
  routing direction still need durable entries after the owner releases the
  hold.
- **Release distribution remains approved work.** Slices 0-2 are landed;
  slice 3 is queued and `.github/workflows/release.yml` is still absent.
  The deliberately open hook-default choice blocks slice 4 only.
- **Standing GitHub authority:** the owner granted persistent permission on
  2026-07-10 to comment, close, and triage issues in this repository as
  appropriate without per-action asks. Pushes remain ask-first.

## Next

1. Fact-find MCP elicitation only: protocol status, support in Claude Code,
   Codex, Grok, and Antigravity, and headless failure behavior. Report the
   evidence; do not draft a security shape or plan.
2. Owner approval or rejection of RTK rewrite routing, including the rrp-15
   disposition and the other approval calls recorded in the plan. No code
   before approval.
3. Execute release-distribution slice 3 under its approved plan. Re-present
   the hook-default choice before slice 4.
4. When the owner releases the decisions hold, reconcile the rejected
   security mechanism and RTK routing direction in `.agents/decisions.md`.
5. Owner push go for this committed docs-only drift slice; push policy is
   ask-first.

## Open / Parked

- Warm-backend slice 7 is unblocked open work, currently unscheduled, and
  remains owner-run Windows validation: AD native import/warm reuse; Exchange
  implicit remoting with first-vs-repeated `Get-Queue` latency; EXO/Graph
  unattended certificate auth. Its plan status still needs correction; see
  `## Blockers`.
- The persistent/shared-runspace idea remains unapproved behind both its
  measured-pain criterion and an explicit owner build go. Its security and
  control shape must also be settled before any build.
- GitHub issue #3 remains open (verified 2026-07-11): item 1 landed; items
  2-4 are an unplanned follow-up candidate, while its permission-bypass
  concern belongs to the open security track.

## Blockers

- **Decision-log conflict, correction blocked by the owner hold:**
  `.agents/decisions.md` still describes the policy-file gate as the open
  response after its criterion fires, while the later explicit owner call in
  `.agents/plans/security-layer.md` rejects that response. Its shared-host
  entry also still cites the already-decided product go/no-go as a gate. Do
  not implement the policy gate; preserve both conflicts until the hold is
  released.
- **Plan-record drift, reported but not edited in this narrow state pass:**
  the warm-runspace plan still says slice 7 is paused behind the already
  decided GO; the release plan retains stale CI/push/policy references; and
  the shared-runspace idea still assumes the rejected policy gate. Explicit
  owner calls, uncontested decisions, and live repo evidence named above
  control.

## Verification

- Automated verification entry point: `.agents/repo-guidance.md`
  (Verification). Review-loop evidence lives in `.agents/review/index.md`;
  do not duplicate volatile counts here.
- This drift slice is docs-only. Its live evidence was rechecked on
  2026-07-11; machine-specific results live in `.agents/machines.md`.

## Active Sources

- `AGENTS.md`
- `.agents/repo-guidance.md`
- `.agents/decisions.md`
- `.agents/plans/security-layer.md`
- `.agents/plans/rtk-rewrite-routing.md`
- `.agents/plans/release-distribution.md`
- `.agents/plans/warm-runspace-mcp-server.md`
- `.agents/plans/shared-persistent-runspace.md`
- `.agents/review/index.md`
- `.agents/machines.md`

## Unrecorded Repo Memory

- None known.

History: rotated entries live verbatim in `docs/history/state-archive.md`.
