using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PtkMcpGuardian.Host;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Package;
using PtkMcpGuardian.Standalone;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class UnixPrivateHostProcessLauncherTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Native_broker_owns_creation_time_group_and_confirms_descendant_death()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = TemporaryRoot();
        var broker = Path.Combine(root, "PtkGuardianBroker");
        var host = Path.Combine(root, "ptk-unix-host-fixture");
        var marker = Path.Combine(root, "host-tree.txt");
        int[] identities = [];
        await CompileAsync(BrokerSourcePath(), broker);
        await CompileAsync(HostSourcePath(), host);

        using var request = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        using var events = new AnonymousPipeServerStream(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        var package = Package(host, broker);
        var command = new PrivateHostLaunchCommand(
            package,
            Pins(package),
            new GuardianHostIdentity(
                new GuardianBootId(Guid.Parse("11111111-1111-4111-8111-111111111111")),
                new HostBootId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
                new HostGeneration(1)),
            EnvironmentWithMarker(marker),
            Handle(request.ClientSafePipeHandle),
            Handle(events.ClientSafePipeHandle));
        var launcher = new UnixPrivateHostProcessLauncher(broker);
        var launch = launcher.Launch(command);
        request.DisposeLocalCopyOfClientHandle();
        events.DisposeLocalCopyOfClientHandle();
        var authority = Assert.IsAssignableFrom<IPrivateHostLaunchedProcess>(launch.LaunchedHost);
        try
        {
            Assert.Equal(GuardianHostLaunchOutcome.Started, launch.Outcome);
            identities = await ReadIdentitiesAsync(marker, TestTimeout);
            Assert.Equal(authority.ProcessId, identities[0]);
            Assert.All(identities, processId => Assert.True(processId > 0));
            Assert.Equal(identities.Length, identities.Distinct().Count());
            Assert.All(identities, processId => Assert.Equal(identities[0], GetProcessGroup(processId)));

            var started = Stopwatch.GetTimestamp();
            authority.BeginContainment(new GuardianHostContainmentDeadline(
                started,
                started + Stopwatch.Frequency * 10));
            await authority.Exited.WaitAsync(TestTimeout);
            await authority.ContainmentConfirmed.WaitAsync(TestTimeout);
            await AssertProcessesGoneAsync(identities, TestTimeout);
        }
        finally
        {
            authority.Dispose();
            KillBestEffort(identities);
            DeleteBestEffort(root);
        }
    }

    [Fact]
    public async Task Native_broker_registry_contains_a_moved_worker_group()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = TemporaryRoot();
        var broker = Path.Combine(root, "PtkGuardianBroker");
        var host = Path.Combine(root, "ptk-unix-registry-host-fixture");
        var factsMarker = Path.Combine(root, "registry-facts.txt");
        var armMarker = Path.Combine(root, "arm.marker");
        var armedMarker = Path.Combine(root, "armed.txt");
        var releaseMarker = Path.Combine(root, "release.marker");
        var descendantMarker = Path.Combine(root, "descendant.txt");
        int[] identities = [];
        IPrivateHostLaunchedProcess? authority = null;
        await CompileAsync(BrokerSourcePath(), broker);
        await CompileAsync(RegistryHostSourcePath(), host);

        using var request = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        using var events = new AnonymousPipeServerStream(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        var package = Package(host, broker);
        var command = new PrivateHostLaunchCommand(
            package,
            Pins(package),
            new GuardianHostIdentity(
                new GuardianBootId(Guid.Parse("55555555-5555-4555-8555-555555555555")),
                new HostBootId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
                new HostGeneration(1)),
            EnvironmentWithRegistryMarkers(
                factsMarker,
                armMarker,
                armedMarker,
                releaseMarker,
                descendantMarker),
            Handle(request.ClientSafePipeHandle),
            Handle(events.ClientSafePipeHandle));
        var launch = new UnixPrivateHostProcessLauncher(broker).Launch(command);
        request.DisposeLocalCopyOfClientHandle();
        events.DisposeLocalCopyOfClientHandle();
        authority = Assert.IsAssignableFrom<IPrivateHostLaunchedProcess>(
            launch.LaunchedHost);
        var registry = Assert.IsAssignableFrom<IUnixWorkerContainmentAuthority>(
            launch.LaunchedHost);
        try
        {
            Assert.Equal(GuardianHostLaunchOutcome.Started, launch.Outcome);
            var facts = await ReadRegistryFactsAsync(factsMarker, TestTimeout);
            identities = [facts.HostPid, facts.BrokerPid, facts.WorkerPid];
            Assert.Equal(authority.ProcessId, facts.HostPid);
            Assert.Equal(facts.HostPid, GetProcessGroup(facts.BrokerPid));
            Assert.Equal(facts.HostPid, GetProcessGroup(facts.WorkerPid));

            var identity = facts.ToContainmentIdentity();
            using var cancellation = new CancellationTokenSource(TestTimeout);
            await registry.RegisterPendingAsync(identity, cancellation.Token);
            await File.WriteAllTextAsync(
                armMarker,
                "arm\n",
                cancellation.Token);
            Assert.Equal(
                facts.WorkerPid,
                await ReadProcessIdAsync(armedMarker, TestTimeout));
            Assert.Equal(facts.WorkerPid, GetProcessGroup(facts.WorkerPid));
            await registry.RegisterArmedAsync(identity, cancellation.Token);

            await File.WriteAllTextAsync(
                releaseMarker,
                "release\n",
                cancellation.Token);
            var descendant =
                await ReadProcessIdAsync(descendantMarker, TestTimeout);
            identities = [.. identities, descendant];
            Assert.Equal(facts.WorkerPid, GetProcessGroup(descendant));

            var started = Stopwatch.GetTimestamp();
            authority.BeginContainment(new GuardianHostContainmentDeadline(
                started,
                started + Stopwatch.Frequency * 10));
            await authority.Exited.WaitAsync(TestTimeout);
            await authority.ContainmentConfirmed.WaitAsync(TestTimeout);
            await AssertProcessesGoneAsync(identities, TestTimeout);
        }
        finally
        {
            authority?.Dispose();
            KillBestEffort(identities);
            DeleteBestEffort(root);
        }
    }

    [Fact]
    public async Task Native_broker_rejects_armed_before_the_worker_group_moves()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = TemporaryRoot();
        var broker = Path.Combine(root, "PtkGuardianBroker");
        var host = Path.Combine(root, "ptk-unix-registry-host-fixture");
        var factsMarker = Path.Combine(root, "registry-facts.txt");
        var armMarker = Path.Combine(root, "arm.marker");
        var armedMarker = Path.Combine(root, "armed.txt");
        var releaseMarker = Path.Combine(root, "release.marker");
        var descendantMarker = Path.Combine(root, "descendant.txt");
        int[] identities = [];
        IPrivateHostLaunchedProcess? authority = null;
        await CompileAsync(BrokerSourcePath(), broker);
        await CompileAsync(RegistryHostSourcePath(), host);

        using var request = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        using var events = new AnonymousPipeServerStream(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        var package = Package(host, broker);
        var command = new PrivateHostLaunchCommand(
            package,
            Pins(package),
            new GuardianHostIdentity(
                new GuardianBootId(Guid.Parse("77777777-7777-4777-8777-777777777777")),
                new HostBootId(Guid.Parse("88888888-8888-4888-8888-888888888888")),
                new HostGeneration(1)),
            EnvironmentWithRegistryMarkers(
                factsMarker,
                armMarker,
                armedMarker,
                releaseMarker,
                descendantMarker),
            Handle(request.ClientSafePipeHandle),
            Handle(events.ClientSafePipeHandle));
        var launch = new UnixPrivateHostProcessLauncher(broker).Launch(command);
        request.DisposeLocalCopyOfClientHandle();
        events.DisposeLocalCopyOfClientHandle();
        authority = Assert.IsAssignableFrom<IPrivateHostLaunchedProcess>(
            launch.LaunchedHost);
        var registry = Assert.IsAssignableFrom<IUnixWorkerContainmentAuthority>(
            launch.LaunchedHost);
        try
        {
            Assert.Equal(GuardianHostLaunchOutcome.Started, launch.Outcome);
            var facts = await ReadRegistryFactsAsync(factsMarker, TestTimeout);
            identities = [facts.HostPid, facts.BrokerPid, facts.WorkerPid];
            Assert.Equal(facts.HostPid, GetProcessGroup(facts.WorkerPid));

            var identity = facts.ToContainmentIdentity();
            using var cancellation = new CancellationTokenSource(TestTimeout);
            await registry.RegisterPendingAsync(identity, cancellation.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => registry.RegisterArmedAsync(identity, cancellation.Token));

            await authority.Exited.WaitAsync(TestTimeout);
            await authority.ContainmentConfirmed.WaitAsync(TestTimeout);
            await AssertProcessesGoneAsync(identities, TestTimeout);
            Assert.False(File.Exists(armedMarker));
            Assert.False(File.Exists(descendantMarker));
        }
        finally
        {
            authority?.Dispose();
            KillBestEffort(identities);
            DeleteBestEffort(root);
        }
    }

    [Fact]
    public async Task Closing_the_guardian_liveness_owner_contains_the_host_group()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = TemporaryRoot();
        var broker = Path.Combine(root, "PtkGuardianBroker");
        var host = Path.Combine(root, "ptk-unix-host-fixture");
        var marker = Path.Combine(root, "host-tree.txt");
        int[] identities = [];
        await CompileAsync(BrokerSourcePath(), broker);
        await CompileAsync(HostSourcePath(), host);

        using var request = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        using var events = new AnonymousPipeServerStream(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        var package = Package(host, broker);
        var command = new PrivateHostLaunchCommand(
            package,
            Pins(package),
            new GuardianHostIdentity(
                new GuardianBootId(Guid.Parse("33333333-3333-4333-8333-333333333333")),
                new HostBootId(Guid.Parse("44444444-4444-4444-8444-444444444444")),
                new HostGeneration(1)),
            EnvironmentWithMarker(marker),
            Handle(request.ClientSafePipeHandle),
            Handle(events.ClientSafePipeHandle));
        var launch = new UnixPrivateHostProcessLauncher(broker).Launch(command);
        request.DisposeLocalCopyOfClientHandle();
        events.DisposeLocalCopyOfClientHandle();
        var authority = Assert.IsAssignableFrom<IPrivateHostLaunchedProcess>(launch.LaunchedHost);
        var exited = authority.Exited;
        var confirmed = authority.ContainmentConfirmed;
        try
        {
            identities = await ReadIdentitiesAsync(marker, TestTimeout);
            var livenessField = authority.GetType().GetField(
                "_liveness",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(livenessField);
            var liveness = Assert.IsType<AnonymousPipeServerStream>(
                livenessField.GetValue(authority));
            liveness.Dispose();
            await exited.WaitAsync(TestTimeout);
            await confirmed.WaitAsync(TestTimeout);
            await AssertProcessesGoneAsync(identities, TestTimeout);
        }
        finally
        {
            authority.Dispose();
            KillBestEffort(identities);
            DeleteBestEffort(root);
        }
    }

    [Fact]
    public void Native_source_freezes_the_outer_broker_boundary()
    {
        var source = File.ReadAllText(BrokerSourcePath());
        Assert.Matches(
            new Regex(@"#define\s+PTK_TERM_TO_KILL_MILLISECONDS\s+2000\b"),
            source);
        Assert.Matches(
            new Regex(@"#define\s+PTK_CONTAINMENT_DEADLINE_MILLISECONDS\s+10000\b"),
            source);
        Assert.Matches(
            new Regex(@"#define\s+PTK_IDENTITY_POLL_MILLISECONDS\s+25\b"),
            source);
        Assert.Matches(
            new Regex(@"#define\s+PTK_MAXIMUM_WORKER_GROUPS\s+128\b"),
            source);
        Assert.Single(Regex.Matches(source, @"\bwaitpid\s*\(").Cast<Match>());
        Assert.Contains("waitpid(host_pid, &status, WNOHANG)", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\b(?:waitid|wait3|wait4|system|popen|setsid|setpgrp)\s*\("), source);
        Assert.DoesNotContain("kill(-1", source, StringComparison.Ordinal);
        Assert.Contains(
            "getpgid(command->worker_broker_pid) != host_pid",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "getpgid(command->worker_pid) != host_pid",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "getpgid(entry->worker_pid) != entry->process_group",
            source,
            StringComparison.Ordinal);
        var brokerMain = source[source.IndexOf("static int broker_main(", StringComparison.Ordinal)..];
        AssertOrder(
            brokerMain,
            "pid_t host_pid = fork();",
            "setpgid(host_pid, host_pid)",
            "getpgid(host_pid) != host_pid",
            "EVENT_READY",
            "received = receive_command(",
            "write_full(child_release[1], &release");
        var gatedHostStart = source.IndexOf("static void exec_gated_host(", StringComparison.Ordinal);
        var childGate = source[gatedHostStart..source.IndexOf(
            "static bool wait_for_child_gate(",
            gatedHostStart,
            StringComparison.Ordinal)];
        AssertOrder(
            childGate,
            "setpgid(0, 0)",
            "read(release_read, &release",
            "execv(host_path, arguments)");
        AssertOrder(
            source,
            "signal_registered_groups(entries, SIGTERM)",
            "signal_host_group(host_pid, SIGTERM)",
            "PTK_TERM_TO_KILL_MILLISECONDS",
            "signal_registered_groups(entries, SIGKILL)",
            "signal_host_group(host_pid, SIGKILL)",
            "PTK_CONTAINMENT_DEADLINE_MILLISECONDS",
            "reap_direct_host(host_pid, &host_reaped)",
            "!group_exists(host_pid)",
            "registry_is_gone(entries)",
            "EVENT_CONTAINMENT_CONFIRMED");
    }

    private static async Task CompileAsync(string source, string output)
    {
        var compiler = "/usr/bin/cc";
        Assert.True(File.Exists(compiler), $"Native compiler missing: {compiler}");
        var start = new ProcessStartInfo
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
            "-Wstrict-prototypes", "-Wmissing-prototypes", source, "-o", output,
        })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Native compiler did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TestTimeout);
        Assert.True(
            process.ExitCode == 0,
            $"Native compile failed: {await standardOutput}{await standardError}");
    }

    private static async Task<int[]> ReadIdentitiesAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var values = (await File.ReadAllTextAsync(path)).Trim().Split(' ')
                        .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
                        .ToArray();
                    if (values.Length == 3) return values;
                }
            }
            catch (IOException)
            {
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("The Unix host fixture did not publish its process tree.");
    }

    private static async Task<RegistryFixtureFacts> ReadRegistryFactsAsync(
        string path,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var values = (await File.ReadAllTextAsync(path))
                        .Trim()
                        .Split(' ');
                    if (values.Length == 7)
                    {
                        return new RegistryFixtureFacts(
                            int.Parse(values[0], CultureInfo.InvariantCulture),
                            int.Parse(values[1], CultureInfo.InvariantCulture),
                            ulong.Parse(values[2], CultureInfo.InvariantCulture),
                            ulong.Parse(values[3], CultureInfo.InvariantCulture),
                            int.Parse(values[4], CultureInfo.InvariantCulture),
                            ulong.Parse(values[5], CultureInfo.InvariantCulture),
                            ulong.Parse(values[6], CultureInfo.InvariantCulture));
                    }
                }
            }
            catch (IOException)
            {
            }
            await Task.Delay(25);
        }
        throw new TimeoutException(
            "The Unix registry fixture did not publish its process identities.");
    }

    private static async Task<int> ReadProcessIdAsync(
        string path,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var value = (await File.ReadAllTextAsync(path)).Trim();
                    if (int.TryParse(
                            value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var processId) &&
                        processId > 0)
                    {
                        return processId;
                    }
                }
            }
            catch (IOException)
            {
            }
            await Task.Delay(25);
        }
        throw new TimeoutException(
            "The Unix registry fixture did not publish its process ID.");
    }

    private static async Task AssertProcessesGoneAsync(IEnumerable<int> processIds, TimeSpan timeout)
    {
        var remaining = processIds.ToHashSet();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (remaining.Count > 0 && DateTimeOffset.UtcNow < deadline)
        {
            foreach (var processId in remaining.ToArray())
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    if (process.HasExited) remaining.Remove(processId);
                }
                catch (ArgumentException)
                {
                    remaining.Remove(processId);
                }
                catch (InvalidOperationException)
                {
                    remaining.Remove(processId);
                }
            }
            if (remaining.Count > 0) await Task.Delay(25);
        }
        Assert.True(remaining.Count == 0, $"Unix host processes survived: {string.Join(", ", remaining)}");
    }

    private static IEnumerable<KeyValuePair<string, string>> EnvironmentWithMarker(string marker)
    {
        foreach (System.Collections.DictionaryEntry value in Environment.GetEnvironmentVariables())
        {
            if (value.Key is string key && value.Value is string text)
                yield return new KeyValuePair<string, string>(key, text);
        }
        yield return new KeyValuePair<string, string>("PTK_UNIX_HOST_FIXTURE_MARKER", marker);
    }

    private static IEnumerable<KeyValuePair<string, string>> EnvironmentWithRegistryMarkers(
        string facts,
        string arm,
        string armed,
        string release,
        string descendant)
    {
        foreach (System.Collections.DictionaryEntry value in
                 Environment.GetEnvironmentVariables())
        {
            if (value.Key is string key && value.Value is string text)
                yield return new KeyValuePair<string, string>(key, text);
        }
        yield return new KeyValuePair<string, string>(
            "PTK_UNIX_REGISTRY_FIXTURE_FACTS",
            facts);
        yield return new KeyValuePair<string, string>(
            "PTK_UNIX_REGISTRY_FIXTURE_ARM",
            arm);
        yield return new KeyValuePair<string, string>(
            "PTK_UNIX_REGISTRY_FIXTURE_ARMED",
            armed);
        yield return new KeyValuePair<string, string>(
            "PTK_UNIX_REGISTRY_FIXTURE_RELEASE",
            release);
        yield return new KeyValuePair<string, string>(
            "PTK_UNIX_REGISTRY_FIXTURE_DESCENDANT",
            descendant);
    }

    private static MatchedPackageFacts Package(string host, string broker) => new(
        host,
        Digest('1'),
        Digest('2'),
        Digest('3'),
        Digest('4'),
        [new MatchedPackageArtifactPath(MatchedPackageRole.GuardianHelper, broker)]);

    private static GuardianHostSupervisorPins Pins(MatchedPackageFacts package) => new(
        package.HostExecutableDigest,
        package.HostBuildDigest,
        package.PublicContractDigest,
        Digest('5'),
        Digest('6'),
        package.PackageManifestDigest);

    private static nuint Handle(Microsoft.Win32.SafeHandles.SafePipeHandle handle) =>
        unchecked((nuint)handle.DangerousGetHandle());

    private static Sha256Digest Digest(char value) => new(new string(value, 64));

    private static int GetProcessGroup(int processId) => GetPgid(processId);

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetPgid(int processId);

    private static void AssertOrder(string value, params string[] terms)
    {
        var previous = -1;
        foreach (var term in terms)
        {
            var current = value.IndexOf(term, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{term}' after offset {previous}.");
            previous = current;
        }
    }

    private static string BrokerSourcePath() => Path.Combine(
        FindRepositoryRoot(),
        "server",
        "PtkMcpGuardian",
        "Native",
        "ptk_guardian_broker.c");

    private static string HostSourcePath() => Path.Combine(
        FindRepositoryRoot(),
        "server",
        "PtkMcpGuardian.Tests",
        "Native",
        "ptk_unix_host_fixture.c");

    private static string RegistryHostSourcePath() => Path.Combine(
        FindRepositoryRoot(),
        "server",
        "PtkMcpGuardian.Tests",
        "Native",
        "ptk_unix_registry_host_fixture.c");

    private static string TemporaryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ptk-unix-launcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void KillBestEffort(IEnumerable<int> processIds)
    {
        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited) process.Kill();
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private sealed record RegistryFixtureFacts(
        int HostPid,
        int BrokerPid,
        ulong BrokerStartIdentityHigh,
        ulong BrokerStartIdentityLow,
        int WorkerPid,
        ulong WorkerStartIdentityHigh,
        ulong WorkerStartIdentityLow)
    {
        internal GuardianHostContainmentIdentity ToContainmentIdentity() => new(
            checked((uint)BrokerPid),
            BrokerStartIdentityHigh,
            BrokerStartIdentityLow,
            checked((uint)WorkerPid),
            WorkerStartIdentityHigh,
            WorkerStartIdentityLow);
    }
}
