namespace PtkMcpServer.Tests;

public sealed class RunspaceHostTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task State_persists_across_calls()
    {
        await _host.InvokeAsync("$x = 41");
        var result = await _host.InvokeAsync("$x + 1");

        Assert.True(result.Success);
        Assert.Equal("42", result.Output.Trim());
    }

    [Fact]
    public async Task Imported_module_stays_loaded_across_calls()
    {
        var import = await _host.InvokeAsync(
            "New-Module -Name PtkWarmTest -ScriptBlock { function Get-Warm { 'warm' } } | Import-Module");
        Assert.True(import.Success);

        var result = await _host.InvokeAsync("Get-Warm");

        Assert.True(result.Success);
        Assert.Equal("warm", result.Output.Trim());
    }

    [Fact]
    public async Task Nonterminating_error_surfaces_without_failing_the_call()
    {
        var result = await _host.InvokeAsync("Write-Error 'boom'; 'still ran'");

        Assert.True(result.Success);
        Assert.Contains("still ran", result.Output);
        Assert.Contains(result.Errors, e => e.Contains("boom"));
    }

    [Fact]
    public async Task Terminating_error_fails_the_call_but_host_survives()
    {
        var result = await _host.InvokeAsync("throw 'bang'");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("bang"));

        var next = await _host.InvokeAsync("'alive'");
        Assert.True(next.Success);
        Assert.Equal("alive", next.Output.Trim());
    }

    [Fact]
    public async Task Warning_stream_is_captured()
    {
        var result = await _host.InvokeAsync("Write-Warning 'careful'");

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, w => w.Contains("careful"));
    }

    [Fact]
    public async Task Concurrent_calls_are_serialized_not_corrupted()
    {
        await _host.InvokeAsync("$counter = 0");

        var calls = Enumerable.Range(0, 8)
            .Select(_ => _host.InvokeAsync("$counter = $counter + 1"))
            .ToArray();
        await Task.WhenAll(calls);

        var result = await _host.InvokeAsync("$counter");
        Assert.Equal("8", result.Output.Trim());
    }

    [Fact]
    public async Task Timeout_recycles_the_runspace_and_host_survives()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(2));

        await host.InvokeAsync("$x = 'before-timeout'");
        var timedOut = await host.InvokeAsync("Start-Sleep -Seconds 60");

        Assert.False(timedOut.Success);
        Assert.True(timedOut.TimedOut);

        // Recycled runspace: host answers again, but pre-timeout state is gone.
        var after = await host.InvokeAsync("if ($null -eq $x) { 'state-cleared' } else { $x }");
        Assert.True(after.Success);
        Assert.Equal("state-cleared", after.Output.Trim());
    }
}
