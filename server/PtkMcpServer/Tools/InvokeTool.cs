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
        "Run shell work once in one isolated warm PowerShell session. PowerShell, " +
        "mixed-dataflow, and most native commands use that session's persistent " +
        "runspace; eligible terminal native commands route internally through rtk, " +
        "and independently proven parse-fatal Bash syntax may use startup-pinned " +
        "Bash/RTK processes owned by the same worker. Output is token-compressed while " +
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
            "Deprecated compatibility flag: true has no effect on dialect handling, " +
            "interpreter/routing, process choice, capture, or shaping. Use ptk_output " +
            "when a handle is returned.")]
        bool raw = false,
        [Description(
            "Routing override: 'auto' (default) runs a single native command " +
            "through rtk's filters; 'pwsh' is explicit consent to interpret the exact " +
            "original text as PowerShell and bypass automatic dialect/Bash/RTK routing; " +
            "normal capture and shaping still apply; 'rtk' asserts RTK only for an " +
            "eligible terminal native application. An ineligible assertion executes the exact original " +
            "once and returns a labeled effective route without asking for a retry.")]
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
