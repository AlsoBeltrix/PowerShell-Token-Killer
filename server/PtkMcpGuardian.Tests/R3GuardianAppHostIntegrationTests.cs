using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PtkMcpGuardian.Standalone.Fake;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class R3GuardianAppHostIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task One_real_MCP_connection_survives_fake_host_crash_and_model_gated_retry()
    {
        var control = new R3FakeHostControl();
        control.EnqueueAttempt(new R3FakeHostAttemptPlan
        {
            HoldContainmentProof = true,
        });
        var composition = R3FakeGuardianComposition.Create(control);
        await using var harness = new RealMcpHarness(composition);

        await harness.InitializeAsync();
        var listed = await harness.RequestAsync("tools/list", new { });
        Assert.Equal(
            PublicToolContractResource.Parse().Tools.Count,
            listed.GetProperty("result").GetProperty("tools").GetArrayLength());

        control.EnqueueOperation(new R3FakeHostOperationPlan
        {
            ResponseText = "[\"before-crash\"]",
        });
        Assert.Equal(
            "[\"before-crash\"]",
            ToolText(await harness.CallJobListAsync(), expectedError: false));

        var firstAttempt = Assert.Single(composition.Factory.Attempts);
        Assert.Equal(1, firstAttempt.Peer.JobListEffectCount);
        firstAttempt.Crash();
        await WaitUntilAsync(() => firstAttempt.ContainmentStartCount == 1);

        var recovering = await harness.ReadStateAsync();
        Assert.Equal(PublicHostState.Recovering, recovering.Host.State);
        Assert.Equal(RecoveryPhase.Containment, recovering.Host.RecoveryPhase);
        Assert.False(recovering.Host.ReadyForEffects);
        Assert.False(Assert.Single(recovering.Sessions).ReadyForEffects);

        var refused = PublicRecoveryCodec.Decode(Encoding.UTF8.GetBytes(
            ToolText(await harness.CallJobListAsync(), expectedError: true)));
        Assert.True(refused.Retryable);
        Assert.Equal(PublicRecoveryDetailCode.HostRecovering, refused.DetailCode);
        var sessionGate = Assert.IsType<SessionReadyGate>(refused.RetryGate);
        Assert.Equal("default", sessionGate.Alias.Value);
        Assert.Equal(1, firstAttempt.Peer.JobListEffectCount);

        // The fake model obeys the refusal: only guardian-local state is
        // polled until the named session gate is ready. No backend call is
        // retained or resubmitted while containment is unresolved.
        Assert.False((await harness.ReadStateAsync()).Sessions.Single().ReadyForEffects);
        firstAttempt.ContainmentProofBarrier.Release();
        var ready = await WaitForReadyStateAsync(harness);
        Assert.True(ready.Host.ReadyForEffects);
        Assert.True(Assert.Single(ready.Sessions).ReadyForEffects);

        control.EnqueueOperation(new R3FakeHostOperationPlan
        {
            ResponseText = "[\"after-recovery\"]",
        });
        Assert.Equal(
            "[\"after-recovery\"]",
            ToolText(await harness.CallJobListAsync(), expectedError: false));

        Assert.Equal(2, composition.Factory.Attempts.Count);
        var replacement = composition.Factory.Attempts[1];
        Assert.Equal(1, replacement.Peer.JobListEffectCount);
        Assert.Equal(1, harness.InitializeCount);
        Assert.Equal(1, harness.InitializedNotificationCount);

        await harness.ShutdownAsync();
        Assert.Equal(0, harness.ExitCode);
        Assert.Equal(string.Empty, harness.StandardError);
        Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
        Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
        Assert.All(composition.Factory.Attempts, attempt =>
        {
            Assert.True(attempt.HostExited.IsCompleted);
            Assert.True(attempt.ContainmentConfirmed.IsCompleted);
            Assert.Equal(1, attempt.DisposeCount);
        });
        Assert.All(harness.PublicLines, line =>
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        });
    }

    [Fact]
    public async Task Partial_private_response_is_one_nonretryable_outcome_unknown_terminal()
    {
        var control = new R3FakeHostControl();
        var composition = R3FakeGuardianComposition.Create(control);
        await using var harness = new RealMcpHarness(composition);

        await harness.InitializeAsync();
        control.EnqueueOperation(new R3FakeHostOperationPlan
        {
            Behavior = R3FakeHostOperationBehavior.PartialResponseThenCrash,
        });

        var responseCountBefore = harness.PublicLines.Count;
        var response = await harness.CallJobListAsync();
        var terminal = PublicRecoveryCodec.Decode(Encoding.UTF8.GetBytes(
            ToolText(response, expectedError: true)));

        Assert.Equal(PublicRecoveryDetailCode.OutcomeUnknown, terminal.DetailCode);
        Assert.False(terminal.Retryable);
        Assert.Null(terminal.RetryAfterMilliseconds);
        Assert.Null(terminal.RecoveryPhase);
        Assert.Null(terminal.RecoveryAttempt);
        Assert.Null(terminal.RetryGate);
        Assert.Equal(responseCountBefore + 1, harness.PublicLines.Count);
        Assert.Equal(1, composition.Factory.Attempts[0].Peer.JobListEffectCount);

        await harness.ShutdownAsync();
        Assert.Equal(0, harness.ExitCode);
        Assert.Equal(string.Empty, harness.StandardError);
        Assert.Equal(1, harness.InitializeCount);
        Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
        Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
    }

    private static string ToolText(JsonElement response, bool expectedError)
    {
        var result = response.GetProperty("result");
        Assert.Equal(expectedError, result.GetProperty("isError").GetBoolean());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        return Assert.IsType<string>(content.GetProperty("text").GetString());
    }

    private static async Task<PublicStateSnapshot> WaitForReadyStateAsync(
        RealMcpHarness harness)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        while (true)
        {
            var state = await harness.ReadStateAsync();
            if (state.Host.ReadyForEffects &&
                state.Sessions.Count == 1 &&
                state.Sessions[0].ReadyForEffects)
            {
                return state;
            }
            await Task.Delay(5, cancellation.Token);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        while (!predicate())
            await Task.Delay(5, cancellation.Token);
    }

    private sealed class RealMcpHarness : IAsyncDisposable
    {
        private readonly R3FakeGuardianComposition _composition;
        private readonly ChannelStream _input = new();
        private readonly ChannelStream _output = new();
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;
        private readonly StringWriter _standardError = new();
        private readonly Task<int> _application;
        private int _nextRequestId;
        private int _initializeCount;
        private int _initializedNotificationCount;
        private bool _shutdown;

        internal RealMcpHarness(R3FakeGuardianComposition composition)
        {
            _composition = composition ?? throw new ArgumentNullException(nameof(composition));
            _writer = new StreamWriter(
                _input,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            _reader = new StreamReader(
                _output,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            _application = Program.RunAsync(
                ["--fake-host"],
                _input,
                _output,
                _standardError,
                composition);
        }

        internal List<string> PublicLines { get; } = [];

        internal int InitializeCount => Volatile.Read(ref _initializeCount);

        internal int InitializedNotificationCount =>
            Volatile.Read(ref _initializedNotificationCount);

        internal int ExitCode => _application.GetAwaiter().GetResult();

        internal string StandardError => _standardError.ToString();

        internal async Task InitializeAsync()
        {
            Interlocked.Increment(ref _initializeCount);
            var response = await RequestAsync("initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "r3-real-mcp-client", version = "1.0.0" },
            });
            Assert.True(response.TryGetProperty("result", out _), response.GetRawText());
            Interlocked.Increment(ref _initializedNotificationCount);
            await NotifyAsync("notifications/initialized", new { });
        }

        internal Task<JsonElement> CallJobListAsync() => RequestAsync(
            "tools/call",
            new
            {
                name = "ptk_job",
                arguments = new { action = "list" },
            });

        internal async Task<PublicStateSnapshot> ReadStateAsync()
        {
            var response = await RequestAsync(
                "tools/call",
                new
                {
                    name = "ptk_state",
                    arguments = new { },
                });
            return PublicStateCodec.Decode(Encoding.UTF8.GetBytes(
                ToolText(response, expectedError: false)));
        }

        internal async Task<JsonElement> RequestAsync(string method, object parameters)
        {
            var id = Interlocked.Increment(ref _nextRequestId);
            await WriteAsync(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            });

            using var cancellation = new CancellationTokenSource(TestTimeout);
            while (true)
            {
                var line = await _reader.ReadLineAsync(cancellation.Token);
                Assert.NotNull(line);
                PublicLines.Add(line);
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind == JsonValueKind.Number &&
                    responseId.GetInt32() == id)
                {
                    return message.Clone();
                }
            }
        }

        internal Task NotifyAsync(string method, object parameters) =>
            WriteAsync(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters,
            });

        internal async Task ShutdownAsync()
        {
            if (_shutdown)
                return;
            foreach (var attempt in _composition.Factory.Attempts)
                attempt.ContainmentProofBarrier.Release();
            _input.CompleteWriting();
            _ = await _application.WaitAsync(TestTimeout);
            _shutdown = true;
        }

        private async Task WriteAsync(object message)
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(message));
            await _writer.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await ShutdownAsync();
            }
            finally
            {
                _writer.Dispose();
                _reader.Dispose();
                _standardError.Dispose();
                _input.Dispose();
                _output.Dispose();
            }
        }
    }

    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private byte[]? _current;
        private int _currentOffset;
        private int _disposed;

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => Volatile.Read(ref _disposed) == 0;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void CompleteWriting() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            while (_current is null || _currentOffset == _current.Length)
            {
                _current = null;
                _currentOffset = 0;
                try
                {
                    _current = await _chunks.Reader.ReadAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
            _current.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            return count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _chunks.Writer.WriteAsync(buffer.ToArray(), cancellationToken)
                .ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _chunks.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
