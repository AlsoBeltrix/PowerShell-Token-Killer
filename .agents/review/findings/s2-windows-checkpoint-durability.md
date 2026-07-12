# s2-windows-checkpoint-durability: flush replaced checkpoint contents

**Severity**: HIGH — a Windows power loss can revert a checkpoint already used
to authorize retention, causing duplicate export or permanent chain-adoption
failure after acknowledged data was deleted.
**Status**: Open
**Branch**: `fix/s2-windows-checkpoint-durability`
**Commit**: pending

## Evidence

`server/PtkMcpServer/Audit/SecureAuditStorage.cs:263-301` replaces protected
files atomically, but the Windows `SetFileInformationByHandle(FileRenameInfoEx)`
path at `SecureAuditStorage.cs:1238-1282` does not call `FlushFileBuffers` and
the durability helper flushes the directory only on non-Windows.
`server/PtkMcpServer/Audit/AuditExportCheckpointStore.cs:358-392` treats the
successful replacement and reload as durable before installing the advanced
checkpoint state that authorizes acknowledged-prefix deletion and
evidence-anchor finalization. Line 1164 is the uncertain-replacement recovery
path, not the normal commit.

## Predicted observable failure

Power loss shortly after an acknowledged checkpoint replacement can restore
the older checkpoint. The minimum result is duplicate export. If retention
already deleted the prefix proved by the newer checkpoint, restart can reject
the retained spool floor permanently. The spool is a protected child of the
same configured root, so this is an unsupported persistence-order assumption
within Windows filesystems, not the reviewer's claimed cross-volume layout.

## What

Restore an explicit Windows content-durability barrier after the handle-based
atomic rename, without weakening the held-reader atomicity fix or introducing a
delete gap.

## Approach

Pending coder triage and implementation. Confirm the retained source handle is
valid after rename, then place `FlushFileBuffers` at the exact post-publication
boundary and add an instrumented Windows guard proving the call and ordering.

## Files changed

- Pending implementation.

## Guard proof

- Pending red-to-green Windows guard for a post-rename file flush before the
  replacement is reported durable.

## Coder dispute (if any)

The finding is valid, but two reviewer details were corrected during intake.
The normal checkpoint commit is at lines 358-392 rather than line 1164, and
spool/control roots cannot be configured on separate volumes. Neither
correction removes the missing durability barrier or rollback risk.

## Known gaps

The test cannot simulate power loss directly. It must prove the durability
primitive and ordering without reverting the open-reader replacement
semantics already guarded on Windows.

## Reviewer comments

Claude Code 2.1.207 reviewed fixed head
`49971d6ce5cb246d2283eab052163ae85a5b5c87` against
`78e256ca0f3b1253aa97dd984f1d913429ea452a`, `guard_confirmed=true`, verdict
`reopened`, recorded 2026-07-12T15:35:30Z. It traced the missing Windows
post-rename flush into checkpoint rollback after retention and classified the
result as a cross-volume crash-durability and availability failure.
