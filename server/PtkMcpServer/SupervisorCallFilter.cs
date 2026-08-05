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
                var result = await next(request, callCancellation).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The MCP tool returned no result.");
                return MarkRefusalAsError(result);
            }
        };

    /// <summary>
    /// Sets the protocol's own error flag on a refusal.
    ///
    /// PTK's tools return a string, which the SDK always wraps as a successful
    /// call, so every refusal — an unknown session, a session at capacity, a
    /// worker still recovering, an unknown output handle, a malformed field —
    /// arrived with <c>isError=false</c>. A client that trusts that flag read
    /// "nothing was executed" as success, and the real outcome was legible
    /// only by parsing the bracketed text (GitHub #34).
    ///
    /// A refusal is recognized by the in-band marker the tools already emit;
    /// the text is left exactly as it was, so nothing that parses it breaks.
    /// </summary>
    internal static CallToolResult MarkRefusalAsErrorForTests(CallToolResult result) =>
        MarkRefusalAsError(result);

    private static CallToolResult MarkRefusalAsError(CallToolResult result)
    {
        if (result.IsError == true) return result;

        var text = result.Content?
            .OfType<TextContentBlock>()
            .FirstOrDefault()?.Text;
        if (string.IsNullOrEmpty(text)) return result;

        if (!IsRefusalText(text)) return result;

        result.IsError = true;
        return result;
    }

    /// <summary>
    /// Whether a response says the work was refused and nothing ran. Matches
    /// only PTK's own leading refusal markers, never a phrase that could
    /// appear in a user command's output.
    /// </summary>
    private static bool IsRefusalText(string text)
    {
        var span = text.AsSpan().TrimStart();

        // "[ptk <tool>] refused ..." and "[ptk output] invalid request: ..."
        if (span.StartsWith("[ptk ", StringComparison.Ordinal))
        {
            var close = span.IndexOf(']');
            if (close > 0)
            {
                var rest = span[(close + 1)..].TrimStart();
                if (rest.StartsWith("refused", StringComparison.Ordinal) ||
                    rest.StartsWith("invalid request", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return span.StartsWith(NotStartedMessage, StringComparison.Ordinal)
            || span.StartsWith("[operation not started]", StringComparison.Ordinal);
    }

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
