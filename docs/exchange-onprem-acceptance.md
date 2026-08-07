# On-prem Exchange acceptance — owner-run

Purpose: close the on-prem Exchange leg of the warm-backend validation
(GitHub #30 / warm-backend slice 7). It proves three things on a real
Exchange environment: a warm ptk session pays Exchange implicit remoting's
setup cost once, Exchange objects render compactly instead of as text soup
or dropped objects, and nothing leaks across sessions. Read-only command
surface (`Get-Queue`, `Get-Mailbox`); the only mutations are the PSSession
itself and its removal.

## Setup (once, on the Exchange-capable machine)

1. Clone the repo and install: `.\scripts\install.ps1` (same as the Server
   2019 run; rtk must be present — the installer refuses clearly if not).
2. Let the installer register ptk with Claude Code, then open a Claude Code
   session on that machine.
3. Have ready: the Exchange server FQDN for the ConnectionUri (or run on
   the Exchange server itself), logged in as an account with Exchange view
   permissions; Kerberos as the logged-in user is fine.

## Run

Paste the block below into the Claude session there, replacing `EXCH-FQDN`:

```text
Using the ptk tools, run this on-prem Exchange acceptance and report a
numbered pass/fail table. Every command goes through ptk_invoke in the
default session unless a step says otherwise.

1. Baseline: call ptk_state; record engine and loaded modules (expect no
   Exchange-related modules).
2. Cold cost, measured — run:
     $t0=[datetime]::UtcNow
     $ex = New-PSSession -ConfigurationName Microsoft.Exchange -ConnectionUri http://EXCH-FQDN/PowerShell/ -Authentication Kerberos
     Import-PSSession $ex -CommandName Get-Queue,Get-Mailbox -AllowClobber | Out-Null
     $q = Get-Queue
     (([datetime]::UtcNow - $t0).TotalSeconds)
   Record the seconds, and whether Get-Queue rendered as a compact typed
   summary (queue identity, status, message count) rather than raw text or
   a dropped object.
3. Warm reuse, measured — in the SAME session:
     $t0=[datetime]::UtcNow; $q2 = Get-Queue; (([datetime]::UtcNow - $t0).TotalSeconds)
   Expect a small fraction of step 2's time: no session setup, no import.
4. Selected properties: Get-Queue | Select-Object Identity,Status,MessageCount
   — confirm the selected properties survive compression.
5. Bigger objects: Get-Mailbox -ResultSize 5 — confirm a compact per-type
   summary; note anything unreadable or misleading, and whether bounded
   output offered a recovery handle.
6. Isolation: ptk_session open name=probe, then in session probe run
   Get-Command Get-Queue — expect NOT found (the import must not leak into
   another session). Close the probe session.
7. Health after load: ptk_state on default — still ready, no
   reset_required.
8. Cleanup: Remove-PSSession $ex; then ptk_reset; confirm ptk_state shows
   a fresh worker.

Report the table plus both timings, and paste verbatim any output that
looked wrong.
```

## Report back

Paste the agent's table into GitHub #30 (or chat). Pass = warm reuse shows
(step 3 time ≪ step 2), rendering readable (steps 2, 4, 5), isolation
holds (step 6), health holds (step 7). #30's Exchange leg closes on that
evidence; anything that renders badly becomes its own finding.
