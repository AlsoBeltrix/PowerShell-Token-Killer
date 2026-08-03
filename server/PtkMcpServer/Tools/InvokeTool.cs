using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Server;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class InvokeTool
{
    [McpServerTool(Name = "ptk_invoke")]
    [Description(
        "Run PowerShell 7 work once in one isolated warm session. The dialect is " +
        "PowerShell, not bash: translate bash-only syntax, or invoke bash explicitly " +
        "as an ordinary native command (bash -lc '...') when you genuinely need it. " +
        "Everything executes in that session's persistent runspace; rtk rewrites the " +
        "native commands it recognizes so their output is filtered at the source. " +
        "Output is token-compressed while " +
        "preserving errors, exit codes, and structure. Variables, imported modules, " +
        "functions, directory, environment drift, and established connections persist " +
        "only in the selected session. Non-default sessions must first be opened with " +
        "ptk_session. Calls serialize within one session; different sessions may run " +
        "concurrently. When a ptk_output handle is returned, read that immutable " +
        "same-invocation snapshot instead of rerunning the command. The legacy raw flag " +
        "does not change routing, capture, or shaping. timeoutSeconds is one total " +
        "wall-clock budget including same-session queue wait; an execution overrun " +
        "replaces only that worker and loses only that session's warm state.")]
    public static Task<string> Invoke(
        ISessionOperations runtime,
        [Description("The command to execute: a PowerShell script or a native command line (git, npm, ...).")] string script,
        CancellationToken cancellationToken,
        [Description(
            "Deprecated compatibility flag: true has no effect on " +
            "routing, process choice, capture, or shaping. Use ptk_output " +
            "when a handle is returned.")]
        bool raw = false,
        [Description(
            "Routing override: 'auto' (default) offers the script to rtk, which " +
            "rewrites the native commands it recognizes and declines the rest; " +
            "'pwsh' skips rtk and runs the exact original text as PowerShell; " +
            "'rtk' asserts the rtk route but cannot override rtk's own decision. " +
            "A declined script executes the exact original once and returns a " +
            "labeled effective route without asking for a retry.")]
        string route = "auto",
        [Description(
            "Per-call timeout override in seconds, capped by the server maximum. A " +
            "total wall-clock budget: queue wait behind another call counts against " +
            "it, and a call whose budget expires while still queued fails fast " +
            "without executing. Raise it for long work that needs warm session state.")]
        int timeoutSeconds = 0,
        [Description(
            "Connection-local warm session name. 'default' is lazy and always exists; " +
            "a non-default name must be explicitly opened with ptk_session.")]
        [RegularExpression("^[a-z0-9][a-z0-9._-]{0,63}$")]
        [MaxLength(64)]
        string session = NamedSessionSupervisor.DefaultName,
        OutputStore? outputStore = null)
        => runtime.InvokeAsync(
            script,
            cancellationToken,
            raw,
            route,
            timeoutSeconds,
            session,
            outputStore);
}
