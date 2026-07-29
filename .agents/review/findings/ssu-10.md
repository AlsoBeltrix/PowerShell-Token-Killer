# ssu-10: muc-7 index row lacked reviewer identity

**Severity**: LOW — the finding index omitted the reviewer/model/effort
provenance carried by every sibling row in the same review batch.

**Status**: Resolved before intake at current head

**Branch**: `master`

**Commit**: `36a1682`

## Evidence

- At reviewed head
  `caf467e423105a621b1431302575b242f77791ac`,
  `.agents/review/index.md:2730` defined the final table column as `Reviewer`.
- The reviewed `muc-1` through `muc-6` rows carried
  `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`.
- The reviewed `muc-7` row put status prose in that final column instead of the
  reviewer identity.
- Post-review commit `36a1682` restored the reviewer identity before this
  candidate was admitted.

## Predicted observable failure

A reader using the finding index cannot determine which reviewer, model, and
effort produced `muc-7`, unlike every sibling finding in the batch.

## What

The new row did not conform to the table's reviewer-column schema.

## Approach

Completed by `36a1682`: keep workflow status in the finding/status records and
put reviewer provenance in the index's `Reviewer` cell.

## Files changed

- `.agents/review/index.md` — corrected before intake by `36a1682`.
- Review records — record the admitted, already-resolved candidate.

## Guard proof

Current `.agents/review/index.md` gives `muc-7` the same six-column shape and
reviewer identity convention as `muc-1` through `muc-6`.

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: claude /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier
(owner-selected inline). Claude Code `2.1.220`, reviewed
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7..caf467e423105a621b1431302575b242f77791ac`,
verdict `findings`; admitted 2026-07-29 and already resolved at current head.
