# Plan: issue #1 — object leg must compress, never destroy, content-bearing streams

**Status:** APPROVED by owner 2026-07-09 ("go" on the in-session
assessment; spec = GitHub issue #1, filed by owner). Taken ahead of
multi-harness slice 3 by the same go.

## Problem (diagnosed against src/PwshTokenCompressor.psm1)

Pure streams are already safe: all-`MatchInfo` routes to
`Compress-PtcMatchInfo` (keeps line numbers + text), all-`String` routes to
joined text passthrough. The destruction happens on **mixed streams**
(String + MatchInfo in the live repro): every specialized guard correctly
declines, `Compress-PtcGenericObject` renders `objects: N (type-of-first)`
plus a property table keyed off the FIRST item's display properties — for a
leading String that is a Length-only table (the repro's `13` is
`"---tracked---".Length`) and the payload is gone. Secondary defect: any
type-heterogeneous stream gets a table keyed on the first item's
properties, misrepresenting the rest.

## Slices

1. **String-bearing mixed streams stringify** (issue fix #2; covers the
   repro). In `Compress-PtcGenericObject`: when the stream contains at
   least one `String` but not only strings, render every item by its
   string form (`[string]$_` — strings are themselves, `MatchInfo.ToString()`
   is `path:line:content`) joined as lines, through the same
   `Limit-PtcText` bound the all-strings leg uses. The text was the medium;
   treat it as text.
2. **Heterogeneous-stream guardrail** (issue fix #3, scoped). For
   type-heterogeneous streams without strings that still reach the generic
   table: header says `mixed:` with the type list instead of naming only
   the first type, and up to 3 width-limited `ToString()` sample lines are
   appended after the table — a generic summary must always carry some
   payload, not just shape.

Homogeneous streams (fs/process/service/matchinfo routes, all-strings
passthrough, single-type generic tables) are untouched — bite-proof from
the issue: the repro pipeline returns the matching lines; `Get-Process`
still returns the typed summary.

## Out of scope

Server-side changes (the module is primed into the server; no server code
moves), rtk routing, and any recompression of the fs/process/service
routes.

## Verification

Pester battery with new guard tests per slice (repro-shaped mixed stream;
heterogeneous samples), guard proofs by sabotaged revert, `dotnet test`,
and the stdio handshake (module changes are served behavior). Codex loop
over the slice commits per the recorded process. GitHub issue #1 gets the
fix reference once the owner pushes.
