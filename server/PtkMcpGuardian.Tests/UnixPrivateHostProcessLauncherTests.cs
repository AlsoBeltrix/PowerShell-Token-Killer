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
        Assert.Single(Regex.Matches(source, @"\bwaitpid\s*\(").Cast<Match>());
        Assert.Contains("waitpid(host_pid, &status, WNOHANG)", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\b(?:waitid|wait3|wait4|system|popen|setsid|setpgrp)\s*\("), source);
        Assert.DoesNotContain("kill(-1", source, StringComparison.Ordinal);
        var brokerMain = source[source.IndexOf("static int broker_main(", StringComparison.Ordinal)..];
        AssertOrder(
            brokerMain,
            "pid_t host_pid = fork();",
            "setpgid(host_pid, host_pid)",
            "getpgid(host_pid) != host_pid",
            "EVENT_READY",
            "wait_for_start_command(command_read, liveness_read)",
            "write_full(child_release[1], &release");
        var gatedHostStart = source.IndexOf("static void exec_gated_host(", StringComparison.Ordinal);
        var childGate = source[gatedHostStart..source.IndexOf(
            "static bool wait_for_child_gate(",
            gatedHostStart,
            StringComparison.Ordinal)];
        AssertOrder(
            childGate,
            "setpgid(0, 0)",
            "read_full(release_read, &release",
            "execv(host_path, arguments)");
        AssertOrder(
            source,
            "signal_host_group(host_pid, SIGTERM)",
            "PTK_TERM_TO_KILL_MILLISECONDS",
            "signal_host_group(host_pid, SIGKILL)",
            "PTK_CONTAINMENT_DEADLINE_MILLISECONDS",
            "reap_direct_host(host_pid, &host_reaped)",
            "!group_exists(host_pid)",
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
}
