# rbc-4: AuditOtlpHttpExporter TLS revocation disabled by default with no opt-in

**Severity**: MAJOR
**Status**: Open (intake, awaiting owner triage)
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `server/PtkMcpServer/Audit/AuditOtlpHttpExporter.cs:432`

## Evidence

`ConfigureCustomTrustPolicy` sets `RevocationMode = X509VerificationFlags.NoCheck`,
silently skipping certificate revocation checks. For an audited security
pipeline that explicitly forbids auto-redirect, disables cookies, and pins
custom roots, this is a meaningful downgrade. An attacker who obtains a
revoked-but-still-valid server cert could establish TLS and inject malicious
audit acknowledgments.

There is no configuration option to opt into a stricter revocation mode and
no comment justifying the `NoCheck` choice.

## Predicted observable failure

A compromised OTLP endpoint with a revoked certificate (revoked by the CA
but not yet expired) establishes TLS with the PTK exporter, receives audit
records, and returns forged 200 acks that cause PTK to advance its
checkpoint as if the records were durably received by a legitimate
collector.

## What

At minimum, add an explicit comment justifying `NoCheck` (e.g., air-gapped
OTLP, private CA without CRL distribution). Preferably, make revocation
mode configurable (default `Online` or `Offline` with a CRL cache) and
document the tradeoff. If `NoCheck` is intentional for the current
deployment model, record that decision in `.agents/decisions.md`.

## Scope of fix

One property in `AuditOtlpHttpExporter.cs`, plus a configuration option
in `AuditExportConfiguration` if made configurable. No architectural
change.

## Guard proof

Not yet written. If made configurable, a guard should assert that a
revoked cert is rejected under `Online`/`Offline` mode and accepted
under an explicit `NoCheck` opt-in.

## Reviewer comments

Read-only review by Hermes subagent (audit subsystem pass). No external
fixed-SHA review has been dispatched.