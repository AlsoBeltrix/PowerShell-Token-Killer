# Buffered audit startup validation

Status: COMPLETE 2026-09-08; owner approved the repair on that date.

## Scope and authority

The owner approved replacing byte-at-a-time retained audit validation with
buffered reads after the measured startup investigation. Preserve the existing
record bounds, JSON/hash-chain validation, protected-file handling, retention
policy, and startup ordering. This is one repair slice; it does not authorize
a live installation, candidate replacement, or release. The subsequent
owner instruction to keep running sessions alive authorized the local
configuration workaround recorded below; the in-place installer guard remains
unchanged.

## Evidence

At clean source `c4b223b`, the installed osx-arm64 build
`070e4205707b452bbbe328c87bd9755c` initialized in 27.933 seconds using the
existing audit root and 0.644 seconds using an empty temporary root. The
initialize handler itself took about 7 ms. The spool held about 127 MB;
no JSONL segment exceeded the default 30-day retention age, and usage was
below the 256 MiB capacity. Cleanup eligibility therefore did not account
for the delay. `ValidateRetainedSegments` reads each byte separately and
queries file length on every iteration; a native startup sample showed
heavy filesystem metadata activity.

## Implementation and verification

1. Isolate bounded JSONL record framing and read retained bytes in blocks.
   Keep the same parser, sequence/hash checks, and protected stream lifetime.
2. Add deterministic I/O regression coverage, including short reads, buffer
   boundaries, exact record limits, oversized records, and incomplete tails.
   Prove the regression fails with the original byte-at-a-time reader.
3. Run the server suite and registration handshake, and compare startup with
   the existing history. Run the remaining applicable repository verification
   entry points; record failures or omissions explicitly.
4. Close this record, update `.agents/state.md`, and commit the slice. Follow
   the repository push policy. Keep the active installation unchanged.

## Results

- The regression failed against the original reader after 288,012 individual
  byte reads for a 288,012-byte fixture. After the repair, the same fixture
  passes with bounded block reads and at most two length/position queries.
- All 15 new reader/startup cases pass: short reads (including one-byte
  returns), UTF-8 and block boundaries, exact/oversized records, incomplete
  tails, premature EOF, empty segments, and a hash-chain discontinuity beyond
  the first block. The focused sink/anchored-retention selection also passed
  73 tests before the two final startup cases were added.
- A Release checkout build reached initialize in 5.096 seconds using the
  existing audit root, versus the installed baseline's 27.933 seconds (about
  82% less elapsed time). The follow-up cold `ptk_state` reported healthy
  local-only audit and the probe exited normally. The measured repair build
  identity was `0.2.0+c4b223b.build.fe0eb3b40be84bc087d935a9ced4eb6d`; this is
  a dirty checkout build, not an installed/released artifact. The baseline
  was self-contained and the repair used `dotnet exec`, so the comparison
  is an end-to-end local observation rather than a controlled microbenchmark.
- SIEM passed 357/357; all five server and all three SIEM projects reported
  no vulnerable packages. Pester passed 116 with 3 platform skips; mini-SIEM
  lifecycle and the complete registration handshake passed.
- The initial server run passed 1,373/1,375; the two failures and three
  script-launch failures coincided with the PowerShell child-launch crashes
  documented in `.agents/machines.md`. With the checksum-proved
  isolated verification runtime, all three focused installer/stdio cases
  passed. The full server rerun passed 1,375/1,375 with the two existing
  xUnit analyzer warnings; no test was suppressed. `git diff --check` passed.
- Subsequent checks with the normal Homebrew PowerShell 7.6.5 passed the
  three affected installer/stdio cases, Pester (116 passed/3 platform skips),
  lifecycle, and registration handshake. Version queries also succeeded in
  both original test environments and under a PTY. The earlier child-launch
  failures do not establish that the system installation is broken; their
  cause remains unknown. No runtime replacement was required for this recheck.
- The initial repair did not produce a package or perform Windows/Linux
  execution. Its later local packaged proof is recorded below. The original
  installed payload and rc.2 artifacts remain unchanged.

## Follow-up: new connections while existing sessions remain running

On 2026-09-08 the owner reported another repository's 30-second MCP startup
timeout, then explicitly required an alternative that does not stop running
sessions. This is a one-time local launch-configuration workaround, not a
new upgrade/retained-version system or a bypass of the in-place install guard.

- Built the canonical `-LayoutOnly -Validate` osx-arm64 layout from clean
  `ae23c00`; the complete packaged handshake and all 30 direct-product checks
  with `-RequireCleanSource` passed. Exact package identity:
  `0.3.0-dev.gae23c00+ae23c00.build.f91a1a3eff31407db1f980f20b11c9a2`.
- The separate executable initialized in 4.925 seconds from
  `/Users/michael/Dev/roon-controller` with the existing audit history,
  reported healthy local-only audit, successfully executed a worker command,
  and exited normally when the probe closed its own stdin.
- Codex's global PTK registration now points at this fixed layout and has a
  90-second startup allowance for contention with older supervisors. Exact
  host paths and backup live in `.agents/machines.md`. The Codex configuration
  loader read back the expected command and timeout; all other TOML values
  were preserved and the prior file was backed up before atomic replacement.
- All 14 pre-existing PTK server/worker PIDs remained present after the
  switch. The original installed server DLL hash was unchanged. The current
  connection retained its original supervisor, worker, and warm-state marker.
- New Codex connections use the repair. Existing connections retain their
  existing workers; an already-failed connection does not gain tools merely
  because the configuration file changed. No old PTK session was stopped,
  reset, or migrated. Do not remove or overwrite the separate layout while
  it is registered or in use. A later ordinary in-place installation still
  follows `.agents/plans/stop-before-install.md`.

Configuration reference: [official OpenAI MCP documentation](https://learn.chatgpt.com/docs/extend/mcp?surface=cli).
