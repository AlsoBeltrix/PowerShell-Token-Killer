# PTK 0.3.0-rc.6

This prerelease fixes the public installer's rtk bootstrap, removes the SIEM
integration, and carries the review fixes landed since rc.5. It supersedes
the published `0.3.0-rc.5` prerelease. The draft's target commit is the
exact source; `BUILD-PROVENANCE.json` in every archive names it.

## What changed

- The installer places rtk where the server can find it. When no rtk on
  `PATH` answers, the installer downloads rtk's release archive for your
  platform, verifies it against the release's `checksums.txt`, and places the
  binary in `~/.local/bin` (or `$RTK_INSTALL_DIR`) before anything is staged.
  No third-party script runs. If that directory is not on `PATH`, the install
  stops before registering anything and says how to add it. rc.5 put rtk
  where the server never looked, and an upgrade over such an install no
  longer rolls back: a copy inside the old payload is never relied on, and
  `PTK_RTK_PATH` is honoured exactly as the server honours it.
- The installer never overwrites an rtk it did not place, reuses its own
  earlier copy, and uninstall removes only the copy it placed. It declares
  the PowerShell 7.3 floor its code has needed since July.
- The SIEM integration is gone: export destinations, the standalone receiver
  and its archives, the loopback web UI and the `ptk-audit-admin` tool. The
  local audit journal under `~/.ptk/audit` is unchanged, still mandatory and
  fail-closed. Export settings and cursor files left under the audit root by
  earlier versions are no longer read and may be deleted. A release now
  carries five PTK archives, `ptk-installer.zip` and `SHA256SUMS`.
- The audit journal's quota control no longer races a concurrent server
  start into a permanent startup failure.
- The hook and the tool descriptions lead with native PowerShell and name the
  one working way to run a bash script, instead of inviting a quoting trap.
- Checkout builds stamp a deterministic seven-character source revision;
  `System.Security.Cryptography.Pkcs` and `.Xml` are lifted past their
  advisories.

## Artifacts

macOS ARM64 assets are Developer ID signed and notarized. Windows and Linux
assets are not publisher code-signed; verify every download against
`SHA256SUMS`. Windows ARM64 runs the x64 rtk under emulation; the installer
probes `rtk rewrite` before completing.
