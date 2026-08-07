using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class SessionTool
{
    [McpServerTool(Name = "ptk_session")]
    [Description(
        "Manage this MCP connection's isolated warm PowerShell sessions. list is " +
        "supervisor-local and starts no worker. open explicitly creates one non-default " +
        "session and waits for its contained worker to become ready; opening an already " +
        "ready name is idempotent. close removes one idle non-default session after its " +
        "worker containment is proved empty. The lazy default session exists for the " +
        "connection lifetime and cannot be closed. At most eight sessions, including " +
        "default, may be open.")]
    public static async Task<CallToolResult> Session(
        ISessionOperations runtime,
        [Description("list | open | close")]
        [AllowedValues("list", "open", "close")]
        string action,
        [Description(
            "Canonical lowercase connection-local name required for open and close; " +
            "omit for list.")]
        [RegularExpression("^[a-z0-9][a-z0-9._-]{0,63}$")]
        [MaxLength(64)]
        string? name = null,
        CancellationToken cancellationToken = default,
        // SDK-injected when the client supplied a progressToken; never part
        // of the tool's argument schema (#44).
        IProgress<ProgressNotificationValue>? progress = null)
        => (await ToolHeartbeat.KeepAliveAsync(
            runtime.SessionAsync(action, name, cancellationToken),
            progress ?? ToolHeartbeat.NoProgress.Instance)
            .ConfigureAwait(false)).ToCallToolResult();
}
