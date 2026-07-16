using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PtkResilienceTestFixture;
using Xunit.Sdk;

namespace PtkMcpServer.Tests;

public sealed class ResilienceFakeGuardianTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Disposable_guardian_preserves_public_pipe_and_delivery_truth_across_host_loss()
    {
        await using var harness = await GuardianHarness.StartAsync();

        var guardianPid = harness.Guardian.Id;
        var publicInput = harness.PublicInput;
        var publicOutput = harness.PublicOutput;
        var initialState = await harness.ReadStateAsync();
        Assert.Equal(guardianPid, initialState.GetProperty("guardian_pid").GetInt32());
        Assert.Equal(1, initialState.GetProperty("initialize_count").GetInt32());
        var initialHost = ReadyHost(initialState);
        await File.WriteAllTextAsync(
            Path.Combine(harness.ControlRoot, "hold-recovery.json"),
            "{\"held\":true}");

        // First prove the availability claim independently of a call barrier:
        // kill an otherwise-idle private host and read guardian-local state
        // while the automatic replacement is deliberately held in backoff.
        await KillProcessAsync(initialHost.Pid);
        await harness.WaitForControlFileAsync(
            Path.Combine(harness.ControlRoot, "recovery-held.json"));
        var recoveryState = await harness.WaitForStateAsync(state =>
            state.GetProperty("host").GetProperty("state").GetString() == "recovering");
        var recoveringHost = recoveryState.GetProperty("host");
        Assert.False(recoveringHost.GetProperty("ready_for_effects").GetBoolean());
        Assert.Equal("backoff", recoveringHost.GetProperty("recovery_phase").GetString());
        Assert.True(recoveringHost.GetProperty("recovery_attempt").GetInt64() >= 1);
        Assert.Equal(guardianPid, recoveryState.GetProperty("guardian_pid").GetInt32());

        // Calls arriving during recovery terminate immediately. They are not
        // retained for a later host generation.
        var refusedToken = Token();
        var refused = await harness.CallBackendAsync("public_terminal_sent", refusedToken);
        var refusedResponse = await refused.Response.WaitAsync(StepTimeout);
        var refusedError = ToolPayload(refusedResponse, expectedError: true);
        Assert.Equal("host_recovering", refusedError.GetProperty("detail_code").GetString());
        AssertRetryableHostGate(refusedError);
        Assert.Equal(
            recoveringHost.GetProperty("recovery_phase").GetString(),
            refusedError.GetProperty("recovery_phase").GetString());
        Assert.Equal(
            recoveringHost.GetProperty("recovery_attempt").GetInt64(),
            refusedError.GetProperty("recovery_attempt").GetInt64());
        Assert.Equal(1, harness.ResponseCount(refused.Id));

        await File.WriteAllTextAsync(
            Path.Combine(harness.ControlRoot, "release-recovery.json"),
            "{\"released\":true}");

        var replacement = await harness.WaitForReadyReplacementAsync(initialHost);
        Assert.Empty(harness.ReceivedFiles(refusedToken));
        AssertPublicIdentityUnchanged(harness, guardianPid, publicInput, publicOutput);

        // A loss before the first private byte is a proved-no-start result.
        var notDispatchedToken = Token();
        var notDispatchedCall = await harness.StartBackendCallAsync(
            "not_dispatched",
            notDispatchedToken);
        await harness.WaitForControlFileAsync(
            ControlFile(harness.ControlRoot, "barrier-not_dispatched", notDispatchedToken));
        await KillProcessAsync(replacement.Pid);
        var notDispatchedResponse = await notDispatchedCall.Response.WaitAsync(StepTimeout);
        var notDispatchedError = ToolPayload(notDispatchedResponse, expectedError: true);
        Assert.Equal(
            "backend_lost_before_dispatch",
            notDispatchedError.GetProperty("detail_code").GetString());
        AssertRetryableHostGate(notDispatchedError);
        Assert.Empty(harness.ReceivedFiles(notDispatchedToken));
        replacement = await harness.WaitForReadyReplacementAsync(replacement);
        Assert.Empty(harness.ReceivedFiles(notDispatchedToken));
        Assert.Equal(1, harness.ResponseCount(notDispatchedCall.Id));
        AssertPublicIdentityUnchanged(harness, guardianPid, publicInput, publicOutput);

        // After any private request byte may have been written, loss is
        // ambiguous and never gains retry metadata.
        var writeStartedToken = Token();
        var writeStartedCall = await harness.StartBackendCallAsync(
            "write_started",
            writeStartedToken);
        await harness.WaitForControlFileAsync(
            ControlFile(harness.ControlRoot, "barrier-write_started", writeStartedToken));
        Assert.Single(harness.ReceivedFiles(writeStartedToken));
        await KillProcessAsync(replacement.Pid);
        var writeStartedResponse = await writeStartedCall.Response.WaitAsync(StepTimeout);
        var outcomeUnknown = ToolPayload(writeStartedResponse, expectedError: true);
        Assert.Equal("outcome_unknown", outcomeUnknown.GetProperty("detail_code").GetString());
        AssertNonRetryable(outcomeUnknown);
        replacement = await harness.WaitForReadyReplacementAsync(replacement);
        Assert.Single(harness.ReceivedFiles(writeStartedToken));
        Assert.Equal(1, harness.ResponseCount(writeStartedCall.Id));
        AssertPublicIdentityUnchanged(harness, guardianPid, publicInput, publicOutput);

        // A complete private terminal decoded before host death remains the
        // exact authoritative terminal. The fixture records its original JSON
        // bytes so the public tool text can be compared without reconstruction.
        var decodedToken = Token();
        var decodedCall = await harness.StartBackendCallAsync("terminal_decoded", decodedToken);
        await harness.WaitForControlFileAsync(
            ControlFile(harness.ControlRoot, "barrier-terminal_decoded", decodedToken));
        var exactDecodedTerminal = await File.ReadAllTextAsync(
            ControlFile(harness.ControlRoot, "terminal", decodedToken));
        await KillProcessAsync(replacement.Pid);
        var decodedResponse = await decodedCall.Response.WaitAsync(StepTimeout);
        Assert.Equal(exactDecodedTerminal, ToolText(decodedResponse, expectedError: false));
        replacement = await harness.WaitForReadyReplacementAsync(replacement);
        Assert.Single(harness.ReceivedFiles(decodedToken));
        Assert.Equal(1, harness.ResponseCount(decodedCall.Id));
        AssertPublicIdentityUnchanged(harness, guardianPid, publicInput, publicOutput);

        // Once the public terminal is flushed, later host loss must not create
        // a second response for the already-completed public request.
        var publicSentToken = Token();
        var publicSentCall = await harness.StartBackendCallAsync(
            "public_terminal_sent",
            publicSentToken);
        var publicSentResponse = await publicSentCall.Response.WaitAsync(StepTimeout);
        await harness.WaitForControlFileAsync(
            ControlFile(harness.ControlRoot, "barrier-public_terminal_sent", publicSentToken));
        var exactPublicTerminal = await File.ReadAllTextAsync(
            ControlFile(harness.ControlRoot, "terminal", publicSentToken));
        Assert.Equal(exactPublicTerminal, ToolText(publicSentResponse, expectedError: false));
        Assert.Equal(1, harness.ResponseCount(publicSentCall.Id));
        await KillProcessAsync(replacement.Pid);
        replacement = await harness.WaitForReadyReplacementAsync(replacement);
        await Task.Delay(250);
        Assert.Single(harness.ReceivedFiles(publicSentToken));
        Assert.Equal(1, harness.ResponseCount(publicSentCall.Id));

        var finalState = await harness.ReadStateAsync();
        Assert.Equal(1, finalState.GetProperty("initialize_count").GetInt32());
        Assert.True(finalState.GetProperty("host").GetProperty("ready_for_effects").GetBoolean());
        Assert.True(replacement.Generation > initialHost.Generation);
        AssertPublicIdentityUnchanged(harness, guardianPid, publicInput, publicOutput);
        await harness.ShutdownAndAssertCleanAsync();
    }

    [Fact]
    public async Task Public_eof_waits_for_an_active_recovery_loop_before_disposal()
    {
        await using var harness = await GuardianHarness.StartAsync();
        var initialHost = ReadyHost(await harness.ReadStateAsync());
        await File.WriteAllTextAsync(
            Path.Combine(harness.ControlRoot, "delay-recovery-cancellation.json"),
            "{\"enabled\":true}");

        await KillProcessAsync(initialHost.Pid);
        await harness.WaitForStateAsync(state =>
            state.GetProperty("host").GetProperty("recovery_phase").GetString() == "backoff");
        await harness.ShutdownAndAssertCleanAsync();

        Assert.True(File.Exists(Path.Combine(
            harness.ControlRoot,
            "recovery-loop-stopped.json")));
    }

    [Fact]
    public async Task Loss_after_ready_snapshot_but_before_private_write_is_proved_not_started()
    {
        await using var harness = await GuardianHarness.StartAsync();
        var initialHost = ReadyHost(await harness.ReadStateAsync());
        var token = Token();

        var call = await harness.StartBackendCallAsync("pre_write_revalidation", token);
        await harness.WaitForControlFileAsync(
            ControlFile(harness.ControlRoot, "barrier-pre_write_revalidation", token));
        await KillProcessAsync(initialHost.Pid);

        var response = await call.Response.WaitAsync(StepTimeout);
        var failure = ToolPayload(response, expectedError: true);
        Assert.Equal(
            "backend_lost_before_dispatch",
            failure.GetProperty("detail_code").GetString());
        AssertRetryableHostGate(failure);
        Assert.Empty(harness.ReceivedFiles(token));
        Assert.Equal(1, harness.ResponseCount(call.Id));

        var replacement = await harness.WaitForReadyReplacementAsync(initialHost);
        Assert.True(replacement.Generation > initialHost.Generation);
        Assert.Empty(harness.ReceivedFiles(token));
        await harness.ShutdownAndAssertCleanAsync();
    }

    [Fact]
    public async Task State_snapshot_that_wins_the_exit_observer_race_is_atomically_recovering()
    {
        await using var harness = await GuardianHarness.StartAsync();
        var initialHost = ReadyHost(await harness.ReadStateAsync());
        await File.WriteAllTextAsync(
            Path.Combine(harness.ControlRoot, "delay-host-observer.json"),
            "{\"enabled\":true}");

        await KillProcessAsync(initialHost.Pid);
        await harness.WaitForControlFileAsync(
            Path.Combine(harness.ControlRoot, "host-observer-delayed.json"));

        var state = await harness.ReadStateAsync();
        var host = state.GetProperty("host");
        Assert.Equal("recovering", host.GetProperty("state").GetString());
        Assert.Equal("backoff", host.GetProperty("recovery_phase").GetString());
        Assert.Equal(1, host.GetProperty("recovery_attempt").GetInt64());
        Assert.InRange(host.GetProperty("retry_after_ms").GetInt32(), 250, 60_000);
        Assert.False(host.GetProperty("ready_for_effects").GetBoolean());

        await File.WriteAllTextAsync(
            Path.Combine(harness.ControlRoot, "release-host-observer.json"),
            "{\"released\":true}");
        var replacement = await harness.WaitForReadyReplacementAsync(initialHost);
        Assert.True(replacement.Generation > initialHost.Generation);
        await harness.ShutdownAndAssertCleanAsync();
    }

    [Fact]
    public async Task One_failed_replacement_advances_attempt_by_exactly_one()
    {
        await using var harness = await GuardianHarness.StartAsync();
        var initialHost = ReadyHost(await harness.ReadStateAsync());
        await File.WriteAllTextAsync(
            Path.Combine(harness.ControlRoot, "fail-next-host-start.json"),
            "{\"enabled\":true}");
        harness.ExpectStderr(
            $"resilience-fixture:recovery:FakePrivateProtocolException{Environment.NewLine}");

        await KillProcessAsync(initialHost.Pid);
        var firstBackoff = await harness.WaitForStateAsync(state =>
        {
            var host = state.GetProperty("host");
            return host.GetProperty("recovery_phase").GetString() == "backoff" &&
                host.GetProperty("recovery_attempt").GetInt64() == 1;
        });
        Assert.Equal(1, firstBackoff.GetProperty("host").GetProperty("recovery_attempt").GetInt64());

        var secondBackoff = await harness.WaitForStateAsync(state =>
        {
            var host = state.GetProperty("host");
            return host.GetProperty("recovery_phase").GetString() == "backoff" &&
                host.GetProperty("recovery_attempt").GetInt64() >= 2;
        });
        Assert.Equal(2, secondBackoff.GetProperty("host").GetProperty("recovery_attempt").GetInt64());

        var replacement = await harness.WaitForReadyReplacementAsync(initialHost);
        Assert.True(replacement.Generation >= 3);
        await harness.ShutdownAndAssertCleanAsync();
    }

    [Fact]
    public async Task Structurally_invalid_public_json_is_recorded_and_the_next_response_is_drained()
    {
        await using var harness = await GuardianHarness.StartAsync();
        harness.ExpectPublicProtocolFailure("[]");

        var probe = await harness.ProbeStdoutDrainAsync();
        Assert.Equal("completed", ToolPayload(probe, expectedError: false)
            .GetProperty("probe").GetString());
        var ping = await harness.PingAsync();
        Assert.Equal(JsonValueKind.Object, ping.GetProperty("result").ValueKind);
        await harness.ShutdownAndAssertCleanAsync();
    }

    [Fact]
    public async Task Already_closed_public_stdout_is_safe_during_harness_construction()
    {
        await GuardianHarness.AssertImmediateEofConstructionAsync();
    }

    [Theory]
    [InlineData("malformed_response")]
    [InlineData("wrong_generation_response")]
    public async Task Invalid_private_response_fails_the_generation_without_replay(string barrier)
    {
        await using var harness = await GuardianHarness.StartAsync();
        var initialHost = ReadyHost(await harness.ReadStateAsync());
        var token = Token();

        var call = await harness.StartBackendCallAsync(barrier, token);
        var response = await call.Response.WaitAsync(StepTimeout);
        var failure = ToolPayload(response, expectedError: true);
        Assert.Equal("outcome_unknown", failure.GetProperty("detail_code").GetString());
        AssertNonRetryable(failure);
        Assert.Single(harness.ReceivedFiles(token));
        Assert.Equal(1, harness.ResponseCount(call.Id));

        var replacement = await harness.WaitForReadyReplacementAsync(initialHost);
        Assert.True(replacement.Generation > initialHost.Generation);
        Assert.Single(harness.ReceivedFiles(token));
        await harness.ShutdownAndAssertCleanAsync();
    }

    [Fact]
    public async Task Ambiguous_private_writer_failure_kills_the_generation_and_is_not_replayed()
    {
        await using var harness = await GuardianHarness.StartAsync();
        var initialHost = ReadyHost(await harness.ReadStateAsync());
        var token = Token();

        var call = await harness.StartBackendCallAsync("writer_failure", token);
        var response = await call.Response.WaitAsync(StepTimeout);
        var failure = ToolPayload(response, expectedError: true);
        Assert.Equal("outcome_unknown", failure.GetProperty("detail_code").GetString());
        AssertNonRetryable(failure);
        Assert.Equal(1, harness.ResponseCount(call.Id));

        var replacement = await harness.WaitForReadyReplacementAsync(initialHost);
        Assert.True(replacement.Generation > initialHost.Generation);
        Assert.Empty(harness.ReceivedFiles(token));
        await harness.ShutdownAndAssertCleanAsync();
    }

    [Fact]
    public async Task Guardian_private_request_ids_remain_monotonic_across_host_generations()
    {
        await using var harness = await GuardianHarness.StartAsync();
        var initialHost = ReadyHost(await harness.ReadStateAsync());
        var firstToken = Token();
        var first = await harness.CallBackendAsync("normal", firstToken);
        _ = await first.Response.WaitAsync(StepTimeout);
        var firstPrivate = PrivateRequest(harness, firstToken);

        var sameHostToken = Token();
        var sameHost = await harness.CallBackendAsync("normal", sameHostToken);
        _ = await sameHost.Response.WaitAsync(StepTimeout);
        var sameHostPrivate = PrivateRequest(harness, sameHostToken);
        Assert.True(sameHostPrivate.RequestId > firstPrivate.RequestId);
        Assert.Equal(firstPrivate.WorkerBootId, sameHostPrivate.WorkerBootId);
        Assert.Equal(firstPrivate.WorkerGeneration, sameHostPrivate.WorkerGeneration);
        Assert.Equal(initialHost.Generation, firstPrivate.WorkerGeneration);

        await KillProcessAsync(initialHost.Pid);
        var replacement = await harness.WaitForReadyReplacementAsync(initialHost);
        var secondToken = Token();
        var second = await harness.CallBackendAsync("normal", secondToken);
        _ = await second.Response.WaitAsync(StepTimeout);
        var secondPrivate = PrivateRequest(harness, secondToken);

        Assert.True(secondPrivate.RequestId > sameHostPrivate.RequestId);
        Assert.NotEqual(firstPrivate.WorkerBootId, secondPrivate.WorkerBootId);
        Assert.True(secondPrivate.WorkerGeneration > firstPrivate.WorkerGeneration);
        Assert.Equal(replacement.Generation, secondPrivate.WorkerGeneration);
        Assert.True(replacement.Generation > initialHost.Generation);
        await harness.ShutdownAndAssertCleanAsync();
    }

    [Fact]
    public async Task Duplicate_private_response_kills_its_generation_and_cannot_poison_the_next_call()
    {
        await using var harness = await GuardianHarness.StartAsync();
        var initialHost = ReadyHost(await harness.ReadStateAsync());
        var duplicateToken = Token();

        var duplicate = await harness.StartBackendCallAsync("duplicate_response", duplicateToken);
        var duplicateResponse = await duplicate.Response.WaitAsync(StepTimeout);
        Assert.Equal("{}", ToolText(duplicateResponse, expectedError: false));
        Assert.Equal(1, harness.ResponseCount(duplicate.Id));
        Assert.Single(harness.ReceivedFiles(duplicateToken));

        var replacement = await harness.WaitForReadyReplacementAsync(initialHost);
        var normalToken = Token();
        var normal = await harness.StartBackendCallAsync("normal", normalToken);
        var normalResponse = await normal.Response.WaitAsync(StepTimeout);
        var exactTerminal = await File.ReadAllTextAsync(
            ControlFile(harness.ControlRoot, "terminal", normalToken));
        Assert.Equal(exactTerminal, ToolText(normalResponse, expectedError: false));
        Assert.Equal(1, harness.ResponseCount(normal.Id));
        Assert.Single(harness.ReceivedFiles(normalToken));
        Assert.True(replacement.Generation > initialHost.Generation);
        await harness.ShutdownAndAssertCleanAsync();
    }

    private static HostIdentity ReadyHost(JsonElement state)
    {
        var host = state.GetProperty("host");
        Assert.Equal("ready", host.GetProperty("state").GetString());
        Assert.True(host.GetProperty("ready_for_effects").GetBoolean());
        return new HostIdentity(
            host.GetProperty("pid").GetInt32(),
            host.GetProperty("generation").GetInt64());
    }

    private static void AssertRetryableHostGate(JsonElement error)
    {
        Assert.Equal(
            new[]
            {
                "detail_code", "retryable", "retry_after_ms", "recovery_phase",
                "recovery_attempt", "retry_gate",
            },
            error.EnumerateObject().Select(property => property.Name));
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.InRange(error.GetProperty("retry_after_ms").GetInt32(), 250, 60_000);
        Assert.NotEqual(JsonValueKind.Null, error.GetProperty("recovery_phase").ValueKind);
        Assert.True(error.GetProperty("recovery_attempt").GetInt64() >= 1);
        Assert.Equal("host_ready", error.GetProperty("retry_gate").GetProperty("kind").GetString());
    }

    private static void AssertNonRetryable(JsonElement error)
    {
        Assert.Equal(
            new[]
            {
                "detail_code", "retryable", "retry_after_ms", "recovery_phase",
                "recovery_attempt", "retry_gate",
            },
            error.EnumerateObject().Select(property => property.Name));
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("retry_after_ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, error.GetProperty("recovery_phase").ValueKind);
        Assert.Equal(JsonValueKind.Null, error.GetProperty("recovery_attempt").ValueKind);
        Assert.Equal(JsonValueKind.Null, error.GetProperty("retry_gate").ValueKind);
    }

    private static void AssertPublicIdentityUnchanged(
        GuardianHarness harness,
        int guardianPid,
        Stream publicInput,
        Stream publicOutput)
    {
        Assert.False(harness.Guardian.HasExited);
        Assert.Equal(guardianPid, harness.Guardian.Id);
        Assert.Same(publicInput, harness.PublicInput);
        Assert.Same(publicOutput, harness.PublicOutput);
        Assert.True(harness.Guardian.StandardInput.BaseStream.CanWrite);
        Assert.True(harness.Guardian.StandardOutput.BaseStream.CanRead);
    }

    private static JsonElement ToolPayload(JsonElement response, bool expectedError)
    {
        var text = ToolText(response, expectedError);
        return JsonSerializer.Deserialize<JsonElement>(text).Clone();
    }

    private static string ToolText(JsonElement response, bool expectedError)
    {
        var result = response.GetProperty("result");
        Assert.Equal(expectedError, result.GetProperty("isError").GetBoolean());
        return result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
    }

    private static string Token() => Guid.NewGuid().ToString("N");

    private static PrivateOperationRequest PrivateRequest(GuardianHarness harness, string token)
    {
        var received = Assert.Single(harness.ReceivedFiles(token));
        using var document = JsonDocument.Parse(File.ReadAllBytes(received));
        var root = document.RootElement;
        Assert.Equal("operation", root.GetProperty("method").GetString());
        Assert.Equal("default", root.GetProperty("session_alias").GetString());
        var payload = root.GetProperty("payload");
        Assert.Equal("job_list", payload.GetProperty("operation").GetString());
        Assert.False(payload.TryGetProperty("barrier", out _));
        var callId = payload.GetProperty("call_id").GetString()!;
        Assert.Equal('7', callId[14]);
        Assert.Equal(
            callId,
            payload.GetProperty("dispatch_capability").GetProperty("call_id").GetString());
        Assert.Equal(
            43,
            payload.GetProperty("dispatch_capability").GetProperty("token").GetString()!.Length);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("output_capability").ValueKind);
        Assert.Empty(payload.GetProperty("arguments").EnumerateObject());
        return new PrivateOperationRequest(
            root.GetProperty("request_id").GetInt64(),
            root.GetProperty("worker_boot_id").GetGuid(),
            root.GetProperty("worker_generation").GetInt64());
    }

    private static string ControlFile(string root, string kind, string token) =>
        Path.Combine(root, $"{kind}-{token}.json");

    private static async Task KillProcessAsync(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(StepTimeout);
    }

    private sealed record HostIdentity(int Pid, long Generation);

    private sealed record PrivateOperationRequest(
        long RequestId,
        Guid WorkerBootId,
        long WorkerGeneration);

    private sealed record PendingRequest(int Id, Task<JsonElement> Response);

    private sealed class GuardianHarness : IAsyncDisposable
    {
        private readonly SemaphoreSlim _inputGate = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _waiters = new();
        private readonly ConcurrentDictionary<int, byte> _issuedRequestIds = new();
        private readonly ConcurrentDictionary<int, int> _responseCounts = new();
        private readonly ConcurrentDictionary<long, byte> _observedHostGenerations = new();
        private readonly ConcurrentQueue<string> _protocolFailures = new();
        private readonly ConcurrentQueue<string> _expectedProtocolFailures = new();
        private readonly CancellationTokenSource _readerCancellation = new();
        private readonly Task _reader;
        private readonly Task<string> _stderr;
        private int _nextId;
        private bool _shutdownComplete;
        private string _expectedStderr = string.Empty;

        private GuardianHarness(Process guardian, string controlRoot)
        {
            Guardian = guardian;
            ControlRoot = controlRoot;
            PublicInput = guardian.StandardInput.BaseStream;
            PublicOutput = guardian.StandardOutput.BaseStream;
            _stderr = guardian.StandardError.ReadToEndAsync();
            _reader = ReadPublicOutputAsync();
        }

        public Process Guardian { get; }
        public string ControlRoot { get; }
        public Stream PublicInput { get; }
        public Stream PublicOutput { get; }

        public static async Task<GuardianHarness> StartAsync()
        {
            var fixtureDll = typeof(FixtureAssemblyMarker).Assembly.Location;
            Assert.True(File.Exists(fixtureDll), $"resilience fixture not found at {fixtureDll}");
            var controlRoot = Path.Combine(
                Path.GetTempPath(),
                "ptk-resilience-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(controlRoot);

            var start = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(fixtureDll);
            start.Environment["PTK_RESILIENCE_FIXTURE_CONTROL_ROOT"] = controlRoot;

            var process = Process.Start(start) ??
                throw new InvalidOperationException("Could not start resilience fixture guardian.");
            var harness = new GuardianHarness(process, controlRoot);
            try
            {
                var initialize = await harness.RequestAsync("initialize", new Dictionary<string, object?>
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new Dictionary<string, object?>(),
                    ["clientInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = "resilience-fixture-test",
                        ["version"] = "0.0.0",
                    },
                });
                Assert.Equal("2.0", initialize.GetProperty("jsonrpc").GetString());
                Assert.Equal(
                    "ptk-resilience-test-fixture",
                    initialize.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
                await harness.SendNotificationAsync("notifications/initialized", new Dictionary<string, object?>());
                return harness;
            }
            catch
            {
                await harness.DisposeAsync();
                throw;
            }
        }

        public static async Task AssertImmediateEofConstructionAsync()
        {
            var fixtureDll = typeof(FixtureAssemblyMarker).Assembly.Location;
            var controlRoot = Path.Combine(
                Path.GetTempPath(),
                "ptk-resilience-immediate-eof-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(controlRoot);
            var start = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(fixtureDll);
            start.Environment.Remove("PTK_RESILIENCE_FIXTURE_CONTROL_ROOT");

            var process = Process.Start(start) ??
                throw new InvalidOperationException("Could not start the immediate-EOF fixture.");
            await process.WaitForExitAsync().WaitAsync(StepTimeout);
            Assert.Equal(64, process.ExitCode);

            await using var harness = new GuardianHarness(process, controlRoot);
            await harness._reader.WaitAsync(StepTimeout);
            Assert.Equal(string.Empty, await harness._stderr.WaitAsync(StepTimeout));
            Assert.Empty(harness._protocolFailures);
            harness._shutdownComplete = true;
        }

        public async Task<JsonElement> ReadStateAsync()
        {
            var response = await CallToolAsync("ptk_state", new Dictionary<string, object?>());
            var state = ToolPayload(response, expectedError: false);
            var generation = state.GetProperty("host").GetProperty("generation").GetInt64();
            Assert.True(generation > 0);
            Assert.True(_observedHostGenerations.TryAdd(generation, 0) ||
                _observedHostGenerations.ContainsKey(generation));
            return state;
        }

        public async Task<JsonElement> WaitForStateAsync(Func<JsonElement, bool> predicate)
        {
            var deadline = Stopwatch.StartNew();
            JsonElement last = default;
            while (deadline.Elapsed < StepTimeout)
            {
                last = await ReadStateAsync();
                if (predicate(last)) return last;
                await Task.Delay(20);
            }
            throw new XunitException($"Timed out waiting for guardian state. Last state: {last.GetRawText()}");
        }

        public async Task<HostIdentity> WaitForReadyReplacementAsync(HostIdentity previous)
        {
            var state = await WaitForStateAsync(candidate =>
            {
                var host = candidate.GetProperty("host");
                return host.GetProperty("state").GetString() == "ready" &&
                    host.GetProperty("ready_for_effects").GetBoolean() &&
                    host.GetProperty("generation").GetInt64() > previous.Generation &&
                    host.GetProperty("pid").GetInt32() != previous.Pid;
            });
            return ReadyHost(state);
        }

        public Task<PendingRequest> CallBackendAsync(string barrier, string token) =>
            StartBackendCallAsync(barrier, token);

        public async Task<PendingRequest> StartBackendCallAsync(string barrier, string token)
        {
            return await StartToolCallAsync("fixture_backend_call", new Dictionary<string, object?>
            {
                ["barrier"] = barrier,
                ["token"] = token,
            });
        }

        public async Task WaitForControlFileAsync(string path)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < StepTimeout)
            {
                if (File.Exists(path)) return;
                if (Guardian.HasExited)
                    throw new XunitException($"Guardian exited before fixture barrier {Path.GetFileName(path)}.");
                await Task.Delay(10);
            }
            throw new XunitException($"Timed out waiting for fixture barrier {Path.GetFileName(path)}.");
        }

        public string[] ReceivedFiles(string token) =>
            Directory.GetFiles(ControlRoot, $"received-{token}-g*.json");

        public int ResponseCount(int id) => _responseCounts.GetValueOrDefault(id);

        public void ExpectPublicProtocolFailure(string exactLine)
        {
            _expectedProtocolFailures.Enqueue(exactLine);
        }

        public void ExpectStderr(string exactText)
        {
            Assert.Equal(string.Empty, _expectedStderr);
            _expectedStderr = exactText;
        }

        public Task<JsonElement> ProbeStdoutDrainAsync() =>
            CallToolAsync("fixture_stdout_drain_probe", new Dictionary<string, object?>());

        public async Task<JsonElement> PingAsync()
        {
            return await RequestAsync("ping", new Dictionary<string, object?>());
        }

        public void AssertPublicStdoutIsJsonRpcOnly()
        {
            Assert.Equal(_expectedProtocolFailures.ToArray(), _protocolFailures.ToArray());
            Assert.Empty(_waiters);
            Assert.Equal(
                _issuedRequestIds.Keys.Order(),
                _responseCounts.Keys.Order());
            Assert.All(_responseCounts, pair => Assert.Equal(1, pair.Value));
        }

        public async Task ShutdownAndAssertCleanAsync()
        {
            if (_shutdownComplete) return;

            Guardian.StandardInput.Close();
            await Guardian.WaitForExitAsync().WaitAsync(StepTimeout);
            Assert.Equal(0, Guardian.ExitCode);
            await _reader.WaitAsync(StepTimeout);
            Assert.Equal(_expectedStderr, await _stderr.WaitAsync(StepTimeout));
            AssertPublicStdoutIsJsonRpcOnly();

            var hostEvidence = Directory.GetFiles(ControlRoot, "host-started-g*.json");
            Assert.NotEmpty(hostEvidence);
            var evidencedGenerations = new HashSet<long>();
            foreach (var path in hostEvidence)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                Assert.True(
                    long.TryParse(name.AsSpan("host-started-g".Length), out var generation) && generation > 0,
                    $"Invalid fake-host evidence filename: {path}");
                Assert.True(evidencedGenerations.Add(generation), $"Duplicate host generation evidence: {path}");
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                var pid = document.RootElement.GetProperty("pid").GetInt32();
                var startUtcTicks = document.RootElement.GetProperty("start_utc_ticks").GetInt64();
                Assert.True(
                    ProcessIdentityIsGone(pid, new DateTime(startUtcTicks, DateTimeKind.Utc)),
                    $"Fake host identity remained alive after public EOF: pid={pid} file={path}");
            }
            Assert.Equal(
                Enumerable.Range(1, checked((int)evidencedGenerations.Max())).Select(value => (long)value),
                evidencedGenerations.Order());
            Assert.True(
                _observedHostGenerations.Keys.All(evidencedGenerations.Contains),
                "Every observed host generation must have exact startup identity evidence.");

            var shutdownEvidencePath = Path.Combine(ControlRoot, "guardian-shutdown.json");
            Assert.True(File.Exists(shutdownEvidencePath), "Guardian shutdown evidence was not written.");
            using (var shutdownEvidence = JsonDocument.Parse(
                       await File.ReadAllTextAsync(shutdownEvidencePath)))
            {
                Assert.Equal(
                    ["admitted_host_count", "admitted_host_generations"],
                    shutdownEvidence.RootElement.EnumerateObject().Select(property => property.Name));
                Assert.Equal(
                    evidencedGenerations.Count,
                    shutdownEvidence.RootElement.GetProperty("admitted_host_count").GetInt32());
                Assert.Equal(
                    evidencedGenerations.Order(),
                    shutdownEvidence.RootElement.GetProperty("admitted_host_generations")
                        .EnumerateArray().Select(value => value.GetInt64()));
            }

            _shutdownComplete = true;
        }

        private async Task<JsonElement> CallToolAsync(string name, object arguments)
        {
            var pending = await StartToolCallAsync(name, arguments);
            return await pending.Response.WaitAsync(StepTimeout);
        }

        private async Task<PendingRequest> StartToolCallAsync(string name, object arguments)
        {
            return await StartRequestAsync("tools/call", new Dictionary<string, object?>
            {
                ["name"] = name,
                ["arguments"] = arguments,
            });
        }

        private async Task<JsonElement> RequestAsync(string method, object parameters)
        {
            var pending = await StartRequestAsync(method, parameters);
            return await pending.Response.WaitAsync(StepTimeout);
        }

        private async Task<PendingRequest> StartRequestAsync(string method, object parameters)
        {
            var id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<JsonElement>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_waiters.TryAdd(id, completion));
            Assert.True(_issuedRequestIds.TryAdd(id, 0));
            await SendLineAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            }));
            return new PendingRequest(id, completion.Task);
        }

        private async Task SendNotificationAsync(string method, object parameters)
        {
            await SendLineAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters,
            }));
        }

        private async Task SendLineAsync(string line)
        {
            await _inputGate.WaitAsync();
            try
            {
                await Guardian.StandardInput.WriteLineAsync(line);
                await Guardian.StandardInput.FlushAsync();
            }
            finally
            {
                _inputGate.Release();
            }
        }

        private async Task ReadPublicOutputAsync()
        {
            try
            {
                while (true)
                {
                    var line = await Guardian.StandardOutput.ReadLineAsync(_readerCancellation.Token);
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        _protocolFailures.Enqueue("blank stdout line");
                        continue;
                    }

                    JsonElement message;
                    try
                    {
                        message = JsonSerializer.Deserialize<JsonElement>(line).Clone();
                    }
                    catch (JsonException)
                    {
                        _protocolFailures.Enqueue(line);
                        continue;
                    }

                    if (message.ValueKind != JsonValueKind.Object)
                    {
                        _protocolFailures.Enqueue(line);
                        continue;
                    }

                    var properties = message.EnumerateObject().Select(property => property.Name).ToArray();
                    var hasResult = message.TryGetProperty("result", out _);
                    var hasError = message.TryGetProperty("error", out _);
                    if (!message.TryGetProperty("jsonrpc", out var version) ||
                        version.ValueKind != JsonValueKind.String ||
                        version.GetString() != "2.0" ||
                        !message.TryGetProperty("id", out var idProperty) ||
                        idProperty.ValueKind != JsonValueKind.Number ||
                        !idProperty.TryGetInt32(out var id) ||
                        hasResult == hasError ||
                        !properties.SequenceEqual(hasResult
                            ? new[] { "jsonrpc", "id", "result" }
                            : new[] { "jsonrpc", "id", "error" }))
                    {
                        _protocolFailures.Enqueue(line);
                        continue;
                    }

                    if (!_issuedRequestIds.ContainsKey(id))
                    {
                        _protocolFailures.Enqueue($"unknown response id: {id}");
                        continue;
                    }
                    _responseCounts.AddOrUpdate(id, 1, (_, count) => checked(count + 1));
                    if (_waiters.TryRemove(id, out var waiter))
                        waiter.TrySetResult(message);
                    else
                        _protocolFailures.Enqueue($"duplicate response id: {id}");
                }
            }
            catch (OperationCanceledException) when (_readerCancellation.IsCancellationRequested)
            {
                // Test teardown.
            }
            catch (Exception exception)
            {
                _protocolFailures.Enqueue($"reader:{exception.GetType().Name}");
            }
            finally
            {
                var stderr = _stderr.IsCompletedSuccessfully ? _stderr.Result : string.Empty;
                foreach (var waiter in _waiters.Values)
                {
                    waiter.TrySetException(new EndOfStreamException(
                        $"Guardian public stdout closed. stderr={stderr}"));
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await ShutdownAndAssertCleanAsync();
            }
            catch
            {
                try { Guardian.Kill(entireProcessTree: true); } catch { /* already exited */ }
                try { await Guardian.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }

            _readerCancellation.Cancel();
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            try { await _stderr.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            _inputGate.Dispose();
            _readerCancellation.Dispose();
            Guardian.Dispose();
            try { Directory.Delete(ControlRoot, recursive: true); } catch { /* preserve failure evidence */ }
        }

        private static bool ProcessIdentityIsGone(int processId, DateTime expectedStartTimeUtc)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.HasExited || process.StartTime.ToUniversalTime() != expectedStartTimeUtc;
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }
}
