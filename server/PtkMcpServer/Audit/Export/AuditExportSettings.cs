using System.Text.Json;
using System.Text.Json.Serialization;

namespace PtkMcpServer.Audit.Export;

/// <summary>
/// Where this host's audit records are delivered. One endpoint plus one
/// credential, identical in shape for every destination (owner ruling
/// 2026-08-10: the PTK receiver "acts exactly like a real SIEM, not require
/// its own machinery"), so no destination carries bespoke enrollment.
/// </summary>
internal enum AuditDestinationKind
{
    /// <summary>No destination configured: PTK still journals locally.</summary>
    None,

    /// <summary>Splunk HTTP Event Collector.</summary>
    SplunkHec,

    /// <summary>Any OTLP/HTTP JSON logs endpoint — collectors, and the PTK
    /// fallback receiver, which is reached the same way.</summary>
    OtlpHttp,
}

internal sealed record AuditExportSettings(
    AuditDestinationKind Kind,
    Uri? Endpoint,
    string? Credential)
{
    internal const string FileName = "export.json";
    internal const string KindEnvironmentVariable = "PTK_AUDIT_EXPORT_KIND";
    internal const string EndpointEnvironmentVariable = "PTK_AUDIT_EXPORT_ENDPOINT";
    internal const string CredentialEnvironmentVariable = "PTK_AUDIT_EXPORT_TOKEN";
    private const int MaximumCredentialLength = 4096;
    private const int MaximumEndpointLength = 2048;
    private const int MaximumFileBytes = 64 * 1024;

    internal static AuditExportSettings Disabled { get; } =
        new(AuditDestinationKind.None, null, null);

    internal bool IsConfigured => Kind != AuditDestinationKind.None && Endpoint is not null;

    /// <summary>
    /// Reads the settings file under the audit root, then applies environment
    /// overrides. An unreadable or invalid configuration NEVER blocks startup:
    /// export is additive and must not gate execution (contract rule 2), so a
    /// bad configuration disables delivery and reports why.
    /// </summary>
    internal static AuditExportSettings Load(
        string auditRootDirectory,
        out string? configurationFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditRootDirectory);
        configurationFailure = null;

        AuditExportSettings settings = Disabled;
        var path = Path.Combine(auditRootDirectory, FileName);
        try
        {
            if (File.Exists(path))
            {
                var bytes = SecureAuditStorage.ReadProtectedFile(
                    path,
                    MaximumFileBytes,
                    requireProtectedParent: false,
                    verifyWithoutMutation: true);
                var file = JsonSerializer.Deserialize<AuditExportSettingsFile>(bytes);
                if (file is not null)
                {
                    settings = new AuditExportSettings(
                        ParseKind(file.Kind),
                        ParseEndpoint(file.Endpoint),
                        Trim(file.Credential));
                }
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            configurationFailure = "export.configuration_unreadable";
            settings = Disabled;
        }

        var environmentKind = Environment.GetEnvironmentVariable(KindEnvironmentVariable);
        var environmentEndpoint = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
        var environmentCredential = Environment.GetEnvironmentVariable(CredentialEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentKind))
            settings = settings with { Kind = ParseKind(environmentKind) };
        if (!string.IsNullOrWhiteSpace(environmentEndpoint))
            settings = settings with { Endpoint = ParseEndpoint(environmentEndpoint) };
        if (!string.IsNullOrWhiteSpace(environmentCredential))
            settings = settings with { Credential = Trim(environmentCredential) };

        if (settings.Kind != AuditDestinationKind.None && settings.Endpoint is null)
        {
            configurationFailure ??= "export.endpoint_invalid";
            return Disabled;
        }
        return settings;
    }

    /// <summary>
    /// Plaintext credentials never reach the journal, logs, or ptk_state. A
    /// destination is described by kind and endpoint only.
    /// </summary>
    internal string Describe() => Kind switch
    {
        AuditDestinationKind.None => "none",
        _ => $"{KindText(Kind)} {Endpoint?.GetLeftPart(UriPartial.Path) ?? "unset"}",
    };

    internal static string KindText(AuditDestinationKind kind) => kind switch
    {
        AuditDestinationKind.SplunkHec => "splunk_hec",
        AuditDestinationKind.OtlpHttp => "otlp_http",
        _ => "none",
    };

    private static AuditDestinationKind ParseKind(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "splunk_hec" or "splunk" => AuditDestinationKind.SplunkHec,
            "otlp_http" or "otlp" or "ptk_receiver" => AuditDestinationKind.OtlpHttp,
            _ => AuditDestinationKind.None,
        };

    /// <summary>
    /// Plaintext HTTP is accepted only for loopback — the zero-config local
    /// receiver — so a remote SIEM can never be configured without TLS.
    /// </summary>
    private static Uri? ParseEndpoint(string? value)
    {
        var text = Trim(value);
        if (text is null || text.Length > MaximumEndpointLength) return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme == Uri.UriSchemeHttps) return uri;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return uri;
        return null;
    }

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > MaximumCredentialLength ? null : trimmed;
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed class AuditExportSettingsFile
    {
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
        [JsonPropertyName("credential")] public string? Credential { get; set; }
    }
}
