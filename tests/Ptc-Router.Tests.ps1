# EXPERIMENTAL — tests for experimental/Ptc-Router.ps1 (ptk-as-router MVP).
# Deterministic: no live ollama, no live rtk. Network and native rtk are mocked.
#
# Red-first: the "context ceiling" and "malformed response" tests are written to
# assert the CORRECT behavior, so they fail against the current code and pass once
# the bugs (Codex #1, #6) are fixed.

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../src/PwshTokenCompressor.psd1') -Force
    . (Join-Path $PSScriptRoot '../experimental/Ptc-Router.ps1')

    # Capture the last request body sent to ollama so tests can assert on num_ctx etc.
    $script:LastBody = $null
}

Describe 'Invoke-PtcRoute routing' {

    It 'routes objects to Compress-PtcObject (lossless)' {
        $out = Invoke-PtcRoute { 1..3 | ForEach-Object { [pscustomobject]@{ Name = "n$_"; V = $_ } } }
        $out | Should -Match 'objects: 3'
        $out | Should -Match 'n1'
    }

    It 'passes small text through raw (does not digest)' {
        $out = Invoke-PtcRoute { 'alpha'; 'beta'; 'gamma' }
        $out | Should -Match 'alpha'
        $out | Should -Match 'gamma'
        $out | Should -Not -Match 'Text:.*words'   # the digest header must NOT appear
    }

    It 'honors -Route raw override' {
        $out = Invoke-PtcRoute { 'one'; 'two' } -Route raw
        $out | Should -Be ("one" + [Environment]::NewLine + "two")
    }
}

Describe 'Invoke-PtcModel (ollama escalation, mocked)' {

    BeforeEach {
        $script:LastBody = $null
    }

    It 'strips <think> blocks and labels output lossy' {
        Mock Invoke-RestMethod { [pscustomobject]@{ response = '<think>secret reasoning</think>VISIBLE ANSWER' } }
        $out = Invoke-PtcModel -Text 'some input text'
        $out | Should -Match 'VISIBLE ANSWER'
        $out | Should -Not -Match 'secret reasoning'
        $out | Should -Match '\[ptk:model LOSSY SUMMARY'
    }

    It 'returns labeled raw text when the network call throws' {
        Mock Invoke-RestMethod { throw 'connection refused' }
        $out = Invoke-PtcModel -Text 'PAYLOAD-MARKER'
        $out | Should -Match 'ptk:model ERROR'
        $out | Should -Match 'PAYLOAD-MARKER'
    }

    # RED for Codex #6: HTTP 200 with no `response` field must fall back, not crash.
    It 'falls back to labeled raw on a malformed 200 response (no .response field)' {
        Mock Invoke-RestMethod { [pscustomobject]@{ unexpected = 'shape' } }
        { Invoke-PtcModel -Text 'PAYLOAD-MARKER' } | Should -Not -Throw
        $out = Invoke-PtcModel -Text 'PAYLOAD-MARKER'
        $out | Should -Match 'PAYLOAD-MARKER'
    }

    # RED for Codex #1: the context window we SEND must be able to hold the input
    # we send, or we silently truncate. Assert num_ctx is consistent with the
    # skip-gate ceiling, not a fixed 32k.
    It 'does not send input larger than the declared num_ctx (no silent truncation)' {
        Mock Invoke-RestMethod {
            param($Uri, $Method, $Body, $ContentType, $TimeoutSec)
            $script:LastBody = $Body | ConvertFrom-Json
            [pscustomobject]@{ response = 'ok' }
        }
        # ~50k tokens of input: above the old 32k num_ctx, below the 250k skip-gate.
        $big = ('word ' * 50000)
        $approxTok = [int][math]::Ceiling($big.Length / 4.0)
        Invoke-PtcModel -Text $big | Out-Null

        $script:LastBody | Should -Not -BeNullOrEmpty
        $sentCtx = [int]$script:LastBody.options.num_ctx
        # The window we asked for must be able to hold the input we sent.
        $sentCtx | Should -BeGreaterOrEqual $approxTok
    }
}

Describe 'Invoke-PtcRtkLog (mocked rtk)' {

    It 'falls back to labeled raw when rtk is absent' {
        Mock Get-Command { $null } -ParameterFilter { $Name -eq 'rtk' }
        $out = Invoke-PtcRtkLog -Text 'LOGLINE-MARKER'
        $out | Should -Match 'rtk not found'
        $out | Should -Match 'LOGLINE-MARKER'
    }
}
