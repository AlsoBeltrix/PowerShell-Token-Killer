using System.Reflection;

namespace PtkMcpServer;

/// <summary>
/// The running build's own version, for `ptk_state`.
///
/// Every platform test report (GitHub #33-#36) hit the same wall: `ptk_state`
/// reported the PowerShell engine version and nothing about ptk, and on Unix
/// the binary's file-version fields are blank, so a tester could not say which
/// build they had exercised. A bug report against an unidentifiable build is
/// hard to act on.
///
/// Reads <c>AssemblyInformationalVersionAttribute</c>, which the build stamps
/// as <c>&lt;version&gt;+&lt;short-sha&gt;.build.&lt;build-identity&gt;</c>, so the value ties
/// a running process back to one exact build of its source.
/// </summary>
internal static class PtkVersion
{
    /// <summary>
    /// Exact build identity for display, e.g.
    /// <c>0.2.0+a1b2c3d.build.0123456789abcdef0123456789abcdef</c>. Falls back to the
    /// assembly version, then to <c>unknown</c> — never throws and never
    /// returns empty, because a diagnostic surface that can fail is worse than
    /// one that says it does not know.
    /// </summary>
    internal static string Value { get; } = Resolve();

    private static string Resolve()
    {
        try
        {
            var assembly = typeof(PtkVersion).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return informational;

            var version = assembly.GetName().Version?.ToString();
            return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
        }
        catch
        {
            return "unknown";
        }
    }
}
