# Runbook: Microsoft false-positive submission for PtkMcpServer.dll

**Audience:** an agent (or human) on the Windows machine where Defender flagged the DLL.
**Goal:** get `Trojan:MSIL/AsyncRAT.AB!MTB` reclassified upstream so all users stop being affected.
**Tracking:** GitHub issue #7 (`AlsoBeltrix/PowerShell-Token-Killer`).

## Context (read first)

- Defender's ML tier (`!MTB`) quarantines `PtkMcpServer.dll` as `Trojan:MSIL/AsyncRAT.AB!MTB` in two places:
  - build intermediate: `server\PtkMcpServer\obj\Release\net10.0\win-x64\PtkMcpServer.dll`
  - installed runtime: `%USERPROFILE%\.ptk\bin\PtkMcpServer.dll`
- This is a false positive: PTK is an open-source MCP server whose *job* is executing shell/PowerShell commands and managing process trees, which matches a generic RAT fingerprint.
- **Path exclusions for the build tree and `~/.ptk/bin` are already in place on this machine.** That means Defender will NOT flag the file in those paths anymore — verification must use a copy outside the excluded paths (exclusions are path-based).
- Environment of original report: Windows Server 2022 Standard (10.0.20348), build at commit `b03a359`, net10.0, win-x64.
- Safety rules: do NOT disable real-time protection, do NOT add new exclusions, use `-DisableRemediation` when scanning so your working copy isn't quarantined.

## Step 1 — Build the artifact at the flagged commit

```powershell
git -C <repo> checkout b03a359
dotnet publish server/PtkMcpServer -c Release -r win-x64
```

Artifact: `server\PtkMcpServer\obj\Release\net10.0\win-x64\PtkMcpServer.dll` (the `bin\Release\...\win-x64\publish\` copy of the same DLL is equally fine).

## Step 2 — Confirm live detection from a NON-excluded path (do not skip)

If the analyst tests a file that current definitions no longer detect, the submission is closed as no-action. Prove the detection is live first:

```powershell
New-Item -ItemType Directory -Force "$env:TEMP\ptk-fp" | Out-Null
Copy-Item server\PtkMcpServer\obj\Release\net10.0\win-x64\PtkMcpServer.dll "$env:TEMP\ptk-fp\"
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -Scan -ScanType 3 -File "$env:TEMP\ptk-fp\PtkMcpServer.dll" -DisableRemediation
```

- **Detection reported** → proceed to Step 3.
- **Clean** → update signatures and retry once:
  ```powershell
  & "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -SignatureUpdate
  ```
  Still clean after that → STOP and report back "no longer detected at signature version X" (get it from `Get-MpComputerStatus`). That outcome means Microsoft already fixed it; skip to Step 5 verification.

## Step 3 — Capture the evidence bundle

```powershell
$out = "$env:TEMP\ptk-fp\evidence.txt"
Get-FileHash "$env:TEMP\ptk-fp\PtkMcpServer.dll" -Algorithm SHA256 | Out-File $out
Get-MpThreat | Where-Object ThreatName -like '*AsyncRAT*' | Format-List | Out-File $out -Append
Get-MpThreatDetection | Sort-Object InitialDetectionTime -Descending | Select-Object -First 5 | Format-List | Out-File $out -Append
Get-MpComputerStatus | Select-Object AMProductVersion, AntivirusSignatureVersion | Format-List | Out-File $out -Append
Get-Content $out
```

Record: SHA256, detection name, `AntivirusSignatureVersion`, detection timestamps.

## Step 4 — Submit at the WDSI portal (browser/human required)

Portal: `https://www.microsoft.com/en-us/wdsi/filesubmission`

- Sign in with a Microsoft account; submitter type: **Software developer** (prioritized over home-user reports).
- Upload `PtkMcpServer.dll` (plain or zipped; no password needed for FP submissions).
- "What do you believe this file is?" → **Incorrect detection (false positive)**.
- Detection name: `Trojan:MSIL/AsyncRAT.AB!MTB`.
- Comment template (fill in the blanks):

  > This is a false positive on an open-source developer tool. PtkMcpServer is the server component of PowerShell-Token-Killer (https://github.com/AlsoBeltrix/PowerShell-Token-Killer), an MCP (Model Context Protocol) server that executes shell/PowerShell commands and manages process trees on behalf of AI coding harnesses — command execution is its documented, legitimate purpose, which is why it matches a generic RAT behavioral profile. The binary is unsigned .NET (net10.0, win-x64) and reproducibly buildable from public source at commit b03a359. Detection breaks our users' builds and quarantines the installed server so it cannot launch (see https://github.com/AlsoBeltrix/PowerShell-Token-Killer/issues/7).
  > SHA256: `<hash>` — detected at signature version `<AntivirusSignatureVersion>` on Windows Server 2022 (10.0.20348).

- Save the **submission ID** and the tracking URL (submission history under the same account).

## Step 5 — After the verdict (typically hours–2 business days)

```powershell
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -SignatureUpdate
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -Scan -ScanType 3 -File "$env:TEMP\ptk-fp\PtkMcpServer.dll"
```

Clean scan on the non-excluded copy = fixed upstream. Then:
1. Remove the path exclusions for the build tree and `~/.ptk/bin` (blanket exclusions on a build tree are their own risk).
2. Rebuild + reinstall, confirm the server launches.
3. Clean up `$env:TEMP\ptk-fp`.

## Report back (comment on issue #7)

- Submission ID + date, SHA256 submitted, signature version at detection time.
- Step 2 outcome (live detection confirmed, or already-clean).
- Step 5 outcome once the verdict lands (clean / still detected).
- Note: `!MTB` fixes are sometimes hash-specific. If a future build re-triggers, resubmit with the new hash and reference the prior submission ID — repeat developer reports push toward a classifier fix. Durable fix is Authenticode signing (tracked separately).
