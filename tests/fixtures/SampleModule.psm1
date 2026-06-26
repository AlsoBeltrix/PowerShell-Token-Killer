<#
.SYNOPSIS
Example module help that should not survive minimal filtering.
#>

using namespace System.Collections.Generic

Import-Module Microsoft.PowerShell.Management

class SampleThing {
    [string]$Name
}

function Get-SampleThing {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [int]$Count = 1
    )

    # This body should be dropped in aggressive mode.
    for ($i = 0; $i -lt $Count; $i++) {
        [SampleThing]@{ Name = $Name }
    }
}

function Set-SampleThing {
    param([string]$Name)
    Write-Output "set $Name"
}

Set-Alias -Name gst -Value Get-SampleThing
Export-ModuleMember -Function Get-SampleThing, Set-SampleThing -Alias gst
