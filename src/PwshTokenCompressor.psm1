Microsoft.PowerShell.Core\Set-StrictMode -Version Latest

$script:DefaultMaxItems = 40
$script:DefaultWidth = 140
$script:PtcTableMaxColumnWidth = 50
$script:PtcPassthroughMaxLines = 400
$script:PtcPassthroughMaxChars = 40KB

function Remove-PtcAnsi {
    param([AllowNull()][string]$Text)
    if ($null -eq $Text) { return '' }
    return $Text -replace "`e\[[0-9;?]*[ -/]*[@-~]", ''
}

function Limit-PtcText {
    param(
        [AllowNull()][string]$Text,
        [int]$MaxLines = 80,
        [int]$Width = $script:DefaultWidth
    )

    $clean = Remove-PtcAnsi $Text
    $lines = @($clean -split "`r?`n")
    $shown = foreach ($line in $lines | Microsoft.PowerShell.Utility\Select-Object -First $MaxLines) {
        if ($line.Length -gt $Width) {
            $line.Substring(0, [Math]::Max(0, $Width - 1)) + '...'
        } else {
            $line
        }
    }

    if ($lines.Count -gt $MaxLines) {
        $shown += "[{0} more lines]" -f ($lines.Count - $MaxLines)
    }

    $shown -join [Environment]::NewLine
}

function Format-PtcSize {
    param([Nullable[long]]$Bytes)
    if ($null -eq $Bytes) { return '' }
    if ($Bytes -ge 1GB) { return '{0:n1}G' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:n1}M' -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return '{0:n1}K' -f ($Bytes / 1KB) }
    return '{0}B' -f $Bytes
}

function Join-PtcLines {
    param([string[]]$Lines)
    ($Lines | Microsoft.PowerShell.Core\Where-Object { $_ -ne $null }) -join [Environment]::NewLine
}

function Get-PtcDisplayProperties {
    param(
        [object]$Object,
        [int]$MaxProperties = 6
    )

    $preferred = @(
        'Name', 'DisplayName', 'Status', 'State', 'Id', 'ProcessName',
        'Path', 'FullName', 'LineNumber', 'Length', 'LastWriteTime',
        'CommandType', 'Version', 'Source'
    )

    $names = @($Object.PSObject.Properties |
        Microsoft.PowerShell.Core\Where-Object { $_.MemberType -in 'NoteProperty', 'Property', 'AliasProperty' } |
        Microsoft.PowerShell.Utility\Select-Object -ExpandProperty Name)

    $ordered = @()
    foreach ($name in $preferred) {
        if ($names -contains $name) { $ordered += $name }
    }
    foreach ($name in $names) {
        if ($ordered -notcontains $name) { $ordered += $name }
    }

    $ordered | Microsoft.PowerShell.Utility\Select-Object -First $MaxProperties
}

function ConvertTo-PtcScalar {
    param([AllowNull()][object]$Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [datetime]) { return $Value.ToString('yyyy-MM-dd HH:mm:ss') }
    if ($Value -is [bool]) { return $Value.ToString().ToLowerInvariant() }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        return '[{0}]' -f @($Value).Count
    }
    Limit-PtcText -Text ([string]$Value) -MaxLines 1 -Width 80
}

function Get-PtcPropertyValue {
    param(
        [object]$Object,
        [string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    $property.Value
}

function Test-PtcHasProperty {
    param(
        [object]$Object,
        [string[]]$Name
    )

    foreach ($candidate in $Name) {
        if ($null -eq $Object.PSObject.Properties[$candidate]) { return $false }
    }
    $true
}

# Index-based head slice. Never use Select-Object -First on object rows in
# shaping code: it stamps 'Selected.*' into the live TypeNames of
# PSObject-wrapped items (PSCustomObject, any Deserialized.* from remoting
# or Import-Clixml), and the mutation persists on the caller's objects
# across warm-session calls (i1-2).
function Select-PtcFirst {
    param(
        [object[]]$Items,
        [int]$Count
    )
    $all = @($Items)
    if ($all.Count -eq 0 -or $Count -le 0) { return @() }
    @($all[0..([Math]::Min($Count, $all.Count) - 1)])
}

function Format-PtcTable {
    param(
        [object[]]$Rows,
        [string[]]$Properties,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    if ($Rows.Count -eq 0) { return @() }
    # Slice by index, never Select-Object: piping PSObject-wrapped rows (any
    # PSCustomObject) through Select-Object -First stamps 'Selected.*' into
    # their LIVE TypeNames, and the mutation persists on the caller's
    # objects across warm-session calls.
    # A non-positive take must yield ZERO rows ($Rows[0..-1] wraps around to
    # first+last), matching Select-Object -First 0 semantics (i1-3).
    $take = [Math]::Min($MaxItems, $Rows.Count)
    $sliced = ($take -gt 0) ? @($Rows[0..($take - 1)]) : @()
    if ($Properties.Count -eq 0) {
        $lines = @($sliced | Microsoft.PowerShell.Core\ForEach-Object { [string]$_ })
        if ($Rows.Count -gt $MaxItems) { $lines += '+{0} more' -f ($Rows.Count - $MaxItems) }
        return $lines
    }

    $visible = $sliced
    $widths = @{}
    foreach ($prop in $Properties) {
        $max = $prop.Length
        foreach ($row in $visible) {
            $value = ConvertTo-PtcScalar (Get-PtcPropertyValue -Object $row -Name $prop)
            if ($value.Length -gt $max) { $max = [Math]::Min($value.Length, $script:PtcTableMaxColumnWidth) }
        }
        $widths[$prop] = $max
    }

    $lines = @()
    $header = ($Properties | Microsoft.PowerShell.Core\ForEach-Object { $_.PadRight($widths[$_]) }) -join '  '
    $lines += $header.TrimEnd()
    $lines += (($Properties | Microsoft.PowerShell.Core\ForEach-Object { ('-' * $widths[$_]) }) -join '  ')

    foreach ($row in $visible) {
        $cells = foreach ($prop in $Properties) {
            $value = ConvertTo-PtcScalar (Get-PtcPropertyValue -Object $row -Name $prop)
            if ($value.Length -gt $script:PtcTableMaxColumnWidth) { $value = $value.Substring(0, $script:PtcTableMaxColumnWidth - 1) + '...' }
            $value.PadRight($widths[$prop])
        }
        $lines += (($cells -join '  ').TrimEnd())
    }

    if ($Rows.Count -gt $MaxItems) {
        $lines += '+{0} more' -f ($Rows.Count - $MaxItems)
    }

    $lines
}

function Compress-PtcFileSystem {
    param(
        [object[]]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    $items = @($InputObject)
    $dirs = @($items | Microsoft.PowerShell.Core\Where-Object { [bool](Get-PtcPropertyValue -Object $_ -Name 'PSIsContainer') })
    $files = @($items | Microsoft.PowerShell.Core\Where-Object { -not [bool](Get-PtcPropertyValue -Object $_ -Name 'PSIsContainer') })
    $totalBytes = ($files | Microsoft.PowerShell.Core\ForEach-Object { [long](Get-PtcPropertyValue -Object $_ -Name 'Length') } | Microsoft.PowerShell.Utility\Measure-Object -Sum).Sum
    if ($null -eq $totalBytes) { $totalBytes = 0 }

    $lines = @("fs: {0} dirs, {1} files, {2}" -f $dirs.Count, $files.Count, (Format-PtcSize $totalBytes))

    $dirRows = @((Select-PtcFirst @($dirs | Microsoft.PowerShell.Utility\Sort-Object Name) $MaxItems) | Microsoft.PowerShell.Core\ForEach-Object {
        [pscustomobject]@{
            Type = 'dir'
            Name = $_.Name + '\'
            Modified = $_.LastWriteTime
        }
    })

    $fileRows = @((Select-PtcFirst @($files | Microsoft.PowerShell.Utility\Sort-Object Name) $MaxItems) | Microsoft.PowerShell.Core\ForEach-Object {
        [pscustomobject]@{
            Type = 'file'
            Name = $_.Name
            Size = Format-PtcSize (Get-PtcPropertyValue -Object $_ -Name 'Length')
            Modified = $_.LastWriteTime
        }
    })

    $rows = @($dirRows + $fileRows)
    if ($rows.Count -gt 0) {
        $lines += Format-PtcTable -Rows $rows -Properties @('Type', 'Name', 'Size', 'Modified') -MaxItems $MaxItems
    }

    $omitted = $items.Count - $rows.Count
    if ($omitted -gt 0) { $lines += '+{0} more filesystem items' -f $omitted }
    Join-PtcLines $lines
}

function Compress-PtcMatchInfo {
    param(
        [object[]]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    $matches = @($InputObject)
    $groups = @($matches | Microsoft.PowerShell.Utility\Group-Object Path | Microsoft.PowerShell.Utility\Sort-Object Name)
    $lines = @('{0} matches in {1} files' -f $matches.Count, $groups.Count)

    foreach ($group in (Select-PtcFirst $groups $MaxItems)) {
        $lines += ''
        $lines += '[file] {0} ({1})' -f $group.Name, $group.Count
        foreach ($match in (Select-PtcFirst @($group.Group) 8)) {
            $text = Limit-PtcText -Text ([string]$match.Line).Trim() -MaxLines 1 -Width 110
            $lines += '  {0,5}: {1}' -f $match.LineNumber, $text
        }
        if ($group.Count -gt 8) { $lines += '  +{0}' -f ($group.Count - 8) }
    }

    if ($groups.Count -gt $MaxItems) {
        $lines += ''
        $lines += '+{0} more files' -f ($groups.Count - $MaxItems)
    }

    Join-PtcLines $lines
}

function Compress-PtcProcess {
    param(
        [object[]]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    $items = @($InputObject)
    $rows = @((Select-PtcFirst @($items | Microsoft.PowerShell.Utility\Sort-Object CPU -Descending) $MaxItems) | Microsoft.PowerShell.Core\ForEach-Object {
        [pscustomobject]@{
            ProcessName = $_.ProcessName
            Id = $_.Id
            CPU = if ($null -eq $_.CPU) { '' } else { '{0:n1}' -f $_.CPU }
            WS = Format-PtcSize $_.WorkingSet64
        }
    })

    Join-PtcLines (@("processes: {0}" -f $items.Count) + (Format-PtcTable -Rows $rows -Properties @('ProcessName', 'Id', 'CPU', 'WS') -MaxItems $MaxItems))
}

function Compress-PtcService {
    param(
        [object[]]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    $items = @($InputObject)
    $status = @($items | Microsoft.PowerShell.Utility\Group-Object Status | Microsoft.PowerShell.Utility\Sort-Object Name | Microsoft.PowerShell.Core\ForEach-Object { '{0}={1}' -f $_.Name, $_.Count })
    $rows = @((Select-PtcFirst @($items | Microsoft.PowerShell.Utility\Sort-Object Status, Name) $MaxItems) | Microsoft.PowerShell.Core\ForEach-Object {
        [pscustomobject]@{
            Status = $_.Status
            Name = $_.Name
            DisplayName = $_.DisplayName
        }
    })

    Join-PtcLines (@("services: {0} ({1})" -f $items.Count, ($status -join ', ')) + (Format-PtcTable -Rows $rows -Properties @('Status', 'Name', 'DisplayName') -MaxItems $MaxItems))
}

function Get-PtcStableTypeName {
    param(
        [AllowNull()][object]$InputObject,
        [AllowNull()][string]$DetachedTypeNonce
    )

    $typeName = [string]$InputObject.PSObject.TypeNames[0]
    if ($DetachedTypeNonce -cmatch '^[0-9a-f]{32}$') {
        $detachedType = [regex]::Match(
            $typeName,
            '^Ptk\.Detached\.(.+)\.([0-9a-f]{32})$',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if ($detachedType.Success -and
            $detachedType.Groups[2].Value -ceq $DetachedTypeNonce) {
            return $detachedType.Groups[1].Value
        }
    }
    $typeName
}

function Compress-PtcGenericObject {
    param(
        [object[]]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems,
        [AllowNull()][string]$DetachedTypeNonce
    )

    $items = @($InputObject)
    if ($items.Count -eq 0) { return '(empty)' }
    if ($items.Count -eq 1 -and $items[0] -is [string]) {
        return Limit-PtcText -Text $items[0] -MaxLines $MaxItems
    }

    $stringCount = @($items | Microsoft.PowerShell.Core\Where-Object { $_ -is [string] }).Count
    if ($stringCount -eq $items.Count) {
        return Limit-PtcText -Text ($items -join [Environment]::NewLine) -MaxLines $MaxItems
    }
    if ($stringCount -gt 0) {
        # Mixed stream containing strings: the text is the medium (issue #1 —
        # a String+MatchInfo repro rendered a Length-only table and lost the
        # payload). Render every item by its string form: strings are
        # themselves, MatchInfo.ToString() is path:line:content.
        return Limit-PtcText -Text (@($items | Microsoft.PowerShell.Core\ForEach-Object { [string]$_ }) -join [Environment]::NewLine) -MaxLines $MaxItems
    }

    # Index, don't Select-Object: piping a PSObject through Select-Object
    # -First stamps 'Selected.*' into its live TypeNames - the header would
    # name a wrapper type, and the mutation leaks onto the caller's objects
    # (visible across warm-session calls).
    $first = $items[0]
    $props = @(Get-PtcDisplayProperties -Object $first)
    $typeNames = @($items | Microsoft.PowerShell.Core\ForEach-Object {
        Get-PtcStableTypeName -InputObject $_ -DetachedTypeNonce $DetachedTypeNonce
    } | Microsoft.PowerShell.Utility\Select-Object -Unique)
    $header = if ($typeNames.Count -gt 1) {
        # Bound the type list too: a stream of many distinct types must not
        # grow the header line without limit (i1-1).
        $shown = @($typeNames | Microsoft.PowerShell.Utility\Select-Object -First 3) -join ', '
        $suffix = ($typeNames.Count -gt 3) ? (', +{0} more' -f ($typeNames.Count - 3)) : ''
        "objects: {0} (mixed: {1}{2})" -f $items.Count, $shown, $suffix
    }
    else {
        "objects: {0} ({1})" -f $items.Count, $typeNames[0]
    }
    $lines = @($header) + (Format-PtcTable -Rows $items -Properties $props -MaxItems $MaxItems)
    if ($typeNames.Count -gt 1) {
        # The table's columns come from the FIRST item only, so on a
        # type-heterogeneous stream it misrepresents the rest; carry some
        # payload so a summary never needs a raw re-run (issue #1 guardrail).
        $lines += 'samples:'
        for ($i = 0; $i -lt [Math]::Min(3, $items.Count); $i++) {
            $lines += '  ' + (Limit-PtcText -Text ([string]$items[$i]) -MaxLines 1 -Width 110)
        }
    }
    Join-PtcLines $lines
}

function Compress-PtcObject {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline)]
        [AllowNull()]
        [object]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems,
        [AllowNull()][string]$DetachedTypeNonce
    )

    begin {
        $items = [System.Collections.Generic.List[object]]::new()
    }
    process {
        if ($null -ne $InputObject) { $items.Add($InputObject) }
    }
    end {
        $array = @($items)
        if ($array.Count -eq 0) {
            '(empty)'
            return
        }

        # Route by type name, but only when EVERY item carries a matching type
        # name AND the properties the specialized compressor needs. Projections
        # and Clixml round-trips (e.g. Select-Object) keep the source type name
        # while dropping properties, so type name alone is not enough to
        # dispatch on; and shape alone is not enough either — one genuine item
        # must not drag look-alike shapes of other types into a specialized
        # compressor. Heterogeneous streams fall through to the generic path,
        # whose property access is null-safe. Each guard must list every
        # property its compressor dereferences directly; a property that is
        # legitimately absent on real objects is guarded conditionally instead:
        # DirectoryInfo has no Length, but a *file* without a known Length is a
        # projection whose size is unknown, not zero, so it goes generic.
        $typeNames = @($array | Microsoft.PowerShell.Core\ForEach-Object {
            Get-PtcStableTypeName -InputObject $_ -DetachedTypeNonce $DetachedTypeNonce
        } | Microsoft.PowerShell.Utility\Select-Object -Unique)
        $allMatchType = {
            param([string[]]$Pattern)
            foreach ($typeName in $typeNames) {
                $matched = @($Pattern | Microsoft.PowerShell.Core\Where-Object { $typeName -like $_ }).Count -gt 0
                if (-not $matched) { return $false }
            }
            $true
        }
        $allHaveProperties = {
            param([string[]]$Name)
            foreach ($item in $array) {
                if (-not (Test-PtcHasProperty -Object $item -Name $Name)) { return $false }
            }
            $true
        }

        $allFileSystemShaped = {
            foreach ($item in $array) {
                if (-not (Test-PtcHasProperty -Object $item -Name 'PSIsContainer', 'Name', 'LastWriteTime')) { return $false }
                if (-not [bool](Get-PtcPropertyValue -Object $item -Name 'PSIsContainer') -and
                    $null -eq (Get-PtcPropertyValue -Object $item -Name 'Length')) { return $false }
            }
            $true
        }

        if ((& $allMatchType '*System.IO.DirectoryInfo*', '*System.IO.FileInfo*') -and
            (& $allFileSystemShaped)) {
            Compress-PtcFileSystem -InputObject $array -MaxItems $MaxItems
            return
        }
        if ((& $allMatchType '*Microsoft.PowerShell.Commands.MatchInfo*') -and
            (& $allHaveProperties 'LineNumber', 'Path', 'Line')) {
            Compress-PtcMatchInfo -InputObject $array -MaxItems $MaxItems
            return
        }
        if ((& $allMatchType '*System.Diagnostics.Process*') -and
            (& $allHaveProperties 'Id', 'ProcessName', 'CPU', 'WorkingSet64')) {
            Compress-PtcProcess -InputObject $array -MaxItems $MaxItems
            return
        }
        if ((& $allMatchType '*ServiceController*') -and
            (& $allHaveProperties 'Status', 'Name', 'DisplayName')) {
            Compress-PtcService -InputObject $array -MaxItems $MaxItems
            return
        }

        Compress-PtcGenericObject -InputObject $array -MaxItems $MaxItems -DetachedTypeNonce $DetachedTypeNonce
    }
}

# Heuristic: does this text look like a log? (timestamped lines and/or level tags
# across most of the first 40 lines). Ported from the experiment/ptk-router spike.
function Test-PtcLogShaped {
    param([string]$Text)
    $allLines = @($Text -split "`r?`n" | Microsoft.PowerShell.Core\Where-Object { $_.Trim() })
    $hasStructuredRecord = @($allLines | Microsoft.PowerShell.Core\Where-Object {
        $trimmed = $_.Trim()
        ($trimmed.StartsWith('{') -and $trimmed.EndsWith('}')) -or
        ($trimmed.StartsWith('[') -and $trimmed.EndsWith(']'))
    }).Count -gt 0
    if ($hasStructuredRecord) { return $false }

    $lines = @($allLines | Microsoft.PowerShell.Utility\Select-Object -First 40)
    if (@($lines).Count -lt 5) { return $false }
    $levelHits = @($lines | Microsoft.PowerShell.Core\Where-Object {
        $_ -match '\[(INFO|WARN|WARNING|ERROR|FATAL|DEBUG|TRACE)\]' -or
        $_ -match '\b(INFO|WARN|ERROR|FATAL)\b.*:' -or
        $_ -match '^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}'
    }).Count
    return ($levelHits / @($lines).Count) -ge 0.5
}

# An explicitly set PTK_RTK_PATH wins outright: if it points at nothing, rtk is
# treated as absent rather than silently falling back to a different binary on
# PATH, so a misconfiguration stays visible.
function Get-PtcLoadedCommand {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][System.Management.Automation.CommandTypes]$CommandType
    )

    # InvokeCommand.GetCommand performs module auto-loading. Routing and
    # dialect detection run before execution is authorized, so command lookup
    # must be observational: already-loaded/session commands and PATH
    # commands only. Escape wildcard metacharacters to retain exact-name
    # semantics and filter defensively in case a provider returns aliases.
    $escaped = [System.Management.Automation.WildcardPattern]::Escape($Name)
    # Pattern-mode enumeration stays inside the already-loaded/session command
    # table and PATH without the exact-name module auto-loader. It is also
    # consistent across Windows and Unix, where Get-Command -ListImported with
    # an Application filter does not return the same PATH results.
    $matches = @($ExecutionContext.InvokeCommand.GetCommands(
            $escaped, $CommandType, $true) |
        Microsoft.PowerShell.Core\Where-Object { $_.Name -eq $Name } |
        Microsoft.PowerShell.Utility\Select-Object -First 1)
    if ($matches.Count -gt 0) { return $matches[0] }

    # Pattern-mode lookup deliberately avoids the exact-name auto-loader, but
    # on Windows it also omits extension expansion (`git` is reported as
    # `git.exe`). Reproduce only the external-command leg after session
    # commands had their chance to shadow it. Enumerating ExternalScript and
    # Application together preserves PowerShell's .ps1-before-.exe order;
    # filtering applications against PATHEXT excludes non-invocable files.
    $extensionCommandTypes = $CommandType -band (
        [System.Management.Automation.CommandTypes]::Application -bor
        [System.Management.Automation.CommandTypes]::ExternalScript)
    if ($IsWindows -and $extensionCommandTypes -ne 0) {
        $extensions = if ($env:PATHEXT) {
            @($env:PATHEXT -split ';' | Microsoft.PowerShell.Core\Where-Object { $_ })
        }
        else {
            @('.COM', '.EXE', '.BAT', '.CMD')
        }
        $extensionMatches = @($ExecutionContext.InvokeCommand.GetCommands(
                "$escaped.*", $extensionCommandTypes, $true) |
            Microsoft.PowerShell.Core\Where-Object {
                [System.IO.Path]::GetFileNameWithoutExtension($_.Name) -ieq $Name -and
                ($_.CommandType -eq [System.Management.Automation.CommandTypes]::ExternalScript -or
                 ($_.CommandType -eq [System.Management.Automation.CommandTypes]::Application -and
                  [System.IO.Path]::GetExtension($_.Name) -in $extensions))
            } |
            Microsoft.PowerShell.Utility\Select-Object -First 1)
        if ($extensionMatches.Count -gt 0) { return $extensionMatches[0] }
    }
    return $null
}

function Get-PtcRtkCommand {
    if (Microsoft.PowerShell.Management\Test-Path env:PTK_RTK_PATH) {
        if ($env:PTK_RTK_PATH -and (Microsoft.PowerShell.Management\Test-Path -LiteralPath $env:PTK_RTK_PATH)) { return $env:PTK_RTK_PATH }
        return $null
    }
    $cmd = Get-PtcLoadedCommand 'rtk' ([System.Management.Automation.CommandTypes]::Application)
    if ($cmd) { return $cmd.Source }
    return $null
}

function Get-PtcPinnedRtkCommand {
    param(
        [AllowNull()][string]$Path,
        [AllowNull()][string]$BinaryDigest,
        [AllowNull()][Nullable[System.IO.UnixFileMode]]$UnixFileMode
    )

    if (-not $Path -or $BinaryDigest -notmatch '^[0-9a-f]{64}$') { return $null }
    try {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        $file = [System.IO.FileInfo]::new($fullPath)
        $maximumBytes = 128L * 1024 * 1024
        if (-not $file.Exists -or $file.LinkTarget -or
            $file.Length -le 0 -or $file.Length -gt $maximumBytes) {
            return $null
        }
        $expectedLength = $file.Length
        if ($null -ne $UnixFileMode -and -not $IsWindows -and
            [int][System.IO.File]::GetUnixFileMode($fullPath) -ne [int]$UnixFileMode) {
            return $null
        }
        $stream = [System.IO.FileStream]::new(
            $fullPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read,
            65536,
            [System.IO.FileOptions]::SequentialScan)
        try {
            $hash = [System.Security.Cryptography.IncrementalHash]::CreateHash(
                [System.Security.Cryptography.HashAlgorithmName]::SHA256)
            try {
                $buffer = [byte[]]::new(65536)
                $total = 0L
                while ($total -lt $expectedLength) {
                    $remaining = $expectedLength - $total
                    $requested = [int][Math]::Min($buffer.Length, $remaining)
                    $read = $stream.Read($buffer, 0, $requested)
                    if ($read -le 0) { return $null }
                    $hash.AppendData($buffer, 0, $read)
                    $total += $read
                }
                if ($stream.ReadByte() -ne -1) { return $null }
                $actual = [System.Convert]::ToHexString(
                    $hash.GetHashAndReset()).ToLowerInvariant()
            }
            finally { $hash.Dispose() }
        }
        finally { $stream.Dispose() }
        $file.Refresh()
        if (-not $file.Exists -or $file.LinkTarget -or
            $file.Length -ne $expectedLength) {
            return $null
        }
        if ($null -ne $UnixFileMode -and -not $IsWindows -and
            [int][System.IO.File]::GetUnixFileMode($fullPath) -ne [int]$UnixFileMode) {
            return $null
        }
        if ($actual -cne $BinaryDigest) { return $null }
        return $fullPath
    }
    catch { return $null }
}

function Invoke-PtcRtkLog {
    param(
        [string]$Text,
        [AllowNull()][string]$PinnedRtkPath,
        [AllowNull()][string]$PinnedRtkDigest,
        [AllowNull()][Nullable[System.IO.UnixFileMode]]$PinnedRtkUnixMode
    )
    $pinned = $PSBoundParameters.ContainsKey('PinnedRtkPath')
    $rtk = if ($pinned) {
        Get-PtcPinnedRtkCommand -Path $PinnedRtkPath -BinaryDigest $PinnedRtkDigest `
            -UnixFileMode $PinnedRtkUnixMode
    }
    else {
        Get-PtcRtkCommand
    }
    if (-not $rtk) {
        return [pscustomobject]@{
            PSTypeName = 'Ptk.RtkLogResult'
            Text = "[ptk:log rtk not found - returning raw text.]`n$Text"
            Code = if ($pinned) { 'rtk_log_identity_unavailable' } else { 'rtk_log_unavailable' }
            RtkBinaryDigest = if ($pinned) { $PinnedRtkDigest } else { $null }
        }
    }
    # rtk is a native command, so invoking it overwrites the caller's
    # $LASTEXITCODE in this runspace. Shaping must not affect the call
    # (Compress-PtcOutput's contract), so restore the snapshot on the way out.
    # Snapshot the value, not the PSVariable: Get-Variable returns the live
    # variable object, whose .Value would mutate when rtk overwrites it.
    $exitCodeVariable = Microsoft.PowerShell.Utility\Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    $hadExitCode = $null -ne $exitCodeVariable
    $savedExitCode = if ($hadExitCode) { $exitCodeVariable.Value } else { $null }
    $tmp = $null
    try {
        $tmp = [System.IO.Path]::GetTempFileName()
        Microsoft.PowerShell.Management\Set-Content -LiteralPath $tmp -Value $Text -NoNewline
        $result = & $rtk log $tmp 2>$null
        $ok = $?
        if (-not $ok -or @($result).Count -eq 0) {
            return [pscustomobject]@{
                PSTypeName = 'Ptk.RtkLogResult'
                Text = "[ptk:log rtk failed - returning raw text.]`n$Text"
                Code = 'rtk_log_failed'
                RtkBinaryDigest = if ($pinned) { $PinnedRtkDigest } else { $null }
            }
        }
        [pscustomobject]@{
            PSTypeName = 'Ptk.RtkLogResult'
            Text = "[ptk:log via rtk]`n" + (@($result) -join [Environment]::NewLine)
            Code = 'rtk_log_used'
            RtkBinaryDigest = if ($pinned) { $PinnedRtkDigest } else { $null }
        }
    } catch {
        [pscustomobject]@{
            PSTypeName = 'Ptk.RtkLogResult'
            Text = "[ptk:log rtk failed - returning raw text.]`n$Text"
            Code = 'rtk_log_failed'
            RtkBinaryDigest = if ($pinned) { $PinnedRtkDigest } else { $null }
        }
    } finally {
        if ($tmp) {
            Microsoft.PowerShell.Management\Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
        if ($hadExitCode) {
            Microsoft.PowerShell.Utility\Set-Variable -Name LASTEXITCODE -Scope Global -Value $savedExitCode
        } else {
            Microsoft.PowerShell.Utility\Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
        }
    }
}

function Limit-PtcPassthrough {
    param(
        [AllowNull()][string]$Text,
        [int]$MaxLines = $script:PtcPassthroughMaxLines,
        [int]$MaxChars = $script:PtcPassthroughMaxChars,
        # The marker's recovery advice. The default is explicitly unavailable;
        # callers with a same-invocation artifact (or a job log) pass their
        # context-specific recovery instruction, so the advice is composed BY the elision
        # itself and can never be false or missing (sd3-2..sd3-4: two
        # downstream inference heuristics both failed - ANSI stripping
        # shortens without eliding, near-boundary elision lengthens).
        [string]$ElisionHint = 'recovery=unavailable: output capture unavailable; command was not rerun'
    )

    if ($null -eq $Text) { return '' }

    $lines = @($Text -split "`r?`n")
    $elidedLineCount = 0
    if ($lines.Count -gt $MaxLines) {
        $headCount = [int][Math]::Ceiling($MaxLines * 0.75)
        $tailCount = $MaxLines - $headCount
        $elidedLineCount = $lines.Count - $MaxLines
        $marker = '[{0} lines elided - {1}]' -f $elidedLineCount, $ElisionHint
        $Text = (@($lines | Microsoft.PowerShell.Utility\Select-Object -First $headCount) + $marker +
            @($lines | Microsoft.PowerShell.Utility\Select-Object -Last $tailCount)) -join [Environment]::NewLine
    }

    if ($Text.Length -gt $MaxChars) {
        $head = [int][Math]::Ceiling($MaxChars * 0.75)
        $tail = $MaxChars - $head
        # The char window can cut the line marker out of the elided middle, so
        # when both bounds fired this marker must carry both facts - every
        # elision stays explicit.
        $elided = if ($elidedLineCount -gt 0) {
            '[{0} lines and {1} chars elided - {2}]' -f
                $elidedLineCount, ($Text.Length - $MaxChars), $ElisionHint
        } else {
            '[{0} chars elided - {1}]' -f ($Text.Length - $MaxChars), $ElisionHint
        }
        $marker = '{0}{1}{0}' -f [Environment]::NewLine, $elided
        $Text = $Text.Substring(0, $head) + $marker + $Text.Substring($Text.Length - $tail)
    }

    $Text
}

# Shapes ptk_invoke output for the MCP server (Phase 2 plan): objects compress via
# Compress-PtcObject; log-shaped direct text goes to rtk when available; all
# text is ANSI-cleaned and bounded by the adopted labeled head+tail window.
# Contract: never throws - any internal failure returns labeled unshaped output,
# because shaping must not be able to fail a ptk_invoke call.
function Compress-PtcOutput {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline)]
        [AllowNull()]
        [object]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems,
        # Execution provenance lets the host retain ANSI cleanup and bounds
        # while suppressing only the lossy second rtk-log pass for output that
        # already came from RTK.
        [ValidateSet('powershell_objects', 'direct_text', 'rtk_unknown', 'rtk_filtered', 'rtk_passthrough')]
        [string]$InputProvenance,
        # Overrides the elision markers' recovery advice with the caller's
        # same-invocation artifact status (see Limit-PtcPassthrough).
        [string]$ElisionHint,
        # The MCP host always supplies its startup-frozen RTK identity. Direct
        # module consumers that omit these retain standalone PATH behavior.
        [AllowNull()][string]$PinnedRtkPath,
        [AllowNull()][string]$PinnedRtkDigest,
        [AllowNull()][Nullable[System.IO.UnixFileMode]]$PinnedRtkUnixMode,
        # Only the host's exact per-capture nonce authorizes stripping its
        # Ptk.Detached.<category>.<nonce> transport wrapper from type names.
        [AllowNull()][string]$DetachedTypeNonce,
        [switch]$EmitRoutingEnvelope
    )

    begin {
        $items = [System.Collections.Generic.List[object]]::new()
        $rtkRoutingAttempted = $false
        $skipRtkLog = $InputProvenance -in @(
            'rtk_unknown',
            'rtk_filtered',
            'rtk_passthrough')
        $limitArgs = @{}
        if ($ElisionHint) { $limitArgs['ElisionHint'] = $ElisionHint }
    }
    process {
        if ($null -ne $InputObject) { $items.Add($InputObject) }
    }
    end {
        $array = @($items)
        if ($array.Count -eq 0) { return }

        try {
            $textual = $true
            foreach ($item in $array) {
                if ($item -is [string]) { continue }
                if ($item.GetType().IsPrimitive -or $item -is [decimal]) { continue }
                $textual = $false
                break
            }

            if ($textual) {
                # Strip ANSI/control sequences at ingest, BEFORE classification:
                # they are pure token waste to a model, and a color prefix
                # defeats Test-PtcLogShaped's line-start timestamp anchor, so a
                # colored log would dodge the rtk dedup leg. Legacy raw is
                # inert, so every capturable call follows this shaping path.
                $text = Remove-PtcAnsi (@($array | Microsoft.PowerShell.Core\ForEach-Object { "$_" }) -join [Environment]::NewLine)
                if (-not $skipRtkLog -and (Test-PtcLogShaped -Text $text)) {
                    $rtkArgs = @{ Text = $text }
                    if ($PSBoundParameters.ContainsKey('PinnedRtkPath')) {
                        $rtkArgs['PinnedRtkPath'] = $PinnedRtkPath
                        $rtkArgs['PinnedRtkDigest'] = $PinnedRtkDigest
                        $rtkArgs['PinnedRtkUnixMode'] = $PinnedRtkUnixMode
                    }
                    $rtkRoutingAttempted = $true
                    $rtkResult = Invoke-PtcRtkLog @rtkArgs
                    $rendered = Limit-PtcPassthrough $rtkResult.Text @limitArgs
                    if ($EmitRoutingEnvelope) {
                        return [pscustomobject]@{
                            PSTypeName = 'Ptk.OutputRoutingEnvelope'
                            Text = $rendered
                            ShapingCode = $rtkResult.Code
                            RtkBinaryDigest = $rtkResult.RtkBinaryDigest
                        }
                    }
                    return $rendered
                }
                return (Limit-PtcPassthrough $text @limitArgs)
            }

            return ($array | Compress-PtcObject -MaxItems $MaxItems -DetachedTypeNonce $DetachedTypeNonce)
        }
        catch {
            $raw = ($array | Microsoft.PowerShell.Utility\Out-String).TrimEnd()
            # Bound the fallback too (P3: no unbounded path), but never let the
            # bounder violate the never-throw contract of this catch.
            try { $raw = Limit-PtcPassthrough $raw @limitArgs } catch { }
            $failureText = "[ptk:shape ERROR - $($_.Exception.Message). Returning unshaped output.]`n$raw"
            if ($EmitRoutingEnvelope -and $rtkRoutingAttempted) {
                return [pscustomobject]@{
                    PSTypeName = 'Ptk.OutputRoutingEnvelope'
                    Text = $failureText
                    ShapingCode = 'rtk_log_failed'
                    RtkBinaryDigest = $PinnedRtkDigest
                }
            }
            return $failureText
        }
    }
}

Microsoft.PowerShell.Core\Export-ModuleMember -Function @(
    'Compress-PtcObject',
    'Compress-PtcOutput'
)
