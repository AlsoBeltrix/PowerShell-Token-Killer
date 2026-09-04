# Privacy

PowerShell Token Killer (PTK) is local software, not a hosted service. PTK does
not send command text, command output, audit records, build identity, or usage
counters to the project maintainers.

## Data stored locally

PTK handles data that can be highly sensitive:

- Same-invocation output artifacts are stored below `~/.ptk/output` (or
  `PTK_OUTPUT_ROOT`). By default they expire after 15 minutes, are subject to
  per-artifact, per-session, and aggregate capacity limits, and never outlive
  the supervisor process. They may contain complete command output that was
  shortened in the MCP response.
- The mandatory audit root, `~/.ptk/audit` by default (or `PTK_AUDIT_ROOT`),
  stores execution metadata and owner-only evidence containing the exact
  submitted script bytes. Evidence and exported records can also contain
  command output, errors, host details, paths, account names, customer data,
  passwords, or tokens. Audit storage has its own retention and capacity
  contract; it is not the short-lived output cache.
- Installation places the product below `~/.ptk` and may add user-scoped MCP
  registrations, hooks, or guidance for the agent clients the user selects.
- The separately deployed SIEM receiver stores submitted audit data in the
  operator-configured database and evidence paths.

Protect `~/.ptk`, its backups, exported evidence, and any SIEM destination as
sensitive data. The detailed storage and retention contract is in
[`server/AUDIT-EXPORT.md`](server/AUDIT-EXPORT.md).

## Network activity

The PTK server has no maintainer telemetry or automatic update service. Network
activity occurs only through these paths:

- a command submitted by the user or agent uses the network with the same OS
  identity, permissions, and upstream authorization as the PTK process;
- an operator explicitly configures audit export or an alert webhook;
- an operator deploys and configures the standalone SIEM receiver, including
  any receiver alert webhook;
- the public installer downloads PTK assets and checksums from this project's
  GitHub releases and, when RTK is not already installed, downloads RTK and its
  checksum from `rtk-ai/rtk` GitHub releases; or
- the operating system performs its normal certificate, signature, or
  notarization checks for downloaded software.

The producer status UI binds to loopback by default. It does not send its
contents to the maintainers.

Those third-party services and operator-selected destinations apply their own
privacy and retention policies. PTK does not control them.

## Information shared with the project

The maintainers receive only information a person deliberately submits through
GitHub issues, pull requests, discussions, or another explicitly published
project channel. GitHub processes that information under GitHub's policies.
Do not post credentials, access tokens, private command text, customer data, or
unredacted audit/output artifacts in a public report.

## Removal

`scripts/install.ps1 -Uninstall` removes installer-owned product files and
registrations while preserving user-owned data. Add `-Purge` to remove the
remaining PTK home, including local configuration and data, after making any
required backup. Data already sent to an operator-configured SIEM, webhook, or
third-party service must be removed under that destination's procedures.

