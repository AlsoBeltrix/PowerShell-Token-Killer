# opr-40: Direct process capture eagerly allocates full output budgets

**Severity**: LOW — every direct RTK or Bash execution allocates two four-megabyte large-object-heap buffers even when stdout and stderr are empty or tiny.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan defines bounded grow-on-demand capture without weakening full-stream draining or truncation truthfulness.

**Source**: Bounded and whole-file Claude Opus 5 review of production code at `2debaf6`.

## Evidence

`server/PtkMcpServer/Execution/RtkProcessRunner.cs:11` sets `MaximumCapturedStreamBytes` to 4 MiB. Every started RTK process launches one `ReadBoundedTextAsync` task for stdout and one for stderr at `:101-102`. Each task constructs `new MemoryStream(MaximumCapturedStreamBytes)` at `:374`; the `MemoryStream(int capacity)` constructor eagerly allocates its backing array. An ordinary invocation therefore allocates two 4 MiB arrays, 8 MiB total, before reading a byte. Both arrays exceed the large-object-heap threshold.

The current code remains correctly bounded and continues draining bytes after the capture cap. The defect is allocation shape: the maximum is used as the initial allocation floor rather than a growth ceiling. The focused runner tests cover small output, nonzero exit, and timeout but do not observe allocations.

`server/PtkMcpServer/Execution/BashProcessRunner.cs:48` sets the same 4 MiB cap. Execution at `:409-410` starts one bounded capture task per redirected output stream, and the shared reader constructs `new MemoryStream(MaximumCapturedStreamBytes)` at `:725`. Every started Bash execution therefore has the same eager 8 MiB allocation shape before reading output. This is the same allocation site shape and repair boundary as RTK, not a distinct finding.

## Predicted observable failure

Frequent small RTK-routed commands repeatedly allocate and discard 8 MiB of large-object-heap storage. Concurrent calls multiply the live working set by roughly 8 MiB each before other process, stream, and shaping buffers. The buffers are collectible and bounded, so the finding is LOW rather than an availability failure, but avoidable LOH churn can increase GC pauses and process memory.

The same observable allocation churn applies to frequent small Bash executions because they use the eager-capacity reader.

## Required repair

Use a bounded grow-on-demand capture strategy that preserves the 4 MiB per-stream ceiling, drains every remaining byte to EOF, retains exact truncation reporting, and does not expose pooled data. Add a focused allocation or injectable-buffer guard that fails against eager full-capacity construction while keeping existing byte, truncation, timeout, and nonzero-exit assertions load-bearing. Prove the guard red/green, run the repository verification entry point, and obtain fixed-SHA Opus review.

## Reviewer

On 2026-08-01, Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 803 lines of `BashProcessRunner.cs` at `94ff698` in three bounded passes plus whole-file production integration. Focused Bash, RTK, and containment tests passed 27/27. Independent adjudication accepted Bash capture as a scope extension of this LOW finding. No product or test file changed in the extension.

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 442 lines of `server/PtkMcpServer/Execution/RtkProcessRunner.cs` at `2debaf6` in two bounded source/caller/test passes plus one whole-file dispatch integration pass. Focused runner, containment, and invoke tests passed 76/76. Independent adjudication accepted this finding at LOW, downgrading the initial MEDIUM proposal because allocation is bounded, short-lived, and not itself a demonstrated availability failure. No product or test file changed for this finding.
