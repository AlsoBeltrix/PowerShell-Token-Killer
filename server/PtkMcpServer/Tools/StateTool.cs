using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class StateTool
{
    [McpServerTool(Name = "ptk_state")]
    [Description(
        "Report supervisor health and the selected warm session's lifecycle, worker " +
        "PID, engine, current directory, loaded modules, and drift. This never starts " +
        "a cold session. While that session is busy it returns prompt supervisor-local " +
        "facts and marks runspace details unavailable instead of queueing. Set " +
        "listAvailable to enumerate installed modules when the selected worker is idle.")]
    public static async Task<CallToolResult> State(
        ISessionOperations runtime,
        [Description("Also enumerate every installed module instead of only loaded ones.")]
        bool listAvailable = false,
        [Description(
            "Connection-local session to inspect. Unknown or closed names never fall " +
            "back to default.")]
        [RegularExpression("^[a-z0-9][a-z0-9._-]{0,63}$")]
        [MaxLength(64)]
        string session = NamedSessionSupervisor.DefaultName,
        CancellationToken cancellationToken = default,
        // SDK-injected when the client supplied a progressToken; never part
        // of the tool's argument schema (#44).
        IProgress<ProgressNotificationValue>? progress = null)
        => (await ToolHeartbeat.KeepAliveAsync(
            runtime.StateAsync(
                listAvailable,
                session,
                cancellationToken),
            progress ?? ToolHeartbeat.NoProgress.Instance)
            .ConfigureAwait(false)).ToCallToolResult();
}
