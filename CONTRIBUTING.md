# Contributing

Contributions are welcome. PTK executes real PowerShell and preserves
security-sensitive evidence, so behavior changes need direct tests and a clear
statement of their user-visible contract.

## Before changing behavior

Search the existing issues first. For a substantial feature, compatibility
change, new dependency, public tool change, or security-sensitive design,
open an issue before implementation so the intended contract can be settled.
Small, well-bounded bug fixes can go directly to a pull request with a linked
reproduction.

Never include real credentials, customer data, private scripts, unredacted
audit records, or sensitive output artifacts in an issue, test fixture, commit,
or workflow log.

## Development setup

The repository uses PowerShell 7 and the .NET SDK. Run the baseline from the
repository root:

```powershell
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
dotnet test server/PtkMcpServer.slnx
dotnet test siem/PtkSiem.slnx
pwsh -NoProfile -File server/test-handshake.ps1
```

Run the more specific package, installer, signing, or lifecycle checks named in
`.agents/repo-guidance.md` when your change reaches those surfaces. Do not
commit `bin/`, `obj/`, temporary package output, test evidence, credentials, or
machine-local configuration.

## Tests and pull requests

- Add a focused regression test for a bug and demonstrate that it fails
  without the repair before showing it green with the repair.
- Preserve exact command execution: PTK must not silently retry work that may
  already have started.
- Preserve audit fail-closed behavior, sensitive-data handling, output-recovery
  identity, worker containment, and release-asset immutability.
- Keep each pull request focused. Explain the user-visible change, affected
  platforms, verification run, and any check not run.
- Update public documentation when a contract, supported environment,
  configuration key, install path, or limitation changes.
- Leave unrelated `.agents/` history and decision records intact unless the
  change directly requires their current-state entry to be updated.

By submitting a contribution, you agree that it is licensed under the
repository's [Apache License 2.0](LICENSE).

