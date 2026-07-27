using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PtkContainmentTestFixture;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

[Collection(WindowsProcessCreationCollection.Name)]
public sealed class WindowsContainmentIntegrationTests
{
    private static readonly TimeSpan CheckpointTimeout = TimeSpan.FromSeconds(15);
    private const string ExactEnvironmentVariable = "PTK_CONTAINMENT_EXACT_ENV";
    private const string ExactEnvironmentValue = "exact-λ-value";
    private const string AmbientLeakEnvironmentVariable = "PTK_CONTAINMENT_AMBIENT_LEAK";
    private const string ExactArgument = "argument λ with \"quote\" and trailing\\";

    [Fact]
    public async Task Runnable_worker_enters_without_a_proof_resume()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scratch = Path.Combine(
            Path.GetTempPath(),
            $"ptk windows runnable containment {Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var enteredMarker = Path.Combine(scratch, "entered marker.txt");

        ContainedWindowsWorker? worker = null;
        Process? workerWitness = null;
        var priorAmbientLeak = Environment.GetEnvironmentVariable(AmbientLeakEnvironmentVariable);
        try
        {
            var command = CreateFixtureCommand(enteredMarker);
            Environment.SetEnvironmentVariable(AmbientLeakEnvironmentVariable, "must-not-leak");
            worker = new WindowsProcessTreeSupervisor().Launch(command);
            workerWitness = OpenProcessWitness(worker.ProcessId);

            using var eventReader = CreateReader(worker.EventReader);
            using var outputReader = CreateReader(worker.StandardOutputReader);
            using var errorReader = CreateReader(worker.StandardErrorReader);
            Assert.Equal("stdin:eof", await ReadLineAsync(eventReader));
            Assert.Equal("entered", await ReadLineAsync(eventReader));
            Assert.Equal("fixture:stdout", await ReadLineAsync(outputReader));
            Assert.Equal("fixture:stderr", await ReadLineAsync(errorReader));
            Assert.Equal("entered\n", await File.ReadAllTextAsync(enteredMarker));

            using var canceledWaitCancellation = new CancellationTokenSource();
            var canceledWait = worker.WaitForExitAsync(canceledWaitCancellation.Token);
            Assert.False(canceledWait.IsCompleted);

            canceledWaitCancellation.Cancel();
            Assert.False(workerWitness.HasExited);

            var owner = worker;
            worker = null;
            var containment = await owner.ContainAsync(
                WorkerContainmentReason.Close);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await canceledWait);
            await WaitForExitAsync(workerWitness);
            Assert.Equal(
                WorkerContainmentOutcome.ConfirmedEmpty,
                containment.Outcome);
            await owner.ContainmentEmpty.WaitAsync(CheckpointTimeout);
            owner.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AmbientLeakEnvironmentVariable, priorAmbientLeak);
            await ContainBestEffortAsync(worker);
            worker?.Dispose();
            workerWitness?.Dispose();
            DeleteScratchBestEffort(scratch);
        }
    }

    [Fact]
    public async Task Suspended_worker_is_contained_before_entry_and_job_owner_kills_its_tree()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scratch = Path.Combine(
            Path.GetTempPath(),
            $"ptk-windows-containment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var enteredMarker = Path.Combine(scratch, "entered.marker");

        ContainedWindowsWorker? worker = null;
        Process? workerWitness = null;
        Process? descendantWitness = null;
        var priorAmbientLeak = Environment.GetEnvironmentVariable(AmbientLeakEnvironmentVariable);
        try
        {
            var supervisor = new WindowsProcessTreeSupervisor();
            var command = CreateFixtureCommand(enteredMarker);
            Environment.SetEnvironmentVariable(AmbientLeakEnvironmentVariable, "must-not-leak");
            worker = supervisor.Launch(
                command,
                WindowsProcessCreationMode.SuspendedForContainmentProof);

            // A proof-mode launch returns only after IsProcessInJob succeeds
            // against this worker and this exact Job Object. The primary thread
            // is still suspended, so no managed fixture instruction can have run.
            Assert.False(File.Exists(enteredMarker));
            workerWitness = OpenProcessWitness(worker.ProcessId);
            Assert.False(workerWitness.HasExited);

            worker.ResumeForContainmentProof();

            using var eventReader = CreateReader(worker.EventReader);
            using var outputReader = CreateReader(worker.StandardOutputReader);
            using var errorReader = CreateReader(worker.StandardErrorReader);

            Assert.Equal("stdin:eof", await ReadLineAsync(eventReader));
            Assert.Equal("entered", await ReadLineAsync(eventReader));
            Assert.Equal("fixture:stdout", await ReadLineAsync(outputReader));
            Assert.Equal("fixture:stderr", await ReadLineAsync(errorReader));
            Assert.Equal("entered\n", await File.ReadAllTextAsync(enteredMarker));

            // The fixture is now blocked solely on the private request pipe.
            // Collection/finalization must not close a lost Job Object handle;
            // the returned owner keeps the sole job handle rooted.
            ForceFullCollection();
            Assert.False(workerWitness.HasExited);

            await worker.RequestWriter.WriteAsync("spawn\n"u8.ToArray());
            await worker.RequestWriter.FlushAsync();
            var descendantEvent = await ReadLineAsync(eventReader);
            Assert.StartsWith("descendant:", descendantEvent, StringComparison.Ordinal);
            Assert.True(
                int.TryParse(
                    descendantEvent.AsSpan("descendant:".Length),
                    out var descendantProcessId));

            descendantWitness = OpenProcessWitness(descendantProcessId);
            Assert.False(descendantWitness.HasExited);

            // Process witnesses retain only process handles, never a Job Object
            // handle. Closing the owner's sole job handle must therefore kill
            // both the worker and the ordinary no-breakaway descendant.
            var owner = worker;
            worker = null;
            var containment = await owner.ContainAsync(
                WorkerContainmentReason.Close);

            await WaitForExitAsync(workerWitness);
            await WaitForExitAsync(descendantWitness);
            Assert.Equal(
                WorkerContainmentOutcome.ConfirmedEmpty,
                containment.Outcome);
            await owner.ContainmentEmpty.WaitAsync(CheckpointTimeout);
            owner.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AmbientLeakEnvironmentVariable, priorAmbientLeak);
            try
            {
                await ContainBestEffortAsync(worker);
                worker?.Dispose();
            }
            finally
            {
                workerWitness?.Dispose();
                descendantWitness?.Dispose();
                DeleteScratchBestEffort(scratch);
            }
        }
    }

    [Fact]
    public async Task Two_worker_jobs_contain_child_and_grandchild_independently()
    {
        if (!OperatingSystem.IsWindows()) return;

        IWorkerContainedProcess? first = null;
        IWorkerContainedProcess? second = null;
        var witnesses = new List<Process>();
        try
        {
            first = await WorkerProcessLauncher.Create().LaunchAsync(
                CreateContainedFixtureCommand());
            second = await WorkerProcessLauncher.Create().LaunchAsync(
                CreateContainedFixtureCommand());
            var firstTree = await ReadTreeAsync(first.StandardOutputReader);
            var secondTree = await ReadTreeAsync(second.StandardOutputReader);

            Assert.Equal(first.ProcessId, firstTree.WorkerPid);
            Assert.Equal(second.ProcessId, secondTree.WorkerPid);
            Assert.NotEqual(first.ProcessId, second.ProcessId);

            var firstWitnesses = OpenTreeWitnesses(firstTree);
            var secondWitnesses = OpenTreeWitnesses(secondTree);
            witnesses.AddRange(firstWitnesses);
            witnesses.AddRange(secondWitnesses);

            var firstResult = await first.ContainAsync(
                WorkerContainmentReason.Reset);
            Assert.Equal(
                WorkerContainmentOutcome.ConfirmedEmpty,
                firstResult.Outcome);
            await first.ContainmentEmpty.WaitAsync(CheckpointTimeout);
            foreach (var witness in firstWitnesses)
                await WaitForExitAsync(witness);
            Assert.All(
                secondWitnesses,
                witness => Assert.False(witness.HasExited));

            var secondResult = await second.ContainAsync(
                WorkerContainmentReason.Timeout);
            Assert.Equal(
                WorkerContainmentOutcome.ConfirmedEmpty,
                secondResult.Outcome);
            await second.ContainmentEmpty.WaitAsync(CheckpointTimeout);
            foreach (var witness in secondWitnesses)
                await WaitForExitAsync(witness);
        }
        finally
        {
            await ContainBestEffortAsync(first);
            await ContainBestEffortAsync(second);
            first?.Dispose();
            second?.Dispose();
            foreach (var witness in witnesses)
                witness.Dispose();
        }
    }

    [Fact]
    public async Task Hard_supervisor_death_closes_both_worker_jobs()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var supervisor = StartContainedSupervisor();
        var standardError = supervisor.StandardError.ReadToEndAsync();
        var ready = await ReadSupervisorReadyAsync(
            supervisor.StandardOutput);
        var witnesses = ready.OwnedProcessIds
            .Select(OpenProcessWitness)
            .ToArray();
        try
        {
            Assert.Equal(supervisor.Id, ready.SupervisorPid);
            Assert.NotEqual(ready.FirstWorkerPid, ready.SecondWorkerPid);
            Assert.NotEqual(
                ready.FirstDescendantPid,
                ready.FirstGrandchildPid);
            Assert.NotEqual(
                ready.SecondDescendantPid,
                ready.SecondGrandchildPid);
            Assert.All(witnesses, witness => Assert.False(witness.HasExited));

            supervisor.Kill();
            await supervisor.WaitForExitAsync().WaitAsync(CheckpointTimeout);

            foreach (var witness in witnesses)
                await WaitForExitAsync(witness);
            Assert.Equal(
                string.Empty,
                await standardError.WaitAsync(CheckpointTimeout));
        }
        finally
        {
            if (!supervisor.HasExited)
                supervisor.Kill(entireProcessTree: true);
            foreach (var witness in witnesses)
                witness.Dispose();
        }
    }

    private static WorkerLaunchCommand CreateFixtureCommand(string enteredMarker)
    {
        var fixtureAssembly = typeof(FixtureAssemblyMarker).Assembly.Location;
        var fixtureDirectory = Path.GetDirectoryName(fixtureAssembly) ??
            throw new InvalidOperationException("The containment fixture directory is unavailable.");
        var appHost = Path.Combine(fixtureDirectory, "PtkContainmentTestFixture.exe");

        string executable;
        string[] arguments;
        if (File.Exists(appHost))
        {
            executable = appHost;
            arguments = ["worker", enteredMarker, ExactArgument];
        }
        else
        {
            executable = ResolveDotnetHost();
            arguments = [fixtureAssembly, "worker", enteredMarker, ExactArgument];
        }

        var environment = CaptureCurrentEnvironment().ToList();
        environment.Add(new KeyValuePair<string, string>(
            ExactEnvironmentVariable,
            ExactEnvironmentValue));

        return new WorkerLaunchCommand(
            executable,
            arguments,
            fixtureDirectory,
            environment);
    }

    private static WorkerLaunchCommand CreateContainedFixtureCommand()
    {
        var fixture = ResolveFixtureInvocation("contained-worker");
        return new WorkerLaunchCommand(
            fixture.Executable,
            fixture.Arguments,
            fixture.WorkingDirectory,
            CaptureCurrentEnvironment());
    }

    private static Process StartContainedSupervisor()
    {
        var fixture = ResolveFixtureInvocation(
            "contained-supervisor",
            "unused-on-windows");
        var start = new ProcessStartInfo
        {
            FileName = fixture.Executable,
            WorkingDirectory = fixture.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in fixture.Arguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start) ??
            throw new InvalidOperationException(
                "The contained supervisor fixture did not start.");
    }

    private static FixtureInvocation ResolveFixtureInvocation(
        params string[] arguments)
    {
        var assembly = typeof(FixtureAssemblyMarker).Assembly.Location;
        var directory = Path.GetDirectoryName(assembly) ??
            throw new InvalidOperationException(
                "The containment fixture directory is unavailable.");
        var appHost = Path.Combine(
            directory,
            "PtkContainmentTestFixture.exe");
        return File.Exists(appHost)
            ? new FixtureInvocation(
                appHost,
                arguments,
                directory)
            : new FixtureInvocation(
                ResolveDotnetHost(),
                [assembly, .. arguments],
                directory);
    }

    private static async Task<TreeSnapshot> ReadTreeAsync(Stream stream)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        var line = await ReadLineAsync(reader);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        return new TreeSnapshot(
            root.GetProperty("workerPid").GetInt32(),
            root.GetProperty("descendantPid").GetInt32(),
            root.GetProperty("grandchildPid").GetInt32());
    }

    private static async Task<SupervisorReadySnapshot>
        ReadSupervisorReadyAsync(StreamReader reader)
    {
        using var cancellation = new CancellationTokenSource(
            CheckpointTimeout);
        var line = await reader.ReadLineAsync(cancellation.Token) ??
            throw new EndOfStreamException(
                "The contained supervisor exited before readiness.");
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        return new SupervisorReadySnapshot(
            root.GetProperty("supervisorPid").GetInt32(),
            root.GetProperty("firstWorkerPid").GetInt32(),
            root.GetProperty("firstDescendantPid").GetInt32(),
            root.GetProperty("firstGrandchildPid").GetInt32(),
            root.GetProperty("secondWorkerPid").GetInt32(),
            root.GetProperty("secondDescendantPid").GetInt32(),
            root.GetProperty("secondGrandchildPid").GetInt32());
    }

    private static Process[] OpenTreeWitnesses(TreeSnapshot tree) =>
    [
        OpenProcessWitness(tree.WorkerPid),
        OpenProcessWitness(tree.DescendantPid),
        OpenProcessWitness(tree.GrandchildPid),
    ];

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
                key.Equals(ExactEnvironmentVariable, StringComparison.OrdinalIgnoreCase) ||
                key.Equals(AmbientLeakEnvironmentVariable, StringComparison.OrdinalIgnoreCase) ||
                WorkerBootstrapEnvironment.ReservedHandleVariables.Contains(key))
            {
                continue;
            }
            yield return new KeyValuePair<string, string>(key, value);
        }
    }

    private static StreamReader CreateReader(Stream stream) => new(
        stream,
        Encoding.ASCII,
        detectEncodingFromByteOrderMarks: false,
        bufferSize: 128,
        leaveOpen: true);

    private static async Task<string> ReadLineAsync(StreamReader reader)
    {
        using var cancellation = new CancellationTokenSource(CheckpointTimeout);
        return await reader.ReadLineAsync(cancellation.Token) ??
            throw new EndOfStreamException("A containment fixture stream closed before its checkpoint.");
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

    private static async Task WaitForExitAsync(Process process)
    {
        using var cancellation = new CancellationTokenSource(CheckpointTimeout);
        await process.WaitForExitAsync(cancellation.Token);
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

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static void DeleteScratchBestEffort(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // The assertions own correctness. Windows can retain a just-closed
            // diagnostic file briefly; cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException cleanup note above.
        }
    }

    private sealed record FixtureInvocation(
        string Executable,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory);

    private sealed record TreeSnapshot(
        int WorkerPid,
        int DescendantPid,
        int GrandchildPid);

    private sealed record SupervisorReadySnapshot(
        int SupervisorPid,
        int FirstWorkerPid,
        int FirstDescendantPid,
        int FirstGrandchildPid,
        int SecondWorkerPid,
        int SecondDescendantPid,
        int SecondGrandchildPid)
    {
        internal IEnumerable<int> OwnedProcessIds =>
        [
            FirstWorkerPid,
            FirstDescendantPid,
            FirstGrandchildPid,
            SecondWorkerPid,
            SecondDescendantPid,
            SecondGrandchildPid,
        ];
    }
}
