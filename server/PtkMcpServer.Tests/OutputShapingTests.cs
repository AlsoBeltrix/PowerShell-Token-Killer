using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tests;

// ProcessEnvironment collection: mutates PSExecutionPolicyPreference and calls
// ResetAsync, whose environment restore would wipe parallel classes' env vars.
[Collection("ProcessEnvironment")]
public sealed class OutputShapingTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Trusted_compressor_is_live_but_detached_from_the_user_session()
    {
        Assert.True(_host.ModuleLoaded);

        var visibility = await _host.InvokeAsync(
            "@(Microsoft.PowerShell.Core\\Get-Module PwshTokenCompressor -All).Count; " +
            "[int]($null -ne $ExecutionContext.InvokeCommand.GetCommand(" +
            "'PwshTokenCompressor\\Compress-PtcOutput', " +
            "[System.Management.Automation.CommandTypes]::Function))",
            raw: true,
            route: "pwsh");
        Assert.True(visibility.Success);
        Assert.Empty(visibility.Errors);
        Assert.Equal(["0", "0"], visibility.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var shaped = await _host.InvokeAsync(
            "[pscustomobject]@{ Name = 'a' }, [pscustomobject]@{ Name = 'b' }");
        Assert.True(shaped.Success);
        Assert.StartsWith("objects: 2", shaped.Output.Trim());
    }

    [Fact]
    public async Task Object_output_is_compressed()
    {
        var result = await _host.InvokeAsync(
            "[pscustomobject]@{ Name = 'a'; Value = 1 }, [pscustomobject]@{ Name = 'b'; Value = 2 }");

        Assert.True(result.Success);
        Assert.StartsWith("objects: 2", result.Output.Trim());
    }

    [Fact]
    public async Task String_output_passes_through_untruncated()
    {
        var result = await _host.InvokeAsync("1..40 | ForEach-Object { \"line $_\" }");

        Assert.True(result.Success);
        Assert.Contains("line 40", result.Output);
        Assert.DoesNotContain("more", result.Output);
    }

    /// <summary>
    /// GitHub #34 F7 / #35 F4: an elided response carried
    /// "[N lines elided - recovery=unavailable ...]" and then ended with a
    /// working recovery handle — the two statements contradicted each other in
    /// the same response. The hint is composed while the artifact is still
    /// being written, so a handle it cannot see yet is not the same as no
    /// recovery; it must not assert either way.
    /// </summary>
    [Fact]
    public async Task An_elision_marker_never_contradicts_the_recovery_line()
    {
        var result = await _host.InvokeAsync(
            "1..3000 | ForEach-Object { \"line-$_-\" + ('x' * 40) }");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains("elided", result.Output, StringComparison.Ordinal);
        // The marker itself must claim no verdict.
        var markerLine = result.Output
            .Split('\n')
            .FirstOrDefault(line => line.Contains("elided", StringComparison.Ordinal));
        Assert.NotNull(markerLine);
        Assert.DoesNotContain("recovery=unavailable", markerLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// GitHub #34 F8, #35, #36: only declines were labeled, and only under an
    /// explicit route=rtk, so no response ever said which route actually ran —
    /// the auto and pwsh answers for one script were byte-identical. A rewrite
    /// that RUNS now says so.
    ///
    /// A decline under `auto` stays silent deliberately: that is every cmdlet
    /// and every pipeline, and labeling it would put a routing line on nearly
    /// every response to announce that nothing happened.
    /// </summary>
    /// <summary>
    /// GitHub #34 F2, #35 F5: for a lossy projection the artifact stores the
    /// same reduced view already shown inline — #34 measured a nested graph
    /// whose entire stored capture was 105 bytes of the collapsed table — yet
    /// the response offered a bare "recovery=available", promising a fuller
    /// copy that does not exist. The offer stands; the claim is now qualified.
    /// </summary>
    [Fact]
    public async Task A_lossy_projection_says_its_artifact_holds_the_same_view()
    {
        // Driven through the session runtime, which composes the recovery
        // line; InvokeAsync alone returns the shaped text without it.
        using var store = new OutputStore(new OutputStoreOptions(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ptk",
                "lossy-recovery-tests",
                Guid.NewGuid().ToString("N")),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            MaximumArtifactBytes: 1024 * 1024,
            MaximumSessionBytes: 2 * 1024 * 1024,
            MaximumAggregateBytes: 4 * 1024 * 1024));
        using var runtime = new SessionRuntime(_host, new RawUsageCounter());

        var result = await runtime.InvokeAsync(
            "[pscustomobject]@{ L1 = [pscustomobject]@{ L2 = 'deep' } }",
            CancellationToken.None,
            outputStore: store);

        Assert.Contains("recovery=available", result, StringComparison.Ordinal);
        Assert.Contains(
            "same shaped view as above",
            result,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// GitHub #34 F5: `1>&amp;2` is valid cmd/bash and reserved-but-unimplemented
    /// in PowerShell, so the whole script is parse-rejected and nothing runs.
    /// The bare parser message says "reserved for future use" and nothing
    /// about what to write instead, which is the actual problem for a caller
    /// carrying bash habits into a PowerShell dialect.
    /// </summary>
    [Fact]
    public async Task A_bash_stderr_redirection_explains_the_dialect_boundary()
    {
        using var runtime = new SessionRuntime(_host, new RawUsageCounter());

        var result = await runtime.InvokeAsync(
            "cmd /c echo hi 1>&2",
            CancellationToken.None);

        Assert.Contains("reserved for future use", result, StringComparison.Ordinal);
        Assert.Contains("[ptk hint]", result, StringComparison.Ordinal);
        Assert.Contains("Write-Error", result, StringComparison.Ordinal);
        Assert.Contains("bash -lc", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_cmdlet_carries_no_routing_noise()
    {
        // The positive half — a command that actually routes reports
        // effective=rtk — needs a real rtk, which this suite replaces with a
        // stub, so it is proved against a live server instead (recorded in the
        // commit). What is provable here is the half that would regress into
        // noise: a decline under `auto` stays silent.
        var plain = await _host.InvokeAsync("Get-Date -Format yyyy");

        Assert.True(plain.Success, string.Join(Environment.NewLine, plain.Errors));
        Assert.DoesNotContain("[route]", plain.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_raw_does_not_skip_compression()
    {
        var result = await _host.InvokeAsync("[pscustomobject]@{ Name = 'a'; Value = 1 }", raw: true);

        Assert.True(result.Success);
        Assert.StartsWith("objects: 1", result.Output.Trim());
    }

    [Fact]
    public async Task Missing_module_falls_back_to_plain_output()
    {
        using var host = new RunspaceHost(
            callTimeout: TimeSpan.FromSeconds(60),
            modulePathOverride: Path.Combine(Path.GetTempPath(), "no-such-module.psd1"));

        Assert.False(host.ModuleLoaded);

        var result = await host.InvokeAsync("[pscustomobject]@{ Name = 'a'; Value = 1 }");
        Assert.True(result.Success);
        Assert.DoesNotContain("objects:", result.Output);
        Assert.Contains("Name", result.Output);
    }

    [Fact]
    public void Module_loads_under_restrictive_windows_execution_policy()
    {
        // Regression: hosted runspaces resolve Windows execution policy, and a
        // machine with none configured (CI runners, fresh installs) defaults to
        // Restricted, which blocked the module import until the runspace pinned
        // its own policy. Process scope outranks user/machine config, so this
        // simulates the unconfigured-machine default on any Windows box.
        var saved = Environment.GetEnvironmentVariable("PSExecutionPolicyPreference");
        try
        {
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", "Restricted");
            using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60));

            Assert.True(host.ModuleLoaded);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", saved);
        }
    }

    [Fact]
    public async Task Reset_reimports_the_module_so_shaping_survives_recycles()
    {
        await _host.ResetAsync();

        Assert.True(_host.ModuleLoaded);

        var result = await _host.InvokeAsync(
            "[pscustomobject]@{ Name = 'a'; Value = 1 }, [pscustomobject]@{ Name = 'b'; Value = 2 }");
        Assert.StartsWith("objects: 2", result.Output.Trim());
    }
}
