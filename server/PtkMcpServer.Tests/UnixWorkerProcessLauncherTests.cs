using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

[Collection(ResilienceProcessCreationCollection.Name)]
public sealed class UnixWorkerProcessLauncherTests
{
    private static readonly TimeSpan CheckpointTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Production_broker_launches_real_worker_only_after_both_registry_acks()
    {
        if (OperatingSystem.IsWindows())
            return;

        var scratch = Directory.CreateTempSubdirectory("ptk-worker-broker-").FullName;
        var brokerPath = Path.Combine(scratch, "PtkContainmentBroker");
        WorkerProcessClient? worker = null;
        try
        {
            await CompileBrokerAsync(brokerPath);
            var registry = new RecordingRegistry();
            worker = await WorkerProcessClient.LaunchAsync(
                new UnixWorkerProcessLauncher(brokerPath, registry),
                ServerCommand(),
                generation: 53,
                DateTimeOffset.UtcNow.AddMinutes(2),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(53, worker.Generation);
            Assert.NotEqual(Guid.Empty, worker.WorkerBootId);
            Assert.Equal(
                new[] { "pending", "armed" },
                registry.Calls);
            Assert.NotNull(registry.Identity);
            Assert.Equal(worker.ProcessId, registry.Identity.WorkerProcessId);
            Assert.Equal(
                registry.Identity.WorkerProcessId,
                registry.Identity.WorkerProcessGroup);

            var response = await worker.Client.ExecuteAsync(
                WorkerSessionOperationCodec.StateOperation,
                new WorkerStateArguments(ListAvailable: false),
                DateTimeOffset.UtcNow.AddMinutes(1),
                TestContext.Current.CancellationToken);
            Assert.Equal(WorkerOperationStatus.Completed, response.Status);

            await worker.ShutdownAsync(TestContext.Current.CancellationToken);
            var diagnostics = await worker.Diagnostics.WaitAsync(CheckpointTimeout);
            Assert.Equal(
                new[] { "pending", "armed", "remove" },
                registry.Calls);
            Assert.Equal(0, diagnostics.StandardOutput.TotalBytes);
            Assert.Equal(0, diagnostics.StandardError.TotalBytes);
            Assert.False(worker.Fatal.IsCompleted);
        }
        finally
        {
            if (worker is not null)
                await worker.DisposeAsync();
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void Broker_protocol_rejects_wrong_payload_length()
    {
        var header = new byte[UnixWorkerBrokerProtocol.HeaderBytes];
        UnixWorkerBrokerProtocol.WriteHeader(
            header,
            (byte)UnixWorkerBrokerEventKind.Hello,
            payloadLength: 19);

        var exception = Assert.Throws<WorkerProcessException>(() =>
            UnixWorkerBrokerProtocol.ParseEvent(header, new byte[19]));

        Assert.Equal("unix_worker_broker_protocol_invalid", exception.DetailCode);
    }

    [Fact]
    public async Task Broker_shutdown_contains_worker_process_group_descendants()
    {
        if (OperatingSystem.IsWindows())
            return;

        var scratch = Directory.CreateTempSubdirectory("ptk-worker-containment-").FullName;
        var brokerPath = Path.Combine(scratch, "PtkContainmentBroker");
        IWorkerContainedProcess? contained = null;
        try
        {
            await CompileBrokerAsync(brokerPath);
            var registry = new RecordingRegistry();
            contained = await new UnixWorkerProcessLauncher(
                brokerPath,
                registry).LaunchAsync(
                    DescendantCommand(),
                    TestContext.Current.CancellationToken);
            using var output = new StreamReader(contained.StandardOutputReader);
            var childLine = await output.ReadLineAsync(
                TestContext.Current.CancellationToken);
            Assert.True(
                int.TryParse(childLine, out var childProcessId) &&
                childProcessId > 0);
            var workerProcessId = contained.ProcessId;
            Assert.True(ProcessExists(workerProcessId));
            Assert.True(ProcessExists(childProcessId));

            await contained.ContainAsync().WaitAsync(CheckpointTimeout);

            Assert.False(ProcessExists(workerProcessId));
            Assert.False(ProcessExists(childProcessId));
            Assert.Equal(
                new[] { "pending", "armed", "remove" },
                registry.Calls);
        }
        finally
        {
            contained?.Dispose();
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task Outer_term_preserves_the_broker_but_not_its_worker()
    {
        if (OperatingSystem.IsWindows())
            return;

        var scratch = Directory.CreateTempSubdirectory("ptk-worker-outer-term-").FullName;
        var brokerPath = Path.Combine(scratch, "PtkContainmentBroker");
        IWorkerContainedProcess? contained = null;
        try
        {
            await CompileBrokerAsync(brokerPath);
            contained = await new UnixWorkerProcessLauncher(
                brokerPath,
                new RecordingRegistry()).LaunchAsync(
                    IdleCommand(),
                    TestContext.Current.CancellationToken);
            var brokerField = contained.GetType().GetField(
                "_brokerProcessId",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(brokerField);
            var brokerProcessId = Assert.IsType<int>(
                brokerField.GetValue(contained));
            var workerProcessId = contained.ProcessId;

            Assert.Equal(0, KillProcess(brokerProcessId, TermSignal));
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.True(ProcessExists(brokerProcessId));

            Assert.Equal(0, KillProcess(workerProcessId, TermSignal));
            await contained.WaitForExitAsync(
                TestContext.Current.CancellationToken).WaitAsync(CheckpointTimeout);
            await AssertProcessGoneAsync(workerProcessId);
            Assert.True(ProcessExists(brokerProcessId));

            await contained.ContainAsync().WaitAsync(CheckpointTimeout);
            Assert.False(ProcessExists(brokerProcessId));
        }
        finally
        {
            contained?.Dispose();
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task CompileBrokerAsync(string outputPath)
    {
        const string compiler = "/usr/bin/cc";
        Assert.True(File.Exists(compiler));
        var startInfo = new ProcessStartInfo
        {
            FileName = compiler,
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
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Native broker compiler did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(CheckpointTimeout);
        Assert.True(
            process.ExitCode == 0,
            $"Broker compile failed. stdout='{await output}' stderr='{await error}'");
        Assert.Equal(string.Empty, await output);
        Assert.Equal(string.Empty, await error);
    }

    private static WorkerLaunchCommand ServerCommand()
    {
        var serverAssembly = typeof(WorkerServer).Assembly.Location;
        var serverDirectory = Path.GetDirectoryName(serverAssembly) ??
            throw new InvalidOperationException("Server assembly directory is unavailable.");
        return new WorkerLaunchCommand(
            ResolveDotnetHost(),
            ["exec", serverAssembly, "--worker"],
            serverDirectory,
            CaptureCurrentEnvironment());
    }

    private static WorkerLaunchCommand DescendantCommand() =>
        new(
            "/bin/sh",
            [
                "-c",
                "/bin/sh -c 'trap \"\" HUP TERM; printf \"%d\\n\" \"$$\"; " +
                "while :; do sleep 30; done' ptk-worker-descendant-r6 & wait",
            ],
            Path.GetFullPath("/"),
            CaptureCurrentEnvironment());

    private static WorkerLaunchCommand IdleCommand() =>
        new(
            "/bin/sh",
            ["-c", "while :; do sleep 30; done"],
            Path.GetFullPath("/"),
            CaptureCurrentEnvironment());

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
    }

    private static async Task AssertProcessGoneAsync(int processId)
    {
        var deadline = DateTimeOffset.UtcNow + CheckpointTimeout;
        while (ProcessExists(processId) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.False(ProcessExists(processId));
    }

    private static string ResolveDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) &&
            Path.IsPathFullyQualified(configured) &&
            File.Exists(configured))
        {
            return configured;
        }

        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var root = runtime.Parent?.Parent?.Parent ??
            throw new InvalidOperationException("The dotnet host directory is unavailable.");
        var inferred = Path.Combine(root.FullName, "dotnet");
        return File.Exists(inferred)
            ? inferred
            : throw new FileNotFoundException("The dotnet host is unavailable.", inferred);
    }

    private static IEnumerable<KeyValuePair<string, string>> CaptureCurrentEnvironment()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key ||
                entry.Value is not string value ||
                key.Contains('=') ||
                WorkerBootstrapEnvironment.ReservedHandleVariables.Contains(key))
            {
                continue;
            }
            yield return new KeyValuePair<string, string>(key, value);
        }
    }

    private static string BrokerSourcePath(
        [CallerFilePath] string testSourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourcePath) ??
                throw new InvalidOperationException("Test source directory is unavailable."),
            "..",
            "PtkMcpGuardian",
            "Native",
            "ptk_containment_broker.c"));

    private const int TermSignal = 15;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillProcess(int processId, int signal);

    private sealed class RecordingRegistry : IUnixWorkerContainmentRegistry
    {
        internal List<string> Calls { get; } = [];
        internal UnixWorkerContainmentIdentity Identity { get; private set; } = null!;

        public ValueTask RegisterPendingAsync(
            UnixWorkerContainmentIdentity identity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Identity = identity;
            Calls.Add("pending");
            return ValueTask.CompletedTask;
        }

        public ValueTask RegisterArmedAsync(
            UnixWorkerContainmentIdentity identity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Same(Identity, identity);
            Calls.Add("armed");
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(
            UnixWorkerContainmentIdentity identity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Same(Identity, identity);
            Calls.Add("remove");
            return ValueTask.CompletedTask;
        }
    }
}
