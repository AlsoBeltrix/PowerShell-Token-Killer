@{
    RootModule        = 'PwshTokenCompressor.psm1'
    ModuleVersion     = '0.2.0'
    GUID              = 'f1110f9c-3d25-4e74-8f46-f7f6a13d2b23'
    Author            = 'PowerShell Token Killer contributors'
    CompanyName       = 'PowerShell-Token-Killer'
    Copyright         = '(c) 2026 PowerShell Token Killer contributors. Licensed under the Apache License, Version 2.0.'
    Description       = 'Output shaping library for the ptk warm-runspace MCP server.'
    PowerShellVersion = '7.2'
    FunctionsToExport = @(
        'Compress-PtcObject',
        'Compress-PtcOutput'
    )
    AliasesToExport   = @()
    CmdletsToExport   = @()
    VariablesToExport = @()
}
