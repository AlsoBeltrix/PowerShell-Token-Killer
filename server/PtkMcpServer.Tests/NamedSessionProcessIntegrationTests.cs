using System.Diagnostics;
using System.Text;
using PtkMcpServer.Sessions;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class NamedSessionProcessIntegrationTests
{
    private static readonly TimeSpan CheckpointTimeout = TimeSpan.FromSeconds(60);
    private static readonly WorkerProtocolLimits Limits =
        WorkerOperationProtocol.CreateLimits(
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1));

    [Fact]
    public async Task Two_real_workers_keep_overlapping_PowerShell_state_isolated()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ptk-named-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var onPremDirectory = Path.Combine(root, "onprem");
        var onlineDirectory = Path.Combine(root, "online");
        var launchDirectory = Path.Combine(root, "launch");
        Directory.CreateDirectory(onPremDirectory);
        Directory.CreateDirectory(onlineDirectory);
        Directory.CreateDirectory(launchDirectory);
        var launchMarkerName = $"launch-{Guid.NewGuid():N}.txt";
        var launchMarkerValue = $"marker-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(
            Path.Combine(launchDirectory, launchMarkerName),
            launchMarkerValue);
        var onPremModule = Path.Combine(onPremDirectory, "OnPremOnly.psm1");
        var onlineModule = Path.Combine(onlineDirectory, "OnlineOnly.psm1");
        await File.WriteAllTextAsync(
            onPremModule,
            "function Get-OnPremOnly { 'onprem-module' }\n" +
            "Export-ModuleMember -Function Get-OnPremOnly\n");
        await File.WriteAllTextAsync(
            onlineModule,
            "function Get-OnlineOnly { 'online-module' }\n" +
            "Export-ModuleMember -Function Get-OnlineOnly\n");

        var brokerPath = await BuildBrokerAsync(root);
        WorkerLaunchCommand command;
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = launchDirectory;
            command = SessionWorkerLaunchCommand.Create();
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
        await using var sessions = new NamedSessionSupervisor(
            () => new ProcessSessionWorkerFactory(
                WorkerProcessLauncher.Create(brokerPath),
                command,
                Limits),
            startupTimeout: TimeSpan.FromSeconds(30),
            containmentGrace: TimeSpan.FromSeconds(3));

        Process? onPremWitness = null;
        Process? onlineWitness = null;
        Process? replacementWitness = null;
        try
        {
            var opened = await Task.WhenAll(
                sessions.OpenAsync("exchange-onprem"),
                sessions.OpenAsync("exchange-online"))
                .WaitAsync(CheckpointTimeout);
            var onPrem = opened.Single(item => item.Name == "exchange-onprem");
            var online = opened.Single(item => item.Name == "exchange-online");
            Assert.NotNull(onPrem.WorkerProcessId);
            Assert.NotNull(online.WorkerProcessId);
            Assert.NotEqual(onPrem.WorkerProcessId, online.WorkerProcessId);
            onPremWitness = OpenWitness(onPrem.WorkerProcessId.Value);
            onlineWitness = OpenWitness(online.WorkerProcessId.Value);

            Assert.Contains(
                $"initial-marker={launchMarkerValue}",
                await InvokeAsync(
                    sessions,
                    "exchange-onprem",
                    $"'initial-marker=' + " +
                        $"(Get-Content -LiteralPath '{PsLiteral(launchMarkerName)}' -Raw)"));
            Assert.Contains(
                $"initial-marker={launchMarkerValue}",
                await InvokeAsync(
                    sessions,
                    "exchange-online",
                    $"'initial-marker=' + " +
                        $"(Get-Content -LiteralPath '{PsLiteral(launchMarkerName)}' -Raw)"));

            var environmentName = $"PTK_SESSION_{Guid.NewGuid():N}";
            var setupOnPrem = await InvokeAsync(
                sessions,
                "exchange-onprem",
                SetupScript(
                    "onprem",
                    environmentName,
                    onPremDirectory,
                    onPremModule));
            var setupOnline = await InvokeAsync(
                sessions,
                "exchange-online",
                SetupScript(
                    "online",
                    environmentName,
                    onlineDirectory,
                    onlineModule));
            Assert.Contains("configured-onprem", setupOnPrem);
            Assert.Contains("configured-online", setupOnline);

            var onPremState = await InvokeAsync(
                sessions,
                "exchange-onprem",
                ProbeScript(
                    environmentName,
                    "OnPremOnly",
                    "OnlineOnly"));
            var onlineState = await InvokeAsync(
                sessions,
                "exchange-online",
                ProbeScript(
                    environmentName,
                    "OnlineOnly",
                    "OnPremOnly"));
            Assert.Contains("tag=onprem", onPremState);
            Assert.Contains("marker=onprem", onPremState);
            Assert.Contains($"env=onprem", onPremState);
            Assert.Contains($"cwd={onPremDirectory}", onPremState);
            Assert.Contains("own=True", onPremState);
            Assert.Contains("other=False", onPremState);
            Assert.Contains($"pid={onPrem.WorkerProcessId}", onPremState);
            Assert.DoesNotContain("tag=online", onPremState);

            Assert.Contains("tag=online", onlineState);
            Assert.Contains("marker=online", onlineState);
            Assert.Contains($"env=online", onlineState);
            Assert.Contains($"cwd={onlineDirectory}", onlineState);
            Assert.Contains("own=True", onlineState);
            Assert.Contains("other=False", onlineState);
            Assert.Contains($"pid={online.WorkerProcessId}", onlineState);
            Assert.DoesNotContain("tag=onprem", onlineState);

            await sessions.CloseAsync("exchange-onprem")
                .WaitAsync(CheckpointTimeout);
            await WaitForExitAsync(onPremWitness);
            Assert.False(onlineWitness.HasExited);
            var onlineAfterSiblingClose = await InvokeAsync(
                sessions,
                "exchange-online",
                "'still-online=' + (Get-Overlap)");
            Assert.Contains("still-online=online", onlineAfterSiblingClose);

            var reset = await sessions.ResetAsync("exchange-online")
                .WaitAsync(CheckpointTimeout);
            Assert.True(reset.WarmStateLost);
            Assert.NotEqual(online.WorkerProcessId, reset.WorkerProcessId);
            await WaitForExitAsync(onlineWitness);
            replacementWitness = OpenWitness(reset.WorkerProcessId!.Value);

            var resetState = await InvokeAsync(
                sessions,
                "exchange-online",
                "\"function=$([bool](Get-Command Get-Overlap -ErrorAction SilentlyContinue));" +
                "marker=$([bool](Get-Variable Marker -Scope Global -ErrorAction SilentlyContinue));" +
                $"env=$env:{environmentName};" +
                "module=$([bool](Get-Module OnlineOnly))\"");
            Assert.Contains("function=False", resetState);
            Assert.Contains("marker=False", resetState);
            Assert.Contains("env=", resetState);
            Assert.Contains("module=False", resetState);

            var activeMarker = Path.Combine(root, "active-entered");
            var active = sessions.InvokeAsync(
                "exchange-online",
                $"[IO.File]::WriteAllText('{PsLiteral(activeMarker)}', 'entered'); " +
                    "Start-Sleep -Seconds 300",
                raw: false,
                WorkerInvokeRoute.Pwsh,
                timeoutSeconds: 600,
                outputStore: null);
            await WaitForFileAsync(activeMarker);
            await sessions.ShutdownAsync().WaitAsync(CheckpointTimeout);
            _ = await Assert.ThrowsAnyAsync<Exception>(
                () => active);
            await WaitForExitAsync(replacementWitness);
            Assert.Empty(sessions.List());
        }
        finally
        {
            await sessions.ShutdownAsync();
            onPremWitness?.Dispose();
            onlineWitness?.Dispose();
            replacementWitness?.Dispose();
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Real_worker_tiny_output_publishes_a_readable_recovery_handle()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ptk-worker-output-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "named-session-process-output-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var brokerPath = await BuildBrokerAsync(root);
        var command = SessionWorkerLaunchCommand.Create();
        await using var sessions = new NamedSessionSupervisor(
            () => new ProcessSessionWorkerFactory(
                WorkerProcessLauncher.Create(brokerPath),
                command,
                Limits),
            startupTimeout: TimeSpan.FromSeconds(30),
            containmentGrace: TimeSpan.FromSeconds(3));
        using var outputStore = new OutputStore(new OutputStoreOptions(
            outputRoot,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            MaximumArtifactBytes: 1024,
            MaximumSessionBytes: 1024,
            MaximumAggregateBytes: 1024));

        try
        {
            await sessions.OpenAsync("output").WaitAsync(CheckpointTimeout);
            var token = $"tiny-{Guid.NewGuid():N}";
            var response = await sessions.InvokeAsync(
                    "output",
                    $"'{token}'",
                    raw: false,
                    WorkerInvokeRoute.Pwsh,
                    timeoutSeconds: 30,
                    outputStore)
                .WaitAsync(CheckpointTimeout);

            Assert.Equal(WorkerResultStatus.Completed, response.Result.Status);
            Assert.Contains(token, response.Result.Text, StringComparison.Ordinal);
            var handle = Assert.IsType<string>(response.OutputRecovery?.Handle);
            var recovered = outputStore.Read(
                handle,
                offset: 0,
                maximumBytes: OutputStore.MaximumReadBytes);
            Assert.Equal(OutputArtifactState.Available, recovered.State);
            Assert.Contains(token, recovered.Text, StringComparison.Ordinal);
        }
        finally
        {
            await sessions.ShutdownAsync();
            outputStore.Dispose();
            try { Directory.Delete(root, recursive: true); }
            catch { }
            try { Directory.Delete(outputRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Real_worker_crash_after_effect_is_not_replayed_and_preserves_its_sibling()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ptk-named-session-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "effect.txt");
        var brokerPath = await BuildBrokerAsync(root);
        var command = SessionWorkerLaunchCommand.Create();
        await using var sessions = new NamedSessionSupervisor(
            () => new ProcessSessionWorkerFactory(
                WorkerProcessLauncher.Create(brokerPath),
                command,
                Limits),
            startupTimeout: TimeSpan.FromSeconds(30),
            containmentGrace: TimeSpan.FromSeconds(3));

        Process? victimWitness = null;
        Process? siblingWitness = null;
        try
        {
            var victim = await sessions.OpenAsync("victim")
                .WaitAsync(CheckpointTimeout);
            var sibling = await sessions.OpenAsync("sibling")
                .WaitAsync(CheckpointTimeout);
            victimWitness = OpenWitness(victim.WorkerProcessId!.Value);
            siblingWitness = OpenWitness(sibling.WorkerProcessId!.Value);
            _ = await InvokeAsync(
                sessions,
                "sibling",
                "$global:SiblingMarker = 'still-warm'; 'configured'");

            var call = sessions.InvokeAsync(
                "victim",
                $"[IO.File]::AppendAllText('{PsLiteral(marker)}', 'once'); " +
                "while ($true) { Start-Sleep -Milliseconds 50 }",
                raw: false,
                WorkerInvokeRoute.Pwsh,
                timeoutSeconds: 600,
                outputStore: null);
            await WaitForFileContentAsync(marker, "once");
            victimWitness.Kill();
            await WaitForExitAsync(victimWitness);

            var failure = await Assert.ThrowsAsync<WorkerInvocationException>(
                () => call);
            Assert.Equal(
                WorkerInvocationDisposition.OutcomeUnknown,
                failure.Disposition);

            var replacement = await WaitForReplacementAsync(
                sessions,
                "victim",
                victim.WorkerProcessId.Value);
            Assert.True(replacement.WarmStateLost);
            Assert.NotEqual(victim.WorkerProcessId, replacement.WorkerProcessId);
            Assert.Equal("once", await File.ReadAllTextAsync(marker));
            Assert.False(siblingWitness.HasExited);
            Assert.Equal(
                sibling.WorkerProcessId,
                sessions.List().Single(item => item.Name == "sibling").WorkerProcessId);
            Assert.Equal(
                "still-warm",
                await InvokeAsync(
                    sessions,
                    "sibling",
                    "$global:SiblingMarker"));
        }
        finally
        {
            await sessions.ShutdownAsync();
            victimWitness?.Dispose();
            siblingWitness?.Dispose();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<string> InvokeAsync(
        NamedSessionSupervisor sessions,
        string name,
        string script)
    {
        var response = await sessions.InvokeAsync(
            name,
            script,
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            outputStore: null).WaitAsync(CheckpointTimeout);
        Assert.Equal(WorkerResultStatus.Completed, response.Result.Status);
        return response.Result.Text;
    }

    private static async Task<NamedSessionSnapshot> WaitForReplacementAsync(
        NamedSessionSupervisor sessions,
        string name,
        int previousProcessId)
    {
        var deadline = DateTimeOffset.UtcNow + CheckpointTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = sessions.List().Single(item => item.Name == name);
            if (snapshot.State == NamedSessionState.Ready &&
                snapshot.WorkerProcessId is { } processId &&
                processId != previousProcessId)
            {
                return snapshot;
            }
            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Timed out waiting for replacement of session '{name}'.");
    }

    private static string SetupScript(
        string marker,
        string environmentName,
        string directory,
        string modulePath) =>
        $"function global:Get-Overlap {{ '{marker}' }}; " +
        $"$global:Marker = '{marker}'; " +
        $"$env:{environmentName} = '{marker}'; " +
        $"Set-Location -LiteralPath '{PsLiteral(directory)}'; " +
        $"Import-Module -Name '{PsLiteral(modulePath)}' -Force; " +
        $"'configured-{marker}'";

    private static string ProbeScript(
        string environmentName,
        string ownModule,
        string otherModule) =>
        $"\"tag=$(& Get-Overlap);marker=$global:Marker;" +
        $"env=$env:{environmentName};cwd=$((Get-Location).Path);" +
        $"own=$([bool](Get-Module {ownModule}));" +
        $"other=$([bool](Get-Module {otherModule}));pid=$PID\"";

    private static string PsLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static async Task<string?> BuildBrokerAsync(string root)
    {
        if (OperatingSystem.IsWindows())
            return null;
        var source = FindBrokerSource();
        var output = Path.Combine(root, SessionWorkerLaunchCommand.UnixBrokerFileName);
        var start = new ProcessStartInfo
        {
            FileName = "cc",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "-std=c17", "-O2", "-fno-common", "-fstack-protector-strong",
            "-Wall", "-Wextra", "-Werror", "-Wpedantic", "-Wshadow",
            "-Wstrict-prototypes", "-Wmissing-prototypes",
            source, "-o", output,
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var compiler = Process.Start(start) ??
            throw new InvalidOperationException("The worker broker compiler did not start.");
        var standardOutput = compiler.StandardOutput.ReadToEndAsync();
        var standardError = compiler.StandardError.ReadToEndAsync();
        await compiler.WaitForExitAsync().WaitAsync(CheckpointTimeout);
        Assert.True(
            compiler.ExitCode == 0,
            $"Broker compile failed. stdout='{await standardOutput}' stderr='{await standardError}'");
        return output;
    }

    private static string FindBrokerSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "server",
                "PtkMcpServer",
                "Native",
                "ptk_worker_broker.c");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("The worker broker source is unavailable.");
    }

    private static Process OpenWitness(int processId)
    {
        var process = Process.GetProcessById(processId);
        try
        {
            _ = process.SafeHandle;
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static async Task WaitForExitAsync(Process process)
    {
        if (!process.HasExited)
            await process.WaitForExitAsync().WaitAsync(CheckpointTimeout);
        Assert.True(process.HasExited);
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow + CheckpointTimeout;
        while (!File.Exists(path) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(File.Exists(path), $"Timed out waiting for '{path}'.");
    }

    private static async Task WaitForFileContentAsync(
        string path,
        string expected)
    {
        var deadline = DateTimeOffset.UtcNow + CheckpointTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path) &&
                    string.Equals(
                        await File.ReadAllTextAsync(path),
                        expected,
                        StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for '{path}' to contain '{expected}'.");
    }
}
