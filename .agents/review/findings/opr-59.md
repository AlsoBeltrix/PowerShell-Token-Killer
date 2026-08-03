# opr-59: EOF read falsely reports that the artifact captured no bytes

**Severity**: LOW — a valid page read at the end of a nonempty retained
artifact returns a self-contradictory public response that can make an agent
discard or restart otherwise complete recovery.

**Status**: Accepted; unplanned. Product and test changes are blocked until an
approved plan defines the page-scoped EOF wording and preserves the true
zero-byte-artifact marker.

**Source**: Exact-source Claude Opus 5 review of
`server/PtkMcpServer/Execution/OutputStore.cs:943-1049` at blob
`7ca10b70d3ef5b7df9a22163e464aa9a134354c8`, integrated with
`server/PtkMcpServer/Tools/OutputTool.cs:193-210` at blob
`de140b30e51d04c35822f2826fd7c0555a1fd1f7`.

## Evidence

For a readable artifact with `TotalBytes = N > 0`, `ReadCore` intentionally
accepts `offset == N` as a valid UTF-8 EOF boundary. The chunk is empty, so it
returns `State=Available|Incomplete`, `Text=""`, `Offset=N`,
`NextOffset=N`, `TotalBytes=N`, and `BytesRead=0`.

`OutputTool.FormatRead` appends `(no captured bytes)` whenever text is empty
and state is Available or Incomplete. It does not distinguish the valid EOF
page above from a genuinely empty artifact. The resulting response reports
`bytes=N offset=N next_offset=N bytes_returned=0` and then claims that no
bytes were captured. Existing recovery tests assert recovered payload
presence, but no guard formats a nonempty artifact at its EOF offset.

## Predicted observable failure

An agent pages through retained output and makes one final read using the
previous `next_offset`. The response correctly says the artifact contains
nonzero bytes and that the page returned zero, then says no bytes were
captured. The agent can misreport an empty command result, discard already
recovered pages, or restart from offset zero. The header permits recovery by a
careful consumer, so severity remains LOW.

## Required repair

Change only the public read formatter and its focused guards. Preserve
`(no captured bytes)` when `TotalBytes == 0`. For an empty page from a
nonempty artifact, either emit an explicitly page-scoped EOF marker or emit no
body marker; settle that wording in an approved plan. Do not change
`OutputStore.ReadCore`, pagination offsets, artifact state, or byte
accounting.

Guard both Available and Incomplete nonempty artifacts at
`offset == TotalBytes`: the formatted response must retain the correct
nonzero byte count and zero-byte page fields without the capture-absence
marker. A true zero-byte artifact must retain that marker. Temporarily restore
the old predicate and prove the EOF guard fails, then restore the repair and
run the focused output/invoke tests plus the repository verification entry
point.

## Reviewer

Claude Opus 5
(`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`)
returned `NEW_FINDING`, severity LOW, after tracing the exact EOF tuple from
`ReadCore` into `FormatRead`. No product or test file changed in this
finding slice.
