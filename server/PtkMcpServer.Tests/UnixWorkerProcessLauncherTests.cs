using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PtkContainmentTestFixture;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

[Collection(ResilienceProcessCreationCollection.Name)]
public sealed class UnixWorkerProcessLauncherTests : IDisposable
{
    private static readonly TimeSpan CheckpointTimeout =
        TimeSpan.FromSeconds(30);
    private static readonly WorkerProtocolLimits Limits =
        WorkerOperationProtocol.CreateLimits(
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1));
    private readonly string _scratch =
        Directory.CreateTempSubdirectory("ptk-unix-worker-").FullName;

    [Fact]
    public void Native_source_freezes_worker_only_liveness_and_grace()
    {
        var source = File.ReadAllText(BrokerSourcePath());
        var launcher = File.ReadAllText(LauncherSourcePath());

        Assert.Matches(
            new Regex(
                @"#define\s+PTK_TERM_TO_KILL_MILLISECONDS\s+UINT64_C\(2000\)",
                RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(
                @"#define\s+PTK_CONTAINMENT_DEADLINE_MILLISECONDS\s+UINT64_C\(10000\)",
                RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(
                @"#define\s+PTK_POLL_MILLISECONDS\s+25\b",
                RegexOptions.CultureInvariant),
            source);
        Assert.Contains(
            "poll(descriptors, 2U, -1)",
            source,
            StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"clock_gettime\s*\(\s*CLOCK_MONOTONIC[\s\S]*?" +
                    @"_exit\s*\(\s*74\s*\)",
                RegexOptions.CultureInvariant),
            source);
        Assert.Contains(
            "setpgid(worker_pid, worker_pid)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "kill(-worker_pid, signal_number)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"\b(?:setsid|setpgrp)\s*\("),
            source);
        Assert.DoesNotContain("guardian", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private_host", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "TaskCompletionSource _containmentEmpty",
            launcher,
            StringComparison.Ordinal);
        Assert.Contains(
            "WaitAsync(BrokerContainmentTimeout)",
            launcher,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_containmentEmpty = Task.CompletedTask",
            launcher,
            StringComparison.Ordinal);
        Assert.Contains(
            "TaskCreationOptions.LongRunning",
            launcher,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Task.Run(",
            launcher,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Finding i13-2: the Unix worker exit code must stay absent. The only
    /// status observable here is the broker's, and the broker reports its own
    /// containment outcome — a fixed 64 once it reaps the worker
    /// (`monitor_worker`) — never the child's. Publishing that as "the worker's
    /// exit code" made every Unix death claim the same wrong number.
    /// </summary>
    /// <remarks>
    /// Asserted against the source, like the other invariants in this class,
    /// because the behavioural tests here skip on Windows and a scripted
    /// in-memory fake cannot fail when the real launcher regresses — the
    /// reviewer proved the first attempt at this guard vacuous for exactly that
    /// reason. Restoring `_brokerExit.Result` fails this test on every
    /// platform.
    /// </remarks>
    [Fact]
    public void Unix_reports_no_worker_exit_code_because_only_the_brokers_is_observable()
    {
        var launcher = File.ReadAllText(LauncherSourcePath());
        var broker = File.ReadAllText(BrokerSourcePath());

        Assert.Matches(
            new Regex(
                @"public\s+int\?\s+ExitCode\s*=>\s*null\s*;",
                RegexOptions.CultureInvariant),
            launcher);
        Assert.DoesNotMatch(
            new Regex(
                @"ExitCode\s*=>[\s\S]{0,200}?_brokerExit",
                RegexOptions.CultureInvariant),
            launcher);
        // The premise: the broker returns its own constant after reaping, so
        // there is no worker status to relay yet. If this stops holding, the
        // finding's reasoning needs revisiting before the property changes.
        Assert.Matches(
            new Regex(
                @"if\s*\(worker_reaped\)\s*\{[\s\S]{0,400}?return\s+contain_worker\(" +
                    @"[\s\S]{0,120}?\?\s*64",
                RegexOptions.CultureInvariant),
            broker);
    }

    [Fact]
    public void Confirmed_registry_proof_is_published_before_containment_returns()
    {
        var launcher = File.ReadAllText(LauncherSourcePath());

        Assert.Matches(
            new Regex(
                @"var\s+registryContainmentEmpty\s*=\s*" +
                    @"_registry\.WaitForEmptyAsync\(identity\);\s*" +
                    @"_registryContainmentEmpty\s*=\s*registryContainmentEmpty;\s*" +
                    @"_\s*=\s*ForwardContainmentEmptyAsync\(" +
                    @"registryContainmentEmpty\);",
                RegexOptions.CultureInvariant),
            launcher);
        Assert.Matches(
            new Regex(
                @"result\.Outcome\s*==\s*" +
                    @"WorkerContainmentOutcome\.ConfirmedEmpty\s*&&\s*" +
                    @"_registryContainmentEmpty\s+is\s*" +
                    @"\{\s*IsCompletedSuccessfully:\s*true\s*\}" +
                    @"[\s\S]*?_containmentEmpty\.TrySetResult\(\);" +
                    @"[\s\S]*?return result;",
                RegexOptions.CultureInvariant),
            launcher);
    }

    [Fact]
    public async Task Production_broker_runs_real_worker_entry_and_warm_runtime()
    {
        if (OperatingSystem.IsWindows())
            return;

        var brokerPath = await CompileBrokerAsync();
        var registry = new UnixWorkerContainmentRegistry();
        using var contained = await new UnixWorkerProcessLauncher(
            brokerPath,
            registry).LaunchAsync(
                CreateServerCommand(
                    new(
                        "PTK_ENVIRONMENT_CASE_PROBE",
                        "uppercase"),
                    new(
                        "ptk_environment_case_probe",
                        "lowercase")));
        var standardOutput = DrainAsync(contained.StandardOutputReader);
        var standardError = DrainAsync(contained.StandardErrorReader);
        var reader = new WorkerProtocolReader(contained.EventReader);
        var writer = new WorkerProtocolWriter(contained.RequestWriter);
        var sessionId = Guid.NewGuid();
        const long incarnation = 9;

        await writer.WriteAsync(
            WorkerOperationProtocol.CreateInitializeEnvelope(
                sessionId,
                incarnation,
                1,
                DateTimeOffset.UtcNow.AddMinutes(1),
                Limits));
        var ready = await ReadEnvelopeAsync(reader);
        Assert.NotNull(ready);
        Assert.Equal(WorkerMessageKind.Ready, ready.Kind);
        Assert.Equal(
            Limits,
            WorkerOperationProtocol.ParseReady(
                ready,
                sessionId,
                incarnation,
                1));

        await writer.WriteAsync(
            WorkerOperationProtocol.CreateStateQueryEnvelope(
                sessionId,
                incarnation,
                2,
                listAvailable: false));
        var stateEnvelope = await ReadEnvelopeAsync(reader);
        Assert.NotNull(stateEnvelope);
        var state = WorkerOperationProtocol.ParseStateSnapshot(
            stateEnvelope,
            sessionId,
            incarnation);
        Assert.True(state.Available);
        Assert.Equal(2, state.RequestId);

        await writer.WriteAsync(
            WorkerOperationProtocol.CreateEmptyEnvelope(
                WorkerMessageKind.Shutdown,
                sessionId,
                incarnation,
                3));
        var stopped = await ReadEnvelopeAsync(reader);
        Assert.NotNull(stopped);
        WorkerOperationProtocol.ParseEmpty(
            stopped,
            WorkerMessageKind.Stopped,
            sessionId,
            incarnation);

        await contained.WaitForExitAsync().WaitAsync(CheckpointTimeout);
        var result = await contained.ContainAsync(
            WorkerContainmentReason.Close);
        Assert.Equal(
            WorkerContainmentOutcome.ConfirmedEmpty,
            result.Outcome);
        Assert.True(contained.ContainmentEmpty.IsCompletedSuccessfully);
        await contained.ContainmentEmpty.WaitAsync(CheckpointTimeout);
        Assert.Empty(await standardOutput.WaitAsync(CheckpointTimeout));
        Assert.Empty(await standardError.WaitAsync(CheckpointTimeout));
    }

    [Theory]
    [InlineData((int)WorkerContainmentReason.Close)]
    [InlineData((int)WorkerContainmentReason.Reset)]
    [InlineData((int)WorkerContainmentReason.Timeout)]
    public async Task Two_domains_contain_independently(int reasonValue)
    {
        if (OperatingSystem.IsWindows())
            return;

        var brokerPath = await CompileBrokerAsync();
        var firstLauncher = WorkerProcessLauncher.Create(brokerPath);
        var secondLauncher = WorkerProcessLauncher.Create(brokerPath);
        IWorkerContainedProcess? first = null;
        IWorkerContainedProcess? second = null;
        try
        {
            first = await firstLauncher.LaunchAsync(
                CreateFixtureCommand("contained-worker"));
            second = await secondLauncher.LaunchAsync(
                CreateFixtureCommand("contained-worker"));
            var firstTree = await ReadTreeAsync(first.StandardOutputReader);
            var secondTree = await ReadTreeAsync(second.StandardOutputReader);

            Assert.Equal(first.ProcessId, firstTree.WorkerPid);
            Assert.Equal(firstTree.WorkerPid, firstTree.WorkerPgid);
            Assert.Equal(
                firstTree.WorkerPgid,
                firstTree.DescendantPgid);
            Assert.Equal(
                firstTree.WorkerPgid,
                firstTree.GrandchildPgid);
            Assert.Equal(second.ProcessId, secondTree.WorkerPid);
            Assert.NotEqual(first.ProcessId, second.ProcessId);
            Assert.True(ProcessExists(firstTree.DescendantPid));
            Assert.True(ProcessExists(firstTree.GrandchildPid));
            Assert.True(ProcessExists(secondTree.DescendantPid));
            Assert.True(ProcessExists(secondTree.GrandchildPid));

            var reason = (WorkerContainmentReason)reasonValue;
            var firstResult = await first.ContainAsync(reason);
            Assert.Equal(
                WorkerContainmentOutcome.ConfirmedEmpty,
                firstResult.Outcome);
            Assert.True(first.ContainmentEmpty.IsCompletedSuccessfully);
            await first.ContainmentEmpty.WaitAsync(CheckpointTimeout);
            await AssertProcessGoneAsync(firstTree.WorkerPid);
            await AssertProcessGoneAsync(firstTree.DescendantPid);
            await AssertProcessGoneAsync(firstTree.GrandchildPid);
            Assert.True(ProcessExists(secondTree.WorkerPid));
            Assert.True(ProcessExists(secondTree.DescendantPid));
            Assert.True(ProcessExists(secondTree.GrandchildPid));

            var secondResult = await second.ContainAsync(reason);
            Assert.Equal(
                WorkerContainmentOutcome.ConfirmedEmpty,
                secondResult.Outcome);
            Assert.True(second.ContainmentEmpty.IsCompletedSuccessfully);
            await second.ContainmentEmpty.WaitAsync(CheckpointTimeout);
            await AssertProcessGoneAsync(secondTree.WorkerPid);
            await AssertProcessGoneAsync(secondTree.DescendantPid);
            await AssertProcessGoneAsync(secondTree.GrandchildPid);
        }
        finally
        {
            await ContainBestEffortAsync(first);
            await ContainBestEffortAsync(second);
            first?.Dispose();
            second?.Dispose();
        }
    }

    [Fact]
    public async Task Direct_worker_death_triggers_broker_containment()
    {
        if (OperatingSystem.IsWindows())
            return;

        var brokerPath = await CompileBrokerAsync();
        var registry = new UnixWorkerContainmentRegistry();
        IWorkerContainedProcess? contained = null;
        TreeSnapshot? tree = null;
        try
        {
            contained = await new UnixWorkerProcessLauncher(
                brokerPath,
                registry).LaunchAsync(
                    CreateFixtureCommand("contained-worker"));
            tree = await ReadTreeAsync(contained.StandardOutputReader);
            Assert.Equal(tree.WorkerPid, tree.WorkerPgid);
            Assert.Equal(tree.WorkerPgid, tree.DescendantPgid);
            Assert.Equal(tree.WorkerPgid, tree.GrandchildPgid);

            using (var worker = Process.GetProcessById(tree.WorkerPid))
            {
                worker.Kill();
                await worker.WaitForExitAsync().WaitAsync(CheckpointTimeout);
            }

            await contained.WaitForExitAsync().WaitAsync(CheckpointTimeout);
            await AssertProcessGoneAsync(tree.DescendantPid);
            await AssertProcessGoneAsync(tree.GrandchildPid);
            await AssertProcessGoneAsync(contained.ContainmentProcessId);

            var result = await contained.ContainAsync(
                WorkerContainmentReason.LaunchFailure);
            Assert.Equal(
                WorkerContainmentOutcome.ConfirmedEmpty,
                result.Outcome);
            await contained.ContainmentEmpty.WaitAsync(CheckpointTimeout);
        }
        finally
        {
            if (tree is not null)
            {
                KillProcessBestEffort(tree.DescendantPid);
                KillProcessBestEffort(tree.GrandchildPid);
            }
            await ContainBestEffortAsync(contained);
            contained?.Dispose();
            registry.Dispose();
        }
    }

    [Fact]
    public async Task Process_group_escape_is_truthfully_unknown_and_blocks_reuse()
    {
        if (OperatingSystem.IsWindows())
            return;

        var brokerPath = await CompileBrokerAsync();
        var gatePath = Path.Combine(_scratch, "escape.gate");
        var registry = new UnixWorkerContainmentRegistry();
        var launcher = new SingleDomainWorkerProcessLauncher(
            new UnixWorkerProcessLauncher(
                brokerPath,
                registry));
        IWorkerContainedProcess? escaped = null;
        IWorkerContainedProcess? replacement = null;
        TreeSnapshot? tree = null;
        try
        {
            escaped = await launcher.LaunchAsync(
                CreateFixtureCommand(
                    "contained-escape-worker",
                    gatePath));
            tree = await ReadTreeAsync(escaped.StandardOutputReader);
            Assert.Equal(tree.WorkerPgid, tree.DescendantPgid);
            Assert.Equal(tree.WorkerPgid, tree.GrandchildPgid);
            await WaitUntilAsync(
                () => registry.HealthyObservationCount > 0);

            File.WriteAllText(gatePath, "escape", new UTF8Encoding(false));
            await WaitForFileAsync(gatePath + ".escaped");
            await WaitUntilAsync(() => registry.EscapeObserved);

            var result = await escaped.ContainAsync(
                WorkerContainmentReason.Timeout);
            Assert.Equal(
                WorkerContainmentOutcome.DescendantsUnknown,
                result.Outcome);
            Assert.Equal("descendants_unknown", result.DetailCode);
            Assert.True(ProcessExists(tree.DescendantPid));
            await AssertProcessGoneAsync(tree.GrandchildPid);
            Assert.False(escaped.ContainmentEmpty.IsCompleted);

            var blocked = await Assert.ThrowsAsync<WorkerProcessException>(
                async () => await launcher.LaunchAsync(
                    CreateFixtureCommand("contained-worker")));
            Assert.Equal(
                "previous_containment_unconfirmed",
                blocked.DetailCode);

            KillProcessBestEffort(tree.DescendantPid);
            await escaped.ContainmentEmpty.WaitAsync(CheckpointTimeout);
            replacement = await launcher.LaunchAsync(
                CreateFixtureCommand("contained-worker"));
            var replacementTree = await ReadTreeAsync(
                replacement.StandardOutputReader);
            var replacementResult = await replacement.ContainAsync(
                WorkerContainmentReason.Close);
            Assert.Equal(
                WorkerContainmentOutcome.ConfirmedEmpty,
                replacementResult.Outcome);
            Assert.True(replacement.ContainmentEmpty.IsCompletedSuccessfully);
            await AssertProcessGoneAsync(replacementTree.DescendantPid);
            await AssertProcessGoneAsync(replacementTree.GrandchildPid);
        }
        finally
        {
            if (tree is not null)
            {
                KillProcessBestEffort(tree.DescendantPid);
                KillProcessBestEffort(tree.GrandchildPid);
            }
            await ContainBestEffortAsync(escaped);
            await ContainBestEffortAsync(replacement);
            escaped?.Dispose();
            replacement?.Dispose();
            registry.Dispose();
        }
    }

    [Fact]
    public void Worker_mode_uses_only_the_broker_owned_group()
    {
        var source = File.ReadAllText(ProcessTreeContainmentSourcePath());
        Assert.Contains(
            "EnterWorkerOwnedGroupMode()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "The Unix worker is not its broker-owned process-group leader.",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TryAcquireExclusiveGroup",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PollLoopAsync",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "setpgid(0, 0)",
            source,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch
        {
        }
    }

    private async Task<string> CompileBrokerAsync()
    {
        var outputPath = Path.Combine(
            _scratch,
            $"PtkWorkerBroker-{Guid.NewGuid():N}");
        var start = new ProcessStartInfo
        {
            FileName = "/usr/bin/cc",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "-std=c17", "-O2", "-fno-common", "-fstack-protector-strong",
            "-Wall", "-Wextra", "-Werror", "-Wpedantic", "-Wshadow",
            "-Wstrict-prototypes", "-Wmissing-prototypes",
            BrokerSourcePath(), "-o", outputPath,
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var compiler = Process.Start(start) ??
            throw new InvalidOperationException(
                "The native broker compiler did not start.");
        var output = compiler.StandardOutput.ReadToEndAsync();
        var error = compiler.StandardError.ReadToEndAsync();
        await compiler.WaitForExitAsync().WaitAsync(CheckpointTimeout);
        Assert.True(
            compiler.ExitCode == 0,
            $"Broker compile failed. stdout='{await output}' stderr='{await error}'");
        Assert.Equal(string.Empty, await output);
        Assert.Equal(string.Empty, await error);
        return outputPath;
    }

    private static WorkerLaunchCommand CreateServerCommand(
        params KeyValuePair<string, string>[] additionalEnvironment)
    {
        var serverAssembly = typeof(WorkerServer).Assembly.Location;
        var directory = Path.GetDirectoryName(serverAssembly) ??
            throw new InvalidOperationException(
                "The server assembly directory is unavailable.");
        return new WorkerLaunchCommand(
            ResolveDotnetHost(),
            ["exec", serverAssembly, "--worker"],
            directory,
            CaptureEnvironment().Concat(additionalEnvironment));
    }

    private static WorkerLaunchCommand CreateFixtureCommand(
        params string[] arguments)
    {
        var assembly = typeof(FixtureAssemblyMarker).Assembly.Location;
        var directory = Path.GetDirectoryName(assembly) ??
            throw new InvalidOperationException(
                "The fixture directory is unavailable.");
        var appHost = Path.Combine(
            directory,
            OperatingSystem.IsWindows()
                ? "PtkContainmentTestFixture.exe"
                : "PtkContainmentTestFixture");
        return File.Exists(appHost)
            ? new WorkerLaunchCommand(
                appHost,
                arguments,
                directory,
                CaptureEnvironment())
            : new WorkerLaunchCommand(
                ResolveDotnetHost(),
                [assembly, .. arguments],
                directory,
                CaptureEnvironment());
    }

    private static async Task<TreeSnapshot> ReadTreeAsync(Stream stream)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        using var cancellation = new CancellationTokenSource(
            CheckpointTimeout);
        var line = await reader.ReadLineAsync(cancellation.Token) ??
            throw new EndOfStreamException(
                "The contained fixture did not report its process tree.");
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        return new TreeSnapshot(
            root.GetProperty("workerPid").GetInt32(),
            root.GetProperty("workerPgid").GetInt32(),
            root.GetProperty("descendantPid").GetInt32(),
            root.GetProperty("descendantPgid").GetInt32(),
            root.GetProperty("grandchildPid").GetInt32(),
            root.GetProperty("grandchildPgid").GetInt32());
    }

    private static async Task<WorkerEnvelope?> ReadEnvelopeAsync(
        WorkerProtocolReader reader)
    {
        using var cancellation = new CancellationTokenSource(
            CheckpointTimeout);
        return await reader.ReadAsync(cancellation.Token);
    }

    private static async Task<byte[]> DrainAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private static string ResolveDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable(
            "DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) &&
            Path.IsPathFullyQualified(configured) &&
            File.Exists(configured))
        {
            return configured;
        }

        var runtime = new DirectoryInfo(
            RuntimeEnvironment.GetRuntimeDirectory());
        var root = runtime.Parent?.Parent?.Parent ??
            throw new InvalidOperationException(
                "The dotnet host directory is unavailable.");
        var executable = OperatingSystem.IsWindows()
            ? "dotnet.exe"
            : "dotnet";
        var inferred = Path.Combine(root.FullName, executable);
        return File.Exists(inferred)
            ? inferred
            : throw new FileNotFoundException(
                "The dotnet host is unavailable.",
                inferred);
    }

    private static IEnumerable<KeyValuePair<string, string>>
        CaptureEnvironment()
    {
        foreach (DictionaryEntry entry in
                 Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key ||
                entry.Value is not string value ||
                key.Contains('=') ||
                WorkerBootstrapEnvironment.ReservedHandleVariables.Contains(
                    key))
            {
                continue;
            }
            yield return new KeyValuePair<string, string>(key, value);
        }
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task AssertProcessGoneAsync(int processId)
    {
        var deadline = DateTimeOffset.UtcNow + CheckpointTimeout;
        while (ProcessExists(processId) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
        Assert.False(ProcessExists(processId));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + CheckpointTimeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private static async Task WaitForFileAsync(string path)
    {
        await WaitUntilAsync(() => File.Exists(path));
    }

    private static void KillProcessBestEffort(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }
    }

    private static async Task ContainBestEffortAsync(
        IWorkerContainedProcess? process)
    {
        if (process is null)
            return;
        try
        {
            await process.ContainAsync(
                WorkerContainmentReason.SupervisorShutdown);
        }
        catch
        {
        }
    }

    private static string BrokerSourcePath(
        [CallerFilePath] string testSourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourcePath) ??
                throw new InvalidOperationException(
                    "The test source directory is unavailable."),
            "..",
            "PtkMcpServer",
            "Native",
            "ptk_worker_broker.c"));

    private static string ProcessTreeContainmentSourcePath(
        [CallerFilePath] string testSourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourcePath) ??
                throw new InvalidOperationException(
                    "The test source directory is unavailable."),
            "..",
            "PtkMcpServer",
            "Execution",
            "ProcessTreeContainment.cs"));

    private static string LauncherSourcePath(
        [CallerFilePath] string testSourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourcePath) ??
                throw new InvalidOperationException(
                    "The test source directory is unavailable."),
            "..",
            "PtkMcpServer",
            "Worker",
            "UnixWorkerProcessLauncher.cs"));

    private sealed record TreeSnapshot(
        int WorkerPid,
        int WorkerPgid,
        int DescendantPid,
        int DescendantPgid,
        int GrandchildPid,
        int GrandchildPgid);
}
