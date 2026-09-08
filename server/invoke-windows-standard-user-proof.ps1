#Requires -Version 7
# Hosted Windows runners are administrators. Exercise downloaded user installs
# with a disposable standard account rather than weakening the installer gate.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DownloadRoot,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$Rid,
    [Parameter(Mandatory)][string]$RtkPath
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true') {
    throw 'Disposable account orchestration is restricted to Windows GitHub Actions runners.'
}
$name = 'ptkproof' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$password = ConvertTo-SecureString ('Ptk9!' + [guid]::NewGuid().ToString('N')) -AsPlainText -Force
$account = New-LocalUser -Name $name -Password $password -AccountNeverExpires
$directory = Join-Path $env:RUNNER_TEMP $name
$pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
try {
    # Copy only the proof scripts and fresh archives into a directory the
    # standard account can read. Its profile and all product state are new.
    New-Item -ItemType Directory $directory | Out-Null
    $checkout = Split-Path -Parent $PSScriptRoot
    foreach ($entry in 'server', 'siem', 'scripts') {
        New-Item -ItemType Directory (Join-Path $directory $entry) | Out-Null
    }
    # These proof scripts load only other scripts; no checkout-built binary
    # is copied or used. The product always comes from the verified archives.
    Get-ChildItem (Join-Path $checkout 'server') -File -Filter '*.ps1' | Copy-Item -Destination (Join-Path $directory 'server')
    Get-ChildItem (Join-Path $checkout 'siem') -File -Filter '*.ps1' | Copy-Item -Destination (Join-Path $directory 'siem')
    Copy-Item (Join-Path $checkout 'scripts/ptk_build_provenance.psm1') (Join-Path $directory 'scripts')
    Copy-Item $DownloadRoot (Join-Path $directory 'downloads') -Recurse
    Copy-Item $RtkPath (Join-Path $directory 'rtk.exe')
    & icacls $directory /grant "${name}:(OI)(CI)M" /T /Q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant the proof account access to its fixture.' }
    $credential = [pscredential]::new("$env:COMPUTERNAME\$name", $password)
    $arguments = @('-NoProfile', '-File', ('"' + (Join-Path $directory 'server/test-downloaded-product.ps1') + '"'),
        '-DownloadRoot', ('"' + (Join-Path $directory 'downloads') + '"'), '-Version', $Version,
        '-SourceCommit', $SourceCommit, '-Rid', $Rid, '-RtkPath', ('"' + (Join-Path $directory 'rtk.exe') + '"'))
    $process = Start-Process -FilePath $pwsh -Credential $credential -LoadUserProfile `
        -ArgumentList $arguments -WorkingDirectory $directory -PassThru `
        -RedirectStandardOutput (Join-Path $directory 'proof.stdout.log') `
        -RedirectStandardError (Join-Path $directory 'proof.stderr.log')
    if (-not $process.WaitForExit(1200000)) {
        $process.Kill($true)
        throw 'Disposable standard-user proof exceeded twenty minutes.'
    }
    Get-Content (Join-Path $directory 'proof.stdout.log') | Out-Host
    Get-Content (Join-Path $directory 'proof.stderr.log') | Out-Host
    if ($process.ExitCode -ne 0) { throw "Standard-user proof failed: exit $($process.ExitCode)" }
}
finally {
    # Remove only the account created above. Logs remain on the disposable
    # runner for workflow diagnostics; no pre-existing account is modified.
    Remove-LocalUser -SID $account.SID
}
