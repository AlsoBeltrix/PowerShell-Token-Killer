# MCP Upgrade Continuity Options

**Status:** CLOSED — all continuity options were declined by owner direction on
2026-07-30. Installed upgrades require all PTK processes to stop; this document
is historical review evidence only and grants no implementation authority.

## Goal

Allow an installed PTK MCP runtime to be upgraded on Windows without permanently
removing PTK from an MCP client conversation that was already connected, while
preserving the repository's reliability and security constraints.

## Observed Failure

The MCP client launches `PtkMcpServer` over stdio. The launched process owns the
client's original stdin/stdout pipes. Replacing an installed payload that is
locked by that process requires the old process to exit. Once it exits, the
existing client connection becomes transport-closed; installing and starting a
new server process does not attach that process to the original pipes. A fresh
client session can use the newly installed runtime, but the interrupted
conversation cannot.

Separately, a client may retain prior tool references after an MCP reconnect. A
transport that reconnects successfully therefore does not by itself prevent
requests for a removed or renamed tool from failing.

## Current Relevant Shape

- Each MCP client connection directly launches a PTK stdio supervisor.
- The supervisor owns the public MCP protocol boundary and the registered tool
  schema.
- Named PTK sessions are hosted in private worker processes with warm runspace
  state and per-session containment.
- The installed public schema currently consists of five tools.
- Installation is per-user, stages payloads before activation, and must retain
  rollback behavior when activation fails.

## Required Properties

Any option claiming transparent same-conversation upgrade must address all of
these properties:

1. The original client stdio connection remains usable throughout the upgrade,
   or the MCP client has an explicit, proven mechanism to replace it.
2. An in-flight call has a single observable outcome. Ambiguous calls are not
   silently replayed after a backend failure.
3. The public tool names and input schemas remain compatible across a rolling
   upgrade. Dynamic client schema refresh is not assumed.
4. Existing session containment, authorization boundaries, secret handling,
   and output sanitization are not weakened.
5. Activation is staged and reversible. A failed new runtime does not destroy
   the last usable installed runtime.
6. Old and new runtime versions can be distinguished operationally, including
   which version owns an existing warm session.
7. Windows is the immediate target, but the public architecture must not
   preclude macOS or Linux.
8. Per-user installation remains the default security boundary; a
   machine-wide privileged service is not assumed.

Warm session preservation is desirable but is a separate property from
preserving the MCP connection. An option must state whether existing named
sessions survive, drain on the old version, or fail explicitly.

## Options

### A. Direct replacement with client restart

Keep the current direct stdio topology. Stage and activate the new payload,
terminate the locked old server only when required, and require the user to
restart the MCP client session.

This has the smallest implementation surface, but it does not meet transparent
same-conversation continuity.

### B. Side-by-side payloads with deferred cutover

Install each runtime in an immutable versioned directory. Change registration
or a small version selector so newly launched MCP connections use the new
version. Do not terminate old supervisors; they continue using their old
payload until their client conversations end naturally.

This avoids disrupting active conversations. It does not make an already
connected conversation adopt the new runtime, and old warm sessions remain on
the old version until drained.

### C. Stable stdio guardian with replaceable private runtime

Install a deliberately small, stable guardian that permanently owns the client
stdio connection and the public five-tool schema. The guardian launches a
versioned private PTK runtime over a separate IPC boundary. Upgrade stages and
health-checks a new private runtime, then asks the guardian to route new calls
to it while the guardian remains connected to the client.

The protocol must define draining, in-flight-call ownership, failure responses,
warm-session version ownership, rollback, and guardian compatibility with both
the old and new private runtime.

### D. Stable stdio shim with a per-user local daemon

Keep a small per-client stdio shim stable. Forward calls over authenticated
per-user named-pipe or local-socket IPC to a longer-lived local daemon that owns
runtime selection and worker lifecycle. Upgrade the daemon or its selected
backend while each shim retains its original client pipes.

This centralizes upgrade coordination across clients, but adds daemon
lifecycle, endpoint authentication, multi-client isolation, stale-endpoint
recovery, version skew, and a larger persistent attack surface.

### E. Same-pipe process handoff

Attempt to transfer or inherit the client's stdio handles into a replacement
server process and preserve protocol state across the handoff.

This may avoid a permanent guardian, but it is platform-specific and must prove
safe framing, buffering, cancellation, process ownership, rollback, and
in-flight-call semantics at the exact handoff boundary.

### F. Depend on client reconnect and schema refresh

Terminate the old server and rely on each MCP client to respawn it, reconnect,
refresh `tools/list`, and discard stale tool references automatically.

This is primarily a client capability rather than a PTK-controlled server
architecture. It is viable only where the target clients document and verify
all required behaviors.

### G. Phased side-by-side installation, then stable guardian

Adopt option B first to make upgrades non-disruptive for active conversations,
with the explicit limitation that those conversations remain on the old
version. Add option C only if adopting a new runtime inside the same
conversation is a required product behavior.

This separates immediate installation safety from the higher-complexity
transparent-cutover requirement.

## Review Questions

1. Which option best achieves the goal under the required properties?
2. Is transparent adoption of the new runtime in an existing conversation worth
   the additional permanent component, or is non-disruptive deferred cutover
   sufficient?
3. Which option has the smallest trustworthy failure surface on Windows?
4. What protocol or lifecycle requirement is missing from the options?
5. What phased implementation and verification sequence would avoid committing
   early to an architecture that cannot meet the required semantics?
