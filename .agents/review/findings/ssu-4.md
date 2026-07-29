# ssu-4: Current transaction can remove the stable launcher path

**Severity**: MEDIUM — a connection started during an upgrade can observe the
supposedly stable launcher path as missing.

**Status**: Open

**Branch**: Not started

**Commit**: Not started

## Evidence

- `.agents/plans/mcp-side-by-side-upgrade.md:75-88` places the stable launcher
  below the install-root `scripts` directory.
- `.agents/plans/mcp-side-by-side-upgrade.md:165-167` says to install the
  launcher and control files with existing transaction machinery.
- `scripts/dev-install.ps1:69` includes `scripts` as one wholesale payload
  entry.
- `scripts/ptk_install_transaction.psm1:281-282` removes the target entry
  before moving the staged entry into place.
- `.agents/plans/mcp-side-by-side-upgrade.md:215-217` later promises upgrades
  will replace only `active.json`, but does not define the transaction/inventory
  change that makes the launcher path persistent.

## Predicted observable failure

Begin a managed MCP connection while an upgrade is between removal and movement
of the staged `scripts` entry. The registered stable launcher path does not exist,
so that connection fails before it can resolve `active.json`.

## What

The stable control plane is located inside a payload directory that the current
transaction replaces wholesale.

## Approach

Pending owner-approved plan revision. Separate immutable version payload entries
from stable control files. Define an explicit one-time launcher installation and
same-file replacement protocol that never removes the registered path, and make
ordinary version activation mutate only the activation record.

## Files changed

- Review records only; no plan or product change.

## Guard proof

Pending a fix. The guard must continuously start registered connections during
an upgrade and prove the launcher path is always executable and each successful
start resolves either the old or new complete runtime.

## Coder dispute (if any)

None.

## Known gaps

The stable-control transaction primitive remains a plan decision.

## Reviewer comments

Reviewer: claude /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / max / frontier
(owner-selected inline). Claude Code `2.1.220`, reviewed
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7..caf467e423105a621b1431302575b242f77791ac`,
verdict `findings`; admitted 2026-07-29.
