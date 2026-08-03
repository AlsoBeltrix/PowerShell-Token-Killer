# opr-58: Native-redirection guidance can destroy its producer input

**Severity**: HIGH — a successful safe invocation can return an authoritative PTK warning that recommends redirecting a native producer into the same file it reads, truncating that file before the producer starts.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan makes rewrite guidance fail closed whenever producer inputs may alias the proposed output target.

**Source**: Complete-source Claude Opus 5 review of `server/PtkMcpServer/Execution/ExecutionPlanner.cs` at `a3b7994` (blob `234c3833b6be6a22d6d984058090fd252f7244ba`), integrated with `PostSuccessGuidance`, `RunspaceHost`, focused tests, and a disposable-file runtime proof.

## Evidence

`ExecutionPlanner.TryCreateMixedDataflowGuidance` accepts any resolved application producer with constant arguments and a canonical `Set-Content` sink with one simple path. It constructs `<producer text> > <sink path>` without comparing that path to producer arguments or filesystem identity. `PostSuccessGuidance.Render` tells the caller to "prefer" the suggested command next time, and `RunspaceHost.WithDispatchRouting` appends it to a successful completed result with no errors.

A bounded proof used a Node producer that synchronously reads its input file completely before emitting it. `node ... file.txt | Set-Content file.txt` completed and retained the 13-character sentinel because the producer buffered the input before the sink wrote. Executing the planner's suggested shape, `node ... file.txt > file.txt`, left the same target at length zero because PowerShell opened and truncated the redirection target before Node read it. Both disposable proof files were removed after measurement. Existing guidance tests cover dynamic paths, wildcard/provider syntax, noncanonical sinks, extra sink arguments, and multiline input, but not input/output aliasing.

## Predicted observable failure

A native producer reads a file and emits only after buffering it; the caller captures that output back to the same file with canonical `Set-Content`. The completed invocation can be safe, so PTK emits its success warning. If the user or an agent follows the recommended native-redirection command, the shell truncates the file before launching the producer and irreversibly replaces it with empty or partial output.

## Required repair

Suppress guidance unless the planner can prove every path-like producer input is disjoint from the sink's filesystem target under the captured working directory. The guard must handle positional and attached constant arguments, relative and absolute spellings, `.` segments, platform case rules, and filesystem aliases such as links where identity is available; uncertainty must produce no guidance. Removing the advisory entirely is also safe if a complete disjointness proof is not supportable.

Add planner and active routing guards using a producer that buffers before output. Cover exact relative spelling, `./` alias, absolute spelling, Windows casing, and a filesystem alias where supported. Prove the original pipeline completes with content intact, no unsafe warning is emitted, and ordinary disjoint input/output guidance still follows the approved policy. Temporarily revert only the repair, prove the guards fail and the disposable target truncates under the suggested shape, restore it, then run focused planner/routing tests and the repository verification entry point.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) accepted this as a distinct current HIGH finding after bounded source review, focused adjudication, and the working agent's runtime proof. Severity reflects explicit PTK-authored guidance leading to irreversible data loss if followed; PTK does not execute the suggestion automatically. No product or test file changed in this finding slice.
