namespace PtkMcpServer;

/// <summary>
/// RTK is a required dependency, not an optional enhancement (owner,
/// 2026-08-03). PTK is a compression router: it compresses PowerShell objects
/// itself and routes everything else to RTK. A server that cannot find RTK
/// cannot do half its job, so it refuses to start rather than degrading
/// silently into an unfiltered passthrough nobody asked for.
/// </summary>
internal static class RtkDependency
{
    internal const string EnvironmentVariable = "PTK_RTK_PATH";

    /// <summary>
    /// Resolves RTK the same way the runspace host does at startup: an explicit
    /// <c>PTK_RTK_PATH</c> wins, otherwise the first <c>rtk</c> on PATH.
    /// Returns null when no usable executable exists.
    ///
    /// Usability is decided by <see cref="RtkExecutableIdentity.TryCapture"/> —
    /// the same capture the runtime uses to pin and hash the binary. A weaker
    /// check here (mere existence) would let the server start on a path the
    /// runtime then fails to capture, producing exactly the silent degradation
    /// the startup gate exists to prevent: RTK is required, so a file that
    /// cannot be pinned is not an RTK.
    /// </summary>
    internal static string? ResolveExecutablePath()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return RtkExecutableIdentity.TryCapture(configured)?.ExecutablePath;
        }

        var searchPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(searchPath))
            return null;

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "rtk.exe", "rtk.cmd", "rtk.bat", "rtk" }
            : ["rtk"];

        foreach (var rawDirectory in searchPath.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                string full;
                try
                {
                    full = Path.Combine(rawDirectory.Trim('"'), candidate);
                }
                catch
                {
                    continue;
                }

                if (RtkExecutableIdentity.TryCapture(full) is { } identity)
                    return identity.ExecutablePath;
            }
        }

        return null;
    }

    /// <summary>
    /// The message a user sees when startup refuses. It names the requirement,
    /// both resolution routes, and where to get RTK — enough to fix the problem
    /// without reading the source.
    /// </summary>
    internal static string UnavailableMessage() =>
        "PTK requires RTK (the Rust Token Killer) and could not find it. " +
        $"Set {EnvironmentVariable} to the rtk executable, or put rtk on PATH. " +
        "PTK routes native-command output through RTK's filters; without it " +
        "PTK cannot compress that output. See https://github.com/rtk-ai/rtk.";
}
