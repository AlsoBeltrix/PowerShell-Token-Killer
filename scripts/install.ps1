#Requires -Version 7
<#
.SYNOPSIS
The ptk installer. Installs a published release, or this checkout, into the
one ptk home (~/.ptk); also produces the canonical layout for release CI.

.DESCRIPTION
Install ensures rtk is present, then obtains a payload — a prebuilt release
asset with -FromRelease, or a self-contained publish of this checkout
otherwise — and from there the path is identical: stage it, smoke-test it,
snapshot the prior payload, activate as a unit, and run the per-agent init
that registers and wires up the detected harnesses (claude, codex, grok,
agy, kimi). An
interactive install asks once which harnesses to skip (pacman-style;
Enter wires all); -Agent/-SkipAgent/-AllAgents pre-answer. Any failure
during activation or registration restores the previous payload
byte-identically.

~/.ptk holds bin/, src/, scripts/, VERSION, BUILD-PROVENANCE.json, LICENSE,
README.md — replaced
wholesale on upgrade. Everything else there is user-owned and is never
touched except by -Purge.

rtk is a required dependency: the server exits 78 without one, and finds it
only on PATH or through PTK_RTK_PATH. An rtk already on PATH is used as-is;
otherwise, once the user agrees, rtk goes into ~/.local/bin (or
$RTK_INSTALL_DIR) as its own installer would put it: rtk's install.sh runs on
Linux and macOS, and on Windows the latest release is checksum-verified and
placed there. It is recorded so uninstall removes only that copy. If that
directory is not on PATH, the install stops before registering anything and
says how to add it.

A source install (no -FromRelease) also needs the .NET SDK and the NativeAOT
native toolchain. A missing SDK is offered as a per-user install under
~/.dotnet; a missing toolchain needs admin/sudo, so the installer stops and
names the command instead. Consent is asked before anything is staged;
-InstallPrerequisites agrees in advance, and a non-interactive session that
was not given it refuses rather than fetching software unannounced.

-Uninstall reverses all of it and keeps user files; add -Purge to remove them.
-LayoutOnly -OutputDir <dir> builds the layout and stops — release CI drives
that mode per RID, so release artifacts and local installs are the same layout
by construction.

Install logic lives in small functions so a future `PtkMcpServer install`
verb can host it in-process (the binary embeds the PowerShell engine).

.EXAMPLE
pwsh -File scripts/install.ps1 -FromRelease         # latest published release
pwsh -File scripts/install.ps1 -FromRelease -Version 0.2.0
pwsh -File scripts/install.ps1                      # build and install this checkout
pwsh -File scripts/install.ps1 -FromRelease -InstallPrerequisites  # unattended
pwsh -File scripts/install.ps1 -Uninstall
pwsh -File scripts/install.ps1 -LayoutOnly -Validate -OutputDir out/ptk-layout
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    # Deprecated, accepted for compatibility: the full per-agent init
    # (hooks, registrations, guidance - every detected harness) runs by
    # DEFAULT after a successful registration; -Hook adds nothing.
    [Parameter(ParameterSetName = 'Install')]
    [switch]$Hook,

    [Parameter(ParameterSetName = 'Uninstall', Mandatory)]
    [switch]$Uninstall,

    # Build the canonical layout into -OutputDir and stop: no home install,
    # no registration. This is the mode release CI drives per RID.
    [Parameter(ParameterSetName = 'LayoutOnly', Mandatory)]
    [switch]$LayoutOnly,
    [Parameter(ParameterSetName = 'LayoutOnly', Mandatory)]
    [string]$OutputDir,
    # Target RID for -LayoutOnly (defaults to this machine's). PTK has no
    # supported cross-target native package build; Unix layouts include a
    # platform-native worker broker.
    [Parameter(ParameterSetName = 'LayoutOnly')]
    [string]$Rid,

    # Version stamped into the publish (-p:Version) and the VERSION file.
    # Release CI passes the tag version; source installs default to
    # <nearest-tag>-dev.g<shortsha> (e.g. 0.3.0-dev.gabc1234). With
    # -FromRelease, selects which release to install instead (default:
    # latest).
    [Parameter(ParameterSetName = 'Install')]
    [Parameter(ParameterSetName = 'LayoutOnly')]
    [string]$Version,

    # Install a prebuilt release asset instead of building this checkout.
    # This is how a user with no .NET SDK installs; everything after the
    # payload is obtained is identical either way.
    [Parameter(ParameterSetName = 'Install')]
    [switch]$FromRelease,

    # Also remove user-owned files under ~/.ptk. Uninstall keeps them by
    # default.
    [Parameter(ParameterSetName = 'Uninstall')]
    [switch]$Purge,

    # Run the full public handshake against the layout without activating it.
    [Parameter(ParameterSetName = 'LayoutOnly')]
    [switch]$Validate,

    # Per-harness consent, passed through to ptk_init: -Agent wires only the
    # named harnesses, -SkipAgent wires all detected except these,
    # -AllAgents answers yes to everything without prompting. With none of
    # them, an interactive install asks once (pacman-style skip selection).
    [Parameter(ParameterSetName = 'Install')]
    [string[]]$Agent,
    [Parameter(ParameterSetName = 'Install')]
    [string[]]$SkipAgent,
    [Parameter(ParameterSetName = 'Install')]
    [switch]$AllAgents,

    # Agree in advance to installing missing prerequisites (rtk; the .NET
    # SDK for a source build). Without it, an interactive install asks and a
    # non-interactive one refuses with manual instructions.
    [Parameter(ParameterSetName = 'Install')]
    [switch]$InstallPrerequisites
)

$ErrorActionPreference = 'Stop'
if ($AllAgents -and ($Agent -or $SkipAgent)) {
    throw '-AllAgents cannot be combined with -Agent or -SkipAgent.'
}
if ($Agent -and $SkipAgent) {
    throw '-Agent and -SkipAgent are mutually exclusive.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$ptkHome = Join-Path $HOME '.ptk'
# Everything the installer owns and replaces wholesale on upgrade; anything
# else under ~/.ptk is user-owned and never touched here.
$payloadEntries = @(
    'bin', 'src', 'scripts', 'VERSION', 'BUILD-PROVENANCE.json', 'LICENSE', 'README.md')
# Records an rtk this installer placed, so uninstall removes only that copy
# and never a user's own.
$rtkMarkerName = '.ptk-installed-rtk'
$arpKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ptk'
$installTransactionModule = Join-Path $PSScriptRoot 'ptk_install_transaction.psm1'
Import-Module $installTransactionModule -Force
$buildProvenanceModule = Join-Path $PSScriptRoot 'ptk_build_provenance.psm1'
Import-Module $buildProvenanceModule -Force

function Get-PtkRid {
    $arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported architecture: $_" }
    }
    $os = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' }
    else { throw 'Unsupported OS.' }
    "$os-$arch"
}

function Assert-PtkNativeBuildRid {
    param([Parameter(Mandatory)][string]$TargetRid)

    $localRid = Get-PtkRid
    if ($TargetRid -cne $localRid) {
        throw (
            "Cross-RID layout publishing is refused: target '$TargetRid' does not " +
            "match build host '$localRid'. PTK packages have no supported " +
            'cross-target native build; run this layout build on a matching host.')
    }
}

function Assert-NotElevated {
    # ptk is a per-user tool and the warm runspace inherits the harness's
    # privileges; an elevated install invites root-owned files and an
    # elevated-execution footgun (plan: Design commitments).
    $elevated = if ($IsWindows) {
        [Security.Principal.WindowsPrincipal]::new(
            [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    else {
        (id -u) -eq '0'
    }
    if ($elevated) {
        throw 'Refusing to run elevated (root/Administrator): ptk installs per-user. Re-run from a normal shell.'
    }
}

function Get-PtkVersion {
    if ($Version) {
        # Accept tag-shaped values (v0.2.0-rc.1): release CI passes the git
        # tag verbatim and MSBuild rejects a leading v as an invalid version.
        return $Version -replace '^[vV]', ''
    }
    # Get-Command first: a missing native command is a terminating
    # CommandNotFoundException that 2>$null does not suppress.
    $git = Get-Command git -ErrorAction SilentlyContinue
    $sha = if ($git) { & $git -C $repoRoot rev-parse --short HEAD 2>$null } else { $null }
    if (-not $sha -or $LASTEXITCODE -ne 0) { $sha = 'unknown' }
    # Base tracks the nearest reachable tag rather than a fixed literal,
    # which otherwise goes stale the moment a release ships: a source
    # install kept reporting 0.2.0-dev after tags had already reached
    # 0.3.0. No reachable tag (e.g. a shallow clone without tags) falls
    # back to 0.0.0 rather than failing the install.
    $base = '0.0.0'
    if ($git) {
        $nearestTag = & $git -C $repoRoot describe --tags --abbrev=0 2>$null
        if ($LASTEXITCODE -eq 0 -and $nearestTag) {
            $numeric = (($nearestTag -replace '^[vV]', '') -split '-', 2)[0]
            if ($numeric -match '^\d+(\.\d+){1,3}$') { $base = $numeric }
        }
    }
    # Missing Git metadata is a handled fallback, not a failed installer result.
    $global:LASTEXITCODE = 0
    "$base-dev.g$sha"
}

function Assert-PtkRuntimeNotRunning {
    # Replacing or removing bin/ under a live server half-fails on Windows
    # file locks and leaves a stale server running old code elsewhere;
    # recorded precedent: every rebuild needed Stop-Process first
    # (.agents/state.md).
    $ptkRuntimeProcessNames = @('PtkMcpServer', 'PtkWorkerBroker')
    $running = @(Get-Process -Name $ptkRuntimeProcessNames -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($ptkHome, [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -gt 0) {
        throw ("PTK process(es) from {0} are running (PID {1}; name(s) {2}). " +
            "Stop all PTK processes or restart the harness session, then re-run.") -f
            $ptkHome,
            ($running.Id -join ', '),
            (($running.ProcessName | Sort-Object -Unique) -join ', ')
    }
}

function Get-PtkServerBinaryName {
    param([string]$TargetRid)
    if ($TargetRid -like 'win-*') { 'PtkMcpServer.exe' } else { 'PtkMcpServer' }
}

# Interim mitigation for GitHub issue #7: Microsoft Defender falsely detected
# PtkMcpServer.dll (reported as Trojan:MSIL/AsyncRAT.AB!MTB) and quarantined it
# out of the build output and the installed payload. When that happens the
# publish/copy steps succeed but the file is silently missing afterwards, so
# verify the payload landed intact and fail with actionable guidance if not.
function Assert-PtkPayloadIntact {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$TargetRid
    )
    $required = @(
        (Join-Path $Root 'bin' (Get-PtkServerBinaryName -TargetRid $TargetRid))
        (Join-Path $Root 'bin' 'PtkMcpServer.dll')
        (Join-Path $Root 'src' 'PwshTokenCompressor.psd1')
        (Join-Path $Root 'LICENSE')
        (Join-Path $Root 'VERSION')
        (Join-Path $Root 'BUILD-PROVENANCE.json')
    )
    if ($TargetRid -notlike 'win-*') {
        $required += Join-Path $Root 'bin' 'PtkWorkerBroker'
    }
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($missing.Count -eq 0) { return }
    Write-Warning ((@(
        'These files are missing from the freshly written payload:'
        ($missing | ForEach-Object { "  $_" })
        'An antivirus quarantine is the most likely cause: Microsoft Defender has'
        'falsely detected PtkMcpServer.dll (Trojan:MSIL/AsyncRAT.AB!MTB) and removed'
        'it immediately after install. See the false-positive tracking issue'
        'https://github.com/AlsoBeltrix/PowerShell-Token-Killer/issues/7 and the'
        'runbook .agents/plans/defender-fp-submission.md. Check the Defender'
        'protection history before restoring anything, and do not add broad'
        'exclusions.'
    ) | ForEach-Object { $_ }) -join [Environment]::NewLine)
    throw 'Install incomplete: payload files missing (possible antivirus quarantine).'
}

# Issue #42: the check above tests five named paths, which a payload missing
# 185 of its 296 files still passes -- every named file was present. The
# staged layout is generated in the same run, so the honest question is
# whether the installed tree matches the tree we just built, and that
# subsumes the quarantine case above: a quarantined PtkMcpServer.dll is a
# missing entry like any other.
#
# Compared on relative path and length rather than content hash: this runs on
# every install over ~300 files, the failure being caught is wholesale loss
# and truncation, and hashing the set is not worth the per-install cost. The
# server binary's own integrity is already proved by the package smoke test
# launching it.
# Issue #42: installs already in the nested state exist in the wild, created
# before the activation guard above. Such an install keeps running a stale,
# incomplete payload while the complete one sits unused one level down, and
# nothing about it looks wrong -- the server starts and handshakes. Say so
# plainly rather than rearranging someone's install directory automatically.
function Assert-PtkPayloadNotNested {
    param([Parameter(Mandatory)][string]$Root)

    $bin = Join-Path $Root 'bin'
    $inner = Join-Path $bin 'bin'
    if (-not (Test-Path -LiteralPath $inner -PathType Container)) { return }

    # Only a bin/bin that is itself a payload is the #42 shape. Anything else
    # under there is somebody's data directory and none of our business.
    $marker = @(Get-ChildItem -LiteralPath $inner -Filter 'PtkMcpServer.*' -File -Force -ErrorAction SilentlyContinue)
    if ($marker.Count -eq 0) { return }

    $outerCount = @(Get-ChildItem -LiteralPath $bin -File -Force -ErrorAction SilentlyContinue).Count
    $innerCount = @(Get-ChildItem -LiteralPath $inner -File -Force -ErrorAction SilentlyContinue).Count
    throw ((@(
        "This install is nested: '$inner' contains a second, complete payload."
        "The registered server is '$bin' ($outerCount files), but the payload actually"
        "installed most recently is '$inner' ($innerCount files)."
        ''
        'This is GitHub issue #42. An earlier install moved the new payload inside the'
        'old one instead of replacing it, so the stale payload stayed registered. It'
        'starts and handshakes normally, but it is missing assemblies, and PowerShell'
        'commands whose objects need them fail -- Get-Process and Get-Service return'
        'nothing and recycle the warm session.'
        ''
        'To repair: close every ptk process and any harness that launched one, remove'
        "'$Root' entirely, and reinstall. Nothing under it is user data."
    ) | ForEach-Object { $_ }) -join [Environment]::NewLine)
}

function Get-PtkPayloadIndex {
    param([Parameter(Mandatory)][string]$Root)

    $map = @{}
    if (-not (Test-Path -LiteralPath $Root)) { return $map }
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Force) {
        $map[[IO.Path]::GetRelativePath($Root, $file.FullName)] = $file.Length
    }
    return $map
}

function Assert-PtkPayloadComplete {
    param(
        # Captured before activation: the transaction MOVES the staged tree,
        # so it no longer exists when the installed payload is validated.
        [Parameter(Mandatory)][hashtable]$StagedIndex,
        [Parameter(Mandatory)][string]$InstalledRoot
    )

    $staged = $StagedIndex
    $installed = Get-PtkPayloadIndex -Root $InstalledRoot

    $missing = [Collections.Generic.List[string]]::new()
    $truncated = [Collections.Generic.List[string]]::new()
    foreach ($relative in $staged.Keys) {
        if (-not $installed.ContainsKey($relative)) {
            $missing.Add($relative)
            continue
        }
        if ($installed[$relative] -ne $staged[$relative]) {
            $truncated.Add(
                "$relative (expected $($staged[$relative]) bytes, found $($installed[$relative]))")
        }
    }
    if ($missing.Count -eq 0 -and $truncated.Count -eq 0) { return }

    # Bounded: a wholesale loss would otherwise print hundreds of lines.
    $show = {
        param($label, $items)
        if ($items.Count -eq 0) { return @() }
        $head = @($items | Sort-Object | Select-Object -First 15)
        $rest = $items.Count - $head.Count
        @("$label ($($items.Count)):") +
        ($head | ForEach-Object { "  $_" }) +
        $(if ($rest -gt 0) { @("  ... and $rest more") } else { @() })
    }

    $lines = @(
        "The installed payload does not match the payload staged in this run."
        (& $show 'Missing from the install' $missing)
        (& $show 'Wrong size in the install' $truncated)
        'Installed root: ' + $InstalledRoot
        'An antivirus quarantine is one known cause: Microsoft Defender has falsely'
        'detected PtkMcpServer.dll (Trojan:MSIL/AsyncRAT.AB!MTB) and removed it right'
        'after install. See https://github.com/AlsoBeltrix/PowerShell-Token-Killer/issues/7'
        'and the runbook .agents/plans/defender-fp-submission.md. Check the Defender'
        'protection history before restoring anything, and do not add broad exclusions.'
        'A previous payload left in place at the install root is the other known cause'
        '(issue #42); stop every ptk process and any harness that launched one, then'
        'reinstall.'
    ) | ForEach-Object { $_ }
    throw ($lines -join [Environment]::NewLine)
}

# --- release payload -------------------------------------------------------
# Obtaining the payload is the only thing that differs between installing a
# published release and building this checkout. Everything downstream -
# transaction, validation, registration, harness init - is shared.

function Select-PtkLatestPublishedReleaseTag {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Release
    )

    $eligible = foreach ($candidate in $Release) {
        if ($null -eq $candidate) {
            throw 'GitHub returned an invalid null release entry.'
        }
        $draft = $candidate.PSObject.Properties['draft']
        $published = $candidate.PSObject.Properties['published_at']
        if ($null -eq $draft -or $draft.Value -isnot [bool] -or $null -eq $published) {
            throw 'GitHub returned an invalid release entry.'
        }
        if ($draft.Value) { continue }
        if ($null -eq $published.Value) { continue }

        [DateTimeOffset]$publishedAt = [DateTimeOffset]::MinValue
        if ($published.Value -is [DateTimeOffset]) {
            $publishedAt = $published.Value.ToUniversalTime()
        }
        elseif ($published.Value -is [DateTime]) {
            $date = $published.Value
            if ($date.Kind -eq [DateTimeKind]::Unspecified) {
                $date = [DateTime]::SpecifyKind($date, [DateTimeKind]::Utc)
            }
            $publishedAt = [DateTimeOffset]::new($date).ToUniversalTime()
        }
        else {
            $text = [Convert]::ToString(
                $published.Value,
                [Globalization.CultureInfo]::InvariantCulture)
            $parsed = [DateTimeOffset]::TryParse(
                $text,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::AssumeUniversal -bor
                    [Globalization.DateTimeStyles]::AdjustToUniversal,
                [ref]$publishedAt)
            if (-not $parsed) {
                throw 'GitHub returned a release with an invalid published_at value.'
            }
        }

        $tagProperty = $candidate.PSObject.Properties['tag_name']
        $tag = if ($null -eq $tagProperty) { $null } else { $tagProperty.Value }
        if ($tag -isnot [string] -or [string]::IsNullOrWhiteSpace($tag)) {
            throw 'GitHub returned a published release without a valid tag_name.'
        }
        [pscustomobject]@{
            Tag = $tag
            PublishedAt = $publishedAt
        }
    }

    $ordered = @($eligible | Sort-Object PublishedAt -Descending)
    if ($ordered.Count -eq 0) {
        throw 'GitHub returned no published releases.'
    }
    $latest = @($ordered | Where-Object { $_.PublishedAt -eq $ordered[0].PublishedAt })
    if ($latest.Count -ne 1) {
        throw 'GitHub returned an ambiguous latest published release.'
    }
    $latest[0].Tag
}

function Get-PtkLatestPublishedReleaseTag {
    param([Parameter(Mandatory)][string]$Repository)

    $releaseResponse = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$Repository/releases?per_page=100" `
        -Headers @{
            Accept = 'application/vnd.github+json'
            'X-GitHub-Api-Version' = '2022-11-28'
        }
    Select-PtkLatestPublishedReleaseTag -Release @($releaseResponse)
}

function Get-PtkReleaseLayout {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$TargetRid,
        [string]$ReleaseVersion
    )

    $repository = 'AlsoBeltrix/PowerShell-Token-Killer'
    $extension = if ($TargetRid -like 'win-*') { 'zip' } else { 'tar.gz' }
    if ($ReleaseVersion) {
        $tag = if ($ReleaseVersion.StartsWith('v')) { $ReleaseVersion } else { "v$ReleaseVersion" }
    }
    else {
        $tag = Get-PtkLatestPublishedReleaseTag -Repository $repository
    }
    $number = $tag.TrimStart('v')
    $asset = "ptk-$number-$TargetRid.$extension"
    $base = "https://github.com/$repository/releases/download/$tag"

    $staging = Join-Path ([IO.Path]::GetTempPath()) ("ptk-dl-{0}" -f ([guid]::NewGuid()))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        Write-Host "Downloading $asset ($tag)..."
        $archive = Join-Path $staging $asset
        Invoke-WebRequest -Uri "$base/$asset" -OutFile $archive -UseBasicParsing
        $sums = Join-Path $staging 'SHA256SUMS'
        Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile $sums -UseBasicParsing

        $line = Get-Content -LiteralPath $sums |
            Where-Object { $_ -match [regex]::Escape($asset) } |
            Select-Object -First 1
        if (-not $line) { throw "SHA256SUMS has no entry for $asset." }
        $expected = ($line -split '\s+')[0].ToLowerInvariant()
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual) {
            throw ("Checksum mismatch for {0}.`n  expected {1}`n  actual   {2}`n" +
                'Refusing to install an unverified download.') -f $asset, $expected, $actual
        }
        Write-Host '  checksum verified'

        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        if ($extension -eq 'zip') {
            Expand-Archive -LiteralPath $archive -DestinationPath $Destination -Force
        }
        else {
            tar -xzf $archive -C $Destination
            if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $asset" }
        }
        $provenancePath = Join-Path $Destination 'BUILD-PROVENANCE.json'
        if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) {
            $legacy = New-PtkLegacyBuildProvenance `
                -Product 'ptk' -ProductVersion $number -TargetRid $TargetRid
            Write-PtkBuildProvenance -Provenance $legacy -Path $provenancePath
        }
        return $number
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- prerequisites ---------------------------------------------------------
# Missing prerequisites are installed only with the user's agreement:
# -InstallPrerequisites agrees in advance, an interactive session asks (Enter
# agrees), and a session that cannot ask declines. PTK_INSTALL_INTERACTIVE=1
# forces the prompt path and =0 the non-interactive one (test seam).
function Confirm-PtkPrerequisiteInstall {
    param([Parameter(Mandatory)][string]$Question)
    if ($InstallPrerequisites) { return $true }
    $interactive = switch ($env:PTK_INSTALL_INTERACTIVE) {
        '1' { $true }
        '0' { $false }
        default { -not [Console]::IsInputRedirected }
    }
    if (-not $interactive) {
        # Without this, the refusal that follows reads as if the user had
        # been asked and declined (twice misread on NETWATCH-01, 2026-10-24).
        Write-Warning ("Not asked, because this session cannot take an answer (its input is " +
            "redirected): $Question Treating that as no. Run the installer in an interactive " +
            'terminal, or pass -InstallPrerequisites to agree in advance.')
        return $false
    }
    $answer = Read-Host "$Question [Y/n]"
    [string]::IsNullOrWhiteSpace($answer) -or $answer.Trim() -match '^(?i:y|yes)$'
}

# Building this checkout needs the .NET SDK and the NativeAOT native
# toolchain. The SDK installs per user without elevation, so it is offered;
# the toolchain needs admin/sudo, which this installer refuses to run under,
# so it is named with the command that installs it.
$dotnetSdkMajor = 10
$userDotnetRoot = Join-Path $HOME '.dotnet'

# True when a .NET SDK of at least $dotnetSdkMajor is reachable. A per-user
# SDK under ~/.dotnet (from an earlier run) counts and is put on this
# process's PATH, so a re-run before the user adds it to PATH neither asks
# nor reinstalls.
function Test-PtkDotnetSdk {
    $exe = if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }
    $candidates = @(
        Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty Source
        Join-Path $userDotnetRoot $exe
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    foreach ($dotnet in $candidates) {
        $sdks = @(& $dotnet --list-sdks 2>$null)
        $ok = $LASTEXITCODE -eq 0 -and [bool]($sdks | Where-Object {
                $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge $dotnetSdkMajor })
        $global:LASTEXITCODE = 0
        if (-not $ok) { continue }
        $root = Split-Path -Parent $dotnet
        if ($root -eq $userDotnetRoot) {
            $env:DOTNET_ROOT = $root
            $env:PATH = $root + [IO.Path]::PathSeparator + $env:PATH
        }
        return $true
    }
    $false
}

# Microsoft's documented per-user installer, into ~/.dotnet. On Windows the
# script is Authenticode-signed and must verify before it runs.
function Install-PtkDotnetSdk {
    $channel = "$dotnetSdkMajor.0"
    $staging = Join-Path ([IO.Path]::GetTempPath()) ("ptk-dotnet-{0}" -f ([guid]::NewGuid()))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        if ($IsWindows) {
            $script = Join-Path $staging 'dotnet-install.ps1'
            Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script
            $signature = Get-AuthenticodeSignature -LiteralPath $script
            if ($signature.Status -ne 'Valid') {
                throw "dotnet-install.ps1 signature is $($signature.Status); refusing to run it."
            }
            & ([Environment]::ProcessPath) -NoProfile -File $script `
                -Channel $channel -InstallDir $userDotnetRoot -NoPath | Out-Host
        }
        else {
            $script = Join-Path $staging 'dotnet-install.sh'
            Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.sh' -OutFile $script
            bash $script --channel $channel --install-dir $userDotnetRoot --no-path | Out-Host
        }
        if ($LASTEXITCODE -ne 0) { throw "dotnet-install failed with exit code $LASTEXITCODE." }
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-PtkDotnetSdk)) {
        throw "dotnet-install finished but no .NET $dotnetSdkMajor SDK answers under $userDotnetRoot."
    }
    Write-Host ("Installed the .NET $dotnetSdkMajor SDK to $userDotnetRoot. To use it outside " +
        'this installer, add that directory to PATH and set DOTNET_ROOT to it.')
}

# Why a NativeAOT build cannot run here, with the install command; $null when
# the toolchain is present.
function Get-PtkNativeToolchainProblem {
    param([Parameter(Mandatory)][string]$TargetRid)
    if ($IsWindows) {
        $arm64 = $TargetRid -eq 'win-arm64'
        $component = $arm64 ? 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' :
            'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
        $programFiles = ${env:ProgramFiles(x86)}
        $vswhere = if ($programFiles) {
            Join-Path $programFiles 'Microsoft Visual Studio' 'Installer' 'vswhere.exe'
        }
        if ($vswhere -and (Test-Path -LiteralPath $vswhere)) {
            $found = & $vswhere -latest -products '*' -requires $component -property installationPath
            $global:LASTEXITCODE = 0
            if ($found) { return $null }
        }
        $extra = $arm64 ? ' --add Microsoft.VisualStudio.Component.VC.Tools.ARM64' : ''
        return ("NativeAOT needs Visual Studio or Build Tools with the 'Desktop development " +
            "with C++' workload$($arm64 ? ' and the ARM64 build tools' : ''). For example:`n" +
            '  winget install Microsoft.VisualStudio.2022.BuildTools --override ' +
            "`"--passive --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended$extra`"")
    }
    if ($IsMacOS) {
        xcode-select -p *> $null
        $ok = $LASTEXITCODE -eq 0
        $global:LASTEXITCODE = 0
        if ($ok) { return $null }
        return "NativeAOT needs the Xcode Command Line Tools. Install them with:`n  xcode-select --install"
    }
    $missing = @()
    if (-not (Get-Command clang -CommandType Application -ErrorAction SilentlyContinue)) {
        $missing += 'clang'
    }
    if (-not (Test-Path -LiteralPath '/usr/include/zlib.h')) { $missing += 'zlib development headers' }
    if ($missing.Count -eq 0) { return $null }
    "NativeAOT needs $($missing -join ' and '). For example:`n" +
        "  sudo apt-get install clang zlib1g-dev    # Debian/Ubuntu`n" +
        '  sudo dnf install clang zlib-devel        # Fedora/RHEL'
}

$sourceBuildFallback = 'To skip building, install a prebuilt release with -FromRelease instead.'

# Checked before any prerequisite question: a build that cannot succeed must
# not ask about, or install, anything on the way to failing.
function Assert-PtkNativeToolchain {
    param([Parameter(Mandatory)][string]$TargetRid)
    $problem = Get-PtkNativeToolchainProblem -TargetRid $TargetRid
    if ($problem) { throw "$problem`nInstall it, then re-run this installer. $sourceBuildFallback" }
}

function Assert-PtkDotnetSdk {
    if (Test-PtkDotnetSdk) { return }
    $question = ("Building this checkout needs the .NET $dotnetSdkMajor SDK, which was not found. " +
        "Install it for this user into $userDotnetRoot?")
    if (-not (Confirm-PtkPrerequisiteInstall -Question $question)) {
        throw ("The .NET $dotnetSdkMajor SDK is required to build this checkout " +
            '(https://dotnet.microsoft.com/download); re-run with -InstallPrerequisites to let ' +
            "this installer fetch it. $sourceBuildFallback")
    }
    Install-PtkDotnetSdk
}

# --- rtk -------------------------------------------------------------------
# rtk is a required dependency: the server exits 78 without one, and finds it
# only through PTK_RTK_PATH or PATH. An rtk the user already has is used as-is
# and never touched. Otherwise, with the user's agreement, rtk goes where its
# own installer puts it (owner, 2026-10-24): on Linux and macOS rtk's
# install.sh runs and installs it; on Windows, which rtk ships no installer
# for, the latest release zip is checksum-verified and rtk.exe placed the way
# rtk's manual instructions say. Both land in $RTK_INSTALL_DIR, default
# ~/.local/bin. The copy is recorded so uninstall removes only that one.

$rtkInstallDir = if ($env:RTK_INSTALL_DIR) { $env:RTK_INSTALL_DIR } else { Join-Path $HOME '.local' 'bin' }
$rtkInstallScriptUrl = 'https://raw.githubusercontent.com/rtk-ai/rtk/refs/heads/master/install.sh'
$rtkReleaseBase = 'https://github.com/rtk-ai/rtk/releases/latest/download'

function Test-PtkRtkAnswers {
    param([Parameter(Mandatory)][string]$Path)
    # A version banner only proves the image loaded. ptk depends on the
    # rewriter answering, which is what must work under emulation. This is
    # the server's own question (`rtk rewrite`): exit 0 or 3 carries the
    # rewrite (rtk 1.x answers 3 when no allow rule covers the command).
    try {
        $rewritten = & $Path rewrite 'git status --short' 2>$null
        $answered = $LASTEXITCODE -in 0, 3 -and -not [string]::IsNullOrWhiteSpace($rewritten)
        # A passing answer of 3 is not a failure for anything that reads
        # LASTEXITCODE after this probe.
        $global:LASTEXITCODE = 0
        return $answered
    }
    catch { return $false }
}

# The path of a working rtk on PATH, or $null.
# True for a path inside the bin/ payload this installer replaces wholesale.
# Installers before 2026-10-24 put rtk there; an upgrade deletes that copy
# before the installed smoke test starts the server, so relying on it rolls
# the whole install back (rr-12).
function Test-PtkPayloadRtk {
    param([Parameter(Mandatory)][string]$Path)
    $payloadBin = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath((Join-Path $ptkHome 'bin')))
    $comparison = if ($IsLinux) { [StringComparison]::Ordinal } else { [StringComparison]::OrdinalIgnoreCase }
    [IO.Path]::GetFullPath($Path).StartsWith($payloadBin + [IO.Path]::DirectorySeparatorChar, $comparison)
}

function Find-PtkRtk {
    # Every rtk on PATH in order, as the server searches, minus any inside the
    # payload: the first one that answers is the one the installed server uses.
    foreach ($candidate in @(Get-Command rtk -All -CommandType Application -ErrorAction SilentlyContinue)) {
        if (Test-PtkPayloadRtk -Path $candidate.Source) { continue }
        if (Test-PtkRtkAnswers -Path $candidate.Source) { return $candidate.Source }
    }
    $null
}

# The server takes PTK_RTK_PATH over PATH with no fallback, so a value inside
# the payload would stop the installed server the moment bin/ is replaced.
function Assert-PtkRtkPathOutsidePayload {
    if (-not $env:PTK_RTK_PATH -or -not (Test-PtkPayloadRtk -Path $env:PTK_RTK_PATH)) { return }
    throw ("PTK_RTK_PATH points at $env:PTK_RTK_PATH, inside the ptk payload ($ptkHome/bin) that " +
        'this install replaces. Unset it, or point it at an rtk outside that directory, and re-run this installer.')
}

# Asked before anything is staged, so declining leaves the machine untouched.
function Assert-PtkRtkConsent {
    if (Find-PtkRtk) { return }
    $how = if ($IsWindows) { "Download the latest release from github.com/rtk-ai/rtk into $rtkInstallDir" }
    else { "Run rtk's installer (install.sh from github.com/rtk-ai/rtk), which installs it into $rtkInstallDir" }
    $question = "rtk (required) is not on PATH. $how?"
    if (Confirm-PtkPrerequisiteInstall -Question $question) { return }
    throw ('rtk is required and was not installed. Install rtk (https://github.com/rtk-ai/rtk) ' +
        'so `rtk` is on PATH, or re-run with -InstallPrerequisites to let this installer fetch it.')
}

# The expected SHA-256 for exactly this asset: as in rtk's install.sh, the
# name must end the line, so a sibling edition (rtk-oss-...) cannot answer.
function Get-PtkRtkExpectedHash {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ChecksumLines,
        [Parameter(Mandatory)][string]$Asset
    )
    $line = $ChecksumLines |
        Where-Object { $_ -match ('\s\*?' + [regex]::Escape($Asset) + '$') } |
        Select-Object -First 1
    if (-not $line) { throw "rtk checksums.txt has no entry for $Asset." }
    ($line -split '\s+')[0].ToLowerInvariant()
}

# Windows: rtk publishes no aarch64 build, so ARM64 gets the x64 one, which
# runs under emulation and is probed like any other.
function Install-PtkRtkRelease {
    $asset = 'rtk-x86_64-pc-windows-msvc.zip'
    $staging = Join-Path ([IO.Path]::GetTempPath()) ("ptk-rtk-{0}" -f ([guid]::NewGuid()))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        $archive = Join-Path $staging $asset
        Invoke-WebRequest -UseBasicParsing -Uri "$rtkReleaseBase/$asset" -OutFile $archive
        $sums = Join-Path $staging 'checksums.txt'
        Invoke-WebRequest -UseBasicParsing -Uri "$rtkReleaseBase/checksums.txt" -OutFile $sums
        $expected = Get-PtkRtkExpectedHash -ChecksumLines @(Get-Content -LiteralPath $sums) -Asset $asset
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual) { throw "Checksum mismatch for $asset; refusing to install it." }

        $extracted = Join-Path $staging 'x'
        Expand-Archive -LiteralPath $archive -DestinationPath $extracted -Force
        $found = Get-ChildItem -LiteralPath $extracted -Filter 'rtk.exe' -Recurse -File |
            Select-Object -First 1
        if (-not $found) { throw 'rtk archive did not contain rtk.exe.' }

        New-Item -ItemType Directory -Path $rtkInstallDir -Force | Out-Null
        $target = Join-Path $rtkInstallDir 'rtk.exe'
        Copy-Item -LiteralPath $found.FullName -Destination $target -Force
        $target
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Linux and macOS: rtk installs itself. install.sh reads RTK_INSTALL_DIR from
# the environment this child inherits.
function Install-PtkRtkWithScript {
    $script = Join-Path ([IO.Path]::GetTempPath()) ("ptk-rtk-install-{0}.sh" -f ([guid]::NewGuid()))
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $rtkInstallScriptUrl -OutFile $script
        sh $script | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "rtk's installer failed with exit code $LASTEXITCODE." }
    }
    finally {
        Remove-Item -LiteralPath $script -Force -ErrorAction SilentlyContinue
    }
    Join-Path $rtkInstallDir 'rtk'
}

# This process inherited PATH when it started; a directory the user has since
# added to their Windows PATH is picked up here, so the smoke tests see it.
function Update-PtkProcessPath {
    if (-not $IsWindows) { return }
    $known = $env:PATH -split ';'
    $added = foreach ($scope in 'User', 'Machine') {
        [Environment]::GetEnvironmentVariable('PATH', $scope) -split ';' |
            Where-Object { $_ -and $_ -notin $known }
    }
    if ($added) { $env:PATH = (@($env:PATH) + $added) -join ';' }
}

function Get-PtkRtkPathAdvice {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [bool]$OnWindows
    )
    $how = if ($OnWindows) {
        "Add it to your user PATH, for example:`n" +
            "  [Environment]::SetEnvironmentVariable('PATH', '$Directory;' + " +
            "[Environment]::GetEnvironmentVariable('PATH', 'User'), 'User')`n" +
            'then open a new terminal'
    }
    else {
        "Add it to your shell profile, as rtk's installer advises:`n" +
            "  export PATH=`"${Directory}:`$PATH`"`n" +
            'then open a new shell'
    }
    ("rtk was installed to $Directory, but that directory is not on PATH, and ptk " +
        "finds rtk only through PATH or PTK_RTK_PATH. $how and re-run this installer.")
}

# Runs before staging: the smoke tests start the packaged server, which
# refuses to start without rtk.
function Install-PtkRtk {
    param([Parameter(Mandatory)][string]$TargetRid)

    $existing = Find-PtkRtk
    if ($existing) {
        Write-Host "rtk found on PATH: $existing"
        return
    }

    $stale = @(Get-Command rtk -All -CommandType Application -ErrorAction SilentlyContinue |
        Where-Object { Test-PtkPayloadRtk -Path $_.Source })
    if ($stale) {
        Write-Host "Ignoring $($stale[0].Source): it sits inside the ptk payload this install replaces."
    }
    Write-Host "rtk not found; installing it into $rtkInstallDir (required dependency)."
    $placed = if ($IsWindows) { Install-PtkRtkRelease } else { Install-PtkRtkWithScript }
    if (-not (Test-Path -LiteralPath $placed -PathType Leaf)) {
        throw "rtk's installer finished, but $placed does not exist."
    }
    # Recorded before the checks below, so a re-run after the user fixes PATH
    # still knows which copy uninstall may remove. The hash keeps uninstall
    # from removing a later rtk the user put at the same path themselves.
    New-Item -ItemType Directory -Path $ptkHome -Force | Out-Null
    $hash = (Get-FileHash -LiteralPath $placed -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $ptkHome $rtkMarkerName) -Value "$placed`n$hash" -NoNewline

    if (-not (Test-PtkRtkAnswers -Path $placed)) {
        $hint = if ($TargetRid -eq 'win-arm64') {
            ' On Windows ARM64 the x64 rtk runs under emulation; check that x64 emulation is available.'
        }
        else { '' }
        throw ("The installed rtk did not answer 'rtk rewrite'.$hint " +
            'ptk would refuse to start, so this install is being stopped.')
    }
    Update-PtkProcessPath
    if (-not (Find-PtkRtk)) {
        throw (Get-PtkRtkPathAdvice -Directory (Split-Path -Parent $placed) -OnWindows $IsWindows)
    }
    Write-Host "rtk installed: $placed"
}

function Remove-PtkInstalledRtk {
    $marker = Join-Path $ptkHome $rtkMarkerName
    if (-not (Test-Path -LiteralPath $marker)) { return }
    # The path, then (since 2026-10-24) its SHA-256; older markers name a
    # copy inside ~/.ptk/bin and carry no hash.
    $ours, $hash = @((Get-Content -LiteralPath $marker -Raw).Trim() -split '\r?\n')
    if ($ours -and (Test-Path -LiteralPath $ours)) {
        $current = (Get-FileHash -LiteralPath $ours -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -and $current -cne $hash) {
            Write-Host "Kept ${ours}: it is no longer the rtk this installer placed."
        }
        else {
            Remove-Item -LiteralPath $ours -Force
            Write-Host "Removed the rtk this installer placed: $ours"
        }
    }
    Remove-Item -LiteralPath $marker -Force
}

# Stamps the built version into the packaged module manifest so every
# user-visible surface agrees. ModuleVersion is a System.Version and cannot
# hold a prerelease label, so a version like 0.2.0-rc.1 splits: 0.2.0 into
# ModuleVersion and rc.1 into PrivateData.PSData.Prerelease, which is where
# PowerShell itself expects it.
function Set-PtkManifestVersion {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$PayloadVersion
    )
    $numeric, $prerelease = $PayloadVersion -split '-', 2
    # Build metadata (+sha) is not part of a module version at all.
    $numeric = ($numeric -split '\+', 2)[0]
    if ($numeric -notmatch '^\d+(\.\d+){1,3}$') {
        throw "Cannot stamp module manifest: '$PayloadVersion' has no usable numeric version."
    }

    $text = Get-Content -LiteralPath $ManifestPath -Raw
    $pattern = [regex]"(?m)^(\s*ModuleVersion\s*=\s*')[^']*(')"
    # Check the match, not whether the text changed: stamping the version the
    # manifest already carries is a no-op replacement, not a failure.
    if (-not $pattern.IsMatch($text)) {
        throw "Cannot stamp module manifest: no ModuleVersion assignment found in $ManifestPath."
    }
    $updated = $pattern.Replace($text, "`${1}$numeric`${2}", 1)

    if ($prerelease) {
        # PSData.Prerelease may not contain a leading hyphen or dots in the
        # PowerShell gallery sense; normalize rc.1 to rc1.
        $tag = ($prerelease -replace '[^A-Za-z0-9]', '')
        $updated = $updated.TrimEnd() -replace '\}\s*$', @"
    PrivateData       = @{
        PSData = @{
            Prerelease = '$tag'
        }
    }
}
"@
    }
    Set-Content -LiteralPath $ManifestPath -Value $updated -NoNewline

    $check = Import-PowerShellDataFile -LiteralPath $ManifestPath
    if ($check.ModuleVersion -ne $numeric) {
        throw "Module manifest stamp failed: expected $numeric, manifest reports $($check.ModuleVersion)."
    }
}

# Publishes the runtime server and assembles the canonical layout (bin/, src/,
# scripts/, VERSION) in $Destination. Legacy audit administration remains a
# separate source project and is not part of the installed runtime payload.
# The one layout generator dev installs and release CI share.
function New-PtkLayout {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$TargetRid,
        [Parameter(Mandatory)][string]$PayloadVersion
    )
    $provenance = New-PtkBuildProvenance `
        -Product 'ptk' `
        -ProductVersion $PayloadVersion `
        -TargetRid $TargetRid `
        -SourceRoot $repoRoot
    $sourceRevision = if ($provenance.source_commit -ceq 'unknown') {
        'unknown'
    }
    else {
        $provenance.source_commit.Substring(0, 7)
    }
    Write-Host (
        "Publishing PtkMcpServer ($TargetRid, $PayloadVersion, build $($provenance.build_identity))...")
    dotnet publish (Join-Path $repoRoot 'server' 'PtkMcpServer') `
        -c Release -r $TargetRid --self-contained `
        -p:Version=$PayloadVersion `
        -p:PtkSourceCommit=$sourceRevision `
        -p:PtkBuildIdentity=$($provenance.build_identity) `
        -o (Join-Path $Destination 'bin') -v q --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    # The same native apphost exposes operator verbs as `ptk`; retain the
    # canonical name for existing registrations and worker process discovery.
    # Copy before release signing so both Windows/macOS images are signed.
    $apphostName = $TargetRid.StartsWith('win-') ? 'PtkMcpServer.exe' : 'PtkMcpServer'
    $cliName = $TargetRid.StartsWith('win-') ? 'ptk.exe' : 'ptk'
    Copy-Item -LiteralPath (Join-Path $Destination 'bin' $apphostName) `
        -Destination (Join-Path $Destination 'bin' $cliName)

    # The redirect decision needs neither a PowerShell host nor the server's
    # runtime. Publish separately so its intermediate files cannot overwrite
    # the MCP payload; copy only the standalone native executable.
    $hookPublish = Join-Path ([IO.Path]::GetTempPath()) ("ptk-hook-publish-{0}" -f [guid]::NewGuid())
    try {
        dotnet publish (Join-Path $repoRoot 'server' 'PtkHook') `
            -c Release -r $TargetRid -o $hookPublish -v q --nologo | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Native redirect hook publish failed.' }
        $hookName = $TargetRid.StartsWith('win-') ? 'ptk-hook.exe' : 'ptk-hook'
        Copy-Item -LiteralPath (Join-Path $hookPublish $hookName) -Destination (Join-Path $Destination 'bin' $hookName)
    }
    finally {
        Remove-Item -LiteralPath $hookPublish -Recurse -Force -ErrorAction SilentlyContinue
    }

    $src = New-Item -ItemType Directory -Path (Join-Path $Destination 'src') -Force
    foreach ($f in 'PwshTokenCompressor.psd1', 'PwshTokenCompressor.psm1') {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'src' $f) -Destination $src.FullName
    }
    Set-PtkManifestVersion `
        -ManifestPath (Join-Path $src.FullName 'PwshTokenCompressor.psd1') `
        -PayloadVersion $PayloadVersion
    $scripts = New-Item -ItemType Directory -Path (Join-Path $Destination 'scripts') -Force
    foreach ($f in 'ptk_init.ps1', 'ptk-audit-destination.ps1', 'install.ps1',
        'ptk_install_transaction.psm1', 'ptk_build_provenance.psm1') {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts' $f) -Destination $scripts.FullName
    }
    # The installed installer validates the next -FromRelease payload with
    # this smoke test before activating it (Resolve-PtkHandshakeScript).
    Copy-Item -LiteralPath (Join-Path $repoRoot 'server' 'test-handshake.ps1') -Destination $scripts.FullName
    # Apache-2.0 requires the licence to travel with the distribution, and a
    # user inspecting an installed payload should find it without the repo.
    foreach ($f in 'LICENSE', 'README.md') {
        Copy-Item -LiteralPath (Join-Path $repoRoot $f) -Destination $Destination
    }
    Set-Content -LiteralPath (Join-Path $Destination 'VERSION') -Value $PayloadVersion -NoNewline
    Write-PtkBuildProvenance `
        -Provenance $provenance `
        -Path (Join-Path $Destination 'BUILD-PROVENANCE.json')
}

function Remove-PtkPayload {
    foreach ($entry in $payloadEntries) {
        $target = Join-Path $ptkHome $entry
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
            Write-Host "Removed $target"
        }
    }
    # Drop the home itself only when nothing user-owned remains. -Force:
    # dot-named directories carry the Hidden attribute on Unix, which
    # Remove-Item refuses without it.
    if ((Test-Path -LiteralPath $ptkHome) -and
        -not (Get-ChildItem -LiteralPath $ptkHome -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $ptkHome -Force
        Write-Host "Removed empty $ptkHome"
    }
}

# Claude registration moved into ptk_init's claude leg (per-harness consent
# work): one consent covers registration, hook, and nudge, and a leg failure
# fails the init child, which fails this installer into the same rollback a
# Register-PtkServer throw used to trigger.

function Write-PtkArpEntry {
    param([Parameter(Mandatory)][string]$PayloadVersion)
    if (-not $IsWindows) { return }
    # The per-user Add/Remove Programs entry winget's upgrade/uninstall
    # tracking keys off (plan: winget-ready from v0.2.0).
    New-Item -Path $arpKeyPath -Force | Out-Null
    Set-ItemProperty -Path $arpKeyPath -Name DisplayName -Value 'PowerShell Token Killer (ptk)'
    Set-ItemProperty -Path $arpKeyPath -Name DisplayVersion -Value $PayloadVersion
    Set-ItemProperty -Path $arpKeyPath -Name Publisher -Value 'PowerShell-Token-Killer'
    Set-ItemProperty -Path $arpKeyPath -Name InstallLocation -Value $ptkHome
    Set-ItemProperty -Path $arpKeyPath -Name UninstallString -Value (
        'pwsh -NoProfile -File "{0}" -Uninstall' -f (Join-Path $ptkHome 'scripts' 'install.ps1'))
    Set-ItemProperty -Path $arpKeyPath -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $arpKeyPath -Name NoRepair -Value 1 -Type DWord
    Write-Host 'Wrote Add/Remove Programs entry (HKCU).'
}

function Remove-PtkArpEntry {
    if (-not $IsWindows) { return }
    if (Test-Path -Path $arpKeyPath) {
        Remove-Item -Path $arpKeyPath -Recurse -Force
        Write-Host 'Removed Add/Remove Programs entry.'
    }
}

function Get-PtkArpState {
    if (-not $IsWindows) {
        return [pscustomobject]@{ Exists = $false; Values = @() }
    }
    if (-not (Test-Path -Path $arpKeyPath)) {
        return [pscustomobject]@{ Exists = $false; Values = @() }
    }

    $key = Get-Item -Path $arpKeyPath
    $values = @($key.GetValueNames() | Sort-Object | ForEach-Object {
            [pscustomobject]@{
                Name = $_
                Kind = $key.GetValueKind($_).ToString()
                Value = $key.GetValue(
                    $_,
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            }
        })
    [pscustomobject]@{ Exists = $true; Values = $values }
}

function Restore-PtkArpState {
    param([Parameter(Mandatory)]$State)
    if (-not $IsWindows) { return }

    Remove-PtkArpEntry
    if (-not $State.Exists) { return }

    New-Item -Path $arpKeyPath -Force | Out-Null
    foreach ($value in @($State.Values)) {
        $propertyType = switch ($value.Kind) {
            'String' { 'String' }
            'ExpandString' { 'ExpandString' }
            'Binary' { 'Binary' }
            'DWord' { 'DWord' }
            'MultiString' { 'MultiString' }
            'QWord' { 'QWord' }
            default { throw "Unsupported prior ARP registry value kind: $($value.Kind)" }
        }
        New-ItemProperty `
            -Path $arpKeyPath `
            -Name $value.Name `
            -Value $value.Value `
            -PropertyType $propertyType `
            -Force |
            Out-Null
    }
}

function Assert-PtkArpStateRestored {
    param([Parameter(Mandatory)]$Expected)
    if (-not $IsWindows) { return }

    $actualJson = Get-PtkArpState | ConvertTo-Json -Depth 6 -Compress
    $expectedJson = $Expected | ConvertTo-Json -Depth 6 -Compress
    if ($actualJson -cne $expectedJson) {
        throw 'The Add/Remove Programs entry was not restored exactly.'
    }
}

function Get-PtkRegistrationPaths {
    # Kimi's data root moves with KIMI_CODE_HOME (same rule as ptk_init).
    $kimiHome = [string]$env:KIMI_CODE_HOME ? $env:KIMI_CODE_HOME : (Join-Path $HOME '.kimi-code')
    @(
        (Join-Path $HOME '.claude.json')
        (Join-Path $HOME '.claude' 'settings.json')
        (Join-Path $HOME '.claude' 'CLAUDE.md')
        (Join-Path $HOME '.codex' 'config.toml')
        (Join-Path $HOME '.codex' 'hooks.json')
        (Join-Path $HOME '.codex' 'AGENTS.md')
        (Join-Path $HOME '.grok' 'config.toml')
        (Join-Path $HOME '.gemini' 'config' 'mcp_config.json')
        (Join-Path $HOME '.gemini' 'config' 'plugins' 'ptk')
        (Join-Path $kimiHome 'mcp.json')
        (Join-Path $kimiHome 'config.toml')
        (Join-Path $kimiHome 'AGENTS.md')
    )
}

# The smoke test ships beside this installer in ptk-installer.zip and in an
# installed payload's scripts/; a checkout keeps it under server/. Looking
# only in the checkout location broke every -FromRelease install run from
# the public bootstrap or from ~/.ptk/scripts (found publishing 0.3.0-rc.4).
function Resolve-PtkHandshakeScript {
    param(
        [Parameter(Mandatory)][string]$InstallerRoot,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )
    $candidates = @(
        (Join-Path $InstallerRoot 'test-handshake.ps1'),
        (Join-Path $RepositoryRoot 'server' 'test-handshake.ps1'))
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "Package smoke script is unavailable: $($candidates -join ' or ')"
}

function Invoke-PtkPackageSmoke {
    param([Parameter(Mandatory)][string]$BinaryPath)

    $handshake = Resolve-PtkHandshakeScript -InstallerRoot $PSScriptRoot -RepositoryRoot $repoRoot
    # Say what this is before it happens. The smoke test starts local worker
    # processes and opens named test sessions; unannounced, that reads like an
    # installer connecting to something.
    Write-Host "Validating staged PTK package: $BinaryPath"
    Write-Host ('  Local smoke test: starts the packaged server and two throwaway ' +
        'PowerShell worker processes to prove session isolation. No network, no ' +
        'external services.')
    # Tee rather than pipe straight to Out-Host: on failure the child's
    # diagnosis (the HANDSHAKE FAILED: line, server stderr) must travel in
    # the thrown error. A -FromRelease rollback removes the staged tree, so
    # an error that names only the binary path leaves nothing to re-test by
    # hand (GitHub #43, secondary).
    $handshakeOutput = & ([Environment]::ProcessPath) -NoProfile -File $handshake `
        -ServerCommand $BinaryPath `
        -TimeoutSec 90 2>&1 |
        ForEach-Object { $_ | Out-Host; $_ }
    if ($LASTEXITCODE -ne 0) {
        $tail = @($handshakeOutput |
            ForEach-Object { "$_" } |
            Select-Object -Last 25) -join "`n"
        throw "Package handshake failed for ${BinaryPath}:`n$tail"
    }
}

function Invoke-PtkHarnessInitialization {
    param(
        [Parameter(Mandatory)][string]$InitScript,
        [string[]]$Arguments = @()
    )

    # ptk_init.ps1 deliberately exits nonzero when any harness leg fails. Run
    # it as a child so that exit cannot terminate this installer before the
    # transaction restores the previous payload and registrations.
    # No `| Out-Host` on this call (hcc-7): piping a native child makes
    # pwsh assemble its stdout line-by-line, and ptk_init's consent prompt
    # is a PARTIAL line - it would reach the terminal only after the user
    # answered blind. Direct invocation lets the child inherit the console.
    & ([Environment]::ProcessPath) -NoProfile -File $InitScript @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Per-harness initialization failed with exit code $LASTEXITCODE."
    }
}

# True only when a REAL ptk hook entry exists: a marker-matched command
# inside hooks.PreToolUse. A raw text match on the whole settings file would
# treat 'ptk-hook.ps1' anywhere (permissions lists, other hook events) as
# hook consent (i2-1).
function Test-PtkHookEntryPresent {
    param([Parameter(Mandatory)][string]$SettingsPath)
    if (-not (Test-Path -LiteralPath $SettingsPath)) { return $false }
    $raw = Get-Content -LiteralPath $SettingsPath -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) { return $false }
    try { $config = $raw | ConvertFrom-Json -AsHashtable } catch { return $false }
    if ($null -eq $config -or -not $config.ContainsKey('hooks') -or
        -not $config['hooks'].ContainsKey('PreToolUse')) { return $false }
    foreach ($entry in @($config['hooks']['PreToolUse'])) {
        if ($null -eq $entry) { continue }
        foreach ($hook in @($entry['hooks'])) {
            if ($null -ne $hook -and [string]$hook['command'] -match
                '(?:^|[/\\\s"''])ptk-hook(?:\.ps1|\.exe)?(?=$|[\s"''])') { return $true }
        }
    }
    $false
}

function Show-PtkCodexSnippet {
    param([Parameter(Mandatory)][string]$BinaryPath)
    Write-Host ''
    Write-Host 'Codex (~/.codex/config.toml):'
    Write-Host '  [mcp_servers.ptk]'
    # TOML basic string with explicit escaping: literal (single-quoted)
    # strings cannot hold an apostrophe at all, and unescaped Windows
    # backslashes are illegal escape sequences in a basic string.
    $escaped = $BinaryPath.Replace('\', '\\').Replace('"', '\"')
    Write-Host ('  command = "{0}"' -f $escaped)
}

$mode = $PSCmdlet.ParameterSetName
# Parameter-set membership alone would let an explicit -Uninstall:$false run
# a full (destructive) uninstall; honor the switch's VALUE. -LayoutOnly:$false
# with -OutputDir has no coherent meaning - refuse rather than guess.
if ($mode -eq 'Uninstall' -and -not $Uninstall) { $mode = 'Install' }
if ($mode -eq 'LayoutOnly' -and -not $LayoutOnly) {
    throw '-LayoutOnly:$false with -OutputDir is ambiguous; pass -LayoutOnly or drop -OutputDir.'
}

switch ($mode) {
    'LayoutOnly' {
        $targetRid = if ($Rid) { $Rid } else { Get-PtkRid }
        $payloadVersion = Get-PtkVersion
        Assert-PtkNativeBuildRid -TargetRid $targetRid
        if ((Test-Path -LiteralPath $OutputDir) -and
            (Get-ChildItem -LiteralPath $OutputDir -Force | Select-Object -First 1)) {
            throw "OutputDir '$OutputDir' is not empty - refusing to clobber."
        }
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
        New-PtkLayout -Destination $OutputDir -TargetRid $targetRid -PayloadVersion $payloadVersion
        Assert-PtkPayloadIntact -Root $OutputDir -TargetRid $targetRid
        if ($Validate) {
            $localRid = Get-PtkRid
            if ($targetRid -cne $localRid) {
                throw "Cannot execute $targetRid layout validation on $localRid."
            }
            Invoke-PtkPackageSmoke -BinaryPath (
                Join-Path $OutputDir 'bin' (
                    Get-PtkServerBinaryName -TargetRid $targetRid))
        }
        Write-Host "Layout ready: $OutputDir ($targetRid, $payloadVersion)"
    }
    'Uninstall' {
        Assert-NotElevated
    Assert-PtkRuntimeNotRunning
        # Per-agent init reversal first (needs a ptk_init.ps1), then ARP and
        # payload. ptk_init -Uninstall reverses every SUPPORTED leg - not
        # just detected ones (mhi-10) - (claude registration, hook, and
        # guidance; codex/grok/kimi registrations; the agy plugin) and no-ops
        # safely where nothing is installed.
        $init = Join-Path $ptkHome 'scripts' 'ptk_init.ps1'
        if (-not (Test-Path -LiteralPath $init)) { $init = Join-Path $PSScriptRoot 'ptk_init.ps1' }
        if (Test-Path -LiteralPath $init) {
            try {
                Invoke-PtkHarnessInitialization `
                    -InitScript $init `
                    -Arguments '-Uninstall'
            }
            catch { Write-Warning "Per-agent uninstall failed (continuing): $_" }
        }
        elseif (Test-PtkHookEntryPresent -SettingsPath (Join-Path $HOME '.claude' 'settings.json')) {
            Write-Warning ('A ptk hook entry exists in the user settings but no ptk_init.ps1 was ' +
                'found; run ptk_init.ps1 -Uninstall from a checkout to remove it.')
        }
        Remove-PtkArpEntry
        Remove-PtkInstalledRtk
        Remove-PtkPayload
        if ($Purge -and (Test-Path -LiteralPath $ptkHome)) {
            Remove-Item -LiteralPath $ptkHome -Recurse -Force
            Write-Host "ptk uninstalled and $ptkHome purged."
        }
        else {
            Write-Host 'ptk uninstalled. User-owned files under ~/.ptk (if any) were kept.'
        }
    }
    default {
        Assert-NotElevated
Assert-PtkRuntimeNotRunning
        # Before touching anything: an install already nested by the pre-fix
        # activation bug cannot be repaired by installing over it (#42).
        Assert-PtkPayloadNotNested -Root $ptkHome
        $targetRid = Get-PtkRid
        $payloadVersion = Get-PtkVersion
        # Checks that need no answer run first, then every prerequisite
        # question, all before staging, so a refusal or decline leaves the
        # machine untouched. rtk is installed before staging because the
        # smoke tests start the server, which refuses to start without it.
        if (-not $FromRelease) { Assert-PtkNativeToolchain -TargetRid $targetRid }
        Assert-PtkRtkPathOutsidePayload
        Assert-PtkRtkConsent
        if (-not $FromRelease) { Assert-PtkDotnetSdk }
        Install-PtkRtk -TargetRid $targetRid
        $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-stage-{0}" -f ([guid]::NewGuid()))
        $snapshot = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-rollback-{0}" -f ([guid]::NewGuid()))
        New-Item -ItemType Directory -Path $staging | Out-Null
        try {
            if ($FromRelease) {
                $payloadVersion = Get-PtkReleaseLayout `
                    -Destination $staging -TargetRid $targetRid -ReleaseVersion $Version
            }
            else {
                New-PtkLayout -Destination $staging -TargetRid $targetRid -PayloadVersion $payloadVersion
            }
            Assert-PtkPayloadIntact -Root $staging -TargetRid $targetRid
            # Activation MOVES the staged entries, so the staged tree is gone
            # by the time InstalledValidation runs. Record what we built while
            # it still exists, and compare the install against that (#42).
            $stagedIndex = Get-PtkPayloadIndex -Root $staging
            Invoke-PtkInstallTransaction `
                -StagingRoot $staging `
                -PayloadRoot $ptkHome `
                -PayloadEntries $payloadEntries `
                -RegistrationPaths (Get-PtkRegistrationPaths) `
                -SnapshotRoot $snapshot `
                -CaptureExternalState { Get-PtkArpState } `
                -RestoreExternalState {
                    param($state)
                    Restore-PtkArpState -State $state
                } `
                -AssertExternalStateRestored {
                    param($state)
                    Assert-PtkArpStateRestored -Expected $state
                } `
                -StagedValidation {
                    param($stagedRoot)
                    Invoke-PtkPackageSmoke -BinaryPath (
                        Join-Path $stagedRoot 'bin' (
                            Get-PtkServerBinaryName -TargetRid $targetRid))
                } `
                -InstalledValidation {
                    param($installedRoot)
                    $installedBinary = Join-Path $installedRoot 'bin' (
                        Get-PtkServerBinaryName -TargetRid $targetRid)
                    Assert-PtkPayloadIntact -Root $installedRoot -TargetRid $targetRid
                    Assert-PtkPayloadComplete `
                        -StagedIndex $stagedIndex -InstalledRoot $installedRoot
                    Invoke-PtkPackageSmoke -BinaryPath $installedBinary
                } `
                -RegistrationCutover {
                    if ($Hook) {
                        Write-Host 'NOTE: -Hook is deprecated - the full per-agent init runs by default.'
                    }
                    # Registration, hooks, and guidance for every consented
                    # harness - ptk_init asks once (pacman-style) unless a
                    # consent flag pre-answers; a failing leg fails this
                    # install into rollback.
                    $initArgs = @()
                    if ($Agent) { $initArgs += @('-Agent', ($Agent -join ',')) }
                    if ($SkipAgent) { $initArgs += @('-SkipAgent', ($SkipAgent -join ',')) }
                    if ($AllAgents) { $initArgs += '-AllAgents' }
                    Invoke-PtkHarnessInitialization -InitScript (
                        Join-Path $ptkHome 'scripts' 'ptk_init.ps1') -Arguments $initArgs
                    Write-PtkArpEntry -PayloadVersion $payloadVersion
                }
        }
        finally {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        }
        $binaryPath = Join-Path $ptkHome 'bin' (Get-PtkServerBinaryName -TargetRid $targetRid)
        Show-PtkCodexSnippet -BinaryPath $binaryPath
        Write-Host ''
        Write-Host "Installed ptk $payloadVersion to $ptkHome ($targetRid)."
    }
}
