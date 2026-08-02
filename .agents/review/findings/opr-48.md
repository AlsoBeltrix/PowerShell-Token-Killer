# opr-48: Unix command resolution ignores real-identity execute access

**Severity**: MEDIUM — cold RTK target identity can bind a PATH file PowerShell would skip, leaving the integrity gate self-consistent about the wrong executable.

**Status**: Accepted; unplanned. Product and test changes are blocked until an approved plan covers a real-identity executable probe with cross-platform guards.

**Source**: Bounded and whole-file Claude Opus 5 review of `server/PtkMcpServer/Execution/ColdCommandResolution.cs`, integrated with `ExecutionPlanner`, `ExecutableFileIdentity`, and focused resolver/planner tests.

## Evidence

`ColdPathCommandResolver.Resolve` at `server/PtkMcpServer/Execution/ColdCommandResolution.cs:126-132` classifies a Unix candidate as `CommandTypes.Application` when any owner, group, or other execute bit is present. That union does not answer whether the server's real uid and groups may execute the file: for a non-root real uid, an owner-created file with owner execute clear but group or other execute set still satisfies the current test while `access(path, X_OK)` rejects it under the owner permission class. Real and effective identities are normally equal for this non-setuid server, but matching PowerShell requires the real-identity semantics of `access`.

PowerShell command discovery uses `Platform.NonWindowsIsExecutable`, which delegates to the native `IsExecutable` implementation and `access(path, X_OK)` (`PowerShell/PowerShell` `f9543fa3ff30f21a3cf86eefc1973c69ecbf272b`; `PowerShell/PowerShell-Native` `2c55e8fc288e31ce72974ad783151b06049591c4`). The resolver therefore stops at a PATH candidate the cold PowerShell child would skip instead of continuing to a later executable with the same name.

`ColdCommandTargetIdentity.TryCapture` can hash the readable but non-executable first file, and `MatchesCurrentResolution` repeats the same incorrect resolution. Prepare and commit consequently agree with each other while disagreeing with PowerShell. A cold RTK plan can be authorized against an executable identity that is not the command target PowerShell would select; the eventual direct launch can fail permission checks or resolve a later target outside the recorded identity, depending on the RTK/native lookup path.

## Required repair

Use the real process identity to test Unix execute access, matching PowerShell's `access(path, X_OK)` semantics, rather than inspecting the union of raw mode bits. Preserve `.ps1` ordering, the existing conservative exception behavior, and settled live-PATH revalidation.

Add a Unix guard with two PATH directories, skipped when the test process has real uid zero. In the first, create an owner-owned readable file whose owner execute bit is clear but another execute class is set; in the second, create an owner-executable file with the same name. Under the required non-root real uid, assert the resolver selects the second file. Temporarily revert only the repair, prove the guard fails under that same condition, restore it, then run the repository verification entry point and fixed-model Claude Opus 5 review.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 268 lines in two bounded passes and a whole-file integration pass. `ColdCommandResolutionTests` passed 13/13 and `ExecutionPlannerTests` passed 82/82 before review. Existing `opr-2` and resolved `rbc-13` were excluded. No product or test file changed in this finding slice.
