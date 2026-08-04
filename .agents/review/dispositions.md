# `opr-*` finding dispositions

Slice 6 of `.agents/plans/rtk-router-delegation.md`. Every accepted `opr-*`
finding gets exactly one disposition here. The "accepted and plan-gated" state
is retired: it produced a 59-item backlog with zero repairs across a weekend,
because recording a finding was cheaper than resolving one.

Detail for each finding stays in `.agents/review/findings/<id>.md`. This file
is the disposition of record.

## Dispositions

- **fixed** — repaired in this plan, with a mutation-proved guard.
- **closed-removed** — its production path no longer exists. Verified by
  symbol search against the tree, not assumed from the slice description.
- **closed-out-of-scope** — reachable only through a configuration the first
  release excludes: disabled audit, the SIEM receiver, `PtkAuditAdmin`, or an
  unselected platform. Real, not blocking. Reopen with the subsystem.
- **open-blocker** — meets the release-blocking rule and is not yet fixed.
  Each needs its own slice and an owner go.

## fixed (5)

| ID | Sev | Landed |
| --- | --- | --- |
| `opr-19` | HIGH | `3a24ee1` — graceful shutdown handshake now runs before containment |
| `opr-20` | HIGH | `44c4c97` — pre-write state cancellation no longer poisons a healthy session |
| `opr-58` | HIGH | `fa9b1ce` — post-success command advice deleted |
| `opr-40` | LOW | `e6e718d` — eager 4 MiB capture buffers deleted with the direct RTK runner |
| `opr-41` | MEDIUM | `e6e718d` — fabricated `$LASTEXITCODE` path deleted with the direct RTK runner |

`opr-20` carries a known gap, recorded in its commit: the fail-closed half is
guarded, the pre-write half is not. No available test seam reaches the
pre-write window — each client owns its writer, the operation lease observes
cancellation before the try block, and the stream seam sits downstream of the
first-write callback. A vacuous guard was removed rather than shipped.

## closed-removed (16)

Verified absent from `server/PtkMcpServer` and `src` (excluding `obj`/`bin`).

Shell inference — `GetShellDialectFinding`, `AssessShellDialect`,
`BashProcessRunner`, `BashExecutableIdentity`, and the bash validator are all
gone (Slice 3, `87075a7`). Seven findings whose shared failure was refusing
input that was never wrong:

`opr-17`, `opr-32`, `opr-33`, `opr-43` (HIGH), `opr-44`, `opr-47` (MEDIUM),
`opr-45` (LOW).

Cold command resolution and RTK argv planning — `ColdCommandResolution`,
`TryCreateRtkArgumentVector`, `SupportsDirectArgumentPassing`, and the
argument-mode fidelity model are gone (Slice 2, `e6e718d`). RTK now decides
routing from command text and PTK resolves nothing on disk:

`opr-48`, `opr-49`, `opr-50`, `opr-55`, `opr-56` (MEDIUM), `opr-51`,
`opr-57` (LOW).

Direct RTK process execution — `RtkProcessRunner` is gone (Slice 2). Its
remaining finding beyond the two fixed above:

`opr-4` (MEDIUM) — cleanup-time cancellation overwriting an elapsed process
timeout. Scoped to the direct RTK/Bash runner budget classification; both
runners are deleted.

`opr-2` (MEDIUM) — Unix PATH de-duplication. Lived in cold command
resolution.

## closed-out-of-scope (11)

Audit and SIEM stay disabled and unadvertised in the first release
(`.agents/plans/minimum-viable-release.md` §non-goals). `ptk_state` reports
audit disabled and ordinary invoke opens no audit storage. These are real
defects in code the release does not exercise:

`opr-35`, `opr-37` (HIGH), `opr-5`, `opr-6`, `opr-7`, `opr-36` (MEDIUM),
`opr-34`, `opr-38` (LOW), plus `opr-39` (LOW, output-root reclaimer ownership
proof) and `opr-3` (MEDIUM, output-root disposal ordering) — both reachable
only through abandoned-root reclamation, not ordinary use.

`opr-16` (LOW) — a test-only deadline-cancellation witness. Not product code.

## open-blocker (0)

None. Every remaining finding is dispositioned above.

## deferred to platform selection (13)

Decision 2 selects the release platform. These are platform-specific and
gate only their own platform's packaging, per the plan's Decision 2 text:

`opr-14` (HIGH, Apple arm64 `fcntl` variadic mispass) — blocks macOS.
`opr-15` (HIGH, Unix identity-probe fail-open) — blocks Linux and macOS.
`opr-25`, `opr-26`, `opr-29`, `opr-30`, `opr-31` (MEDIUM), `opr-24`,
`opr-27`, `opr-28`, `opr-46` (LOW) — Unix containment and launcher paths.
`opr-13` (MEDIUM), `opr-23` (MEDIUM) — Windows/environment identity.

Windows-only release: none of the Unix items block. Do not repair an
unselected platform's findings.

## remaining, not blocking (14)

Reachable in the release contract but below the release-blocking bar:
diagnostics, labels, and bounded internal states that do not execute a
different command, lose data, or break a session.

`opr-1` (LOW, `/dev/null` descriptor leak), `opr-8` (MEDIUM, Windows stdin
guard inheritability), `opr-9` (MEDIUM, culture-sensitive timeout parsing),
`opr-10` (MEDIUM, out-of-range timeout crashes startup), `opr-11` (MEDIUM,
unknown route falls back silently), `opr-12` (LOW, negative timeout),
`opr-18` (LOW, module inventory freezes), `opr-21`, `opr-22` (LOW,
diagnostic overwrite and classification race), `opr-42` (MEDIUM, state
summary registry race), `opr-52` (LOW, terminal diagnostic collapse),
`opr-53` (MEDIUM), `opr-54` (LOW) — worker text forging supervisor
directives, and `opr-59` (LOW, false `(no captured bytes)` marker).

Two deserve a second look before release even though they do not block:
`opr-10` terminates the supervisor before the MCP handshake on a malformed
timeout value, and `opr-53` lets worker output forge PTK-authored control
lines. Both are cheap; neither is in this plan's scope.
