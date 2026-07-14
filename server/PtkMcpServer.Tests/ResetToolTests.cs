using PtkMcpServer.Sessions;
using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

// Shares the ProcessEnvironment collection: reset restores the process-wide
// environment baseline, which would wipe env vars a parallel test class set.
[Collection("ProcessEnvironment")]
public sealed class ResetToolTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));
    private readonly JobManager _jobs = new(
        Path.Combine(Path.GetTempPath(), "ptk-reset-jobs-" + Guid.NewGuid().ToString("N")));
    private readonly RawUsageCounter _rawUsage = new();
    private readonly SessionRuntime _runtime;

    public ResetToolTests()
    {
        _runtime = new SessionRuntime(_host, _jobs, _rawUsage);
    }

    public void Dispose()
    {
        _runtime.Dispose();
    }

    [Fact]
    public async Task Reset_clears_variables_and_loaded_modules()
    {
        await _host.InvokeAsync("$x = 'precious'");
        await _host.InvokeAsync(
            "New-Module -Name PtkWarmTest -ScriptBlock { function Get-Warm { 'warm' } } | Import-Module");

        var message = await _runtime.ResetAsync(CancellationToken.None);
        Assert.Contains("recycled", message);

        var variable = await _host.InvokeAsync("if ($null -eq $x) { 'gone' } else { $x }");
        Assert.Equal("gone", variable.Output.Trim());

        var state = await _runtime.StateAsync(listAvailable: false, CancellationToken.None);
        Assert.DoesNotContain("PtkWarmTest", state);
    }

    [Fact]
    public async Task Reset_adapter_forwards_to_the_session_runtime()
    {
        await _host.InvokeAsync("$adapterResetProbe = 'present'");

        var message = await ResetTool.Reset(_runtime, CancellationToken.None);

        Assert.Contains("recycled", message);
        var probe = await _host.InvokeAsync(
            "if ($null -eq $adapterResetProbe) { 'gone' } else { $adapterResetProbe }");
        Assert.Equal("gone", probe.Output.Trim());
    }

    [Fact]
    public async Task Reset_kills_running_background_jobs()
    {
        var job = _jobs.Start("Start-Sleep -Seconds 300");
        Assert.True(_jobs.Snapshot(job.Id)!.Running);

        var message = await _runtime.ResetAsync(CancellationToken.None);
        Assert.Contains("1 background job(s) killed", message);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_jobs.Snapshot(job.Id)!.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }
        Assert.False(_jobs.Snapshot(job.Id)!.Running);
    }

    [Fact]
    public async Task Reset_restores_the_environment_baseline()
    {
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            await _host.InvokeAsync("$env:PTK_RESET_DRIFT_PROBE = 'polluted'");
            await _host.InvokeAsync(
                "$env:PATH = 'ptk-fake-shim-dir' + [System.IO.Path]::PathSeparator + $env:PATH");
            Assert.Equal("polluted", Environment.GetEnvironmentVariable("PTK_RESET_DRIFT_PROBE"));

            await _runtime.ResetAsync(CancellationToken.None);

            Assert.Null(Environment.GetEnvironmentVariable("PTK_RESET_DRIFT_PROBE"));
            Assert.Equal(savedPath, Environment.GetEnvironmentVariable("PATH"));

            var state = await _runtime.StateAsync(listAvailable: false, CancellationToken.None);
            Assert.DoesNotContain("PTK_RESET_DRIFT_PROBE", state);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RESET_DRIFT_PROBE", null);
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    [Fact]
    public async Task Reset_preserves_runtime_raw_count_and_available_module_cache()
    {
        _runtime.ClearAvailableCacheForTests();
        var enumerations = 0;
        _host.IdleInvocationOverrideForTests = script =>
        {
            if (!script.Contains("-ListAvailable", StringComparison.Ordinal)) return null;
            Interlocked.Increment(ref enumerations);
            return new InvokeResult(
                Success: true,
                Output: "  CachedAcrossReset 1.0",
                Errors: [],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.Completed,
                UserExecutionStarted: true);
        };
        try
        {
            await _runtime.InvokeAsync("'raw probe'", CancellationToken.None, raw: true);
            var before = await _runtime.StateAsync(
                listAvailable: true,
                CancellationToken.None);
            Assert.Contains("CachedAcrossReset 1.0", before);
            Assert.Equal(1, Volatile.Read(ref enumerations));

            await _runtime.ResetAsync(CancellationToken.None);

            var after = await _runtime.StateAsync(
                listAvailable: true,
                CancellationToken.None);
            Assert.Contains("CachedAcrossReset 1.0", after);
            Assert.Contains("raw calls this session: 1", after);
            Assert.Equal(1, Volatile.Read(ref enumerations));
        }
        finally
        {
            _host.IdleInvocationOverrideForTests = null;
            _runtime.ClearAvailableCacheForTests();
        }
    }
}
