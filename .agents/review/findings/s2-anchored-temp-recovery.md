# s2-anchored-temp-recovery: recover bounded crash-left spool temporaries

**Severity**: HIGH — a crash during rotation can permanently prevent anchored
startup and its out-of-band administration path.
**Status**: Open
**Branch**: `fix/s2-anchored-temp-recovery`
**Commit**: pending

## Evidence

`server/PtkMcpServer/Audit/FileAuditJournalSink.cs` creates protected
`.allocating` rotation temporaries and macOS compaction temporaries before
atomic publication. Local-only startup enables bounded temporary recovery, but
anchored writer preparation disables it. `server/PtkMcpServer/Audit/
AuditAnchoredWriterPreparation.cs` rejects every non-segment spool entry as
unknown before the writer or `ptk-audit-admin` can open.

## Predicted observable failure

A hard process death after creating a valid protected rotation temporary but
before publication leaves that name in the spool. Every anchored restart and
the audit administration executable then fail preflight until a human manually
deletes a file inside the protected audit root.

## What

Teach anchored preflight to recognize and safely recover only the same bounded,
canonical crash-left allocation and compaction temporaries that the journal
sink itself can create. Unknown, malformed, linked, or unprotected entries must
continue to fail closed.

## Approach

Pending implementation. Reuse one canonical parser/recovery path rather than
duplicating filename rules. Add anchored restart guards for valid crash debris
and invalid near-miss names, with exact protection and quota behavior retained.

## Files changed

- Pending implementation.

## Guard proof

- Pending red-to-green anchored restart proof with a valid protected
  `.allocating` artifact.

## Coder dispute (if any)

None.

## Known gaps

Recovery must not turn the anchored preflight into a general spool cleanup
mechanism or delete any possibly published segment.

## Reviewer comments

Claude Code 2.1.207 (`claude-fable-5`) reviewed fixed head
`6cbd1d3061985f06bb0a5da8bcf2faa84a5bb826` against
`78e256ca0f3b1253aa97dd984f1d913429ea452a`, `guard_confirmed=true`, verdict
`reopened`, recorded 2026-07-12T14:33:24Z. The reviewer traced a hard death
between protected temporary creation and atomic publication into a permanent
anchored preflight refusal shared by normal and administrative startup.
