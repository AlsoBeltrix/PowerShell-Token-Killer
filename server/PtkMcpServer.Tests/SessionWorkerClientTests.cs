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
            artifact: null,
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
            artifact: null,
            CancellationToken.None);
        _ = await process.ReadRequestAsync();
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(
            () => secondCall);

        Assert.Equal("request_id_mismatch", failure.DetailCode);
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

        var call = client.InvokeAsync(
            "'artifact'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 30,
            artifact,
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

        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(
            () => call);

        Assert.Equal("artifact_sequence_invalid", failure.DetailCode);
        Assert.False(client.IsTransportUsable);
        _ = await Assert.ThrowsAsync<WorkerProtocolException>(
            () => client.Fatal);
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
            artifact: null,
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
        private int _disposed;

        internal ScriptedProcess()
        {
            var requests = new Pipe();
            var events = new Pipe();
            _requestWriter = requests.Writer.AsStream();
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
        public Stream StandardErrorReader => Stream.Null;
        public Task ContainmentEmpty => _containmentEmpty.Task;

        internal async Task<WorkerEnvelope> ReadRequestAsync() =>
            await _requests.ReadAsync()
                .AsTask()
                .WaitAsync(CheckpointTimeout) ??
            throw new EndOfStreamException("The client request stream ended.");

        internal Task WriteEventAsync(WorkerEnvelope envelope) =>
            _events.WriteAsync(envelope)
                .AsTask()
                .WaitAsync(CheckpointTimeout);

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
}
