# ssu-6: Launch-time manifest verification is unbounded

**Severity**: MEDIUM — the literal launch contract can add a full runtime hash
walk to every MCP connection and push startup beyond client timeouts.

**Status**: Plan decision resolved 2026-07-29; implementation and guard not
started

**Branch**: `master`

**Commit**: Plan decision recorded in `.agents/decisions.md`; product not started

## Evidence

- At reviewed head `caf467e423105a621b1431302575b242f77791ac`,
  `.agents/plans/mcp-side-by-side-upgrade.md:94-100` defined runtime identity
  from a canonical manifest over every installer-owned runtime file and required
  every file to match before reusing a directory.
- At that head, `.agents/plans/mcp-side-by-side-upgrade.md:127` separately
  required the versioned record to match the runtime manifest before launch,
  without bounding that check or distinguishing it from full install-time
  verification.
- `.agents/plans/release-distribution.md:313` records a representative runtime
  as 558 files and 129 MB.
- `.agents/plans/release-distribution.md:330-335` already records a 2.5–2.8
  second handshake wall time.

## Predicted observable failure

On a cold cache, start a new managed connection. A literal implementation hashes
129 MB across 558 files in the PowerShell launcher before the server starts,
adding seconds to the existing handshake and intermittently exceeding client MCP
startup limits.

## What

The plan conflates the full install-time runtime-integrity check with the
constant-bounded activation-to-runtime coherence check needed on every launch.

## Approach

Owner approved the fast-start boundary on 2026-07-29. Full inventory and
per-file hashing occurs only during install/reuse. Launch reads the bounded
activation and canonical manifest files, checks manifest identity, containment,
and selected-executable attributes, and never enumerates or hashes the payload
tree.

## Files changed

- `.agents/decisions.md` — durable install-time versus launch-time boundary.
- `.agents/plans/mcp-side-by-side-upgrade.md` — manifest bounds, separate
  verifiers, explicit tradeoff, I/O ceiling, and guard requirements.
- Review/state records — finding progression only.
- No product file changed.

## Guard proof

Pending a fix. The guard must prove the normal launch path does not enumerate or
hash the runtime tree and stays within an explicit startup budget while still
rejecting malformed, escaping, or mismatched activation records.

## Coder dispute (if any)

None.

## Known gaps

Implementation and exact-host launch-cost evidence remain pending. Per-launch
full payload tamper detection is explicitly outside this plan.

## Reviewer comments

Reviewer: claude /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier
(owner-selected inline). Claude Code `2.1.220`, reviewed
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7..caf467e423105a621b1431302575b242f77791ac`,
verdict `findings`; admitted 2026-07-29.
