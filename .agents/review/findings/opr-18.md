# opr-18: Available-module inventory freezes after the first state probe

**Severity**: LOW — optional session diagnostics silently report a stale module
inventory for the rest of a live worker.

**Status**: Accepted; unplanned. Product and test changes are blocked until an
approved plan defines the cache-invalidation or explicit-snapshot contract and
its deterministic guard.

**Source**: Bounded no-tool Claude Opus 5 review of `SessionRuntime` at
`eb03b503e2b56e52f98d22dc98c4ae9f096f84e3`, followed by evidence-bound
adjudication against production callers, documentation, and focused tests.

## Evidence

`SessionRuntime.StateCoreAsync` runs `Get-Module -ListAvailable` only while
`_availableModuleCache` is null, stores the first clean result, and returns that
string on every later `listAvailable=true` call for the worker lifetime. The
production path has no invalidation before whole-worker reset or replacement.

The same warm session can change `$env:PSModulePath` and create, install,
remove, or update module files through `ptk_invoke`. `Get-Module
-ListAvailable` resolves the current search path and filesystem, while the
adjacent environment-drift report observes search-path changes. The public
`ptk_state` contract says it reports the selected session's modules and drift;
it does not disclose a first-call snapshot or stale-cache policy.

The extraction review at
`7999328de546c86b042e58b0ff21b38d6e97e322` established runtime-local cache
ownership and used
`ResetToolTests.Reset_preserves_runtime_raw_count_and_available_module_cache`
to require persistence across the retired direct-runtime reset path. That
guard did not establish truthfulness after later module-search-path or
filesystem changes in a still-live production worker.

## Predicted observable failure

After one successful `ptk_state listAvailable=true`, a later `ptk_invoke`
prepends a directory containing a new module to `$env:PSModulePath`. Every
subsequent `listAvailable=true` response omits that module until the worker is
replaced, potentially while the same response reports the environment drift
that made its module inventory stale.

## Required repair boundary

The plan must choose and document one truthful contract: invalidate or re-key
the inventory when its inputs change, or label and expose it explicitly as a
first-call snapshot rather than current session state. Preserve zero-wait
state behavior: a refresh must not queue behind an active runspace or another
slow enumeration.

## Required guard

Within one live runtime, obtain the first successful available-module report,
then use the warm runspace to prepend a temporary module root containing a
unique valid manifest at `<root>/<ModuleName>/<ModuleName>.psd1` to
`$env:PSModulePath`. A second report must include the module or explicitly
identify the old value as a stale snapshot, without worker reset or
replacement. Temporarily revert only the repair and prove that assertion
fails, restore it, then run the focused state tests and the full server
solution.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`
(`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `LOW`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- A separate probe-exception candidate was rejected: ordinary cancellation
  and PowerShell failures already become structured results, production
  lifetime excludes disposal races, and unexpected internal faults should
  fail the operation instead of returning a deceptively partial report.
