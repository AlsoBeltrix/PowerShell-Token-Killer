# Plan: Recover output after caller disconnect

**Status:** COMPLETED 2026-08-01 — Slice 1 merged through PR #22 (`0b37f3a`), Slice 2 merged through PR #23 (`9ad7765`), both exact-head Opus reviews accepted with `guard_confirmed=true`, all hosted matrix jobs green, and GitHub issue #16 closed. Amended 2026-07-31 before Slice 2 to keep the public list at a fixed maximum of 10 rather than version the strict audit schema for a caller-selectable limit.

## Problem

`ptk_invoke` can outlive the MCP request carrying its response. The supervisor
continues the admitted operation and `OutputStore` seals the immutable artifact,
but the only public read capability is the opaque handle returned in that lost
response. A completed, successful invocation can therefore become unreachable
without rerunning paid or effectful work.

The output store already retains the facts needed for discovery: creation and
seal times, monotonic sequence, state, completeness, byte count, provenance,
expiry, and the opaque read handle. It attributes quota to a private session
generation identity, but does not retain the public named-session label.

## Product contract

Extend the existing non-executing `ptk_output` tool with `action=list`.

- List only sealed, currently readable artifacts. Never expose reservations,
  pending captures, expired entries, eviction tombstones, or filesystem paths.
- Return newest first by the store's monotonic sequence. Wall-clock ties and
  clock changes must not alter ordering.
- Return at most the 10 newest matching artifacts. This issue adds no caller-
  selectable limit or pagination; calls in one named session serialize, so the
  lost completed invocation is newest when the disconnected caller regains
  control.
- Accept an optional public `session` filter. Omission lists across this MCP
  connection only; the server process already owns exactly one connection and
  its output store and named-session supervisor are connection-wide singletons.
- Include, per result, the opaque handle, public session name, state,
  completeness, byte count, provenance, creation/seal/expiry timestamps, and
  detail code. These values are sufficient to select a snapshot and then use
  existing `read`, `search`, or `status` without rerunning work.
- Preserve artifacts across reset/reopen of the same public session name until
  normal retention removes them. The stored public label is descriptive; quota
  and generation isolation continue to use the existing private session alias.
- `handle` becomes optional in the generated schema only because `list` does not
  consume one. It remains mandatory for `read`, `search`, and `status` at both
  the audit boundary and direct tool boundary.
- `session` is valid only for `list`; handle, offset, maxBytes, and pattern are
  invalid for `list`. Existing action-specific validation remains fail closed.
- Listing is read-only, accepts no script, starts no session or worker, performs
  no invocation, and does not extend artifact retention.

The list exposes bearer handles that were previously unenumerable. This is an
intentional capability change bounded to the same MCP connection that can create
and reset the named sessions. Audit records must capture the requested session;
the list action itself fixes the effective bound. Tool-result handles must remain
outside request metadata and existing handle/pattern protection rules must remain
unchanged.

## Slice 1 — retained discovery model

1. Add a bounded listing record and `OutputStore.List(session, limit)`.
2. Store a validated public session name separately from the existing private
   session alias on every reservation.
3. Thread the public name through `ForegroundOutputCapture` and the named-session
   supervisor without changing private quota attribution or worker protocol.
4. Select readable entries under the store lock after the normal retention pass,
   order by descending sequence, and copy immutable listing records before
   releasing the lock. Drain claimed deletes after releasing the lock as other
   store queries do.
5. Add focused store and supervisor-path tests proving newest-first ordering,
   session filtering, limit enforcement, pending/tombstone exclusion, reset-name
   continuity, and discovery of a sealed handle whose original response is not
   used.

Review this slice against an exact commit and base with Claude Opus 5 at maximum
effort. Record the finding and guard result in `.agents/review/`, then integrate
only an accepted review with `guard_confirmed=true`.

## Slice 2 — public tool and audit boundary

1. Add `list` and `session` to `OutputTool`; format a fixed-maximum-10,
   line-oriented response whose handles can be passed verbatim to existing
   actions.
2. Extend audit metadata capture with action-specific validation: list accepts
   no handle and records only the normalized optional session; other actions
   still require and protect the handle. Do not overload byte-count audit fields
   or change the strict `ptk.audit/2` schema for the fixed list bound.
3. Update generated-schema conformance, raw-usage/description assertions, public
   tool contract fixtures and hashes, README, and server README.
4. Add an integration-style regression that seals a completed invocation,
   discards its normal response, lists by public session, extracts the handle,
   and reads the exact retained output. The test must prove the invocation ran
   once.

Review this slice against an exact commit and base with Claude Opus 5 at maximum
effort. Record the finding and guard result in `.agents/review/`, then integrate
only an accepted review with `guard_confirmed=true`.

## Verification

For every new regression, temporarily remove only the production behavior it
guards, prove the test fails for the intended reason, restore the exact bytes,
and prove it passes.

Before either implementation slice is complete, run the repository verification
entry points from `.agents/repo-guidance.md` relevant to that slice. Before issue
closure, run the complete server solution, PowerShell suite, and registered
handshake, then require all hosted matrix jobs green on the exact reviewed head.

## Integration and closure

Keep one finding per commit and one implementation slice per PR. Push every
commit immediately under `.agents/push-policy.md`. Merge without rewriting
history. Close issue #16 only after both slices are on `master`, exact-head CI is
green, and the issue comment names the new discovery syntax and verification.
After closure, rescan the live GitHub issue queue before selecting the next item.

## Non-goals

- no command replay, retry, background job system, durable cross-process index,
  stable caller-supplied invocation ID, or retention extension;
- no cross-connection or post-server-restart discovery;
- no filesystem path disclosure or arbitrary artifact enumeration outside the
  current connection's bounded retained store;
- no change to timeout, cancellation, containment, worker lifetime, or the
  semantics of existing `ptk_output` actions.
