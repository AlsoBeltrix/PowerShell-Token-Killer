# opr-38: Acknowledged-gap reason accepts one trailing line feed

**Severity**: LOW — the shipped administration CLI can durably publish a disposition reason outside its declared token grammar, and a retry using the clean spelling then conflicts with the stored intent.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan replaces the newline-permissive anchor and adds the exact grammar guard.

**Source**: Bounded no-tool Claude Opus 5 review of `server/PtkMcpServer/Audit/AuditOperatorDispositionIntent.cs` at `9d29281`, followed by production CLI reachability and materiality adjudication.

## Evidence

`AuditOperatorDispositionProof.ReasonPattern` uses `^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$`. In .NET, `$` also matches immediately before one final line feed, so `operator.accepted\n` passes the pattern and the 128-character length bound even though the grammar intends one lowercase dotted token.

`PtkAuditAdmin` passes `args[6]` directly to `AuditOperatorDispositionProof.AcknowledgedGap` for `--acknowledged-gap-reason`; quoted process arguments can contain an embedded line feed. The accepted value flows unchanged into `IntentFields`, is JSON-escaped, hashed, atomically published, and re-admitted by the same validator on every read.

The malformed reason is part of intent compatibility. Repeating the same disposition with the visible clean spelling produces a different proof and a hard conflict instead of returning the existing disposition identity. JSON framing and authority remain intact, so the impact is bounded to validator correctness and operator-visible idempotence.

## Required guard

Prove `AuditOperatorDispositionProof.AcknowledgedGap` rejects a valid reason token followed by exactly one `\n`, while accepting the clean token and preserving the existing length and character grammar. Exercise the production disposition parse path if the test seam permits it, and prove no intent file is published for the rejected argument. Temporarily restore the `$` anchor, confirm the exact trailing-LF guard fails for the intended reason, restore `\z`, then run focused operator-disposition and full server verification.

## Review provenance

- Reviewer: `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`.
- Focused baseline: `AuditOperatorDispositionTests` 22/22.
- Initial bounded pass and separate production-reachability/materiality adjudication both accepted the finding as LOW.
- A separate `FirstFailureUtc` concern was rejected because `ValidateFields` constructs `AuditExportBlockedRecord`, whose codec validation requires a zero UTC offset.
- `guard_confirmed=false`; no repair or test was implemented.
