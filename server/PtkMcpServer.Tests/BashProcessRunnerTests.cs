using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PtkMcpServer.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BashProcessEnvironmentCollection
{
    public const string Name = "Bash process environment";
}

[Collection(BashProcessEnvironmentCollection.Name)]
public sealed class BashProcessRunnerTests : IDisposable
{
    private static readonly string[] PoisonedStartupNames =
    [
        "BASH_ENV",
        "ENV",
        "SHELLOPTS",
        "BASHOPTS",
        "BASH_COMPAT",
        "LD_PRELOAD",
        "LD_AUDIT",
        "LD_LIBRARY_PATH",
        "DYLD_INSERT_LIBRARIES",
        "BASH_FUNC_ptk_test%%",
    ];

    private readonly string _root =
        Directory.CreateTempSubdirectory("ptk-bash-runner-").FullName;

    [Fact]
    public void Bash_identity_pins_the_startup_digest_and_detects_file_replacement()
    {
        var executable = Path.Combine(_root, "bash-image");
        var initial = Encoding.UTF8.GetBytes("startup bash image");
        File.WriteAllBytes(executable, initial);

        var identity = BashExecutableIdentity.TryCapture(executable);

        Assert.NotNull(identity);
        Assert.Equal(Path.GetFullPath(executable), identity.ExecutablePath);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(initial)).ToLowerInvariant(),
            identity.BinaryDigest);
        Assert.Equal($"bash_sha256_{identity.BinaryDigest}", identity.AuditIdentityCode);
        Assert.True(identity.MatchesCurrentFile());

        File.WriteAllText(executable, "replacement bash image", new UTF8Encoding(false));

        Assert.False(identity.MatchesCurrentFile());
    }

    [Fact]
    public void Executable_identities_detect_same_byte_Unix_mode_drift()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = CreateExecutable(
            "mode-drift-image",
            "#!/bin/sh\nexit 0\n");
        var bash = BashExecutableIdentity.TryCapture(path);
        var rtk = RtkExecutableIdentity.TryCapture(path);
        Assert.NotNull(bash);
        Assert.NotNull(rtk);
        Assert.True(bash.MatchesCurrentFile());
        Assert.True(rtk.MatchesCurrentFile());

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.False(bash.MatchesCurrentFile());
        Assert.False(rtk.MatchesCurrentFile());
    }

    [Fact]
    public void Executable_identity_rejects_growth_beyond_the_frozen_hash_bound()
    {
        var path = Path.Combine(_root, "growing-executable-image");
        File.WriteAllText(path, "bounded image", new UTF8Encoding(false));
        try
        {
            ExecutableFileIdentity.BeforeHashForTests = capturedPath =>
            {
                using var stream = new FileStream(
                    capturedPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.SetLength((128L * 1024 * 1024) + 1);
            };

            Assert.Null(ExecutableFileIdentity.TryCapture(path));
        }
        finally
        {
            ExecutableFileIdentity.BeforeHashForTests = null;
        }
    }

    [Fact]
    public async Task Validation_refuses_a_changed_startup_identity_without_starting_it()
    {
        var bash = CaptureImage("changed-validation-bash");
        var dispatch = CreateDispatch(
            "if true; then printf hello; fi",
            bash,
            Path.GetFullPath(Path.Combine(_root, "unused-rtk")));
        File.WriteAllText(
            bash.ExecutablePath,
            "replacement after startup",
            new UTF8Encoding(false));
        var startedRecords = 0;

        var result = await BashProcessRunner.ValidateAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(5),
            CancellationToken.None,
            () =>
            {
                startedRecords++;
                return ValueTask.FromResult(true);
            });

        Assert.Equal(BashSyntaxValidationStatus.IdentityChanged, result.Status);
        Assert.False(result.ProcessStarted);
        Assert.Null(result.ExitCode);
        Assert.Equal(0, startedRecords);
    }

    [Fact]
    public void Validation_start_info_has_exact_argv_cwd_and_scrubbed_startup_environment()
    {
        const string script = "if true; then printf '%s\\n' 'hello world'; fi";
        var bash = CaptureImage("validation-bash");

        var startInfo = WithPoisonedStartupEnvironment(() =>
            BashProcessRunner.CreateValidationStartInfo(bash, script, _root));

        Assert.Equal(bash.ExecutablePath, startInfo.FileName);
        Assert.Equal(_root, startInfo.WorkingDirectory);
        Assert.Equal(
            ["--noprofile", "--norc", "-n", "-c", script],
            startInfo.ArgumentList);
        AssertDirectProcess(startInfo);
        AssertScrubbed(startInfo);
    }

    [Fact]
    public void Execution_start_info_has_exact_rtk_proxy_argv_cwd_and_scrubbed_environment()
    {
        const string script = "if true; then printf '%s\\n' 'hello world'; fi";
        var bash = CaptureImage("execution-bash");
        var rtkPath = Path.GetFullPath(Path.Combine(_root, "trusted-rtk"));
        var dispatch = CreateDispatch(script, bash, rtkPath);

        var startInfo = WithPoisonedStartupEnvironment(() =>
            BashProcessRunner.CreateExecutionStartInfo(dispatch));

        Assert.Equal(rtkPath, startInfo.FileName);
        Assert.Equal(_root, startInfo.WorkingDirectory);
        Assert.Equal(
            ["proxy", "--", bash.ExecutablePath, "--noprofile", "--norc", "-c", script],
            startInfo.ArgumentList);
        AssertDirectProcess(startInfo);
        AssertScrubbed(startInfo);
    }

    [Fact]
    public async Task Real_bash_syntax_validation_does_not_execute_a_valid_side_effect_script()
    {
        var bash = TryCaptureRealBash();
        if (bash is null) return;

        var marker = Path.Combine(_root, "must-not-exist");
        var quotedMarker = BashSingleQuote(marker);
        var script = $"if true; then printf side-effect > '{quotedMarker}'; fi";
        var dispatch = CreateDispatch(
            script,
            bash,
            Path.GetFullPath(Path.Combine(_root, "unused-rtk")));
        var startedRecords = 0;

        var result = await BashProcessRunner.ValidateAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(5),
            CancellationToken.None,
            () =>
            {
                startedRecords++;
                return ValueTask.FromResult(true);
            },
            validationLimit: TimeSpan.FromSeconds(2));

        Assert.Equal(BashSyntaxValidationStatus.Valid, result.Status);
        Assert.True(result.ProcessStarted);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, startedRecords);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Real_bash_validator_scrubs_hostile_startup_environment_and_never_executes_input()
    {
        var bash = TryCaptureRealBash();
        if (bash is null) return;

        var bashEnvMarker = Path.Combine(_root, "bash-env-must-not-run");
        var functionMarker = Path.Combine(_root, "function-must-not-run");
        var submittedMarker = Path.Combine(_root, "submitted-must-not-run");
        var bashEnv = Path.Combine(_root, "hostile-bash-env");
        File.WriteAllText(
            bashEnv,
            $"printf bash-env > '{BashSingleQuote(bashEnvMarker)}'\n" +
            "ptk_guard\n" +
            "if then\n",
            new UTF8Encoding(false));
        var exportedFunction =
            $"() {{ printf function > '{BashSingleQuote(functionMarker)}'; }}";
        var script =
            $"if true; then printf submitted > '{BashSingleQuote(submittedMarker)}'; fi";
        var dispatch = CreateDispatch(
            script,
            bash,
            Path.GetFullPath(Path.Combine(_root, "unused-rtk")));
        var startedRecords = 0;

        var result = await WithEnvironmentAsync(
            new Dictionary<string, string?>
            {
                ["BASH_ENV"] = bashEnv,
                ["BASH_FUNC_ptk_guard%%"] = exportedFunction,
            },
            () => BashProcessRunner.ValidateAsync(
                dispatch,
                DateTimeOffset.UtcNow.AddSeconds(5),
                CancellationToken.None,
                () =>
                {
                    startedRecords++;
                    return ValueTask.FromResult(true);
                },
                validationLimit: TimeSpan.FromSeconds(2)));

        Assert.Equal(BashSyntaxValidationStatus.Valid, result.Status);
        Assert.True(result.ProcessStarted);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, startedRecords);
        Assert.False(File.Exists(bashEnvMarker));
        Assert.False(File.Exists(functionMarker));
        Assert.False(File.Exists(submittedMarker));
    }

    [Fact]
    public async Task Real_bash_syntax_validation_rejects_invalid_input()
    {
        var bash = TryCaptureRealBash();
        if (bash is null) return;

        const string script = "if true; then";
        var dispatch = CreateDispatch(
            script,
            bash,
            Path.GetFullPath(Path.Combine(_root, "unused-rtk")));

        var result = await BashProcessRunner.ValidateAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(5),
            CancellationToken.None,
            () => ValueTask.FromResult(true),
            validationLimit: TimeSpan.FromSeconds(2));

        Assert.Equal(BashSyntaxValidationStatus.SyntaxInvalid, result.Status);
        Assert.True(result.ProcessStarted);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Timed_out_validator_process_is_killed_on_Unix()
    {
        if (OperatingSystem.IsWindows()) return;

        var pidFile = Path.Combine(_root, "validator.pid");
        var fakeBashPath = Path.Combine(_root, "fake-bash");
        File.WriteAllText(
            fakeBashPath,
            $"#!/bin/sh\nprintf '%s' \"$$\" > '{BashSingleQuote(pidFile)}'\nsleep 30\n",
            new UTF8Encoding(false));
        File.SetUnixFileMode(
            fakeBashPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var fakeBash = BashExecutableIdentity.TryCapture(fakeBashPath);
        Assert.NotNull(fakeBash);
        var dispatch = CreateDispatch(
            "if true; then",
            fakeBash,
            Path.GetFullPath(Path.Combine(_root, "unused-rtk")));

        var result = await BashProcessRunner.ValidateAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(5),
            CancellationToken.None,
            () => ValueTask.FromResult(true),
            validationLimit: TimeSpan.FromMilliseconds(500));

        Assert.Equal(BashSyntaxValidationStatus.TimedOut, result.Status);
        Assert.True(result.ProcessStarted);
        Assert.True(File.Exists(pidFile));
        var pid = int.Parse(File.ReadAllText(pidFile));
        Assert.True(await ProcessExitedAsync(pid));
    }

    [Fact]
    public async Task Validator_budget_kills_process_while_started_audit_is_still_blocked()
    {
        if (OperatingSystem.IsWindows()) return;

        var pidFile = Path.Combine(_root, "blocked-audit-validator.pid");
        var fakeBashPath = CreateExecutable(
            "blocked-audit-bash",
            $"#!/bin/sh\nprintf '%s' \"$$\" > '{BashSingleQuote(pidFile)}'\nsleep 30\n");
        var fakeBash = BashExecutableIdentity.TryCapture(fakeBashPath);
        Assert.NotNull(fakeBash);
        var dispatch = CreateDispatch(
            "if true; then",
            fakeBash,
            Path.GetFullPath(Path.Combine(_root, "unused-rtk")));
        var stoppedBeforeAuditReturned = false;

        var result = await BashProcessRunner.ValidateAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(5),
            CancellationToken.None,
            async () =>
            {
                for (var attempt = 0; attempt < 40 && !File.Exists(pidFile); attempt++)
                    await Task.Delay(10);
                // The fixed validator cap triggers the kill; the runner then
                // gets its separate two-second root-stop grace to reap it.
                await Task.Delay(2500);
                if (File.Exists(pidFile))
                {
                    stoppedBeforeAuditReturned = await ProcessExitedAsync(
                        int.Parse(File.ReadAllText(pidFile)));
                }
                return true;
            },
            validationLimit: TimeSpan.FromMilliseconds(500));

        Assert.Equal(BashSyntaxValidationStatus.TimedOut, result.Status);
        Assert.True(result.ProcessStarted);
        Assert.True(File.Exists(pidFile));
        Assert.True(result.RootTerminationConfirmed);
        Assert.True(stoppedBeforeAuditReturned);
    }

    [Fact]
    public async Task Validator_records_a_successful_start_even_when_start_consumes_its_budget()
    {
        if (OperatingSystem.IsWindows()) return;

        var fakeBashPath = CreateExecutable(
            "post-start-budget-bash",
            "#!/bin/sh\nsleep 30\n");
        var fakeBash = BashExecutableIdentity.TryCapture(fakeBashPath);
        Assert.NotNull(fakeBash);
        var dispatch = CreateDispatch(
            "if true; then",
            fakeBash,
            Path.GetFullPath(Path.Combine(_root, "unused-rtk")));
        var startedRecords = 0;

        var result = await BashProcessRunner.ValidateAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(5),
            CancellationToken.None,
            () =>
            {
                startedRecords++;
                return ValueTask.FromResult(true);
            },
            validationLimit: TimeSpan.FromMilliseconds(50),
            afterProcessStartedForTests: () => Thread.Sleep(100));

        Assert.Equal(BashSyntaxValidationStatus.TimedOut, result.Status);
        Assert.True(result.ProcessStarted);
        Assert.True(result.RootTerminationConfirmed);
        Assert.Equal(1, startedRecords);
    }

    [Fact]
    public async Task Validator_pipe_drains_cannot_outlive_the_validation_deadline()
    {
        if (OperatingSystem.IsWindows()) return;

        var childPidFile = Path.Combine(_root, "validator-pipe-child.pid");
        var fakeBashPath = CreateExecutable(
            "validator-pipe-holder",
            $"#!/bin/sh\nsleep 30 &\nprintf '%s' \"$!\" > '{BashSingleQuote(childPidFile)}'\nexit 0\n");
        var fakeBash = BashExecutableIdentity.TryCapture(fakeBashPath);
        Assert.NotNull(fakeBash);
        var dispatch = CreateDispatch(
            "if true; then",
            fakeBash,
            Path.GetFullPath(Path.Combine(_root, "unused-rtk")));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await BashProcessRunner.ValidateAsync(
                dispatch,
                DateTimeOffset.UtcNow.AddSeconds(5),
                CancellationToken.None,
                () => ValueTask.FromResult(true),
                validationLimit: TimeSpan.FromMilliseconds(500));

            stopwatch.Stop();
            Assert.Equal(BashSyntaxValidationStatus.TimedOut, result.Status);
            Assert.True(result.ProcessStarted);
            Assert.True(result.RootTerminationConfirmed);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(childPidFile));
        }
        finally
        {
            if (File.Exists(childPidFile))
                TryKillProcess(int.Parse(File.ReadAllText(childPidFile)));
        }
    }

    [Fact]
    public async Task Actual_bash_ignores_hostile_login_profile_and_executes_exact_script_once()
    {
        var bash = TryCaptureRealBash();
        if (bash is null) return;

        var hostileHome = Directory.CreateDirectory(Path.Combine(_root, "hostile-home"));
        var profileMarker = Path.Combine(_root, "profile-must-not-run");
        File.WriteAllText(
            Path.Combine(hostileHome.FullName, ".bash_profile"),
            $"printf profile > '{BashSingleQuote(profileMarker)}'\n",
            new UTF8Encoding(false));
        var rtk = CreateForwardingRtk("profile-rtk", hostileHome.FullName);
        var executionMarker = Path.Combine(_root, "actual-script-runs");
        var script =
            $"if true; then printf 'exact-script\\n' >> '{BashSingleQuote(executionMarker)}'; printf executed; fi";
        var dispatch = CreateDispatch(script, bash, rtk.ExecutablePath);

        var result = await BashProcessRunner.ExecuteAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(5),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(InvokeDisposition.Completed, result.Disposition);
        Assert.True(result.UserExecutionStarted);
        Assert.Equal("executed", result.Output);
        Assert.Null(result.ExitCode);
        Assert.False(File.Exists(profileMarker));
        Assert.Equal(["exact-script"], File.ReadAllLines(executionMarker));
        Assert.Equal(1, InvocationCount(rtk.InvocationLog));
    }

    [Fact]
    public async Task Execute_preserves_stdout_stderr_and_nonzero_exit_as_completed()
    {
        var bash = TryCaptureRealBash();
        if (bash is null) return;

        var rtk = CreateForwardingRtk("stream-rtk");
        const string script =
            "if true; then printf 'stdout-one\\n'; printf 'stderr-one\\nstderr-two\\n' >&2; exit 23; fi";
        var dispatch = CreateDispatch(script, bash, rtk.ExecutablePath);

        var result = await BashProcessRunner.ExecuteAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(5),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(InvokeDisposition.Completed, result.Disposition);
        Assert.True(result.UserExecutionStarted);
        Assert.False(result.TimedOut);
        Assert.Equal(23, result.ExitCode);
        Assert.Equal("stdout-one\n", result.Output);
        Assert.NotNull(result.Stderr);
        Assert.Equal(["stderr-one", "stderr-two"], result.Stderr);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal(1, InvocationCount(rtk.InvocationLog));
    }

    [Fact]
    public async Task Execute_refuses_replaced_pinned_bash_without_invoking_rtk()
    {
        if (OperatingSystem.IsWindows()) return;

        var bashPath = CreateExecutable(
            "replaceable-bash",
            "#!/bin/sh\nprintf should-not-run\n");
        var bash = BashExecutableIdentity.TryCapture(bashPath);
        Assert.NotNull(bash);
        var rtk = CreateForwardingRtk("replacement-rtk");
        var dispatch = CreateDispatch("if true; then printf hello; fi", bash, rtk.ExecutablePath);
        File.WriteAllText(
            bash.ExecutablePath,
            "#!/bin/sh\nprintf replacement\n",
            new UTF8Encoding(false));

        var result = await BashProcessRunner.ExecuteAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(5),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(InvokeDisposition.NotStarted, result.Disposition);
        Assert.False(result.UserExecutionStarted);
        Assert.False(result.TimedOut);
        Assert.Equal("bash_identity_changed", result.AuditDetailCode);
        Assert.Contains("identity changed", Assert.Single(result.Errors));
        Assert.Equal(0, InvocationCount(rtk.InvocationLog));
    }

    [Fact]
    public async Task Execute_prestart_cancellation_has_a_typed_terminal_detail()
    {
        var bash = TryCaptureRealBash();
        if (bash is null) return;

        var rtk = CreateForwardingRtk("prestart-cancel-rtk");
        var dispatch = CreateDispatch("if true; then printf never; fi", bash, rtk.ExecutablePath);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var result = await BashProcessRunner.ExecuteAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(5),
            canceled.Token);

        Assert.False(result.Success);
        Assert.Equal(InvokeDisposition.NotStarted, result.Disposition);
        Assert.False(result.UserExecutionStarted);
        Assert.Equal("bash_execution_canceled_before_start", result.AuditDetailCode);
        Assert.Equal(0, InvocationCount(rtk.InvocationLog));
    }

    [Fact]
    public async Task Execute_truncates_bounded_stdout_with_warning_and_never_reruns()
    {
        var bash = TryCaptureRealBash();
        if (bash is null) return;

        var rtk = CreateForwardingRtk("bounded-output-rtk");
        var requestedBytes = BashProcessRunner.MaximumCapturedStreamBytes + 4096;
        var script = $"if true; then printf '%*s' {requestedBytes} ''; fi";
        var dispatch = CreateDispatch(script, bash, rtk.ExecutablePath);

        var result = await BashProcessRunner.ExecuteAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(10),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(InvokeDisposition.Completed, result.Disposition);
        Assert.True(result.UserExecutionStarted);
        Assert.Equal(
            BashProcessRunner.MaximumCapturedStreamBytes,
            Encoding.UTF8.GetByteCount(result.Output));
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("stdout exceeded", warning);
        Assert.Contains("did not rerun", warning);
        Assert.Equal(1, InvocationCount(rtk.InvocationLog));
    }

    [Fact]
    public async Task Timed_out_execution_stops_the_forwarded_bash_process_tree_without_rerun()
    {
        var bash = TryCaptureRealBash();
        if (bash is null) return;

        var rtk = CreateForwardingRtk("timeout-rtk");
        var bashPidFile = Path.Combine(_root, "execution-bash.pid");
        var childPidFile = Path.Combine(_root, "execution-child.pid");
        var script =
            $"if true; then printf '%s' \"$$\" > '{BashSingleQuote(bashPidFile)}'; " +
            $"sleep 30 & printf '%s' \"$!\" > '{BashSingleQuote(childPidFile)}'; wait; fi";
        var dispatch = CreateDispatch(script, bash, rtk.ExecutablePath);

        var result = await BashProcessRunner.ExecuteAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(1),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.TimedOut);
        Assert.Equal(InvokeDisposition.OutcomeUnknown, result.Disposition);
        Assert.True(result.UserExecutionStarted);
        Assert.Equal("bash_execution_timed_out", result.AuditDetailCode);
        Assert.Equal(1, InvocationCount(rtk.InvocationLog));
        Assert.True(File.Exists(bashPidFile));
        Assert.True(File.Exists(childPidFile));
        Assert.True(await ProcessExitedAsync(int.Parse(File.ReadAllText(bashPidFile))));
        Assert.True(await ProcessExitedAsync(int.Parse(File.ReadAllText(childPidFile))));
    }

    [Fact]
    public async Task Execution_pipe_drains_cannot_outlive_the_call_deadline()
    {
        var bash = TryCaptureRealBash();
        if (bash is null) return;

        var rtk = CreateForwardingRtk("pipe-deadline-rtk");
        var childPidFile = Path.Combine(_root, "execution-pipe-child.pid");
        var script =
            "cat <<'EOF' >/dev/null\nvalidated\nEOF\n" +
            $"sleep 30 & printf '%s' \"$!\" > '{BashSingleQuote(childPidFile)}'; exit 0";
        var dispatch = CreateDispatch(script, bash, rtk.ExecutablePath);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await BashProcessRunner.ExecuteAsync(
                dispatch,
                DateTimeOffset.UtcNow.AddMilliseconds(500),
                CancellationToken.None);

            stopwatch.Stop();
            Assert.False(result.Success);
            Assert.True(result.TimedOut);
            Assert.Equal(InvokeDisposition.OutcomeUnknown, result.Disposition);
            Assert.True(result.UserExecutionStarted);
            Assert.Contains(result.Errors, error =>
                error.Contains("descendant coverage is unknown", StringComparison.Ordinal));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Equal(1, InvocationCount(rtk.InvocationLog));
            Assert.True(File.Exists(childPidFile));
        }
        finally
        {
            if (File.Exists(childPidFile))
                TryKillProcess(int.Parse(File.ReadAllText(childPidFile)));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private BashExecutableIdentity CaptureImage(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "test bash image", new UTF8Encoding(false));
        return BashExecutableIdentity.TryCapture(path)
               ?? throw new InvalidOperationException("Could not capture test Bash identity.");
    }

    private ExecutionDispatch CreateDispatch(
        string script,
        BashExecutableIdentity bash,
        string rtkPath)
    {
        var plan = ExecutionPlanner.CreateBash(
            script,
            "auto",
            new RtkExecutableIdentity(rtkPath),
            bash,
            _root,
            ResolutionContext.Warm);
        return ExecutionDispatch.FromPlan(plan);
    }

    private static BashExecutableIdentity? TryCaptureRealBash()
    {
        if (OperatingSystem.IsWindows()) return null;
        var path = File.Exists("/bin/bash") ? "/bin/bash" : "/usr/bin/bash";
        return BashExecutableIdentity.TryCapture(path);
    }

    private static T WithPoisonedStartupEnvironment<T>(Func<T> action)
    {
        const string safeName = "PTK_BASH_TEST_SAFE";
        var names = PoisonedStartupNames.Append(safeName).ToArray();
        var previous = names.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        try
        {
            foreach (var name in PoisonedStartupNames)
                Environment.SetEnvironmentVariable(name, "poisoned");
            Environment.SetEnvironmentVariable(safeName, "preserved");
            return action();
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static async Task<T> WithEnvironmentAsync<T>(
        IReadOnlyDictionary<string, string?> values,
        Func<Task<T>> action)
    {
        var previous = values.Keys.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        try
        {
            foreach (var (name, value) in values)
                Environment.SetEnvironmentVariable(name, value);
            return await action();
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private ForwardingRtk CreateForwardingRtk(string name, string? home = null)
    {
        var invocationLog = Path.Combine(_root, $"{name}.invocations");
        var setHome = home is null
            ? string.Empty
            : $"HOME='{BashSingleQuote(home)}'\nexport HOME\n";
        var executablePath = CreateExecutable(
            name,
            "#!/bin/sh\n" +
            $"printf 'start\\n' >> '{BashSingleQuote(invocationLog)}'\n" +
            "if [ \"$1\" != proxy ] || [ \"$2\" != -- ]; then exit 97; fi\n" +
            "shift 2\n" +
            setHome +
            "exec \"$@\"\n");
        return new ForwardingRtk(executablePath, invocationLog);
    }

    private string CreateExecutable(string name, string contents)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("POSIX executable fixture required.");
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static int InvocationCount(string path) =>
        File.Exists(path) ? File.ReadAllLines(path).Length : 0;

    private static void AssertDirectProcess(ProcessStartInfo startInfo)
    {
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardOutputEncoding?.WebName);
        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardErrorEncoding?.WebName);
    }

    private static void AssertScrubbed(ProcessStartInfo startInfo)
    {
        Assert.DoesNotContain(startInfo.Environment.Keys, IsBashStartupVariable);
        Assert.Equal("preserved", startInfo.Environment["PTK_BASH_TEST_SAFE"]);
    }

    private static bool IsBashStartupVariable(string name) =>
        name.Equals("BASH_ENV", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ENV", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SHELLOPTS", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("BASHOPTS", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("BASH_COMPAT", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LD_PRELOAD", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LD_AUDIT", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LD_LIBRARY_PATH", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("DYLD_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("BASH_FUNC_", StringComparison.OrdinalIgnoreCase);

    private static string BashSingleQuote(string value) => value.Replace("'", "'\\''");

    private static async Task<bool> ProcessExitedAsync(int pid)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            await Task.Delay(25);
        }
        return false;
    }

    private static void TryKillProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(2000);
        }
        catch { }
    }

    private sealed record ForwardingRtk(string ExecutablePath, string InvocationLog);
}
