[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command = 'help',

    [Parameter(ValueFromRemainingArguments)]
    [object[]]$Arguments
)

$modulePath = Join-Path $PSScriptRoot 'src/PwshTokenCompressor.psd1'
Import-Module $modulePath -Force
Invoke-Ptc -Command $Command @Arguments
