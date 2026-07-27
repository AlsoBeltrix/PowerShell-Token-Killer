using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using PtkContainmentTestFixture;

namespace PtkMcpServer.Tests;

[Collection(ResilienceProcessCreationCollection.Name)]
public sealed class UnixWorkerBrokerParentDeathIntegrationTests : IDisposable
{
    private static readonly TimeSpan CheckpointTimeout =
        TimeSpan.FromSeconds(30);
    private readonly string _scratch =
        Directory.CreateTempSubdirectory(
            "ptk-worker-parent-death-").FullName;

    [Fact]
    public async Task Hard_supervisor_death_contains_two_worker_domains()
    {
        if (OperatingSystem.IsWindows())
            return;

        var broker = await CompileAsync(
            BrokerSourcePath(),
            "PtkWorkerBroker");
        var worker = await CompileAsync(
            WorkerFixtureSourcePath(),
            "PtkWorkerBrokerFixture");
        using var supervisor = StartSupervisor(broker, worker);
        var standardError = supervisor.StandardError.ReadToEndAsync();
        var ready = await ReadReadyAsync(supervisor.StandardOutput);

        Assert.Equal(supervisor.Id, ready.SupervisorPid);
        Assert.NotEqual(
            ready.FirstContainmentPid,
            ready.SecondContainmentPid);
        Assert.NotEqual(ready.FirstWorkerPid, ready.SecondWorkerPid);
        Assert.Equal(
            ready.FirstWorkerPid,
            ready.FirstWorkerPgid);
        Assert.Equal(
            ready.FirstWorkerPgid,
            ready.FirstDescendantPgid);
        Assert.Equal(
            ready.FirstWorkerPgid,
            ready.FirstGrandchildPgid);
        Assert.Equal(
            ready.SecondWorkerPid,
            ready.SecondWorkerPgid);
        Assert.Equal(
            ready.SecondWorkerPgid,
            ready.SecondDescendantPgid);
        Assert.Equal(
            ready.SecondWorkerPgid,
            ready.SecondGrandchildPgid);
        Assert.Equal(
            GetProcessGroup(supervisor.Id),
            GetProcessGroup(ready.FirstContainmentPid));
        Assert.Equal(
            GetProcessGroup(supervisor.Id),
            GetProcessGroup(ready.SecondContainmentPid));

        var ownedProcesses = ready.OwnedProcessIds.ToArray();
        Assert.All(ownedProcesses, processId =>
            Assert.True(ProcessExists(processId)));

        // This is deliberately a single-process hard kill. Job-handle close
        // on Windows and liveness-pipe EOF on Unix own descendant cleanup.
        supervisor.Kill();
        await supervisor.WaitForExitAsync().WaitAsync(CheckpointTimeout);

        foreach (var processId in ownedProcesses)
            await AssertProcessGoneAsync(processId);
        Assert.Equal(
            string.Empty,
            await standardError.WaitAsync(CheckpointTimeout));
    }

    [Fact]
    public void Reduced_fixture_and_product_source_bind_only_worker_topology()
    {
        var fixture = File.ReadAllText(WorkerFixtureSourcePath());
        var broker = File.ReadAllText(BrokerSourcePath());

        Assert.Contains(
            "worker_group != worker_pid",
            fixture,
            StringComparison.Ordinal);
        Assert.Contains(
            "descendant_group != worker_group",
            fixture,
            StringComparison.Ordinal);
        Assert.Contains(
            "grandchild_group != worker_group",
            fixture,
            StringComparison.Ordinal);
        Assert.Contains(
            "signal(SIGTERM, SIG_IGN)",
            fixture,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "guardian",
            fixture,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "private_host",
            fixture,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "#define PTK_LIVENESS_READ 3",
            broker,
            StringComparison.Ordinal);
        Assert.Contains(
            "setpgid(worker_pid, worker_pid)",
            broker,
            StringComparison.Ordinal);
        Assert.Contains(
            "kill(-worker_pid, signal_number)",
            broker,
            StringComparison.Ordinal);
        Assert.Contains(
            "PTK_CONTAINMENT_DEADLINE_MILLISECONDS",
            broker,
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

    private async Task<string> CompileAsync(
        string source,
        string outputName)
    {
        var outputPath = Path.Combine(_scratch, outputName);
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
            source, "-o", outputPath,
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var compiler = Process.Start(start) ??
            throw new InvalidOperationException(
                "The native fixture compiler did not start.");
        var output = compiler.StandardOutput.ReadToEndAsync();
        var error = compiler.StandardError.ReadToEndAsync();
        await compiler.WaitForExitAsync().WaitAsync(CheckpointTimeout);
        Assert.True(
            compiler.ExitCode == 0,
            $"Compile failed. stdout='{await output}' stderr='{await error}'");
        Assert.Equal(string.Empty, await output);
        Assert.Equal(string.Empty, await error);
        return outputPath;
    }

    private static Process StartSupervisor(
        string brokerPath,
        string workerPath)
    {
        var assembly = typeof(FixtureAssemblyMarker).Assembly.Location;
        var directory = Path.GetDirectoryName(assembly) ??
            throw new InvalidOperationException(
                "The fixture directory is unavailable.");
        var appHost = Path.Combine(
            directory,
            "PtkContainmentTestFixture");
        var start = new ProcessStartInfo
        {
            FileName = File.Exists(appHost)
                ? appHost
                : ResolveDotnetHost(),
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!File.Exists(appHost))
            start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("contained-supervisor");
        start.ArgumentList.Add(brokerPath);
        start.ArgumentList.Add(workerPath);
        return Process.Start(start) ??
            throw new InvalidOperationException(
                "The contained supervisor fixture did not start.");
    }

    private static async Task<ReadySnapshot> ReadReadyAsync(
        StreamReader reader)
    {
        using var cancellation = new CancellationTokenSource(
            CheckpointTimeout);
        var line = await reader.ReadLineAsync(cancellation.Token) ??
            throw new EndOfStreamException(
                "The contained supervisor exited before readiness.");
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        return new ReadySnapshot(
            root.GetProperty("supervisorPid").GetInt32(),
            root.GetProperty("firstContainmentPid").GetInt32(),
            root.GetProperty("firstWorkerPid").GetInt32(),
            root.GetProperty("firstWorkerPgid").GetInt32(),
            root.GetProperty("firstDescendantPid").GetInt32(),
            root.GetProperty("firstDescendantPgid").GetInt32(),
            root.GetProperty("firstGrandchildPid").GetInt32(),
            root.GetProperty("firstGrandchildPgid").GetInt32(),
            root.GetProperty("secondContainmentPid").GetInt32(),
            root.GetProperty("secondWorkerPid").GetInt32(),
            root.GetProperty("secondWorkerPgid").GetInt32(),
            root.GetProperty("secondDescendantPid").GetInt32(),
            root.GetProperty("secondDescendantPgid").GetInt32(),
            root.GetProperty("secondGrandchildPid").GetInt32(),
            root.GetProperty("secondGrandchildPgid").GetInt32());
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
        var inferred = Path.Combine(root.FullName, "dotnet");
        return File.Exists(inferred)
            ? inferred
            : throw new FileNotFoundException(
                "The dotnet host is unavailable.",
                inferred);
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

    private static string WorkerFixtureSourcePath(
        [CallerFilePath] string testSourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourcePath) ??
                throw new InvalidOperationException(
                    "The test source directory is unavailable."),
            "Native",
            "ptk_guardian_broker_fixture.c"));

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetProcessGroup(int processId);

    private sealed record ReadySnapshot(
        int SupervisorPid,
        int FirstContainmentPid,
        int FirstWorkerPid,
        int FirstWorkerPgid,
        int FirstDescendantPid,
        int FirstDescendantPgid,
        int FirstGrandchildPid,
        int FirstGrandchildPgid,
        int SecondContainmentPid,
        int SecondWorkerPid,
        int SecondWorkerPgid,
        int SecondDescendantPid,
        int SecondDescendantPgid,
        int SecondGrandchildPid,
        int SecondGrandchildPgid)
    {
        internal IEnumerable<int> OwnedProcessIds =>
        [
            FirstContainmentPid,
            FirstWorkerPid,
            FirstDescendantPid,
            FirstGrandchildPid,
            SecondContainmentPid,
            SecondWorkerPid,
            SecondDescendantPid,
            SecondGrandchildPid,
        ];
    }
}
