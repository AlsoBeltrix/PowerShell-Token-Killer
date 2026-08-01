# opr-12: Negative invoke timeouts silently select the server default

**Severity**: LOW — invalid negative input is accepted as a different documented mode rather than rejected.
**Status**: Accepted; unplanned. Product change blocked until an approved plan covers invoke-timeout validation.
**Source**: Bounded Claude Opus 5 review of `InvokeTool` validated against audit code, tests, and approved design.

## Evidence

`server/PtkMcpServer/Tools/InvokeTool.cs` declares `timeoutSeconds=0` as the server-default selector and documents positive overrides, but has no non-negative range constraint.

`AuditCallMetadata` treats every value less than or equal to zero as the default timeout. `AuditCallMetadataTests.Invoke_route_and_timeout_normalization_match_current_tool_behavior` explicitly proves `timeoutSeconds=-10` is accepted and recorded as the 300-second default.

The approved timeout contract assigns the default meaning to zero and describes positive overrides; it does not assign meaning to negative values.

## Predicted observable failure

A caller supplies a negative timeout due to a calculation or serialization error. The call executes with the operator-configured default rather than failing validation, so work can run substantially longer than the caller intended and the invalid input is not surfaced.

## Required repair

Constrain the public schema to non-negative integers and reject negatives at the authoritative audit boundary and supervisor defense layer. Keep exactly zero as the default selector and preserve positive capping. Add schema and boundary tests for `-1`, `0`, `1`, and an over-maximum positive value; prove the negative test red against current behavior before retaining the repair.

## Reviewer

Claude Opus 5, owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, no-tool transport; exact head `17be2e5957e4ce1c80242c912ef4580953aea56a`. Verdict: `finding`. No product-change guard claim.
