# opr-5: Audit startup canonicalizes configured roots before absolute-path validation

**Severity**: MEDIUM — legacy audit administration can silently bind to a launcher-dependent directory instead of rejecting an ambiguous configured root.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan covers legacy audit-administration startup validation.

**Source**: Bounded Claude Opus 5 review of current production code.

## Evidence

`server/PtkMcpServer/Audit/AuditStartupConfiguration.cs:23-28` reads `PTK_AUDIT_ROOT` and passes `Path.GetFullPath(configuredAuditRoot)` to `AuditOptions.Create`.

`server/PtkMcpServer/Audit/AuditOptions.cs:114-118` explicitly rejects a root for which `Path.IsPathFullyQualified` is false, then canonicalizes a valid root internally at line 172. The startup adapter's earlier `Path.GetFullPath` therefore makes that fail-closed guard unreachable for the only externally configured audit root. `server/PtkMcpServer.Tests/AuditOptionsHealthTests.cs:54-58` guards direct relative-path rejection, while `AuditStartupConfigurationTests` exercises only absolute and missing roots.

## Predicted observable failure

Launch `PtkAuditAdmin` with `PTK_AUDIT_ROOT=audit` or another relative or drive-relative value. Startup succeeds and resolves the audit root against the process or drive current directory. The same configured value can select different audit data depending on launch context, causing administration or disposition operations to inspect or modify an unintended local journal instead of rejecting ambiguous configuration.

## Required repair

Pass the configured value directly to `AuditOptions.Create` so its fully-qualified-path guard remains authoritative and its existing canonicalization runs exactly once. Add a startup-configuration guard that supplies a relative root and requires `ArgumentException`; retain absolute-root and missing-root behavior. Prove the guard fails against current code, restore the repair, run the repository verification entry point, and obtain fixed-SHA Claude Opus 5 review before integration.

## Reviewer

Claude Code 2.1.220 using owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, frontier, read-only review of `server/PtkMcpServer/Audit/AuditStartupConfiguration.cs` at `f2e19148763faeba4033ecdcd078abf93289e94f`. Verdict: `finding`.
