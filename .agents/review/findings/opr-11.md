# opr-11: Unknown invoke routes silently fall back to automatic routing

**Severity**: MEDIUM — a typo in the explicit PowerShell-consent token can execute under a different routing policy without refusal or warning.
**Status**: Accepted; unplanned. Product change blocked until an approved plan covers invoke-route validation.
**Source**: Bounded Claude Opus 5 review of `InvokeTool` validated against audit code, tests, and approved design; runtime-boundary adequacy extended by the complete-source `WorkerSupervisor` review at `09df6b8`.

## Evidence

`server/PtkMcpServer/Tools/InvokeTool.cs` documents `route=pwsh` as explicit consent to interpret the exact original text as PowerShell and bypass automatic dialect/Bash/RTK routing, but the parameter has no `AllowedValues` constraint.

`AuditCallMetadata.NormalizeRoute` and `WorkerSupervisor.ParseRoute` lowercase the value and map everything other than `pwsh` or `rtk` to `auto`. `AuditCallMetadataTests.Invoke_route_and_timeout_normalization_match_current_tool_behavior` explicitly proves `future-route` is accepted and recorded as `auto`.

The approved tool design defines the route surface as `auto|pwsh|rtk`; it does not authorize arbitrary values as synonyms for `auto`.

## Predicted observable failure

A caller sends `route="pwhs"`, `route="powershell"`, or a value with unintended whitespace while relying on the documented PowerShell-consent behavior. The call is accepted and automatic routing runs instead; the caller receives no validation failure or warning that its consent token was not honored.

## Required repair

Add the same schema-level allowed-values constraint used by other enumerated tool arguments, reject unknown route strings in the authoritative audit boundary, and retain a defensive failure rather than an automatic fallback in supervisor parsing. Add a tool-schema assertion and boundary tests for typo, whitespace, case policy, and each accepted route. Prove the typo test red against current behavior before retaining the repair.

## Runtime-boundary extension 2026-08-01

The schema constraint required above is client guidance, not the authoritative validator. A real shipped-server stdio `ptk_state` probe for `opr-54` demonstrated that the MCP SDK emits DataAnnotations into tool schema but does not enforce those constraints on incoming `tools/call` arguments. The active server operation path must therefore validate `auto|pwsh|rtk` before routing, and `WorkerSupervisor.ParseRoute` must refuse every explicit unknown value instead of falling back to `auto`. If audit metadata capture is re-enabled, keep its validation consistent without relying on that currently dormant component to protect production execution.

Extend the required guards with real stdio calls for a typo, whitespace, case policy, and every accepted route; generated-schema assertions alone are insufficient. This does not change severity or failure mode, only the repair and guard boundary.

## Reviewer

Claude Opus 5, owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, no-tool transport; exact head `17be2e5957e4ce1c80242c912ef4580953aea56a`. Verdict: `finding`. No product-change guard claim. The runtime-boundary extension was accepted by `claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier` using working-agent-supplied stdio evidence; `guard_confirmed=false`.
