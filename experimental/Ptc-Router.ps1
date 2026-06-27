# =============================================================================
# EXPERIMENTAL — ptk-as-router MVP.  NOT part of the reviewed module.
#
# Saturday spike (2026-06-27). Lives on branch experiment/ptk-router only.
# Kill switch: delete this file / drop the branch. Nothing in src/ depends on it.
#
# Idea under test: ptk is a toolbox+router. It runs a PowerShell command
# IN-PROCESS (preserving the warm session — cold child pwsh would break the
# owner's on-prem Exchange workflow), inspects the result, and dispatches:
#   - non-string objects        -> Compress-PtcObject        (lossless, instant)
#   - log-shaped text           -> rtk log                   (dedup, instant)
#   - long unstructured text    -> local model via ollama    (LOSSY, slow, labeled)
#   - short/structured text     -> Invoke-PtcTextFilter      (deterministic)
#
# Auto-routes by default; -Route forces a path. Model output is ALWAYS labeled
# lossy with before/after token estimates so the calling agent never mistakes a
# summary for faithful data.
# =============================================================================

# Invoke-PtcTextFilter is internal to the module (not exported). Call it through
# the module's own scope so the experimental router can reach it without us
# changing the reviewed module's export list.
function Invoke-PtcTextFilterScoped {
    param([string]$Text, [string]$Path, [string]$Level = 'aggressive', [int]$MaxLines = 300)
    $mod = Get-Module PwshTokenCompressor
    if (-not $mod) { return $Text }  # module not loaded — degrade to raw
    & $mod {
        param($t, $p, $l, $m)
        Invoke-PtcTextFilter -Text $t -Path $p -Level $l -MaxLines $m
    } $Text $Path $Level $MaxLines
}

$script:PtcRouterModel   = 'qwen3.6:35b-mlx'
$script:PtcOllamaUrl     = 'http://localhost:11434/api/generate'
$script:PtcModelMaxTok   = 250000   # below ollama's ~256k ceiling, with headroom
$script:PtcLongTextTok   = 1500     # only bother with the model above this
$script:PtcModelCtx      = 32768

# Rough token estimate (chars/4) — good enough for routing + labeling, no tiktoken dep.
function Get-PtcApproxTokens {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return 0 }
    [int][math]::Ceiling($Text.Length / 4.0)
}

# Heuristic: does this text look like a log? (timestamped lines and/or level tags)
function Test-PtcLogShaped {
    param([string]$Text)
    $lines = @($Text -split "`r?`n" | Where-Object { $_.Trim() }) | Select-Object -First 40
    if ($lines.Count -lt 5) { return $false }
    $levelHits = @($lines | Where-Object {
        $_ -match '\[(INFO|WARN|WARNING|ERROR|FATAL|DEBUG|TRACE)\]' -or
        $_ -match '\b(INFO|WARN|ERROR|FATAL)\b.*:' -or
        $_ -match '^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}'
    }).Count
    return ($levelHits / $lines.Count) -ge 0.5
}

function Invoke-PtcModel {
    param(
        [string]$Text,
        [string]$Model = $script:PtcRouterModel,
        [string]$Instruction = $null
    )
    $inTok = Get-PtcApproxTokens $Text
    if ($inTok -gt $script:PtcModelMaxTok) {
        return @"
[ptk:model SKIPPED — input ~$inTok tokens exceeds model ceiling ($($script:PtcModelMaxTok)).
 Deterministic compression required first. Returning raw text untouched.]
$Text
"@
    }
    if (-not $Instruction) {
        $Instruction = @'
You are a token optimization expert helping a coding agent. Read the document
below and produce a semantically accurate, token-efficient version. Preserve
every fact, number, name, date, and decision; drop only redundancy and filler.
Output only the optimized document, no preamble.
'@
    }
    $prompt = "$Instruction`n`n<doc>`n$Text`n</doc>"
    $body = @{
        model   = $Model
        prompt  = $prompt
        stream  = $false
        think   = $false
        options = @{ temperature = 0.0; num_ctx = $script:PtcModelCtx }
    } | ConvertTo-Json -Depth 6 -Compress

    try {
        $resp = Invoke-RestMethod -Uri $script:PtcOllamaUrl -Method Post -Body $body `
            -ContentType 'application/json' -TimeoutSec 900
    } catch {
        return "[ptk:model ERROR — $($_.Exception.Message). Returning raw text.]`n$Text"
    }
    $out = $resp.response
    if ($out -match '(?s)<think>.*?</think>') { $out = $out -replace '(?s)<think>.*?</think>', '' }
    $out = $out.Trim()
    $outTok = Get-PtcApproxTokens $out
    $pct = if ($inTok -gt 0) { [math]::Round((1 - $outTok / [double]$inTok) * 100, 1) } else { 0 }
    @"
[ptk:model LOSSY SUMMARY via $Model — ~$inTok -> ~$outTok tokens ($pct% smaller).
 Non-deterministic; may omit details. Re-fetch raw if a specific value matters.]
$out
"@
}

function Invoke-PtcRtkLog {
    param([string]$Text)
    if (-not (Get-Command rtk -ErrorAction SilentlyContinue)) {
        return "[ptk:log rtk not found — falling back to raw text.]`n$Text"
    }
    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        Set-Content -Path $tmp -Value $Text -NoNewline
        $result = & rtk log $tmp 2>$null
        "[ptk:log via rtk]`n" + ($result -join [Environment]::NewLine)
    } finally {
        Remove-Item $tmp -ErrorAction SilentlyContinue
    }
}

function Invoke-PtcRoute {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)]
        [scriptblock]$Command,
        [ValidateSet('auto', 'object', 'log', 'model', 'text', 'raw')]
        [string]$Route = 'auto',
        [string]$Model = $script:PtcRouterModel
    )

    # Run IN-PROCESS — preserves the warm host/session.
    $result = & $Command

    # Object path: anything that isn't plain strings.
    $isStringy = $true
    foreach ($item in @($result)) {
        if ($null -ne $item -and $item -isnot [string]) { $isStringy = $false; break }
    }

    if ($Route -eq 'object' -or ($Route -eq 'auto' -and -not $isStringy)) {
        return ($result | Compress-PtcObject | Out-String).TrimEnd()
    }

    # From here we are dealing with text.
    $text = (@($result) -join [Environment]::NewLine)
    $tok  = Get-PtcApproxTokens $text

    switch ($Route) {
        'raw'   { return $text }
        'text'  { return Invoke-PtcTextFilterScoped -Text $text -Path 'out.txt' }
        'log'   { return Invoke-PtcRtkLog -Text $text }
        'model' { return Invoke-PtcModel -Text $text -Model $Model }
        'auto'  {
            if (Test-PtcLogShaped $text)          { return Invoke-PtcRtkLog -Text $text }
            if ($tok -ge $script:PtcLongTextTok)  { return Invoke-PtcModel -Text $text -Model $Model }
            return Invoke-PtcTextFilterScoped -Text $text -Path 'out.txt'
        }
    }
}

Set-Alias -Name ptkr -Value Invoke-PtcRoute -Scope Global -ErrorAction SilentlyContinue
