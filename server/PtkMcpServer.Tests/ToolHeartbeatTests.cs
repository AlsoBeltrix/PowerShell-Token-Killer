using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tests;

/// <summary>
/// GitHub #44: a client that supplied a progressToken reaps a call that
/// stays silent past its idle timeout, silently capping ptk's own
/// timeoutSeconds contract. The heartbeat holds the channel open for the
/// call's whole budget.
/// </summary>
[Collection("ToolHeartbeatInterval")]
public sealed class ToolHeartbeatTests : IDisposable
{
    private readonly TimeSpan _originalInterval = ToolHeartbeat.Interval;

    public void Dispose() => ToolHeartbeat.Interval = _originalInterval;

    private sealed class RecordingProgress : IProgress<ProgressNotificationValue>
    {
        private readonly List<ProgressNotificationValue> _reports = [];
        public IReadOnlyList<ProgressNotificationValue> Reports
        {
            get { lock (_reports) return [.. _reports]; }
        }
        public void Report(ProgressNotificationValue value)
        {
            lock (_reports) _reports.Add(value);
        }
    }

    [Fact]
    public async Task Fast_work_completes_without_a_beat()
    {
        ToolHeartbeat.Interval = TimeSpan.FromSeconds(30);
        var progress = new RecordingProgress();

        var result = await ToolHeartbeat.KeepAliveAsync(
            Task.FromResult(42), progress);

        Assert.Equal(42, result);
        Assert.Empty(progress.Reports);
    }

    [Fact]
    public async Task Slow_work_beats_until_it_finishes_then_returns_its_result()
    {
        ToolHeartbeat.Interval = TimeSpan.FromMilliseconds(20);
        var progress = new RecordingProgress();
        var work = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var keepAlive = ToolHeartbeat.KeepAliveAsync(work.Task, progress);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (progress.Reports.Count < 2 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(progress.Reports.Count >= 2, "expected at least two heartbeats");
        work.SetResult("done");
        Assert.Equal("done", await keepAlive);

        // A monotonically increasing count, no fabricated total.
        var first = progress.Reports[0];
        var second = progress.Reports[1];
        Assert.Equal(1f, first.Progress);
        Assert.Equal(2f, second.Progress);
        Assert.Null(first.Total);
        Assert.Equal("executing", first.Message);
    }

    [Fact]
    public async Task A_faulted_work_task_rethrows_through_the_heartbeat()
    {
        ToolHeartbeat.Interval = TimeSpan.FromSeconds(30);
        var progress = new RecordingProgress();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ToolHeartbeat.KeepAliveAsync(
                Task.FromException<int>(new InvalidOperationException("boom")),
                progress));
        Assert.Empty(progress.Reports);
    }

    /// <summary>
    /// The wiring guard: the heartbeat must actually cover the tool's await
    /// of the supervisor, not merely exist as a helper. A slow invoke must
    /// produce beats through the real <c>InvokeTool</c> entry point.
    /// </summary>
    [Fact]
    public async Task A_slow_invoke_beats_through_the_real_tool()
    {
        ToolHeartbeat.Interval = TimeSpan.FromMilliseconds(20);
        var progress = new RecordingProgress();
        var gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var call = Tools.InvokeTool.Invoke(
            new SlowOperations(gate.Task),
            "'x'",
            CancellationToken.None,
            progress: progress);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (progress.Reports.Count < 1 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(progress.Reports.Count >= 1,
            "expected a heartbeat while the invoke was executing");
        gate.SetResult();
        var result = await call;
        Assert.Null(result.IsError);
    }

    /// <summary>
    /// The claim the tool comments make, pinned at the wire: the injected
    /// progress parameter is SDK infrastructure and must never appear in
    /// the tool's argument schema a client sees.
    /// </summary>
    [Fact]
    public void The_injected_progress_parameter_never_reaches_the_tool_schema()
    {
        var services = new ServiceCollection()
            .AddSingleton<ISessionOperations>(new SlowOperations(Task.CompletedTask))
            .BuildServiceProvider();
        var tool = ModelContextProtocol.Server.McpServerTool.Create(
            typeof(Tools.InvokeTool).GetMethod(nameof(Tools.InvokeTool.Invoke))!,
            options: new ModelContextProtocol.Server.McpServerToolCreateOptions
            {
                Services = services,
            });

        var schema = tool.ProtocolTool.InputSchema.GetRawText();
        Assert.Contains("script", schema);
        Assert.DoesNotContain("progress", schema);
        Assert.DoesNotContain("runtime", schema);
        Assert.DoesNotContain("cancellationToken", schema);
    }

    private sealed class SlowOperations(Task gate) : ISessionOperations
    {
        public async Task<ToolOutcome> InvokeAsync(
            string script,
            CancellationToken cancellationToken,
            bool raw,
            string route,
            int timeoutSeconds,
            string session,
            OutputStore? outputStore)
        {
            await gate.ConfigureAwait(false);
            return ToolOutcome.Completed("slow result");
        }

        public Task<ToolOutcome> StateAsync(
            bool listAvailable, string session, CancellationToken cancellationToken) =>
            Task.FromResult(ToolOutcome.Completed("state"));

        public Task<ToolOutcome> ResetAsync(
            string session, CancellationToken cancellationToken) =>
            Task.FromResult(ToolOutcome.Completed("reset"));

        public Task<ToolOutcome> SessionAsync(
            string action, string? name, CancellationToken cancellationToken) =>
            Task.FromResult(ToolOutcome.Completed("session"));
    }
}
