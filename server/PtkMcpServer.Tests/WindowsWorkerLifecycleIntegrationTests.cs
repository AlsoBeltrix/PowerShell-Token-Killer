using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

[Collection(WindowsProcessCreationCollection.Name)]
public sealed class WindowsWorkerLifecycleIntegrationTests
{
    private static readonly TimeSpan CheckpointTimeout = TimeSpan.FromSeconds(60);
    private static readonly Guid SessionId =
        Guid.Parse("72b2c475-503f-466f-9f32-bd171e4ea517");
    private static readonly WorkerProtocolLimits Limits =
        WorkerOperationProtocol.CreateLimits(
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1));

    [Fact]
    public async Task Contained_worker_completes_lifecycle_with_silent_diagnostics()
    {
        if (!OperatingSystem.IsWindows()) return;

        ContainedWindowsWorker? worker = null;
        Process? witness = null;
        try
        {
            worker = new WindowsProcessTreeSupervisor().Launch(CreateServerCommand());
            witness = OpenProcessWitness(worker.ProcessId);

            var standardOutput = DrainAsync(worker.StandardOutputReader);
            var standardError = DrainAsync(worker.StandardErrorReader);
            var reader = new WorkerProtocolReader(worker.EventReader);
            var writer = new WorkerProtocolWriter(worker.RequestWriter);

            const long incarnation = 7;
            await writer.WriteAsync(WorkerOperationProtocol.CreateInitializeEnvelope(
                SessionId,
                incarnation,
                1,
                DateTimeOffset.UtcNow.AddMinutes(2),
                Limits));

            var ready = await ReadAsync(reader);
            Assert.NotNull(ready);
            Assert.Equal(WorkerMessageKind.Ready, ready.Kind);
            Assert.Equal(SessionId, ready.SessionId);
            Assert.Equal(incarnation, ready.Incarnation);
            Assert.Equal(1, ready.RequestId);
            Assert.Equal(
                Limits,
                WorkerOperationProtocol.ParseReady(
                    ready,
                    SessionId,
                    incarnation,
                    expectedRequestId: 1));

            await writer.WriteAsync(WorkerOperationProtocol.CreateEmptyEnvelope(
                WorkerMessageKind.Shutdown,
                SessionId,
                incarnation,
                2));

            var stopped = await ReadAsync(reader);
            Assert.NotNull(stopped);
            Assert.Equal(WorkerMessageKind.Stopped, stopped.Kind);
            Assert.Equal(SessionId, stopped.SessionId);
            Assert.Equal(incarnation, stopped.Incarnation);
            Assert.Equal(2, stopped.RequestId);
            WorkerOperationProtocol.ParseEmpty(
                stopped,
                WorkerMessageKind.Stopped,
                SessionId,
                incarnation);

            await worker.WaitForExitAsync().WaitAsync(CheckpointTimeout);
            await witness.WaitForExitAsync().WaitAsync(CheckpointTimeout);
            Assert.Equal(0, witness.ExitCode);
            Assert.Empty(await standardOutput.WaitAsync(CheckpointTimeout));
            Assert.Empty(await standardError.WaitAsync(CheckpointTimeout));
            Assert.Null(await ReadAsync(reader));
        }
        finally
        {
            worker?.Dispose();
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

    private static async Task<WorkerEnvelope?> ReadAsync(WorkerProtocolReader reader)
    {
        using var cancellation = new CancellationTokenSource(CheckpointTimeout);
        return await reader.ReadAsync(cancellation.Token);
    }

    private static async Task<byte[]> DrainAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
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
