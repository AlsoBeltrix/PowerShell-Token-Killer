using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class ResetTool
{
    [McpServerTool(Name = "ptk_reset")]
    [Description(
        "Replace one idle session worker with a fresh contained worker and factory " +
        "runspace. This discards only that session's variables, modules, functions, " +
        "directory, environment drift, and live connections. It never resets another " +
        "session and refuses while the selected session is busy or old containment is " +
        "unconfirmed.")]
    public static async Task<CallToolResult> Reset(
        ISessionOperations runtime,
        [Description(
            "Connection-local session to reset. Unknown or closed names never fall " +
            "back to default.")]
        [RegularExpression("^[a-z0-9][a-z0-9._-]{0,63}$")]
        [MaxLength(64)]
        string session = NamedSessionSupervisor.DefaultName,
        CancellationToken cancellationToken = default)
        => (await runtime.ResetAsync(session, cancellationToken)
            .ConfigureAwait(false)).ToCallToolResult();
}
