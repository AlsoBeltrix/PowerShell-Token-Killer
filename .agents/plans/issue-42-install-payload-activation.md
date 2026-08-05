# Plan: issue #42 — install nests the payload and leaves a stale server registered

Status: DRAFT, awaiting owner approval.
Issue: https://github.com/AlsoBeltrix/PowerShell-Token-Killer/issues/42
Release-blocking for 1.0.

## Problem (verified in current source and on a live install, not from the issue text)

`Install-PtkStagedPayload` activates each payload entry with remove-then-move
(`scripts/ptk_install_transaction.psm1:279`):

```powershell
$target = Join-Path $PayloadRoot $entry
Remove-PtkInstallPath -Path $target
Move-Item -LiteralPath (Join-Path $StagingRoot $entry) -Destination $target
```

`Move-Item` of a directory onto a path that **still exists as a directory**
does not replace it — it moves the source inside. Reproduced in isolation:
moving `stage\bin` onto an existing `dest\bin` yields `dest\bin\bin\new.txt`
with `dest\bin\old.txt` surviving.

`Remove-PtkInstallPath` (`:78`) calls `Remove-Item -Recurse -Force` and does
not verify the path is gone afterward. Any condition that leaves the
directory present — a running server holding a file handle, an AV scanner, a
partially failed recursive delete — silently converts a replace into a nest.

Live evidence on `ASHBIAMWEB1` at the time of filing:

```
~/.ptk/bin/       111 files, PtkMcpServer.exe 2026-08-04 15:35  <- registered, incomplete
~/.ptk/bin/bin/   296 files, PtkMcpServer.exe 2026-08-04 16:00  <- the actual install
```

A correct `dotnet publish -r win-x64 --self-contained` produces 296 files;
the registered payload was missing 185 (179 `.dll`), including
`System.Collections.NonGeneric.dll` and `Microsoft.PowerShell.SDK.dll`.

### Why both validation gates passed

- `Assert-PtkPayloadIntact` (`scripts/install.ps1:199`) tests five named
  paths. All five exist in the stale payload. It was written for the issue #7
  single-file quarantine case; a 185-file shortfall passes it unchanged.
- `Invoke-PtkPackageSmoke` passes because the truncated server starts and
  completes a handshake. The missing assemblies are only needed once a worker
  materializes an affected object.

### User-visible consequence

Inside a worker on the incomplete payload, `Get-Process` and `Get-Service`
are refused with `Trusted pre-execution isolation failed; the script was NOT
executed and the runspace was recycled`, so an ordinary read-only command
destroys warm session state. `ConvertFrom-Json` throws a Newtonsoft type
initializer error. The underlying fault is
`ExtendedTypeSystemException` wrapping
`FileNotFoundException: System.Collections.NonGeneric`. This is the defect
`.agents/state.md` had carried as an uninvestigated live observation.

## Non-goals

- No new install layout, no versioned/retained payload directories, no
  rollback redesign. The transaction's existing snapshot/restore machinery
  stays as it is.
- No AV-specific handling beyond what issue #7 already added.
- No change to what `dotnet publish` produces.

## Slices

Each slice is one commit, with its guard proved by sabotaged revert before
the commit lands.

### Slice 1 — activation must replace, never nest

Make `Install-PtkStagedPayload` assert the target is gone after
`Remove-PtkInstallPath` and before `Move-Item`, and fail the transaction with
an actionable message naming the surviving path if it is not. The existing
catch/rollback path then restores the prior install rather than producing a
nested one.

Consider hardening `Remove-PtkInstallPath` itself to verify removal, since
that is the invariant it implies; decide during implementation whether the
check belongs there, at the call site, or both.

Guard: an xUnit/Pester test that pre-creates the destination directory and
makes removal fail (or stubs `Remove-PtkInstallPath` to a no-op), then
asserts the transaction throws and that no `bin/bin` exists. Prove it red
against current source before retaining.

### Slice 2 — verify the installed payload against the staged one

Replace the five-name check with a set comparison. The staged layout is
generated in the same run, so the installer can require that every relative
path under the staging root exists in the installed root with matching
length. `Get-PtkInstallPathFingerprint`
(`scripts/ptk_install_transaction.psm1:3`) already produces exactly this kind
of recursive record (relative path, mode, length, SHA-256) and refuses
reparse points; prefer reusing it over writing a second walker.

This subsumes the issue #7 quarantine check — a quarantined
`PtkMcpServer.dll` is a missing entry — so `Assert-PtkPayloadIntact`'s
Defender guidance text must be preserved in the new failure message rather
than dropped.

Open question for implementation: hash comparison over ~300 files costs real
time on every install. Length-and-path may be the right default with hashing
reserved for the binary itself. Decide with a measurement, and record it.

Guard: a test that deletes N files from the installed root after activation
and asserts the validation fails naming them. Prove red first.

### Slice 3 — detect and handle an already-nested install

Installs in the wild may already be in this state, and #42 was found only
because a worker misbehaved. On install (and cheaply at server startup, if
that proves clean to do), detect a payload root containing a nested
duplicate and either repair it or refuse with instructions.

Decide the response during implementation: repairing someone's install
directory automatically is a heavier action than refusing with a clear
message, and the simplicity rule in `.agents/repo-guidance.md` favors the
smaller one. Bring the recommendation to the owner if it turns out to need
more than a refusal.

Guard: a test constructing a nested layout and asserting the chosen
behaviour. Prove red first.

### Slice 3b — the release gate must not pass on a payload this incomplete

Confirmed 2026-08-05: `server/direct-product-proof.ps1` returns
`DIRECT PROOF PASSED: 16 checks` against the broken registered payload on
this host, including `renders a trusted type instead of dropping it`. That
check uses `Get-Culture` (`:119`), and `CultureInfo` does not need any of
the missing assemblies. `server/test-handshake.ps1 -UseRegistrationCommand`
passes too.

`.agents/repo-guidance.md` §Verification names the direct product proof as
the release gate for a packaged artifact, so as it stands that gate would
sign off on an artifact carrying this defect.

Add a check exercising a type that needs the wider assembly set — a
`Get-Process`-shaped projection is the obvious one, since that is what
fails. This is a narrow fix for one assembly and does not replace slice 2;
land it with slice 2, since both ask the same question at different
boundaries.

Guard: the new check must fail against the nested/truncated payload and
pass against a correct one. Both payloads exist on this host right now, so
prove it against each rather than by simulation.

### Slice 4 — close the loop

Repair this host's own install, confirm `Get-Process`, `Get-Service`, and
`ConvertFrom-Json` all work in a fresh worker, comment on #42 with the fix
and the verification, and close it.

## Verification

Full battery per `.agents/repo-guidance.md` §Verification — Pester, both
dotnet suites, dependency audit — plus `server/test-handshake.ps1`, since
this touches install and server registration. A real install/reinstall on
this host is required evidence for slices 1–3, not optional: the defect was
invisible to every automated gate that existed.

Codex review after the code lands, per the session process. Dispatch with
`-c 'mcp_servers={}'` (see `.agents/review/index.md`).

## Risk

The install transaction is the one code path that can leave a user with no
working ptk at all, and its failure mode is exactly the thing being changed.
Two specific hazards:

- A stricter activation assert converts installs that previously "succeeded"
  (nested) into hard failures. That is the intent, but the rollback path must
  be proved to restore the prior payload, not leave a half-state.
- A stricter payload verification that is wrong in either direction is
  expensive: a false positive blocks every install, a false negative
  reproduces #42. The comparison must be against the staged set generated in
  the same run, never a hardcoded manifest that drifts from what publish
  emits.
