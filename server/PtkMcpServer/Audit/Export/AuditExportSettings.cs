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
    string? Credential,
    // Optional operator alert webhook (R4, reporting surface (c)); same
    // https-or-loopback rule as the endpoint.
    Uri? AlertWebhook = null,
    // Optional exact leaf-certificate SHA-256 pin. This is destination-local
    // trust and never mutates an OS trust store.
    string? ServerCertificateSha256 = null)
{
    internal const string FileName = "export.json";
    internal const string KindEnvironmentVariable = "PTK_AUDIT_EXPORT_KIND";
    internal const string EndpointEnvironmentVariable = "PTK_AUDIT_EXPORT_ENDPOINT";
    internal const string CredentialEnvironmentVariable = "PTK_AUDIT_EXPORT_TOKEN";
    internal const string AlertWebhookEnvironmentVariable = "PTK_AUDIT_ALERT_WEBHOOK";
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
                        Trim(file.Credential),
                        ParseEndpoint(file.AlertWebhook));
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
        var environmentWebhook = Environment.GetEnvironmentVariable(AlertWebhookEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentWebhook))
            settings = settings with { AlertWebhook = ParseEndpoint(environmentWebhook) };
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

    internal static AuditDestinationKind ParseKind(string? value) =>
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
    internal static Uri? ParseEndpoint(string? value)
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

    /// <summary>
    /// Validates a settings-page write (audit-restoration R4) with the exact
    /// rules the loader applies, so the UI can never persist a configuration
    /// the next start would refuse: an unknown kind or — for a configured
    /// destination — an endpoint that is neither HTTPS nor loopback HTTP.
    /// </summary>
    internal static bool TryValidateForWrite(
        string? kind,
        string? endpoint,
        out string failure)
    {
        failure = string.Empty;
        var trimmedKind = kind?.Trim().ToLowerInvariant();
        var parsedKind = ParseKind(kind);
        if (parsedKind == AuditDestinationKind.None &&
            trimmedKind is not (null or "" or "none"))
        {
            failure = "invalid_kind";
            return false;
        }
        if (parsedKind != AuditDestinationKind.None && ParseEndpoint(endpoint) is null)
        {
            failure = "invalid_endpoint";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Atomically writes the owner-only settings file. A null credential
    /// preserves the one already on disk, so the settings page can change
    /// the endpoint without re-entering (or ever reading back) the secret.
    /// </summary>
    internal static bool TryWrite(
        string auditRootDirectory,
        string? kind,
        string? endpoint,
        string? credential)
    {
        var path = Path.Combine(auditRootDirectory, FileName);
        var temporaryPath = Path.Combine(
            auditRootDirectory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            string? alertWebhook = null;
            if (File.Exists(path))
            {
                try
                {
                    var bytes = SecureAuditStorage.ReadProtectedFile(
                        path,
                        MaximumFileBytes,
                        requireProtectedParent: false,
                        verifyWithoutMutation: true);
                    var prior = JsonSerializer.Deserialize<AuditExportSettingsFile>(bytes);
                    credential ??= prior?.Credential;
                    alertWebhook = prior?.AlertWebhook;
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    // An unreadable prior file preserves nothing.
                }
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(new AuditExportSettingsFile
            {
                Kind = KindText(ParseKind(kind)),
                Endpoint = Trim(endpoint),
                Credential = Trim(credential),
                AlertWebhook = Trim(alertWebhook),
            });
            using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            SecureAuditStorage.TryDelete(temporaryPath);
            return false;
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed class AuditExportSettingsFile
    {
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
        [JsonPropertyName("credential")] public string? Credential { get; set; }
        [JsonPropertyName("alert_webhook")] public string? AlertWebhook { get; set; }
    }
}
