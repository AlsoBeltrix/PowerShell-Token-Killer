# opr-24: Confirmed-empty Unix launch loses process provenance

**Severity**: LOW — a Unix worker that was created and then proved contained can be reported as never launched, making public startup provenance depend on exception wrapping rather than process creation.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan preserves post-spawn provenance across every confirmed and unconfirmed launch-failure containment outcome.

**Source**: Multi-pass no-tool Claude Opus 5 review of `server/PtkMcpServer/Worker/UnixWorkerProcessLauncher.cs` at `488e65354e1562c6b80f0adbdee8a07c53af08df`, followed by focused and final merge adjudication against `ProcessSessionWorkerFactory`.

## Evidence

After the broker has spawned, any handshake exception enters `UnixWorkerProcessLauncher.LaunchAsync` cleanup. `DescendantsUnknown` is wrapped in a `WorkerProcessException` carrying `ContainmentEmpty`, but `ConfirmedEmpty` disposes the authority and bare-rethrows the original exception. `ProcessSessionWorkerFactory` has no process object on either path and infers `ProcessLaunched` solely from whether the exception carries a containment task. The confirmed path therefore reports `ProcessLaunched: false` and no containment result even though the broker and worker were created and exact empty-domain proof completed. The unknown path truthfully reports that a process was launched.

## Predicted observable failure

A Unix broker starts, its handshake fails, and containment promptly proves the domain empty. The resulting `SessionWorkerStartException` says no process was launched and publishes no containment outcome. The same failure with slower proof says a process was launched and carries an observer. Launch-attempt accounting and lifecycle consumers therefore receive different provenance solely from proof timing.

## Required guard

Add deterministic Unix launcher/factory tests for a post-spawn handshake failure with containment already confirmed, immediately completing, and still pending. Assert every path reports `ProcessLaunched: true`; only the containment outcome and observer state may differ. Add a pre-spawn failure control that remains `ProcessLaunched: false`. Temporarily revert only the repair, prove the post-spawn assertions fail, restore it, then run focused launcher/factory tests and full server verification.

## Reviewer

Reviewer: `@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` (`frontier`, `max`, no-tool, session-only).

- Verdict: `accepted`; final severity `LOW`; confidence `high`.
- `guard_confirmed=false`; no repair was implemented or tested.
- Exact containment remains sound; this finding is limited to launch provenance and public failure metadata.
