using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PtkSiemReceiver.Security;

namespace PtkSiemReceiver.Configuration;

internal sealed class SiemReceiverConfigurationException : Exception
{
    internal SiemReceiverConfigurationException(string failureCode)
        : base($"siem_receiver_configuration_invalid: {failureCode}")
    {
        FailureCode = failureCode;
    }

    internal string FailureCode { get; }
}

/// <summary>
/// The frozen-at-startup receiver configuration. Loaded once from the file
/// named by <c>PTK_SIEM_CONFIG</c>; never reloaded. Modeled on the producer's
/// <c>AuditExportOptions</c> (`server/PtkMcpServer/Audit/AuditExportConfiguration.cs`).
/// </summary>
internal sealed class SiemReceiverOptions
{
    internal SiemReceiverOptions(
        IPAddress ingestBindAddress,
        int ingestPort,
        string serverCertificatePath,
        string serverCertificateKeyPath,
        IReadOnlyList<string> clientCaBundlePaths,
        X509RevocationMode revocationCheckMode,
        int maxRequestBytes,
        int maxConcurrentRequests,
        IPAddress operatorBindAddress,
        int operatorPort,
        string operatorToken,
        string? operatorHttpsCertificatePath,
        string? operatorHttpsCertificateKeyPath,
        string sqlitePath,
        int? retentionMaxAgeDays,
        long? retentionMaxTotalBytes,
        string? configurationPath = null,
        ProtectedPathIdentity? configurationIdentity = null,
        string? ingestToken = null,
        IReadOnlyList<AlertRule>? alertRules = null,
        string? alertWebhookUrl = null)
    {
        IngestBindAddress = ingestBindAddress;
        IngestPort = ingestPort;
        ServerCertificatePath = serverCertificatePath;
        ServerCertificateKeyPath = serverCertificateKeyPath;
        ClientCaBundlePaths = clientCaBundlePaths;
        RevocationCheckMode = revocationCheckMode;
        MaxRequestBytes = maxRequestBytes;
        MaxConcurrentRequests = maxConcurrentRequests;
        OperatorBindAddress = operatorBindAddress;
        OperatorPort = operatorPort;
        OperatorToken = operatorToken;
        OperatorHttpsCertificatePath = operatorHttpsCertificatePath;
        OperatorHttpsCertificateKeyPath = operatorHttpsCertificateKeyPath;
        SqlitePath = sqlitePath;
        RetentionMaxAgeDays = retentionMaxAgeDays;
        RetentionMaxTotalBytes = retentionMaxTotalBytes;
        ConfigurationPath = configurationPath;
        ConfigurationIdentity = configurationIdentity;
        IngestToken = ingestToken;
        AlertRules = alertRules ?? [];
        AlertWebhookUrl = alertWebhookUrl;
        AlertRuleConfigHash = AlertRules.Count > 0
            ? AlertRuleSet.ComputeConfigHash(AlertRules)
            : null;
    }

    internal IPAddress IngestBindAddress { get; }

    internal int IngestPort { get; }

    internal string ServerCertificatePath { get; }

    internal string ServerCertificateKeyPath { get; }

    /// <summary>PEM CA bundle paths; multiple bundles so old+new CAs can overlap during rotation.</summary>
    internal IReadOnlyList<string> ClientCaBundlePaths { get; }

    /// <summary>Explicit in config; there is no silent fallback mode.</summary>
    internal X509RevocationMode RevocationCheckMode { get; }

    internal int MaxRequestBytes { get; }

    /// <summary>
    /// Global ingest admission cap (rbc-12): requests beyond this many
    /// concurrently in flight receive a retryable 503 instead of queuing
    /// without bound behind storage.
    /// </summary>
    internal int MaxConcurrentRequests { get; }

    internal IPAddress OperatorBindAddress { get; }

    internal int OperatorPort { get; }

    internal string OperatorToken { get; }

    internal string? OperatorHttpsCertificatePath { get; }

    internal string? OperatorHttpsCertificateKeyPath { get; }

    internal string SqlitePath { get; }

    internal int? RetentionMaxAgeDays { get; }

    internal long? RetentionMaxTotalBytes { get; }

    internal string? ConfigurationPath { get; }

    internal ProtectedPathIdentity? ConfigurationIdentity { get; }

    /// <summary>
    /// Optional ingest bearer token (audit-restoration R3c). When set, a
    /// client without an mTLS certificate may authenticate with
    /// <c>Authorization: Bearer</c> — the mode PTK's own exporter uses, since
    /// its one-endpoint-plus-token contract is identical for Splunk,
    /// Sentinel, any OTLP collector, and this receiver (owner ruling
    /// 2026-08-10, no pairing machinery). When null, the ingest port
    /// requires a client certificate exactly as before.
    /// </summary>
    internal string? IngestToken { get; }

    /// <summary>Declarative alert rules (mini-SIEM S6), frozen at startup.
    /// Empty means alerting is off and ingest enqueues no work items.</summary>
    internal IReadOnlyList<AlertRule> AlertRules { get; }

    internal string? AlertWebhookUrl { get; }

    /// <summary>SHA-256 over the canonical rule text; stamped on every work
    /// item at enqueue and on every alert at evaluation, so a rule change
    /// across a crash is evident, never silent.</summary>
    internal string? AlertRuleConfigHash { get; }

    // Never include the operator or ingest token (or anything derived from
    // them) here.
    public override string ToString() => "siem receiver configuration";
}

/// <summary>One declarative alert rule. <c>event_match</c> carries an event
/// type; <c>ingest_rate</c> carries a threshold over a window;
/// <c>chain_break</c> and <c>gap_detected</c> carry nothing further.</summary>
internal sealed record AlertRule(
    string Name,
    string Type,
    string? EventType,
    int? Threshold,
    int? WindowSeconds);

internal static class AlertRuleSet
{
    internal static string ComputeConfigHash(IReadOnlyList<AlertRule> rules)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var rule in rules)
        {
            builder.Append(rule.Name).Append('\n')
                .Append(rule.Type).Append('\n')
                .Append(rule.EventType ?? string.Empty).Append('\n')
                .Append(rule.Threshold?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
                .Append('\n')
                .Append(rule.WindowSeconds?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
                .Append('\n');
        }

        return Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }
}

/// <summary>
/// Strict JSON configuration loader, modeled on the producer's
/// <c>AuditExportConfigurationLoader</c>: strict UTF-8 (no BOM), size-capped,
/// unknown properties rejected everywhere, actionable single-code failures,
/// and no fallback defaults for security-relevant fields.
///
/// The configuration itself crosses the protected-path boundary before any
/// token or referenced path is parsed. Referenced assets cross the same
/// boundary in receiver startup before they are consumed.
/// </summary>
internal static class SiemReceiverConfigurationLoader
{
    internal const int MaximumConfigurationBytes = 256 * 1024;
    internal const int DefaultMaxRequestBytes = 1024 * 1024;
    internal const int DefaultMaxConcurrentRequests = 64;

    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "ingest",
        "operator",
        "storage",
        "alerts",
    };

    private static readonly HashSet<string> AlertsProperties = new(StringComparer.Ordinal)
    {
        "rules",
        "webhookUrl",
    };

    private static readonly HashSet<string> AlertRuleProperties = new(StringComparer.Ordinal)
    {
        "name",
        "type",
        "eventType",
        "threshold",
        "windowSeconds",
    };

    private static readonly HashSet<string> AlertRuleTypes = new(StringComparer.Ordinal)
    {
        "event_match",
        "chain_break",
        "gap_detected",
        "ingest_rate",
    };

    private static readonly HashSet<string> IngestProperties = new(StringComparer.Ordinal)
    {
        "bindAddress",
        "port",
        "serverCertificatePath",
        "serverCertificateKeyPath",
        "clientCaBundlePaths",
        "revocationCheckMode",
        "maxRequestBytes",
        "maxConcurrentRequests",
        "token",
    };

    /// <summary>A short ingest token is a footgun, not a configuration: its
    /// SHA-256 appears in custody receipts, so it must be high-entropy.</summary>
    internal const int MinimumIngestTokenLength = 16;

    private static readonly HashSet<string> OperatorProperties = new(StringComparer.Ordinal)
    {
        "bindAddress",
        "port",
        "token",
        "httpsCertificatePath",
        "httpsCertificateKeyPath",
    };

    private static readonly HashSet<string> StorageProperties = new(StringComparer.Ordinal)
    {
        "sqlitePath",
        "retention",
    };

    private static readonly HashSet<string> RetentionProperties = new(StringComparer.Ordinal)
    {
        "maxAgeDays",
        "maxTotalBytes",
    };

    internal static SiemReceiverOptions Load(
        string configurationPath,
        ProtectedPathTestHooks? protectedPathTestHooks = null)
    {
        byte[]? bytes = null;
        ProtectedPathIdentity? configurationIdentity = null;
        try
        {
            if (string.IsNullOrWhiteSpace(configurationPath) ||
                !Path.IsPathFullyQualified(configurationPath))
            {
                Fail("config_path");
            }

            try
            {
                var protectedRead = SiemProtectedPath.ReadExternalFileWithIdentity(
                    SiemProtectedPath.NormalizeAbsolute(configurationPath),
                    MaximumConfigurationBytes,
                    protectedPathTestHooks);
                bytes = protectedRead.Bytes;
                configurationIdentity = protectedRead.Identity;
            }
            catch (ProtectedPathException exception)
            {
                throw new SiemReceiverConfigurationException(
                    exception.FailureKind switch
                    {
                        ProtectedPathFailureKind.TooLarge => "config_bytes",
                        ProtectedPathFailureKind.Missing => "config_read",
                        ProtectedPathFailureKind.InvalidPath => "config_path",
                        _ => "config_protection",
                    });
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new SiemReceiverConfigurationException("config_read");
            }

            if (bytes.Length == 0 || HasUtf8Bom(bytes))
                Fail("config_json");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(bytes.AsMemory(), new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
            }
            catch (JsonException)
            {
                throw new SiemReceiverConfigurationException("config_json");
            }

            using (document)
            {
                return Validate(
                    document.RootElement,
                    SiemProtectedPath.NormalizeAbsolute(configurationPath),
                    configurationIdentity.Value);
            }
        }
        catch (SiemReceiverConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new SiemReceiverConfigurationException("load_failed");
        }
        finally
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static SiemReceiverOptions Validate(
        JsonElement root,
        string configurationPath,
        ProtectedPathIdentity configurationIdentity)
    {
        if (root.ValueKind != JsonValueKind.Object)
            Fail("config_root");
        RejectUnknownProperties(root, RootProperties);

        var ingest = RequiredObject(root, "ingest", "ingest_section");
        RejectUnknownProperties(ingest, IngestProperties);
        var operatorSection = RequiredObject(root, "operator", "operator_section");
        RejectUnknownProperties(operatorSection, OperatorProperties);
        var storage = RequiredObject(root, "storage", "storage_section");
        RejectUnknownProperties(storage, StorageProperties);

        var ingestBindAddress = ParseAddress(
            RequiredString(ingest, "bindAddress", "ingest_bind_address"),
            "ingest_bind_address");
        var ingestPort = ParsePort(ingest, "port", "ingest_port");
        var serverCertificatePath = RequiredAbsolutePath(
            ingest, "serverCertificatePath", "server_certificate_path");
        var serverCertificateKeyPath = RequiredAbsolutePath(
            ingest, "serverCertificateKeyPath", "server_certificate_key_path");
        var clientCaBundlePaths = ParseCaBundlePaths(ingest);
        var revocationCheckMode = ParseRevocationMode(ingest);
        var maxRequestBytes = ParseOptionalPositiveInt32(
            ingest, "maxRequestBytes", "max_request_bytes") ?? DefaultMaxRequestBytes;
        var maxConcurrentRequests = ParseOptionalPositiveInt32(
            ingest, "maxConcurrentRequests", "max_concurrent_requests") ?? DefaultMaxConcurrentRequests;
        var ingestToken = OptionalString(ingest, "token", "ingest_token");
        if (ingestToken is not null && ingestToken.Length < MinimumIngestTokenLength)
            Fail("ingest_token");

        var operatorBindAddress = OptionalString(
                operatorSection, "bindAddress", "operator_bind_address") is { } operatorBindText
            ? ParseAddress(operatorBindText, "operator_bind_address")
            : IPAddress.Loopback;
        var operatorPort = ParsePort(operatorSection, "port", "operator_port");
        var operatorToken = RequiredString(operatorSection, "token", "operator_token");
        var operatorHttpsCertificatePath = OptionalAbsolutePath(
            operatorSection, "httpsCertificatePath", "operator_https_pair");
        var operatorHttpsCertificateKeyPath = OptionalAbsolutePath(
            operatorSection, "httpsCertificateKeyPath", "operator_https_pair");

        if (operatorHttpsCertificatePath is null != operatorHttpsCertificateKeyPath is null)
            Fail("operator_https_pair");

        // The operator bearer token never travels plaintext off-host: a
        // non-loopback operator bind is a startup failure unless an operator
        // HTTPS certificate is configured.
        if (!IPAddress.IsLoopback(operatorBindAddress) && operatorHttpsCertificatePath is null)
            Fail("operator_https_required");

        // The operator endpoint is a separate port from ingest by design.
        if (operatorPort == ingestPort)
            Fail("operator_port_conflict");

        // The two surfaces' authorities must not be collapsible by
        // configuration: a shared secret would let the read-only operator
        // credential authenticate store-writing ingest (cr7-2).
        if (ingestToken is not null &&
            string.Equals(ingestToken, operatorToken, StringComparison.Ordinal))
            Fail("token_reuse");

        IReadOnlyList<AlertRule> alertRules = [];
        string? alertWebhookUrl = null;
        if (root.TryGetProperty("alerts", out var alerts))
        {
            if (alerts.ValueKind != JsonValueKind.Object)
                Fail("alerts_section");
            RejectUnknownProperties(alerts, AlertsProperties);
            alertRules = ParseAlertRules(alerts);
            alertWebhookUrl = OptionalString(alerts, "webhookUrl", "alert_webhook");
            if (alertWebhookUrl is not null)
                ValidateAlertWebhookUrl(alertWebhookUrl);
        }

        var sqlitePath = RequiredAbsolutePath(
            storage, "sqlitePath", "storage_sqlite_path");

        int? retentionMaxAgeDays = null;
        long? retentionMaxTotalBytes = null;
        if (storage.TryGetProperty("retention", out var retention))
        {
            if (retention.ValueKind != JsonValueKind.Object)
                Fail("retention_section");
            RejectUnknownProperties(retention, RetentionProperties);
            retentionMaxAgeDays = ParseOptionalPositiveInt32(
                retention, "maxAgeDays", "retention_max_age_days");
            retentionMaxTotalBytes = ParseOptionalPositiveInt64(
                retention, "maxTotalBytes", "retention_max_total_bytes");
            if (retentionMaxAgeDays is null && retentionMaxTotalBytes is null)
                Fail("retention_bounds");
        }

        return new SiemReceiverOptions(
            ingestBindAddress,
            ingestPort,
            serverCertificatePath,
            serverCertificateKeyPath,
            clientCaBundlePaths,
            revocationCheckMode,
            maxRequestBytes,
            maxConcurrentRequests,
            operatorBindAddress,
            operatorPort,
            operatorToken,
            operatorHttpsCertificatePath,
            operatorHttpsCertificateKeyPath,
            sqlitePath,
            retentionMaxAgeDays,
            retentionMaxTotalBytes,
            configurationPath,
            configurationIdentity,
            ingestToken,
            alertRules,
            alertWebhookUrl);
    }

    private static IReadOnlyList<AlertRule> ParseAlertRules(JsonElement alerts)
    {
        if (!alerts.TryGetProperty("rules", out var rules) ||
            rules.ValueKind != JsonValueKind.Array ||
            rules.GetArrayLength() == 0)
        {
            Fail("alert_rules");
        }

        var parsed = new List<AlertRule>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object)
                Fail("alert_rule");
            RejectUnknownProperties(rule, AlertRuleProperties);
            var name = RequiredString(rule, "name", "alert_rule");
            if (!names.Add(name)) Fail("alert_rule");
            var type = RequiredString(rule, "type", "alert_rule");
            if (!AlertRuleTypes.Contains(type)) Fail("alert_rule");

            var eventType = OptionalString(rule, "eventType", "alert_rule");
            var threshold = ParseOptionalPositiveInt32(rule, "threshold", "alert_rule");
            var windowSeconds = ParseOptionalPositiveInt32(rule, "windowSeconds", "alert_rule");

            // Each type's parameters are exact: a match rule names its event
            // type, a rate rule names its bound, nothing else carries either.
            var valid = type switch
            {
                "event_match" => eventType is not null && threshold is null && windowSeconds is null,
                "ingest_rate" => eventType is null && threshold is not null && windowSeconds is not null,
                _ => eventType is null && threshold is null && windowSeconds is null,
            };
            if (!valid) Fail("alert_rule");

            parsed.Add(new AlertRule(name, type, eventType, threshold, windowSeconds));
        }

        return parsed;
    }

    /// <summary>The producer's webhook rule: HTTPS anywhere, plain HTTP only
    /// to loopback — alert bodies name evidence subjects and must not travel
    /// plaintext off-host.</summary>
    private static void ValidateAlertWebhookUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps &&
             !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)))
        {
            Fail("alert_webhook");
        }
    }

    private static void RejectUnknownProperties(JsonElement element, HashSet<string> expected)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
                Fail("unknown_property");
        }
    }

    private static JsonElement RequiredObject(
        JsonElement parent, string propertyName, string failureCode)
    {
        if (!parent.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            Fail(failureCode);
        }

        return element;
    }

    private static string RequiredString(
        JsonElement parent, string propertyName, string failureCode)
    {
        var value = OptionalString(parent, propertyName, failureCode);
        if (value is null)
            Fail(failureCode);
        return value!;
    }

    private static string? OptionalString(
        JsonElement parent, string propertyName, string failureCode)
    {
        if (!parent.TryGetProperty(propertyName, out var element))
            return null;
        if (element.ValueKind != JsonValueKind.String)
            Fail(failureCode);
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
            Fail(failureCode);
        return value;
    }

    private static string RequiredAbsolutePath(
        JsonElement parent,
        string propertyName,
        string failureCode)
    {
        var value = RequiredString(parent, propertyName, failureCode);
        return NormalizeConfiguredPath(value, failureCode);
    }

    private static string? OptionalAbsolutePath(
        JsonElement parent,
        string propertyName,
        string failureCode)
    {
        var value = OptionalString(parent, propertyName, failureCode);
        return value is null ? null : NormalizeConfiguredPath(value, failureCode);
    }

    private static string NormalizeConfiguredPath(string value, string failureCode)
    {
        try
        {
            return SiemProtectedPath.NormalizeAbsolute(value);
        }
        catch (ProtectedPathException)
        {
            Fail(failureCode);
            throw null!;
        }
    }

    private static int ParsePort(JsonElement parent, string propertyName, string failureCode)
    {
        if (!parent.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var port) ||
            port is < 1 or > 65535)
        {
            Fail(failureCode);
            throw null!; // unreachable
        }

        return port;
    }

    private static int? ParseOptionalPositiveInt32(
        JsonElement parent, string propertyName, string failureCode)
    {
        if (!parent.TryGetProperty(propertyName, out var element))
            return null;
        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var value) ||
            value <= 0)
        {
            Fail(failureCode);
        }

        return element.GetInt32();
    }

    private static long? ParseOptionalPositiveInt64(
        JsonElement parent, string propertyName, string failureCode)
    {
        if (!parent.TryGetProperty(propertyName, out var element))
            return null;
        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt64(out var value) ||
            value <= 0)
        {
            Fail(failureCode);
        }

        return element.GetInt64();
    }

    private static IPAddress ParseAddress(string text, string failureCode)
    {
        if (!IPAddress.TryParse(text, out var address))
        {
            Fail(failureCode);
            throw null!; // unreachable
        }

        return address;
    }

    private static IReadOnlyList<string> ParseCaBundlePaths(JsonElement ingest)
    {
        if (!ingest.TryGetProperty("clientCaBundlePaths", out var element) ||
            element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() == 0)
        {
            Fail("client_ca_bundle");
        }

        var paths = new List<string>();
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(entry.GetString()))
            {
                Fail("client_ca_bundle");
            }

            paths.Add(NormalizeConfiguredPath(entry.GetString()!, "client_ca_bundle"));
        }

        return Array.AsReadOnly(paths.ToArray());
    }

    private static X509RevocationMode ParseRevocationMode(JsonElement ingest)
    {
        // Explicit, exact-case; no silent fallback when absent or unrecognized.
        var text = RequiredString(ingest, "revocationCheckMode", "revocation_check_mode");
        return text switch
        {
            "NoCheck" => X509RevocationMode.NoCheck,
            "Online" => X509RevocationMode.Online,
            "Offline" => X509RevocationMode.Offline,
            _ => throw new SiemReceiverConfigurationException("revocation_check_mode"),
        };
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException;

    private static void Fail(string failureCode) =>
        throw new SiemReceiverConfigurationException(failureCode);
}
