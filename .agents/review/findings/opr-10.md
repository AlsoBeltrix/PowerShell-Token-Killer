# opr-10: Out-of-range timeout values crash startup instead of falling back

**Severity**: MEDIUM — malformed positive timeout configuration can terminate the supervisor before the MCP handshake.
**Status**: Accepted; unplanned. Product change blocked until an approved plan covers timeout-configuration bounds.
**Source**: Bounded Claude Opus 5 review of `DefaultSessionRuntimeFactory`.

## Evidence

`server/PtkMcpServer/Sessions/DefaultSessionRuntimeFactory.cs` accepts any parsed `double` greater than zero, then passes it to `TimeSpan.FromSeconds`. The predicate does not require a finite number or a value within `TimeSpan` range.

Floating-point inputs that parse as positive infinity, including sufficiently large exponent forms, satisfy `seconds > 0`. `TimeSpan.FromSeconds` then throws instead of following the method's fallback path. Program startup reads both variables before constructing the MCP server, and worker startup reads them again before initialization.

### Whole-second and effective-minimum extension (2026-08-01)

The same predicate also accepts finite positive values that violate the sole downstream protocol contract. `WorkerOperationProtocol.CreateLimits` calls `WholePositiveSeconds`, which requires an integral `TimeSpan.TotalSeconds` from 1 through `WorkerOperationProtocol.MaximumTimeoutSeconds` (86,400). A value such as `1.5` parses and converts correctly, then throws at that later boundary. A sufficiently small positive value can round to `TimeSpan.Zero` and fail the same boundary. In supervisor mode this happens before the MCP handshake; in worker mode it happens before initialization-limit comparison. This is the same parser-validation defect and repair site as `opr-10`, not a distinct finding.

## Predicted observable failure

An operator typo such as an excessively large positive exponent causes an unhandled startup exception and restart loop rather than resolving to the documented fallback. No MCP handshake becomes available.

Finite fractional values such as `1.5` and sub-millisecond positive values produce the same unhandled startup failure instead of taking the documented fallback path.

## Required repair

Require `double.IsFinite(seconds)` and bound the value before converting it. Align the bound with the stricter worker-protocol timeout limit so an individually parsed value cannot survive parsing only to fail later with an unrelated range error. Add isolated tests for positive infinity, exponent overflow, the maximum accepted boundary, and one value beyond it; prove the tests red against the current parser before retaining the repair.

### Required repair extension

The complete downstream contract supersedes the narrower bound wording above: validate the parsed number before conversion as finite, integral seconds from 1 through `WorkerOperationProtocol.MaximumTimeoutSeconds` (86,400). A nonconforming configured value must take the documented fallback path rather than survive parsing and fail later in `TimeSpan.FromSeconds` or `CreateLimits`. Add isolated tests for positive infinity, exponent overflow, `1.5`, `0.5`, a sub-millisecond positive value, 1, 86,400, and one value beyond the maximum; prove the invalid-value guards red against the current parser before retaining the repair.

## Review disposition

On 2026-08-01, Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) reviewed all 44 lines of `DefaultSessionRuntimeFactory.cs` at `c5f9536` with active supervisor/worker callers and protocol validation. Focused `WorkerProcessEntryTests` and `WorkerOperationProtocolTests` passed 26/26; no dedicated parser test covers this path. Independent adjudication merged the finite fractional/sub-millisecond candidate into `opr-10` at unchanged MEDIUM severity because the defect site, production reachability, failure, and repair are identical. No product or test file changed in this extension.

Reviewer: owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, no-tool transport; exact head `421ba275a3152836df355cd477d268fa16978629`. Verdict: `finding`. No product-change guard claim.

The same review's timeout-pair candidate was rejected because `WorkerOperationProtocol.CreateLimits` already fails when the default exceeds the maximum. Coarse cancellation during `RunspaceHost` construction was non-actionable and was not recorded.
