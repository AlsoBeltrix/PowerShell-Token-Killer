# opr-50: Windows drive-relative command names bypass the bare-name guard

**Severity**: MEDIUM — a cold RTK plan can resolve `C:tool` against server drive state instead of the child location while treating the result as a revalidatable literal command target.

**Status**: Accepted; unplanned. Product and test changes are blocked until an approved plan conservatively excludes Windows drive-qualified command forms from cold RTK routing.

**Source**: Bounded and whole-file Claude Opus 5 review of `server/PtkMcpServer/Execution/ColdCommandResolution.cs`, integrated with target identity, planner invariants, and real child-process probes.

## Evidence

The literal-name guard at `server/PtkMcpServer/Execution/ColdCommandResolution.cs:35-38` rejects directory separators but not the Windows volume separator. A command token such as `C:cmd.exe` therefore enters PATH resolution even though it is a drive-relative path form, not a bare command name.

Inside the Windows candidate loop, `Path.Combine(directory, candidate.Name)` at `:179` discards the PATH directory for a rooted drive-relative candidate. `PathCommand` then calls parameterless `Path.GetFullPath` at `:187`, binding the result to the server process's drive state. `MatchesCurrentResolution` at `:249-254` takes the resolver branch because `C:cmd.exe` is rooted but not fully qualified, so commit revalidation repeats the same base error.

A local probe created `pwsh` with OS `WorkingDirectory = C:\Windows\System32`; `Get-Command C:cmd.exe -CommandType Application` resolved `C:\Windows\System32\cmd.exe`. With the server process current directory on D:, the resolver expression normalized `C:cmd.exe` as `C:\cmd.exe`. The command token thus has concrete PowerShell semantics that the resolver does not reproduce.

The common outcome is conservative fallback or a spurious no-start when the server-derived target is missing or differs from the frozen identity. If a file exists at the server-derived drive-relative location, preparation and revalidation can remain self-consistent around a target selected from server state rather than child state. The reviewed code does not prove which image a later RTK/native launcher executes, so this finding is limited to the incorrect resolution and weakened target-identity contract.

## Required repair

Keep cold optimization limited to true bare command names by rejecting `:` when `windows` is true. Route drive-relative and other volume-qualified forms through exact PowerShell execution instead of attempting to emulate their session/process drive semantics. Do not change Unix command-name handling, settled live-PATH revalidation, or fully-qualified target identity behavior.

Add a Windows resolver guard asserting `Resolve("C:fixture.exe", ...)` returns `null`, including a control where a server-drive-relative file would otherwise satisfy `File.Exists`. Add a planner control proving the excluded form remains on the exact PowerShell path. Temporarily revert only the repair, prove the resolver guard fails, restore it, then run the repository verification entry point and fixed-model Claude Opus 5 review.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 268 lines in two bounded passes and a whole-file integration pass. `ColdCommandResolutionTests` passed 13/13 and `ExecutionPlannerTests` passed 82/82 before review. Existing `opr-2` and resolved `rbc-13` were excluded. No product or test file changed in this finding slice.
