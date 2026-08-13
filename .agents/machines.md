# Machine State

Machine-specific, nonportable facts only. Date each verification; prune stale
entries during a `drift` pass.

## `nagatha.local` — mini-SIEM S8 local package proof (2026-08-12)

- Exact implementation head
  `a9576c2b52cff1c99d3e16951352081430c153b3`; macOS 26.6.1 arm64,
  .NET SDK 10.0.302, PowerShell 7.6.3 inside the PTK worker.
- `siem/build-package.ps1` produced the self-contained `osx-arm64` layout
  under the test-only version `0.2.3-rc.1`; this is proof input, not a release
  version ruling. `siem/verify-package.ps1` accepted exact `VERSION`, assembly
  `0.2.3.0`, informational version `0.2.3-rc.1+a9576c2`, executable mode,
  byte-identical operator guide and project/proto licenses, and exit 1 naming
  `PTK_SIEM_CONFIG` with empty stdout when configuration was absent.
- The 343-file layout archived and extracted successfully as a 40,814,996-byte
  tarball with SHA-256
  `57a9f71f4b2932eabdb5a6be9802ad24a1a9d7e5327097fcc6079b2f60eaec50`;
  the verifier passed again against the extracted layout.
- Two independent guard mutations bit before commit: omitting the version
  property failed on assembly `0.2.0.0` versus expected `0.2.3.0`; omitting
  the guide failed on missing `README.md`. Both were restored and the exact
  committed-head package passed.
- Standing verification: Pester 112 passed/3 skipped; server 1,306/1,306;
  SIEM 329/329; registered five-tool handshake passed; server and SIEM
  vulnerable-package audits were clean; PSScriptAnalyzer, `actionlint`, and
  `git diff --check` passed. This bullet is local osx-arm64 evidence only; the
  subsequent bullet records hosted three-OS evidence.
- Hosted follow-up `31650818998` at exact head
  `b94bae5ce2aac239c26c9c497d9a488f840100a6` passed all six jobs. Its SIEM
  matrix ran 329 tests and the native package builder/verifier on
  `ubuntu-latest`, `windows-latest`, and `macos-latest`; the ordinary matrix
  passed Pester, 1,306 server tests, and registered handshake on all three.
  The preceding run `31649960173` is the Windows fail-before proof: idle SQLite
  pools blocked test-root deletion and inherited-ACL export fixtures were
  rejected; both exact failures disappear on the fixed head. Five-RID release
  signing/archive proof remains pending.

### Linux ARM64 package-tool repair proof (2026-08-12)

- Signed-draft run `31654128120` at exact source head
  `0c05a81ee82452f18f16fbf3f25b3ed142554763` reached the native
  `ubuntu-24.04-arm` runner. PTK's `linux-arm64` layout built, but the SIEM
  receiver publish failed when `Grpc.Tools` 2.82.0's bundled
  `tools/linux_arm64/protoc` exited 139 under MSBuild. This exactly reproduced
  the already recorded clean-build caveat later in this file; upstream records
  the regression across 2.69.0-2.82.0 and its fix in 2.83.0 at
  `grpc/grpc#38538`.
- On `nagatha.local`, after raising only the build-time `Grpc.Tools` pin to
  2.83.0, restore and the complete SIEM suite passed 329/329; all three SIEM
  projects reported no vulnerable direct or transitive packages. The shared
  builder produced an `osx-arm64` layout stamped `0.3.0-rc.1`, and the
  independent verifier accepted the required payload, version/provenance,
  guide, and licenses. The temporary package root was removed and
  `git diff --check` passed.
- Exact-head retry `31654609624`
  (`https://github.com/AlsoBeltrix/PowerShell-Token-Killer/actions/runs/31654609624`)
  at `0c8ed87635ef37db548d086ada78a2020c4b390f` passed `linux-x64`,
  `linux-arm64`, `osx-arm64`, `win-x64`, `win-arm64`, and draft assembly.
  Linux ARM64's formerly failing receiver publish completed in seven seconds,
  then its exact package verifier, product proof, archive, and upload passed.
  Both Windows jobs passed Trusted Signing for PTK and receiver payloads before
  their smoke/product/Defender gates. The macOS job passed Developer ID signing
  of both payloads and joint notarization before archive.
- Release ID `369612019`, tag `v0.3.0-rc.1`, title `ptk 0.3.0-rc.1`, was first
  verified as an unpublished draft targeting exact full SHA
  `0c8ed87635ef37db548d086ada78a2020c4b390f`, with no remote tag. It has
  exactly eleven uploaded assets: five PTK archives, five receiver archives,
  and `SHA256SUMS` (1,042 bytes, SHA-256
  `ea694f1285de98c37e8bd881f1ea031fa4a607b4921b3e66b478de11cd301f30`).
  Every manifest entry matched GitHub's independently reported asset digest:
  PTK win-x64 `9d80e2805d03ac77960b08186bf8350b79101a5d639f103d409ac7c35be1e18e`,
  win-arm64 `c57d25c53f07c776c3d124bac19fbc12c9a7ed6a7d928f21c49c67631d273056`,
  linux-x64 `ac05f4f14a090a4d60a6fc17d7e02356f945da1a40739d8714534a29b3d285d7`,
  linux-arm64 `199768af827630df52b37416bca5bd539a416670238bc59618232efcbee8cdce`,
  osx-arm64 `bfdb7eb2b4c7d67dbf88ab2f8ae4faa20dfeefffeb463cfebe551d61d65cbfc1`;
  receiver win-x64 `e6598d7ef1cbb243d690d8a7ef2979b5c033305d1b18cd1483d2df0f6a10f335`,
  win-arm64 `c1da164e0df1609dea71319d1c2c930b0659549004d318c8f70fd8d25d98cb82`,
  linux-x64 `9a7c2c437a73a47c7aaea2d8414332923c450fce19c28f8512749ba8a59e1074`,
  linux-arm64 `a09227ad2a09a9ceb6d30d6cd24b884a5416d1cf79771f5cf13ccce644e32204`,
  osx-arm64 `6f0f042a5a26d56b363587bc3ecd5453726e2279fccafa76fcfe6f542ecbf387`.
- Draft verification initially found `target_commitish=master`; this would let a
  later paperwork push move the eventual tag away from the artifact source.
  The draft was corrected in place to the exact SHA without publication or tag
  creation, and `release.yml` now passes `$GITHUB_SHA` to future draft creation.
- Owner authorization then published the same release as a GitHub prerelease at
  `2026-08-13T01:38:18Z`. Post-publication verification found `draft=false`,
  `prerelease=true`, stable URL
  `https://github.com/AlsoBeltrix/PowerShell-Token-Killer/releases/tag/v0.3.0-rc.1`,
  all eleven uploaded assets unchanged, and all ten `SHA256SUMS` entries still
  matching GitHub's asset digests. Remote lightweight tag `v0.3.0-rc.1`
  resolves directly to `0c8ed87635ef37db548d086ada78a2020c4b390f`.

## `nagatha.local` — mini-SIEM S7 isolated end-to-end smoke (2026-08-12)

_Manual two-host-equivalent acceptance at exact source head
`823386937d7dbe2999cd2b1f6bd12891269e29ac`; macOS 26.6.1 arm64, .NET SDK
10.0.302. Packaging/S8 was not exercised or authorized._

- Receiver and producer were separately self-contained-published for
  `osx-arm64` beneath
  `/Users/michael/.ptk-s7-smoke-9d80d4a26f87483ab29645c2ee97019d/receiver-environment/install/PtkSiemReceiver`
  and
  `/Users/michael/.ptk-s7-smoke-9d80d4a26f87483ab29645c2ee97019d/producer-environment/install/PtkMcpServer`.
  Their SHA-256 values were respectively
  `f40f4b3718283e42e9590b30fc302dddaad259cd1e0143f10f57dd29fec1d301`
  and
  `633dfbef35d950ca2411421ce25269de1cbe652014a8d1dc67cd7b1a18cd8ed7`.
- Each child ran under a macOS `sandbox-exec` profile that denied read and
  write access to the other environment root. An active producer-side probe
  could neither read the receiver config nor create a file in the receiver
  root. This is the plan's equivalently isolated-machine proof, not a claim
  that two physical hosts were used.
- The receiver used a temporary CA and served real HTTPS at
  `https://localhost:49764/v1/logs`; its operator endpoint was loopback HTTP at
  `http://127.0.0.1:49765`. This host's .NET client did not honor a temporary
  `SSL_CERT_FILE` on the first attempt (`export.unreachable`, cursor unchanged),
  so the successful producer sandbox used loopback
  `http://127.0.0.1:49766/v1/logs` through a temporary forwarder that validated
  the receiver certificate with .NET `CustomRootTrust`. The forwarder SHA-256
  was
  `d67f2713bd6643713658edb68edffddaccd78527c59ff11fb78c89b4605e448e`.
  No user or system Keychain/trust setting was changed.
- A real MCP `ptk_invoke` returned marker
  `PTK-S7-90371c809be54301b2160b81185a0414`. For supervisor boot
  `d54c1f7d-b746-406a-a3d6-318f8cb86a77`, the protected producer cursor at
  `producer-environment/audit/export-cursor.json` advanced from sequence 1,
  offset 2506 to sequence 3, offset 8148 only after receiver acceptance.
  `ptk_state` reported healthy `otlp_http`, delivered 3, pending bytes 0.
- Receiver `/` returned 200 and identified PTK SIEM Receiver. Authenticated
  `/api/events` and `/api/events/{id}` returned the resulting
  `call.completed` event `019ff7fe-235c-77ee-b5a2-157b0f1027b5`, sequence 3,
  hash
  `2ac517873966a86524e1ce14749d3082364381113244eb6fa813dcdead9b7997`.
  Custody advanced 0→1, witness sequence reached 3, health stayed true with
  restore pending false, and the independent anchor was configured.
- Both process trees stopped and the exact disposable root above was removed;
  no smoke directory, certificate, token, database, or forwarder remains.

## `nagatha.local` — published `0.3.0-rc.1` SIEM operator acceptance (2026-08-13)

- Downloaded the public osx-arm64 producer and receiver archives from canonical
  GitHub release `v0.3.0-rc.1`. Their SHA-256 values matched published assets:
  producer `bfdb7eb2b4c7d67dbf88ab2f8ae4faa20dfeefffeb463cfebe551d61d65cbfc1`,
  receiver `6f0f042a5a26d56b363587bc3ecd5453726e2279fccafa76fcfe6f542ecbf387`.
  Both report product version `0.3.0-rc.1+0c8ed87`; receiver `codesign --verify
  --strict` passed.
- `siem/verify-package.ps1` rejected the authentic receiver because it expected
  current checkout SHA `d8992e9` instead of packaged source SHA `0c8ed87`.
  Package verification is incorrectly checkout-coupled for older releases.
- Started the released receiver from disposable
  `~/.ptk-siem-live-proof` with protected configuration/storage/witness/anchor,
  HTTPS token ingest on 19418, and loopback operator HTTP on 19443. It accepted
  the producer-owned v2 golden request, stored three events and one intact
  chain, produced three alerts, and reported healthy custody with no gaps or
  quarantine.
- Started the released PTK producer from its public archive against an isolated
  audit/output root. Its direct HTTPS export did not trust the disposable CA
  through `SSL_CERT_FILE`, matching the prior S7 smoke. A disposable loopback
  forwarder on 19466 validated receiver TLS with that CA. No user/system trust
  store changed.
- Through real MCP stdio, client `ptk-live-siem-proof` invoked exact marker
  `PTK-LIVE-SIEM-PROOF-20260813`. The released producer returned the marker;
  the receiver stored boot `954d66dd-5201-4565-9a75-bb85cb232dd8` sequences
  1-3 (`server.started`, `call.accepted`, `call.completed`), with terminal event
  `019ffbe6-dbd5-70f5-a9c1-4910bf583d87`, outcome `completed`, duration 680 ms,
  and client-asserted name/version `ptk-live-siem-proof`/`0.3.0-rc.1`.
  Producer export cursor advanced through sequence 3; receiver custody remained
  healthy.
- Operator API detail exposes client identity, tool/action, outcome, duration,
  and evidence ID. The embedded dashboard exposes none of the actor fields,
  has no clickable/detail rows, and cannot show the command because exact
  script bytes remain only in the producer evidence store. The UI therefore
  cannot answer which model did what; model identity is not supplied by MCP.
- Published receiver and TLS-validating forwarder remain running for owner
  inspection at `http://127.0.0.1:19443/`. Durable proof summary is
  `~/.ptk-siem-live-proof/LIVE-PROOF.json`; the installed PTK process/config was
  not changed or restarted. This is a disposable non-anchored evaluation, not
  production deployment.

## `nagatha.local` — SIEM operator-readiness S0 executable gate (2026-08-13)

- Ran `siem/operator-readiness-acceptance.ps1` against the preserved authentic
  public `0.3.0-rc.1` osx-arm64 producer/receiver archives and
  `~/.ptk-siem-live-proof/LIVE-PROOF.json`. The script extracted both archives
  beneath a fresh isolated home and used no checkout-built product binary.
- Eight requirements passed: both published SHA-256 values, fresh isolation,
  producer and receiver release/source identity, separately deployed
  mini-SIEM, and observation release/archive provenance.
- Twenty-three named requirements failed: public setup without the documented
  TLS-validating forwarder gap; exact-one and zero-hidden-destination proof;
  correlated activity plus client/agent/model/provenance/context/exact
  command/complete response/outcome/chain fields; dashboard activity fields,
  drill-down, and system-event separation; alert/gap/quarantine/custody use;
  restart durability; real-product external-SIEM acceptance; and explicit
  multiple-destination independent failure/replay accounting. This is the
  expected `0.3.0-rc.1` failure, not a backend regression.
- The authentic receiver verified with release source `0c8ed87` and the full
  `0c8ed87635ef37db548d086ada78a2020c4b390f`; it rejected current checkout
  source `dc246de`. A deliberate verifier regression that read checkout
  `HEAD` made `siem/test-verify-package.ps1` fail because the deliberately
  wrong supplied source commit was accepted. Restoring release-bound identity
  made the guard pass.

- Final S0 verification: SIEM 330/330; server 1,310/1,310; PowerShell
  112 passed, 3 platform-skipped; registered-command handshake passed;
  producer and SIEM dependency scans reported no vulnerable packages; rebuilt
  osx-arm64 receiver package and release-bound verifier guard passed;
  `git diff --check` passed. The first server run from the PTK MCP worker had
  the documented narrowed-`PSModulePath` artifact: exactly four
  `StateToolTests` failed to find `Microsoft.PowerShell.Utility` while 1,306
  passed. With only that inherited variable removed for the child process, all
  15 focused state tests and then the full 1,310-test suite passed.

## `10.1.10.173` — Michael's Windows x64 dev machine (added 2026-08-07)

Repo lives at `F:\dev\PowerShell-Token-Killer`. Reachable as
`ssh michael@10.1.10.173` (key auth); **`scp` needs `-O`**, same as the
ARM VM.

- Windows x64 (AMD64), 10.0.26200.0. PowerShell 7 is a **normal MSI
  install** at `c:\program files\powershell\7\pwsh.exe` — no MSIX package,
  so GitHub #40 does not apply here.
- rtk is winget-installed and reached through a **0-byte symlink shim** at
  `~\AppData\Local\Microsoft\WinGet\Links\rtk.exe` → the real 9.1 MB binary
  under `...\WinGet\Packages\rtk-ai.rtk_Microsoft.Winget.Source_*\rtk.exe`
  (rtk 0.44.2; `hook check` answers correctly when invoked on the target).
- State as of 2026-08-07: installed payload `0.2.0-dev.gecd3a4c`, 296 bin
  files, server present, no stale `ptk-stage-*`/`ptk-snapshot-*` in TEMP,
  538 GB free on C:. ("No running processes" held only for that SSH probe:
  whenever a harness session on the box has ptk connected, its
  `PtkMcpServer` supervisor+worker are alive and `scripts/install.ps1`
  refuses to install — reproduced locally 2026-08-07; write-up in
  `.agents/state.md` §Now.)
- Local interactive shells are non-elevated as expected (`elevated=False`
  as `NETWATCH-01\michael`). This host *is* `NETWATCH-01` —
  `netwatch-01.internal` resolves to this address — so the separate
  `NETWATCH-01` section below describes the same machine.
- The install failure's second leg was product, not host: the v0.2.1 RTK
  startup gate rejected the winget symlink shim (link-length-zero bound,
  fixed in-repo — `.agents/state.md` §Now). The shim itself is healthy
  locally: `rtk --version` returns 0.44.2 through the link, whose target
  `…\WinGet\Packages\rtk-ai.rtk_…\rtk.exe` is a real 9.1 MB file.

**Two SSH-only blockers make this host unusable for reproducing installs
remotely — both are artifacts of the access path, not product defects:**

1. **SSH sessions land elevated** (High Mandatory Level,
   `IsInRole(Administrator)` true), and `scripts/install.ps1:154` refuses to
   run elevated by design. An interactive local shell gets a filtered token
   and is unaffected.
2. **`fsutil behavior query SymlinkEvaluation` reports
   `Remote-to-local: DISABLED`** (the Windows default), so the winget rtk
   *shim* cannot be traversed from an SSH session: `&` fails with
   `StandardOutputEncoding is only supported when standard output is
   redirected` and `Start-Process` with `The path cannot be traversed
   because it contains an untrusted mount point`. Proved to be the symlink
   hop and not rtk: `git --version` succeeds in the same session with the
   same redirection, and rtk's **real target** path runs fine
   (`rtk 0.44.2`, `hook check` → `rtk git status --short`).

To drive an install here, run it in a local interactive shell on the box,
or set `PTK_RTK_PATH` to rtk's real target to sidestep blocker 2 — blocker
1 has no remote workaround short of a scheduled task with a filtered token.

## `10.1.10.212` — Michael's Windows 11 ARM64 VM (added 2026-08-07)

The bench that closed the Windows half of GitHub #40. Reachable from the Mac
as `ssh michael@10.1.10.212` (key auth, no password prompt); git works there
too. **`scp` needs `-O`** — the default SFTP protocol fails against this
host's OpenSSH with `write remote ... Failure`, while legacy-protocol scp
succeeds.

- Windows 11 ARM64, 10.0.26100.0. `whoami` is `michael`; ssh sessions land
  elevated (High Mandatory Level, `IsInRole(Administrator)` true).
- **PowerShell 7.6.4 is MSIX-packaged
  (`Microsoft.PowerShell_7.6.4.0_arm64__8wekyb3d8bbwe`) — installed by
  `winget`, not the Store.** `winget list` reports source `winget`, and
  `winget show Microsoft.PowerShell` on this host resolves to
  `Installer Type: msix` (`PowerShell-7.6.4.msixbundle`);
  `--installer-type msi` is refused with `No applicable installer found`,
  even though upstream publishes `PowerShell-7.6.4-win-arm64.msi`. This is
  the whole reason the host reproduces #40, and it means **ARM64's default
  winget install path reproduces it** — the owner's x64 machines, installed
  by the same command, get the MSI and do not. `pwsh` resolves to
  `...\AppData\Local\Microsoft\WindowsApps\pwsh.exe`.
- Default ssh shell is `cmd.exe` (no `DefaultShell` registry value), so
  remote commands are cmd syntax unless `pwsh -File` is invoked explicitly.
- Bench layout under `C:\Users\michael\ptk40`: the published `v0.2.1`
  win-arm64 artifact under `ptk\`, rtk 0.45.0 in `~\.local\bin`. Installing
  and running that artifact here doubles as end-user validation of the
  signed win-arm64 build.
- The packaged layout ships **no `$PSHOME\Modules` directory** — worker
  module resolution depends entirely on the inherited `PSModulePath`.

## `nagatha.local` — Michael's Mac

_Installed payload re-verified 2026-08-05 with the repo at `78b2dbb`;
read-only inventory, no installation or registration change._

- The live server runs from `~/.ptk/bin/PtkMcpServer`; installed version is
  `0.2.0-dev.g33806a0`, 123,944 bytes, SHA-256
  `633dfbef35d950ca2411421ce25269de1cbe652014a8d1dc67cd7b1a18cd8ed7`,
  with five supervisor processes live from that exact path.
- That installation serves the five-tool named-session surface — this session
  drove `ptk_invoke`, `ptk_session`, and `ptk_output` against it. The host is
  no longer on the pre-candidate job API.
- The installed payload is still behind the repo head; it is a separate
  artifact from the checkout under test and is not evidence for any checkout
  commit.
- The 2026-07-28 `0.2.0-dev.g6db333c` rollback-baseline inventory (installed
  file/version hashes, the 564-file managed-path digest, and the codex
  `config.toml` PTK-section hash) described a payload that is no longer
  installed. Pruned 2026-08-05 as superseded.
- On 2026-07-11 at plan base `2a83723`, disposable Darwin fork/pipe probes
  proved the audited-session Unix topology at the mid-creation, armed/gated,
  and released-with-descendant barriers. The broker observed supervisor
  liveness EOF, prevented pre-gate work, terminated each process group, and
  left no survivor. This proves the Darwin primitives/topology, not the later
  .NET implementation.

### Audited-harness Slice 1 checkout validation

_Verified 2026-07-11 at `460c106`; this was checkout validation, not an
installed-payload update._

- The full .NET suite passed 371/371; the PowerShell module suite passed
  134/136 with two Windows-only skips; the stdio handshake passed, including
  boundary attribution, exact evidence references, core-event secrecy, and
  the fail-closed audit-outage path. The handshake build had zero warnings.
- Real Darwin extended-ACL sabotage proved the owner-mode check alone was
  insufficient. The final storage implementation strips an existing extended
  ACL and rejects post-creation ACL reintroduction; both guards were proved
  red before the fix and green after it.

### Audited-harness Slice 2 checkout validation

_Verified 2026-07-12 at final reviewed code head `3d3739a`; this was checkout
validation, not an installed-payload update._

- The exact final head passed 927/927 .NET tests. The PowerShell module suite
  passed 134/136 with its two Windows-only skips, and the stdio handshake
  passed with a zero-warning build. The separately built audit-administration
  executable returned its exact usage contract and expected exit code 2.

- At `8470b4b`, the full .NET suite passed 390/390, the PowerShell module suite
  passed 134/136 with its two Windows-only skips, and the stdio handshake
  passed with a zero-warning build.
- At `5238984`, the full .NET suite passed 402/402. The export identity has an
  independent literal byte-vector guard, forced eight-publisher collision
  coverage, strict-UTF-8 rejection, corrupt/missing-key refusal, and
  crash-temp/link guards; each new behavior was proved red before restoration.
- At `eb0060f`, the full .NET suite passed 455/455. Strict configuration,
  read-only protection checks, endpoint noncanonicalization, and Schannel PEM
  client-certificate loading passed focused red→green guards.
- At `815a3f1`, the full .NET suite passed 464/464, the PowerShell module suite
  passed 134/136 with its two Windows-only skips, and the stdio handshake
  passed with a zero-warning build. Atomic replacement guards rejected
  delete-gap-move publication, post-commit destination deletion, and removed
  owner-only protection checks.
- At `93ebb4c`, the full .NET suite passed 536/536, the PowerShell module suite
  passed 134/136 with its two Windows-only skips, and the stdio handshake
  passed with a zero-warning build. At `56e30b0`, the full .NET suite passed
  543/543 with the same PowerShell/handshake battery; at `9651941`, it passed
  552/552 with that battery again.
- At `480713d`, the focused `FileAuditJournalSinkTests` suite passed 35/35;
  crash-safe macOS compaction guards covered publication, clone/copy failure,
  missing paths, allocation metadata, and repeated startup.
- At `0684af5`, the stdio handshake passed with a zero-warning build after the
  shared strict spool-record codec landed. At `1ce5900`, the exact-head full
  .NET suite passed 587/587. Later focused record-validation guards rejected
  malformed prior hashes, noncanonical boot IDs, and empty intermediate
  segments.

### Audited-harness Slice 3 checkout validation

_Verified 2026-07-13 at final integrated head `b78d9c6` in Claude's disposable
worktree; this was checkout validation, not an installed-payload update._

- The exact head passed 1,030/1,030 .NET tests, 139 PowerShell module tests
  with two platform skips, and the stdio handshake with a zero-warning build.
- Claude independently proved five integrated guard classes load-bearing:
  durable dispatch, preference-independent RTK capture, RTK provenance/bounds,
  AST fidelity, and Bash validation ordering. Every mutation failed for its
  intended reason, restored byte-exactly, and passed afterward.
- The clean disposable worktree and all proof artifacts were removed; an
  independent probe found no local residue and the coder tree remained clean.
- This checkout battery does not replace the later installed/OS-protected live
  validation needed for executable check/start races, dynamic dependencies,
  ACL/xattr mutation, and other properties a source snapshot cannot bind.

### Audited-harness Slice 4 checkout validation

_Verified 2026-07-13 at final integrated product head `76d4f0c` in Claude's
disposable worktree; this was checkout validation, not an installed-payload
update._

- The exact restored head passed 1,085/1,085 .NET tests, 141 PowerShell module
  tests with two platform skips, and the stdio handshake.
- Claude independently proved eight integrated guard classes load-bearing:
  supervisor-lifetime handles, audited pre-disclosure reads, anonymous
  path-substitute safety, raw-invariant capture, exactly-once execution,
  in-marker recovery hints, honest seam-absent RTK output, and raw-independent
  foreground/background dialect routing. Every mutation failed for its
  intended reason, restored byte-exactly, and passed afterward.
- The private-output stop/join test missed its fixed two-second scheduling wait
  once in a parallel full suite after its timeout assertions had passed. Its
  later post-shutdown assertions stayed green in 8/8 isolated runs and 3/3
  isolated runs under deliberate CPU load; four full-suite confirmations were
  clean. This is recorded as a non-blocking test timing/CI-flake risk, not a
  production failure.
- Two additional full-suite processes parked idle in managed waits beyond five
  minutes and were terminated; immediate exact-tree retries completed green.
  This host-specific runner instability is not a passing result and does not
  replace the clean batteries above.
- The clean review worktree and temporary prompt were removed; no installed
  payload or persistent host configuration changed. Live Windows validation
  of Slice 4 remains a later platform gate.

### MCP resilience R0 contract and containment validation

_Verified 2026-07-15 at exact corrected code head
`46ff7fb96d2b9947576e9f22627665ac763459d2`; this was checkout validation,
not an installed-payload update._

- The host reported macOS 26.5.2 build 25F84 on ARM64, Apple clang 21.0.0
  targeting `arm64-apple-darwin25.5.0`, .NET SDK 10.0.301, and PowerShell
  7.6.3.
- The native Darwin guardian-broker integration set passed 11/11. It exercised
  the pending/armed/released process-group boundaries, guardian-liveness EOF,
  identity-held containment, direct-host reaping, and the macOS prohibition on
  waiting for nonchildren.
- The exact head passed 1,531/1,531 .NET tests in 2m23s, 141 PowerShell tests
  with two platform skips, and the complete stdio handshake with a zero-warning,
  zero-error build. The focused fake guardian/private protocol/R0 contract set
  also passed 34/34.
- One separate exact-head full run observed a transient pre-existing
  `AuditAnchoredRuntimeTests` assertion while an `.anchoring.*.script` rename
  was still in flight. Its isolated guard passed 10/10 and the clean full rerun
  above passed; this is not counted as a passing run or as R0 behavior evidence.

### Production-reliability salvage Slice 2 checkout validation

_Verified 2026-07-27 against exact candidate tree
`faf8c2338a1a729fc5c46d87376475265148a5b0`; this was checkout validation,
not an installed-payload update._

- The complete server suite passed 1,250/1,250 twice; Pester passed 141 tests
  with two platform skips; the standalone SIEM suite passed 247/247; and the
  full stdio handshake passed, including audit-disabled execution, an
  unwritable audit root, output recovery, and cleanup.
- Clean static inventory found no removed OTLP producer symbol, anchored export
  loop, runtime `Grpc.Tools`, runtime `Google.Protobuf`, or server protobuf
  artifact. The runtime package boundary test was proved red before the
  installer correction and green after restoration.
- Deliberately reintroducing startup access to `PTK_AUDIT_ROOT` made the
  focused unwritable-root integration test fail because the server exited.
  Restoring `Program.cs` byte-exactly to SHA-256
  `890598568aafc8a5df15582f3580c357da8a69f5f5e2f9df8666d6ed6ab15063`
  made the test pass.
- Repository-wide formatting still reports pre-existing whitespace only in
  `OutputStoreTests.cs` and `AuditAdminOperations.cs`; neither unrelated file
  was rewritten. Known `System.Security.Cryptography.Xml` advisories remain.

### Production-reliability salvage Slice 3 checkout validation

_Verified 2026-07-27 at exact code head
`bfa335ec223609df9ce0dbfcfd9efe99382203d4`, tree
`49bdb5822e4a37bf7cee302854451fb273eaae12`; this was checkout validation,
not an installed-payload update._

- The connected installed PTK MCP transport returned `Transport closed` before
  the run, including for `ptk_state`, so repo guidance selected the normal
  shell fallback. The Slice 3 candidate was not installed and this pre-run
  transport failure is not attributed to it; it remains evidence for the
  already-open stale/dead-client-transport reliability boundary.
- The first focused run caught invalid worker-owned terminal data being
  misclassified as supervisor protocol input. After correction, the focused
  116-test worker set passed three consecutive runs. A later full run exposed
  and corrected a test-only assumption that concurrent invoke/state responses
  must be globally ordered; correlation is strict, cross-request completion
  order is deliberately not.
- Deliberate regressions independently made the exact message-union,
  no-background, artifact-seal length, capacity, shutdown-correlation,
  warm-state, cross-session routing, and unsolicited-terminal guards fail.
  Every production file was restored to its pre-mutation SHA-256 before the
  final battery.
- The final server suite passed 1,223/1,223 in 5m05s. Pester passed 141 tests
  with two platform skips, SIEM passed 247/247, the scoped formatter and
  `git diff --check` passed, and the unchanged public five-tool stdio handshake
  passed. The known five `System.Security.Cryptography.Xml` advisories remain.

## `magneto` — Michael's Linux x86_64 host

_Verified 2026-07-27 against exact candidate tree
`faf8c2338a1a729fc5c46d87376475265148a5b0`; this was a disposable
SSH-based checkout validation, not an installed-payload update._

- The host is x86_64 with .NET SDK 10.0.110 and PowerShell 7.6.3. `gabrielle`
  and `altiera` were also confirmed x86_64, but the full battery used
  `magneto`.
- The transferred archive matched SHA-256
  `cd54244720384c08422353b5ce93fcf0b56a738e9c913f24d7639e9843d94f0a`.
  Clean restore/build produced no runtime protobuf or gRPC artifact.
- The server suite passed 1,250/1,250; Pester 6.0.1, loaded only into the
  disposable validation environment, passed 141 tests with two skips; the
  SIEM suite passed 247/247; and the stdio handshake passed.
- Local and remote disposable checkouts were removed. No installed payload or
  persistent host configuration changed. ARM64 Linux remains untested, and
  UTM is not an approved validation route.

### Production-reliability salvage Slice 3 checkout validation

_Verified 2026-07-27 against exact commit
`bfa335ec223609df9ce0dbfcfd9efe99382203d4`; this was a disposable SSH-based
checkout validation, not an installed-payload update._

- The transferred `git archive` matched SHA-256
  `938103827f6161d3d9dc23da6eac70b9923f982e2b444da5b3b27eb0b718cd03`
  locally and remotely. The host reported x86_64 and .NET SDK 10.0.110.
- A clean restore/build and the complete server suite passed 1,223/1,223 in
  3m13s. Pester 6.0.1, saved and imported only under the disposable test root,
  passed 141 tests with two skips; SIEM passed 247/247; and the complete public
  stdio handshake passed.
- The only restore/build warnings were the five already-recorded transitive
  `System.Security.Cryptography.Xml` advisories. The local archive and remote
  checkout, temporary Pester module, and build products were removed. No
  installed payload or persistent host configuration changed. ARM64 Linux
  remains untested, and UTM is not an approved validation route.

## `NETWATCH-01` — Michael's Windows machine

_Verified 2026-07-11 for audited-session slice 0 at repo base `2a83723`._

- SSH ran as ordinary identity `NETWATCH-01\michael` on Windows NT
  10.0.26200.0 x64, Windows PowerShell 5.1.26100.8737, and .NET SDK 10.0.301
  / runtime 10.0.9. The probe did not read or modify the existing `F:\dev`
  repositories.
- A disposable native probe used
  `CreateProcessW`/`STARTUPINFOEX`/`PROC_THREAD_ATTRIBUTE_JOB_LIST`, queried
  back exact `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (`0x2000`), and proved
  specific worker membership before `ResumeThread`. Its source SHA-256 was
  `ace621f6a2f27bc2a634d4d54c43150aa6c6df1ad93efd95d2a9b025616aeb4c`;
  the executed assembly SHA-256 was
  `89d7f134667257e603d4bf32789bbbe91f1800ed7d88ca7eaf1bf7f6bab39b90`.
- The gated case proved no workload signal before release and worker death
  after raw supervisor-only termination. The released case proved an ordinary
  descendant was alive and in the same exact job, then both held process
  handles signaled after the supervisor's sole noninherited job handle closed.
  The probe contained no `AssignProcessToJobObject` or `TerminateJobObject`
  symbol/path.
- No `PtkWindowsJobProbe` process existed before or after the final run. Its
  temporary directory was removed, and no persistent host configuration was
  changed.

### Audited-harness Slice 1 checkout validation

_Verified 2026-07-11 at `460c106` in a disposable copy under `F:\dev`; the
installed Windows payload was not changed._

- The full .NET suite passed 371/371; the PowerShell module suite passed
  136/136; the stdio handshake passed with the same audit/evidence/outage
  checks as the Mac run and a zero-warning build.
- Windows-specific journal tests verified that protected directories and
  files are owned by the current SID, have inheritance disabled, and expose a
  single current-user FullControl allow rule. Alternate-owner sabotage failed
  closed.
- Live command-resolution probes exposed extensionless PATHEXT misses and a
  `.ps1`/`.exe` precedence error in the first correction. The final C# and
  PowerShell guards each failed under an Application-only sabotage and passed
  after restoring joint ExternalScript/Application enumeration. The updated
  source hashes matched the Mac checkout and the validation tree contained no
  AppleDouble files.
- All four disposable validation directories and their four transfer archives
  were removed from `F:\dev` after local landing; no installed payload or
  persistent host configuration was changed.

### Audited-harness Slice 2 checkout validation

_Verified 2026-07-12 at final reviewed code head `3d3739a` in disposable copies
under `F:\dev`; the installed Windows payload and existing repositories were
not changed._

- The exact final head passed 927/927 .NET tests, 136/136 PowerShell module
  tests, and the stdio handshake with a zero-warning build. A separate
  disposable checkout built the audit-administration executable and confirmed
  its exact usage contract and expected exit code 2.
- The corrected checkpoint durability branch independently passed its focused
  four-test Windows guard, the full suite, a disabled-retry red mutation, and a
  post-rename-flush red mutation. Claude's accepted one-finding re-review also
  ran the focused guards plus ten repeated concurrency iterations.
- Every transfer bundle and disposable validation directory was removed after
  use. Access used `10.1.10.177` with the pinned SSH host alias
  `netwatch-01`; no existing checkout or installed payload was modified.

- The full .NET suite passed 390/390; the PowerShell module suite passed
  136/136; the stdio handshake passed with a zero-warning build.
- The transfer archive and disposable validation directory were removed after
  the run.
- At `eb0060f`, the full .NET suite passed 455/455. Focused configuration/ACL
  and Schannel mTLS guards also passed. The exact Windows Pester and handshake
  checks were not rerun at that historical commit.

### Audited-harness Slice 3 checkout validation

_Verified 2026-07-12 at fixed code head `669ce6e` in a disposable copy under
`F:\dev`; the installed Windows payload and existing repositories were not
changed._

- A `git archive` ZIP of the exact code head was transferred with strict host
  key checking and verified on Windows at SHA-256
  `d472a9a6e64b1dfc57168722a62e52cc25a05468a52c740e0b4a3d955e77f0bf`.
- The full .NET suite passed 1,010/1,010; the PowerShell module suite passed
  140 tests with one skip; and the stdio handshake passed with a zero-warning
  build.
- The passing .NET count is not live Windows Bash coverage where `/bin/bash`
  is absent; the Darwin battery exercises the real Bash leg. Full live Windows
  session checks remain the plan's later validation slice.
- The GUID-named validation directory, uploaded archive, and local transfer
  archive were all removed after the battery. No persistent configuration or
  installed payload changed.

#### Failed checkout validation — 2026-07-13

Code head `40923784601bf8063d9461188b04be3940374c7d` is **not verified on
Windows**. An exact `git archive` ZIP was uploaded to a collision-checked
GUID-named path under `F:\dev`; the local and Windows copies matched SHA-256
`CE5707231353BABCBA096E90076513B9532A7C0B1FE9C64F337A474D8110FF2E` before
expansion.

- `dotnet.exe test server/PtkMcpServer.slnx` passed 1,020/1,030 and failed the
  following ten tests:
  - `RtkProcessRunnerTests.Deadline_stops_the_started_root_without_retrying_it`
    — the expected `starts.txt` marker did not exist.
  - `RtkProcessRunnerTests.Direct_capture_preserves_stdout_stderr_and_nonzero_exit`
    — captured stderr was `DIRECT_STDERR  ` rather than `DIRECT_STDERR`.
  - `InvokeToolTests.Single_native_command_routes_through_rtk`
  - `InvokeToolTests.Relative_rtk_override_is_bound_before_the_warm_cwd_changes`
  - `InvokeToolTests.Rtk_routed_output_is_not_shaped_by_rtk_a_second_time`
  - `InvokeToolTests.Authorization_observes_exact_rtk_preparation_before_exit_reset_and_execution`
  - `InvokeToolTests.Rtk_capture_ignores_warm_native_error_preferences_and_preserves_error_state`
  - `InvokeToolTests.Operator_pinned_rtk_identity_survives_warm_environment_poisoning`
  - `InvokeToolTests.Rtk_install_nag_is_filtered_but_real_stderr_survives`
  - `InvokeToolTests.Routed_command_exit_code_is_reported`
- The eight `InvokeToolTests` failures all observed empty/no routed output from
  the new Windows RTK fixture. Pester and the stdio handshake did not run after
  the required .NET gate failed.
- A preliminary validation-harness attempt stopped before expansion because it
  used an unsupported `New-Item -LiteralPath` parameter; its `finally` cleanup
  succeeded. The corrected harness produced the test evidence above.
- The failed run's disposable checkout, uploaded archive, and local transfer
  archive were confirmed removed. Existing repositories, installed payloads,
  and persistent host configuration were unchanged.

#### Corrective checkout validation — 2026-07-13

_Verified at exact corrective head
`c100ba199d9854f7171733d9950b26e2a8a397ab` in a disposable copy under
`F:\dev`; the installed Windows payload and existing repositories were not
changed._

- A `git archive` ZIP of the exact head was transferred after collision checks;
  the local and Windows copies matched SHA-256
  `76F027844CC53919B8D2FCEE526940F9196A684337AA3BBFB0E798C5B67BF5A3`.
- Removing only the fixture's explicit stdout/stderr forwarding made exactly
  the eight RTK-route integration guards fail: 1,022/1,030 passed. The script
  required three named failures, restored `Program.cs` byte-exactly, verified
  its SHA-256, and reran the same full suite successfully at 1,030/1,030.
- The PowerShell module suite passed 140 tests with one platform skip. The
  stdio handshake passed with a zero-warning, zero-error build.
- The GUID-named checkout, uploaded archive, and local transfer archive were
  confirmed removed. Existing repositories, installed payloads, and
  persistent configuration were unchanged. The checkout proof still does not
  replace later installed/OS-protected live validation for the recorded
  check/start and dependency caveats.
- Claude independently repeated the exact-head proof at review head
  `64eb767a826da0c8177d9fcdd2fa1ea7033a1d73` from archive SHA-256
  `C3949B5474FE9427BBFEEE2767F95C8C6FCD7900F11524635CEB2028AC2D5874`.
  The same eight-test mutation failed and byte-exact restoration passed the
  full Windows battery. The reviewer corrected one no-effect cleanup-command
  quoting error, then it and the coder independently confirmed zero remaining
  `ptkrev-*` artifacts under `F:\dev`; no persistent host state changed.
- The final integrated head `b78d9c6f176cb42771f823a6b1bdc2b3e6561f07`
  passed a separately hashed exact-current-head Windows checkout at SHA-256
  `8C547113682979E02F0CF0FA0C05A722DEBCC8A5CB388645FF1415C1F4B5DA90`:
  1,030/1,030 .NET, 140 Pester with one skip, and the zero-warning handshake.
  `src/`, `server/`, and `tests/` are byte-identical from corrective head
  `c100ba1` through the final head, so the independent forwarding red-to-green
  proof covers this final code tree. All review archives, scripts, and the
  remote checkout were removed; independent probes found no residue.

### CI flake stabilization validation — 2026-07-14

_Verified in the existing clean `F:\dev\PowerShell-Token-Killer` checkout at
exact follow-up head `d30bbf3701c484aeb81ab59616f6aa074687e95c`._

- The host reported Windows NT 10.0.26200.0, PowerShell 7.6.3, and .NET SDK
  10.0.301. The checkout was fast-forwarded from `00e74d2` with the owner's
  explicit permission; the installed PTK payload was not changed.
- At the old head, the singleton audit accessor race reproduced on the first
  focused run. A deterministic first-two-handler barrier admitted only one
  handler under the singleton mutation; restored scoped ownership passed
  10/10 with all original recovery, event-count, and shutdown assertions.
- A deterministic 1.5-second private-output opening delay made the old setup
  warm-up budget fail before the named invariant. The explicit setup-only
  budget passed the same delay. Removing the production `StopCompleted` join
  then made the stabilized test fail; exact restoration passed.
- The final direct battery passed 1,207/1,207 .NET tests, 142 Pester tests with
  one platform skip, and the complete stdio handshake with a zero-warning,
  zero-error build. The checkout was clean at the exact head afterward.

#### Final-tip scheduling follow-up — 2026-07-14

_Verified from the existing `F:\dev\PowerShell-Token-Killer` checkout using
the exact stable server patch from `d30bbf3..6193129`; local and Windows patch
IDs matched `ce3374eb1781fd1d3a94770214e5df4c606a7522`, and only the three
intended test files differed._

- The host reported Windows 11 Pro 10.0.26200, PowerShell 7.6.3, .NET SDK
  10.0.301, and Pester 5.8.0. `git diff --check` passed and no untracked files
  were present.
- The three corrected focused tests passed together. The complete battery then
  passed 1,207/1,207 .NET tests in 1m32s, 142 Pester tests with one expected
  platform skip, and the full stdio handshake with zero build warnings and
  errors.

#### Concurrent evidence recovery scoping — 2026-07-14

_Verified from the clean `F:\dev\PowerShell-Token-Killer` checkout by applying
the exact stable server patch from `24c7958..6193ae4`; local and Windows patch
IDs matched `a2a198e539ce0c6a494e42508751e50bf3e7116a`, and only
`AuditRuntimeGateTests.cs` differed._

- Temporarily changing the target fixture from scoped back to singleton made
  its deterministic overlap guard fail after twenty seconds with exactly one
  of the first two handlers admitted. Exact restoration passed the focused
  test 10/10 at roughly 439-445 ms each.
- The complete battery passed 1,207/1,207 .NET tests in 1m32s, 142 Pester tests
  with one expected platform skip, and the full stdio handshake with zero
  build warnings and errors. `git diff --check` passed and no unrelated or
  untracked files were present.

### Audited-harness Slice 7d Windows containment validation

_Verified 2026-07-14 for exact code tree `bbc2a0e2b116280ceebaab5442014286671aefe2`
in disposable GUID-named directories under `F:\dev`; no installed payload or
existing checkout changed._

- The final 274-file archive matched SHA-256
  `1F521058547342CC3C9F0798FEF77F09A751D6797C4A6CFC313CABD834068EF9`
  before expansion and contained no AppleDouble sidecars. The focused Windows
  launcher/containment set passed 29/29, the complete .NET suite passed
  1,309/1,309, Pester passed 142 with one expected platform skip, and the full
  stdio handshake passed with zero build warnings or errors.
- The live runnable fixture proved the production mode is not suspended, NUL
  stdin and private request/event plus diagnostic pipes are mapped, Unicode
  quoting and the caller-frozen environment survive while a post-freeze ambient
  variable does not leak. The proof-mode fixture observed no marker before
  exact-job membership verification and explicit resume; the returned owner
  survived forced GC, then closing its sole job handle killed both worker and
  ordinary descendant while independent process witnesses remained open.
- A separate archive with SHA-256
  `ACBE3C61921F32C7B6567DE84D19F7B66630C5FE5A187B4C2E404B5C1D0CC405`
  changed only runnable flags to include `CREATE_SUSPENDED`. The live runnable
  guard failed at its intended fifteen-second read checkpoint. Cleanup found no
  matching process residue; the local production file was restored to its
  pre-mutation SHA-256 before the final archive and green runs.
- One preliminary transfer was stopped before execution when macOS archive
  metadata doubled the extracted file count. It and every later mutation/final
  archive, disposable directory, and matching process were removed. No
  persistent host configuration changed.

#### Owning-wait prerequisite follow-up — 2026-07-14

_Verified for exact code tree `d1cca1b5fc69be61c7102843ab3ceb645cd362eb`
in a disposable GUID-named directory under `F:\dev`; no installed payload or
existing checkout changed._

- The 274-file exact-head archive matched SHA-256
  `7D60831481B56D72D5640D6C01F5EB548AB7ABE9EB98490628A6632179AD434A`
  before expansion.
- The focused Windows launcher/containment set passed 29/29, the complete .NET
  suite passed 1,309/1,309, Pester passed 142 tests with one expected platform
  skip, and the stdio handshake passed with a zero-warning, zero-error build.
- The live case canceled its sole outstanding native wait while the independent
  process witness remained alive, disposed the contained owner before awaiting
  the canceled task, then observed cancellation and worker exit.
- The final residue check found zero matching processes. The remote checkout
  and archive plus the local transfer archive were removed.

### Audited-harness Slice 7e Windows worker-entry validation

_Verified 2026-07-14 for exact code tree
`12617ccb25a3f3ff8d9690d94ebe5cea4f141ee6` in a disposable GUID-named
directory under `F:\dev`; no installed PTK payload or existing checkout
changed._

- The exact-tree archive matched SHA-256
  `CF6CCEF45D7E28682FF1FA649E5366B1981CFFBFB0D2E877662B87D7EDF595E2`,
  contained 319 ZIP entries representing 284 source files, and contained no
  AppleDouble sidecars.
- A forced-build mutation disabling the early `Program` worker branch
  discovered exactly one live lifecycle test and failed it at the first hello
  `Assert.NotNull` in 254 ms. An earlier unforced mutation attempt returned
  green and was rejected as vacuous; it contributed no proof and cleaned its
  disposable paths before the forced-build rerun.
- Exact restoration and rebuild passed 125/125 focused worker/bootstrap/
  containment guards, 1,432/1,432 .NET tests, 142 Pester tests with one
  expected platform skip under `pwsh -NoProfile`, and the full stdio handshake
  with zero build warnings or errors.
- Post-validation comparison found zero source-file mismatches and zero
  relevant `dotnet`, `testhost`, or PTK worker process residue. The remote
  archive and directory plus the local transfer archive were removed.
- A prior profile-bearing SSH shell could not run the canonical Pester command
  because PSProfile defines `source` as `Import-PythonVenv`; the required
  no-profile run passed. During the earlier candidate validation, the host's
  first .NET invocation also reported installing an ASP.NET Core HTTPS
  development certificate. It was not removed because certificate-wide
  cleanup could affect unrelated host state.

### MCP resilience R0 contract and containment validation

_Verified 2026-07-15 at exact corrected code head
`46ff7fb96d2b9947576e9f22627665ac763459d2` in a disposable GUID-named
directory under `F:\dev`; no installed payload or existing checkout changed._

- The host reported Windows NT 10.0.26200.0 x64, .NET SDK 10.0.302, and
  PowerShell 7.6.3. The exact 366-entry archive had SHA-256
  `F785AC44B435BB9073DD1B486E2B28FFD066C569C329A3083499B8174F3D6EC0`
  and contained no AppleDouble metadata.
- The real nested Job Object proof passed 2/2. It confirmed the noninheritable
  outer kill-on-close Job, creation-time nested host/worker membership before
  resume, ordinary descendant membership, held process identities, and death
  of host, worker, and descendant after closing the guardian's sole Job handle.
- The focused fake guardian/private protocol/R0 contract set passed 34/34.
  The original candidate had failed its single recovery diagnostic assertion
  because the test expected LF while `Console.Error.WriteLine` emits CRLF on
  Windows; exact correction `46ff7fb` made that same set green.
- The PowerShell module suite passed 142 tests with one platform skip. The full
  stdio handshake passed with zero build warnings or errors. Post-run checks
  found no PTK fixture/server/testhost processes, and the remote archive,
  harness, and disposable directory were absent.
- A broader pre-correction Windows run passed 1,513/1,531 .NET tests. Besides
  the corrected R0 newline assertion, the other 17 failures were pre-existing
  certificate/HTTPS fixtures failing during PKCS#12 import or client-certificate
  parsing on this SDK. That failed run is not claimed as a green Windows
  battery; R0's required direct Windows topology and protocol evidence is the
  focused exact-head evidence above.

## Disposable Ubuntu 26.04 ARM64 validation

_Focused verification 2026-07-11 for the Slice 1 secure-storage implementation
that landed in `460c106`._

- Raw open calls confirmed Linux ARM64 uses the architecture-specific
  `O_DIRECTORY=0x4000` and `O_NOFOLLOW=0x8000` values. The focused audit
  storage/evidence suite passed 48/48 after replacing the x86-only constants;
  the pre-fix run failed with `EINVAL`.

## Windows payload/install status

_Not verified by the 2026-07-11 containment probe._

- `NETWATCH-01`'s payload and guidance status remains unknown; that probe
  deliberately did not inspect `F:\dev` or installed PTK files. The former
  combined Mac/Windows reinstall claim was falsified on the Mac and is not
  evidence for any Windows host; verify the target payload directly before
  taking install action.
- `ASHBIAMWEB1` is no longer unknown: its installed payload is recorded in its
  own section below (`0.2.0-dev.g12e1ff5`, re-confirmed 2026-08-03).

## mini-SIEM S1 baseline (Mac, 2026-07-15)

_Fresh transcripted baseline of the existing battery, captured at S1 start of
`.agents/plans/mini-siem-implementation.md` (plan Verification; the
predecessor's author-reported counts are not baseline evidence)._

- Head `8b6d3bb`, worktree `mini-siem`, branch `plan/mini-siem-discovery`,
  clean tree. Darwin 25.5.0 arm64, dotnet 10.0.301, pwsh 7.6.3.
- Exact repo-guidance commands: Pester 141 passed / 0 failed / 2 skipped;
  `dotnet test server/PtkMcpServer.slnx` 1484/1484 passed; handshake
  `HANDSHAKE PASSED`. All three exit codes 0.
- Full normalized transcript:
  `.agents/review/baselines/2026-07-15-mini-siem-s1.txt` (tracked copy of the
  ptk job log with terminal color escapes removed).

## mini-SIEM S2 verification (Mac, 2026-07-15)

_Local verification of S2 receiver code `e761b75` plus producer conformance
head `1f6d485` on Darwin 25.5.0 arm64, dotnet 10.0.301, pwsh 7.6.3._

- `dotnet test siem/PtkSiem.slnx`: 77/77 passed, including live TLS negatives
  for missing certificate, wrong CA, expired certificate, wrong EKU, explicit
  online-revocation refusal, and old/new CA rotation.
- Producer conformance project: 2/2 passed both with the override unset and
  with `PTK_SIEM_CONFORMANCE_MODE=in-process`; the latter drove the real
  exporter into the live SIEM Kestrel endpoint. The scoped formatter checks
  for the SIEM solution, conformance project, and changed producer integration
  file passed.
- Existing battery: Pester 141 passed / 0 failed / 2 skipped;
  `dotnet test server/PtkMcpServer.slnx` 1485/1485 passed; handshake
  `HANDSHAKE PASSED`.
- Required guard discrimination was exercised and restored independently:
  false-ack production default, optional rather than required client cert,
  skipped event-hash recomputation, non-200 success response, and raw-JSON
  rather than OTLP golden output each made its focused test fail. Restored
  trees passed the complete local battery above.
- One pre-existing scheduler timing assertion failed in an intermediate full
  run while an extra redundant TLS test was present. The redundant load was
  removed without touching scheduler code; the exact scheduler test then
  passed 20/20 focused runs and the final complete 1485-test run passed.
- Hosted ubuntu/windows/macos CI was not run from this local branch; the SIEM
  job is configured to exercise both receiver and live producer conformance
  legs on all three when the branch is published.

## mini-SIEM S3 verification (Mac, 2026-07-15)

_Local verification of S3 durable-store head `eb51f2e` plus producer
conformance compatibility head `9f53831` on Darwin 25.5.0 arm64, dotnet
10.0.301, pwsh 7.6.3._

- `dotnet test siem/PtkSiem.slnx`: 91/91 passed. Coverage includes migration
  reopen, effective WAL/FULL writer assertions, exact raw event evidence,
  deterministic custody framing and chaining, post-head duplicate replay,
  mismatched duplicate quarantine, a controlled simultaneous fork, durable
  strict-validator quarantine, unique `(boot_id, sequence)` backstop, simulated
  `SQLITE_FULL`, interrupted event/quarantine transactions, and live mTLS
  `200`/`400`/`503` storage paths.
- `dotnet list siem/PtkSiem.slnx package --vulnerable --include-transitive`
  reported no vulnerable direct or transitive package in either project. The
  explicit native SQLite bundle override resolved to 2.1.12 throughout.
- Producer conformance passed 2/2 both with the override absent and with
  `PTK_SIEM_CONFORMANCE_MODE=in-process`; the compatibility adapter preserves
  the original S2 fake-committer seam while production uses receipt metadata.
- Existing battery: Pester 141 passed / 0 failed / 2 skipped;
  `dotnet test server/PtkMcpServer.slnx` 1485/1485 passed; handshake
  `HANDSHAKE PASSED` with a zero-warning build.
- Six independent guard mutations failed for the intended reason and were
  restored: `synchronous=OFF` tripped startup policy; moving the injected fault
  after commit exposed a persisted partial transaction; rejecting a replay
  broke post-head idempotence; bypassing quarantine left no attempt evidence;
  removing the boot/sequence uniqueness allowed a fork insert; omitting remote
  endpoint bytes changed the frozen custody digest. The restored tree passed
  the complete battery above.
- Hosted ubuntu/windows/macos CI was not run from this local branch. The plan's
  separate startup filesystem-protection matrix remains unimplemented and is
  recorded as a scheduling conflict in `.agents/state.md`; these results do not
  claim acceptance row 7.

## R0 + mini-SIEM S1-S3 integration verification (Mac, 2026-07-16)

_Combined local verification of merge `374f164` on Darwin arm64. The worktree
also contained unrelated, unstaged governance-only changes; those changes were
excluded from the merge and did not alter the tested product or test sources._

- `dotnet test server/PtkMcpServer.slnx`: 1532/1532 passed.
- `dotnet test siem/PtkSiem.slnx`: 91/91 passed.
- Producer conformance: 2/2 passed with
  `PTK_SIEM_CONFORMANCE_MODE=in-process`, and 2/2 passed with the override
  absent.
- Existing PowerShell battery: 141 passed / 0 failed / 2 skipped; handshake
  reported `HANDSHAKE PASSED` with zero warnings and zero errors.
- `dotnet list siem/PtkSiem.slnx package --vulnerable --include-transitive`
  reported no vulnerable direct or transitive packages.
- This is local macOS evidence at merge `374f164`; later direct platform and
  hosted evidence is recorded in its own sections and in the CI portability
  plan. Mini-SIEM S4 remains gated on the absent producer-owned serialized v3
  OTLP request fixture, and startup filesystem owner/mode/DACL plus
  symlink/reparse enforcement remains unimplemented.

## R0 + mini-SIEM S1-S3 integration verification (Windows, 2026-07-16)

_Direct validation of exact integration tip `1ad195e` on `NETWATCH-01`, Windows
NT 10.0.26200.0 x64, PowerShell 7.6.3, .NET SDK 10.0.302/runtime 10.0.10._

- A `git archive` ZIP of the exact commit was transferred to a collision-checked
  disposable directory under `F:\dev`. Local and Windows SHA-256 matched
  `9A18367007DA43E2D4535B90CC17265F7329A4607897EEEC4DFE13E00E2450FD`.
- The .NET/TLS checks ran under a transient `SYSTEM` scheduled task because the
  key-authenticated OpenSSH logon cannot use current-user DPAPI on this host:
  `ProtectedData.Protect(..., CurrentUser)` returned `Access is denied`, causing
  persisted PKCS#12 imports to fail before test behavior. In the service
  context, `dotnet test server/PtkMcpServer.slnx` passed 1532/1532 and
  `dotnet test siem/PtkSiem.slnx` passed 91/91.
- Producer conformance passed 2/2 both with
  `PTK_SIEM_CONFORMANCE_MODE=in-process` and with the variable truly absent.
  The handshake reported `HANDSHAKE PASSED` after a zero-warning, zero-error
  build.
- The documented PowerShell command was reproduced in a nested `-NoProfile`
  process under the ordinary `NETWATCH-01\michael` account: 142 passed / 0
  failed / 1 skipped. The outer SSH shell loads `PSProfile`, whose real `source`
  alias intentionally changes one dialect-detector expectation, so that shell
  is not equivalent to the documented no-profile battery.
- Two preliminary service-runner attempts incorrectly set the conformance mode
  to an empty string while trying to remove it; the contract tests correctly
  rejected that set-but-invalid value. A final runner asserted a null process
  value before launching the green server and default-conformance batteries.
- The scheduled task, archive, checkout, helper scripts, and logs were removed.
  The isolated SYSTEM profile's first .NET invocation created ASP.NET
  development certificate thumbprint
  `35346EB43A9DCC683647EB3FEC9366AD60B8C57D`; cleanup verified its marker and
  removed that exact certificate with its private key. Final task/path/wildcard
  residue counts were zero. No existing checkout or installed PTK payload was
  changed.
- This validates the committed Windows source in a service context; it does not
  satisfy the still-unimplemented dedicated receiver-account filesystem/DACL
  and symlink/reparse acceptance matrix. Mini-SIEM S4 remains gated on
  producer-owned serialized v3 OTLP request bytes.

## R0 + mini-SIEM S1-S3 integration verification (Linux, 2026-07-16)

_Direct validation of exact snapshot `a473ca3` as ordinary user `michael` on a
disposable Ubuntu 26.04 ARM64 checkout at `192.168.64.5`: kernel 7.0.0-27,
.NET SDK 10.0.110/runtime 10.0.10, PowerShell 7.6.3, and Pester 5.8.0._

- A `git archive` ZIP of the exact commit was transferred to a collision-checked
  disposable directory. Local and Linux SHA-256 matched
  `C8615684E1AC6680DBD19EB7EFA7A7D7B4C472AE3F840B2EDD4BD4BCCCC53DF6`.
- The full server suite passed 1532/1532. The PowerShell module suite passed
  141 / 0 failed / 2 platform skips. The complete stdio handshake reported
  `HANDSHAKE PASSED`, including warm cross-call state, foreground/background
  output recovery, audit attribution and outage refusal, and cleanup checks.
- The mini-SIEM suite passed 91/91. Producer conformance passed 2/2 with
  `PTK_SIEM_CONFORMANCE_MODE` truly absent and 2/2 in `in-process` mode.
- A clean build exposed a Linux ARM64 tooling caveat: `Grpc.Tools` 2.82.0's
  bundled `linux_arm64/protoc` exited 139 only when spawned by MSBuild. The same
  binary reported `libprotoc 35.0` and completed the exact generated command
  successfully when invoked directly; disabling build servers and interposing
  transparent wrappers did not change the MSBuild-only crash. The test battery
  above used that bundled binary to generate the normal intermediate files,
  then ran the ordinary projects with `--no-restore`. No product or test source
  was modified, no system `protoc` was installed, and this evidence therefore
  validates behavior but not a clean ARM64 build path.
- The archive, checkout, wrapper, diagnostic logs, and test-created temporary
  job directories were removed. Process and scoped path checks found no PTK
  residue. The only HTTPS development certificate in the user store predates
  this validation (created 2026-07-11) and was preserved. No installed PTK
  payload or existing checkout changed.
- The receiver filesystem protection matrix remains unimplemented, and
  mini-SIEM S4 remains gated on producer-owned serialized v3 OTLP request
  bytes.

## CI portability Slices 11-12 verification (Mac and Windows, 2026-07-16)

_Verified at exact repair head `f658f212da64d2ad08c94c0341844d301f0eac4c`; this
was disposable-checkout validation and did not update an installed payload._

- On `nagatha.local`, a `core.autocrlf=true` parent checkout reproduced the six
  byte-contract failures with `w/crlf`. The repaired checkout reported `w/lf`
  and `text eol=lf`, and the R0 contract class passed 14/14. The exact repair
  head then passed 1,532/1,532 server tests, 141 Pester tests with two expected
  platform skips, and the complete stdio handshake.
- On `NETWATCH-01`, the checked-out R0 artifacts likewise reported `w/lf` with
  the LF attribute. A temporary direct-final marker writer held open for one
  second reproduced the hosted sharing violation; the pending-file version
  passed with the same hold, and the exact committed Windows integration test
  passed 10/10 after the hold was removed. The R0 contract class passed 14/14.
- The complete Windows server suite passed 1,532/1,532 under a transient
  `NT AUTHORITY\\SYSTEM` scheduled task. The documented no-profile PowerShell
  suite passed 142 tests with one expected skip under
  `NETWATCH-01\\michael`, and the stdio handshake passed with a zero-warning,
  zero-error build. The service identity was required for .NET because this
  host's key-authenticated SSH identity cannot use current-user DPAPI for the
  persisted PKCS#12 test imports; the service profile's legacy Pester was not
  counted as product evidence.
- The service run created no certificate. All exact validation tasks,
  processes, checkouts, bundles, scripts, logs, and local proof directories
  were removed, with zero scoped residue afterward. Existing checkouts,
  installed payloads, and unrelated host configuration were unchanged.

## CI portability Slices 13-16 verification (Mac, Linux, and Windows, 2026-07-16)

_The four runtime-equivalent repairs were verified at exact head
`a031556034ec41adf3f97de4248e6553f28ac90d`; the independent-review source-
guard closure was verified at final head
`5642376563fe9b0d1eb8c2512291fec45cf29bc6`._

- On `nagatha.local`, final head `5642376` passed 1,532/1,532 server tests,
  141 Pester tests with two expected platform skips, and the complete stdio
  handshake with zero warnings and errors. Homebrew PowerShell 7.6.3 was
  installed but not linked into the ordinary command path, so the passing
  battery explicitly prepended `/opt/homebrew/opt/powershell/bin`. The first
  diagnostic run without that directory failed 58 job-dependent tests solely
  because `pwsh` was unresolved; it is not counted, and its fifteen exact
  test-created job directories were removed. The SDK's first-use text referred
  to its feature-band sentinel: the only local ASP.NET development identity
  predates this validation and was preserved.
- On the Ubuntu 26.04 ARM64 VM at `192.168.64.5`, exact head `a031556` passed
  1,532/1,532 server tests, 141 Pester tests with two expected platform skips,
  and the full zero-warning handshake. The final `5642376` deadline-helper
  source guard then passed 1/1 from a new exact checkout. Both runs required
  the already-recorded direct-protoc generation around the host's MSBuild-only
  ARM64 `protoc` exit 139; they validate behavior, not a clean ARM64 build.
  All scoped checkouts, job directories, and matching processes were removed.
- On `NETWATCH-01`, disabling only the production Windows error-32 classifier
  made the corrected checkpoint test fail at the retained-handle timeout. The
  production file restored to its committed SHA-256 and the focused test then
  passed 10/10. A first full `SYSTEM` run passed 1,531/1,532; its sole
  non-counted failure transiently observed the pre-existing journal quota lock
  before its owner-only DACL. A clean rerun passed 1,532/1,532. Under nested
  no-profile `NETWATCH-01\michael`, Pester 5.8.0 passed 142 tests with one
  expected skip and the full zero-warning handshake passed. The final
  `5642376` deadline-helper source guard separately passed 1/1 under that
  ordinary identity.
- Windows production/test source hashes matched the committed bytes. The
  `SYSTEM` certificate store was empty before and after the full run; one
  pre-existing Michael ASP.NET development certificate was preserved. Final
  checks found zero scoped tasks, processes, checkouts, archives, scripts, or
  logs on either remote host. No existing repository or installed PTK payload
  changed.

## Mini-SIEM S3H storage-hardening verification (Mac, Windows, and Linux, 2026-07-16)

_Verified at S3H code head `c726a33` (base `1f314a2` plus the exact source
snapshots named below). S4-S6 and PTK runtime sources were not changed._

- On `nagatha.local` (Darwin arm64, .NET SDK 10.0.302/runtime 10.0.10), the
  restored final tree passed 243/243 SIEM tests with zero skips.
  `dotnet format siem/PtkSiem.slnx --no-restore --verify-no-changes` formatted
  zero files, and the direct/transitive package vulnerability audit reported
  no vulnerable package in either SIEM project. The unchanged server suite
  exited zero, and the documented Pester suite passed 141 with two platform
  skips.
- Eight independent guard mutations failed for their intended reason and were
  restored: masking away POSIX special bits; bypassing the protected config
  read; reopening TLS paths after verification; disabling the lexical
  ancestor walk; removing protected-file/storage identity intersection;
  removing final WAL namespace verification; removing both live-open database
  identity proofs; and, on Windows, accepting a one-ACE DACL with inheritance
  flags. The final restored SIEM tree passed again.
- On `NETWATCH-01` (Windows 10.0.26200.8875, .NET SDK 10.0.302/runtime
  10.0.10), an exact 355-file archive had SHA-256
  `CBD0756703DCF3837F153C864B931A1263E89FF6BB73390399657DEA6AA5BEF6`.
  Under a transient `NT AUTHORITY\\LOCAL SERVICE` scheduled task, the full
  SIEM suite passed 243/243. Removing only the `AceFlags.None` predicate made
  the inheritance-flags theory case fail while the wrong-rights case remained
  green, proving that exact-DACL branch. All disposable tasks, processes,
  archives, directories, and local transfer files were removed; scoped
  residue counts were zero, and no existing checkout or installed payload was
  touched.
- On the Ubuntu 26.04 ARM64 VM at `192.168.64.5` (kernel 7.0.0-27, .NET SDK
  10.0.110/runtime 10.0.10), the refreshed source manifest was
  `ef91ee5d470ea5d8416e6aa26d83ba67f85a4dd03b1d901fbe32690ab632b5de`
  and archive SHA-256 was
  `c36bc4d63472dfe1ecb3507af7db28ea45e95a0bd2b306821934398f9a1ab81d`.
  The ordinary build reproduced the already-recorded MSBuild-only bundled
  `protoc` exit 139. Direct invocation of that same `libprotoc 35.0` generated
  the normal intermediates, after which the unmodified suite passed 243/243
  with zero skips. All scoped directories, archives, and processes were
  removed; no existing checkout or payload changed.
- The local stdio handshake was diagnostic-only for this SIEM-only slice and
  did not pass: two default 30-second runs and one discriminating 120-second
  run received no initialize response from the unchanged server and required
  their bounded process-tree kill fallback. `server/`, `src/`, and `tests/`
  had no diff, no PTK/DOTNET override was present, the server suite exited
  zero, and the Pester suite was green, so no out-of-scope runtime repair was
  attempted. Two empty/test-content handshake roots discovered under
  `/Users/michael/.ptk` predated this run (creation 2026-07-14) and were
  preserved; the failed runs created no new retained root.

## Production-reliability salvage Slice 0 baseline (Mac, 2026-07-27)

_Recorded on `impl/production-reliability-salvage` with unchanged product base
`c9b11bcb0b4e41a11110c5870562b4980c0b86b3`; reviewed plan/review documentation
head before this record was `f729ed2a300d3fbf43269845ffc82aa6856a98b9`._

- `pwsh -NoProfile -Command "Invoke-Pester -Path
  tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"` passed 141 tests with
  two platform skips.
- `dotnet test siem/PtkSiem.slnx` passed 247/247.
- `pwsh -NoProfile -File server/test-handshake.ps1` passed the complete stdio
  handshake with a zero-warning build.
- The first `dotnet test server/PtkMcpServer.slnx` run passed 1,586/1,587 and
  failed
  `JobManagerTests.Associated_start_failure_retries_internal_containment_on_a_bounded_observer`
  during `JobManager.Dispose`; the focused test then passed 1/1.
- The second full server run passed 1,585/1,587 and failed
  `AuditAnchoredRuntimeTests.Anchored_runtime_exports_only_after_start_and_restart_adopts_clean_stop`
  plus
  `AuditRuntimeGateTests.Concurrent_startup_repair_opens_and_publishes_one_runtime`;
  both focused tests then passed 1/1.
- The third and final full server run passed 1,587/1,587. The earlier failures
  remain production blockers because the baseline is intermittent even though
  a green reference run exists.
- Two independent direct MCP clients against the exact Codex registration
  command `/Users/michael/.ptk/bin/PtkMcpServer` received separate process and
  reported server PIDs `7523` and `7524` over distinct stdio streams.
- Two independent ephemeral read-only Codex agents then called only
  `ptk_state`; they received distinct PTK server PIDs `9595` and `9596`.
  Both probe servers exited. This is the plan-required production-harness proof
  that unrelated agents receive separate PTK server processes/connections.

## Production-reliability salvage Slice 1 verification (Mac, 2026-07-27)

_Verified on the candidate tree based on Slice 0 record `e449a99`; the commit
containing this record carries the exact product diff._

- The retired surface consists only of the guardian/private-host fixture and
  its two test classes, the frozen public/recovery/package contract artifacts,
  and their solution/project references. Retained audit/SIEM vectors remain
  covered by `AuditInteropContractTests`.
- SHA-256 values for all three `PtkContainmentTestFixture` sources and both
  Windows containment test files matched the pre-change values exactly, and
  `git diff` reported no change in those paths. Windows execution was not
  available on this Mac.
- The new exact direct-server tool guard was proved nonvacuous: temporarily
  adding a production `ptk_guardian_probe` tool made
  `Tool_discovery_finds_exact_direct_server_surface` fail with the unexpected
  sixth tool. Removing the probe restored the focused
  `AuditInteropContractTests|ToolSchemaConformanceTests` run to 43/43.
- `dotnet format server/PtkMcpServer.slnx --no-restore
  --verify-no-changes --include
  server/PtkMcpServer.Tests/AuditInteropContractTests.cs
  server/PtkMcpServer.Tests/ToolSchemaConformanceTests.cs` exited zero.
- Pester passed 141 tests with two platform skips; the SIEM solution passed
  247/247; and the complete stdio handshake passed with a zero-warning build.
- Post-change server run 1 passed 1,556/1,557 and failed
  `StateToolTests.Path_drift_reports_an_entry_level_diff`; its focused rerun
  passed 1/1. Run 2 passed 1,556/1,557 and reproduced the pre-change
  `JobManagerTests.Associated_start_failure_retries_internal_containment_on_a_bounded_observer`
  failure. Run 3 passed 1,556/1,557 and failed
  `RunspaceHostTests.Healthy_private_render_cleanup_can_complete_before_same_call_shaping`;
  its focused rerun passed 1/1. The three-attempt stall threshold ended further
  full reruns, so no post-change full server pass is claimed.
- `dotnet list ... package --vulnerable --include-transitive` reported five
  high-severity advisories for `System.Security.Cryptography.Xml` 10.0.6.
  `dotnet nuget why` traced it through `Microsoft.PowerShell.SDK` 7.6.3 and
  `Microsoft.Windows.Compatibility`/`System.ServiceModel`. No dependency was
  changed or suppressed in this slice.

## Production-reliability salvage intermittent-suite diagnosis (Mac, 2026-07-27)

_Diagnosed on clean `impl/production-reliability-salvage` head `36af69a`; PTK
MCP transport was unavailable, so the normal shell fallback was used._

- Exact `git diff --exit-code` from product base `c9b11bc` through `36af69a`
  was empty for `RunspaceHost.cs`, `JobManager.cs`, the runtime audit directory,
  and all five implicated test files. The Slice 1 R0 retirement therefore
  changed none of the failing paths.
- Direct environment/PATH mutation inventory found the relevant test classes
  already grouped under `ProcessEnvironment`; no unannotated PATH writer
  explained `StateToolTests.Path_drift_reports_an_entry_level_diff`.
- The five implicated classes ran together under ordinary xUnit scheduling and
  passed 125/125 in 1m08s. The same 125 tests passed with
  `xUnit.ParallelizeTestCollections=false` in 1m38s; diagnostic output confirmed
  `parallel test collections = off`.
- The complete suite passed 1,557/1,557 with collection parallelism disabled in
  9m37s. One final paired default-parallel control passed 1,557/1,557 in 7m58s.
  This does not erase the five failures in the six earlier default-parallel
  runs; it confirms their intermittent rather than deterministic character.
- The failing tests use fixed 5-15 second observation/watchdog bounds or, in the
  PATH case, omit an invocation-success assertion before checking state. Their
  production paths were unchanged, and every failure passed immediately alone.
  The evidence supports aggregate same-testhost scheduling pressure, not an R0
  product regression.
- During diagnosis the 16-logical-CPU Mac reported load averages
  `24.05 45.07 47.35`. An unrelated Headroom memory proxy had been running for
  more than twelve days and was consuming about 212% CPU; other unrelated Node
  work was active. No `PtkMcpServer`, fixture, `pwsh`, `dotnet test`, `testhost`,
  or `sleep 300` process remained after the tests.
- The smallest proposed repair is test-only: disable xUnit test-collection
  parallelism at the server test assembly. Do not widen product/test deadlines
  or patch the five currently visible classes individually. Explicit
  concurrency inside tests remains unaffected.

## Production-reliability salvage Slice 1a verification (Mac, 2026-07-27)

_Verified on `impl/production-reliability-salvage` from pre-commit head
`224079474299`. PTK MCP transport was unavailable, so the normal shell fallback
was used._

- The proposed assembly-level `CollectionBehavior` attribute compiled into the
  test assembly with `DisableTestParallelization = true`, but xUnit still
  reported `parallel test collections = on [16 threads]`. An isolated project
  using the repository's exact xUnit 2.9.3, VSTest adapter 3.1.4, .NET test SDK
  17.14.1, and .NET 10 reproduced the failure. The ineffective attribute was
  removed rather than retained as false assurance.
- `server/PtkMcpServer.Tests/xunit.runner.json` with
  `parallelizeTestCollections: false`, copied by the test project, reported
  `parallel test collections = off`. The explicit
  `xUnit.ParallelizeTestCollections=true` command-line diagnostic restored
  `on [16 threads]`. Both filtered proofs passed 1/1.
- Three consecutive unmodified `dotnet test server/PtkMcpServer.slnx` runs
  passed 1,557/1,557 with no retry or parallelism override:
  - run 1: starting load `60.86 24.72 14.24`; test duration 7m20s; real 457.79s;
  - run 2: starting load `28.35 33.05 23.37`; test duration 6m20s; real 391.18s;
  - run 3: starting load `19.07 23.78 22.53`; test duration 6m32s; real 400.87s.
- The remaining local battery passed: Pester 141 passed / 0 failed / 2 platform
  skips; SIEM 247/247; and the complete stdio handshake reported
  `HANDSHAKE PASSED` after a zero-warning build.
- `git diff --check` and JSON parsing passed. The repository-wide
  `dotnet format ... --verify-no-changes` command remains red on committed,
  unrelated whitespace in `AuditOtlpRecordMapperTests.cs`; Slice 1a does not
  touch that file and does not claim the pre-existing formatter baseline is
  clean.
- Evidence is local macOS only. No `ci/**` push or PR was authorized. The
  anchored-evidence ordering race, any serialized `JobManager.Dispose`
  recurrence, fixed watchdog sensitivity, two recorded Windows containment
  failures, and five transitive high-severity
  `System.Security.Cryptography.Xml` advisories remain open.

## Production-reliability salvage Slice 4 cross-platform validation (2026-07-27)

_Verified at exact commit `75505b98021229efebc338597d38450059da9294`,
tree `101a0e33b243c8cfd73e9f6f9e29c8a26876e123`; this was checkout
validation, not an install or deployment._

- The exact `git archive` SHA-256 was
  `d950a72d6c317405db3667c9fa53fd77993f75a6daf0a2d589fd2085d960f94d`
  locally, on Linux x86_64 `magneto`, and on Windows x64 `NETWATCH-01`.
  No emulator or UTM was used.
- The final macOS ARM64, Linux x86_64, and Windows x64 batteries each passed:
  server 1,240/1,240; Pester 141 passed with two platform skips; SIEM
  247/247; and the complete public stdio handshake.
- Windows validation first caught four defects or false assumptions. Commit
  `cba10d3` replaced close-to-kill with `TerminateJobObject`, retained the Job
  Object while querying active membership, and refused to claim containment
  when termination could not be proved. Commit `f5e3b16` made the route-consent
  guard independent of `NETWATCH-01`'s legitimate `export` alias. Commit
  `585b75c` stopped descendants-only cleanup from turning a failed kill request
  into a successful root kill. Commit `75505b9` gave the bounded containment
  observer its independent retry before fallback escalation; its formerly
  intermittent Windows guard then passed 30 consecutive runs.
- The five known `System.Security.Cryptography.Xml` 10.0.6 advisories were the
  only dependency warnings. The installed PTK MCP transport returned
  `Transport closed` before validation, so the normal shell fallback was used;
  the candidate was never installed and the transport failure is not attributed
  to it.
- No matching test, fixture, or checkout process survived. The disposable
  Linux and Windows validation trees were removed. The three local transfer
  directories were moved to the macOS Trash, so they remain recoverable there
  until Trash is emptied. No installed payload or persistent host configuration
  changed.

## Production-reliability salvage Slice 5 cross-platform validation (2026-07-27)

_Verified at exact commit `001c4ebe23d48e1804f2bb169f09a8f0e0c0dd2a`,
tree `6f0fa1a387da4d09e04d1fc849974c6d1581f401`; this was checkout
validation, not an install or deployment._

- The exact `git archive` SHA-256 was
  `6446eb24503947f92aeb722452055ff6177790923eac77f93eccdc7f28c154b9`
  locally, on Linux x86_64 `magneto`, and on Windows x64 `NETWATCH-01`.
  No emulator or UTM was used.
- On macOS ARM64, the 24 focused Slice 5 tests and complete 1,264/1,264
  server suite passed. Pester passed 141 tests with two platform skips, SIEM
  passed 247/247, the changed-file formatter and `git diff --check` passed,
  and the complete public stdio handshake passed.
- Deliberate regressions separately made the worker-launch-directory,
  confirmed-empty containment proof, supervisor-owned stopped-worker PID,
  late-start/concurrent shutdown, and admitted invoke/state transport-recovery
  guards fail. Production sources were restored before the final battery.
- `magneto` reported x86_64, .NET SDK 10.0.110, and PowerShell 7.6.3. Its
  complete server suite passed 1,264/1,264 in 3m23s, Pester 6.0.1 loaded only
  from the disposable validation root passed 141 tests with two skips, the
  direct SIEM test project passed 247/247, and the public handshake passed.
- `NETWATCH-01` reported x64, .NET SDK 10.0.302, and PowerShell 7.6.4. Its
  complete server suite passed 1,264/1,264 in 2m30s, Pester passed 142 tests
  with one platform skip, the direct SIEM test project passed 247/247, and the
  public handshake passed.
- On both remote SDKs, `dotnet test siem/PtkSiem.slnx` returned success with
  no build, output, or tests. That no-op was rejected as evidence; direct
  execution of
  `siem/PtkSiemReceiver.Tests/PtkSiemReceiver.Tests.csproj` produced the
  recorded 247/247 results.
- The five known `System.Security.Cryptography.Xml` 10.0.6 advisories were
  unchanged. No matching candidate, worker, broker, or test process survived.
  The disposable Linux and Windows validation trees were removed. The local
  transfer directory was moved to
  `/Users/michael/.Trash/ptk-slice5-001c4eb-lZWhDsQHms`, so it remains
  recoverable until Trash is emptied. No installed payload or persistent host
  configuration changed.

## Production-reliability salvage Slice 6 validation in progress (2026-07-28)

_Validating exact commit `6c9bdacc84685af54055b22627666bfb8231c2d1`,
tree `788eabcc341be9ad9a726f169b886de66857b289`; this is disposable
checkout validation, not an install or deployment._

- The exact source archive SHA-256 is
  `496c0fc9da2a061f9bd3bad4221c17645cf7d7daeb9e978265de2821c0f61ead`.
  The disposable Pester 6.0.1 archive SHA-256 is
  `87fb1475455d2b895d7ccc031465998e28ea12b8b557d50c5b41ee88f4623969`.
- On macOS ARM64, twelve deliberate guard mutations each failed, were restored
  byte-for-byte, and then passed together 12/12. The complete server suite
  passed 1,167/1,167 in 5m14s; Pester passed 141 tests with two platform skips;
  SIEM passed 247/247; formatting, `git diff --check`, and the complete public
  stdio handshake passed. Publish smoke included an executable ARM64 Mach-O
  `PtkWorkerBroker`.
- On Linux x86_64 `magneto`, the transferred archives matched the local hashes.
  The host reported .NET SDK 10.0.110 and PowerShell 7.6.3. The logged evidence
  run passed the complete server suite 1,167/1,167 in 2m39s, Pester 141 with two
  platform skips, SIEM 247/247, and the complete public stdio handshake. The
  combined log SHA-256 is
  `e6333cb5ac63a2f4b6a4e20039ac6daba9eb2894fc0af436b7dddc5f59e98008`;
  its recorded exit was zero. No candidate server, worker, broker, fixture, or
  test process survived.
- Windows x64 validation remains pending. On 2026-07-28,
  `netwatch-01.internal` resolved to `10.1.10.173` but was down/unroutable, and
  SSH to the previously used `10.1.10.177` timed out. No Windows files or
  installed payload were touched. Slice 7 was held when this validation run
  ended; the owner subsequently directed local Slice 7 work to continue
  without waiving this exact-commit Windows battery.
- The five known `System.Security.Cryptography.Xml` 10.0.6 advisories were the
  only dependency warnings. After its log was copied and hashed, the disposable
  Linux checkout was removed. The local transfer directory remains only until
  Windows evidence and final cleanup are complete.

## Production-reliability salvage Slice 7 cross-platform validation (2026-07-28)

_Verified at exact code commit
`51594735e40c6d50a2aaf94c84a9e55a70f63b50`, tree
`a59d665adcc589e5d4b05e193493ffb59d0082e7`; this was checkout
validation, not an install or deployment._

- The exact source archive SHA-256 is
  `7369426331fbade378c8dba5853c409628cb84b13150bf8596c36bff4084698e`.
  The disposable Pester 6.0.1 archive SHA-256 remains
  `87fb1475455d2b895d7ccc031465998e28ea12b8b557d50c5b41ee88f4623969`.
- On macOS ARM64, six deliberate regressions independently proved the first
  pipe-write boundary, post-write transport poisoning, no resend, valid
  terminal ownership, public failure projection, and failed-replacement/reset
  guards. Every production file returned to its pre-mutation SHA-256 before
  the focused 37/37 recovery battery passed. The complete server suite then
  passed 1,180/1,180 in 7m29s; Pester passed 141 tests with two platform skips;
  SIEM passed 247/247; the built-tree and self-contained published-executable
  handshakes passed; scoped formatting and `git diff --check` passed. Both the
  published server and `PtkWorkerBroker` were executable ARM64 Mach-O files.
- The real-process recovery guard hard-killed a worker after one external file
  effect. The call returned `outcome_unknown`, the effect remained exactly
  once, one fresh worker replaced the victim, and its sibling retained the same
  PID and warm variable. Deterministic protocol guards separately covered
  failure before write entry, synchronous/asynchronous/partial write failure,
  loss after a complete write, loss during a result frame, and worker death
  immediately after a complete terminal.
- On Linux x86_64 `magneto`, the transferred archives matched the local hashes.
  The host reported .NET SDK 10.0.110 and PowerShell 7.6.3. The complete server
  suite passed 1,180/1,180 in 2m49s, Pester passed 141 tests with two platform
  skips, SIEM passed 247/247, and the complete public stdio handshake passed.
  The combined log SHA-256 is
  `be080564f725837a28e4623d09e714e0662c64c61e55761f3157a9cc7266c374`;
  its recorded exit was zero.
- No candidate server, worker, broker, fixture, or test process survived on
  either validated host. The disposable Linux checkout was removed after its
  log was copied. The local exact-commit transfer directory is
  `/tmp/ptk-slice7-5159473-transfer.Jb134Y` and remains for Windows validation.
  The macOS publish directory was moved to
  `/Users/michael/.Trash/ptk-slice7-publish.2NCq6d`, so it remains recoverable
  until Trash is emptied.
- Windows x64 remains pending for both Slice 6 and Slice 7. A fresh
  `NETWATCH-01` probe after the Linux run timed out without touching the host.
  The five known transitive `System.Security.Cryptography.Xml` 10.0.6
  advisories were the only dependency warnings.

## Production-reliability salvage Slice 8 cross-platform validation (2026-07-28)

_Product behavior was verified at exact commit
`4270487c891d3c2cb0976f25eb3082f90c7ac630`, tree
`ebd281d36b1612c50f2cf0d731d4ca7658cc3ee3`. Test-only descendant
`4779cb2f7000306cc11f24fe017a4671b5b16cbf`, tree
`3897d550ceaece1dceac88e86e14b72fa72092f2`, adds direct observation of
the late-publication boundary. This was checkout validation, not an install or
deployment._

- The exact product source archive SHA-256 is
  `71f3e73641bf2b507b28907ba9b48ff9dc290e17a95b73c638d0d36cbd5308b1`;
  the test-hardened descendant archive SHA-256 is
  `a25a4d9eababdddd9a2e1c55e74a48ccd9459973788c046fcf692bd855b8b554`;
  the disposable Pester 6.0.1 archive SHA-256 remains
  `87fb1475455d2b895d7ccc031465998e28ea12b8b557d50c5b41ee88f4623969`.
- On macOS ARM64, fifteen deliberate production regressions independently
  proved session quota attribution and capture refusal, healthy and wedged
  storage-lane behavior, terminal cancellation and late-publication
  arbitration, full-buffer discard with continued protocol validation,
  worker-loss quota release, normal root cleanup, stale and hard-death
  reclamation, live-root protection, duplicate and over-reservation rejection,
  malformed artifact poisoning, and local storage failure without a false
  handle. One cancellation bypass initially survived because quota eviction
  hid a late unreachable publication. The test was strengthened to observe the
  irreversible publication claim directly; the same bypass then failed and the
  restored code passed. The final focused
  reliability battery passed 82/82, and the complete server suite passed
  1,197/1,197 in 8m02s on the test-hardened descendant.
- At the exact product commit, the complete macOS server suite passed
  1,197/1,197; Pester passed 141 tests with two platform skips; SIEM passed
  247/247; scoped formatting, `git diff --check`, and the public stdio
  handshake passed. The handshake covered output recovery and hard-kill output
  root cleanup. A self-contained `osx-arm64` publish succeeded after the
  required RID restore and its published-executable handshake passed; the
  server and `PtkWorkerBroker` were executable ARM64 Mach-O files. The earlier
  `--no-restore` publish refusal was the expected `NETSDK1047`, not a product
  failure.
- On Linux x86_64 `magneto`, the product and Pester archives matched the local
  hashes. The host reported .NET SDK 10.0.110 and PowerShell 7.6.3. The
  complete server suite passed 1,197/1,197, Pester passed 141 tests with two
  platform skips, SIEM passed 247/247, and the complete public stdio handshake
  passed. The combined log SHA-256 is
  `305726c1f25861140a4bc309182a70e451a79b6803f09d263e581e74a041b074`;
  its recorded exit was zero. No candidate process or output-root residue
  survived, and the disposable remote checkout was removed.
- Windows x64 remains pending for Slices 6-8. No Windows host or installed
  payload was touched. The local Slice 8 archives and Linux evidence remain at
  `/tmp/ptk-slice8-4270487-transfer.4VAS9N` for that validation. The macOS
  publish directory was moved to
  `/Users/michael/.Trash/ptk-slice8-publish.PyQkk1`, so it remains recoverable
  until Trash is emptied. The five known transitive
  `System.Security.Cryptography.Xml` 10.0.6 advisories were the only dependency
  warnings.

## Production-reliability salvage Slice 9 package validation (2026-07-28)

_Validated exact commit `46710afd2e90911f2402019ff9ab4ddc10695845`,
tree `f8b5ec704d7676d7b942eb9859ea6bc986d9e472`; this was disposable
checkout/package validation, not installation or deployment._

- The exact source archive SHA-256 is
  `f4b669dc3501990b2ab448b346bbe0ed70a197707c819715c102af45d63605f5`.
  The disposable Pester 6.0.1 archive SHA-256 is
  `87fb1475455d2b895d7ccc031465998e28ea12b8b557d50c5b41ee88f4623969`.
- On macOS ARM64, the transaction fault matrix, scoped package guards,
  Pester 141 with two platform skips, SIEM 247/247, formatting,
  `git diff --check`, and the complete built public handshake passed. A
  self-contained `osx-arm64` layout then passed the same handshake before
  activation and again from its disposable activated path; user-owned content
  and the disposable registration remained correct, and the sensitive snapshot
  was removed. One complete server run passed 1,199/1,199. The exact-final
  repeat passed 1,198/1,199 and hit only the unchanged, already-recorded
  500 ms
  `BashProcessRunnerTests.Execution_pipe_drains_cannot_outlive_the_call_deadline`
  fixture while host load was approximately 29-36; that test passed immediately
  in isolation. Slice 9 changes neither the fixture nor the Bash runner.
- Six deliberate regressions independently made their intended guards fail:
  skipped file restoration; deletion of an unconfirmed recovery snapshot;
  omission of its readable manifest; direct in-process invocation of
  `ptk_init.ps1` whose `exit 1` bypasses PowerShell `catch`; stale
  `background=true` guidance; and crossing the same-named function body between
  two named sessions. Every source was restored before the final batteries.
- On Linux x86_64 `magneto`, the transferred archives matched the local hashes.
  The host reported .NET SDK 10.0.110, PowerShell 7.6.3, and disposable Pester
  6.0.1. The complete server suite passed 1,199/1,199 in 2m56s; Pester passed
  141 tests with two platform skips; SIEM passed 247/247; and the complete
  built handshake, transaction fault matrix, self-contained `linux-x64` layout
  handshake, staged-package handshake, and activated-package handshake all
  passed. The combined log SHA-256 is
  `15eac5e6a02a1eb17001382eef5b702985d8806616c7ecb1810d0ba0735c33d0`;
  its recorded exit was zero.
- No candidate server, worker, broker, fixture, or test process survived. The
  disposable Linux checkout was removed. No installed payload, real harness
  registration, or Windows ARP state changed. The local exact-commit archive
  and Linux evidence remain at
  `/tmp/ptk-slice9-46710af-transfer.OT8x4y`; the final macOS layout remains at
  `/tmp/ptk-slice9-final-layout.H1IwNl`.
- `NETWATCH-01` remained unreachable at both `netwatch-01.internal` and the
  previously used `10.1.10.177`, so Windows x64 validation remains pending for
  Slices 6-9. The five known transitive `System.Security.Cryptography.Xml`
  10.0.6 advisories were the only dependency warnings.

## Production-reliability Slice 10 timeout-containment validation (2026-07-28)

_Validated exact code commit `ef1657b22b9847f24dc991e6a9c0a58aa8624281`,
tree `c10274576bca11c42124f1257f459113420df8e7`; this was disposable
checkout/archive validation, not installation or deployment._

- Five deliberate regressions independently proved the new guards
  load-bearing: recomputing the runspace deadline instead of forwarding the
  admission deadline, blocking the watchdog inside a cancellation callback,
  marking a request terminal before its terminal frame was written, rejecting
  an incomplete timeout artifact, and advertising worker-local recovery while
  the supervisor owned capture. Each targeted test failed for that exact
  reason. Restoration returned
  `WorkerOperationScheduler.cs` to blob
  `d384de596440753c7afe772d1c6e228d532dc709` and
  `SessionRuntime.cs` to blob
  `3e2eef6ad57ebbf0ff648aec0a9a1ab6a2c56f1e`; each targeted test then passed.
- On `nagatha.local` (macOS 26.5.2 ARM64, .NET SDK 10.0.302, PowerShell
  7.6.3), the complete server suite passed 1,211/1,211 in 2m27s; Pester passed
  141 tests with two platform skips; SIEM passed 247/247; the complete public
  handshake passed; scoped formatting and `git diff --check` were clean.
  The full production acceptance passed 100 replacements, with process count
  5/5, handles/fds 543/545, and private footprint 354.1/366 MiB. It replaced
  the unresponsive timed-out worker, killed its two native descendants,
  preserved the warm sibling, and later proved supervisor hard-kill removed
  all four owned descendants. The server/Pester/SIEM/handshake/acceptance log
  SHA-256 values are respectively
  `161ece5b00a1724ea16a7e01a9e28f2b7ed1de5ffa0604082c7f45392f0b7707`,
  `7a3b517052464301858715d8d99fc1abd5c87f09960b2bb42ade85223fb19309`,
  `87fd9cd48f0193a83f841bb05051ab7330308b1a6b002d73f67abd6f518e818e`,
  `9ed9fceff6ad1eeee2bb4cd015e5fd8a5d183dbe13032fec6a459d37996a1b02`,
  and
  `7fa4f4ac5a88f68a9acddf6257365504c03217b503dbd4b56f6c52911304508c`.
- The exact source archive SHA-256 was
  `b9eb69780b226789c3e3adbc40b8c3b91dee94cbbe1b6e647286fb6f83ff60db`;
  the disposable Pester 6.0.1 archive SHA-256 was
  `fe7313dffbc461c82d71c5cef31c0f013e1d8f685876f81100e4d84f77d49f48`.
  Both matched after transfer to `magneto` (Arch Linux 7.1.4-arch1-1 x86_64,
  .NET SDK 10.0.110, PowerShell 7.6.3).
- From that exact archive on `magneto`, the complete server suite passed
  1,211/1,211 in 3m02s; Pester passed 141 tests with two platform skips; SIEM
  passed 247/247; and the complete public handshake passed. The full production
  acceptance passed 100 replacements, with process count 5/5, handles/fds
  530/530, and private footprint 235.2/240.5 MiB. It replaced the timed-out
  worker, killed both native descendants, and proved supervisor hard-kill
  removed all four owned descendants. The combined Linux log SHA-256 is
  `43f3a2716a79b73d6fd0efa4a2a5e8020e116d6c72473f734e0c4ff9376f5146`;
  its exit was zero.
- No candidate server, worker, broker, fixture, or test process survived on
  either host. The 316 MiB remote checkout and the local transfer directory
  were removed after hashing; they are disposable and not recoverable. No
  installed payload or harness registration changed. The five already-recorded
  transitive `System.Security.Cryptography.Xml` 10.0.6 advisories were the only
  dependency warnings.

## Production-reliability Slice 10 Unix-escape validation (2026-07-28)

_Validated exact acceptance commit
`a626c3b8e993575ad916058bc33959c02b4daae3`, tree
`63f066f30bcf41b861fee73b3d5a9edfe0953f0a`; its product tree is unchanged
from timeout-containment commit `ef1657b22b9847f24dc991e6a9c0a58aa8624281`.
This was disposable checkout/archive validation, not installation or
deployment._

- The public production-acceptance harness starts a child and grandchild in the
  worker's broker-owned process group, has only the child call `setsid`, and
  verifies the resulting process groups before fault injection. A timed-out
  invocation then must be reported without replay, the old worker and
  same-group grandchild must die, the escaped child must remain live, session
  state must say `last_failure=descendants_unknown`, and reset must remain
  refused until exact cleanup of that escaped PID. Only then may reset create a
  different worker.
- A deliberate regression made
  `UnixWorkerContainmentRegistry` ignore both observed process-group changes.
  The real acceptance then failed because PTK falsely reported the session
  `ready` with a replacement worker instead of `descendants_unknown`. Restoring
  source blob `bc6f9499839f43c05034141580313d2aac917a7f` made the reduced and
  full acceptance runs pass. The regression log SHA-256 is
  `d1899378a512ee2552c72a53d389e86f952e92f49c533f742903ce9eb5fa08ad`.
- On `nagatha.local`, the full 100-cycle acceptance passed with process count
  5/5, handles/fds 543/545, and private footprint 338.3/348.1 MiB. Timeout
  containment killed both native descendants; the verified escape faulted
  `descendants_unknown`, blocked replacement while its escaped child lived,
  then recovered after cleanup; and supervisor hard-kill removed all four
  owned descendants. The full log SHA-256 is
  `2d332cc96a68332a41636becf7cbee9befdac16316b0280bf0a2974b51a5a985`.
- The exact source archive SHA-256 was
  `4f99eddf4c51e683b18f2351250d39f54fe999aa596978fe429a961d98dbf36c`
  and matched after transfer to `magneto`. From that archive, Linux x86_64
  passed the same 100-cycle acceptance with process count 5/5, handles/fds
  530/530, and private footprint 230/231.5 MiB, including timeout, real
  `setsid` escape, reset blocking/recovery, and supervisor hard-kill. The Linux
  log SHA-256 is
  `f735b8ff17386b20d15e943d16eede650fe227dd59ce0388ea81c740df8edf64`;
  its exit was zero.
- No candidate process survived either successful run. The 83 MiB remote
  checkout and local transfer directory were removed after hashing; they are
  disposable and not recoverable. Nothing was installed, registered, deployed,
  or pushed.
- A separate direct-worker-kill diagnostic exposed the next Slice 10 blocker.
  In two macOS reduced runs, the escape topology was valid (the worker and
  grandchild shared the old group and only the child had escaped), but directly
  killing the worker did not reap the same-group grandchild within 30 seconds.
  The surviving PIDs were `68630` and `70174`; the harness `finally` cleanup
  removed them. The failed log SHA-256 values are
  `c019a9c3431645b5f63dc9276a8c7ae7aa5e14bd35d07f510f1119f360403a34`
  and
  `e03408e688944a0737b83748c483071812aa8794716a301cb15949c820bffe91`.
  This did not affect the isolated timeout-driven escape acceptance. It was
  diagnosed and closed by the direct-worker-death work recorded next.

## Production-reliability Slice 10 direct-worker-death validation (2026-07-28)

_Validated exact product/test commit
`8b9644ae8937b33c9bd9de1498dbef6ee7cd88d2`, tree
`66fa36fa870880788cd8c6f5979feba3908f309b`; this was disposable
checkout/archive validation, not installation or deployment._

- The load-bearing regression first ran against the old native broker and
  failed after 30 seconds with both the contained descendant and grandchild
  still live. Its log SHA-256 is
  `bb04a898906d27f8ad5893a9f9da4e2edf1bb237da43d36b20bde25fc9aa72be`.
  Root cause was in `monitor_worker`: after reaping the worker, the broker kept
  polling liveness and control pipes that descendants could retain. The fixed
  broker immediately contains the remaining process group when the worker is
  reaped. The focused guard then passed, and all nine Unix launcher tests
  passed; their log SHA-256 values are
  `10cca9f06aa5ed1db09c860d191547a101117318c1b087a9b989ffd700d5ba75`
  and
  `983c2818412e1b55766c61247f6ca62b93ea777d840432564239153db8471ca2`.
- On `nagatha.local`, the complete server suite passed 1,212/1,212 in 2m24s;
  Pester passed 141 tests with two platform skips; SIEM passed 247/247; the
  complete public handshake passed; scoped formatting, PSScriptAnalyzer, and
  `git diff --check` were clean. The full production acceptance passed 100
  replacements with process count 5/5, handles/fds 543/545, and private
  footprint 327.1/343.8 MiB. Directly killing an executing worker reported
  `outcome_unknown` without replay, killed both descendants, replaced victim
  `83726` with `83744`, and preserved warm sibling `83736`; the escape and
  supervisor hard-kill guards also passed. The server/Pester/SIEM/handshake/
  acceptance log SHA-256 values are respectively
  `14dcca1cd530ebebc621085d7daef9a29fc0c82abe6dc1068ef23e8f5715f32b`,
  `05336aa6382f5cb60311c6d81122ba2c60ccb9f59b98e178490a05ac0d000f09`,
  `392d6cd198117ca30a33c93939a2bac89eef591214bda48fb53bfab273306fbf`,
  `fee4813f527272ec29e319773c85b24806d9caeb779b6aab98f914b5990d0a26`,
  and
  `35dbc29db56b57b4061fa3fa64e754275b3a527b1b97f5723090f2fbbd5d507a`.
- The exact source archive SHA-256 was
  `fb07dcdd096623b014ebea5838c422b1d171aba3caf489273221afb2d29dbb20`
  and matched after every transfer. On `magneto` (Arch Linux
  7.1.4-arch1-1 x86_64, .NET SDK 10.0.110, PowerShell 7.6.3), the complete
  server suite passed 1,212/1,212 in 3m02s. The wrapper then stopped because
  that host has no Pester module; the server-pass-plus-environment-stop log
  SHA-256 is
  `7b55448cafe1ef51781be1d09119d894036f26a9aab7ba6e5e2d76618b5d821d`.
  No package was installed to bypass that host precondition.
- The same verified archive ran Pester 6.0.1 on `gabrielle` (Arch Linux
  7.1.5-arch1-1 x86_64, PowerShell 7.6.3): 141 passed with two platform skips.
  Its log SHA-256 is
  `7877af984bbaa00acc053ea8ccd6321fad6ad1f9f39f8bc3ba18608cb60cc274`.
  Back on `magneto`, SIEM passed 247/247, the public handshake passed, and the
  full production acceptance passed 100 replacements with process count 5/5,
  handles/fds 530/530, and private footprint 245.7/242.4 MiB. Direct worker
  death killed both descendants, replaced victim `245163` with `245262`, and
  preserved warm sibling `245188`; escape and supervisor hard-kill also
  passed. The combined remaining-checks log SHA-256 is
  `c0754c157b244307620a3a82dc5ae55f5de1e7b2566994042c7d97822d417f4c`.
- A focused hard-kill boundary run passed nine cases: worker loss before the
  invoke write; synchronous, asynchronous, and partial failure at or after
  write-call entry; loss after a complete invoke write; loss during a result
  frame; worker exit after a complete valid terminal; a real worker kill after
  an observable effect; and direct broker-owned process-group containment.
  Together with the public during-execution kill above, this covers every
  hard-kill boundary listed in Slice 10 without adding product test hooks. The
  focused log SHA-256 is
  `37cdf80a21df788d3ae235f845669591b73358570fca3b44d427aad13a598934`;
  all nine passed.
- The three remote validation roots and the local transfer root were
  prefix-checked and removed after hashing. No candidate server, worker,
  broker, fixture, or test process survived. Nothing was installed, registered,
  deployed, or pushed. The five already-recorded transitive
  `System.Security.Cryptography.Xml` 10.0.6 advisories were the only dependency
  warnings.

## Production-reliability Slice 11 documentation/integration (2026-07-28)

_Validated and locally integrated exact documentation commit
`2c96e842618d73ac59fa37d5492a5ae92a0d163d` on `nagatha.local`; this was
checkout verification and a local fast-forward, not installation, registration,
deployment, or push._

- Pester passed 141 tests with two platform skips; the complete server suite
  passed 1,212/1,212 in 2m25s; the standalone retained SIEM receiver passed
  247/247.
- The first direct-checkout registration handshake failed before MCP
  initialization because build-on-launch `dotnet run -v q` wrote NuGet
  `NU1903` warnings to protocol stdout. A direct process probe confirmed the
  first stdout line was the project-path advisory, not JSON-RPC. The corrected
  path builds outside the MCP child and then launches
  `dotnet run --no-build --no-launch-profile`; the same complete handshake
  passed with exactly the five intended tools, two isolated named workers,
  exactly-once dispatch, per-session reset, retained output through reset/close,
  audit-disabled startup, and clean output-root shutdown.
- Current `origin/master`
  `c9b11bcb0b4e41a11110c5870562b4980c0b86b3` was the exact merge base. Local
  `master` fast-forwarded to the verified commit, and
  `git diff impl/production-reliability-salvage master` was empty.

## Post-Slice 11 dependency remediation and shaping diagnostic (2026-07-28)

_Validated exact code commit
`b6fcbcdd9a81bdd5ce9e9ba8dde087a2adc02ff3`, tree
`b76e88a94f00eb9af5ffdf91eb4ed6ece06493c2`, on `nagatha.local`; no
installation, registration, deployment, or push occurred._

- Before the fix, every server project resolved
  `System.Security.Cryptography.Xml` 10.0.6 through
  `Microsoft.PowerShell.SDK` 7.6.3 and reported five high advisories. GitHub's
  affected ranges end at 10.0.9, while even PowerShell SDK 7.6.4 still declares
  10.0.6. The direct 10.0.10 override changed
  `dotnet list ... --vulnerable --include-transitive` to no vulnerable packages
  in all five server projects; `dotnet nuget why` and the built dependency file
  both resolved XML crypto and PKCS at 10.0.10.
- Pester passed 141 with two platform skips; server passed 1,212/1,212; SIEM
  passed 247/247; the stdout-clean registration handshake passed with zero
  build warnings. Production acceptance passed 100 replacements with
  process count 5/5, handles/fds 544/546, and private footprint
  323.1/333.8 MiB. Timeout and direct-worker-death containment, process-group
  escape refusal, sibling isolation, and supervisor hard-kill cleanup all
  passed.
- A disposable `osx-arm64` self-contained layout validated its complete public
  handshake and contained 10.0.10 with no 10.0.6 XML-crypto dependency. Its
  prefix-checked temporary root was removed afterward.
- A direct replacement-server diagnostic preserved the requested values from a
  synthetic `Deserialized.Microsoft.Exchange.*` object, returned an explicitly
  selected script property's value, and proved its getter ran once rather than
  again during shaping. A terminating error surfaced its exact message. The
  conservative incomplete marker remained because active type data was not
  inspected. These checks do not replace the real EXO/Outlook Windows gate.
- Both recorded `NETWATCH-01` addresses timed out again before this diagnostic;
  no Windows host state was read or changed.

## Native package target guard (2026-07-28)

_Validated exact code/test commit
`e2beda3c35b6bc4d1a6b1d0191e43ed9957a19f0`, tree
`42dd0feb73366ae81d438ecea4eedef9e84a4431`, on `nagatha.local`; no UTM,
installation, registration, deployment, or push was used._

- A disposable macOS `-LayoutOnly -Rid linux-arm64` probe completed but
  `/usr/bin/file` identified its bundled `PtkWorkerBroker` as
  `Mach-O 64-bit executable arm64`, not Linux ELF. The disposable layout was
  prefix-checked and removed. Ignored compiler intermediates were not treated as
  release evidence.
- `dev-install.ps1` now requires the requested layout RID to equal the build
  host RID before it creates the output directory. The owner has matching
  hardware/runners in the release plan; cross-target native compilation remains
  unsupported rather than silently producing a bad package.
- Removing the new guard made its focused Pester test fail 1/1; restoring it
  passed 1/1. The complete Pester suite passed 142 with two platform skips,
  server passed 1,212/1,212, SIEM passed 247/247, and the stdout-clean
  direct-checkout handshake passed with zero warnings. A same-RID `osx-arm64`
  self-contained layout then passed the complete public handshake.

## Exact-head Linux x86_64 package validation (2026-07-28)

_Validated exact committed head
`37b7d94dacdfd7fb03b52ce95d4409031e6e6699`, tree
`45f7c8a5a9bdbcc907cc3aea2bb47e0bea346301`, from one immutable archive on
real Linux x86_64 hosts; no VM, emulation, installation, registration,
deployment, or push was used._

- The archive was 1,232,753 bytes with SHA-256
  `c3e55114792d53f8edb478351a0b24a93a3890cb94b8e81c55c0be844147fc92`;
  both remote hosts verified that hash before extraction.
- On `gabrielle` with PowerShell 7.6.3 and Pester 6.0.1, the complete Pester
  suite passed 142 tests with two platform skips. The complete 540-byte job
  log SHA-256 is
  `1bbff1e460e463a545e6478801fdec3f79801a77ad8a55266bd53041b0927b6e`.
- On `magneto` with .NET SDK 10.0.110 and PowerShell 7.6.3, the server suite
  passed 1,212/1,212 in 3m03s and SIEM passed 247/247. All five server projects
  reported no vulnerable direct or transitive packages. The direct-checkout
  registration handshake passed with exactly the five public tools and two
  isolated named workers.
- `dev-install.ps1 -LayoutOnly -Validate -Rid linux-x64` built and validated
  the exact staged package on `magneto`. `/usr/bin/file` identified both
  `PtkMcpServer` and `PtkWorkerBroker` as Linux x86-64 ELF executables. Their
  SHA-256 values were respectively
  `9cbd0d1e7625321c4f8af30f8e22a2bd863a366c2ec68d766a81325c2a2be372`
  and
  `f8516a39e68cca1a5ebccc1b43eeb36c1a26aeb0fc735046de73c3a0f8203d47`.
- The staged package passed production acceptance: 100 worker replacements
  retained 5/5 processes and 532/532 file descriptors; timeout and direct
  worker-death paths each contained two native descendants; the observed
  process-group escape faulted `descendants_unknown` and blocked replacement;
  the sibling remained warm; supervisor hard-kill removed four owned
  descendants. The complete 7,984-byte job log SHA-256 is
  `70c9311eecfd30391405f147a21f94b975b96d77d5369d21faadc5f89959d824`.
- Both disposable remote roots and the local transfer root were
  prefix-checked and removed after evidence hashing. A process scan found no
  executable from the validation root before cleanup. The current Windows
  host still timed out at both recorded addresses, and all three available
  Linux hosts are x86_64, so the Windows/Exchange and real ARM64 Linux gates
  remain open.

## Exact-head macOS ARM64 staged-package acceptance (2026-07-28)

_Validated exact committed head
`920944ef3a4491edbf1d2c6a3915a5e39106a8fb`, tree
`aa33b61c70baf2ec2cc6a60e75a606eb5327615c`, in a disposable same-host
package; no installation, registration, client restart, deployment, or push
was used._

- The 1,234,720-byte source archive SHA-256 was
  `b1ffa86f328dfbbb7185a5b2805660947f4c009dfa1b5aa4867c1acb1b9a4ee6`.
  `dev-install.ps1 -LayoutOnly -Validate -Rid osx-arm64` built the staged
  package and its complete five-tool public handshake passed.
- `/usr/bin/file` identified both staged public executables as Mach-O 64-bit
  ARM64. `PtkMcpServer` SHA-256 was
  `633dfbef35d950ca2411421ce25269de1cbe652014a8d1dc67cd7b1a18cd8ed7`;
  `PtkWorkerBroker` SHA-256 was
  `6595946d6e48162c7439e60bb77cddb288ce4c9e13311082fbb91c3754e2e980`.
- The packaged executable passed production acceptance. One hundred worker
  replacements retained 5/5 processes and 531/531 file descriptors. Timeout
  and direct worker-death paths each contained two native descendants; the
  sibling stayed warm; the observed process-group escape faulted
  `descendants_unknown` and blocked replacement; supervisor hard-kill removed
  four owned descendants.
- The complete 3,658-byte job log is
  `/Users/michael/.ptk/jobs/job-7145-1.log`, SHA-256
  `b936074023cf3f6ba268a9085a4da984961f7feb6aa90341106c305a28580c01`.
  A process scan found no executable from the validation root, and the
  prefix-checked disposable root was removed after evidence hashing.

## `ASHBIAMWEB1` — Codex Windows workspace

### RTK router delegation verification host — 2026-08-03

- `rtk 0.44.2` at `C:\Users\mcoelho\.local\bin\rtk.exe`, on `PATH`. RTK source
  checkout for contract reading is at `D:\source\rtk` (branch `develop`).
- Installed PTK remains `0.2.0-dev.g12e1ff5` (`~/.ptk/VERSION`, re-confirmed
  2026-08-03) — the plan's work is committed but not installed here.
- `codex-cli 0.146.0`. Its auth token is revoked: review dispatches complete
  and return valid verdicts while logging repeated
  `Failed to refresh token ... refresh token was revoked` to stderr. Run
  `codex login` before the next dispatch.
- SIEM suite is 226/247 on this host: the 21 failures are the recorded
  symlink-privilege cases, since the ordinary token cannot create Windows
  symlinks.

_Battery re-run 2026-08-03 at `a3112f3` during a `drift` pass; every fact in the
block above still held._ Server 1,059/1,059; Pester 84 passed with 1 platform
skip; SIEM 226/247 with the same 21 symlink-privilege setup failures; no
vulnerable package in any of the five server projects; the registration-command
stdio handshake reported `HANDSHAKE PASSED`. Hosted CI at the same commit was
green on all six jobs, including the RTK install step on all three platforms and
SIEM 247/247 where symlink creation is permitted.

### Installed payload ACL repair — 2026-07-28

- Before `bb2349a`, `C:\Users\mcoelho\.ptk` had a protected DACL whose
  current-user full-control ACE did not inherit; the only inheritable ACE
  belonged to the Codex sandbox group. Installed `~/.ptk/bin/` was unreadable to the ordinary
  user token. The existing supervisor answered `ptk_session list` and
  `ptk_state`, but the first `ptk_invoke` failed with MCP `-32603`, left the
  default session cold, and started no worker. Claude independently reported
  reconnect `-32000`.
- Applying the committed transaction helper to exactly the installed PTK root
  produced one protected explicit current-user full-control ACE with container
  and object inheritance. All 296 installed `~/.ptk/bin/` files became readable. The
  already-running supervisor PID 1732 then started worker PID 9384 and completed
  `Get-Location`; `ptk_state` reported the default session ready with no
  failure or reset requirement.
- Guard proof: with only the product repair temporarily removed, the new Pester
  test failed in `Copy-PtkInstallPath` with access denied on `home\bin`; after
  restoration it passed and verified the exact one-ACE DACL. The install
  transaction fault/rollback script passed.
- Pester, the complete server suite, dependency vulnerability audit, and the
  registered-command handshake passed after the ACL repair. The SIEM suite
  passed 226/247; all 21 remaining cases stopped while creating their test
  symlink with Windows error "A required privilege is not held by the client",
  before product assertions. This token cannot close the symlink-specific
  SIEM gate.
- No installed payload byte, registration, or client configuration was
  replaced. Only the installed PTK root ACL changed; repository code containing
  the durable repair remains uninstalled and unpushed.

### Exact-head Windows x64 production acceptance — 2026-07-28

- Ordinary non-elevated `ANALOG\mcoelho` on Windows Server build
  `10.0.20348.0` x64, PowerShell `7.6.4`, and .NET SDK `10.0.302` validated
  exact head `7eaf8a0cbe391abda7185e23e621fe7b01028886`, tree
  `2c0ae924f5eaf6a4e317ecf06788e9fa05a7abaf`.
- The first 100-cycle checkout acceptance completed all replacements, then
  failed waiting for the timeout child/grandchild witness. A one-cycle run
  reproduced it. Windows cold worker plus two nested PowerShell starts could
  not reliably publish within the hard-coded one-second invoke deadline.
  `6ccdaa7` applies the existing bounded 4–12 second process-tree startup
  allowance and makes the recovery-line assertion CRLF-safe. The old path
  failed twice; the repaired one-cycle and complete 100-cycle runs passed.
- That slice's complete server verification exposed a separate classification
  race: 1,211/1,212 passed while
  `Factory_reports_a_real_initialization_deadline_as_postlaunch_timeout`
  received `worker_start_canceled` instead of `worker_start_timed_out`.
  `7eaf8a0` translates cancellation from the internal linked deadline source
  to `TimeoutException` whenever the caller token was not canceled, instead
  of classifying from a post-cancellation wall-clock comparison. The focused
  guard passed 20/20 and the complete server suite passed 1,212/1,212.
- Final-head Pester passed 144 with one platform skip. The direct public
  handshake passed. The server dependency audit reported no vulnerable direct
  or transitive package in all five projects.
- `scripts/dev-install.ps1 -LayoutOnly -Validate` produced a disposable
  `win-x64` layout version `0.2.0-dev.g7eaf8a0`: 579 files,
  139,203,538 bytes. `PtkMcpServer.exe` was 162,304 bytes with SHA-256
  `1dd7c4b34290b15cd5ef2b64e2ed38fa1c6a4808d64af2606bb70e1643eefb25`.
  The layout handshake, disposable staged and activated install handshakes,
  and install transaction passed.
- The exact packaged executable passed 100-cycle production acceptance:
  baseline/final processes 5/5, handles 1,715/1,712, private bytes
  191.5/181.3 MiB; timed-out worker `3868` was replaced by `17112` after both
  descendants exited; directly killed worker `8768` was replaced by `15960`
  after both descendants exited while sibling `18176` stayed warm; hard-killed
  supervisor `8032` and all six owned descendants exited.
- The SIEM suite passed 226/247. All 21 failures occurred while test setup
  attempted to create symlinks and Windows returned "A required privilege is
  not held by the client", before product assertions. No `.ptk-siem-*` root
  remained.
- A read-only follow-up confirmed the token exposes only
  `SeChangeNotifyPrivilege` and `SeIncreaseWorkingSetPrivilege`, not
  `SeCreateSymbolicLinkPrivilege`. The host is joined to `ad.analog.com`,
  has ExchangeOnlineManagement `3.10.1`, Microsoft.Graph `2.38.1`, and Outlook
  Office16 installed, but has no `ExchangeInstallPath`, `RemoteExchange.ps1`,
  or available `Get-Queue`. No connection, authentication, mailbox read, or
  on-prem Exchange operation was attempted.
- Acceptance and staged-install roots returned to zero, no checkout or
  packaged PTK process remained, and the explicitly verified disposable
  layout under the user's local temp directory was removed. No candidate
  payload was installed, no harness registration changed, and the existing
  installed PTK processes were not stopped.

### Enterprise field validation — 2026-07-29

- The installed worker payload did not include the external PowerShell 7 module
  directory in `PSModulePath`; it found only the legacy inbox module set and
  could not import ExchangeOnlineManagement. Commit `36146a1` discovers the
  module directory beside installed `pwsh`, preserves inherited entries, and
  handles resolved Unix/macOS launcher symlinks. Its guard failed when only the
  product wiring was removed and passed after restoration; the complete server
  suite passed 1,213/1,213.
- With the equivalent session-local module-path workaround on the older
  installed payload, the existing app-only EXO helper authenticated and an
  identity-bound `Get-EXOMailbox` minimum-property read matched only the
  owner-authorized mailbox. Initial completion took 5,438 ms; warm connection
  check took 921 ms and the repeated exact-identity read took 551 ms in the same
  worker. The selected SMTP/type projection retained both requested note
  properties. Its `active_member_not_evaluated` marker is the designed
  conservative signal for uninspected selected-object type data, not evidence
  that either selected value was dropped.
- The host-local Graph helper could not run because its expected credential
  file was absent; the older worker also lacked `COMPUTERNAME`. WAM interactive
  authentication failed because the hosted worker has no parent window handle.
  Two device-code attempts expired after 120 seconds without an authenticated
  account. No Graph API request ran and no directory object was read.
- The older installed worker ran PowerShell in MTA and the Outlook COM probe
  exceeded 120 seconds before producing any account or item record. Its one
  headless `OUTLOOK.EXE -Embedding` residue was identified by PID and creation
  time and removed. Commit `56b562c` makes Windows runspaces STA. The committed
  guard failed as MTA before the product change and passed as STA after it.
  Exact-head COM initialization then returned in one to two seconds and left no
  Outlook process, but this machine's Outlook namespace exposed no current user;
  therefore no Inbox or item metadata was read.
- Verification at `56b562c`: server 1,214/1,214, Pester 144 passed with one
  platform skip, public registered-command handshake passed, no vulnerable
  server packages, changed-file formatting and diff hygiene passed. Independent
  SIEM verification remained 226/247 because all 21 redirect tests failed during
  symlink setup under the ordinary token lacking symlink privilege, before
  product assertions. Repository-temp helpers, redacted status/prompt files,
  Graph helpers, and Outlook processes were removed. Nothing was installed,
  registered, pushed, or changed outside the authorized account-scoped reads.

### Installed activation — 2026-07-29

- The owner explicitly authorized installing the current candidate and killing
  the running installed PTK process if needed. The prior payload was
  `0.2.0-dev.g7d66273`. Supervisor PID 1732 and worker PID 9384 were re-resolved
  immediately before termination as `PtkMcpServer.exe` under the exact
  user-scoped installed `bin` path; both were stopped and no process from that
  payload root remained before activation.
- Default `scripts/install.ps1` installed exact repository head `12e1ff5` as
  `0.2.0-dev.g12e1ff5`. Staged and activated package validation each passed the
  complete five-tool named-session handshake. The installed executable SHA-256
  is `65d2c4ef7eb62384240c1ab0ca151d00b018f4f03571d0d4edaf9815125484de`;
  the installed server DLL SHA-256 is
  `1cc209704d8330ccc1a3b814b8269fc7c863fc10d1a619bf22b00554dd49b570`.
  The user Add/Remove Programs entry reports the same version.
- Claude and Codex registrations both resolve the exact installed executable.
  Default detected-harness initialization installed Claude's hook/guidance,
  Codex guidance, and the detected Gemini/agy PTK plugin. An independent
  registered-command handshake passed after installation. A separate bounded
  stdio smoke against the installed executable proved Windows apartment state
  `STA`, the PowerShell 7 module directory present in `PSModulePath`, and
  ExchangeOnlineManagement discoverable; its named session and process tree
  exited cleanly.
- The Codex connection whose old supervisor was deliberately killed returns
  `Transport closed` and cannot bind the new process inside the same client
  session. Two live Claude parents subsequently launched fresh supervisors from
  the new installed path, proving activation is usable from new connections;
  they were left running. A fresh Claude/Codex session remains required to prove
  stale removed-tool references are gone in the intended harness. The installer
  stage and temporary installed-runtime probe were removed. Nothing was pushed.

## Cross-host self-contained Linux package acceptance (2026-07-28)

_Built one immutable package on `magneto`, then validated those exact package
bytes on `gabrielle` and `altiera`, both of which had PowerShell 7.6.3 but no
.NET SDK. No installation, registration, deployment, or push was used._

- Exact committed head was
  `d243c82d135b390b69fd93b25d01135f4d791a58`, tree
  `14ac333b80d0120f6f3af54a135fe231cb330510`. The 1,235,575-byte source
  archive SHA-256 was
  `8cde0267adc55b7c0060184278539f251e61b6934e6e7d93617f637bc42c142d`.
- `magneto` built and validated the `linux-x64` layout. The resulting
  53,459,626-byte layout archive SHA-256 was
  `d02880e038a2d76ba86b48a7f7b3f3ac8ea0010fc5bae577368184c3800611a5`.
  The complete 2,135-byte build/handshake log is
  `/Users/michael/.ptk/jobs/job-7145-2.log`, SHA-256
  `15725156975d6e943959fafa2c5c3ae64f571c174c1285f2060ce3f1ec1c8501`.
- `gabrielle` ran kernel `7.1.5-arch1-1`; `altiera` ran
  `7.1.3-arch2-1`. Each independently verified both archive hashes, confirmed
  `dotnet` was absent, identified the server and broker as Linux x86-64 ELF
  executables, and passed the complete five-tool package handshake.
- Both hosts then passed the 100-cycle packaged production acceptance.
  `gabrielle` retained 5/5 processes and finished at 532 file descriptors
  versus a 530 baseline; private footprint moved from 219.5 to 209.3 MiB.
  `altiera` retained 5/5 processes and likewise finished at 532 descriptors
  versus 530; private footprint moved from 210.6 to 215.8 MiB. Both changes
  remained within the acceptance bounds.
- On each host timeout and direct worker-death paths contained two native
  descendants, the sibling stayed warm, the observed process-group escape
  faulted `descendants_unknown` and blocked replacement, and supervisor
  hard-kill removed four owned descendants.
- The complete `gabrielle` 3,841-byte log is
  `/Users/michael/.ptk/jobs/job-7145-3.log`, SHA-256
  `c4b2a0431814cb53d802fe7d386d21d5bffedfdb832d1a685be45bdae70843cb`.
  The complete `altiera` 3,833-byte log is
  `/Users/michael/.ptk/jobs/job-7145-4.log`, SHA-256
  `3ec96bb9fd4f6a007930c92e633501d8d99a7fb02604e26faffa1b2aa355acbd`.
  Process scans found no executable from any validation root; all three remote
  roots and the prefix-checked local transfer root were removed after hashing.

## ptk-session PSModulePath truncation (2026-08-05)

Verification launched from inside a ptk warm session on the owner's Mac can
inherit a truncated `PSModulePath`. Observed after an MCP server restart
mid-session: the fresh worker's `PSModulePath` held only the user and
`/usr/local/share` module directories plus `~/.ptk/bin/Modules`, with no
`$PSHOME/Modules` entry. Processes spawned by `dotnet test` inherit that,
and the four StateToolTests module probes fail together (the state banner
lists no `Microsoft.PowerShell.Utility`) while the same class passes 15/15
from a plain shell. The same head passed from the pre-restart session, so
the truncation is a property of how that server instance was launched, not
of the commit under test. When these four fail together from a ptk session,
re-run from a plain shell before treating it as a product regression.
Whether the truncation is product-visible inside `ptk_invoke` child
processes is undecided — not filed as an issue; owner's call.

## Hosted CI runner flakes (GitHub Actions)

Observed 2026-08-05, recorded so the next reader does not re-investigate a
green product as a regression.

- `test (windows-latest)` failed run 31028868581 at `178acea` with
  `System.IO.IOException : The protected external path owner or DACL is
  invalid` from `SecureAuditStorage.VerifyWindowsOwnerOnlyAcl`, in
  `FileAuditJournalSinkTests.Concurrent_factories_converge_on_one_host_identity_without_temporary_files`.
  The commit touched only worker death diagnostics -- nothing near audit
  storage. The immediately following commit (`17a6a44`, same audit code)
  passed all six jobs, and the same suite passed locally on Windows at both
  commits. Treated as a runner-environment flake in the audit ACL fixture,
  not a product failure. A recurrence with a product change nearby deserves a
  fresh look.
