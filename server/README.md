# PtkMcpServer

`PtkMcpServer` is a stdio MCP supervisor for isolated warm PowerShell sessions.
One MCP connection may own up to eight sessions, including the lazy `default`
session. Each session runs in its own contained worker process with one
serialized PowerShell runspace, so variables, imported modules, functions,
working directory, environment drift, and established connections persist
inside that session without leaking into another.

The supervisor owns the public MCP pipe, session registry, output store,
admission, and worker replacement. On Unix, `PtkWorkerBroker` creates each
worker as a process-group leader and proves cleanup before replacement. On
Windows, each worker is created inside a Job Object before user code can run.

During each worker's runspace priming, PTK freezes the compressor source in
memory, captures its shaping command, and detaches the module from the
user-visible session. Routing and dialect preflight execute as C# parser logic
over data-only command facts captured through CLR APIs; no PowerShell command
or scriptblock runs before dispatch authorization. User scripts therefore
cannot replace preflight through shadowing, debugger hooks, type data, or later
module file edits. If the module cannot be found or loaded, calls fall back to
plain `Out-String` output and the worker reports the problem.

## Prerequisites

- .NET SDK 10.x (`dotnet --list-sdks`)
- Network access on first build for NuGet restore
- PowerShell 7.x (`pwsh`) for the handshake script and hook installer
- An MCP client that can launch stdio servers, such as Claude Code

The server itself hosts PowerShell through the `Microsoft.PowerShell.SDK`
package pinned in `server/PtkMcpServer/PtkMcpServer.csproj`.

## Setup

Verify the server before registering it broadly:

```powershell
dotnet test server/PtkMcpServer.slnx
pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand -TimeoutSec 90
```

The handshake builds first, then starts the server through the same
`dotnet run --no-build --no-launch-profile` command used by the MCP
registration, and must end with `HANDSHAKE PASSED`. Building outside the MCP
child keeps restore/build warnings off protocol stdout, and bypassing launch
profiles prevents later development settings from changing that launch. The
explicit `-TimeoutSec 90` gives cold build/startup work room to finish.

A checkout has no project-scope registration (the committed `.mcp.json` is
deliberately empty). Install and register user-wide with
`pwsh -File scripts/install.ps1` (builds a self-contained binary into
`~/.ptk` and registers it), or register the checkout directly:

```powershell
dotnet build <path-to-repo>/server/PtkMcpServer -v q --nologo
claude mcp add ptk --scope user -- dotnet run --no-build --no-launch-profile -v q --project <path-to-repo>/server/PtkMcpServer
```

Repeat the build after changing source. Do not remove `--no-build` or
`--no-launch-profile`; build warnings on stdout corrupt the JSON-RPC transport,
and a later launch profile could introduce another unsafe startup side effect.

Check with `claude mcp list`; remove with `claude mcp remove ptk`.

## Tools

| Tool | Arguments | Purpose |
| --- | --- | --- |
| `ptk_invoke` | `script`; optional `raw`, `route`, `timeoutSeconds`, `session` | Execute the original command once in the selected warm session. Same-session queue wait and execution share one timeout budget. The legacy `raw` flag is deprecated compatibility telemetry and does not change routing or shaping. |
| `ptk_output` | `action` (`read`/`search`/`status`/`list`); `handle` required except for `list`; optional `offset`, `maxBytes`, `pattern`; optional `session` for `list` only | Discover, read, search, or inspect immutable same-invocation artifacts. It accepts no script, starts no worker, and never reruns a command. |
| `ptk_state` | optional `listAvailable`, `session` | Report the exact packaged build identity, supervisor health and selected-session state, worker PID, engine, cwd, modules, and drift. It never starts a cold session and never queues behind a busy selected session. |
| `ptk_reset` | optional `session` | Replace one idle session worker with a fresh contained worker and factory runspace. It refuses while that session is busy or old containment is unconfirmed. |
| `ptk_session` | `action` (`list`/`open`/`close`); optional `name` | List the connection-local registry, open one named session, or close one idle named session. `default` is lazy and cannot be closed; at most eight sessions may be open. |

## Audit status

Auditing is mandatory and fail-closed. Every supervisor boot opens a local
audit journal (default `~/.ptk/audit`, override `PTK_AUDIT_ROOT`) before
serving any tool call: with a healthy root, every invoke is journaled durably
before it runs — including the **exact submitted script bytes** as protected
evidence — and with an unwritable or refused root the transport stays up but
every invoke is refused fail-closed until the root heals. `ptk_state` reports
real audit health.

**Protect the audit root as sensitive data.** Journaled scripts can contain
passwords, tokens, and customer data; the store is created owner-only, and
backups or copies of it need the same protection.

SIEM export is optional and asynchronous — configured via `export.json` in
the audit root, the loopback web UI's settings page (default port 8317), or
`PTK_AUDIT_EXPORT_KIND` / `PTK_AUDIT_EXPORT_ENDPOINT` /
`PTK_AUDIT_EXPORT_TOKEN` — and never gates execution. See
[the audit, export, and receiver contract](AUDIT-EXPORT.md) for the full
surface, including `PtkAuditAdmin` and the standalone SIEM receiver.

`ptk_invoke` returns command output, then labeled sections when present, in
this order: `[exit] N`, `[stderr]`, `[errors]`, and `[warnings]`. Empty
output returns `(no output)`. `[stderr]` is neutral, not a failure signal:
native tools routinely write progress and diagnostics to stderr while
succeeding (an exit-0 test run, for example), so native stderr is reported
under its own label. `[errors]` is reserved for genuine PowerShell error
records (`Write-Error`, exceptions, terminating errors).

## `ptk_invoke` Behavior

By default, `ptk_invoke` executes with `route=auto` and `raw=false`.

Routing rewrites eligible native commands through
[rtk](https://github.com/rtk-ai/rtk), an external CLI whose per-command
filters compress the output of common tools (`git`, `npm`, `docker`, ...) at
the source and pass through commands it does not recognize. The rewrite
(`<cmd>` becomes `& '<rtk>' <cmd>`) executes inside the warm runspace, so the
runspace's current directory and environment still apply.

Routing rules:

- RTK decides. PTK submits the exact submitted text to
  `rtk hook check --agent ptk` and either runs the rewrite RTK returns or runs
  the original unchanged. It does not judge eligibility from the PowerShell
  AST and does not resolve executables against PATH.
- RTK rewrites each segment of a compound command it recognizes and preserves
  the rest, so `git status && cargo test` routes both halves. Heredocs,
  multi-line blocks, pipelines, and unknown commands are declined by RTK and
  execute exactly as PowerShell.
- An accepted rewrite must reduce to the submitted text once RTK's inserted
  `rtk ` prefixes are removed, and every wrapped name must resolve to a native
  application in the selected session. Either check failing declines the whole
  rewrite; a partially applied rewrite is never executed.
- `rtk` itself is not double-routed.
- PTK freezes RTK's canonical path, bounded SHA-256 identity, and Unix mode at
  server startup, and binds that absolute path into the accepted rewrite, so
  warm-session `PATH`/`PTK_RTK_PATH` changes cannot substitute a different
  binary. Identity or availability loss before a routed process starts takes
  the exact-original fallback once; PTK never asks the model to reconstruct
  the command and never retries after start.
- Bash is not inferred. PTK does not detect, refuse, validate, or delegate a
  dialect. A user who needs bash invokes it explicitly as an ordinary native
  command (`bash -lc '...'`), and rtk sees that like any other command. The
  deprecated `raw=true` flag is inert compatibility telemetry and does not
  affect interpreter, routing, process choice, capture, or shaping.
- PTK emits no post-success command advice. A mixed native/PowerShell pipeline
  executes exactly as submitted and returns no rewritten-command suggestion.

Output shaping:

- Object output compresses with `Compress-PtcObject`.
- Plain strings and primitive scalars pass through with ANSI/control
  sequences stripped, otherwise unaltered; pathologically large text is
  elided to a labeled head+tail window. When PTK successfully seals a
  same-invocation snapshot, the response names an opaque `ptk_output` handle
  that can recover the elided middle without rerunning the command; otherwise
  it explicitly reports recovery unavailable.
- Log-shaped text routes through `rtk log` when possible.
- Log-shaped text falls back to labeled raw text if `rtk` is absent or fails.
- The host passes the exact startup-frozen identity into shaping, bounds the
  rehash to a regular nonsymlink file of at most 128 MiB, checks Unix mode
  drift, and validates the returned routing envelope against the authorized
  digest.
- Nonzero native exit codes are reported as `[exit] N`.

Overrides:

- `raw=true` is accepted only as deprecated compatibility telemetry. It does
  not change dialect handling, interpreter, routing, process choice, capture,
  or shaping.
- `route=pwsh`, independently of `raw`, is explicit consent to interpret the
  exact original text as PowerShell; normal capture and shaping still apply.
- `route=rtk` asserts the `rtk` rewrite only for the safe single-application
  shape. Ineligible or unavailable routing is labeled and executes the exact
  original once through PowerShell.
- When a response supplies a `ptk_output` handle, use it to read the immutable
  same-invocation artifact. `ptk_output` never executes or reruns the command.
- If response delivery is lost after execution, call
  `ptk_output(action="list", session="<name>")` on the same MCP connection.
  It returns at most the ten newest readable retained artifacts, newest first;
  omit `session` to list across the connection. Listing starts no worker and
  does not extend retention.

Long-running work:

- `timeoutSeconds` raises the per-call timeout, capped by
  `PTK_MAX_CALL_TIMEOUT_SECONDS`, for work that needs the selected warm
  session. Same-session queue wait counts against the same budget.
- A call that expires while still queued is proved not started and leaves warm
  state intact.
- A call that times out after execution starts is not replayed. PTK contains the
  old worker process tree and replaces only that session worker, so that
  session's warm state is lost while sibling sessions remain unchanged.
- Every process started by `ptk_invoke`, including one launched through
  `Start-Process`, belongs to the selected worker's containment tree. It is not
  a supported detach path and is terminated on executing-call timeout, reset,
  close, worker replacement, or server shutdown.
- PTK has no public background-job tool. Start cold stateless watchers and
  deploys outside PTK through the harness's ordinary process facilities,
  redirect results to caller-chosen files, and poll them there. Work that needs
  warm-session state must use a dedicated PTK session and a sufficient
  foreground timeout; it cannot also outlive that worker.

## Claude Code Hook

`scripts/ptk_init.ps1` installs a Claude Code `PreToolUse` hook that redirects
ordinary Bash and PowerShell tool calls toward the `ptk_invoke` MCP tool using
a deny-with-guidance response. The guidance names the tool without a harness
prefix — the same tool carries a different id per harness
([docs/harness-support.md](../docs/harness-support.md)).

The script is the multi-harness init surface (`-Agent claude|codex|grok|agy|all`,
defaulting to the agents detected on the machine). All four legs are
implemented: claude (hook + guidance block), codex (idempotent `codex mcp
add` + `~/.codex/AGENTS.md` block; no hook — trust-gated), grok
(`grok mcp add -s user` behind a config-presence short-circuit; its
guidance home is `~/.claude/CLAUDE.md`, which grok session-loads), and agy
(a user-level plugin directory carrying registration + rules; no hook —
enforcement is deferred until a live install run demonstrates agy's
documented deny surface). `install.ps1` chains this script by default:
one command per machine produces the whole state.

```powershell
pwsh -File scripts/ptk_init.ps1              # user-level install (default)
pwsh -File scripts/ptk_init.ps1 -Show        # inspect per-leg status
pwsh -File scripts/ptk_init.ps1 -DryRun
pwsh -File scripts/ptk_init.ps1 -Uninstall   # hook out, nudge block out
pwsh -File scripts/ptk_init.ps1 -Local       # per-repo opt-in (warns, see below)
```

A bare install ships every layer the leg supports — for claude the hook
AND the `~/.claude/CLAUDE.md` guidance block (also grok's nudge home); for
codex the registration and the `~/.codex/AGENTS.md` block. There is no
opt-in flag for the guidance block: it is idempotent, marker-owned,
conditionally worded, and removed by `-Uninstall`.

Installs are **user-level by default** (`~/.claude/settings.json`; the old
`-Global` switch is accepted and means the same thing). `-Local` is the
explicit per-repo opt-in: it edits the repo's `.claude/settings.json`, and
any tooling that tracks that file by content — governance refresh
mechanisms, dotfile managers — will treat the repo as owner-modified from
then on; the installer warns about this. `-Show`/`-DryRun`/`-Uninstall`
operate on the same target the install form would.

The installer refuses to install the hook while no installed payload exists
at `~/.ptk` (run `scripts/install.ps1` first): a redirect hook without a
server would deny every shell call while steering at a tool that cannot
answer. It preserves unrelated hooks and replaces only the ptk-owned entry
when re-run. The hook takes effect at the next Claude Code session start.

Failure semantics, precisely: the hook fails open only against its OWN
failure — if the hook script is missing or errors, harness shell calls
proceed normally. A down server does not fail open: shell calls are still
denied — but the hook checks for a running server process, and when none
exists the deny guidance says so and points at `PTK_DIRECT` up front
(liveness shapes the wording only, never the decision). `PTK_DIRECT` is the
way through until the harness has replaced the dead MCP transport. The hook
cannot restart that transport itself; production deployment must verify the
intended harness's reconnect behavior.

The missing-script fail-open is exactly what a **stale registration**
produces: an entry written from a checkout that later moved fails open
silently on every shell call. The installer registers the installed copy
(`~/.ptk/scripts/ptk-hook.ps1`) to make that class structurally rare, and
`ptk_init.ps1 -Show` flags a registered target that no longer exists. Two
heal paths: re-running `ptk_init.ps1`, or a `install.ps1` install —
the latter refreshes an existing hook entry only when it also registered
the server with Claude Code (no claude CLI → no refresh; run
`ptk_init.ps1 -Agent claude` yourself after registering manually).

A command containing `PTK_DIRECT` bypasses the hook. Use that for work that
genuinely needs the harness shell, such as interactive or TTY-dependent tools,
or when the ptk MCP server is unavailable.

## Configuration

Set these in the MCP registration `env` block when defaults do not fit:

| Variable | Default | Meaning |
| --- | --- | --- |
| `PTK_CALL_TIMEOUT_SECONDS` | `300` | Default per-call limit: a total wall-clock budget covering same-session queue wait plus execution. Queue expiry is proved not started; execution overrun replaces only that session worker. |
| `PTK_MAX_CALL_TIMEOUT_SECONDS` | `3600` | Cap on the per-call `timeoutSeconds` override. |
| `PTK_OUTPUT_ROOT` | `~/.ptk/output` | Parent for supervisor-owned immutable output-artifact roots. |
| `PTK_MODULE_PATH` | auto-discovered `src/PwshTokenCompressor.psd1` | Explicit module manifest to import into the runspace. If set to a missing file, shaping is disabled. |
| `PTK_RTK_PATH` | `rtk` on `PATH` | Explicit `rtk` binary for native routing and log shaping. If set to a missing file, `rtk` is treated as absent. |

## Operational Notes

- Calls serialize within one session; different sessions can run concurrently.
  The per-call timeout is a wall-clock budget over the whole request — same-
  session queue wait included — and deadlines are re-checked when timers fire,
  so a machine that sleeps mid-call times the call out promptly on wake instead
  of silently extending it.
- `useLocalScope: false` is intentional, so assignments and imported modules
  persist into later calls.
- `ptk_reset` and an execution timeout create a fresh contained worker and
  primed runspace for only the selected session. A queue expiry is different:
  the call never ran and warm state survives.
- Cancellation before dispatch leaves the worker usable. Once request bytes may
  have reached the worker, PTK never replays the command and may replace that
  worker to recover a trustworthy transport.
- Losing a session worker does not close the public MCP connection. When
  containment confirms no descendants survived, the supervisor replaces only
  that session's worker, reports `warm_state_lost`, and never replays ambiguous
  work. During replacement, `ptk_state` remains callable and may report
  `session_recovering`; sibling sessions remain available. If descendant
  cleanup cannot be confirmed, the affected session faults instead of being
  replaced unsafely.
- The public `PtkMcpServer` process owns the client stdio connection. If that
  process exits or is killed, the connection is irreversibly closed; a new
  server cannot attach to the old pipes. Start a fresh harness session if the
  client retains the dead tool entry. PTK cannot restart its own public
  transport.
- Child native processes inherit EOF for stdin instead of the MCP JSON-RPC pipe,
  so stdin-reading commands do not hang forever waiting on the transport.
- No interactive prompts can be answered inside the server. Use unattended auth
  patterns for connection-bearing modules, or run those commands outside the
  server.

## Security Posture

The server is not a security boundary. `ptk_invoke` runs arbitrary PowerShell
with the same authority as the MCP client process. A destructive-cmdlet policy
gate is intentionally not implemented in the current code; review scripts at
the client permission prompt instead of blanket-allowing the tool. Run the
harness under the restricted identity whose upstream RBAC is meant to govern
the work. Worker processes and platform containment reduce state leakage and
cleanup failures; they do not make hostile commands safe. Ordinary runtime
execution has audit disabled. Retained legacy evidence stores and the SIEM
receiver contract are documented in [AUDIT-EXPORT.md](AUDIT-EXPORT.md), but
they are not an authorization boundary or an enabled runtime producer.
