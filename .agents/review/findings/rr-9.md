# rr-9: rtk's installer script is fetched unpinned

**Severity:** MEDIUM — supply-chain and availability exposure on every Linux
or macOS install that lacks rtk.
**Status:** OPEN — awaiting owner ruling D2 (pin to a tag with a recorded
hash, or keep following `master`); the live route proof is pending an
owner go, because auto mode refused to execute the external script.
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
