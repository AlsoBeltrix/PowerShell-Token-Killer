# openreview: RTK router delegation implementation — codex r2

`Reviewer: codex / (harness default model) / (harness default effort) / standard`

- Harness: codex-cli 0.146.0
- Dispatch: `codex exec --cd <repo> -s read-only --color never -` (prompt on stdin)
- Range: `87d03d8..076626fe17c45463b22e5fdb42bbf5db0f35e09d` (plan approval → Slice 6)
- `capability_ok`: true
- Verdict: **acceptable_with_changes** — 3 material changes, 4 findings
- UTC: 2026-08-03

Same dispatch deviations as r1, recorded there: harness-default tier per the
owner's explicit word (the playbook's default is frontier/max), `-s read-only`
so no disposable worktree, and codex auth-refresh errors on stderr. This
verdict is ungraded.

## Acceptance (orchestrator-computed)

| Check | Result |
| --- | --- |
| Exit code | 0 |
| Payload matches schema | yes |
| `verdict` in enum | `acceptable_with_changes` |
| `reviewed_sha` == dispatched head | yes |
| `base_sha` == dispatched base | yes |
| `capability_ok` literally true | yes |
| `material_changes` non-empty for this verdict | yes (3) |

Accepted verdict.

## Reviewer's own approach

> Keep the reviewed change's overall design: fail fast when a runtime-valid
> startup RTK cannot be captured, ask the pinned RTK via
> `hook check --agent ptk` for each auto/rtk call, accept only prefix-only
> rewrites, bind `rtk` to the pinned absolute path, execute accepted rewrites
> in the warm runspace, and delete the obsolete resolver/dialect surfaces and
> docs.

Comparison: "the right architectural direction and deletes a large amount of
risky local routing code, but it leaves a few old public surfaces and stale
docs behind, and its startup/runtime RTK validation plus rewrite exactness
checks need tightening."

The architecture — including the pinned-path binding this session added after
its own tests caught the PATH-substitution defect — is endorsed unchanged.

## Findings — all four ADMITTED and fixed

Each was verified against the tree before repair, not taken on the reviewer's
word.

**F1 (HIGH) — startup accepted an unusable RTK and then degraded.**
`RtkDependency.ResolveExecutablePath` gated on `File.Exists` while the runtime
pins via `RtkExecutableIdentity.TryCapture`. A path passing the weaker check
but failing capture let the server start and then run native commands
unfiltered — the exact silent degradation the required-dependency gate exists
to prevent. Startup now uses `TryCapture`, the same criteria as runtime.
Guard: `A_configured_file_the_runtime_cannot_capture_is_unresolvable` (an
oversized file passes `File.Exists` and fails the 128 MiB capture bound).
Mutation-proved.

**F2 (MEDIUM) — the module still shipped the old routing model.**
`Resolve-PtcInvokeScript` (the pre-Slice-2 single-native-command AST rewriter)
was still exported with no production caller, the manifest still listed the
deleted `Get-PtcShellDialectFinding`, and 18 Pester assertions still guarded
the obsolete resolver. All removed; the manifest now exports exactly
`Compress-PtcObject` and `Compress-PtcOutput`, verified with
`Test-ModuleManifest`.

**F3 (MEDIUM) — rewrite acceptance was not exact around quoted whitespace.**
The strip-and-compare normalized all whitespace, so a rewrite changing
`git commit -m "two  spaces"` to `"two spaces"` reduced to the same string and
was accepted, then executed with different argument text. The comparison is
now exact apart from leading/trailing trim. Guard:
`Rewrite_altering_whitespace_inside_a_quoted_argument_is_declined`.
Mutation-proved.

**F4 (LOW) — `server/README.md` documented deleted behavior.** Automatic Bash
delegation, post-success `[ptk:routing]` advice, and the old single-application
`route=rtk` shape. Rewritten to the delegation model.

## Disposition

All three material changes adopted; all four findings fixed with guards.
Landed in a single follow-up commit rather than four, because they are one
review's remediation and the plan's one-item-per-commit rule governs findings
worked as separate slices, not a single review pass's corrections.
