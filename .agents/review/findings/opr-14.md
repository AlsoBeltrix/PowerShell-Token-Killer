# opr-14: Fixed `fcntl` P/Invokes mispass variadic arguments on Apple arm64

**Severity**: HIGH — user PowerShell descendants can inherit live private worker-protocol descriptors on supported Apple arm64 hosts.

**Status**: Accepted; unplanned. Product change is blocked until an approved plan covers an ABI-correct close-on-exec operation and a real Apple arm64 guard.

**Source**: Bounded Claude Opus 5 review of `UnixWorkerBootstrap`, evidence-bound Opus adjudication, and Apple’s arm64 ABI documentation.

## Evidence

`server/PtkMcpServer/Worker/UnixWorkerBootstrap.cs` and `server/PtkMcpServer/Worker/UnixWorkerProcessLauncher.cs` each declare libc’s variadic `fcntl` as the fixed P/Invoke `Fcntl(int descriptor, int command, int argument)` and use it for `F_SETFD`. Apple’s [arm64 ABI guidance](https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms) assigns variadic arguments to stack slots, while a fixed third argument follows the ordinary fixed-argument convention. The native variadic callee therefore does not reliably receive `flags | FD_CLOEXEC` on Apple arm64.

The worker bootstrap keeps duplicated request and event descriptors open for its protocol lifetime. Their only inheritance protection is the affected `F_SETFD` call. The process launcher also depends on the same call while preparing temporary descriptor mappings before `posix_spawn`; a concurrent spawn can inherit another launch’s temporary descriptors if close-on-exec was not actually set. Existing macOS arm64 suites exercise worker startup but do not assert the actual descriptor flags, descendant inheritance, or overlapping-launch isolation, so their success does not guard this ABI boundary.

## Predicted observable failure

A command child on macOS arm64 can inherit the worker’s duplicated request reader and event writer. The child can retain protocol pipe lifetime after worker death, race or consume supervisor requests, or inject bytes into the event channel, breaking worker isolation and producing hangs or transport corruption. Overlapping worker launches can also inherit one another’s temporary mapping descriptors.

## Required repair

Replace both direct `fcntl` P/Invokes with an ABI-correct non-variadic native shim on Apple arm64 while preserving Linux behavior. Add real Apple arm64 integration guards that prove `FD_CLOEXEC` is set, an exec-created command child cannot observe duplicated bootstrap descriptors, and overlapping worker launches cannot inherit one another’s temporary mapping descriptors. Prove the guards fail against the current implementation before retaining the repair.

## Review disposition

Reviewer: owner-selected `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5`, max effort, no-tool transport; exact head `f14e32e20c83007ed7b14cb87317f18a5e1ce585`. Initial and evidence-bound adjudication verdict: `finding`; `guard_confirmed=false`. No product-change guard claim.

- Rejected candidate: a duplicated descriptor can be closed twice if managed pipe construction throws, but this happens only after terminal bootstrap failure in a worker that immediately exits and has no material product effect.
