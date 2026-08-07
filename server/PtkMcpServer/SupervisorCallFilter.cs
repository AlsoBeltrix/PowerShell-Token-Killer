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
                return MarkRefusalAsError(request.Params?.Name, result);
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
    internal static CallToolResult MarkRefusalAsErrorForTests(
        string? toolName,
        CallToolResult result) =>
        MarkRefusalAsError(toolName, result);

    private static CallToolResult MarkRefusalAsError(string? toolName, CallToolResult result)
    {
        if (result.IsError == true) return result;

        // opr-53/o53-3: the session tools author their own verdict
        // (ToolOutcome) — and their completed responses deliberately carry
        // no structured marker, because clients that prefer structured
        // content over text would hide the output. The gate is therefore
        // the tool's identity, never the response shape: matching a session
        // tool's text would let worker output forge the error flag back
        // into the channel opr-53 removed it from. Text matching exists
        // only for ptk_output, the one tool that still returns a bare
        // string, and ptk_output never renders worker output as its own
        // status line.
        if (toolName != "ptk_output") return result;

        var text = result.Content?
            .OfType<TextContentBlock>()
            .FirstOrDefault()?.Text;
        if (string.IsNullOrEmpty(text)) return result;

        if (!IsRefusalText(text)) return result;

        result.IsError = true;
        return result;
    }

    /// <summary>
    /// Whether a response says PTK refused the work and nothing ran.
    ///
    /// Recognized from the first line only, and only when it is entirely one
    /// of PTK's own status markers. Two constraints shape this, both from
    /// codereview round 2:
    ///
    /// - It must not fire on a user's own output. A script whose stdout
    ///   happens to begin with these words is not a refusal, so the whole
    ///   first line must match the marker's shape — a bare prefix test let
    ///   arbitrary text after it qualify.
    /// - It must cover every no-start form the server emits, not the two that
    ///   were convenient: `status=not_started` from the supervisor and
    ///   `state=not_found` from the output store were both missed.
    ///
    /// This path covers only <c>ptk_output</c>, which still returns a bare
    /// string — enforced by tool name above, since the four session tools
    /// carry a <see cref="Sessions.ToolOutcome"/> verdict and their
    /// completed responses intentionally have no structured marker to
    /// recognize them by (opr-53, amended by o53-3). <c>ptk_output</c>
    /// never renders worker output as its own status line, so text matching
    /// there cannot be fooled by a script the way the invoke channel could;
    /// every marker below is still pinned by a test.
    /// </summary>
    private static bool IsRefusalText(string text)
    {
        var newline = text.AsSpan().IndexOfAny('\r', '\n');
        var first = (newline >= 0 ? text.AsSpan(0, newline) : text.AsSpan()).Trim();

        if (first.StartsWith("[operation not started]", StringComparison.Ordinal))
            return true;
        if (!first.StartsWith("[ptk ", StringComparison.Ordinal))
            return false;

        var close = first.IndexOf(']');
        if (close < 0) return false;
        var rest = first[(close + 1)..].TrimStart();

        // Nothing ran: an explicit refusal, or a malformed request.
        return rest.StartsWith("refused ", StringComparison.Ordinal)
            || rest.StartsWith("invalid request", StringComparison.Ordinal)
            // "[ptk invoke] status=not_started ..." — proved not started.
            || rest.StartsWith("status=not_started", StringComparison.Ordinal)
            // "[ptk output] action=read state=not_found ..." — no such handle.
            || rest.Contains("state=not_found", StringComparison.Ordinal);
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
