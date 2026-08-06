using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;
using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

public sealed class SessionOperationsSeamTests
{
    [Fact]
    public async Task Tool_adapters_delegate_only_through_the_session_operations_seam()
    {
        var operations = new RecordingSessionOperations();
        using var cancellation = new CancellationTokenSource();
        using var outputStore = new OutputStore(new OutputStoreOptions(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ptk",
                "session-operations-tests",
                Guid.NewGuid().ToString("N")),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1),
            MaximumArtifactBytes: 1024,
            MaximumSessionBytes: 2048,
            MaximumAggregateBytes: 4096));

        Assert.Equal(
            "invoke",
            Text(await InvokeTool.Invoke(
                operations,
                "Get-Item .",
                cancellation.Token,
                raw: true,
                route: "rtk",
                timeoutSeconds: 17,
                session: "sample-online",
                outputStore: outputStore)));
        Assert.Equal(
            ["Get-Item .", cancellation.Token, true, "rtk", 17, "sample-online", outputStore],
            operations.LastArguments);

        Assert.Equal(
            "state",
            Text(await StateTool.State(
                operations,
                listAvailable: true,
                session: "sample-onprem",
                cancellationToken: cancellation.Token)));
        Assert.Equal(
            [true, "sample-onprem", cancellation.Token],
            operations.LastArguments);

        Assert.Equal(
            "reset",
            Text(await ResetTool.Reset(
                operations,
                session: "sample-online",
                cancellationToken: cancellation.Token)));
        Assert.Equal(
            ["sample-online", cancellation.Token],
            operations.LastArguments);

        Assert.Equal(
            "session",
            Text(await SessionTool.Session(
                operations,
                "open",
                name: "sample-online",
                cancellationToken: cancellation.Token)));
        Assert.Equal(
            ["open", "sample-online", cancellation.Token],
            operations.LastArguments);
    }

    private static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private sealed class RecordingSessionOperations : ISessionOperations
    {
        internal object?[] LastArguments { get; private set; } = [];

        public Task<ToolOutcome> InvokeAsync(
            string script,
            CancellationToken cancellationToken,
            bool raw,
            string route,
            int timeoutSeconds,
            string session,
            OutputStore? outputStore)
        {
            LastArguments =
                [script, cancellationToken, raw, route, timeoutSeconds, session, outputStore];
            return Task.FromResult(ToolOutcome.Completed("invoke"));
        }

        public Task<ToolOutcome> StateAsync(
            bool listAvailable,
            string session,
            CancellationToken cancellationToken)
        {
            LastArguments = [listAvailable, session, cancellationToken];
            return Task.FromResult(ToolOutcome.Completed("state"));
        }

        public Task<ToolOutcome> ResetAsync(
            string session,
            CancellationToken cancellationToken)
        {
            LastArguments = [session, cancellationToken];
            return Task.FromResult(ToolOutcome.Completed("reset"));
        }

        public Task<ToolOutcome> SessionAsync(
            string action,
            string? name,
            CancellationToken cancellationToken)
        {
            LastArguments = [action, name, cancellationToken];
            return Task.FromResult(ToolOutcome.Completed("session"));
        }
    }
}
