# opr-9: Timeout environment parsing is culture-sensitive

**Severity**: MEDIUM — a valid-looking timeout can silently resolve to a different duration on another host culture.
**Status**: Accepted; unplanned. Product change blocked until an approved plan covers timeout-configuration parsing.
**Source**: Bounded Claude Opus 5 review of `DefaultSessionRuntimeFactory`.

## Evidence

`server/PtkMcpServer/Sessions/DefaultSessionRuntimeFactory.cs` parses `PTK_CALL_TIMEOUT_SECONDS` and `PTK_MAX_CALL_TIMEOUT_SECONDS` with `double.TryParse(string?, out double)`. That overload uses `CurrentCulture` and permits thousands separators.

On a comma-decimal culture, a dot can be interpreted as a grouping separator: an operator value such as `1.5` can resolve as `15` rather than one and a half seconds. Conversely a culture-native value such as `600,5` does not resolve the same way on an invariant or en-US worker host. Supervisor and worker independently parse the variables, so culture drift can also produce a protocol-limits mismatch.

## Predicted observable failure

The effective default or maximum timeout silently differs by host culture, changing containment and caller latency without changing configuration text. If supervisor and worker cultures differ, worker initialization can fail with `protocol_limits_mismatch`.

## Required repair

Parse with `NumberStyles.Float` and `CultureInfo.InvariantCulture`, deliberately excluding culture-specific thousands separators. Add culture-isolated tests proving one canonical decimal representation resolves identically and ambiguous localized/grouped forms fall back. Prove the new guard red against the current parser before retaining the repair.

## Reviewer

Claude Opus 5, owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, no-tool transport; exact head `421ba275a3152836df355cd477d268fa16978629`. Verdict: `finding`. No product-change guard claim.
