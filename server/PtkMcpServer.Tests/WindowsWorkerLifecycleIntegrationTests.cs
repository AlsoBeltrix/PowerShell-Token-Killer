using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

[Collection(WindowsProcessCreationCollection.Name)]
public sealed class WindowsWorkerLifecycleIntegrationTests
{
    private static readonly TimeSpan CheckpointTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Contained_worker_completes_lifecycle_with_silent_diagnostics()
    {
        if (!OperatingSystem.IsWindows()) return;

        WorkerProcessClient? worker = null;
        Process? witness = null;
        try
        {
            const long generation = 7;
            worker = await WorkerProcessClient.LaunchAsync(
                new WindowsWorkerProcessLauncher(),
                CreateServerCommand(),
                generation,
                DateTimeOffset.UtcNow.AddMinutes(2),
                cancellationToken: TestContext.Current.CancellationToken);
            witness = OpenProcessWitness(worker.ProcessId);

            Assert.Equal(generation, worker.Generation);
            Assert.NotEqual(Guid.Empty, worker.WorkerBootId);
            await worker.ShutdownAsync(TestContext.Current.CancellationToken);
            await witness.WaitForExitAsync().WaitAsync(CheckpointTimeout);
            Assert.Equal(0, witness.ExitCode);
            var diagnostics = await worker.Diagnostics.WaitAsync(CheckpointTimeout);
            Assert.Equal(0, diagnostics.StandardOutput.TotalBytes);
            Assert.Equal(0, diagnostics.StandardError.TotalBytes);
            Assert.False(worker.Fatal.IsCompleted);
        }
        finally
        {
            if (worker is not null)
                await worker.DisposeAsync();
            witness?.Dispose();
        }
    }

    private static WorkerLaunchCommand CreateServerCommand()
    {
        var serverAssembly = typeof(WorkerServer).Assembly.Location;
        var serverDirectory = Path.GetDirectoryName(serverAssembly) ??
            throw new InvalidOperationException("The server assembly directory is unavailable.");
        return new WorkerLaunchCommand(
            ResolveDotnetHost(),
            ["exec", serverAssembly, "--worker"],
            serverDirectory,
            CaptureCurrentEnvironment());
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
        var dotnetRoot = runtime.Parent?.Parent?.Parent ??
            throw new InvalidOperationException("The dotnet host directory is unavailable.");
        var inferred = Path.Combine(dotnetRoot.FullName, "dotnet.exe");
        return File.Exists(inferred)
            ? inferred
            : throw new FileNotFoundException("The dotnet host executable is unavailable.", inferred);
    }

    private static IEnumerable<KeyValuePair<string, string>> CaptureCurrentEnvironment()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || entry.Value is not string value ||
                key.Contains('=') ||
                WorkerBootstrapEnvironment.ReservedHandleVariables.Contains(key))
            {
                continue;
            }
            yield return new KeyValuePair<string, string>(key, value);
        }
    }

    private static Process OpenProcessWitness(int processId)
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
}
