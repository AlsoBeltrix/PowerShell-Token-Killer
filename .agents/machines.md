# Machine State

Machine-specific, nonportable facts only. Date each verification; prune stale
entries during a `drift` pass.

## `nagatha.local` — Michael's Mac

_Last verified 2026-07-11 against repo base `78779b0`._

- The live server runs from `~/.ptk/bin/PtkMcpServer`; installed version is
  `0.2.0-dev.g6db333c`.
- Installed `ptk_init.ps1`, `ptk-hook.ps1`, and
  `PwshTokenCompressor.psm1` hash-match the checkout. No product file
  changed between `6db333c` and `78779b0`.
- The Claude and Codex guidance blocks contain the current PowerShell-dialect
  and raw-recovery wording, and the Claude hook points at the installed copy.
  No dev-install or `ptk_init` rerun is pending on this Mac.
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

- Current Windows payload and guidance status remains unknown, including on
  `NETWATCH-01`; the probe deliberately did not inspect `F:\dev` or installed
  PTK files. The former combined Mac/Windows reinstall claim was falsified on
  the Mac and is not evidence for any Windows host; verify the target payload
  directly before taking install action.

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
