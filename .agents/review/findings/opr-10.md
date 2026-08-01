# opr-10: Out-of-range timeout values crash startup instead of falling back

**Severity**: MEDIUM — malformed positive timeout configuration can terminate the supervisor before the MCP handshake.
**Status**: Accepted; unplanned. Product change blocked until an approved plan covers timeout-configuration bounds.
**Source**: Bounded Claude Opus 5 review of `DefaultSessionRuntimeFactory`.

## Evidence

`server/PtkMcpServer/Sessions/DefaultSessionRuntimeFactory.cs` accepts any parsed `double` greater than zero, then passes it to `TimeSpan.FromSeconds`. The predicate does not require a finite number or a value within `TimeSpan` range.

Floating-point inputs that parse as positive infinity, including sufficiently large exponent forms, satisfy `seconds > 0`. `TimeSpan.FromSeconds` then throws instead of following the method's fallback path. Program startup reads both variables before constructing the MCP server, and worker startup reads them again before initialization.

## Predicted observable failure

An operator typo such as an excessively large positive exponent causes an unhandled startup exception and restart loop rather than resolving to the documented fallback. No MCP handshake becomes available.

## Required repair

Require `double.IsFinite(seconds)` and bound the value before converting it. Align the bound with the stricter worker-protocol timeout limit so an individually parsed value cannot survive parsing only to fail later with an unrelated range error. Add isolated tests for positive infinity, exponent overflow, the maximum accepted boundary, and one value beyond it; prove the tests red against the current parser before retaining the repair.

## Review disposition

Reviewer: owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, no-tool transport; exact head `421ba275a3152836df355cd477d268fa16978629`. Verdict: `finding`. No product-change guard claim.

The same review's timeout-pair candidate was rejected because `WorkerOperationProtocol.CreateLimits` already fails when the default exceeds the maximum. Coarse cancellation during `RunspaceHost` construction was non-actionable and was not recorded.
