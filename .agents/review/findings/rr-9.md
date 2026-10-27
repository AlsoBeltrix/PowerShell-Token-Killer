# rr-9: rtk's installer script is fetched unpinned

**Severity:** MEDIUM — supply-chain and availability exposure on every Linux
or macOS install that lacks rtk.
**Status:** CLOSED 2026-10-27 by the owner's ruling D2 (codex accepted `2455ecd3..3f465431`): the installer no
longer fetches or runs rtk's script on any platform; it downloads the
platform's archive from rtk's latest release, verifies it against the
release's `checksums.txt`, and places the binary itself, the same path
Windows already used. The fetcher is tested with fixture archives of both
kinds on every platform, and the source is pinned to contain no
`install.sh` reference. The live `-FromRelease` bootstrap proof moves to the
next release's post-publication step (D4).
**Scope:** `scripts/install.ps1` (`Install-PtkRtkWithScript`).

Since `406aab47` (2026-10-24, owner direction "rtk goes where its own
installer puts it"), a Linux or macOS install without rtk downloads
`https://raw.githubusercontent.com/rtk-ai/rtk/refs/heads/master/install.sh`
and runs it with `sh`. Verified 2026-10-26: the URL answers 200; the script
honors `RTK_INSTALL_DIR`, defaults to `~/.local/bin`, downloads
`releases/latest` and verifies it against `checksums.txt` — all matching
what the installer assumes. But rtk's default branch is `develop`, so
`master` is a secondary, mutable ref that may lag or disappear, and PTK runs
whatever it serves with no integrity check of its own. The rc.5 record states
this route has never been exercised live; the installer's Pester coverage
fakes the script call.

Proposed route proof (one run, throwaway homes, an isolated
`RTK_INSTALL_DIR`, a `PATH` that cannot see the host's rtk):
`scripts/install.ps1 -FromRelease -Version 0.3.0-rc.5 -InstallPrerequisites -SkipAgent claude,codex,grok,agy,kimi`
first with the rtk directory off `PATH` (expect rtk placed and recorded, then
the stop with PATH advice), then with it on `PATH` (expect a full rc.5
install and both handshakes), then `-Uninstall` in each home (expect the
recorded rtk removed once). The same shape belongs in CI on ubuntu and macos.

## Verdict (codex, 2026-10-27)

Accepted, pins `2455ecd3..3f465431`, no reopening finding: one archive
fetcher on every platform with the script route deleted; the RID mapping
matches rtk's release (musl x64 as the portable Linux choice, GNU for
ARM64); tar selection and exit-code check, recursive discovery, checksum
verification before extraction with exact-name matching, `chmod +x` on
Unix, Windows behavior unchanged, and existing `rtk`/`rtk-oss` installs
still preferred through `PATH` or `PTK_RTK_PATH`.
