using System.Diagnostics;
using System.IO.Pipelines;
using System.Security.Cryptography;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class SessionWorkerClientTests
{
    private static readonly TimeSpan CheckpointTimeout = TimeSpan.FromSeconds(10);
    private static readonly WorkerProtocolLimits Limits =
        WorkerOperationProtocol.CreateLimits(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(5));

    [Fact]
    public async Task Late_terminal_from_a_previous_request_faults_instead_of_completing_the_next_request()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 3,
            Limits);
        await InitializeAsync(client, process);

        var firstCall = client.InvokeAsync(
            "'first'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            artifactCapture: null,
            CancellationToken.None);
        var firstRequest = WorkerOperationProtocol.ParseInvoke(
            await process.ReadRequestAsync(),
            client.SessionId,
            client.Incarnation,
            Limits);
        var firstTerminal = WorkerOperationProtocol.CreateResultEnvelope(
            client.SessionId,
            client.Incarnation,
            new WorkerResult(
                firstRequest.RequestId,
                WorkerResultStatus.Completed,
                "first",
                DetailCode: null));
        await process.WriteEventAsync(firstTerminal);
        await process.WriteEventAsync(firstTerminal);

        var first = await firstCall.WaitAsync(CheckpointTimeout);
        Assert.Equal("first", first.Result.Text);

        var secondCall = client.InvokeAsync(
            "'second'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            artifactCapture: null,
            CancellationToken.None);
        _ = await process.ReadRequestAsync();
        var failure = await Assert.ThrowsAsync<WorkerInvocationException>(
            () => secondCall);

        Assert.Equal(
            WorkerInvocationDisposition.OutcomeUnknown,
            failure.Disposition);
        Assert.Equal("request_id_mismatch", failure.CauseDetailCode);
        Assert.False(client.IsTransportUsable);
        _ = await Assert.ThrowsAsync<WorkerProtocolException>(
            () => client.Fatal);
    }

    [Fact]
    public async Task Artifact_frame_after_a_valid_seal_faults_the_transport()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 7,
            Limits);
        await InitializeAsync(client, process);
        var artifact = new WorkerArtifactRequest(Guid.NewGuid(), 1024);
        using var artifactCapture = new ValidatingArtifactCapture(artifact);

        var call = client.InvokeAsync(
            "'artifact'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            artifactCapture,
            CancellationToken.None);
        var request = WorkerOperationProtocol.ParseInvoke(
            await process.ReadRequestAsync(),
            client.SessionId,
            client.Incarnation,
            Limits);
        var bytes = "sealed"u8.ToArray();
        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateArtifactChunkEnvelope(
                client.SessionId,
                client.Incarnation,
                new WorkerArtifactChunk(
                    request.RequestId,
                    artifact.ArtifactId,
                    Offset: 0,
                    bytes),
                Limits));
        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateArtifactSealEnvelope(
                client.SessionId,
                client.Incarnation,
                new WorkerArtifactSeal(
                    request.RequestId,
                    artifact.ArtifactId,
                    bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes))
                        .ToLowerInvariant())));
        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateArtifactChunkEnvelope(
                client.SessionId,
                client.Incarnation,
                new WorkerArtifactChunk(
                    request.RequestId,
                    artifact.ArtifactId,
                    bytes.Length,
                    "late"u8.ToArray()),
                Limits));

        var failure = await Assert.ThrowsAsync<WorkerInvocationException>(
            () => call);

        Assert.Equal(
            WorkerInvocationDisposition.OutcomeUnknown,
            failure.Disposition);
        Assert.Equal("artifact_sequence_invalid", failure.CauseDetailCode);
        Assert.False(client.IsTransportUsable);
        _ = await Assert.ThrowsAsync<WorkerProtocolException>(
            () => client.Fatal);
    }

    [Fact]
    public async Task Stalled_artifact_sink_does_not_delay_a_complete_terminal()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 8,
            Limits);
        await InitializeAsync(client, process);
        using var store = CreateOutputStore();
        Assert.True(
            store.TryReserve("alpha", out var reservation, out var failure),
            failure);
        var releaseSink = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var capture = new SupervisorWorkerArtifactCapture(
            store,
            reservation!,
            maximumBytes: 1024,
            maximumChunkBytes: 1024,
            storageWait: TimeSpan.FromMilliseconds(75),
            sinkGateForTests: _ => releaseSink.Task);

        var call = client.InvokeAsync(
            "'artifact'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            capture,
            CancellationToken.None);
        var request = WorkerOperationProtocol.ParseInvoke(
            await process.ReadRequestAsync(),
            client.SessionId,
            client.Incarnation,
            Limits);
        var bytes = WorkerOutputArtifactCodec.Encode(
            new OutputArtifactContent(
                "ordinary result survives",
                StandardError: [],
                Errors: [],
                Warnings: [],
                ExitCode: null,
                OutputProvenance.DirectText),
            maximumBytes: 1024);
        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateArtifactChunkEnvelope(
                client.SessionId,
                client.Incarnation,
                new WorkerArtifactChunk(
                    request.RequestId,
                    capture.Request.ArtifactId,
                    Offset: 0,
                    bytes),
                Limits));
        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateArtifactSealEnvelope(
                client.SessionId,
                client.Incarnation,
                new WorkerArtifactSeal(
                    request.RequestId,
                    capture.Request.ArtifactId,
                    bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes))
                        .ToLowerInvariant())));

        var stopwatch = Stopwatch.StartNew();
        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateResultEnvelope(
                client.SessionId,
                client.Incarnation,
                new WorkerResult(
                    request.RequestId,
                    WorkerResultStatus.Completed,
                    "ordinary terminal",
                    DetailCode: null)));
        var invocation = await call.WaitAsync(TimeSpan.FromSeconds(1));
        stopwatch.Stop();

        Assert.Equal("ordinary terminal", invocation.Result.Text);
        Assert.Null(invocation.OutputRecovery);
        var completion = invocation.OutputRecoveryCompletion;
        Assert.NotNull(completion);
        var recovery = await completion
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Null(recovery.Handle);
        Assert.Equal(
            "artifact_sink_incomplete",
            recovery.DetailCode);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            stopwatch.Elapsed.ToString());

        releaseSink.TrySetResult();
        await capture.SinkCompletionForTests.WaitAsync(CheckpointTimeout);
        Assert.True(
            SpinWait.SpinUntil(
                () => store.TryReserve(
                    "alpha",
                    out var replacement,
                    out _) && Dispose(replacement),
                CheckpointTimeout));
    }

    [Fact]
    public async Task Invalid_digest_valid_artifact_content_faults_after_terminal()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 9,
            Limits);
        await InitializeAsync(client, process);
        using var store = CreateOutputStore();
        Assert.True(
            store.TryReserve("alpha", out var reservation, out var failure),
            failure);
        using var capture = new SupervisorWorkerArtifactCapture(
            store,
            reservation!,
            maximumBytes: 1024,
            maximumChunkBytes: 1024,
            storageWait: TimeSpan.FromSeconds(2));

        var call = client.InvokeAsync(
            "'invalid artifact content'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            capture,
            CancellationToken.None);
        var request = WorkerOperationProtocol.ParseInvoke(
            await process.ReadRequestAsync(),
            client.SessionId,
            client.Incarnation,
            Limits);
        var bytes = "{}"u8.ToArray();
        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateArtifactChunkEnvelope(
                client.SessionId,
                client.Incarnation,
                new WorkerArtifactChunk(
                    request.RequestId,
                    capture.Request.ArtifactId,
                    Offset: 0,
                    bytes),
                Limits));
        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateArtifactSealEnvelope(
                client.SessionId,
                client.Incarnation,
                new WorkerArtifactSeal(
                    request.RequestId,
                    capture.Request.ArtifactId,
                    bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes))
                        .ToLowerInvariant())));
        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateResultEnvelope(
                client.SessionId,
                client.Incarnation,
                new WorkerResult(
                    request.RequestId,
                    WorkerResultStatus.Completed,
                    "ordinary terminal",
                    DetailCode: null)));

        var invocation = await call.WaitAsync(CheckpointTimeout);
        var completion = invocation.OutputRecoveryCompletion;
        Assert.NotNull(completion);
        var protocolFailure =
            await Assert.ThrowsAsync<WorkerInvocationException>(
                () => completion);

        Assert.Equal(
            WorkerInvocationDisposition.OutcomeUnknown,
            protocolFailure.Disposition);
        Assert.Equal(
            "artifact_content_invalid",
            protocolFailure.CauseDetailCode);
        Assert.False(client.IsTransportUsable);
        _ = await Assert.ThrowsAsync<WorkerProtocolException>(
            () => client.Fatal);
    }

    [Fact]
    public async Task Worker_loss_during_artifact_transfer_releases_capture_without_a_handle()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 9,
            Limits);
        await InitializeAsync(client, process);
        using var store = CreateOutputStore();
        Assert.True(
            store.TryReserve("alpha", out var reservation, out var failure),
            failure);
        using var capture = new SupervisorWorkerArtifactCapture(
            store,
            reservation!,
            maximumBytes: 1024,
            maximumChunkBytes: 1024,
            storageWait: TimeSpan.FromSeconds(2));

        var call = client.InvokeAsync(
            "'partial artifact'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            capture,
            CancellationToken.None);
        var request = WorkerOperationProtocol.ParseInvoke(
            await process.ReadRequestAsync(),
            client.SessionId,
            client.Incarnation,
            Limits);
        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateArtifactChunkEnvelope(
                client.SessionId,
                client.Incarnation,
                new WorkerArtifactChunk(
                    request.RequestId,
                    capture.Request.ArtifactId,
                    Offset: 0,
                    "partial"u8.ToArray()),
                Limits));
        process.ExitUnexpectedly();

        var invocationFailure =
            await Assert.ThrowsAsync<WorkerInvocationException>(() => call);

        Assert.Equal(
            WorkerInvocationDisposition.OutcomeUnknown,
            invocationFailure.Disposition);
        Assert.False(capture.IsSealed);
        capture.Dispose();
        Assert.True(
            SpinWait.SpinUntil(
                () => store.TryReserve(
                    "alpha",
                    out var replacement,
                    out _) && Dispose(replacement),
                CheckpointTimeout));
        Assert.Empty(Directory.GetFiles(store.RootPathForTests));
    }

    [Fact]
    public async Task Worker_loss_before_the_invoke_pipe_write_is_proved_not_started()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 9,
            Limits);
        await InitializeAsync(client, process);
        process.ExitUnexpectedly();
        await WaitForFatalAsync(client);
        var writesBeforeInvoke = process.RequestWriteCalls;

        var failure = await Assert.ThrowsAsync<WorkerInvocationException>(
            () => client.InvokeAsync(
                "'never dispatched'",
                raw: false,
                WorkerInvokeRoute.Pwsh,
                timeoutSeconds: 30,
                artifactCapture: null,
                CancellationToken.None));

        Assert.Equal(
            WorkerInvocationDisposition.NotStarted,
            failure.Disposition);
        Assert.Equal("worker_transport_unavailable", failure.CauseDetailCode);
        Assert.Equal(writesBeforeInvoke, process.RequestWriteCalls);
    }

    [Theory]
    [InlineData((int)WriteFailureMode.SynchronousAtEntry)]
    [InlineData((int)WriteFailureMode.AsynchronousReturn)]
    [InlineData((int)WriteFailureMode.PartialWrite)]
    public async Task Failure_at_or_after_the_first_invoke_pipe_write_is_outcome_unknown(
        int modeValue)
    {
        var process = new ScriptedProcess((WriteFailureMode)modeValue);
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 10,
            Limits);
        await InitializeAsync(client, process);

        var failure = await Assert.ThrowsAsync<WorkerInvocationException>(
            () => client.InvokeAsync(
                "'possibly dispatched'",
                raw: false,
                WorkerInvokeRoute.Pwsh,
                timeoutSeconds: 30,
                artifactCapture: null,
                CancellationToken.None));

        Assert.Equal(
            WorkerInvocationDisposition.OutcomeUnknown,
            failure.Disposition);
        Assert.Equal("worker_transport_failure", failure.CauseDetailCode);
        Assert.Equal(2, process.RequestWriteCalls);
        Assert.False(client.IsTransportUsable);
    }

    [Fact]
    public async Task Worker_loss_after_a_complete_invoke_write_is_outcome_unknown_and_never_replayed()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 12,
            Limits);
        await InitializeAsync(client, process);

        var call = client.InvokeAsync(
            "'execute once'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            artifactCapture: null,
            CancellationToken.None);
        _ = WorkerOperationProtocol.ParseInvoke(
            await process.ReadRequestAsync(),
            client.SessionId,
            client.Incarnation,
            Limits);
        process.ExitUnexpectedly();

        var failure = await Assert.ThrowsAsync<WorkerInvocationException>(
            () => call);

        Assert.Equal(
            WorkerInvocationDisposition.OutcomeUnknown,
            failure.Disposition);
        Assert.Equal(2, process.RequestWriteCalls);
    }

    [Fact]
    public async Task Worker_loss_during_result_frame_is_outcome_unknown_and_never_replayed()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 14,
            Limits);
        await InitializeAsync(client, process);

        var call = client.InvokeAsync(
            "'partial result'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            artifactCapture: null,
            CancellationToken.None);
        var request = WorkerOperationProtocol.ParseInvoke(
            await process.ReadRequestAsync(),
            client.SessionId,
            client.Incarnation,
            Limits);
        var encoded = WorkerProtocol.Encode(
            WorkerOperationProtocol.CreateResultEnvelope(
                client.SessionId,
                client.Incarnation,
                new WorkerResult(
                    request.RequestId,
                    WorkerResultStatus.Completed,
                    "never complete",
                    DetailCode: null)));
        await process.WritePartialEventAndExitAsync(
            encoded.AsMemory(0, encoded.Length / 2));

        var failure = await Assert.ThrowsAsync<WorkerInvocationException>(
            () => call);

        Assert.Equal(
            WorkerInvocationDisposition.OutcomeUnknown,
            failure.Disposition);
        Assert.Equal("truncated_frame", failure.CauseDetailCode);
        Assert.Equal(2, process.RequestWriteCalls);
    }

    [Fact]
    public async Task Complete_valid_terminal_wins_even_when_the_worker_exits_immediately_afterward()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 13,
            Limits);
        await InitializeAsync(client, process);

        var call = client.InvokeAsync(
            "'completed once'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            artifactCapture: null,
            CancellationToken.None);
        var request = WorkerOperationProtocol.ParseInvoke(
            await process.ReadRequestAsync(),
            client.SessionId,
            client.Incarnation,
            Limits);
        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateResultEnvelope(
                client.SessionId,
                client.Incarnation,
                new WorkerResult(
                    request.RequestId,
                    WorkerResultStatus.Completed,
                    "completed once",
                    DetailCode: null)));
        process.ExitUnexpectedly();

        var result = await call.WaitAsync(CheckpointTimeout);

        Assert.Equal(WorkerResultStatus.Completed, result.Result.Status);
        Assert.Equal("completed once", result.Result.Text);
        Assert.Equal(2, process.RequestWriteCalls);
    }

    [Fact]
    public async Task Cancellation_after_request_write_poisons_transport_and_sends_one_best_effort_cancel()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 11,
            Limits);
        await InitializeAsync(client, process);
        using var cancellation = new CancellationTokenSource();

        var call = client.InvokeAsync(
            "Start-Sleep -Seconds 300",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 300,
            artifactCapture: null,
            cancellation.Token);
        var request = WorkerOperationProtocol.ParseInvoke(
            await process.ReadRequestAsync(),
            client.SessionId,
            client.Incarnation,
            Limits);
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => call);
        var cancel = WorkerOperationProtocol.ParseCancel(
            await process.ReadRequestAsync(),
            client.SessionId,
            client.Incarnation);

        Assert.Equal(request.RequestId, cancel.RequestId);
        Assert.False(client.IsTransportUsable);
        _ = await Assert.ThrowsAsync<IOException>(
            () => client.Fatal);
    }

    [Fact]
    public async Task Factory_reports_a_real_initialization_deadline_as_postlaunch_timeout()
    {
        var process = new ScriptedProcess();
        var command = new WorkerLaunchCommand(
            Path.Combine(Path.GetTempPath(), "ptk-never-launched"),
            [],
            Path.GetTempPath(),
            []);
        var factory = new ProcessSessionWorkerFactory(
            new StaticLauncher(process),
            command,
            Limits);

        var failure = await Assert.ThrowsAsync<SessionWorkerStartException>(
            () => factory.StartAsync(
                Guid.NewGuid(),
                incarnation: 1,
                DateTimeOffset.UtcNow.AddMilliseconds(50),
                CancellationToken.None));

        Assert.Equal("worker_start_timed_out", failure.DetailCode);
        Assert.True(failure.ProcessLaunched);
        Assert.Equal(
            WorkerContainmentOutcome.ConfirmedEmpty,
            failure.Containment?.Outcome);
        Assert.True(failure.ContainmentEmpty?.IsCompletedSuccessfully);
    }

    /// <summary>
    /// Slice 5 / opr-19: StopAsync completed _fatal before its handshake, and
    /// NextRequestId rejects a completed _fatal - so request allocation threw
    /// on the very next line, the nonfatal catch swallowed it, and no shutdown
    /// frame was ever written. Every close, reset, replace, and dispose
    /// skipped worker-side session teardown and went straight to forced
    /// containment. The graceful exchange must actually happen.
    /// Re-completing _fatal before the handshake makes this FAIL.
    /// </summary>
    [Fact]
    public async Task Stop_performs_the_graceful_shutdown_handshake_before_containment()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 4,
            Limits);
        await InitializeAsync(client, process);

        var stop = client.StopAsync(
            WorkerContainmentReason.Close,
            CancellationToken.None);

        // The worker must actually receive a correlated shutdown frame.
        var request = await process.ReadRequestAsync();
        Assert.Equal(WorkerMessageKind.Shutdown, request.Kind);
        Assert.NotNull(request.RequestId);

        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateEmptyEnvelope(
                WorkerMessageKind.Stopped,
                client.SessionId,
                client.Incarnation,
                request.RequestId!.Value));

        var containment = await stop.WaitAsync(CheckpointTimeout);
        Assert.NotNull(containment);
    }

    /// <summary>
    /// Slice 5 / opr-19 failure path: a worker that never acknowledges must
    /// still reach bounded containment rather than hanging or throwing.
    /// </summary>
    [Fact]
    public async Task Stop_without_an_acknowledgement_still_reaches_containment()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 5,
            Limits);
        await InitializeAsync(client, process);

        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(250));
        var containment = await client
            .StopAsync(WorkerContainmentReason.Close, cancellation.Token)
            .WaitAsync(CheckpointTimeout);

        Assert.NotNull(containment);
    }

    /// <summary>
    /// <summary>
    /// Slice 5 / opr-20, fail-closed half: at or after the first write attempt
    /// the stream and the request outcome are both ambiguous, so the client
    /// must still poison. The repair narrows the poisoning window; it must not
    /// remove it. The writer invokes its first-write callback immediately
    /// before the stream write, so a failure raised by that write is at-write
    /// and stays fail-closed.
    /// </summary>
    [Fact]
    public async Task State_failure_at_the_first_write_still_poisons_the_transport()
    {
        var process = new ScriptedProcess(WriteFailureMode.SynchronousAtEntry);
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 1,
            Limits);
        await InitializeAsync(client, process);

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.StateAsync(listAvailable: false, CancellationToken.None));

        Assert.False(
            client.IsTransportUsable,
            "an ambiguous at-write failure must still poison the transport");
    }
    /// <summary>
    /// GitHub #13: a worker that dies mid-invocation used to report
    /// worker_transport_closed and nothing else, so a worker defect, a
    /// transport fault, and the caller's own command killing the process were
    /// indistinguishable. The worker's own exit diagnostic now names the
    /// cause.
    /// </summary>
    [Fact]
    public async Task A_dying_worker_reports_the_kind_it_named_in_its_diagnostic()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 11,
            Limits);
        await InitializeAsync(client, process);

        var call = client.InvokeAsync(
            "'dies'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            artifactCapture: null,
            CancellationToken.None);
        _ = await process.ReadRequestAsync();
        await process.ExitUnexpectedlyAsync(
            "ptk_worker_exit kind=runtime_failure detail=runtime_failure\n",
            exitCode: 84);

        var failure = await Assert.ThrowsAsync<WorkerInvocationException>(
            () => call);

        Assert.Equal(
            WorkerInvocationDisposition.OutcomeUnknown,
            failure.Disposition);
        Assert.Equal("worker_exit_runtime_failure", failure.CauseDetailCode);
        Assert.Equal(84, failure.WorkerExit?.ExitCode);
        Assert.Equal(
            "ptk_worker_exit kind=runtime_failure detail=runtime_failure",
            failure.WorkerExit?.Diagnostic);
    }

    /// <summary>
    /// A worker can die without managing to say why. The exit code alone still
    /// separates "the process died" from "the pipe broke".
    /// </summary>
    [Fact]
    public async Task A_silent_death_reports_the_exit_code()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 12,
            Limits);
        await InitializeAsync(client, process);

        var call = client.InvokeAsync(
            "'dies'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            artifactCapture: null,
            CancellationToken.None);
        _ = await process.ReadRequestAsync();
        await process.ExitUnexpectedlyAsync(diagnostic: null, exitCode: 139);

        var failure = await Assert.ThrowsAsync<WorkerInvocationException>(
            () => call);

        Assert.Equal("worker_exited_unexpectedly", failure.CauseDetailCode);
        Assert.Equal(139, failure.WorkerExit?.ExitCode);
        Assert.Null(failure.WorkerExit?.Diagnostic);
    }

    /// <summary>
    /// A death that explains nothing must read exactly as it did before #13.
    /// Reporting a cause here would be inventing one.
    /// </summary>
    [Fact]
    public async Task A_death_that_explains_nothing_still_reports_the_transport_close()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 13,
            Limits);
        await InitializeAsync(client, process);

        var call = client.InvokeAsync(
            "'dies'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            artifactCapture: null,
            CancellationToken.None);
        _ = await process.ReadRequestAsync();
        await process.ExitUnexpectedlyAsync(diagnostic: null, exitCode: null);

        var failure = await Assert.ThrowsAsync<WorkerInvocationException>(
            () => call);

        Assert.Equal("worker_transport_closed", failure.CauseDetailCode);
    }

    /// <summary>
    /// Output the worker never authored must not be quoted back as its dying
    /// words: a caller's command writing to standard error is not a
    /// ptk_worker_exit line.
    /// </summary>
    [Fact]
    public async Task Foreign_standard_error_is_not_reported_as_a_worker_kind()
    {
        var process = new ScriptedProcess();
        await using var client = new ProcessSessionWorker(
            process,
            Guid.NewGuid(),
            incarnation: 14,
            Limits);
        await InitializeAsync(client, process);

        var call = client.InvokeAsync(
            "'dies'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            artifactCapture: null,
            CancellationToken.None);
        _ = await process.ReadRequestAsync();
        await process.ExitUnexpectedlyAsync(
            "cc1plus: out of memory allocating 65536 bytes\n",
            exitCode: 1);

        var failure = await Assert.ThrowsAsync<WorkerInvocationException>(
            () => call);

        // Retained and reported as text, but never parsed into a kind ptk
        // would then present as its own classification.
        Assert.Equal("worker_exited_unexpectedly", failure.CauseDetailCode);
        Assert.Equal(
            "cc1plus: out of memory allocating 65536 bytes",
            failure.WorkerExit?.Diagnostic);
    }

    private static async Task InitializeAsync(
        ProcessSessionWorker client,
        ScriptedProcess process)
    {
        var initialize = client.InitializeAsync(
            DateTimeOffset.UtcNow.Add(CheckpointTimeout),
            CancellationToken.None);
        var request = WorkerOperationProtocol.ParseInitialize(
            await process.ReadRequestAsync());
        await process.WriteEventAsync(
            WorkerOperationProtocol.CreateReadyEnvelope(request));
        await initialize.WaitAsync(CheckpointTimeout);
    }

    private static async Task WaitForFatalAsync(ProcessSessionWorker client)
    {
        _ = await Assert.ThrowsAnyAsync<Exception>(
            () => client.Fatal.WaitAsync(CheckpointTimeout));
    }

    private static OutputStore CreateOutputStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "session-worker-output-tests",
            Guid.NewGuid().ToString("N"));
        return new OutputStore(new OutputStoreOptions(
            root,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            MaximumArtifactBytes: 1024,
            MaximumSessionBytes: 1024,
            MaximumAggregateBytes: 1024));
    }

    private static bool Dispose(IDisposable? disposable)
    {
        disposable?.Dispose();
        return true;
    }

    private enum WriteFailureMode
    {
        SynchronousAtEntry,
        AsynchronousReturn,
        PartialWrite,
    }

    private sealed class ScriptedProcess : IWorkerContainedProcess
    {
        private static int _nextProcessId = 50000;
        private readonly Stream _requestWriter;
        private readonly Stream _requestReader;
        private readonly Stream _eventWriter;
        private readonly Stream _eventReader;
        private readonly WorkerProtocolReader _requests;
        private readonly WorkerProtocolWriter _events;
        private readonly TaskCompletionSource _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _containmentEmpty = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stream _standardError;
        private readonly Stream _standardErrorWriter;
        private int _disposed;

        private readonly RequestWriteStream _requestWriterMonitor;

        internal ScriptedProcess(WriteFailureMode? writeFailureMode = null)
        {
            var requests = new Pipe();
            var events = new Pipe();
            var standardError = new Pipe();
            _standardError = standardError.Reader.AsStream();
            _standardErrorWriter = standardError.Writer.AsStream();
            _requestWriterMonitor = new RequestWriteStream(
                requests.Writer.AsStream(),
                writeFailureMode);
            _requestWriter = _requestWriterMonitor;
            _requestReader = requests.Reader.AsStream();
            _eventWriter = events.Writer.AsStream();
            _eventReader = events.Reader.AsStream();
            _requests = new WorkerProtocolReader(_requestReader);
            _events = new WorkerProtocolWriter(_eventWriter);
            ProcessId = Interlocked.Increment(ref _nextProcessId);
        }

        public int ProcessId { get; }
        public int ContainmentProcessId => ProcessId;
        public Stream RequestWriter => _requestWriter;
        public Stream EventReader => _eventReader;
        public Stream StandardOutputReader => Stream.Null;
        public Stream StandardErrorReader => _standardError;
        public int? ExitCode { get; private set; }
        public Task ContainmentEmpty => _containmentEmpty.Task;
        internal int RequestWriteCalls => _requestWriterMonitor.WriteCalls;

        internal async Task<WorkerEnvelope> ReadRequestAsync() =>
            await _requests.ReadAsync()
                .AsTask()
                .WaitAsync(CheckpointTimeout) ??
            throw new EndOfStreamException("The client request stream ended.");

        internal Task WriteEventAsync(WorkerEnvelope envelope) =>
            _events.WriteAsync(envelope)
                .AsTask()
                .WaitAsync(CheckpointTimeout);

        internal async Task WritePartialEventAndExitAsync(
            ReadOnlyMemory<byte> bytes)
        {
            await _eventWriter.WriteAsync(bytes);
            await _eventWriter.FlushAsync();
            ExitUnexpectedly();
        }

        internal void ExitUnexpectedly()
        {
            _eventWriter.Dispose();
            _standardErrorWriter.Dispose();
            _exit.TrySetResult();
        }

        /// <summary>
        /// Dies the way a real worker does: writes its bounded exit
        /// diagnostic, closes both streams, and reports an exit code.
        /// </summary>
        internal async Task ExitUnexpectedlyAsync(
            string? diagnostic,
            int? exitCode)
        {
            if (diagnostic is not null)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(diagnostic);
                await _standardErrorWriter.WriteAsync(bytes);
                await _standardErrorWriter.FlushAsync();
            }
            ExitCode = exitCode;
            ExitUnexpectedly();
        }

        public Task WaitForExitAsync(
            CancellationToken cancellationToken = default) =>
            _exit.Task.WaitAsync(cancellationToken);

        public Task<WorkerContainmentResult> ContainAsync(
            WorkerContainmentReason reason)
        {
            _containmentEmpty.TrySetResult();
            _exit.TrySetResult();
            return Task.FromResult(WorkerContainmentResult.Confirmed());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _containmentEmpty.TrySetResult();
            _exit.TrySetResult();
            _requestWriter.Dispose();
            _requestReader.Dispose();
            _eventWriter.Dispose();
            _eventReader.Dispose();
            _standardErrorWriter.Dispose();
            _standardError.Dispose();
        }
    }

    private sealed class RequestWriteStream(
        Stream inner,
        WriteFailureMode? failureMode) : Stream
    {
        internal int WriteCalls { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            if (WriteCalls != 2 || failureMode is null)
                return inner.WriteAsync(buffer, cancellationToken);

            return failureMode.Value switch
            {
                WriteFailureMode.SynchronousAtEntry =>
                    throw new IOException("injected synchronous write failure"),
                WriteFailureMode.AsynchronousReturn =>
                    ValueTask.FromException(
                        new IOException("injected asynchronous write failure")),
                WriteFailureMode.PartialWrite =>
                    WritePartialThenFailAsync(buffer, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(failureMode)),
            };
        }

        private async ValueTask WritePartialThenFailAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken)
        {
            await inner.WriteAsync(
                buffer[..Math.Min(17, buffer.Length)],
                cancellationToken);
            throw new IOException("injected partial write failure");
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class StaticLauncher(IWorkerContainedProcess process) :
        IWorkerProcessLauncher
    {
        public Task<IWorkerContainedProcess> LaunchAsync(
            WorkerLaunchCommand command,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(process);
    }

    private sealed class ValidatingArtifactCapture(
        WorkerArtifactRequest request) : IWorkerArtifactCapture
    {
        private WorkerArtifactReceiver? _receiver;

        public WorkerArtifactRequest Request { get; } = request;
        public bool IsSealed => _receiver?.IsSealed == true;
        public Task SinkCompletionForTests => Task.CompletedTask;

        public void BindRequest(long requestId) =>
            _receiver = new WorkerArtifactReceiver(requestId, Request);

        public void Accept(WorkerArtifactChunk chunk) =>
            (_receiver ?? throw new InvalidOperationException()).Accept(chunk);

        public void Accept(WorkerArtifactSeal seal) =>
            (_receiver ?? throw new InvalidOperationException()).Accept(seal);

        public Task<OutputRecoverySummary> CompleteAtResultAsync() =>
            Task.FromResult(
                OutputRecoverySummary.Unavailable("test_capture"));

        public void Dispose() => _receiver?.Dispose();
    }
}
