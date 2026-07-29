# ssu-9: muc-7 carries a stale state-file line citation

**Severity**: LOW — a reader following the evidence citation lands on unrelated
state text instead of the GitHub issue that supports the stale-transport claim.

**Status**: Open

**Branch**: Not started

**Commit**: Not started

## Evidence

- `.agents/review/findings/muc-7.md:22` cites `.agents/state.md:830` for GitHub
  issue 11.
- At reviewed head
  `caf467e423105a621b1431302575b242f77791ac`, the GitHub issue 11 bullet was at
  `.agents/state.md:850`, not line 830.
- The durable anchor begins `GitHub #11 (Codex keeps a stale ptk transport...`;
  its current line can continue moving as state is maintained.

## Predicted observable failure

A reviewer follows the recorded line citation and cannot verify the evidence for
the finding that justifies the side-by-side upgrade plan.

## What

A volatile line number was copied without being revalidated against the reviewed
head.

## Approach

Pending correction. Cite the stable leading text of the GitHub issue 11 bullet
and, if a line number is retained, update it in the same commit.

## Files changed

- Review records only; the stale citation remains unchanged.

## Guard proof

Pending a fix. Re-resolve the anchor in the current state file and confirm the
finding citation identifies that exact bullet.

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: claude /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier
(owner-selected inline). Claude Code `2.1.220`, reviewed
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7..caf467e423105a621b1431302575b242f77791ac`,
verdict `findings`; admitted 2026-07-29.
