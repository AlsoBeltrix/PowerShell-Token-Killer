# Unpublished candidate verification: `0.3.0-rc.2`

## Scope and immutable source

- Owner authority: 2026-09-04 build/test go, recorded in `.agents/decisions.md`.
  Do not publish, create an explicit tag, close issues, replace the owner's
  installed payload, or stop existing PTK sessions.
- Candidate source: `c8b084fbb79c9d73965dbfc632163919c29e50dd`.
- Product tree: unchanged from verified product commit `0c9328a`; the diff
  from six-job green CI commit `72ccd90` contains only `.agents/` records.
- Release workflow: [33928685705](https://github.com/AlsoBeltrix/PowerShell-Token-Killer/actions/runs/33928685705),
  dispatched 2026-09-04 at 23:13:43 UTC with version `0.3.0-rc.2`.
  GitHub rejected a raw-SHA dispatch ref without creating a run; dispatching
  `master` succeeded and the run API confirmed the exact source SHA above.
- Before dispatch, no canonical tag or release record used `v0.3.0-rc.2`.
  Existing `v0.3.0-rc.1` records were left untouched.

## Evidence status

The native `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, and `osx-arm64`
jobs started and reached layout building. This is progress, not a passing
candidate result. Draft assembly and downloaded-artifact verification remain
pending. The candidate is not release-ready and publication is not authorized.

## Local isolation

Read-only process inspection found two live installed supervisors, PIDs 7123
and 7562, using `/Users/michael/.ptk/bin/PtkMcpServer`, with their workers and
brokers. No process was stopped and no installed file was replaced. GitHub
builds run on separate native runners. Local follow-up must use disposable
roots and restrict uninstall to the disposable `.ptk` being tested.

## Remaining proof

Follow `.agents/plans/release-readiness.md`'s candidate procedure: completed
five-RID gates, unpublished draft metadata and exact asset inventory,
downloaded SHA-256/digest agreement, unique PTK/SIEM provenance, native
installation/lifecycle and product/SIEM checks, and signing/notarization
evidence. Record any unperformed check explicitly; source-test success is not
a substitute for candidate evidence.
