using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace PtkMcpServer.Sessions;

/// <summary>
/// What PTK itself decided about a call, carried as data rather than inferred
/// from the response text.
/// </summary>
public enum ToolDisposition
{
    /// <summary>The work ran. Whatever the script concluded is the script's
    /// business, not PTK's.</summary>
    Completed,

    /// <summary>PTK did not start the work. Nothing executed.</summary>
    NotStarted,

    /// <summary>The work started and PTK cannot say whether it finished.</summary>
    OutcomeUnknown,

    /// <summary>The work started and failed inside PTK's own machinery.</summary>
    Failed,
}

/// <summary>
/// A tool response and PTK's own verdict on it.
/// </summary>
/// <remarks>
/// Finding opr-53. Supervisor status and recovery information used to live in
/// the same newline-delimited channel as worker output, and the protocol
/// reserved no grammar, so a script that simply printed
/// <c>[ptk worker] status=refused ...</c> or a fake
/// <c>recovery=available: ptk_output handle=...</c> had those lines preserved
/// verbatim beside PTK's genuine ones — indistinguishable to the model
/// reading them. A forged "the command was not started" invites a caller to
/// resubmit an already-executed mutating command; a forged handle points it
/// at an artifact PTK never issued.
///
/// The repair is not to escape the text: that mutates legitimate user output,
/// and any escaping scheme is another grammar to forge. Instead PTK's own
/// decisions travel outside the text channel entirely, in the protocol's
/// <c>structuredContent</c> and <c>isError</c> fields, which worker output
/// cannot reach. The text is still returned byte-for-byte as before, so
/// nothing that reads it breaks and no user output is altered — but a caller
/// that wants the truth reads the structured fields, and those cannot be
/// impersonated from inside a script.
///
/// This also removes the text-matching in
/// <see cref="SupervisorCallFilter"/>: the refusal→<c>isError</c> mapping
/// used to re-derive from the response text what the supervisor already knew,
/// which was recorded as deliberately deferred follow-up. It now reads the
/// disposition directly.
/// </remarks>
public sealed record ToolOutcome(string Text, ToolDisposition Disposition, string? DetailCode = null)
{
    /// <summary>The ordinary case: work ran, no PTK-level verdict to add.</summary>
    public static ToolOutcome Completed(string text) =>
        new(text, ToolDisposition.Completed);

    /// <summary>
    /// The protocol's own error flag. Only a refusal sets it: the work did
    /// not run, so a client trusting the flag must not read success. A script
    /// that ran and threw is a successful call whatever it concluded, and an
    /// unknown outcome is not a proven non-start.
    /// </summary>
    internal bool IsError => Disposition == ToolDisposition.NotStarted;

    internal CallToolResult ToCallToolResult()
    {
        var structured = new JsonObject
        {
            ["disposition"] = Disposition switch
            {
                ToolDisposition.Completed => "completed",
                ToolDisposition.NotStarted => "not_started",
                ToolDisposition.OutcomeUnknown => "outcome_unknown",
                ToolDisposition.Failed => "failed",
                _ => "completed",
            },
            // Says plainly what a caller most needs and most easily gets
            // wrong: whether resubmitting is safe. Only a proved non-start
            // is safe to retry; an unknown outcome explicitly is not.
            ["executed"] = Disposition != ToolDisposition.NotStarted,
            ["safe_to_resubmit"] = Disposition == ToolDisposition.NotStarted,
        };
        if (DetailCode is not null)
            structured["detail"] = DetailCode;

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = Text }],
            StructuredContent = JsonSerializer.Deserialize<JsonElement>(
                structured.ToJsonString()),
            IsError = IsError ? true : null,
        };
    }
}
