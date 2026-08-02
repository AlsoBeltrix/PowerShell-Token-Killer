# opr-49: Windows rooted PATH entries bind the server drive

**Severity**: MEDIUM — cold resolution can select or reject a different executable from the child when a rooted-but-not-fully-qualified PATH entry is evaluated on another drive.

**Status**: Accepted; unplanned. Product and test changes are blocked until an approved plan covers base-aware Windows PATH normalization and cross-drive parity guards.

**Source**: Bounded and whole-file Claude Opus 5 review of `server/PtkMcpServer/Execution/ColdCommandResolution.cs`, integrated with target identity, planner invariants, and real child-process probes.

## Evidence

At `server/PtkMcpServer/Execution/ColdCommandResolution.cs:83-97`, every PATH entry that is not fully qualified is normalized with `Path.GetFullPath(Path.Combine(workingDirectory, directory))`. On Windows, an entry such as `\tools` or `\Windows\System32` is rooted but not fully qualified. `Path.Combine` discards `workingDirectory` when its second argument is rooted, and the parameterless `Path.GetFullPath` then supplies the server process's current drive. Drive-relative entries such as `C:tools` similarly depend on process-global drive state instead of the audited child working directory.

A local process-boundary probe created `pwsh` through `ProcessStartInfo` with `WorkingDirectory = C:\Windows\System32` and `PATH = \Windows\System32`; PowerShell resolved `cmd.exe` from that C: root. With the server process current directory on D:, the current resolver expression normalized the same PATH entry as `D:\Windows\System32`. This is a real resolver/child semantic divergence, not a textual path difference.

The wrong directory can produce a conservative miss and direct fallback or a spurious `RtkTargetResolutionChanged` rejection. If the server-current volume contains a same-named candidate, resolver preparation and revalidation can instead remain self-consistent around a file the cold child would not select, weakening the target-identity contract. The reviewed identity equality prevents a changed resolver result from silently matching a different digest, but it cannot detect a prepare/commit pair that repeats the same incorrect base.

## Required repair

Resolve non-fully-qualified PATH entries with the supplied absolute `workingDirectory` as the base, using the base-aware `Path.GetFullPath(directory, workingDirectory)` behavior rather than combining first and normalizing against process-global state. Preserve the existing uncertain result for entries that cannot be normalized, platform-correct de-duplication work tracked by `opr-2`, and settled live-PATH revalidation.

Add Windows guards for root-relative and drive-relative PATH entries. Run resolution with a `workingDirectory` on a drive different from the server process current drive, create the candidate only beneath the working-directory-derived target, and assert resolution is independent of `Directory.GetCurrentDirectory()`. Skip only when no safe second drive exists. Temporarily revert only the repair, prove the root-relative guard fails, restore it, then run the repository verification entry point and fixed-model Claude Opus 5 review.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 268 lines in two bounded passes and a whole-file integration pass. `ColdCommandResolutionTests` passed 13/13 and `ExecutionPlannerTests` passed 82/82 before review. Existing `opr-2` and resolved `rbc-13` were excluded. No product or test file changed in this finding slice.
