using System.Reflection;

namespace PtkSiemReceiver;

/// <summary>
/// Exact receiver build identity shared by logs and operator diagnostics.
/// Release packaging stamps the informational version from the matching
/// BUILD-PROVENANCE.json record.
/// </summary>
internal static class PtkSiemVersion
{
    internal static string Value { get; } = Resolve();

    private static string Resolve()
    {
        try
        {
            var assembly = typeof(PtkSiemVersion).Assembly;
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
