using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PtkMcpGuardian.Host;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Package;
using PtkMcpGuardian.Standalone;
using PtkMcpGuardian.Standalone.Fake;
using PtkMcpServer;
using PtkMcpServer.Audit;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class ProductionGuardianCompositionTests
{
    /// <summary>
    /// How long the native broker compiler may take. This is not a test
    /// deadline - see <see cref="CompositionTestTimeout"/> for that.
    /// </summary>
    private static readonly TimeSpan BrokerCompileTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a readiness poll may wait for a real host, worker, or job to
    /// reach the state it is waiting for.
    /// </summary>
    /// <remarks>
    /// These polls used to be budgeted in attempts (<c>attempt &lt; 200</c>),
    /// which is a unit that shrinks under exactly the conditions that make
    /// recovery slow: each attempt is a full MCP round trip, so on a loaded
    /// host the round trips stretch at the same moment the replacement apphost
    /// takes longer to launch, and the count runs out before readiness arrives.
    /// That produced a false red on `Unix_composition_recovers_real_host_...`
    /// during the first full x64 Linux battery, on a four-CPU host at load 5-8,
    /// while the same identity passed 3/3 in isolation (r6x-5). Wall clock does
    /// not shrink under load, so the budget now means what it says.
    ///
    /// Raising the attempt count instead would only move the cliff - one site
    /// had already been widened to 400 for the same reason. Any test using this
    /// budget needs an enclosing deadline that can contain it plus setup and
    /// teardown.
    /// </remarks>
    private static readonly TimeSpan ReadinessPollBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True while a readiness poll started at <paramref name="pollStarted"/> is
    /// still inside <see cref="ReadinessPollBudget"/>. Callers keep their own
    /// poll interval; this bounds only how long they may keep trying.
    /// </summary>
    private static bool PollingWithinBudget(long pollStarted) =>
        Stopwatch.GetElapsedTime(pollStarted) < ReadinessPollBudget;

    /// <summary>
    /// The single enclosing deadline for every identity in this class. All of
    /// them launch real guardian, host, and worker processes, so all of them
    /// are exposed to the same host-load effects.
    /// </summary>
    /// <remarks>
    /// It has to contain each identity's poll sites at a full
    /// <see cref="ReadinessPollBudget"/> plus real process setup and teardown;
    /// otherwise a poll that never succeeds surfaces as an opaque
    /// <see cref="OperationCanceledException"/> instead of failing on its own
    /// assertion, which is the difference between "recovery never became ready"
    /// and "something in this test hung". Three identities poll twice, so this
    /// is sized for two full budgets plus headroom.
    ///
    /// The identities that poll nothing need it just as much:
    /// `Composition_isolates_one_alias_worker_crash_from_a_second_alias` starts
    /// two real workers, crashes one and observes recovery, and failed 3/3 on
    /// x64 Linux against the old 30 s deadline while every other identity
    /// passed - a deadline chosen against a fast development Mac, on a
    /// four-CPU host that carries a steady background load. One deadline for
    /// the whole class is the rule least likely to rot into per-test tuning
    /// (r6x-5).
    ///
    /// It bounds only pathological hangs: a healthy run finishes each identity
    /// in seconds, so raising it cannot change any passing outcome.
    /// </remarks>
    private static readonly TimeSpan CompositionTestTimeout = TimeSpan.FromSeconds(180);
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly WorkerBootId Worker = new(
        Guid.Parse("22222222-2222-4222-8222-222222222222"));

    /// <summary>
    /// A real background job's output must reach the guardian's output store and
    /// become readable by opaque handle. The worker holds the bytes and cannot
    /// reach the guardian's output events, so the private host has to fetch and
    /// seal them at the job terminal; when it does not, the guardian's capability
    /// is registered and never written and every background job reports
    /// <c>recovery=unavailable</c> forever (r6x-2 #3). This identity is
    /// deliberately cross-platform: the same contract had only a Windows-gated
    /// test, which is why a platform-neutral defect read as Windows-specific.
    /// </summary>
    [Fact]
    public async Task Composition_seals_a_real_background_job_artifact_for_handle_recovery()
    {
        var auditRoot = TemporaryRoot("job-seal-audit");
        var outputRoot = TemporaryRoot("job-seal-output");
        string? nativeRoot = null;
        IPrivateHostProcessLauncher launcher;
        MatchedPackageFacts package;
        if (OperatingSystem.IsWindows())
        {
            launcher = new WindowsPrivateHostProcessLauncher();
            package = Package(FindServerAppHost());
        }
        else
        {
            nativeRoot = TemporaryRoot("job-seal-broker");
            Directory.CreateDirectory(nativeRoot);
            var broker = Path.Combine(nativeRoot, "PtkGuardianBroker");
            await CompileGuardianBrokerAsync(broker);
            launcher = new UnixPrivateHostProcessLauncher(broker);
            package = Package(FindServerAppHost(), broker);
        }

        var composition = ProductionGuardianComposition.Create(
            package,
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        // Polls for the job seal below, so it needs the polling deadline.
        using var timeout = new CancellationTokenSource(CompositionTestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            _ = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "job-seal-composition",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);

            var startedResponse = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "Write-Output 'PTK_SEALED_BACKGROUND_ARTIFACT'",
                        raw = true,
                        route = "pwsh",
                        background = true,
                        timeoutSeconds = 30,
                        session = "default",
                    },
                },
                timeout.Token);
            var jobId = MarkerInteger(
                ToolText(startedResponse, expectedError: false),
                "[job ");

            const string SealedMarker = "recovery=available: ptk_output handle=";
            var requestId = 3;
            string? sealedStatus = null;
            string? lastStatus = null;
            for (var pollStarted = Stopwatch.GetTimestamp();
                PollingWithinBudget(pollStarted);)
            {
                var statusResponse = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new
                    {
                        name = "ptk_job",
                        arguments = new
                        {
                            action = "status",
                            id = jobId,
                            session = "default",
                        },
                    },
                    timeout.Token);
                lastStatus = ToolText(statusResponse, expectedError: false);
                if (lastStatus.Contains(SealedMarker, StringComparison.Ordinal))
                {
                    sealedStatus = lastStatus;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
            Assert.True(
                sealedStatus is not null,
                "The background job never published a sealed artifact. " +
                $"Last status: {lastStatus}");

            var handleStart = sealedStatus!.IndexOf(
                SealedMarker,
                StringComparison.Ordinal) + SealedMarker.Length;
            var handleEnd = handleStart;
            while (handleEnd < sealedStatus.Length &&
                sealedStatus[handleEnd] is not (';' or '\r' or '\n' or ' '))
            {
                handleEnd++;
            }
            var handle = sealedStatus[handleStart..handleEnd];
            Assert.StartsWith("ptko_", handle, StringComparison.Ordinal);

            var artifactResponse = await RequestAsync(
                writer,
                reader,
                requestId++,
                "tools/call",
                new
                {
                    name = "ptk_output",
                    arguments = new { handle },
                },
                timeout.Token);
            Assert.Contains(
                "PTK_SEALED_BACKGROUND_ARTIFACT",
                ToolText(artifactResponse, expectedError: false),
                StringComparison.Ordinal);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
            Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
            Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
            Assert.Equal(0, composition.Supervisor.OwnedClientCount);
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
            if (nativeRoot is not null) DeleteRoot(nativeRoot);
        }
    }

    [Fact]
    public async Task Composition_freezes_package_session_and_guardian_owned_state()
    {
        var auditRoot = TemporaryRoot("audit");
        var outputRoot = TemporaryRoot("output");
        var package = Package(Path.Combine(Path.GetTempPath(), "never-launched-host"));
        var composition = ProductionGuardianComposition.Create(
            package,
            LocalAudit(auditRoot),
            new NeverLauncher(),
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        try
        {
            Assert.Equal(Guardian, composition.GuardianBootId);
            Assert.Equal(package.HostExecutableDigest, composition.Pins.HostExecutableDigest);
            Assert.Equal(package.HostBuildDigest, composition.Pins.HostBuildDigest);
            Assert.Equal(package.PublicContractDigest, composition.Pins.PublicContractDigest);
            Assert.Equal(package.PackageManifestDigest, composition.Pins.PackageManifestDigest);
            Assert.Equal(
                composition.SessionState.ConfigurationDigest,
                composition.Pins.ConfigurationDigest);
            Assert.Equal(
                composition.SessionState.CatalogDigest,
                composition.Pins.CatalogDigest);

            var state = composition.Supervisor.SnapshotState();
            Assert.Equal(Guardian, state.GuardianBootId);
            Assert.Equal(PublicHostState.Absent, state.Host.State);
            Assert.False(state.Host.ReadyForEffects);
            var session = Assert.Single(state.Sessions);
            Assert.Equal("default", session.Alias.Value);
            Assert.Equal(PublicSessionState.Lost, session.State);
            Assert.False(session.ReadyForEffects);
            Assert.True(session.WarmStateLost);
            Assert.Equal(BootstrapState.Unknown, session.BootstrapState);
        }
        finally
        {
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Composition_serves_the_real_private_host_before_public_initialize()
    {
        var auditRoot = TemporaryRoot("real-audit");
        var outputRoot = TemporaryRoot("real-output");
        string? nativeRoot = null;
        IPrivateHostProcessLauncher launcher;
        MatchedPackageFacts package;
        if (OperatingSystem.IsWindows())
        {
            launcher = new WindowsPrivateHostProcessLauncher();
            package = Package(FindServerAppHost());
        }
        else
        {
            nativeRoot = TemporaryRoot("native-broker");
            Directory.CreateDirectory(nativeRoot);
            var broker = Path.Combine(nativeRoot, "PtkGuardianBroker");
            await CompileGuardianBrokerAsync(broker);
            launcher = new UnixPrivateHostProcessLauncher(broker);
            package = Package(FindServerAppHost(), broker);
        }
        var composition = ProductionGuardianComposition.Create(
            package,
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(CompositionTestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-guardian-composition-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);

            var stateResponse = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_state",
                    arguments = new { },
                },
                timeout.Token);
            var state = PublicStateCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
            Assert.Equal(PublicHostState.Ready, state.Host.State);
            Assert.True(state.Host.ReadyForEffects);
            var session = Assert.Single(state.Sessions);
            Assert.NotNull(session.WorkerBootId);
            Assert.Equal(2, session.Generation?.Value);
            Assert.True(session.ReadyForEffects);

            var jobs = await RequestAsync(
                writer,
                reader,
                requestId: 3,
                "tools/call",
                new
                {
                    name = "ptk_job",
                    arguments = new { action = "list" },
                },
                timeout.Token);
            Assert.Equal("(no jobs)", ToolText(jobs, expectedError: false));

            var invocation = await RequestAsync(
                writer,
                reader,
                requestId: 4,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "Write-Output 'production-private-host'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "production-private-host",
                ToolText(invocation, expectedError: false),
                StringComparison.Ordinal);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
            Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
            Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
            Assert.Equal(0, composition.Supervisor.OwnedClientCount);
            Assert.Equal(0, composition.Supervisor.OwnedAttemptWatcherSetCount);
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
            if (nativeRoot is not null) DeleteRoot(nativeRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Composition_opens_a_dynamic_session_on_the_real_private_host()
    {
        var auditRoot = TemporaryRoot("real-open-audit");
        var outputRoot = TemporaryRoot("real-open-output");
        string? nativeRoot = null;
        IPrivateHostProcessLauncher launcher;
        MatchedPackageFacts package;
        if (OperatingSystem.IsWindows())
        {
            launcher = new WindowsPrivateHostProcessLauncher();
            package = Package(FindServerAppHost());
        }
        else
        {
            nativeRoot = TemporaryRoot("native-broker");
            Directory.CreateDirectory(nativeRoot);
            var broker = Path.Combine(nativeRoot, "PtkGuardianBroker");
            await CompileGuardianBrokerAsync(broker);
            launcher = new UnixPrivateHostProcessLauncher(broker);
            package = Package(FindServerAppHost(), broker);
        }
        var composition = ProductionGuardianComposition.Create(
            package,
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(CompositionTestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-guardian-open-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);

            var open = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_session",
                    arguments = new
                    {
                        action = "open",
                        name = "scratch",
                        allowColdBackground = true,
                    },
                },
                timeout.Token);
            var openText = ToolText(open, expectedError: false);
            Assert.Contains("session=scratch", openText, StringComparison.Ordinal);
            Assert.Contains("state=ready", openText, StringComparison.Ordinal);

            var stateResponse = await RequestAsync(
                writer,
                reader,
                requestId: 3,
                "tools/call",
                new
                {
                    name = "ptk_state",
                    arguments = new { },
                },
                timeout.Token);
            var state = PublicStateCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
            Assert.Equal(2, state.Sessions.Count);
            var scratch = state.Sessions[1];
            Assert.Equal("scratch", scratch.Alias.Value);
            Assert.Equal(PublicSessionState.Ready, scratch.State);
            Assert.Equal(2, scratch.Generation?.Value);
            Assert.True(scratch.ReadyForEffects);

            var invocation = await RequestAsync(
                writer,
                reader,
                requestId: 4,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "Write-Output 'scratch-worker'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "scratch",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "scratch-worker",
                ToolText(invocation, expectedError: false),
                StringComparison.Ordinal);

            var close = await RequestAsync(
                writer,
                reader,
                requestId: 5,
                "tools/call",
                new
                {
                    name = "ptk_session",
                    arguments = new
                    {
                        action = "close",
                        name = "scratch",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "state=cold",
                ToolText(close, expectedError: false),
                StringComparison.Ordinal);

            var closedStateResponse = await RequestAsync(
                writer,
                reader,
                requestId: 6,
                "tools/call",
                new
                {
                    name = "ptk_state",
                    arguments = new { },
                },
                timeout.Token);
            var closedState = PublicStateCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(closedStateResponse, expectedError: false)));
            Assert.Equal(2, closedState.Sessions.Count);
            var closedScratch = closedState.Sessions[1];
            Assert.Equal("scratch", closedScratch.Alias.Value);
            Assert.Equal(PublicSessionState.Cold, closedScratch.State);
            Assert.Null(closedScratch.WorkerBootId);
            Assert.False(closedScratch.ReadyForEffects);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
            Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
            Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
            Assert.Equal(0, composition.Supervisor.OwnedClientCount);
            Assert.Equal(0, composition.Supervisor.OwnedAttemptWatcherSetCount);
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
            if (nativeRoot is not null) DeleteRoot(nativeRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Composition_isolates_one_alias_worker_crash_from_a_second_alias()
    {
        // R6 acceptance matrix: "One worker crash affects only one alias"
        // (.agents/plans/mcp-resilience.md). The in-proc rig proves this shape
        // already; this proves it on the real apphost, against real worker
        // processes, which is what acceptance requires.
        var auditRoot = TemporaryRoot("real-isolation-audit");
        var outputRoot = TemporaryRoot("real-isolation-output");
        string? nativeRoot = null;
        IPrivateHostProcessLauncher launcher;
        MatchedPackageFacts package;
        if (OperatingSystem.IsWindows())
        {
            launcher = new WindowsPrivateHostProcessLauncher();
            package = Package(FindServerAppHost());
        }
        else
        {
            nativeRoot = TemporaryRoot("native-broker");
            Directory.CreateDirectory(nativeRoot);
            var broker = Path.Combine(nativeRoot, "PtkGuardianBroker");
            await CompileGuardianBrokerAsync(broker);
            launcher = new UnixPrivateHostProcessLauncher(broker);
            package = Package(FindServerAppHost(), broker);
        }
        var composition = ProductionGuardianComposition.Create(
            package,
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(CompositionTestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        Process? crashedWorker = null;
        var requestId = 0;
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: ++requestId,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-guardian-isolation-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);

            var open = await RequestAsync(
                writer,
                reader,
                requestId: ++requestId,
                "tools/call",
                new
                {
                    name = "ptk_session",
                    arguments = new
                    {
                        action = "open",
                        name = "scratch",
                        allowColdBackground = true,
                    },
                },
                timeout.Token);
            Assert.Contains("state=ready", ToolText(open, expectedError: false), StringComparison.Ordinal);

            // Warm state and worker identity are observed the way a model
            // observes them: by running something in the session.
            async Task<int> WorkerProcessIdAsync(string? session)
            {
                const string script = "'PTK_WORKER_PID=' + $PID";
                var arguments = session is null
                    ? (object)new
                    {
                        script,
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 30,
                    }
                    : new
                    {
                        script,
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 30,
                        session,
                    };
                var response = await RequestAsync(
                    writer,
                    reader,
                    requestId: ++requestId,
                    "tools/call",
                    new { name = "ptk_invoke", arguments },
                    timeout.Token);
                var text = ToolText(response, expectedError: false);
                var marker = text.IndexOf("PTK_WORKER_PID=", StringComparison.Ordinal);
                Assert.True(marker >= 0, $"worker PID was not reported: {text}");
                var digits = new string(text[(marker + "PTK_WORKER_PID=".Length)..]
                    .TakeWhile(char.IsAsciiDigit)
                    .ToArray());
                Assert.True(
                    int.TryParse(digits, out var processId) && processId > 0,
                    $"worker PID was not a process id: {text}");
                return processId;
            }

            var defaultSentinelResponse = await RequestAsync(
                writer,
                reader,
                requestId: ++requestId,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "$global:PtkDefaultSentinel = 'default-warm'; 'set'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 30,
                    },
                },
                timeout.Token);
            Assert.Contains("set", ToolText(defaultSentinelResponse, expectedError: false), StringComparison.Ordinal);

            var defaultPid = await WorkerProcessIdAsync(session: null);
            var scratchPid = await WorkerProcessIdAsync("scratch");
            Assert.NotEqual(defaultPid, scratchPid);

            async Task<PublicStateSnapshot> StateAsync()
            {
                var response = await RequestAsync(
                    writer,
                    reader,
                    requestId: ++requestId,
                    "tools/call",
                    new { name = "ptk_state", arguments = new { } },
                    timeout.Token);
                return PublicStateCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(response, expectedError: false)));
            }

            var before = await StateAsync();
            var defaultBefore = before.Sessions.Single(
                session => session.Alias.Value == "default");
            var scratchBefore = before.Sessions.Single(
                session => session.Alias.Value == "scratch");
            Assert.Equal(PublicSessionState.Ready, scratchBefore.State);

            // Kill only the scratch alias's real worker process.
            crashedWorker = Process.GetProcessById(scratchPid);
            crashedWorker.Kill(entireProcessTree: true);
            crashedWorker.WaitForExit(TimeSpan.FromSeconds(30));

            PublicSessionStateSnapshot? scratchAfter = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                var polled = await StateAsync();
                var candidate = polled.Sessions.Single(
                    session => session.Alias.Value == "scratch");
                if (candidate.State == PublicSessionState.Ready &&
                    candidate.Generation?.Value > scratchBefore.Generation?.Value)
                {
                    scratchAfter = candidate;
                    break;
                }
                // The default alias must never stop being usable while its
                // neighbour is being recovered.
                var neighbour = polled.Sessions.Single(
                    session => session.Alias.Value == "default");
                Assert.Equal(PublicSessionState.Ready, neighbour.State);
                Assert.True(neighbour.ReadyForEffects);
                await Task.Delay(250, timeout.Token);
            }

            Assert.NotNull(scratchAfter);
            Assert.True(
                scratchAfter.Generation?.Value > scratchBefore.Generation?.Value,
                "the crashed alias came back on a later generation");
            Assert.True(scratchAfter.WarmStateLost, "the crashed alias reports lost warm state");

            // The untouched alias kept its exact worker process, its generation,
            // its warm state, and its ability to run work.
            var after = await StateAsync();
            var defaultAfter = after.Sessions.Single(
                session => session.Alias.Value == "default");
            Assert.Equal(defaultBefore.Generation?.Value, defaultAfter.Generation?.Value);
            Assert.Equal(defaultBefore.WorkerBootId, defaultAfter.WorkerBootId);
            Assert.Equal(PublicSessionState.Ready, defaultAfter.State);
            Assert.Equal(defaultBefore.WarmStateLost, defaultAfter.WarmStateLost);
            Assert.Equal(defaultPid, await WorkerProcessIdAsync(session: null));

            var sentinelResponse = await RequestAsync(
                writer,
                reader,
                requestId: ++requestId,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "$global:PtkDefaultSentinel",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 30,
                    },
                },
                timeout.Token);
            Assert.Contains(
                "default-warm",
                ToolText(sentinelResponse, expectedError: false),
                StringComparison.Ordinal);

            // The recovered alias is a fresh baseline that still runs work.
            var recoveredResponse = await RequestAsync(
                writer,
                reader,
                requestId: ++requestId,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "'recovered-scratch'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 30,
                        session = "scratch",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "recovered-scratch",
                ToolText(recoveredResponse, expectedError: false),
                StringComparison.Ordinal);
            Assert.NotEqual(scratchPid, await WorkerProcessIdAsync("scratch"));

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
            Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
        }
        finally
        {
            KillProcessIfAlive(crashedWorker);
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
            if (nativeRoot is not null) DeleteRoot(nativeRoot);
        }
    }

    [Fact]
    public async Task Unix_composition_recovers_real_host_and_descendants_on_the_same_public_connection()
    {
        if (OperatingSystem.IsWindows()) return;

        var auditRoot = TemporaryRoot("unix-recovery-audit");
        var outputRoot = TemporaryRoot("unix-recovery-output");
        var nativeRoot = TemporaryRoot("unix-recovery-broker");
        Directory.CreateDirectory(nativeRoot);
        var broker = Path.Combine(nativeRoot, "PtkGuardianBroker");
        var backgroundMarker = Path.Combine(nativeRoot, "background-pid.txt");
        var escapedBackgroundMarker = backgroundMarker.Replace(
            "'",
            "''",
            StringComparison.Ordinal);
        Process? descendant = null;
        Process? backgroundProcess = null;
        await CompileGuardianBrokerAsync(broker);
        var launcher = new RecordingPrivateHostLauncher(
            new UnixPrivateHostProcessLauncher(broker));
        var composition = ProductionGuardianComposition.Create(
            Package(FindServerAppHost(), broker),
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(CompositionTestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-unix-host-recovery-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);

            var initialStateResponse = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new { name = "ptk_state", arguments = new { } },
                timeout.Token);
            var initialState = PublicStateCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(initialStateResponse, expectedError: false)));
            Assert.Equal(PublicHostState.Ready, initialState.Host.State);
            Assert.Equal(1, initialState.Host.Generation?.Value);

            var descendantResponse = await RequestAsync(
                writer,
                reader,
                requestId: 3,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "$child = Start-Process -FilePath '/usr/bin/tail' -ArgumentList '-f','/dev/null' -PassThru; 'PTK_CHILD_PID=' + $child.Id",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            var descendantProcessId = MarkerInteger(
                ToolText(descendantResponse, expectedError: false),
                "PTK_CHILD_PID=");
            descendant = Process.GetProcessById(descendantProcessId);
            Assert.False(descendant.HasExited);

            var backgroundResponse = await RequestAsync(
                writer,
                reader,
                requestId: 4,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = $"[IO.File]::WriteAllText('{escapedBackgroundMarker}', [string]$PID); Start-Sleep -Seconds 300",
                        raw = true,
                        route = "pwsh",
                        background = true,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            _ = ToolText(backgroundResponse, expectedError: false);
            var backgroundProcessId = 0;
            for (var pollStarted = Stopwatch.GetTimestamp();
                PollingWithinBudget(pollStarted);)
            {
                if (File.Exists(backgroundMarker) &&
                    int.TryParse(
                        await File.ReadAllTextAsync(backgroundMarker, timeout.Token),
                        CultureInfo.InvariantCulture,
                        out var publishedProcessId) &&
                    publishedProcessId > 0)
                {
                    backgroundProcessId = publishedProcessId;
                    break;
                }

                await Task.Delay(25, timeout.Token);
            }
            Assert.True(
                backgroundProcessId > 0,
                "The cold background job did not publish a valid PID.");
            backgroundProcess = Process.GetProcessById(backgroundProcessId);
            Assert.False(backgroundProcess.HasExited);

            var firstAuthority = await launcher.FirstAuthority.WaitAsync(timeout.Token);
            using (var firstHost = Process.GetProcessById(firstAuthority.ProcessId))
            {
                firstHost.Kill();
                await firstHost.WaitForExitAsync(timeout.Token);
            }
            await firstAuthority.ContainmentConfirmed.WaitAsync(timeout.Token);
            await descendant.WaitForExitAsync(timeout.Token);
            await backgroundProcess.WaitForExitAsync(timeout.Token);
            Assert.True(descendant.HasExited);
            Assert.True(backgroundProcess.HasExited);

            PublicStateSnapshot? recovered = null;
            var requestId = 5;
            for (var pollStarted = Stopwatch.GetTimestamp();
                PollingWithinBudget(pollStarted);)
            {
                var response = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new { name = "ptk_state", arguments = new { } },
                    timeout.Token);
                var candidate = PublicStateCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(response, expectedError: false)));
                if (candidate.Host.ReadyForEffects)
                {
                    recovered = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
            Assert.NotNull(recovered);
            Assert.Equal(2, recovered.Host.Generation?.Value);
            Assert.True(Assert.Single(recovered.Sessions).WarmStateLost);
            Assert.Equal(2, launcher.LaunchCount);

            var invocation = await RequestAsync(
                writer,
                reader,
                requestId,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "'recovered-unix-private-host'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "recovered-unix-private-host",
                ToolText(invocation, expectedError: false),
                StringComparison.Ordinal);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            KillProcessIfAlive(descendant);
            KillProcessIfAlive(backgroundProcess);
            descendant?.Dispose();
            backgroundProcess?.Dispose();
            DeleteRoot(auditRoot);
            DeleteRoot(nativeRoot);
        }
        Assert.False(Directory.Exists(outputRoot));
    }

    /// <summary>
    /// The private host must ignore the transitional idle watchdog: with an
    /// aggressive <c>PTK_IDLE_EXIT_SECONDS</c> it still survives past that
    /// interval with its warm state intact, so idle policy can never create a
    /// restart loop under a live public connection.
    /// </summary>
    /// <remarks>
    /// Deliberately cross-platform. This identity was Windows-gated and so
    /// returned vacuously on macOS and Linux, reporting green without executing
    /// (audit finding F1, gap G7) — the same class of blind spot that hid all
    /// three `r6x-2` defects.
    /// </remarks>
    [Fact]
    public async Task Private_host_ignores_the_transitional_idle_watchdog()
    {
        var auditRoot = TemporaryRoot("idle-audit");
        var outputRoot = TemporaryRoot("idle-output");
        string? nativeRoot = null;
        IPrivateHostProcessLauncher inner;
        MatchedPackageFacts package;
        if (OperatingSystem.IsWindows())
        {
            inner = new WindowsPrivateHostProcessLauncher();
            package = Package(FindServerAppHost());
        }
        else
        {
            nativeRoot = TemporaryRoot("idle-broker");
            Directory.CreateDirectory(nativeRoot);
            var broker = Path.Combine(nativeRoot, "PtkGuardianBroker");
            await CompileGuardianBrokerAsync(broker);
            inner = new UnixPrivateHostProcessLauncher(broker);
            package = Package(FindServerAppHost(), broker);
        }

        var launcher = new GatedContainmentLauncher(inner);
        launcher.ReleaseFirstContainmentConfirmation();
        var composition = ProductionGuardianComposition.Create(
            package,
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker,
            parentEnvironment: ParentEnvironmentWith(
                "PTK_IDLE_EXIT_SECONDS",
                "1"));
        using var timeout = new CancellationTokenSource(CompositionTestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-private-host-idle-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);
            var firstHostProcessId = await launcher.FirstHostProcessId.WaitAsync(timeout.Token);

            var mutation = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "$global:PtkR5IdleSentinel = 'survived'; 'sentinel-set'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "sentinel-set",
                ToolText(mutation, expectedError: false),
                StringComparison.Ordinal);

            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            using (var firstHost = Process.GetProcessById(firstHostProcessId))
                Assert.False(firstHost.HasExited);

            var stateResponse = await RequestAsync(
                writer,
                reader,
                requestId: 3,
                "tools/call",
                new
                {
                    name = "ptk_state",
                    arguments = new { },
                },
                timeout.Token);
            var state = PublicStateCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
            Assert.Equal(PublicHostState.Ready, state.Host.State);
            Assert.Equal(1, state.Host.Generation?.Value);
            var session = Assert.Single(state.Sessions);
            Assert.NotNull(session.WorkerBootId);
            Assert.Equal(2, session.Generation?.Value);
            Assert.False(session.WarmStateLost);

            var proof = await RequestAsync(
                writer,
                reader,
                requestId: 4,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "if ($global:PtkR5IdleSentinel -eq 'survived') { 'sentinel-present' } else { 'sentinel-absent' }",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "sentinel-present",
                ToolText(proof, expectedError: false),
                StringComparison.Ordinal);
            Assert.Equal(1, launcher.LaunchCount);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
            DeleteRoot(nativeRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public Task Composition_classifies_real_prewrite_loss() =>
        RunRealDispatchBarrierAsync(RealDispatchBarrier.BeforeWriteAuthorization);

    [Fact]
    public Task Composition_classifies_real_possibly_written_loss() =>
        RunRealDispatchBarrierAsync(RealDispatchBarrier.WriteStarting);

    [Fact]
    public Task Composition_retains_real_decoded_terminal_on_loss() =>
        RunRealDispatchBarrierAsync(RealDispatchBarrier.TerminalDecoded);

    private async Task RunRealDispatchBarrierAsync(
        RealDispatchBarrier barrier)
    {
        var auditRoot = TemporaryRoot($"barrier-{barrier}-audit");
        var outputRoot = TemporaryRoot($"barrier-{barrier}-output");
        var effectRoot = TemporaryRoot($"barrier-{barrier}-effect");
        Directory.CreateDirectory(effectRoot);
        var effectPath = Path.Combine(effectRoot, "effect.txt");
        var real = await RealHostLaunchAsync($"barrier-{barrier}-broker");
        var launcher = new GatedContainmentLauncher(real.Inner);
        launcher.ReleaseFirstContainmentConfirmation();
        var observer = new RealHostKillingDispatchObserver(barrier, launcher);
        var composition = ProductionGuardianComposition.Create(
            real.Package,
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker,
            dispatchObserver: observer);
        using var timeout = new CancellationTokenSource(CompositionTestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-host-dispatch-barrier-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);

            var escapedEffectPath = effectPath.Replace("'", "''", StringComparison.Ordinal);
            var response = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = $"[IO.File]::AppendAllText('{escapedEffectPath}', 'effect' + [Environment]::NewLine); 'barrier-effect'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            await observer.Triggered
                .WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);

            if (barrier == RealDispatchBarrier.TerminalDecoded)
            {
                Assert.Contains(
                    "barrier-effect",
                    ToolText(response, expectedError: false),
                    StringComparison.Ordinal);
            }
            else
            {
                var recovery = PublicRecoveryCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(response, expectedError: true)));
                if (barrier == RealDispatchBarrier.BeforeWriteAuthorization)
                {
                    Assert.Equal(
                        PublicRecoveryDetailCode.BackendLostBeforeDispatch,
                        recovery.DetailCode);
                    Assert.True(recovery.Retryable);
                    Assert.IsType<SessionReadyGate>(recovery.RetryGate);
                }
                else
                {
                    Assert.Equal(PublicRecoveryDetailCode.OutcomeUnknown, recovery.DetailCode);
                    Assert.False(recovery.Retryable);
                    Assert.Null(recovery.RetryAfterMilliseconds);
                    Assert.Null(recovery.RetryGate);
                }
            }

            _ = await launcher.ReplacementHostProcessId.WaitAsync(timeout.Token);
            PublicStateSnapshot? recovered = null;
            var requestId = 3;
            for (var pollStarted = Stopwatch.GetTimestamp();
                PollingWithinBudget(pollStarted);)
            {
                var stateResponse = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new
                    {
                        name = "ptk_state",
                        arguments = new { },
                    },
                    timeout.Token);
                var candidate = PublicStateCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
                if (candidate.Host.ReadyForEffects)
                {
                    recovered = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
            Assert.NotNull(recovered);
            Assert.Equal(2, recovered.Host.Generation?.Value);
            Assert.True(Assert.Single(recovered.Sessions).WarmStateLost);
            Assert.Equal(2, launcher.LaunchCount);

            var effectCount = File.Exists(effectPath)
                ? File.ReadLines(effectPath).Count(line =>
                    StringComparer.Ordinal.Equals(line, "effect"))
                : 0;
            if (barrier == RealDispatchBarrier.BeforeWriteAuthorization)
                Assert.Equal(0, effectCount);
            else if (barrier == RealDispatchBarrier.TerminalDecoded)
                Assert.Equal(1, effectCount);
            else
                Assert.InRange(effectCount, 0, 1);

            var postRecovery = await RequestAsync(
                writer,
                reader,
                requestId,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "'post-barrier-recovery'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "post-barrier-recovery",
                ToolText(postRecovery, expectedError: false),
                StringComparison.Ordinal);
            Assert.Equal(2, launcher.LaunchCount);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
            DeleteRoot(effectRoot);
            DeleteRoot(real.NativeRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Composition_requires_explicit_repair_after_ambiguous_reset()
    {
        var auditRoot = TemporaryRoot("ambiguous-reset-audit");
        var outputRoot = TemporaryRoot("ambiguous-reset-output");
        var real = await RealHostLaunchAsync("ambiguous-reset-broker");
        var launcher = new GatedContainmentLauncher(real.Inner);
        launcher.ReleaseFirstContainmentConfirmation();
        var observer = new RealHostKillingDispatchObserver(
            RealDispatchBarrier.WriteStarting,
            launcher);
        var composition = ProductionGuardianComposition.Create(
            real.Package,
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker,
            dispatchObserver: observer);
        using var timeout = new CancellationTokenSource(CompositionTestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-host-ambiguous-reset-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);

            var ambiguousResponse = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_reset",
                    arguments = new
                    {
                        session = "default",
                        expectedGeneration = 0,
                        force = false,
                        timeoutSeconds = 10,
                    },
                },
                timeout.Token);
            await observer.Triggered.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
            var ambiguous = PublicRecoveryCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(ambiguousResponse, expectedError: true)));
            Assert.Equal(PublicRecoveryDetailCode.OutcomeUnknown, ambiguous.DetailCode);
            Assert.False(ambiguous.Retryable);

            _ = await launcher.ReplacementHostProcessId.WaitAsync(timeout.Token);
            PublicStateSnapshot? recovered = null;
            var requestId = 3;
            for (var pollStarted = Stopwatch.GetTimestamp();
                PollingWithinBudget(pollStarted);)
            {
                var stateResponse = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new
                    {
                        name = "ptk_state",
                        arguments = new { },
                    },
                    timeout.Token);
                var candidate = PublicStateCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
                if (candidate.Host.ReadyForEffects)
                {
                    recovered = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }

            Assert.NotNull(recovered);
            Assert.Equal(2, recovered.Host.Generation?.Value);
            var unknown = Assert.Single(recovered.Sessions);
            Assert.Equal(PublicSessionState.RecoveryUnknown, unknown.State);
            Assert.False(unknown.ReadyForEffects);
            Assert.True(unknown.WarmStateLost);
            Assert.Equal(BootstrapState.Unknown, unknown.BootstrapState);

            var blockedResponse = await RequestAsync(
                writer,
                reader,
                requestId++,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "'must-not-run'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            var blocked = PublicRecoveryCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(blockedResponse, expectedError: true)));
            Assert.Equal(
                PublicRecoveryDetailCode.SessionRecoveryUnknown,
                blocked.DetailCode);
            Assert.False(blocked.Retryable);
            Assert.Equal(2, launcher.LaunchCount);

            var repairResponse = await RequestAsync(
                writer,
                reader,
                requestId++,
                "tools/call",
                new
                {
                    name = "ptk_reset",
                    arguments = new
                    {
                        session = "default",
                        expectedGeneration = 0,
                        force = false,
                        timeoutSeconds = 10,
                    },
                },
                timeout.Token);
            Assert.Contains(
                "state=ready",
                ToolText(repairResponse, expectedError: false),
                StringComparison.Ordinal);

            var repairedStateResponse = await RequestAsync(
                writer,
                reader,
                requestId++,
                "tools/call",
                new
                {
                    name = "ptk_state",
                    arguments = new { },
                },
                timeout.Token);
            var repairedState = PublicStateCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(repairedStateResponse, expectedError: false)));
            var repaired = Assert.Single(repairedState.Sessions);
            Assert.Equal(PublicSessionState.Ready, repaired.State);
            Assert.True(repaired.ReadyForEffects);
            Assert.Equal(BootstrapState.Restored, repaired.BootstrapState);

            var freshResponse = await RequestAsync(
                writer,
                reader,
                requestId,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "'after-explicit-repair'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "after-explicit-repair",
                ToolText(freshResponse, expectedError: false),
                StringComparison.Ordinal);
            Assert.Equal(2, launcher.LaunchCount);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
            Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
            Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
            Assert.Equal(0, composition.Supervisor.OwnedClientCount);
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
            DeleteRoot(real.NativeRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Windows_composition_recovers_a_real_host_on_the_same_public_connection()
    {
        if (!OperatingSystem.IsWindows()) return;

        var auditRoot = TemporaryRoot("recovery-audit");
        var outputRoot = TemporaryRoot("recovery-output");
        var launcher = new GatedContainmentLauncher(
            new WindowsPrivateHostProcessLauncher());
        var composition = ProductionGuardianComposition.Create(
            Package(FindServerAppHost()),
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(CompositionTestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-host-recovery-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);

            var initialResponse = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_state",
                    arguments = new { },
                },
                timeout.Token);
            var initial = PublicStateCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(initialResponse, expectedError: false)));
            Assert.Equal(PublicHostState.Ready, initial.Host.State);
            Assert.Equal(1, initial.Host.Generation?.Value);
            Assert.False(Assert.Single(initial.Sessions).WarmStateLost);
            var firstHostProcessId = await launcher.FirstHostProcessId.WaitAsync(timeout.Token);

            var warmMutation = await RequestAsync(
                writer,
                reader,
                requestId: 3,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "$global:PtkR5UndeclaredState = 'old-generation'; 'warm-state-set'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "warm-state-set",
                ToolText(warmMutation, expectedError: false),
                StringComparison.Ordinal);
            var warmProof = await RequestAsync(
                writer,
                reader,
                requestId: 4,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "if ($global:PtkR5UndeclaredState -eq 'old-generation') { 'warm-state-present' } else { 'warm-state-absent' }",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "warm-state-present",
                ToolText(warmProof, expectedError: false),
                StringComparison.Ordinal);
            var descendantResponse = await RequestAsync(
                writer,
                reader,
                requestId: 5,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "$child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/PING.EXE') -ArgumentList '-t','127.0.0.1' -PassThru; 'PTK_CHILD_PID=' + $child.Id",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            var descendantProcessId = MarkerInteger(
                ToolText(descendantResponse, expectedError: false),
                "PTK_CHILD_PID=");
            using var descendantProcess = Process.GetProcessById(descendantProcessId);
            Assert.False(descendantProcess.HasExited);

            using (var firstHost = Process.GetProcessById(firstHostProcessId))
            {
                firstHost.Kill();
                await firstHost.WaitForExitAsync(timeout.Token);
                Assert.True(firstHost.HasExited);
            }
            await launcher.FirstContainmentConfirmed.WaitAsync(timeout.Token);
            await descendantProcess.WaitForExitAsync(timeout.Token);
            Assert.True(descendantProcess.HasExited);
            Assert.Equal(1, launcher.LaunchCount);

            var recoveryStateResponse = await RequestAsync(
                writer,
                reader,
                requestId: 6,
                "tools/call",
                new
                {
                    name = "ptk_state",
                    arguments = new { },
                },
                timeout.Token);
            var recovering = PublicStateCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(recoveryStateResponse, expectedError: false)));
            Assert.Equal(PublicHostState.Recovering, recovering.Host.State);
            Assert.Equal(RecoveryPhase.Containment, recovering.Host.RecoveryPhase);
            Assert.Equal(1, recovering.Host.RecoveryAttempt);
            Assert.Equal(1, recovering.Host.Generation?.Value);
            Assert.False(recovering.Host.ReadyForEffects);
            var recoveringSession = Assert.Single(recovering.Sessions);
            Assert.Equal(PublicSessionState.Recovering, recoveringSession.State);
            Assert.True(recoveringSession.WarmStateLost);
            Assert.False(recoveringSession.ReadyForEffects);

            var refusedResponse = await RequestAsync(
                writer,
                reader,
                requestId: 7,
                "tools/call",
                new
                {
                    name = "ptk_job",
                    arguments = new { action = "list" },
                },
                timeout.Token);
            var refusal = PublicRecoveryCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(refusedResponse, expectedError: true)));
            Assert.Equal(PublicRecoveryDetailCode.HostRecovering, refusal.DetailCode);
            Assert.True(refusal.Retryable);
            Assert.Equal(RecoveryPhase.Containment, refusal.RecoveryPhase);
            Assert.Equal(1, refusal.RecoveryAttempt);
            var retryGate = Assert.IsType<SessionReadyGate>(refusal.RetryGate);
            Assert.Equal("default", retryGate.Alias.Value);
            Assert.Equal(1, launcher.LaunchCount);

            launcher.ReleaseFirstContainmentConfirmation();
            var replacementHostProcessId = await launcher.ReplacementHostProcessId
                .WaitAsync(timeout.Token);
            Assert.NotEqual(firstHostProcessId, replacementHostProcessId);

            PublicStateSnapshot? recovered = null;
            var requestId = 8;
            for (var pollStarted = Stopwatch.GetTimestamp();
                PollingWithinBudget(pollStarted);)
            {
                var stateResponse = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new
                    {
                        name = "ptk_state",
                        arguments = new { },
                    },
                    timeout.Token);
                var candidate = PublicStateCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
                if (candidate.Host.ReadyForEffects)
                {
                    recovered = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }

            Assert.NotNull(recovered);
            Assert.Equal(PublicHostState.Ready, recovered.Host.State);
            Assert.Equal(2, recovered.Host.Generation?.Value);
            var recoveredSession = Assert.Single(recovered.Sessions);
            Assert.Equal(PublicSessionState.Ready, recoveredSession.State);
            Assert.True(recoveredSession.ReadyForEffects);
            Assert.True(recoveredSession.WarmStateLost);
            Assert.Equal(BootstrapState.Restored, recoveredSession.BootstrapState);

            var invocation = await RequestAsync(
                writer,
                reader,
                requestId,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "if (Get-Variable -Name PtkR5UndeclaredState -Scope Global -ErrorAction Ignore) { 'warm-state-present' } else { 'warm-state-absent' }; 'recovered-private-host'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            var recoveredText = ToolText(invocation, expectedError: false);
            Assert.Contains("warm-state-absent", recoveredText, StringComparison.Ordinal);
            Assert.DoesNotContain("warm-state-present", recoveredText, StringComparison.Ordinal);
            Assert.Contains("recovered-private-host", recoveredText, StringComparison.Ordinal);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
            Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
            Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
            Assert.Equal(0, composition.Supervisor.OwnedClientCount);
            Assert.Equal(0, composition.Supervisor.OwnedAttemptWatcherSetCount);
        }
        finally
        {
            launcher.ReleaseFirstContainmentConfirmation();
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Composition_recovers_after_replacement_dies_during_startup()
    {
        var auditRoot = TemporaryRoot("startup-crash-audit");
        var outputRoot = TemporaryRoot("startup-crash-output");
        var real = await RealHostLaunchAsync("startup-crash-broker");
        var launcher = new CrashSecondLaunchLauncher(real.Inner);
        var composition = ProductionGuardianComposition.Create(
            real.Package,
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(CompositionTestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        var run = composition.RunAsync(input, output, timeout.Token);
        try
        {
            var firstHostProcessId = await launcher.FirstHostProcessId
                .WaitAsync(timeout.Token);
            PublicStateSnapshot? initial = null;
            for (var pollStarted = Stopwatch.GetTimestamp();
                PollingWithinBudget(pollStarted);)
            {
                var candidate = composition.Supervisor.SnapshotState();
                if (candidate.Host.ReadyForEffects)
                {
                    initial = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
            Assert.NotNull(initial);
            Assert.Equal(1, initial.Host.Generation?.Value);
            var firstHostBootId = initial.Host.BootId;

            using (var firstHost = Process.GetProcessById(firstHostProcessId))
            {
                firstHost.Kill();
                await firstHost.WaitForExitAsync(timeout.Token);
            }

            var failedReplacementProcessId = await launcher.FailedReplacementProcessId
                .WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
            var recoveredHostProcessId = await launcher.RecoveredHostProcessId
                .WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
            Assert.NotEqual(firstHostProcessId, failedReplacementProcessId);
            Assert.NotEqual(failedReplacementProcessId, recoveredHostProcessId);

            PublicStateSnapshot? recovered = null;
            for (var pollStarted = Stopwatch.GetTimestamp();
                PollingWithinBudget(pollStarted);)
            {
                var candidate = composition.Supervisor.SnapshotState();
                if (candidate.Host.ReadyForEffects)
                {
                    recovered = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
            Assert.NotNull(recovered);
            Assert.Equal(PublicHostState.Ready, recovered.Host.State);
            Assert.Equal(3, recovered.Host.Generation?.Value);
            Assert.NotEqual(firstHostBootId, recovered.Host.BootId);
            var session = Assert.Single(recovered.Sessions);
            Assert.True(session.ReadyForEffects);
            Assert.True(session.WarmStateLost);
            Assert.Equal(BootstrapState.Restored, session.BootstrapState);
            Assert.Equal(3, launcher.LaunchCount);

            input.CompleteWriting();
            await run.WaitAsync(timeout.Token);
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
            DeleteRoot(real.NativeRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Composition_keeps_a_real_job_tombstone_and_sealed_output()
    {
        var auditRoot = TemporaryRoot("job-tombstone-audit");
        var outputRoot = TemporaryRoot("job-tombstone-output");
        var real = await RealHostLaunchAsync("job-tombstone-broker");
        var launcher = new GatedContainmentLauncher(real.Inner);
        var composition = ProductionGuardianComposition.Create(
            real.Package,
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(CompositionTestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-host-job-tombstone-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);
            var firstHostProcessId = await launcher.FirstHostProcessId.WaitAsync(timeout.Token);

            var startedResponse = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "Write-Output 'PTK_R5_SEALED_JOB_OUTPUT'",
                        raw = true,
                        route = "pwsh",
                        background = true,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            var startedText = ToolText(startedResponse, expectedError: false);
            var jobId = MarkerInteger(startedText, "[job ");

            string? completedStatus = null;
            string? lastStatus = null;
            var requestId = 3;
            for (var pollStarted = Stopwatch.GetTimestamp();
                PollingWithinBudget(pollStarted);)
            {
                var statusResponse = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new
                    {
                        name = "ptk_job",
                        arguments = new
                        {
                            action = "status",
                            id = jobId,
                            session = "default",
                        },
                    },
                    timeout.Token);
                var candidate = ToolText(statusResponse, expectedError: false);
                lastStatus = candidate;
                if (candidate.Contains("exited 0", StringComparison.Ordinal) &&
                    candidate.Contains(
                        "recovery=available: ptk_output handle=",
                        StringComparison.Ordinal))
                {
                    completedStatus = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
            Assert.True(
                completedStatus is not null,
                $"The background job did not publish a sealed terminal. Last status: {lastStatus}");

            using (var firstHost = Process.GetProcessById(firstHostProcessId))
            {
                firstHost.Kill();
                await firstHost.WaitForExitAsync(timeout.Token);
                Assert.True(firstHost.HasExited);
            }
            await launcher.FirstContainmentConfirmed.WaitAsync(timeout.Token);
            Assert.Equal(1, launcher.LaunchCount);

            var tombstoneStatusResponse = await RequestAsync(
                writer,
                reader,
                requestId++,
                "tools/call",
                new
                {
                    name = "ptk_job",
                    arguments = new
                    {
                        action = "status",
                        id = jobId,
                        session = "default",
                    },
                },
                timeout.Token);
            var tombstoneStatus = ToolText(
                tombstoneStatusResponse,
                expectedError: false);
            Assert.Contains(
                $"job {jobId}: exited 0 (original host generation lost)",
                tombstoneStatus,
                StringComparison.Ordinal);
            Assert.Contains(
                "recovery=handle: ptk_output handle=",
                tombstoneStatus,
                StringComparison.Ordinal);

            var tombstoneOutputResponse = await RequestAsync(
                writer,
                reader,
                requestId++,
                "tools/call",
                new
                {
                    name = "ptk_job",
                    arguments = new
                    {
                        action = "output",
                        id = jobId,
                        offset = 0,
                        session = "default",
                    },
                },
                timeout.Token);
            var tombstoneOutput = ToolText(
                tombstoneOutputResponse,
                expectedError: false);
            Assert.Contains(
                "PTK_R5_SEALED_JOB_OUTPUT",
                tombstoneOutput,
                StringComparison.Ordinal);
            Assert.Contains(
                $"[job {jobId} exited 0 (original host generation lost)] next offset:",
                tombstoneOutput,
                StringComparison.Ordinal);

            var tombstoneListResponse = await RequestAsync(
                writer,
                reader,
                requestId++,
                "tools/call",
                new
                {
                    name = "ptk_job",
                    arguments = new
                    {
                        action = "list",
                        session = "default",
                    },
                },
                timeout.Token);
            var tombstoneList = ToolText(
                tombstoneListResponse,
                expectedError: false);
            Assert.Contains(
                $"job {jobId}: exited 0 (original host generation lost)",
                tombstoneList,
                StringComparison.Ordinal);
            Assert.Equal(1, launcher.LaunchCount);

            launcher.ReleaseFirstContainmentConfirmation();
            var replacementHostProcessId = await launcher.ReplacementHostProcessId
                .WaitAsync(timeout.Token);
            Assert.NotEqual(firstHostProcessId, replacementHostProcessId);

            PublicStateSnapshot? recovered = null;
            for (var pollStarted = Stopwatch.GetTimestamp();
                PollingWithinBudget(pollStarted);)
            {
                var stateResponse = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new
                    {
                        name = "ptk_state",
                        arguments = new { },
                    },
                    timeout.Token);
                var candidate = PublicStateCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
                if (candidate.Host.ReadyForEffects)
                {
                    recovered = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
            Assert.NotNull(recovered);
            Assert.Equal(2, recovered.Host.Generation?.Value);

            var confirmedStatusResponse = await RequestAsync(
                writer,
                reader,
                requestId,
                "tools/call",
                new
                {
                    name = "ptk_job",
                    arguments = new
                    {
                        action = "status",
                        id = jobId,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "exited 0 (original host generation lost)",
                ToolText(confirmedStatusResponse, expectedError: false),
                StringComparison.Ordinal);
            Assert.Equal(2, launcher.LaunchCount);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
            Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
            Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
            Assert.Equal(0, composition.Supervisor.OwnedClientCount);
            Assert.Equal(0, composition.Supervisor.OwnedAttemptWatcherSetCount);
        }
        finally
        {
            launcher.ReleaseFirstContainmentConfirmation();
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
            DeleteRoot(real.NativeRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Composition_never_replays_a_real_effect_when_the_worker_dies()
    {
        var auditRoot = TemporaryRoot("outcome-unknown-audit");
        var outputRoot = TemporaryRoot("outcome-unknown-output");
        string? nativeRoot = null;
        IPrivateHostProcessLauncher launcher;
        MatchedPackageFacts package;
        if (OperatingSystem.IsWindows())
        {
            launcher = new WindowsPrivateHostProcessLauncher();
            package = Package(FindServerAppHost());
        }
        else
        {
            nativeRoot = TemporaryRoot("outcome-unknown-broker");
            Directory.CreateDirectory(nativeRoot);
            var broker = Path.Combine(nativeRoot, "PtkGuardianBroker");
            await CompileGuardianBrokerAsync(broker);
            launcher = new UnixPrivateHostProcessLauncher(broker);
            package = Package(FindServerAppHost(), broker);
        }
        var composition = ProductionGuardianComposition.Create(
            package,
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(CompositionTestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-host-outcome-unknown-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);

            var ambiguousResponse = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "[System.Diagnostics.Process]::GetCurrentProcess().Kill()",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            var ambiguous = PublicRecoveryCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(ambiguousResponse, expectedError: true)));
            Assert.Equal(PublicRecoveryDetailCode.OutcomeUnknown, ambiguous.DetailCode);
            Assert.False(ambiguous.Retryable);
            Assert.Null(ambiguous.RetryAfterMilliseconds);
            Assert.Null(ambiguous.RecoveryPhase);
            Assert.Null(ambiguous.RecoveryAttempt);
            Assert.Null(ambiguous.RetryGate);

            // The alias recovers by replacing its own worker; the private host
            // is not relaunched. Waiting for a replacement host here is what
            // this identity used to do, and it hung for the full timeout after
            // the R6 worker cutover made per-alias worker recovery the design
            // (r6x-2 #2). Readiness is polled on the *session*, because that is
            // what decides whether an effect can be dispatched again - polling
            // only the host lets the follow-up invoke race a Starting alias.
            PublicStateSnapshot? recovered = null;
            var requestId = 3;
            for (var pollStarted = Stopwatch.GetTimestamp();
                PollingWithinBudget(pollStarted);)
            {
                var stateResponse = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new
                    {
                        name = "ptk_state",
                        arguments = new { },
                    },
                    timeout.Token);
                var candidate = PublicStateCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
                if (candidate.Host.ReadyForEffects &&
                    candidate.Sessions.Single().ReadyForEffects)
                {
                    recovered = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }

            Assert.NotNull(recovered);
            Assert.Equal(PublicHostState.Ready, recovered.Host.State);
            Assert.Equal(1, recovered.Host.Generation?.Value);
            var recoveredSession = Assert.Single(recovered.Sessions);
            Assert.True(recoveredSession.WarmStateLost);
            Assert.Equal(PublicSessionState.Ready, recoveredSession.State);
            Assert.True(
                recoveredSession.Generation?.Value > 1,
                $"The worker generation did not advance: {recoveredSession.Generation?.Value}.");

            var invocation = await RequestAsync(
                writer,
                reader,
                requestId,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "Write-Output 'effect-was-not-replayed'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "effect-was-not-replayed",
                ToolText(invocation, expectedError: false),
                StringComparison.Ordinal);
            Assert.Equal(
                1,
                PublicStateCodec.Decode(Encoding.UTF8.GetBytes(ToolText(
                    await RequestAsync(
                        writer,
                        reader,
                        requestId + 1,
                        "tools/call",
                        new { name = "ptk_state", arguments = new { } },
                        timeout.Token),
                    expectedError: false))).Host.Generation?.Value);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
            Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
            Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
            Assert.Equal(0, composition.Supervisor.OwnedClientCount);
            Assert.Equal(0, composition.Supervisor.OwnedAttemptWatcherSetCount);
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
            if (nativeRoot is not null) DeleteRoot(nativeRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    private static async Task<JsonElement> RequestAsync(
        StreamWriter writer,
        StreamReader reader,
        int requestId,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await WriteAsync(
            writer,
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = method,
                ["params"] = parameters,
            },
            cancellationToken);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            Assert.NotNull(line);
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (message.TryGetProperty("id", out var responseId) &&
                responseId.ValueKind == JsonValueKind.Number &&
                responseId.GetInt32() == requestId)
            {
                return message.Clone();
            }
        }
    }

    private static async Task WriteAsync(
        StreamWriter writer,
        object message,
        CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message);
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static string ToolText(JsonElement response, bool expectedError)
    {
        var result = response.GetProperty("result");
        Assert.Equal(expectedError, result.GetProperty("isError").GetBoolean());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        return Assert.IsType<string>(content.GetProperty("text").GetString());
    }

    private static int MarkerInteger(string text, string marker)
    {
        var markerOffset = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerOffset >= 0, $"Marker '{marker}' was absent from '{text}'.");
        var start = checked(markerOffset + marker.Length);
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;
        Assert.True(end > start, $"Marker '{marker}' had no integer in '{text}'.");
        return int.Parse(
            text.AsSpan(start, end - start),
            NumberStyles.None,
            CultureInfo.InvariantCulture);
    }

    private static AuditStartupConfiguration LocalAudit(string root) =>
        AuditStartupConfiguration.Load(
            root,
            configuredExportPath: null,
            static (_, _) => throw new InvalidOperationException(
                "Local-only test audit must not load export configuration."));

    private static OutputStoreOptions OutputOptions(string root) => new(
        root,
        TimeSpan.FromMinutes(5),
        TimeSpan.FromHours(1),
        MaximumArtifactBytes: 1024 * 1024,
        MaximumSessionBytes: 4 * 1024 * 1024,
        MaximumAggregateBytes: 8 * 1024 * 1024);

    private static KeyValuePair<string, string>[] ParentEnvironmentWith(
        string name,
        string value)
    {
        var environment = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment.Add(
                Assert.IsType<string>(entry.Key),
                Assert.IsType<string>(entry.Value));
        }
        environment[name] = value;
        return [.. environment];
    }

    /// <summary>
    /// The real private-host launcher for this platform and its matched package.
    /// On Unix it compiles the guardian broker into a temporary root, which the
    /// caller must delete through <see cref="DeleteRoot"/>.
    /// </summary>
    /// <remarks>
    /// Every identity that drives a real host needs this pair, and open-coding
    /// the platform branch per test is what let identities drift into being
    /// Windows-only by accident (audit F1).
    /// </remarks>
    private static async Task<RealHostLaunch> RealHostLaunchAsync(string label)
    {
        if (OperatingSystem.IsWindows())
        {
            return new RealHostLaunch(
                new WindowsPrivateHostProcessLauncher(),
                Package(FindServerAppHost()),
                NativeRoot: null);
        }

        var nativeRoot = TemporaryRoot(label);
        Directory.CreateDirectory(nativeRoot);
        var broker = Path.Combine(nativeRoot, "PtkGuardianBroker");
        await CompileGuardianBrokerAsync(broker);
        return new RealHostLaunch(
            new UnixPrivateHostProcessLauncher(broker),
            Package(FindServerAppHost(), broker),
            nativeRoot);
    }

    private sealed record RealHostLaunch(
        IPrivateHostProcessLauncher Inner,
        MatchedPackageFacts Package,
        string? NativeRoot);

    private static MatchedPackageFacts Package(
        string hostAppHost,
        string? guardianHelper = null) => new(
        hostAppHost,
        Digest('1'),
        Digest('2'),
        PublicToolContractResource.ComputeDigest(),
        Digest('6'),
        guardianHelper is null
            ? []
            : [new MatchedPackageArtifactPath(
                MatchedPackageRole.GuardianHelper,
                guardianHelper)]);

    private static string FindServerAppHost()
    {
        var configurationDirectory = Directory.GetParent(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) ??
            throw new InvalidOperationException("The test configuration directory is unavailable.");
        var repositoryRoot = FindRepositoryRoot();
        var path = Path.Combine(
            repositoryRoot,
            "server",
            "PtkMcpServer",
            "bin",
            configurationDirectory.Name,
            "net10.0",
            OperatingSystem.IsWindows() ? "PtkMcpServer.exe" : "PtkMcpServer");
        Assert.True(File.Exists(path), $"The private host apphost is absent: {path}");
        return path;
    }

    private static async Task CompileGuardianBrokerAsync(string outputPath)
    {
        var source = Path.Combine(
            FindRepositoryRoot(),
            "server",
            "PtkMcpGuardian",
            "Native",
            "ptk_guardian_broker.c");
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
            "-Wstrict-prototypes", "-Wmissing-prototypes", source, "-o", outputPath,
        })
        {
            start.ArgumentList.Add(argument);
        }
        using var compiler = Process.Start(start) ??
            throw new InvalidOperationException("The native broker compiler did not start.");
        var standardOutput = compiler.StandardOutput.ReadToEndAsync();
        var standardError = compiler.StandardError.ReadToEndAsync();
        await compiler.WaitForExitAsync().WaitAsync(BrokerCompileTimeout);
        Assert.True(
            compiler.ExitCode == 0,
            $"Native broker compile failed: {await standardOutput}{await standardError}");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static string TemporaryRoot(string kind)
    {
        var temporary = new DirectoryInfo(Path.GetTempPath());
        var canonical = OperatingSystem.IsMacOS() &&
            temporary.FullName.StartsWith("/var/", StringComparison.Ordinal)
            ? "/private" + temporary.FullName
            : temporary.ResolveLinkTarget(returnFinalTarget: true)?.FullName ??
                temporary.FullName;
        return Path.Combine(
            canonical,
            $"ptk-production-guardian-{kind}-{Guid.NewGuid():N}");
    }

    private static void KillProcessIfAlive(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
        }
    }

    // Nullable because the platform-branching identities only create a native
    // broker root off Windows.
    private static void DeleteRoot(string? root)
    {
        if (root is not null && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static Sha256Digest Digest(char value) => new(new string(value, 64));

    private sealed class NeverLauncher : IPrivateHostProcessLauncher
    {
        public PrivateHostProcessLaunchResult Launch(PrivateHostLaunchCommand command) =>
            throw new InvalidOperationException("The construction test must not launch a host.");
    }

    private sealed class RecordingPrivateHostLauncher(
        IPrivateHostProcessLauncher inner) : IPrivateHostProcessLauncher
    {
        private readonly TaskCompletionSource<IPrivateHostLaunchedProcess> _firstAuthority = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _launchCount;

        internal Task<IPrivateHostLaunchedProcess> FirstAuthority => _firstAuthority.Task;

        internal int LaunchCount => Volatile.Read(ref _launchCount);

        public PrivateHostProcessLaunchResult Launch(PrivateHostLaunchCommand command)
        {
            var result = inner.Launch(command);
            var count = Interlocked.Increment(ref _launchCount);
            if (count == 1 && result.LaunchedHost is not null)
                _firstAuthority.TrySetResult(result.LaunchedHost);
            return result;
        }
    }

    /// <summary>
    /// Observes and gates a real launcher. The inner launcher is supplied by the
    /// caller so this works on either platform — it used to hard-code the
    /// Windows launcher, which is why every identity using it was Windows-only
    /// and therefore vacuous on macOS and Linux.
    /// </summary>
    private sealed class GatedContainmentLauncher(IPrivateHostProcessLauncher inner)
        : IPrivateHostProcessLauncher
    {
        private readonly IPrivateHostProcessLauncher _inner = inner;
        private readonly TaskCompletionSource<int> _firstHostProcessId = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstContainmentConfirmed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstContainmentRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _replacementHostProcessId = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _launchCount;

        internal Task<int> FirstHostProcessId => _firstHostProcessId.Task;

        internal Task FirstContainmentConfirmed => _firstContainmentConfirmed.Task;

        internal Task<int> ReplacementHostProcessId => _replacementHostProcessId.Task;

        internal int LaunchCount => Volatile.Read(ref _launchCount);

        internal void ReleaseFirstContainmentConfirmation() =>
            _firstContainmentRelease.TrySetResult();

        public PrivateHostProcessLaunchResult Launch(PrivateHostLaunchCommand command)
        {
            var launchNumber = Interlocked.Increment(ref _launchCount);
            var result = _inner.Launch(command);
            if (result.Outcome != GuardianHostLaunchOutcome.Started)
                return result;

            var process = result.LaunchedHost!;
            if (launchNumber == 1)
            {
                _firstHostProcessId.TrySetResult(process.ProcessId);
                return new PrivateHostProcessLaunchResult(
                    GuardianHostLaunchOutcome.Started,
                    new GatedContainmentProcess(
                        process,
                        _firstContainmentConfirmed,
                        _firstContainmentRelease.Task));
            }

            if (launchNumber == 2)
                _replacementHostProcessId.TrySetResult(process.ProcessId);
            return result;
        }

        /// <summary>
        /// Wraps a launched host to gate its containment confirmation.
        /// </summary>
        /// <remarks>
        /// It must also forward <see cref="IUnixWorkerContainmentAuthority"/>:
        /// `PrivateHostAttemptFactory` recovers that authority with
        /// `_process as IUnixWorkerContainmentAuthority`, so a wrapper that
        /// implements only <see cref="IPrivateHostLaunchedProcess"/> turns into
        /// a `PlatformNotSupportedException` the moment a worker is contained on
        /// Unix. That silent interface loss is why every identity using this
        /// launcher was Windows-gated, and it fails as an unexplained startup
        /// failure rather than anything naming the cause.
        /// </remarks>
        private sealed class GatedContainmentProcess :
            IPrivateHostLaunchedProcess,
            IUnixWorkerContainmentAuthority
        {
            private readonly IPrivateHostLaunchedProcess _inner;
            private readonly Task _containmentConfirmed;

            internal GatedContainmentProcess(
                IPrivateHostLaunchedProcess inner,
                TaskCompletionSource firstContainmentConfirmed,
                Task containmentRelease)
            {
                _inner = inner;
                _containmentConfirmed = ConfirmContainmentAsync(
                    inner.ContainmentConfirmed,
                    firstContainmentConfirmed,
                    containmentRelease);
            }

            private IUnixWorkerContainmentAuthority UnixAuthority =>
                _inner as IUnixWorkerContainmentAuthority ??
                    throw new PlatformNotSupportedException(
                        "The wrapped host has no Unix worker containment authority.");

            public int ProcessId => _inner.ProcessId;

            public Task Exited => _inner.Exited;

            public Task ContainmentConfirmed => _containmentConfirmed;

            public void BeginContainment(GuardianHostContainmentDeadline deadline) =>
                _inner.BeginContainment(deadline);

            public Task RegisterPendingAsync(
                GuardianHostContainmentIdentity identity,
                CancellationToken cancellationToken) =>
                UnixAuthority.RegisterPendingAsync(identity, cancellationToken);

            public Task RegisterArmedAsync(
                GuardianHostContainmentIdentity identity,
                CancellationToken cancellationToken) =>
                UnixAuthority.RegisterArmedAsync(identity, cancellationToken);

            public Task RemoveAsync(
                GuardianHostContainmentIdentity identity,
                CancellationToken cancellationToken) =>
                UnixAuthority.RemoveAsync(identity, cancellationToken);

            public void Dispose() => _inner.Dispose();

            private static async Task ConfirmContainmentAsync(
                Task innerConfirmation,
                TaskCompletionSource firstContainmentConfirmed,
                Task containmentRelease)
            {
                await innerConfirmation.ConfigureAwait(false);
                firstContainmentConfirmed.TrySetResult();
                await containmentRelease.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Crashes the replacement host mid-startup. The inner launcher is supplied
    /// by the caller for the same reason <see cref="GatedContainmentLauncher"/>
    /// takes one — hard-coding the Windows launcher is what made this identity
    /// Windows-only, and it covers the initialize hard-kill barrier (audit
    /// E1.2), so off Windows that barrier had no real-process coverage at all.
    /// </summary>
    private sealed class CrashSecondLaunchLauncher(IPrivateHostProcessLauncher inner)
        : IPrivateHostProcessLauncher
    {
        private readonly IPrivateHostProcessLauncher _inner = inner;
        private readonly TaskCompletionSource<int> _firstHostProcessId = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _failedReplacementProcessId = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _recoveredHostProcessId = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _launchCount;

        internal Task<int> FirstHostProcessId => _firstHostProcessId.Task;

        internal Task<int> FailedReplacementProcessId =>
            _failedReplacementProcessId.Task;

        internal Task<int> RecoveredHostProcessId => _recoveredHostProcessId.Task;

        internal int LaunchCount => Volatile.Read(ref _launchCount);

        public PrivateHostProcessLaunchResult Launch(PrivateHostLaunchCommand command)
        {
            var launchNumber = Interlocked.Increment(ref _launchCount);
            var result = _inner.Launch(command);
            if (result.Outcome != GuardianHostLaunchOutcome.Started)
                return result;

            var processId = result.LaunchedHost!.ProcessId;
            if (launchNumber == 1)
            {
                _firstHostProcessId.TrySetResult(processId);
            }
            else if (launchNumber == 2)
            {
                _failedReplacementProcessId.TrySetResult(processId);
                using var process = Process.GetProcessById(processId);
                process.Kill();
            }
            else if (launchNumber == 3)
            {
                _recoveredHostProcessId.TrySetResult(processId);
            }
            return result;
        }
    }

    private enum RealDispatchBarrier
    {
        BeforeWriteAuthorization,
        WriteStarting,
        TerminalDecoded,
    }

    private sealed class RealHostKillingDispatchObserver(
        RealDispatchBarrier barrier,
        GatedContainmentLauncher launcher) : IGuardianHostSupervisorDispatchObserver
    {
        private readonly TaskCompletionSource _triggered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimed;

        internal Task Triggered => _triggered.Task;

        public async ValueTask BeforeWriteAuthorizationAsync(
            GuardianHostDispatchObservation observation,
            CancellationToken cancellationToken)
        {
            _ = observation;
            if (!TryClaim(RealDispatchBarrier.BeforeWriteAuthorization))
                return;

            await KillFirstHostAsync(cancellationToken).ConfigureAwait(false);
            await launcher.FirstContainmentConfirmed
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public void OnWriteStarting(GuardianHostDispatchObservation observation)
        {
            _ = observation;
            if (TryClaim(RealDispatchBarrier.WriteStarting))
                KillFirstHost();
        }

        public void OnTerminalDecoded(GuardianHostDispatchObservation observation)
        {
            _ = observation;
            if (TryClaim(RealDispatchBarrier.TerminalDecoded))
                KillFirstHost();
        }

        private bool TryClaim(RealDispatchBarrier candidate)
        {
            if (barrier != candidate || Interlocked.Exchange(ref _claimed, 1) != 0)
                return false;
            _triggered.TrySetResult();
            return true;
        }

        private async Task KillFirstHostAsync(CancellationToken cancellationToken)
        {
            var processId = await launcher.FirstHostProcessId
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            using var process = Process.GetProcessById(processId);
            process.Kill();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        private void KillFirstHost()
        {
            var processId = launcher.FirstHostProcessId.GetAwaiter().GetResult();
            using var process = Process.GetProcessById(processId);
            process.Kill();
        }
    }
}
