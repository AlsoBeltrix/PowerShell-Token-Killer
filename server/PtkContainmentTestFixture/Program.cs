using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PtkMcpServer;
using PtkMcpServer.Worker;

namespace PtkContainmentTestFixture;

public static class FixtureAssemblyMarker
{
}

internal static partial class Program
{
    private const string RequestHandleEnvironmentVariable = "PTK_WORKER_REQUEST_HANDLE";
    private const string EventHandleEnvironmentVariable = "PTK_WORKER_EVENT_HANDLE";
    private const string ExactEnvironmentVariable = "PTK_CONTAINMENT_EXACT_ENV";
    private const string ExactEnvironmentValue = "exact-λ-value";
    private const string AmbientLeakEnvironmentVariable = "PTK_CONTAINMENT_AMBIENT_LEAK";
    private const string ExactArgument = "argument λ with \"quote\" and trailing\\";
    private const uint HandleFlagInherit = 0x00000001;
    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;
    private static readonly byte[] SpawnCommand = "spawn\n"u8.ToArray();

    private static async Task<int> Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["contained-worker"] =>
                    RunContainedWorker(escape: false, gatePath: null),
                ["contained-escape-worker", var gatePath] =>
                    RunContainedWorker(escape: true, gatePath),
                ["contained-descendant"] =>
                    RunContainedDescendant(escape: false, gatePath: null),
                ["contained-escape-descendant", var gatePath] =>
                    RunContainedDescendant(escape: true, gatePath),
                ["contained-leaf"] => RunContainedLeaf(),
                ["output-root-owner", var parentPath] =>
                    RunOutputRootOwner(parentPath),
                ["contained-supervisor", var brokerPath] =>
                    await RunContainedSupervisorAsync(
                            brokerPath,
                            workerPath: null)
                        .ConfigureAwait(false),
                ["contained-supervisor", var brokerPath, var workerPath] =>
                    await RunContainedSupervisorAsync(
                            brokerPath,
                            workerPath)
                        .ConfigureAwait(false),
                ["worker", var markerPath, ExactArgument] => RunWorker(markerPath),
                ["descendant"] => RunDescendant(),
                ["nested-host", var encodedScratchPath, var outerJobHandle] =>
                    WindowsNestedJobFixture.RunHost(encodedScratchPath, outerJobHandle),
                ["nested-worker", var encodedScratchPath, var outerJobHandle, var innerJobHandle] =>
                    WindowsNestedJobFixture.RunWorker(
                        encodedScratchPath,
                        outerJobHandle,
                        innerJobHandle),
                ["nested-descendant", var encodedScratchPath, var outerJobHandle, var innerJobHandle] =>
                    WindowsNestedJobFixture.RunDescendant(
                        encodedScratchPath,
                        outerJobHandle,
                        innerJobHandle),
                _ => 64,
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"fixture:error:{exception.GetType().Name}");
            return 70;
        }
    }

    private static int RunContainedWorker(bool escape, string? gatePath)
    {
        CloseBootstrapHandles();
        using var descendant = StartSelf(
            escape ? "contained-escape-descendant" : "contained-descendant",
            gatePath,
            redirectStandardOutput: true);
        var descendantLine = descendant.StandardOutput.ReadLine() ??
            throw new EndOfStreamException(
                "The contained descendant exited before reporting its child.");
        using var descendantDocument = JsonDocument.Parse(descendantLine);
        var descendantRoot = descendantDocument.RootElement;
        var grandchildPid =
            descendantRoot.GetProperty("grandchildPid").GetInt32();
        var grandchildPgid =
            descendantRoot.GetProperty("grandchildPgid").GetInt32();
        var workerProcessGroup = CurrentProcessGroup();
        var descendantProcessGroup = ProcessGroup(descendant.Id);
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            workerPid = Environment.ProcessId,
            workerPgid = workerProcessGroup,
            descendantPid = descendant.Id,
            descendantPgid = descendantProcessGroup,
            grandchildPid,
            grandchildPgid,
        }));
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static int RunContainedDescendant(bool escape, string? gatePath)
    {
        if (Environment.GetEnvironmentVariable(RequestHandleEnvironmentVariable)
                is not null ||
            Environment.GetEnvironmentVariable(EventHandleEnvironmentVariable)
                is not null)
        {
            return 65;
        }

        using var grandchild = StartSelf(
            "contained-leaf",
            argument: null);
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            grandchildPid = grandchild.Id,
            grandchildPgid = ProcessGroup(grandchild.Id),
        }));
        Console.Out.Flush();

        if (escape)
        {
            if (string.IsNullOrWhiteSpace(gatePath) ||
                !Path.IsPathFullyQualified(gatePath))
            {
                return 64;
            }
            while (!File.Exists(gatePath))
                Thread.Sleep(10);
            if (OperatingSystem.IsWindows() || CreateSession() <= 0)
                return 69;
            File.WriteAllText(
                gatePath + ".escaped",
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
        }

        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static int RunContainedLeaf()
    {
        if (Environment.GetEnvironmentVariable(RequestHandleEnvironmentVariable)
                is not null ||
            Environment.GetEnvironmentVariable(EventHandleEnvironmentVariable)
                is not null)
        {
            return 65;
        }

        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static int RunOutputRootOwner(string parentPath)
    {
        if (!Path.IsPathFullyQualified(parentPath))
            return 64;
        var ownership = OutputRootOwnership.CreateCurrent();
        var root = Path.Combine(parentPath, ownership.DirectoryName);
        using var store = new OutputStore(new OutputStoreOptions(
            root,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            MaximumArtifactBytes: 1024,
            MaximumSessionBytes: 2048,
            MaximumAggregateBytes: 4096,
            RootOwnership: ownership));
        Console.Out.WriteLine(store.RootPathForTests);
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static async Task<int> RunContainedSupervisorAsync(
        string brokerPath,
        string? workerPath)
    {
        IWorkerContainedProcess? first = null;
        IWorkerContainedProcess? second = null;
        try
        {
            var firstLauncher = WorkerProcessLauncher.Create(
                OperatingSystem.IsWindows() ? null : brokerPath);
            var secondLauncher = WorkerProcessLauncher.Create(
                OperatingSystem.IsWindows() ? null : brokerPath);
            first = await firstLauncher.LaunchAsync(
                CreateContainedWorkerCommand(workerPath)).ConfigureAwait(false);
            second = await secondLauncher.LaunchAsync(
                CreateContainedWorkerCommand(workerPath)).ConfigureAwait(false);

            var firstTree = await ReadTreeAsync(first.StandardOutputReader)
                .ConfigureAwait(false);
            var secondTree = await ReadTreeAsync(second.StandardOutputReader)
                .ConfigureAwait(false);
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                supervisorPid = Environment.ProcessId,
                firstContainmentPid = first.ContainmentProcessId,
                firstWorkerPid = firstTree.WorkerPid,
                firstWorkerPgid = firstTree.WorkerPgid,
                firstDescendantPid = firstTree.DescendantPid,
                firstDescendantPgid = firstTree.DescendantPgid,
                firstGrandchildPid = firstTree.GrandchildPid,
                firstGrandchildPgid = firstTree.GrandchildPgid,
                secondContainmentPid = second.ContainmentProcessId,
                secondWorkerPid = secondTree.WorkerPid,
                secondWorkerPgid = secondTree.WorkerPgid,
                secondDescendantPid = secondTree.DescendantPid,
                secondDescendantPgid = secondTree.DescendantPgid,
                secondGrandchildPid = secondTree.GrandchildPid,
                secondGrandchildPgid = secondTree.GrandchildPgid,
            }));
            Console.Out.Flush();
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await ContainBestEffortAsync(first).ConfigureAwait(false);
            await ContainBestEffortAsync(second).ConfigureAwait(false);
            first?.Dispose();
            second?.Dispose();
        }
    }

    private static async Task<TreeSnapshot> ReadTreeAsync(Stream stream)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        var line = await reader.ReadLineAsync().ConfigureAwait(false) ??
            throw new EndOfStreamException(
                "A contained worker closed before reporting its tree.");
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

    private static async Task ContainBestEffortAsync(
        IWorkerContainedProcess? process)
    {
        if (process is null)
            return;
        try
        {
            await process.ContainAsync(
                WorkerContainmentReason.SupervisorShutdown).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static WorkerLaunchCommand CreateSelfCommand(params string[] arguments)
    {
        var start = SelfInvocation(arguments);
        return new WorkerLaunchCommand(
            start.Executable,
            start.Arguments,
            Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location) ??
                throw new InvalidOperationException(
                    "Fixture directory is unavailable."),
            CaptureEnvironment());
    }

    private static WorkerLaunchCommand CreateContainedWorkerCommand(
        string? workerPath) =>
        workerPath is null
            ? CreateSelfCommand("contained-worker")
            : new WorkerLaunchCommand(
                Path.GetFullPath(workerPath),
                [],
                Path.GetPathRoot(Path.GetFullPath(workerPath)) ??
                    throw new InvalidOperationException(
                        "The native worker path has no root."),
                CaptureEnvironment());

    private static Process StartSelf(
        string mode,
        string? argument,
        bool redirectStandardOutput = false)
    {
        var arguments = argument is null
            ? new[] { mode }
            : new[] { mode, argument };
        var invocation = SelfInvocation(arguments);
        var start = new ProcessStartInfo
        {
            FileName = invocation.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = redirectStandardOutput,
        };
        foreach (var value in invocation.Arguments)
            start.ArgumentList.Add(value);
        return Process.Start(start) ??
            throw new InvalidOperationException(
                "The contained fixture descendant did not start.");
    }

    private static SelfCommand SelfInvocation(IReadOnlyList<string> arguments)
    {
        var processPath = Environment.ProcessPath ??
            throw new InvalidOperationException(
                "The fixture process path is unavailable.");
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        var values = new List<string>();
        if (Path.GetFileNameWithoutExtension(processPath).Equals(
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(entryAssemblyPath))
            {
                throw new InvalidOperationException(
                    "The fixture entry assembly path is unavailable.");
            }
            values.Add(entryAssemblyPath);
        }
        values.AddRange(arguments);
        return new SelfCommand(
            Path.GetFullPath(processPath),
            values.ToArray());
    }

    private static IEnumerable<KeyValuePair<string, string>> CaptureEnvironment()
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

    private static void CloseBootstrapHandles()
    {
        var request = TakeBootstrapHandleValue(
            RequestHandleEnvironmentVariable);
        var events = TakeBootstrapHandleValue(
            EventHandleEnvironmentVariable);
        if (OperatingSystem.IsWindows())
        {
            using var requestHandle = new SafeFileHandle(
                request,
                ownsHandle: true);
            using var eventHandle = new SafeFileHandle(
                events,
                ownsHandle: true);
            return;
        }

        _ = CloseDescriptor(request.ToInt32());
        _ = CloseDescriptor(events.ToInt32());
    }

    private static int CurrentProcessGroup() =>
        OperatingSystem.IsWindows() ? 0 : GetProcessGroup(0);

    private static int ProcessGroup(int processId) =>
        OperatingSystem.IsWindows() ? 0 : GetProcessGroup(processId);

    private static int RunWorker(string markerPath)
    {
        if (!OperatingSystem.IsWindows()) return 69;
        if (!Path.IsPathFullyQualified(markerPath)) return 64;

        var standardInputHandle = GetRequiredStandardHandle(StandardInputHandle);
        var requestHandleValue = TakeBootstrapHandleValue(RequestHandleEnvironmentVariable);
        var eventHandleValue = TakeBootstrapHandleValue(EventHandleEnvironmentVariable);
        var standardOutputHandle = GetRequiredStandardHandle(StandardOutputHandle);
        var standardErrorHandle = GetRequiredStandardHandle(StandardErrorHandle);

        if (Environment.GetEnvironmentVariable(ExactEnvironmentVariable) != ExactEnvironmentValue ||
            Environment.GetEnvironmentVariable(AmbientLeakEnvironmentVariable) is not null)
        {
            throw new InvalidOperationException("The fixture did not receive its exact closed environment.");
        }

        DisableInheritance(standardInputHandle);
        DisableInheritance(requestHandleValue);
        DisableInheritance(eventHandleValue);
        DisableInheritance(standardOutputHandle);
        DisableInheritance(standardErrorHandle);

        using var requestHandle = new SafeFileHandle(requestHandleValue, ownsHandle: true);
        using var eventHandle = new SafeFileHandle(eventHandleValue, ownsHandle: true);

        using var request = new FileStream(
            requestHandle,
            FileAccess.Read,
            bufferSize: 1,
            isAsync: false);
        using var events = new FileStream(
            eventHandle,
            FileAccess.Write,
            bufferSize: 1,
            isAsync: false);

        if (Console.In.Read() != -1)
            throw new InvalidOperationException("Fixture standard input was not NUL/EOF.");
        WriteEvent(events, "stdin:eof\n");

        File.WriteAllText(markerPath, "entered\n", new UTF8Encoding(false));
        WriteEvent(events, "entered\n");
        Console.Out.WriteLine("fixture:stdout");
        Console.Out.Flush();
        Console.Error.WriteLine("fixture:stderr");
        Console.Error.Flush();

        ReadExactCommand(request, SpawnCommand);
        using var descendant = StartDescendant();
        WriteEvent(events, $"descendant:{descendant.Id.ToString(CultureInfo.InvariantCulture)}\n");

        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static int RunDescendant()
    {
        if (!OperatingSystem.IsWindows()) return 69;
        if (Environment.GetEnvironmentVariable(RequestHandleEnvironmentVariable) is not null ||
            Environment.GetEnvironmentVariable(EventHandleEnvironmentVariable) is not null)
        {
            return 65;
        }

        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static IntPtr TakeBootstrapHandleValue(string variableName)
    {
        var text = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, null);
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value is 0 or ulong.MaxValue)
        {
            throw new InvalidOperationException($"Missing or invalid {variableName}.");
        }
        return new IntPtr(unchecked((long)value));
    }

    private static IntPtr GetRequiredStandardHandle(int standardHandle)
    {
        var actual = GetStdHandle(standardHandle);
        if (actual == IntPtr.Zero || actual == new IntPtr(-1))
            throw new InvalidOperationException("A fixture standard handle was not mapped.");
        return actual;
    }

    private static void DisableInheritance(IntPtr handle)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void ReadExactCommand(Stream stream, ReadOnlySpan<byte> expected)
    {
        Span<byte> received = stackalloc byte[expected.Length];
        var offset = 0;
        while (offset < received.Length)
        {
            var read = stream.Read(received[offset..]);
            if (read == 0)
                throw new EndOfStreamException("Supervisor request pipe closed before spawn.");
            offset += read;
        }
        if (!received.SequenceEqual(expected))
            throw new InvalidDataException("Unexpected supervisor request.");
    }

    private static void WriteEvent(Stream stream, string value)
    {
        var encoded = Encoding.ASCII.GetBytes(value);
        stream.Write(encoded);
        stream.Flush();
    }

    private static Process StartDescendant()
    {
        var processPath = Environment.ProcessPath ??
            throw new InvalidOperationException("The fixture process path is unavailable.");
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals(
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(entryAssemblyPath))
                throw new InvalidOperationException("The fixture entry assembly path is unavailable.");
            startInfo.ArgumentList.Add(entryAssemblyPath);
        }
        startInfo.ArgumentList.Add("descendant");
        return Process.Start(startInfo) ??
            throw new InvalidOperationException("The fixture descendant did not start.");
    }

    private sealed record SelfCommand(
        string Executable,
        IReadOnlyList<string> Arguments);

    private sealed record TreeSnapshot(
        int WorkerPid,
        int WorkerPgid,
        int DescendantPid,
        int DescendantPgid,
        int GrandchildPid,
        int GrandchildPgid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(
        IntPtr hObject,
        uint dwMask,
        uint dwFlags);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseDescriptor(int descriptor);

    [LibraryImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static partial int GetProcessGroup(int processId);

    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static partial int CreateSession();
}
