using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PtkMcpServer;

internal static class SupervisorCallFilter
{
    internal const string NotStartedMessage =
        "[operation not started] The operation was not started.";

    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Create() =>
        next => async (request, cancellationToken) =>
        {
            SupervisorLifecycle lifecycle;
            try
            {
                lifecycle = request.Services?.GetRequiredService<SupervisorLifecycle>()
                    ?? throw new InvalidOperationException();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                return Refusal();
            }

            if (!lifecycle.TryBeginCall(
                    cancellationToken,
                    out var lease,
                    out var callCancellation))
            {
                return Refusal();
            }

            using (lease)
            {
                return await next(request, callCancellation).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The MCP tool returned no result.");
            }
        };

    private static CallToolResult Refusal() => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = NotStartedMessage,
            },
        ],
    };

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
