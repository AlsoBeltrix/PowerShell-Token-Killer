# openreview: RTK router delegation plan — codex r1

`Reviewer: codex / (harness default model) / (harness default effort) / standard`

- Harness: codex-cli 0.146.0
- Dispatch: `codex exec --cd <repo> -s read-only --color never -` (prompt on stdin)
- Range: `e22d619d83082d130bc9821b67a4f5529eb49146..3f8160c509eb96fcc4d9c4d2b96169673cfbf57d`
- `capability_ok`: true
- Verdict: **acceptable_with_changes**
- UTC: 2026-08-03T17:39Z

## Dispatch deviations (recorded, not silent)

1. **Tier.** The playbook fixes `openreview` at the frontier pair, max
   effort. The owner dispatched "codex on default model/effort" explicitly.
   The owner's dispatch word is final, so this ran at the harness default
   with no `-m` or effort flag. No frontier pair is recorded for codex in
   `.agents/review/harnesses.local.json`; none was invented. The pair is
   therefore ungraded and this verdict carries no `grade`.
2. **Worktree isolation.** Launched `-s read-only`, which forbids creating
   the disposable worktree the playbook normally grants. Acceptable for a
   docs-only range with nothing to build or revert; a code range needs
   `workspace-write` plus an explicit worktree instruction.
3. **Transport noise.** The child logged repeated
   `Failed to refresh token: ... refresh token was revoked` errors to stderr
   while still completing the review and emitting a schema-valid payload.
   Recorded as a note per the dispatch-provenance rule, not an invalidation.
   The owner may need `codex login` before the next dispatch.

## Acceptance (orchestrator-computed)

| Check | Result |
| --- | --- |
| Exit code | 0 |
| Payload matches schema | yes |
| `verdict` in enum | `acceptable_with_changes` |
| `reviewed_sha` == dispatched head | yes |
| `base_sha` == dispatched base | yes |
| `capability_ok` literally true | yes |
| `material_changes` non-empty for this verdict | yes (1) |

Accepted verdict.

## Reviewer's own approach

> Use RTK hook check as the native-command rewrite authority and delete
> PTK's duplicated AST/PATH routing, Bash inference, and post-success
> advice, but retain a minimal PowerShell binding guard before accepting a
> rewrite so warm-session functions, aliases, cmdlets, and external scripts
> still execute as PowerShell unless the user explicitly chooses an exact
> native route contract.

Comparison: the plan "correctly identifies the main simplification" but its
Slice 2 wording "delegates too absolutely to RTK text rewriting and would
let an implementation bypass PowerShell's warm-session command binding."

## Material change 1 — ADOPTED

> Amend Slice 2 so an RTK rewrite is only accepted after PTK verifies the
> rewritten command names are not shadowed in the warm PowerShell session by
> aliases, functions, cmdlets, external scripts, or other non-native
> bindings; otherwise decline the rewrite and execute the original text
> unchanged.

## Finding — ADMITTED (HIGH)

**RTK text delegation can bypass warm-session PowerShell bindings.**

Evidence cited: `.agents/plans/rtk-router-delegation.md:63` (RTK becomes
routing authority, PTK stops resolving executable identity) and
`server/PtkMcpServer.Tests/InvokeToolTests.cs:1364`
(`Warm_function_or_alias_shadow_keeps_native_name_on_direct_route`).

Predicted failure: a session defining `function global:git` or an alias
`git` would have `git status` rewritten to `rtk git status` under
`route=auto`, executing native git instead of the persisted binding.

### Independent verification (not taken on the reviewer's word)

Both halves reproduced live against the installed build:

1. Current behavior is correct. In a fresh named session:
   `function global:git { 'SHADOWED - PowerShell function ran' }` then
   `git status` → `SHADOWED - PowerShell function ran`. The existing test at
   `InvokeToolTests.cs:1364-1396` asserts `ExecutionPath.PowerShellDirect`
   for exactly this case.
2. RTK cannot know. `rtk hook check --agent claude "git status"` returns
   `rtk git status` unconditionally — it rewrites command text and holds no
   session state, so the guard cannot live upstream.

The finding is real and the guard must live in PTK.

## Disposition

Slice 2 amended: acceptance rule 4 (warm-binding guard), a rationale
paragraph, retained command-fact capture, the deleted-vs-retained boundary
restated, and a completion criterion covering a warm-shadowed name.

The amendment narrows Decision 1's scope: RTK is the rewrite *generator*;
PTK keeps the final say on whether a name binds natively in this session.
Decision 1 remains unruled.
