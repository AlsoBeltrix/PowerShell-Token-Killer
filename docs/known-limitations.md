# Known limitations

This page describes intentional or currently accepted boundaries of the source
tree. Version-specific release notes can narrow them further. An unresolved
defect is not converted into an accepted limitation merely by appearing here.

## Platforms and installation

- Native release packages are built for `win-x64`, `win-arm64`, `linux-x64`,
  `linux-arm64`, and `osx-arm64`. Other operating systems and architectures are
  not supported by the release workflow.
- The public bootstrap requires PowerShell 7. The installed server is
  self-contained and does not require a separately installed .NET SDK or
  PowerShell runtime.
- Installation as root or Administrator is refused. Running an agent harness
  elevated after installation still runs PTK and submitted commands elevated;
  PTK does not reduce that authority.
- Windows ARM64 uses the upstream x64 RTK binary under OS emulation because RTK
  does not publish a Windows ARM64 build. The installer probes that binary
  before registration.
- No package-manager distribution is currently provided. The supported public
  path is the checksum-verified GitHub release installer bundle.

## Sessions and execution

- Sessions belong to one MCP connection. There is no daemon, reattachment,
  cross-harness session, shared runspace, or durable session key. Supervisor
  restart loses warm session state.
- One session executes serially. Different sessions can progress independently,
  and one connection can have at most eight open sessions including `default`.
- PTK exposes foreground invocation only. It has no detached/background job
  tool, and it never replays a timed-out or transport-ambiguous command.
- PowerShell is the primary dialect. PTK does not infer Bash syntax; invoke
  `bash -lc '...'` explicitly when Bash is required and available.
- PTK is not a sandbox, privilege boundary, or authorization layer. Commands
  inherit the launching harness's OS identity, permissions, network access, and
  upstream RBAC.

## Output and object shaping

- Shaped responses are intentionally summaries. A retained `ptk_output`
  artifact can recover captured same-invocation data, but artifacts are bounded
  (8 MiB each by default), normally expire after 15 minutes, can be evicted
  under capacity pressure, and never outlive the supervisor.
- PTK does not execute arbitrary active, lazy, COM, or user-defined property
  getters merely to enrich output. Values that are already materialized and
  trusted types receive richer shaping; other values use conservative
  projections or explicit markers.
- Terminal control sequences are removed from direct text output. Log-shaped
  text can be deduplicated by RTK. Use the immutable recovery artifact when the
  response reports one and exact retained text is required.
- `Write-Host`/information and verbose records are rendered and retained under
  labeled stream sections. Progress records are transient UI state and are
  intentionally not captured or recoverable.

## Clients and integrations

- Registration, hook, and guidance capabilities differ by agent harness. The
  dated evidence is in the
  [harness capability matrix](harness-support.md); behavior not recorded there
  has not been claimed as supported.
- Current registration adapters do not inject per-call agent, model, task, or
  run identity. A client or proxy can supply the documented MCP `_meta`
  namespace; otherwise audit export records these values as
  `not_supplied_by_client` rather than guessing.
- Audit export and the standalone SIEM receiver are optional operator-managed
  deployments. PTK never installs or selects a SIEM destination automatically.
  Consult the operator-readiness status in
  [`server/AUDIT-EXPORT.md`](../server/AUDIT-EXPORT.md) before relying on those
  components for an external-SIEM investigation workflow.
