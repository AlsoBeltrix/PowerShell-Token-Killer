# ssu-6: Launch-time manifest verification is unbounded

**Severity**: MEDIUM — the literal launch contract can add a full runtime hash
walk to every MCP connection and push startup beyond client timeouts.

**Status**: Open

**Branch**: Not started

**Commit**: Not started

## Evidence

- `.agents/plans/mcp-side-by-side-upgrade.md:94-100` defines runtime identity
  from a canonical manifest over every installer-owned runtime file and requires
  every file to match before reusing a directory.
- `.agents/plans/mcp-side-by-side-upgrade.md:127` separately requires the
  versioned record to match the runtime manifest before launch, without bounding
  that check or distinguishing it from full install-time verification.
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

Pending owner-approved plan revision. Keep full per-file verification at install
and reuse time. At launch, validate only the bounded activation record, the
recorded manifest digest/identity, containment, and selected executable
existence. If full launch-time tamper detection is required, make that an
explicit measured feature rather than an ambiguous default.

## Files changed

- Review records only; no plan or product change.

## Guard proof

Pending a fix. The guard must prove the normal launch path does not enumerate or
hash the runtime tree and stays within an explicit startup budget while still
rejecting malformed, escaping, or mismatched activation records.

## Coder dispute (if any)

None.

## Known gaps

The exact bounded trust relationship between `active.json` and the immutable
manifest remains a plan decision.

## Reviewer comments

Reviewer: claude /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier
(owner-selected inline). Claude Code `2.1.220`, reviewed
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7..caf467e423105a621b1431302575b242f77791ac`,
verdict `findings`; admitted 2026-07-29.
