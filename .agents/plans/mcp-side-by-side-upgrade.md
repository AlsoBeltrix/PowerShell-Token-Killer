# Plan: Non-disruptive side-by-side MCP runtime upgrades

**Status:** DRAFT — implementation is not authorized. The owner-approved Claude
Opus 5 openreview over `c4bd2af..caf467e` completed intake with eight admitted
and two declined candidates. The `ssu-1` native-launcher decision is settled;
the `ssu-3` per-harness migration decision is also settled. Four other
plan/product findings and one citation correction remain open. One admitted
metadata finding was already resolved. The next owner gate is `ssu-4`.
Codereview remains deferred until Slice 0 has implementation and deterministic
guard proof.

## Goal

An ordinary PTK upgrade must not terminate an MCP supervisor that is already
connected to a client. Existing conversations and their warm named sessions
continue on the exact runtime they started. New conversations start the newly
activated runtime. Failed installation before activation leaves the prior
selection and every managed harness registration usable.

## Current failure and evidence

- `scripts/dev-install.ps1:128` rejects installation while a PTK server under
  `~/.ptk` is running.
- `scripts/dev-install.ps1:481` applies that guard to every install.
- The current transaction replaces `bin/`, `src/`, `scripts/`, and `VERSION`
  in place. Windows therefore requires the live installed executable to exit.
- Killing that process closes the client-owned stdio transport. Starting a
  replacement process cannot attach it to the original pipes.
- Codex has already retained a dead or stale transport after this boundary;
  GitHub #9 and #11 remain open.

## Governing decisions and constraints

1. The single per-user PTK home remains `~/.ptk`.
2. The public transport remains client-owned stdio. This plan adds neither a
   daemon nor a guardian that parses or proxies MCP.
3. The public five-tool names and schemas do not change.
4. No active supervisor, worker, or warm session migrates between versions.
5. No in-flight request is replayed by installer or launcher machinery.
6. An upgrade must not need process enumeration, process termination, or
   successful deletion of an old payload.
7. User-owned files under `~/.ptk` remain untouched.
8. Existing rollback guarantees cover handled installer failures. Power loss
   at an arbitrary machine instruction is not newly claimed crash-atomic.
9. A custom user registration that does not match a PTK-managed target is
   preserved and reported, never silently overwritten.

## Architecture

### Stable client command

Every PTK-managed harness registration points to:

```text
~/.ptk/launcher/ptk-launch(.exe)
```

`ptk-launch` is a packaged, per-RID native executable and a per-client process
launcher, not a shared service. It requires no separately installed PowerShell
or .NET runtime. It:

1. reads one bounded installer-owned activation record;
2. resolves one fixed-layout runtime below `~/.ptk`;
3. on Unix, replaces itself with that runtime while preserving stdin, stdout,
   stderr, arguments, and frozen runtime-attribution environment values;
4. on Windows, creates the runtime with inherited stdin, stdout, and stderr,
   assigns it before resume to a launcher-owned non-inherited kill-on-close Job
   Object, waits for it, and returns its exit code;
5. emits no stdout of its own; and
6. on any launcher failure, writes one bounded sanitized diagnostic to stderr
   and exits nonzero.

The launcher does not parse MCP, buffer JSON-RPC, retry calls, hold sessions,
choose tools, or survive its child. It reads activation once before the child
starts. Changing activation cannot affect a running connection.

This boundary is conditional on Slice 0 proving native-handle inheritance and
teardown. Failure of that proof stops the plan and returns to the owner; it does
not authorize a symlink/junction or multi-registration fallback.

### Installed layout

```text
~/.ptk/
  active.json                         installer-owned
  launcher/                           installer-owned stable native control
    ptk-launch(.exe)
  scripts/                            installer-owned stable control files
    ptk-hook.ps1
    ptk_init.ps1
    dev-install.ps1
    ptk_install_transaction.psm1
  versions/                           installer-owned immutable runtimes
    <rid>/
      <payload-digest>/
        bin/
        src/
        VERSION
        manifest.json
  bin/ src/ VERSION                   one-time legacy payload, retained
  policy.psd1 and other unknown files user-owned, untouched
```

The semantic version is display metadata, not directory identity. The runtime
directory identity is its RID plus the lowercase SHA-256 digest of a canonical
manifest over every installer-owned runtime file. Rebuilding the same version
with different bytes cannot collide.

An existing runtime directory is reusable only when its manifest and every file
match exactly. Any mismatch fails closed; the installer never merges or repairs
individual files in an immutable runtime.

### Activation record

`active.json` is UTF-8 without BOM, at most 4096 bytes, and has exactly:

```json
{
  "format": "ptk.active/1",
  "layout": "legacy | versioned",
  "rid": "win-x64",
  "payload_digest": "64 lowercase hex or null",
  "version": "display-only string"
}
```

Rules:

- duplicate, missing, or unknown properties fail;
- `rid` must equal the local runtime RID;
- `layout=legacy` requires a null digest and resolves only
  `~/.ptk/bin/<server-name>`;
- `layout=versioned` requires 64 lowercase hex and resolves only
  `~/.ptk/versions/<rid>/<digest>/bin/<server-name>`;
- no record field is treated as a path;
- every resolved component must remain under the canonical PTK home and must
  not be a link or reparse point; and
- a versioned record must match the runtime manifest before launch.

Activation uses a temp file in `~/.ptk`, flushes it, and replaces `active.json`
by same-directory atomic rename. The old complete record or the new complete
record is visible; a partial record is not.

### Runtime attribution

The launcher passes these frozen values to the child:

- `PTK_RUNTIME_LAYOUT`
- `PTK_RUNTIME_RID`
- `PTK_RUNTIME_DIGEST`
- `PTK_RUNTIME_VERSION`
- `PTK_RUNTIME_ROOT`

The runtime validates them against `AppContext.BaseDirectory`; inconsistent
values fail before MCP initialization. `ptk_state` adds one supervisor line
with runtime version, RID, abbreviated digest, and layout. This is output-only;
the tool name and input schema remain unchanged.

## Transaction and migration

### Install lock

One exclusive per-user installer lock under `~/.ptk` serializes install,
upgrade, prune, and uninstall. It does not lock runtime launch or require
running supervisors to cooperate.

### Phase A: prepare without changing launch selection

1. Refuse elevation and validate ownership/ACL of `~/.ptk`.
2. Acquire the install lock.
3. Publish into a protected same-volume staging directory below
   `~/.ptk/.staging/<guid>`.
4. create and validate the canonical runtime manifest;
5. run the complete five-tool handshake directly against the staged binary;
6. move the complete runtime directory to its immutable digest identity;
7. snapshot installer-owned control files, `active.json`, managed registration
   files, and Windows ARP state using the existing transaction machinery; and
8. install the launcher and other stable control scripts.

Moving an already verified runtime into `versions/` is not activation. A failed
later step may leave the immutable inactive runtime for reuse.

### Phase B: one-time registration migration

On a legacy installation:

1. write an activation record with `layout=legacy`;
2. migrate missing or recognized PTK-managed registrations one harness at a
   time using the harness-specific transaction below;
3. preserve and report custom `ptk` registrations;
4. immediately run the full registered-command handshake through each changed
   registration; this still launches the legacy runtime; and
5. on any handled failure, restore control files, activation record,
   every changed harness registration file, and ARP state byte-for-byte.

Recognized managed targets are limited to:

- the old `~/.ptk/bin/PtkMcpServer(.exe)` command;
- a binary below `~/.ptk/versions/<rid>/<digest>/bin/`; or
- the exact stable launcher command and arguments.

No new runtime becomes active during registration migration. A crash or client
launch during this phase can start only the legacy runtime.

Fresh installs skip the legacy record. They register the stable launcher only
after the first versioned record has been prepared in the transaction snapshot.

### Phase C: final version activation

All fallible validation, registration, control-file, and ARP changes complete
before activation. The last correctness-changing operation is the atomic
replacement of `active.json` with the candidate versioned record.

After that replacement:

- installation is committed;
- no later exception may trigger rollback to the old record;
- cleanup and human-readable reporting are best-effort and non-throwing; and
- existing processes continue from their already opened old payload.

A handled failure before replacement restores the old activation and managed
external state. The candidate runtime may remain inactive and immutable.

### Subsequent upgrades

Managed registrations and the launcher command remain unchanged. Upgrade stages
and validates a new immutable runtime, updates control/ARP state, then replaces
only `active.json`. No running-process check occurs on this path.

## Registration behavior

Refactor `scripts/ptk_init.ps1` so registration is represented as command plus
argument vector rather than one binary path.

Add an installer-only refresh mode with explicit test seams. Its common
protocol is:

1. read and classify the current registration before any mutation;
2. snapshot every harness file that can change, including absence;
3. prove the installed native launcher directly;
4. leave an exact launcher registration unchanged;
5. preserve a custom registration and return `custom-preserved`;
6. create a missing registration or migrate a recognized managed registration
   with the harness-specific method below;
7. run the registered-command five-tool handshake immediately; and
8. on mutation or handshake failure, restore every harness snapshot
   byte-for-byte and, when a working registration existed, prove its command
   still works.

Harness-specific mutation:

- **Claude Code:** after `claude mcp get ptk` identifies a recognized managed
  target and the user registration files are snapshotted, the CLI's user-scope
  remove/add sequence may replace it. Any remove, add, or handshake failure
  restores the exact snapshot directly; rollback never depends on another CLI
  call. `scripts/dev-install.ps1` no longer owns a separate unprotected
  Claude-only remove/add path.
- **Codex:** never call `codex mcp remove ptk` during migration. Replace only
  `command` and `args` in the recognized `[mcp_servers.ptk]` base table using a
  deterministic, header-scoped TOML mutation. Preserve all other base keys,
  every `[mcp_servers.ptk.tools.*]` approval subtable, and unrelated
  configuration. Duplicate, inline, or unknown registration shapes fail closed
  without writing.
- **Grok:** before any live mutation, run the installed `grok` CLI's user-scope
  add and remove forms against a disposable home/config containing unrelated
  sentinels. Require the expected `[mcp_servers.ptk]` shape, successful removal,
  and preservation of unrelated values. If that proof fails, preserve the live
  registration and fail the install before activation. After proof, the
  snapshotted live entry may use the verified remove/add sequence; any failure
  restores the exact live snapshot.
- **Agy:** stage and replace the PTK-owned plugin registration file when the
  plugin owns registration. For a recognized managed global
  `mcpServers.ptk` object, update only that JSON object and prove every unrelated
  value is unchanged. Preserve a custom global object. Any failure restores the
  plugin directory and global config snapshots.

Claude, Codex, Grok, and Agy tests cover missing, exact, recognized managed, and
custom registrations; quoting and spaces on Windows and Unix; failure after
mutation but before handshake; byte-identical rollback; and isolation so one
harness failure changes no other harness.

## Retention, pruning, and uninstall

Ordinary install never deletes:

- the previously active version;
- any inactive version;
- the one-time legacy `bin/`, `src/`, or `VERSION`; or
- an unknown/user-owned path.

This deliberately trades disk for deterministic continuity.

Add explicit `-PruneInactive` maintenance:

1. acquire the installer lock;
2. read and validate the active record;
3. fail closed if any process named `PtkMcpServer` has a path below `~/.ptk`,
   or if a candidate process path cannot be inspected;
4. delete only inactive digest directories and, after versioned activation,
   the recognized legacy payload entries; and
5. never delete stable control files, active runtime, or unknown paths.

Prune does not kill processes. Uninstall retains the existing no-running-server
precondition, removes managed registrations/control/runtime payloads, and keeps
unknown user-owned files.

## Implementation slices

Each slice is one reviewable commit. Do not start the next slice until the
previous slice has its guard proof and passes the scoped verification.

### Slice 0 — launcher feasibility gate

Files:

- new `server/PtkMcpServer/Native/ptk_launcher.c`
- `server/PtkMcpServer/PtkMcpServer.csproj` native build/publish wiring
- new disposable launcher lifecycle/stdio test script under `server/`
- `server/PtkMcpServer.Tests/InstallerTransactionTests.cs` or a dedicated
  launcher integration test wrapper

Prove on Windows first, then macOS and Linux:

1. exact five-tool handshake works through the launcher with no launcher stdout;
2. binary stdout bytes are forwarded unchanged and unbuffered;
3. client EOF terminates runtime and all workers;
4. hard launcher termination cannot orphan runtime or workers;
5. child exit status is propagated;
6. activation is read once per launch; and
7. paths with spaces and non-ASCII characters work; and
8. the packaged registered command completes the handshake with `pwsh` absent
   from `PATH` and no separately installed .NET runtime.

Any orphan, protocol mutation, or unbounded teardown fails the architecture.

### Slice 1 — immutable layout and activation primitives

Files:

- `scripts/dev-install.ps1`
- `scripts/ptk_install_transaction.psm1`
- `server/test-install-transaction.ps1`
- `server/test-staged-install.ps1`

Add strict manifest generation/validation, runtime identity, activation
read/write/replace, installer locking, and failure injection immediately before
and during the atomic replace.

### Slice 2 — runtime attribution

Files:

- `server/PtkMcpServer/Program.cs`
- `server/PtkMcpServer/Sessions/WorkerSupervisor.cs`
- focused C# tests for environment/path consistency and `ptk_state`

Reject inconsistent launcher metadata before MCP startup and expose exact
runtime attribution in state output.

### Slice 3 — ownership-aware registration migration

Files:

- `scripts/ptk_init.ps1`
- `tests/PwshTokenCompressor.Tests.ps1`
- `scripts/dev-install.ps1`

Unify registration command construction, add managed/custom classification, and
prove every supported harness converges or preserves custom state.

### Slice 4 — side-by-side transaction

Files:

- `scripts/dev-install.ps1`
- `scripts/ptk_install_transaction.psm1`
- installer transaction and staged-install tests
- new end-to-end side-by-side upgrade test

Implement legacy registration migration, final activation ordering, handled
failure rollback, and inactive candidate reuse.

### Slice 5 — offline pruning and uninstall

Files:

- `scripts/dev-install.ps1`
- focused prune/uninstall tests

Implement fail-closed offline prune and update uninstall for versions/control
layout without weakening its no-running-server precondition.

### Slice 6 — documentation and acceptance

Files:

- `README.md`
- `server/README.md`
- `.agents/plans/release-distribution.md`
- `.agents/state.md`
- `.agents/machines.md`

Replace the old wholesale-root layout contract, document version pinning and
disk retention, and record exact-host evidence.

## Required guards

### Transaction fault matrix

Inject failure at each boundary:

- staging validation;
- immutable directory publication;
- launcher/control-file installation;
- each managed harness registration;
- registered-command legacy smoke;
- ARP update;
- activation temp write;
- activation replace before commit; and
- post-commit reporting/cleanup.

Before commit, every failure restores the prior activation, registration, ARP,
and stable control state. After commit, reporting failure must not roll back the
new activation.

### Live-upgrade acceptance

Using disposable homes and real packaged binaries:

1. launch runtime A through the stable launcher;
2. open a named session, set warm state, and start a bounded in-flight call;
3. install runtime B while A remains connected;
4. prove A's PID, version, session identity, warm state, and call outcome remain
   unchanged and the call occurred once;
5. launch a second client and prove it runs B with the same five-tool schema;
6. close B and prove A remains usable;
7. close A and prove all workers exit; and
8. run offline prune and prove only B plus stable control/user files remain.

On Windows, keep A's executable open through the whole install to prove no
locked file is replaced or deleted.

### Rollback with a live old runtime

With A connected, inject a pre-activation failure while installing B. Prove:

- A remains usable throughout;
- `active.json` and all managed registrations are byte-identical to the prior
  state;
- a newly launched client still selects A;
- B is inactive; and
- no process was killed.

### Guard-proof discipline

For every new behavior guard, temporarily restore the pre-slice implementation,
prove the guard fails for the intended reason, restore the slice, and prove the
guard plus the scoped suite pass. Do not accept a test that passes against the
old in-place installer.

## Verification

Scoped verification after every slice, then before completion:

```text
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
dotnet test server/PtkMcpServer.slnx
dotnet test siem/PtkSiem.slnx
dotnet list server/PtkMcpServer.slnx package --vulnerable --include-transitive
pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand -TimeoutSec 90
```

Also run staged-install, installer fault-matrix, live-upgrade, rollback, and
offline-prune acceptance on exact packaged output. The Windows live-upgrade
gate is mandatory before installation on the owner's account. macOS ARM64 and
Linux x64 must pass before a cross-platform completion claim.

## Explicit non-goals

- Same-conversation adoption of the new runtime.
- Migration or sharing of warm sessions.
- MCP reconnect, replay, proxying, or schema negotiation.
- Guardian/private-host resurrection.
- Shared daemon or machine-wide service.
- Symlink, junction, or hard-link activation.
- Automatic background update or automatic pruning.
- Killing a process during ordinary install or prune.
- Reusing a semantic version directory with different bytes.

## Openreview decision status

The owner settled `ssu-1` on 2026-07-29: the permanent registration boundary is
the native self-contained launcher defined above, not a PowerShell launcher.
The owner also settled `ssu-3`: registration migration uses the cautious
harness-specific transaction above, not a uniform remove/add sequence. These
decisions authorize plan finalization only. Implementation still requires a
separate explicit go after all admitted review findings are closed. The next
plan decision is `ssu-4`.
