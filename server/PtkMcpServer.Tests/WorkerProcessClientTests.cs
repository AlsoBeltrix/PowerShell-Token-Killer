using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerProcessClientTests
{
    private static readonly DateTimeOffset Deadline =
        DateTimeOffset.UtcNow.AddMinutes(2);

    [Fact]
    public async Task Launch_binds_dynamic_boot_drains_diagnostics_and_owns_clean_shutdown()
    {
        var diagnostic = Encoding.UTF8.GetBytes(
            new string('s', 64 * 1024 + 257));
        var launcher = new FakeLauncher(diagnostic);
        await using var authority = await WorkerProcessClient.LaunchAsync(
            launcher,
            Command(),
            generation: 41,
            Deadline,
            cancellationToken: TestContext.Current.CancellationToken);

        var response = await authority.Client.ExecuteAsync(
            WorkerSessionOperationCodec.StateOperation,
            new WorkerStateArguments(ListAvailable: false),
            Deadline,
            TestContext.Current.CancellationToken);
        await authority.ShutdownAsync(TestContext.Current.CancellationToken);
        var diagnostics = await authority.Diagnostics;

        Assert.NotEqual(Guid.Empty, authority.WorkerBootId);
        Assert.Equal(41, authority.Generation);
        Assert.Equal(WorkerOperationStatus.Completed, response.Status);
        Assert.Equal(1, launcher.Process!.Runtime.ExecutionCount);
        Assert.Equal(1, launcher.Process.DisposeCount);
        Assert.False(authority.Fatal.IsCompleted);
        Assert.Equal(diagnostic.Length, diagnostics.StandardOutput.TotalBytes);
        Assert.Equal(
            64 * 1024,
            diagnostics.StandardOutput.DigestedBytes);
        Assert.True(diagnostics.StandardOutput.Truncated);
        Assert.Equal(64, diagnostics.StandardOutput.PrefixSha256.Length);
        Assert.Equal(0, diagnostics.StandardError.TotalBytes);
        Assert.False(diagnostics.StandardError.Truncated);
    }

    [Fact]
    public async Task Unexpected_process_exit_fails_generation_and_contains_once()
    {
        var launcher = new FakeLauncher([]);
        await using var authority = await WorkerProcessClient.LaunchAsync(
            launcher,
            Command(),
            generation: 43,
            Deadline,
            cancellationToken: TestContext.Current.CancellationToken);

        launcher.Process!.Crash();
        var exception = await Assert.ThrowsAsync<WorkerProcessException>(async () =>
            await authority.Fatal.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Equal("worker_exited", exception.DetailCode);
        await launcher.Process.Contained.WaitAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(1, launcher.Process.DisposeCount);
    }

    [Fact]
    public async Task Exit_before_hello_refuses_launch_and_contains_once()
    {
        var launcher = new FakeLauncher([], exitBeforeHello: true);

        var exception = await Assert.ThrowsAsync<WorkerProcessException>(() =>
            WorkerProcessClient.LaunchAsync(
                launcher,
                Command(),
                generation: 47,
                Deadline,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("worker_exited_before_ready", exception.DetailCode);
        Assert.Equal(1, launcher.Process!.DisposeCount);
    }

    private static WorkerLaunchCommand Command() =>
        new(
            Path.GetFullPath("/ptk-test-worker"),
            ["--worker"],
            Path.GetFullPath("/"),
            []);

    private sealed class FakeLauncher(
        byte[] standardOutput,
        bool exitBeforeHello = false) : IWorkerProcessLauncher
    {
        internal FakeContainedProcess? Process { get; private set; }

        public Task<IWorkerContainedProcess> LaunchAsync(
            WorkerLaunchCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process = new FakeContainedProcess(standardOutput, exitBeforeHello);
            return Task.FromResult<IWorkerContainedProcess>(Process);
        }
    }

    private sealed class FakeContainedProcess : IWorkerContainedProcess
    {
        private static readonly Guid BootId = Guid.ParseExact(
            "63ed69bc-24ce-4554-9f7c-30db01dd1964",
            "D");

        private readonly Pipe _requests = new();
        private readonly Pipe _events = new();
        private readonly Stream _serverRequests;
        private readonly Stream _serverEvents;
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _contained = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        internal FakeContainedProcess(byte[] standardOutput, bool exitBeforeHello)
        {
            RequestWriter = _requests.Writer.AsStream(leaveOpen: true);
            EventReader = _events.Reader.AsStream(leaveOpen: true);
            _serverRequests = _requests.Reader.AsStream(leaveOpen: true);
            _serverEvents = _events.Writer.AsStream(leaveOpen: true);
            StandardOutputReader = new MemoryStream(standardOutput, writable: false);
            StandardErrorReader = new MemoryStream([], writable: false);
            Runtime = new FakeRuntime();
            if (exitBeforeHello)
            {
                _exited.TrySetResult();
                _ = CloseServerStreamsAsync();
            }
            else
            {
                var server = new WorkerServer(
                    _serverRequests,
                    _serverEvents,
                    (_, _) => Task.FromResult<ISessionLifetime>(Runtime),
                    BootId);
                _ = RunServerAsync(server);
            }
        }

        public int ProcessId => 4242;
        public Stream RequestWriter { get; }
        public Stream EventReader { get; }
        public Stream StandardOutputReader { get; }
        public Stream StandardErrorReader { get; }
        internal FakeRuntime Runtime { get; }
        internal int DisposeCount { get; private set; }
        internal Task Contained => _contained.Task;

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _exited.Task.WaitAsync(cancellationToken);

        public Task ContainAsync()
        {
            Dispose();
            return _exited.Task;
        }

        internal void Crash()
        {
            _exited.TrySetResult();
            _ = CloseServerStreamsAsync();
        }

        private async Task RunServerAsync(WorkerServer server)
        {
            try
            {
                _ = await server.RunAsync();
            }
            finally
            {
                await CloseServerStreamsAsync();
                _exited.TrySetResult();
            }
        }

        private async Task CloseServerStreamsAsync()
        {
            try
            {
                await _serverRequests.DisposeAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            try
            {
                await _serverEvents.DisposeAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            DisposeCount++;
            try
            {
                RequestWriter.Dispose();
                EventReader.Dispose();
                StandardOutputReader.Dispose();
                StandardErrorReader.Dispose();
                _serverRequests.Dispose();
                _serverEvents.Dispose();
            }
            finally
            {
                _exited.TrySetResult();
                _contained.TrySetResult();
            }
        }
    }

    private sealed class FakeRuntime : IWorkerSessionRuntime
    {
        internal int ExecutionCount { get; private set; }

        public Task<JsonElement> ExecuteAsync(
            WorkerOperationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return Task.FromResult(
                WorkerSessionOperationCodec.CreateResult(
                    request.Operation,
                    new WorkerStateResult("process-client-state")));
        }

        public Task<WorkerPreparedRuntimeResult> InvokeAsync(
            WorkerInvokePreparePayload prepare,
            IInvocationAuthorizer authorizer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ShutdownAsync() => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
