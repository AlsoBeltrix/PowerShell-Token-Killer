# ci-xunit2031-1: Output-list regression emits xUnit2031

**Severity**: LOW — hosted CI passes but emits an avoidable analyzer warning in the no-rerun recovery guard.
**Status**: Accepted; pending integration
**Branch**: `fix/xunit2031-output-list-guard`
**Commit**: `771522f`
**Source**: PR #24 hosted CI annotation

## Evidence

All three hosted server jobs on PR #24 annotated `NamedSessionSupervisorTests.cs:808` with xUnit2031 because the issue #16 regression filtered with LINQ `Where` before calling `Assert.Single`. A clean local rebuild reproduced exactly one xUnit2031 warning at that line.

## Predicted observable failure

Every rebuild and hosted test matrix emits the analyzer warning, obscuring future warnings and leaving the newly added recovery guard inconsistent with the repository's xUnit analyzer guidance.

## What

Use xUnit's predicate-taking `Assert.Single` overload instead of materializing a filtered enumerable.

## Approach

Pass the unfiltered line array and the existing ordinal `handle=` predicate directly to `Assert.Single`. This preserves the assertion that exactly one handle line exists while satisfying xUnit2031 and avoiding an intermediate LINQ filter.

## Files changed

- `server/PtkMcpServer.Tests/NamedSessionSupervisorTests.cs:808-810` — use the predicate overload without changing the guarded behavior.

## Guard proof

- Before the change, `dotnet build ... -t:Rebuild` emitted exactly one xUnit2031 warning at line 808.
- After the change, the same rebuild completed with no warnings or errors.
- `Completed_output_is_discoverable_through_public_list_without_response_handle` passed 1/1, and the full server solution passed 1,220/1,220.

## Coder dispute (if any)

None.

## Known gaps

None. This is test-only and changes no product behavior.

## Reviewer comments

Reviewer: claude / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier — escalated: owner (inline, session-only)

- Claude Code 2.1.220 reviewed head `33d007c7eacb9cb836b5aad5c6f0456bc8b7fa6d` against base `968451373278e35d15d5b26c124c084c9b8ba799`; `guard_confirmed=true`; verdict `accepted`; UTC `2026-08-01T02:38:29.8524951Z`.
- Reviewer confirmed the predicate overload preserves exactly-one-match semantics and still returns the matched handle line.
- Reviewer confirmed the analyzer-red/warning-free rebuild and focused/full green tests are appropriate guard evidence for this test-only fix.
- No product behavior is touched.
