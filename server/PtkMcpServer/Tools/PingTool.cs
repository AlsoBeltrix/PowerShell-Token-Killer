using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class PingTool
{
    [McpServerTool(Name = "ptk_ping")]
    [Description("Health check for the ptk warm-runspace server. Returns 'pong'.")]
    public static string Ping() => "pong";
}
