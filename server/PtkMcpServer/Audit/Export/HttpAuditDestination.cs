using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PtkMcpServer.Audit.Export;

/// <summary>
/// The one HTTP delivery path shared by every destination. Only the request
/// body and the credential header differ per kind, so Splunk, an OTLP
/// collector, and the PTK fallback receiver are configured and operated
/// identically (owner ruling 2026-08-10).
/// </summary>
internal sealed class HttpAuditDestination : IAuditDestination
{
    private readonly AuditDestinationKind _kind;
    private readonly Uri _endpoint;
    private readonly string? _credential;
    private readonly string? _serverCertificateSha256;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    internal HttpAuditDestination(
        AuditExportSettings settings,
        HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsConfigured)
            throw new ArgumentException("The destination is not configured.", nameof(settings));

        _kind = settings.Kind;
        _endpoint = RequestUri(settings.Kind, settings.Endpoint!);
        _credential = settings.Credential;
        _serverCertificateSha256 = settings.ServerCertificateSha256;
        _ownsClient = client is null;
        _client = client ?? AuditDestinationTls.CreateClient(TimeSpan.FromSeconds(30));
    }

    public string Describe() =>
        $"{AuditExportSettings.KindText(_kind)} {_endpoint.GetLeftPart(UriPartial.Path)}";

    public async Task<AuditDeliveryResult> DeliverAsync(
        IReadOnlyList<string> records,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return AuditDeliveryResult.Delivered;

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        AuditDestinationTls.ApplyPin(request, _serverCertificateSha256);
        var (body, mediaType) = _kind switch
        {
            AuditDestinationKind.SplunkHec => (FormatSplunkEvents(records), "application/json"),
            _ => (FormatOtlpLogs(records), "application/json"),
        };
        request.Content = new StringContent(body, new UTF8Encoding(false), mediaType);
        ApplyCredential(request);

        HttpResponseMessage response;
        try
        {
            response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // The client's own timeout: the destination may still have taken
            // the batch, so retry (at-least-once, duplicates tolerated).
            return AuditDeliveryResult.Retryable("export.timeout");
        }
        catch (HttpRequestException)
        {
            return AuditDeliveryResult.Retryable("export.unreachable");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode) return AuditDeliveryResult.Delivered;
            var status = (int)response.StatusCode;
            // 5xx and 429 are the destination asking for later; 408/425 are
            // explicitly transient; 401/403 are operator-fixable
            // configuration. All are retried rather than discarding audit
            // records — only a genuine refusal of these bytes is permanent
            // (cr3-5: 408 was classified permanent and skipped a whole batch).
            if (status >= 500 ||
                status is (int)HttpStatusCode.TooManyRequests
                    or (int)HttpStatusCode.RequestTimeout
                    or 425)
            {
                return AuditDeliveryResult.Retryable($"export.http_{status}");
            }
            if (status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
                return AuditDeliveryResult.Retryable($"export.unauthorized_{status}");
            return AuditDeliveryResult.Permanent($"export.http_{status}");
        }
    }

    private void ApplyCredential(HttpRequestMessage request)
    {
        if (string.IsNullOrEmpty(_credential)) return;
        request.Headers.Authorization = _kind == AuditDestinationKind.SplunkHec
            ? new AuthenticationHeaderValue("Splunk", _credential)
            : new AuthenticationHeaderValue("Bearer", _credential);
    }

    /// <summary>Splunk HEC accepts concatenated event objects.</summary>
    private static string FormatSplunkEvents(IReadOnlyList<string> records)
    {
        var builder = new StringBuilder();
        foreach (var record in records)
        {
            builder.Append(AuditEvidenceEnvelope.IsEvidenceRecord(record)
                ? "{\"sourcetype\":\"ptk:evidence\",\"event\":"
                : "{\"sourcetype\":\"ptk:audit\",\"event\":");
            AppendRecord(builder, record);
            builder.Append('}');
        }
        return builder.ToString();
    }

    /// <summary>
    /// OTLP/HTTP JSON logs. JSON rather than protobuf deliberately: it needs
    /// no protoc/Grpc.Tools build path, which is the recorded ARM64 Linux
    /// build blocker, while remaining a standard OTLP encoding every
    /// collector accepts (amends the R1 vendor-the-generated-code call; see
    /// .agents/plans/audit-restoration-r1-discovery.md).
    /// </summary>
    private static string FormatOtlpLogs(IReadOnlyList<string> records)
    {
        var builder = new StringBuilder();
        builder.Append(
            "{\"resourceLogs\":[{\"resource\":{\"attributes\":[" +
            "{\"key\":\"service.name\",\"value\":{\"stringValue\":\"ptk\"}}]}," +
            "\"scopeLogs\":[{\"scope\":{\"name\":\"ptk.audit\"},\"logRecords\":[");
        for (var index = 0; index < records.Count; index++)
        {
            if (index > 0) builder.Append(',');
            AppendOtlpLogRecord(builder, records[index]);
        }
        builder.Append("]}]}]}");
        return builder.ToString();
    }

    private static void AppendOtlpLogRecord(StringBuilder builder, string record)
    {
        var (eventType, eventId, timeUnixNano) = DescribeRecord(record);
        builder.Append("{\"timeUnixNano\":\"")
            .Append(timeUnixNano.ToString(CultureInfo.InvariantCulture))
            .Append("\",\"severityText\":\"INFO\",\"body\":{\"stringValue\":");
        builder.Append(JsonSerializer.Serialize(record));
        builder.Append("},\"attributes\":[");
        var wroteAttribute = false;
        if (eventType is not null)
        {
            builder.Append("{\"key\":\"ptk.event_type\",\"value\":{\"stringValue\":")
                .Append(JsonSerializer.Serialize(eventType))
                .Append("}}");
            wroteAttribute = true;
        }
        if (eventId is not null)
        {
            if (wroteAttribute) builder.Append(',');
            builder.Append("{\"key\":\"ptk.event_id\",\"value\":{\"stringValue\":")
                .Append(JsonSerializer.Serialize(eventId))
                .Append("}}");
        }
        builder.Append("]}");
    }

    /// <summary>
    /// Best-effort indexing hints. A record that cannot be parsed is still
    /// delivered verbatim: the exporter never drops audit evidence because it
    /// could not decorate it.
    /// </summary>
    private static (string? EventType, string? EventId, long TimeUnixNano) DescribeRecord(
        string record)
    {
        try
        {
            using var document = JsonDocument.Parse(record);
            var root = document.RootElement;
            var eventType = root.TryGetProperty("event_type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
            var eventId = root.TryGetProperty("event_id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            var timestamp = DateTimeOffset.UtcNow;
            if (root.TryGetProperty("observed_utc", out var observed) &&
                observed.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    observed.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                timestamp = parsed;
            }
            return (eventType, eventId, UnixNanoseconds(timestamp));
        }
        catch (JsonException)
        {
            return (null, null, UnixNanoseconds(DateTimeOffset.UtcNow));
        }
    }

    private static long UnixNanoseconds(DateTimeOffset value) =>
        value.ToUnixTimeMilliseconds() * 1_000_000L;

    private static void AppendRecord(StringBuilder builder, string record)
    {
        // A canonical journal line is already JSON; anything unparseable is
        // shipped as a string rather than dropped.
        try
        {
            using var document = JsonDocument.Parse(record);
            builder.Append(record);
        }
        catch (JsonException)
        {
            builder.Append(JsonSerializer.Serialize(record));
        }
    }

    private static Uri RequestUri(AuditDestinationKind kind, Uri endpoint)
    {
        // A bare host is completed with the destination's conventional path;
        // an endpoint that already names a path is used exactly as given.
        if (endpoint.AbsolutePath is not ("" or "/")) return endpoint;
        var suffix = kind switch
        {
            AuditDestinationKind.SplunkHec => "services/collector/event",
            _ => "v1/logs",
        };
        return new Uri(endpoint, suffix);
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
