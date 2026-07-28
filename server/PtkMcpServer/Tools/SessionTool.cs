using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
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
    public static Task<string> Session(
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
        CancellationToken cancellationToken = default)
        => runtime.SessionAsync(action, name, cancellationToken);
}
