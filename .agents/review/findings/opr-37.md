# opr-37: Truncated initial-checkpoint temporary permanently blocks anchored startup

**Severity**: HIGH — a hard crash during initial checkpoint publication can leave a canonical protected temporary that every later anchored writer and out-of-band administration startup rejects until an operator edits the protected audit root.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan defines the narrow prefix-safe recovery rule and its guard.

**Source**: Bounded no-tool Claude Opus 5 review of `server/PtkMcpServer/Audit/AuditAnchoredWriterPreparation.cs` at `07f6ca4f8ef27d623211b3bbace725b5ac6935b8`, followed by adversarial fail-closed adjudication against checkpoint publication, exact recovery tests, and prior anchored-temporary recovery intent.

## Evidence

`AuditExportCheckpointStore.CreateForWriterCore` creates the persistent boot lock before `PublishInitial`. `PublishInitial` then creates the canonical temporary before writing and flushing the deterministic initial checkpoint bytes. A hard process or host death after file creation but before the full write is durable can therefore leave a zero-length or short temporary alongside the same boot's non-live lock and exact empty segment zero, with no published checkpoint.

`AuditAnchoredWriterStartupPreflight.ClassifyCandidatesOrThrow` recognizes precisely that bounded topology and computes the expected initial checkpoint bytes, but cleanup at lines 208–223 requires equal length at lines 210–214 and exact content at lines 215–222. A proper prefix, including zero bytes, throws before any cleanup. Every later `FileAuditJournalSink.PrepareAnchored` repeats the same refusal, including the active `PtkAuditAdmin` path.

The truncated temporary is not authoritative: the absence of a published checkpoint proves atomic publication did not complete, the empty segment contains no audit records, and the temporary name, boot ID, protected retained identity, non-live lock, and exact topology already bind the candidate. This is distinct from malformed or ambiguous control state and from `s2-anchored-temp-recovery`, which addressed spool allocation and compaction temporaries rather than the initial checkpoint temporary.

## Impact

One ordinary crash window can permanently deny anchored writer startup and all out-of-band evidence/disposition administration until a human manually removes protected audit state. That repeats the availability class previously rated HIGH for crash-left anchored spool temporaries.

## Required guard

Construct the exact non-live-lock, empty-segment-zero, no-checkpoint topology with a canonical protected initial-checkpoint temporary containing both zero bytes and a non-empty proper prefix of `AuditExportCheckpointCodec.Serialize(AuditExportCheckpoint.Initial(bootId))`. Prove startup removes the complete bounded recovery candidate — that temporary, its non-live persistent lock, and its empty segment zero — changes no other topology or control entry, and successfully creates a successor writer. Equal-length mismatch, over-length content, non-prefix content, live locks, nonempty segments, noncanonical names, and replaced or unprotected identities must remain fail closed. Mutation proof must show the new prefix case fails against the pre-fix exact-length rule and passes after restoration.

## Review provenance

- Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`.
- Focused baselines: `AuditAnchoredWriterPreparationTests` 22/22; `AuditCompletedChainRetirementTests` 13/13.
- Initial bounded helper pass and separate adversarial fail-closed adjudication both accepted the finding as HIGH.
- `guard_confirmed=false`; no repair or test was implemented.
