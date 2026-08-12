# Plan: Resumable long invocations and lossless output recovery

**Status:** DRAFT — owner-directed plan recorded 2026-08-12. The observed
problems and requested outcomes are binding inputs; the API names and defaults
below are the recommended design, not an implementation approval. Implementation
requires a separate explicit owner go.

## Observed failure and prior-art boundary

On 2026-08-12, an expensive Claude Fable review was started through
`ptk_invoke` in named session `coder` with `timeoutSeconds=1200`. The MCP caller
failed at its own 300-second transport ceiling while the accepted operation
continued. The prompt supervisor-only diagnostic then exposed only the session
worker PID, `active=true`, and `runspace: unavailable
(detail=session_busy)`. It did not expose an invocation identity, admission or
start time, elapsed time, requested deadline, tracked child process, or a way
to distinguish that still-running invocation from any other work. Reissuing the
command was unacceptable because the work was paid and effectful.

This is not a regression of the already-landed fixes in issues #6, #16, or #44:

- #44's MCP progress heartbeat keeps a request alive only when every transport
  layer honors progress as activity. It cannot override an upstream hard
  request ceiling or repair a lost response.
- #6 makes `timeoutSeconds` a total queue-plus-execution budget and makes
  `ptk_state` return promptly while busy. It does not identify the active call
  or make it reattachable.
- #16 lets `ptk_output(action=list)` rediscover a sealed artifact after a
  completed response is lost. It deliberately has no stable invocation ID and
  cannot identify or control work that is still queued or running.

The replacement must make request delivery and operation lifetime separate,
explicit concepts. “Resumable” means that a caller can rediscover, observe,
wait for, cancel, and read the terminal result of the same single execution. It
never means replaying a command or resuming a worker after server loss.

## Binding product contract

### Invocation identity and lifecycle

Every admitted `ptk_invoke`, foreground or detached, receives one opaque,
connection-local invocation ID with prefix `ptki_`. IDs are generated from at
least 192 random bits, are never reused within the MCP connection, and are not
derived from scripts, sessions, timestamps, worker PIDs, or output handles.

The supervisor owns one bounded invocation registry. Its state machine is:

```text
accepted -> queued -> dispatching -> running -> sealing -> terminal
accepted -----------------------------> terminal:not_started
queued --------------------------------> terminal:not_started
dispatching|running|sealing -> cancel_requested -> terminal
```

Terminal outcomes are exactly `succeeded`, `failed`, `canceled`, `timed_out`,
`not_started`, and `outcome_unknown`. Detail codes continue to distinguish
queue expiry, execution timeout, worker/protocol loss, output-capture failure,
and ordinary command failure. A transition is monotonic and idempotent; no
terminal invocation can execute again or return to an active phase.

The registry retains active entries unconditionally and retains the newest 64
terminal entries for 15 minutes. It evicts only terminal entries, oldest first.
It is supervisor-memory state: it survives worker replacement, session reset,
and session close, but not MCP server exit or connection replacement. Terminal
eviction does not delete an independently retained `ptk_output` artifact.

Every terminal entry retains the existing bounded shaped tool result, terminal
detail, timestamps, and the published output handle when one exists. Reading a
terminal result returns those retained facts and never calls a worker, reruns a
script, or extends either retention period.

### `ptk_invoke` admission and delivery

Add these parameters:

```text
delivery = foreground | detached       # default foreground
onBusy = fail | queue                   # default fail
outputMode = shaped | verbatim          # default shaped
```

`foreground` preserves the ordinary one-call response shape. It returns the
terminal result and invocation ID after execution. Request cancellation remains
linked through execution as it is today. If a transport disappears without
sending cancellation, the invocation continues under its existing timeout and
is discoverable by ID or session. If cancellation reaches PTK, the registry
records the cancellation request and terminal outcome.

`detached` returns promptly after the invocation is atomically published in the
supervisor registry and either admitted to the idle session or accepted into
the explicit queue. After that publication boundary, cancellation or loss of
the start request does not cancel the operation. Before that boundary,
cancellation proves the script not started and publishes no discoverable
invocation. The detached start response carries the invocation ID, session,
phase, acceptance time, effective timeout, and deadline. If that response is
lost after publication, `ptk_operation(action=list, session=...)` rediscovers
the same invocation.

The detached continuation must be owned and observed by the supervisor. Every
code path, including scheduling failure and server shutdown, produces one
terminal transition or an explicit `outcome_unknown`; no fire-and-forget
`Task`, unobserved exception, or automatic retry is permitted.

`onBusy=fail` is the new default for both delivery modes. It atomically refuses
with `session_busy`, publishes no invocation, and never executes later when the
selected session already has an active or queued invocation. This is an
intentional behavior change from implicit same-session queuing. Callers that
want serialization say `onBusy=queue`; the existing `timeoutSeconds` remains
one wall-clock budget beginning at acceptance and includes the queue wait.
Each session admits at most eight queued invocations. The ninth refuses before
registry publication and script execution. Different named sessions remain
concurrent.

No invocation may borrow another session, silently fall back to `default`,
detach a process from worker containment, or outlive its selected worker after
timeout, reset, close, or server shutdown.

### Public operation tool

Add one non-executing management tool:

```text
ptk_operation(
  action,                         # list | status | wait | cancel
  invocation=null,               # required except for list
  session=null,                  # optional for list only
  waitSeconds=0                  # wait only; 0..60
)
```

- `list` returns at most the ten newest retained invocations, newest accepted
  first, optionally filtered by public session name. It includes active and
  terminal entries, never script or output content. It starts no worker and
  extends no retention.
- `status` returns immediately from supervisor memory. It includes invocation
  ID, session, phase/outcome, detail code, accepted/start/terminal UTC times,
  elapsed milliseconds, requested/effective timeout, deadline, worker PID,
  bounded known child PIDs, queue position, and published output handle when
  available. Unknown or inapplicable fields are explicit `null`/`none`, never
  inferred.
- `wait` waits at most `waitSeconds`, returns early on a terminal transition,
  and otherwise returns the current status. A terminal wait also returns the
  exact retained bounded result metadata. Repeated waits are idempotent and
  never rerun work. The 60-second maximum keeps polling calls below common MCP
  transport ceilings.
- `cancel` is the only detached-operation cancellation surface. It is
  idempotent: a queued invocation becomes `not_started`; a running invocation
  enters `cancel_requested` and uses the existing cooperative-cancel plus
  bounded containment path; a terminal invocation returns its terminal state.
  If cooperative cancellation cannot prove the worker safe, replace only that
  session worker and report the warm-state loss. Cancellation of one operation
  never cancels sibling sessions.

Operation IDs are bearer capabilities inside one already-authorized MCP
connection. Treat them like output handles at validation and audit boundaries:
bound their length, compare ordinally, never log them as arbitrary request
text, and never accept them where another action does not consume them.

### Prompt state observability

Extend the supervisor-owned portion of `ptk_state` and `ptk_session list`.
Neither tool may acquire the busy runspace gate or wait on the active
invocation.

For the selected session, `ptk_state` reports:

- active invocation ID and phase;
- accepted/start UTC timestamps and elapsed milliseconds;
- requested/effective timeout and absolute deadline;
- worker PID plus a bounded supervisor-known set of direct/native child PIDs,
  with an explicit overflow count;
- queued invocation count and the active invocation's queue wait;
- output-capture state (`none`, `reserved`, `sealing`, `available`,
  `unavailable`) and the public handle only after a successful seal.

For PowerShell-only work there may be no child PID; report `none`. Obtain child
facts from the existing containment/supervisor bookkeeping, not by querying the
untrusted runspace or performing an unbounded system process scan. Never expose
the submitted script, environment, command line, output bytes, or tentative
output-store handle.

`ptk_session list` adds only the compact active invocation ID, phase, elapsed
time, and queued count. Full detail remains in `ptk_state`/`ptk_operation`.

### Invocation/output correlation and lost-response recovery

Thread the invocation ID into `OutputCaptureReservation`, artifact metadata,
and the sealed listing/status records without using it as the output handle or
quota identity. `ptk_output(action=list|status)` reports the originating
invocation ID. `ptk_operation(action=status|wait)` reports the published output
handle. The two stores keep their independent TTL and quota rules.

Recovery after any lost response is deterministic:

1. List operations by the known session.
2. Select the invocation by ID, timing, and phase without rerunning anything.
3. Wait in bounded calls or inspect status.
4. On terminal success, read the correlated immutable output handle; if the
   invocation record has expired, use the independently retained
   `ptk_output(action=list, session=...)` entry and its invocation ID.

An unsealed reservation never exposes a public output handle. Capture failure
does not fabricate one; the terminal invocation remains readable with
`output=unavailable` and its bounded detail code.

### Lossless text mode

`outputMode=verbatim` is an explicit result contract for source, code,
configuration, and other exact-text reads. It does not revive `raw=true` and
does not alter PowerShell consent, timeout, session selection, containment, or
single-execution guarantees.

For this mode:

- PTK must reserve a full same-invocation artifact before dispatch. If the
  selected route cannot provide unshaped text (including an RTK rewrite without
  a negotiated raw-capture seam), refuse before execution with
  `verbatim_unavailable`. Never silently change the route or rerun the command.
- The artifact is the exact UTF-8 encoding of the worker's pre-shaping text.
  For PowerShell objects, “verbatim” begins after the existing deterministic
  object-to-text recovery rendering; it is not a claim to serialize object
  identity. Binary output is out of scope.
- Do not pass the artifact through RTK log shaping, object summarization, ANSI
  stripping, line/character elision, newline normalization, or marker
  injection. Always publish a `ptk_output` handle on a successful seal, even
  when the text is small.
- The normal invoke/operation result reports metadata and the handle rather
  than embedding the purportedly exact payload in a decorated text response.
  Add `encoding=utf8|base64` to `ptk_output(action=read)`, defaulting to `utf8`;
  `base64` returns an undecorated encoded data field plus byte offsets, total
  bytes, and SHA-256 so arbitrary source text cannot impersonate PTK metadata
  or be changed by text framing. Offsets always address original artifact
  bytes, not encoded characters.
- A post-dispatch capture or seal failure is terminally labeled
  `verbatim_unavailable_after_execution`; it never triggers replay. The
  operation outcome and output-availability outcome remain separate.

`outputMode=shaped` preserves the current compression and best-effort recovery
behavior. The tool description must recommend `route=pwsh,
outputMode=verbatim` for exact PowerShell text reads and must state that
`raw=true` remains inert compatibility telemetry.

## Audit and security integration

The async acceptance boundary must remain compatible with the optional strict
audit path.

- The `ptk_invoke` MCP call records its ordinary call lifecycle plus the new
  normalized `delivery`, `onBusy`, and `outputMode` fields. Scripts remain in
  the existing protected evidence path, never in registry/list/state output.
- Before a detached operation can execute, reserve audit capacity for both the
  start call's terminal and exactly one later invocation terminal. Emit
  `invocation.accepted`, `invocation.started`, and exactly one
  `invocation.completed|failed|canceled|timed_out|not_started|outcome_unknown`
  record correlated by call ID and invocation ID. If that reservation or the
  accepted record fails, refuse before publication and execution.
- `ptk_operation` calls receive their own ordinary call lifecycle. Status,
  list, and wait are read-only. A cancel call records whether cancellation was
  newly requested or the target was already terminal; the later invocation
  terminal remains singular.
- Extend the strict `ptk.audit/2` model, serializer/parser, receiver schema,
  dashboard/detail projections, fixtures, and compatibility tests in the same
  slice. Do not smuggle new values through unrelated fields or silently drop
  them from export.
- Hash/protect operation IDs and output handles consistently with existing
  bearer-value policy. Do not persist submitted scripts or result content in
  the operation registry.

Audit-disabled operation semantics must be identical except for absent journal
records. Audit degradation must follow the repository's current fail-closed or
diagnostic policy; this plan does not create a bypass.

## Implementation slices

### Slice 1 — invocation registry and observable identity

1. Add immutable invocation identity, lifecycle records, clock seam, terminal
   retention, and bounded registry under `server/PtkMcpServer/Sessions/`.
2. Allocate and publish IDs in `NamedSessionSupervisor` for every accepted
   foreground invocation while preserving one execution and existing timeout/
   worker-replacement behavior.
3. Thread supervisor-known phase/timing/containment facts into `StateTool` and
   session listing without touching the runspace gate.
4. Correlate output reservations/listings/status with invocation IDs in
   `Execution/OutputStore.cs` and `Tools/OutputTool.cs`.
5. Add focused lifecycle, race, retention, state, and correlation tests in
   `NamedSessionSupervisorTests`, `StateToolTests`, and `OutputStoreTests`.

The slice is complete only when the live failure shape can identify an active
foreground invocation by session and elapsed time after its original response
consumer is abandoned, without starting a second invocation.

### Slice 2 — `ptk_operation` read surfaces

1. Add `Tools/OperationTool.cs` with strict action-specific validation for
   `list`, `status`, and bounded `wait`; do not add `cancel` to the public enum
   until Slice 3 so no schema advertises a partial effectful path.
2. Store and return the existing bounded terminal result without rerunning or
   querying a worker.
3. Add schema conformance, heartbeat, audit metadata, unknown-field, bearer-
   protection, and list-bound tests.
4. Extend the real stdio handshake to prove list/status/wait, lost start-result
   discovery, repeated terminal reads, and one-execution sentinels.

### Slice 3 — detached delivery, explicit queueing, and cancellation

1. Add `delivery` and `onBusy` to `InvokeTool`, `ISessionOperations`,
   `WorkerSupervisor`, and `NamedSessionSupervisor`.
2. Add the supervisor-owned detached continuation, atomic publish/dispatch
   boundary, eight-entry per-session queue, and request-token separation.
3. Implement `ptk_operation(action=cancel)` and the idempotent queued/running/
   terminal rules, reusing current worker cancellation and containment proof.
4. Add strict audit reservation/events for asynchronous terminals before any
   detached execution can be enabled.
5. Mutation-prove transport loss after publication, start-call cancellation on
   both sides of the publication boundary, fail-fast default, explicit queue
   order/expiry, queue bound, cancel races, worker loss, reset/close refusal,
   sibling-session isolation, shutdown, and no automatic replay.

### Slice 4 — verbatim artifact contract

1. Add `outputMode` validation and admission to the invoke path and worker
   protocol. Preserve `raw=true` as inert telemetry.
2. Add required artifact reservation and route-capability refusal before
   execution. Keep output-mode selection independent from routing consent.
3. Preserve pre-shaping UTF-8 bytes through worker artifact frames and seal
   digest; add base64 reads and digest/encoding metadata to `ptk_output`.
4. Add adversarial exactness fixtures containing ANSI bytes, mixed newlines,
   leading/trailing blank lines, non-ASCII, NUL, PTK-looking markers, JSON,
   Markdown fences, and more than the inline shaping window. Compare decoded
   bytes and SHA-256, not rendered strings.
5. Mutation-prove that object shaping, RTK log routing, text elision, newline
   normalization, or metadata concatenation each breaks at least one fixture.

### Slice 5 — public contract and packaged proof

1. Update `README.md`, `server/README.md`, setup-generated agent guidance,
   harness support, tool descriptions, registration/init guidance, and every
   five-tool count/schema assertion to the six-tool surface. Do not hand-edit
   toolkit-owned governance artifacts.
2. Update `server/test-handshake.ps1` and
   `server/direct-product-proof.ps1` with a real long detached operation,
   prompt state/status polling, explicit cancellation, lost-response recovery,
   exact output recovery, timeout, reset, and containment assertions.
3. Exercise one installed package with an MCP caller whose request ceiling is
   shorter than the operation. Prove the start returns before that ceiling,
   polling observes the same ID/PID/timing, the command runs once, and the
   terminal artifact is byte-exact.
4. Record host-specific evidence in `.agents/machines.md` and current durable
   state in `.agents/state.md` only after the corresponding behavior lands.

## Verification and review gates

For every new regression, remove or invert only the production behavior it
guards, prove failure for the named reason, restore the exact bytes, and prove
the test passes. Race tests use deterministic barriers and fake clocks; sleeps
alone are not acceptance evidence.

Each implementation slice must pass focused tests plus the server verification
entry points in `.agents/repo-guidance.md`. A schema/audit slice also runs the
full SIEM suite and dependency audits. A public-tool slice runs the registered
stdio handshake. Packaged completion requires the direct-product proof on each
selected release platform.

Reviewer dispatch is not implied by this plan. If the owner invokes
`codereview` or `openreview`, follow that playbook with exact base/head pins and
record the result in `.agents/review/`; never rerun an expensive reviewer after
a transport failure unless the owner explicitly reverses that restriction.

One implementation slice per commit. Keep plan/state/review paperwork in the
same slice commit, but do not mix unrelated active-review paperwork into a plan
commit. No push, PR, issue, package, tag, release, or publish is implied.

## Non-goals

- no command replay, retry, duplicate execution, or automatic restart;
- no operation survival across MCP server exit or a different connection;
- no general cold-job scheduler, cron/watch service, or concurrent pipelines
  inside one warm session;
- no detached subprocess that escapes the selected worker's containment tree;
- no arbitrary queue growth, caller-chosen retention, pagination, or
  cross-connection enumeration;
- no binary-output contract or claim that PowerShell objects have original
  byte streams;
- no restoration of `raw=true` as a routing, shaping, or capture bypass;
- no weakening of timeout, audit, evidence, quota, session-isolation, or
  worker-replacement guarantees.

## Supersession notes

This plan extends completed issue #16 by adding active/terminal invocation
identity and bidirectional output correlation. It supersedes only these earlier
statements for the behavior implemented here:

- `.agents/plans/production-reliability-salvage.md` and `README.md` say there is
  no public background-job/detached warm-session surface;
- `.agents/plans/issue-16-output-discovery.md` lists stable invocation IDs and a
  background job system as non-goals;
- prior queue descriptions imply queuing is the ordinary default.

The earlier no-cold-job, no-replay, connection-local retention, containment,
output-quota, and total-timeout decisions remain in force. Update the active
docs when implementation lands; do not rewrite completed historical plans.
