using System.IO.Pipelines;
using PtkMcpServer.Sessions;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerSessionIntegrationTests
{
    private static readonly WorkerProtocolLimits Limits =
        WorkerOperationProtocol.CreateLimits(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(5));

    [Fact]
    public async Task Real_session_stays_warm_reports_busy_and_cancels_once()
    {
        await using var worker = await RealWorkerFixture.StartAsync(Guid.NewGuid(), 1);
        var warmValue = "slice3-" + Guid.NewGuid().ToString("N");

        var seeded = await worker.InvokeAsync(
            requestId: 2,
            $"$global:ptkSlice3Warm = '{warmValue}'; 'seeded'");
        Assert.Equal(WorkerResultStatus.Completed, seeded.Status);

        var recalled = await worker.InvokeAsync(
            requestId: 3,
            "$global:ptkSlice3Warm");
        Assert.Equal(WorkerResultStatus.Completed, recalled.Status);
        Assert.Contains(warmValue, recalled.Text, StringComparison.Ordinal);

        var startedPath = Path.Combine(worker.Root, "invoke-started");
        var escapedStartedPath = startedPath.Replace("'", "''", StringComparison.Ordinal);
        await worker.SendAsync(WorkerOperationProtocol.CreateInvokeEnvelope(
            worker.SessionId,
            worker.Incarnation,
            requestId: 4,
            $"[IO.File]::WriteAllText('{escapedStartedPath}', 'started'); " +
                "Start-Sleep -Seconds 60; 'must-not-complete'",
            raw: false,
            WorkerInvokeRoute.Pwsh,
            timeoutSeconds: 120,
            artifact: null,
            Limits));
        await WaitForFileAsync(startedPath);

        await worker.SendAsync(WorkerOperationProtocol.CreateStateQueryEnvelope(
            worker.SessionId,
            worker.Incarnation,
            requestId: 5,
            listAvailable: false));
        var stateEnvelope = await worker.ReadForRequestAsync(
            requestId: 5,
            WorkerMessageKind.StateSnapshot);
        var state = WorkerOperationProtocol.ParseStateSnapshot(
            stateEnvelope,
            worker.SessionId,
            worker.Incarnation);
        Assert.False(state.Available);
        Assert.Equal("runspace_busy", state.DetailCode);

        await worker.SendAsync(WorkerOperationProtocol.CreateCancelEnvelope(
            worker.SessionId,
            worker.Incarnation,
            requestId: 4));
        var canceledEnvelope = await worker.ReadForRequestAsync(
            requestId: 4,
            WorkerMessageKind.Result);
        var canceled = WorkerOperationProtocol.ParseResult(
            canceledEnvelope,
            worker.SessionId,
            worker.Incarnation);
        Assert.Equal(WorkerResultStatus.Canceled, canceled.Status);
        Assert.Equal("operation_canceled", canceled.DetailCode);

        var stillWarm = await worker.InvokeAsync(
            requestId: 6,
            "$global:ptkSlice3Warm");
        Assert.Equal(WorkerResultStatus.Completed, stillWarm.Status);
        Assert.Contains(warmValue, stillWarm.Text, StringComparison.Ordinal);

        await worker.ShutdownAsync(requestId: 7);
        Assert.Equal(
            1,
            worker.Received.Count(frame =>
                frame.Kind == WorkerMessageKind.Result &&
                frame.RequestId == 4));
    }

    [Fact]
    public async Task Two_real_worker_servers_reject_stale_and_cross_routed_frames()
    {
        var firstSessionId = Guid.NewGuid();
        var secondSessionId = Guid.NewGuid();
        await using var first = await RealWorkerFixture.StartAsync(firstSessionId, 3);
        await using var second = await RealWorkerFixture.StartAsync(secondSessionId, 7);

        await first.SendAsync(WorkerOperationProtocol.CreateStateQueryEnvelope(
            firstSessionId,
            incarnation: 4,
            requestId: 2,
            listAvailable: false));
        await second.SendAsync(WorkerOperationProtocol.CreateStateQueryEnvelope(
            firstSessionId,
            incarnation: 7,
            requestId: 2,
            listAvailable: false));

        var exits = await Task.WhenAll(
            first.WaitForExitAsync(),
            second.WaitForExitAsync());
        Assert.Equal(
            new WorkerServerExit(
                WorkerServerExitKind.ProtocolError,
                "worker_incarnation_mismatch"),
            exits[0]);
        Assert.Equal(
            new WorkerServerExit(
                WorkerServerExitKind.ProtocolError,
                "session_identity_mismatch"),
            exits[1]);
        Assert.All(
            new[] { first, second },
            worker => Assert.Collection(
                worker.Received,
                frame => Assert.Equal(WorkerMessageKind.Ready, frame.Kind)));
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path)) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The real worker invocation did not enter PowerShell.");
    }

    private sealed class RealWorkerFixture : IAsyncDisposable
    {
        private readonly Stream _requestRead;
        private readonly Stream _requestWrite;
        private readonly Stream _eventRead;
        private readonly Stream _eventWrite;
        private readonly WorkerProtocolWriter _writer;
        private readonly WorkerProtocolReader _reader;
        private readonly Task<WorkerServerExit> _run;
        private bool _stopped;

        private RealWorkerFixture(Guid sessionId, long incarnation)
        {
            SessionId = sessionId;
            Incarnation = incarnation;
            Root = Directory.CreateTempSubdirectory("ptk-worker-session-").FullName;

            var requests = new Pipe();
            var events = new Pipe();
            _requestRead = requests.Reader.AsStream();
            _requestWrite = requests.Writer.AsStream();
            _eventRead = events.Reader.AsStream();
            _eventWrite = events.Writer.AsStream();
            _writer = new WorkerProtocolWriter(_requestWrite);
            _reader = new WorkerProtocolReader(_eventRead);

            var server = new WorkerServer(
                _requestRead,
                _eventWrite,
                (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RunspaceHost? host = new(
                        callTimeout: TimeSpan.FromSeconds(30),
                        maxCallTimeout: TimeSpan.FromMinutes(5));
                    try
                    {
                        var session = new WorkerSession(new SessionRuntime(
                            host,
                            new RawUsageCounter()));
                        host = null;
                        return Task.FromResult<IWorkerSession>(session);
                    }
                    finally
                    {
                        host?.Dispose();
                    }
                });
            _run = server.RunAsync();
        }

        internal Guid SessionId { get; }
        internal long Incarnation { get; }
        internal string Root { get; }
        internal List<WorkerEnvelope> Received { get; } = [];

        internal static async Task<RealWorkerFixture> StartAsync(
            Guid sessionId,
            long incarnation)
        {
            var fixture = new RealWorkerFixture(sessionId, incarnation);
            try
            {
                await fixture.SendAsync(
                    WorkerOperationProtocol.CreateInitializeEnvelope(
                        sessionId,
                        incarnation,
                        requestId: 1,
                        DateTimeOffset.UtcNow.AddSeconds(30),
                        Limits));
                var ready = await fixture.ReadAsync();
                Assert.Equal(WorkerMessageKind.Ready, ready.Kind);
                Assert.Equal(
                    Limits,
                    WorkerOperationProtocol.ParseReady(
                        ready,
                        sessionId,
                        incarnation,
                        expectedRequestId: 1));
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal Task SendAsync(WorkerEnvelope envelope) =>
            _writer.WriteAsync(envelope).AsTask();

        internal async Task<WorkerResult> InvokeAsync(long requestId, string script)
        {
            await SendAsync(WorkerOperationProtocol.CreateInvokeEnvelope(
                SessionId,
                Incarnation,
                requestId,
                script,
                raw: false,
                WorkerInvokeRoute.Pwsh,
                timeoutSeconds: 30,
                artifact: null,
                Limits));
            return WorkerOperationProtocol.ParseResult(
                await ReadForRequestAsync(requestId, WorkerMessageKind.Result),
                SessionId,
                Incarnation);
        }

        internal async Task<WorkerEnvelope> ReadForRequestAsync(
            long requestId,
            WorkerMessageKind kind)
        {
            while (true)
            {
                var frame = await ReadAsync();
                if (frame.RequestId == requestId && frame.Kind == kind) return frame;
            }
        }

        internal async Task ShutdownAsync(long requestId)
        {
            if (_stopped) return;
            await SendAsync(WorkerOperationProtocol.CreateEmptyEnvelope(
                WorkerMessageKind.Shutdown,
                SessionId,
                Incarnation,
                requestId));
            var stopped = await ReadForRequestAsync(requestId, WorkerMessageKind.Stopped);
            WorkerOperationProtocol.ParseEmpty(
                stopped,
                WorkerMessageKind.Stopped,
                SessionId,
                Incarnation);
            Assert.Equal(
                new WorkerServerExit(WorkerServerExitKind.Shutdown, "shutdown"),
                await WaitForExitAsync());
            _stopped = true;
        }

        internal Task<WorkerServerExit> WaitForExitAsync() =>
            _run.WaitAsync(TimeSpan.FromSeconds(10));

        public async ValueTask DisposeAsync()
        {
            if (!_run.IsCompleted)
            {
                try
                {
                    await ShutdownAsync(requestId: long.MaxValue);
                }
                catch
                {
                    try { await _requestWrite.DisposeAsync(); }
                    catch { }
                    try { await _run.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch { }
                }
            }

            await DisposeStreamAsync(_requestWrite);
            await DisposeStreamAsync(_requestRead);
            await DisposeStreamAsync(_eventWrite);
            await DisposeStreamAsync(_eventRead);
            try { Directory.Delete(Root, recursive: true); }
            catch { }
        }

        private async Task<WorkerEnvelope> ReadAsync()
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var frame = await _reader.ReadAsync(cancellation.Token) ??
                throw new EndOfStreamException(
                    "The real worker fixture ended before its expected frame.");
            Received.Add(frame);
            return frame;
        }

        private static async ValueTask DisposeStreamAsync(Stream stream)
        {
            try { await stream.DisposeAsync(); }
            catch { }
        }
    }
}
