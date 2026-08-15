using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Audit.Web;

/// <summary>
/// The production loopback audit UI is destination configuration and delivery
/// status only. Supplying the coordinator and operations removes every raw
/// journal, quarantine, evidence, and legacy settings route. The constructor's
/// internal compatibility path remains only for historical unit tests. This
/// service NEVER gates execution: any fault here degrades to a missing page.
///
/// One UI per audit root: supervisors race to bind the loopback port and the
/// losers stand by, retrying periodically, so whichever process survives
/// keeps serving. Requests authenticate with a bearer token minted into an
/// owner-only file under the audit root — loopback binding alone does not
/// stop a hostile web page from scripting requests at 127.0.0.1 (DNS
/// rebinding), but such a page cannot read the token file.
///
/// The token is minted fresh per bind, published only while this process
/// owns the listener, and deleted on stop (cr5-1): a credential is never
/// published while an unauthenticated process could own the configured
/// port, and a token a squatter manages to harvest dies at the next bind
/// instead of unlocking the real UI later. The unavoidable residue is
/// spoofing — a squatter can serve a fake page to an operator who types
/// the port by hand — but it cannot use what it captures.
/// </summary>
internal sealed class AuditWebUiService : IHostedService, IAsyncDisposable
{
    internal const string TokenFileName = "ui-token";
    internal const int DefaultPort = 8317;
    internal const string PortEnvironmentVariable = "PTK_AUDIT_UI_PORT";
    internal const string DisableEnvironmentVariable = "PTK_AUDIT_UI_DISABLED";
    private const int MaximumTailRecords = 500;
    private const int MaximumRequestBytes = 64 * 1024;

    private readonly AuditOptions _options;
    private readonly AuditHealth _health;
    private readonly AuditExportHealth _exportHealth;
    private readonly Func<AuditJournal?> _journalSource;
    private readonly AuditExportCoordinator? _coordinator;
    private readonly AuditDestinationOperations? _destinationOperations;
    private readonly int _port;
    private readonly TimeSpan _bindRetryInterval;
    private readonly CancellationTokenSource _stopping = new();
    private HttpListener? _listener;
    private Task? _loop;
    private string? _token;
    private bool _tokenPublished;
    private int _disposed;

    internal AuditWebUiService(
        AuditOptions options,
        AuditHealth health,
        AuditExportHealth exportHealth,
        Func<AuditJournal?> journalSource,
        int? port = null,
        TimeSpan? bindRetryInterval = null,
        AuditExportCoordinator? coordinator = null,
        AuditDestinationOperations? destinationOperations = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(exportHealth);
        ArgumentNullException.ThrowIfNull(journalSource);
        _options = options;
        _health = health;
        _exportHealth = exportHealth;
        _journalSource = journalSource;
        _coordinator = coordinator;
        _destinationOperations = destinationOperations;
        _port = port ?? ReadConfiguredPort();
        _bindRetryInterval = bindRetryInterval ?? TimeSpan.FromSeconds(60);
    }

    internal bool IsServing => _listener is not null;

    internal Uri? BoundAddress =>
        _listener is null ? null : new Uri($"http://127.0.0.1:{_port}/");

    private static int ReadConfiguredPort()
    {
        var text = Environment.GetEnvironmentVariable(PortEnvironmentVariable);
        return int.TryParse(text, out var port) && port is >= 1 and <= 65535
            ? port
            : DefaultPort;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(DisableEnvironmentVariable) == "1")
            return Task.CompletedTask;
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        // Retire the published credential before releasing the port: a
        // token must never outlive the listener it authenticates (cr5-1).
        if (_tokenPublished)
        {
            _tokenPublished = false;
            try { File.Delete(Path.Combine(_options.RootDirectory, TokenFileName)); }
            catch (Exception exception) when (!IsFatal(exception)) { }
        }
        try { _listener?.Stop(); }
        catch (Exception exception) when (!IsFatal(exception)) { }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Bind FIRST, mint after (cr5-1): the credential exists only
                // while this process owns the listener it opens, so nothing
                // is ever published toward a port a squatter could hold.
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                listener.Start();
                try
                {
                    _token = MintAndPublishToken();
                    _tokenPublished = true;
                }
                catch
                {
                    // Unpublishable token: release the port so another
                    // supervisor (or this one, next pass) can serve.
                    try { listener.Stop(); }
                    catch (Exception stopFailure) when (!IsFatal(stopFailure)) { }
                    throw;
                }
                _listener = listener;
                await ServeAsync(listener, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // Another supervisor on this root already serves the UI, or
                // the port is otherwise unavailable: stand by and retry. The
                // UI must never take the audit runtime down with it.
                _listener = null;
            }

            try
            {
                await Task.Delay(_bindRetryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ServeAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                if (cancellationToken.IsCancellationRequested) return;
                continue;
            }

            _ = Task.Run(
                () => HandleSafelyAsync(context, cancellationToken),
                CancellationToken.None);
        }
    }

    private async Task HandleSafelyAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            try
            {
                await WriteJsonAsync(
                    context.Response,
                    500,
                    new { error = "internal" }).ConfigureAwait(false);
            }
            catch (Exception writeFailure) when (!IsFatal(writeFailure)) { }
        }
        finally
        {
            try { context.Response.Close(); }
            catch (Exception exception) when (!IsFatal(exception)) { }
        }
    }

    private async Task HandleAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        // Loopback + host pinning: a rebound DNS name still sends its own
        // Host header, and any non-loopback binding is refused outright.
        if (request.RemoteEndPoint?.Address is not { } remote ||
            !IPAddress.IsLoopback(remote) ||
            !IsLoopbackHost(request.UserHostName))
        {
            await WriteJsonAsync(context.Response, 403, new { error = "forbidden" })
                .ConfigureAwait(false);
            return;
        }

        if (!HasValidToken(request))
        {
            await WriteJsonAsync(context.Response, 401, new { error = "unauthorized" })
                .ConfigureAwait(false);
            return;
        }

        var path = request.Url?.AbsolutePath ?? "/";
        if (_coordinator is not null && _destinationOperations is not null)
        {
            await HandleDestinationUiRequestAsync(context, path, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        switch (request.HttpMethod, path)
        {
            case ("GET", "/"):
                await WriteHtmlAsync(context.Response, IndexHtml).ConfigureAwait(false);
                return;
            case ("GET", "/api/health"):
                await WriteJsonAsync(context.Response, 200, BuildHealth()).ConfigureAwait(false);
                return;
            case ("GET", "/api/records"):
                var read = ReadRecentRecords(ParseTail(request));
                await WriteJsonAsync(
                    context.Response,
                    200,
                    new
                    {
                        records = read.Records,
                        partial = read.Partial,
                        unreadable_count = read.UnreadableCount,
                        unreadable_segments = read.UnreadableSegments,
                        live_tail_error = read.LiveTailError,
                        read_error = read.ReadError,
                    })
                    .ConfigureAwait(false);
                return;
            case ("GET", "/api/quarantine"):
                await WriteJsonAsync(context.Response, 200, new { items = ReadQuarantine() })
                    .ConfigureAwait(false);
                return;
            case ("GET", "/api/settings"):
                await WriteJsonAsync(context.Response, 200, ReadSettings()).ConfigureAwait(false);
                return;
            case ("PUT", "/api/settings"):
                await HandleSettingsWriteAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await WriteJsonAsync(context.Response, 404, new { error = "not_found" })
                    .ConfigureAwait(false);
                return;
        }
    }

    private static bool IsLoopbackHost(string? userHostName)
    {
        if (string.IsNullOrEmpty(userHostName)) return false;
        var host = userHostName;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && !host.Contains(']')) host = host[..colon];
        return host is "127.0.0.1" or "localhost" or "[::1]";
    }

    private bool HasValidToken(HttpListenerRequest request)
    {
        if (_token is null) return false;
        var presented = request.Headers["Authorization"] is { } header &&
            header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? header["Bearer ".Length..]
            : request.QueryString["token"];
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(_token));
    }

    /// <summary>
    /// Mints a fresh token for THIS bind and publishes it atomically. A
    /// retained token is never reused: rotation is what makes a harvested
    /// or stale credential worthless against every future listener. The
    /// overwrite is safe because only the process holding the bind reaches
    /// here — a bind-failed standby never touches the file (cr5-5).
    /// </summary>
    private string MintAndPublishToken()
    {
        var path = Path.Combine(_options.RootDirectory, TokenFileName);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var temporaryPath = Path.Combine(
            _options.RootDirectory,
            $".{TokenFileName}.{Guid.NewGuid():N}.tmp");
        using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
        {
            stream.Write(Encoding.ASCII.GetBytes(token));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
        return token;
    }

    private async Task HandleDestinationUiRequestAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (request.HttpMethod == "GET" && path == "/")
        {
            await WriteHtmlAsync(context.Response, StatusIndexHtml).ConfigureAwait(false);
            return;
        }
        if (request.HttpMethod == "GET" &&
            (path == "/api/status" || path == "/api/health"))
        {
            await WriteJsonAsync(context.Response, 200, BuildDestinationStatus())
                .ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "POST" && path == "/api/destinations")
        {
            var body = await ReadJsonBodyAsync(context, cancellationToken).ConfigureAwait(false);
            if (body is null) return;
            using (body)
            {
                if (!TryReadDestinationDraft(body.RootElement, out var draft, out var failure))
                {
                    await WriteJsonAsync(context.Response, 400, new { error = failure })
                        .ConfigureAwait(false);
                    return;
                }
                var confirmed = ReadBoolean(
                    body.RootElement,
                    "confirm_sensitive_duplication");
                var result = await _destinationOperations!
                    .AddAsync(draft, confirmed, cancellationToken)
                    .ConfigureAwait(false);
                await WriteDestinationOperationAsync(context.Response, result)
                    .ConfigureAwait(false);
                return;
            }
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 &&
            string.Equals(parts[0], "api", StringComparison.Ordinal) &&
            string.Equals(parts[1], "destinations", StringComparison.Ordinal) &&
            Guid.TryParse(parts[2], out var destinationId))
        {
            if (request.HttpMethod == "PUT" && parts.Length == 3)
            {
                var body = await ReadJsonBodyAsync(context, cancellationToken).ConfigureAwait(false);
                if (body is null) return;
                using (body)
                {
                    if (!TryReadDestinationDraft(body.RootElement, out var draft, out var failure))
                    {
                        await WriteJsonAsync(context.Response, 400, new { error = failure })
                            .ConfigureAwait(false);
                        return;
                    }
                    var result = await _destinationOperations!
                        .UpdateAsync(destinationId, draft, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteDestinationOperationAsync(context.Response, result)
                        .ConfigureAwait(false);
                    return;
                }
            }

            if (request.HttpMethod == "POST" && parts.Length == 4)
            {
                AuditDestinationOperationResult result;
                switch (parts[3])
                {
                    case "enable":
                        result = await _destinationOperations!
                            .SetEnabledAsync(destinationId, true, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case "disable":
                        result = await _destinationOperations!
                            .SetEnabledAsync(destinationId, false, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case "remove":
                        result = await _destinationOperations!
                            .RemoveAsync(destinationId, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case "abandon":
                    {
                        var body = await ReadJsonBodyAsync(context, cancellationToken)
                            .ConfigureAwait(false);
                        if (body is null) return;
                        using (body)
                        {
                            var actor = ReadString(body.RootElement, "actor") ?? string.Empty;
                            var reason = ReadString(body.RootElement, "reason") ?? string.Empty;
                            var remove = ReadBoolean(body.RootElement, "remove");
                            result = await _destinationOperations!
                                .AbandonAsync(
                                    destinationId,
                                    actor,
                                    reason,
                                    remove,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        break;
                    }
                    case "backfill":
                    {
                        var body = await ReadJsonBodyAsync(context, cancellationToken)
                            .ConfigureAwait(false);
                        if (body is null) return;
                        using (body)
                        {
                            if (!TryReadUtc(body.RootElement, "from_utc", out var fromUtc) ||
                                !TryReadUtc(body.RootElement, "to_utc", out var toUtc))
                            {
                                await WriteJsonAsync(
                                        context.Response,
                                        400,
                                        new { error = "invalid_backfill_range" })
                                    .ConfigureAwait(false);
                                return;
                            }
                            var backfill = await _destinationOperations!
                                .StartBackfillAsync(
                                    destinationId,
                                    fromUtc,
                                    toUtc,
                                    ReadString(body.RootElement, "actor") ?? string.Empty,
                                    ReadBoolean(body.RootElement, "confirm_backfill"),
                                    cancellationToken)
                                .ConfigureAwait(false);
                            await WriteBackfillOperationAsync(context.Response, backfill)
                                .ConfigureAwait(false);
                            return;
                        }
                    }
                    default:
                        await WriteJsonAsync(context.Response, 404, new { error = "not_found" })
                            .ConfigureAwait(false);
                        return;
                }
                await WriteDestinationOperationAsync(context.Response, result)
                    .ConfigureAwait(false);
                return;
            }
        }

        await WriteJsonAsync(context.Response, 404, new { error = "not_found" })
            .ConfigureAwait(false);
    }

    private object BuildDestinationStatus()
    {
        long journalBytes = 0;
        long evidenceBytes = 0;
        var segments = 0;
        var evidenceFiles = 0;
        try
        {
            foreach (var path in Directory.GetFiles(_options.SpoolDirectory, "*.jsonl"))
            {
                segments++;
                journalBytes += new FileInfo(path).Length;
            }
            foreach (var path in Directory.GetFiles(_options.EvidenceDirectory, "*.script"))
            {
                evidenceFiles++;
                evidenceBytes += new FileInfo(path).Length;
            }
        }
        catch (Exception exception) when (!IsFatal(exception)) { }

        return new
        {
            destinations = _coordinator!.Statuses().Select(status =>
            {
                var delivery = status.Delivery;
                var health = !status.Enabled
                    ? "disabled"
                    : delivery.ConsecutiveFailures > 0
                        ? "retrying"
                        : delivery.ExportGaps > 0 ||
                          delivery.RefusedRecords > 0 ||
                          delivery.UnverifiedBootBoundaries > 0
                            ? "degraded"
                            : delivery.PendingBytes > 0 ? "queued" : "healthy";
                return new
                {
                    destination_id = status.DestinationId,
                    kind = status.Kind,
                    operator_label = status.OperatorLabel,
                    endpoint_summary = status.EndpointSummary,
                    adapter = status.Adapter,
                    credential_reference = status.CredentialReference,
                    server_certificate_sha256 = status.ServerCertificateSha256,
                    configuration_revision = status.ConfigurationRevision,
                    activated_utc = status.ActivatedUtc,
                    enabled = status.Enabled,
                    backfill = status.Backfill is null
                        ? null
                        : new
                        {
                            backfill_id = status.Backfill.BackfillId,
                            from_utc = status.Backfill.FromUtc,
                            to_utc = status.Backfill.ToUtc,
                            created_utc = status.Backfill.CreatedUtc,
                            actor = status.Backfill.Actor,
                            state = status.Backfill.State.ToString().ToLowerInvariant(),
                            completed_utc = status.Backfill.CompletedUtc,
                            failure = status.Backfill.Failure,
                            delivery = new
                            {
                                pending_event_records =
                                    status.Backfill.Delivery.PendingEventRecords,
                                pending_event_bytes = Math.Max(
                                    0,
                                    status.Backfill.Delivery.PendingBytes -
                                    status.Backfill.Delivery.PendingEvidenceBytes),
                                pending_evidence_records =
                                    status.Backfill.Delivery.PendingEvidenceRecords,
                                pending_evidence_bytes =
                                    status.Backfill.Delivery.PendingEvidenceBytes,
                                oldest_pending_utc =
                                    status.Backfill.Delivery.OldestPendingUtc,
                                last_attempt_utc =
                                    status.Backfill.Delivery.LastAttemptUtc,
                                last_acknowledgment_utc =
                                    status.Backfill.Delivery.LastAcknowledgmentUtc,
                                error = status.Backfill.Delivery.LastFailureDetail,
                            },
                        },
                    delivery = new
                    {
                        health,
                        pending_event_records = delivery.PendingEventRecords,
                        pending_event_bytes = Math.Max(
                            0,
                            delivery.PendingBytes - delivery.PendingEvidenceBytes),
                        pending_evidence_records = delivery.PendingEvidenceRecords,
                        pending_evidence_bytes = delivery.PendingEvidenceBytes,
                        oldest_pending_utc = delivery.OldestPendingUtc,
                        last_attempt_utc = delivery.LastAttemptUtc,
                        last_acknowledgment_utc = delivery.LastAcknowledgmentUtc,
                        delivered_records = delivery.DeliveredRecords,
                        consecutive_failures = delivery.ConsecutiveFailures,
                        error = delivery.LastFailureDetail,
                        export_gaps = delivery.ExportGaps,
                        missing_records = delivery.MissingRecords,
                        refused_records = delivery.RefusedRecords,
                        unverified_boot_boundaries = delivery.UnverifiedBootBoundaries,
                        standby = delivery.Standby,
                    },
                };
            }).ToArray(),
            local_journal = new
            {
                segments,
                bytes = journalBytes,
                capacity_bytes = _options.AggregateBytes,
                evidence_files = evidenceFiles,
                evidence_bytes = evidenceBytes,
                evidence_capacity_bytes = _options.EvidenceAggregateBytes,
            },
        };
    }

    private static bool TryReadDestinationDraft(
        JsonElement root,
        out AuditDestinationDraft draft,
        out string failure)
    {
        draft = default!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            failure = "invalid_json";
            return false;
        }
        var kind = AuditExportSettings.ParseKind(ReadString(root, "kind"));
        var label = ReadString(root, "operator_label") ?? string.Empty;
        var endpoint = AuditExportSettings.ParseEndpoint(ReadString(root, "endpoint"));
        var credential = ReadString(root, "credential") ?? string.Empty;
        var serverCertificateSha256 = ReadString(root, "server_certificate_sha256");
        if (kind == AuditDestinationKind.None)
        {
            failure = "invalid_kind";
            return false;
        }
        if (endpoint is null)
        {
            failure = "invalid_endpoint";
            return false;
        }
        draft = new AuditDestinationDraft(
            kind,
            label,
            endpoint,
            credential,
            serverCertificateSha256);
        failure = string.Empty;
        return true;
    }

    private async Task<JsonDocument?> ReadJsonBodyAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength64 is < 0 or > MaximumRequestBytes)
        {
            await WriteJsonAsync(context.Response, 400, new { error = "request_too_large" })
                .ConfigureAwait(false);
            return null;
        }
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(context.Response, 400, new { error = "invalid_json" })
                .ConfigureAwait(false);
            return null;
        }
    }

    private static bool ReadBoolean(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static bool TryReadUtc(
        JsonElement root,
        string name,
        out DateTimeOffset value)
    {
        value = default;
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            element.TryGetDateTimeOffset(out value);
    }

    private static Task WriteBackfillOperationAsync(
        HttpListenerResponse response,
        AuditBackfillOperationResult result)
    {
        if (!result.Succeeded)
        {
            var status = result.Failure switch
            {
                "destination_not_found" => 404,
                "backfill_already_active" => 409,
                "backfill_state_unwritable" => 500,
                _ => 400,
            };
            return WriteJsonAsync(response, status, new { error = result.Failure });
        }
        var backfill = result.Backfill!;
        return WriteJsonAsync(response, 200, new
        {
            started = true,
            backfill = new
            {
                backfill_id = backfill.BackfillId,
                destination_id = backfill.DestinationId,
                from_utc = backfill.FromUtc,
                to_utc = backfill.ToUtc,
                created_utc = backfill.CreatedUtc,
                state = backfill.State.ToString().ToLowerInvariant(),
            },
        });
    }

    private static Task WriteDestinationOperationAsync(
        HttpListenerResponse response,
        AuditDestinationOperationResult result)
    {
        if (!result.Succeeded)
        {
            var status = result.Failure switch
            {
                "configuration_unwritable" or "configuration_unreadable" or
                    "configuration_regressed" or "abandonment_unwritable" => 500,
                "endpoint_unreachable" or "validation_timeout" or "credential_refused" => 502,
                "destination_not_found" => 404,
                "sensitive_duplication_confirmation_required" or
                    "pending_obligations_require_abandonment" or
                    "configuration_conflict" or
                    "label_exists" => 409,
                _ => 400,
            };
            return WriteJsonAsync(response, status, new { error = result.Failure });
        }
        var destination = result.Destination;
        return WriteJsonAsync(
            response,
            200,
            new
            {
                saved = true,
                destination = destination is null
                    ? null
                    : new
                    {
                        destination_id = destination.DestinationId,
                        kind = AuditExportSettings.KindText(destination.Kind),
                        operator_label = destination.OperatorLabel,
                        endpoint_summary = destination.RedactedEndpoint,
                        adapter = destination.Adapter,
                        credential_reference = destination.CredentialReference,
                        server_certificate_sha256 = destination.ServerCertificateSha256,
                        configuration_revision = destination.ConfigurationRevision,
                        activated_utc = destination.ActivatedUtc,
                        enabled = destination.Enabled,
                    },
            });
    }

    private object BuildHealth()
    {
        var audit = _health.Snapshot();
        var export = _exportHealth.Snapshot();
        long spoolBytes = 0;
        var segmentCount = 0;
        try
        {
            foreach (var file in Directory.GetFiles(_options.SpoolDirectory, "*.jsonl"))
            {
                segmentCount++;
                spoolBytes += new FileInfo(file).Length;
            }
        }
        catch (Exception exception) when (!IsFatal(exception)) { }

        return new
        {
            audit = new
            {
                state = audit.State.ToString().ToLowerInvariant(),
                mode = audit.ProtectionMode == AuditProtectionMode.LocalOnly
                    ? "local-only"
                    : "anchored",
                failure_class = audit.FailureClass,
                undelivered_evictions = audit.UndeliveredEvictions,
                lineage_publish_failures = audit.LineagePublishFailures,
            },
            export = new
            {
                status_line = export.StatusLine(),
                configured = export.Configured,
                delivered = export.DeliveredRecords,
                pending_bytes = export.PendingBytes,
                export_gaps = export.ExportGaps,
                missing_records = export.MissingRecords,
                refused_records = export.RefusedRecords,
                unverified_boot_boundaries = export.UnverifiedBootBoundaries,
                standby = export.Standby,
                alert_webhook = new
                {
                    configured = export.AlertWebhookConfigured,
                    consecutive_failures = export.AlertWebhookConsecutiveFailures,
                    last_failure = export.AlertWebhookLastFailure,
                    last_success_utc = export.AlertWebhookLastSuccessUtc?.ToString("O"),
                },
            },
            spool = new { segments = segmentCount, bytes = spoolBytes },
        };
    }

    private sealed record RecordsRead(
        IReadOnlyList<string> Records,
        int UnreadableCount,
        IReadOnlyList<object> UnreadableSegments,
        string? LiveTailError,
        string? ReadError)
    {
        public bool Partial =>
            UnreadableCount > 0 || LiveTailError is not null || ReadError is not null;
    }

    private const int MaximumReportedUnreadableSegments = 8;

    /// <summary>
    /// The newest records across the spool, oldest-first within the answer.
    /// Closed segments are read as files; this supervisor's own live tail is
    /// read through the journal writer's handle. Another supervisor's live
    /// segment becomes readable after rotation — the honest limit of a
    /// shared root, stated in the UI. Any other read failure is evidence the
    /// answer is missing, and the answer says so (cr5-3): only a vanished
    /// file (retention) and a lock-shaped failure on the newest segment of
    /// its boot — the one position a live segment can occupy — pass as
    /// expected.
    /// </summary>
    private RecordsRead ReadRecentRecords(int tail)
    {
        var units = new List<(DateTime SortKey, List<string> Lines)>();
        var unreadableCount = 0;
        var unreadable = new List<object>();
        string? liveTailError = null;
        string? readError = null;
        try
        {
            var files = new DirectoryInfo(_options.SpoolDirectory)
                .GetFiles("*.jsonl")
                .Select(file => AuditSpoolSegmentIdentity.TryParse(file.Name, out var identity)
                    ? (File: file, Identity: identity)
                    : default)
                .Where(entry => entry.File is not null)
                .OrderBy(entry => entry.File.LastWriteTimeUtc)
                .ToArray();
            var newestIndexPerBoot = files
                .GroupBy(entry => entry.Identity.SupervisorBootId)
                .ToDictionary(group => group.Key, group => group.Max(entry => entry.Identity.Index));
            foreach (var (file, identity) in files)
            {
                try
                {
                    using var stream = new FileStream(
                        file.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var segmentLines = new List<string>();
                    while (reader.ReadLine() is { } line)
                    {
                        if (line.Length > 0) segmentLines.Add(line);
                    }
                    units.Add((file.LastWriteTimeUtc, segmentLines));
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    var failureClass = ClassifySegmentReadFailure(
                        exception,
                        identity.Index == newestIndexPerBoot[identity.SupervisorBootId]);
                    if (failureClass != SegmentReadFailureClass.Reportable) continue;
                    unreadableCount++;
                    if (unreadable.Count < MaximumReportedUnreadableSegments)
                    {
                        unreadable.Add(new
                        {
                            segment = file.Name,
                            error = exception.GetType().Name,
                        });
                    }
                }
            }

            var journal = _journalSource();
            if (journal is not null)
            {
                try
                {
                    // The live tail holds the NEWEST records, so it is read
                    // unconditionally (cr5-4): a populated closed spool must
                    // not short-circuit it. The read is bounded by the live
                    // segment itself — rotation caps its size, and every
                    // pass either advances the offset or breaks.
                    long offset = 0;
                    var identity = default(AuditSpoolSegmentIdentity);
                    var identityKnown = false;
                    var liveLines = new List<string>();
                    while (true)
                    {
                        AuditCommittedSpoolRead read;
                        if (!identityKnown)
                        {
                            read = journal.ReadCommittedSpool(
                                AuditSpoolSegmentIdentity.Create(journal.SupervisorBootId, 0),
                                0,
                                _options.MaxRecordBytes);
                            if (read.CurrentSegment is not { } current) break;
                            identity = current;
                            identityKnown = true;
                            offset = 0;
                            continue;
                        }

                        read = journal.ReadCommittedSpool(identity, offset, _options.MaxRecordBytes);
                        if (read.Status != AuditCommittedSpoolReadStatus.Data ||
                            read.Bytes.IsEmpty)
                        {
                            break;
                        }
                        var text = Encoding.UTF8.GetString(read.Bytes.Span);
                        var lastNewline = text.LastIndexOf('\n');
                        if (lastNewline < 0) break;
                        foreach (var line in text[..lastNewline].Split('\n'))
                        {
                            if (line.Length > 0) liveLines.Add(line);
                        }
                        offset += Encoding.UTF8.GetByteCount(text[..(lastNewline + 1)]);
                    }

                    if (liveLines.Count > 0)
                    {
                        // The live tail is one more unit, keyed by its own
                        // segment's last write — an unreadable stat falls
                        // back to newest, the single-supervisor truth.
                        var sortKey = DateTime.MaxValue;
                        if (identityKnown)
                        {
                            try
                            {
                                var liveFile = new FileInfo(Path.Combine(
                                    _options.SpoolDirectory,
                                    identity.FileName));
                                if (liveFile.Exists) sortKey = liveFile.LastWriteTimeUtc;
                            }
                            catch (Exception statFailure) when (!IsFatal(statFailure)) { }
                        }
                        units.Add((sortKey, liveLines));
                    }
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    liveTailError = exception.GetType().Name;
                }
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            readError = exception.GetType().Name;
        }

        // Newest evidence wins regardless of which unit held it (cr5-4
        // repair round 1): closed segments and this supervisor's live tail
        // are ordered together by their segment's last write, so a quiet
        // bind winner's stale live tail cannot outrank a busy peer's newer
        // rotated segments. OrderBy is stable, so a same-instant tie keeps
        // the live tail last, as before.
        var lines = units
            .OrderBy(unit => unit.SortKey)
            .SelectMany(unit => unit.Lines)
            .ToList();
        return new RecordsRead(
            lines.Count <= tail ? lines : lines[^tail..],
            unreadableCount,
            unreadable,
            liveTailError,
            readError);
    }

    internal enum SegmentReadFailureClass
    {
        /// <summary>The segment FILE vanished: retention, silent.</summary>
        VanishedSegment,
        /// <summary>Lock-shaped failure on the newest segment of its boot —
        /// the one position a live segment can occupy; served via the
        /// journal handle when ours, readable after rotation when another
        /// supervisor's.</summary>
        ExpectedLive,
        /// <summary>Omitted evidence the answer must report.</summary>
        Reportable,
    }

    /// <summary>
    /// The decision table for a closed-segment read failure (cr5-3, repair
    /// round 1). Order matters: <see cref="FileNotFoundException"/> and
    /// <see cref="DirectoryNotFoundException"/> both derive from
    /// <see cref="IOException"/> — a vanished FILE is retention, but a
    /// vanished DIRECTORY is the spool itself going away, which is
    /// reportable evidence loss even on the newest segment of a boot.
    /// </summary>
    internal static SegmentReadFailureClass ClassifySegmentReadFailure(
        Exception exception,
        bool newestOfBoot) => exception switch
    {
        FileNotFoundException => SegmentReadFailureClass.VanishedSegment,
        DirectoryNotFoundException => SegmentReadFailureClass.Reportable,
        IOException when newestOfBoot => SegmentReadFailureClass.ExpectedLive,
        _ => SegmentReadFailureClass.Reportable,
    };

    private static int ParseTail(HttpListenerRequest request) =>
        int.TryParse(request.QueryString["tail"], out var tail) &&
        tail is >= 1 and <= MaximumTailRecords
            ? tail
            : 100;

    private IReadOnlyList<object> ReadQuarantine()
    {
        try
        {
            var directory = new DirectoryInfo(
                Path.Combine(_options.RootDirectory, AuditJournalFactory.QuarantineDirectoryName));
            if (!directory.Exists) return [];
            return directory.GetFiles()
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(100)
                .Select(object (file) => new
                {
                    name = file.Name,
                    bytes = file.Length,
                    modified_utc = file.LastWriteTimeUtc.ToString("O"),
                })
                .ToArray();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return [];
        }
    }

    private object ReadSettings()
    {
        var settings = AuditExportSettings.Load(_options.RootDirectory, out var failure);
        return new
        {
            kind = AuditExportSettings.KindText(settings.Kind),
            endpoint = settings.Endpoint?.ToString(),
            credential_set = !string.IsNullOrEmpty(settings.Credential),
            configuration_failure = failure,
            note = "Changes apply when PTK next starts; the export configuration is startup-frozen.",
        };
    }

    private async Task HandleSettingsWriteAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength64 is < 0 or > MaximumRequestBytes)
        {
            await WriteJsonAsync(context.Response, 400, new { error = "request_too_large" })
                .ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        string? kind = null;
        string? endpoint = null;
        string? credential = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            kind = ReadString(root, "kind");
            endpoint = ReadString(root, "endpoint");
            credential = ReadString(root, "credential");
        }
        catch (JsonException)
        {
            await WriteJsonAsync(context.Response, 400, new { error = "invalid_json" })
                .ConfigureAwait(false);
            return;
        }

        if (!AuditExportSettings.TryValidateForWrite(kind, endpoint, out var validationFailure))
        {
            await WriteJsonAsync(context.Response, 400, new { error = validationFailure })
                .ConfigureAwait(false);
            return;
        }

        if (!AuditExportSettings.TryWrite(
                _options.RootDirectory,
                kind,
                endpoint,
                credential))
        {
            await WriteJsonAsync(context.Response, 500, new { error = "write_failed" })
                .ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
            context.Response,
            200,
            new { saved = true, applies = "next start" }).ConfigureAwait(false);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        int status,
        object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse response, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopping.Dispose();
        try { _listener?.Close(); }
        catch (Exception exception) when (!IsFatal(exception)) { }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private const string StatusIndexHtml = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>PTK delivery status</title>
<style>
body{font:15px system-ui,sans-serif;max-width:1100px;margin:2rem auto;padding:0 1rem;color:#17202a;background:#f7f8fa}
h1,h2{margin:.5rem 0}.note{background:#eef4ff;border-left:4px solid #356ad2;padding:.8rem;margin:1rem 0}
table{width:100%;border-collapse:collapse;background:white}th,td{padding:.55rem;border-bottom:1px solid #ddd;text-align:left;vertical-align:top}
code{font-size:.85em}.ok{color:#14743c}.bad{color:#a12622}.queued{color:#8a5900}form{background:white;padding:1rem;margin:1rem 0;display:grid;gap:.6rem}
input,select,button{font:inherit;padding:.45rem}button{width:max-content}.actions button{margin:.15rem}.muted{color:#59636e}
</style></head><body>
<h1>PTK SIEM delivery</h1>
<p class="note">This page shows destination configuration, independent delivery state, and local journal capacity. It does not expose commands, output, errors, or forensic records.</p>
<section><h2>Destinations</h2><div id="destinations">Loading…</div></section>
<section><h2>Add destination</h2><form id="add">
<label>Operator label <input name="label" maxlength="128" required></label>
<label>Type <select name="kind"><option value="otlp_http">OTLP/HTTP</option><option value="splunk_hec">Splunk HEC</option></select></label>
<label>Endpoint <input name="endpoint" type="url" required placeholder="https://siem.example/v1/logs"></label>
<label>Credential <input name="credential" type="password" autocomplete="new-password"></label>
<label>Optional server certificate SHA-256 pin <input name="server_certificate_sha256" maxlength="95" autocomplete="off"></label>
<label><input name="duplicate" type="checkbox"> I confirm this additional destination duplicates sensitive PTK evidence.</label>
<button>Add and enable</button><span id="result" class="muted"></span>
</form></section>
<section><h2>Local journal</h2><div id="journal"></div></section>
<script>
const token=new URLSearchParams(location.search).get('token')||'';
const api=(path,options={})=>fetch(path,{...options,headers:{Authorization:'Bearer '+token,'Content-Type':'application/json',...(options.headers||{})}});
const fmt=n=>new Intl.NumberFormat().format(n||0); const when=v=>v?new Date(v).toLocaleString():'never';
async function refresh(){const r=await api('/api/status');if(!r.ok){document.getElementById('destinations').textContent='Status unavailable: '+r.status;return}const s=await r.json();
document.getElementById('destinations').innerHTML=s.destinations.length?'<table><thead><tr><th>Destination</th><th>Delivery</th><th>Pending</th><th>Last activity</th><th>Actions</th></tr></thead><tbody>'+s.destinations.map(d=>{const x=d.delivery;const b=d.backfill?`<br><span class="muted">backfill ${esc(d.backfill.state)} ${when(d.backfill.from_utc)} → ${when(d.backfill.to_utc)}</span>`:'';return `<tr><td><strong>${esc(d.operator_label)}</strong><br><code>${esc(d.destination_id)}</code><br>${esc(d.kind)} · ${esc(d.endpoint_summary)}<br><span class="muted">credential ${esc(d.credential_reference)} · rev ${d.configuration_revision}</span>${b}</td><td class="${x.health==='healthy'?'ok':x.health==='queued'?'queued':'bad'}">${esc(x.health)}${x.error?'<br>'+esc(x.error):''}</td><td>${fmt(x.pending_event_records)} events / ${fmt(x.pending_event_bytes)} bytes<br>${fmt(x.pending_evidence_records)} evidence / ${fmt(x.pending_evidence_bytes)} bytes<br>oldest ${when(x.oldest_pending_utc)}</td><td>attempt ${when(x.last_attempt_utc)}<br>ack ${when(x.last_acknowledgment_utc)}</td><td class="actions"><button onclick="op('${d.destination_id}','${d.enabled?'disable':'enable'}')">${d.enabled?'Disable':'Enable'}</button><button onclick="op('${d.destination_id}','remove')">Remove</button><button onclick="abandon('${d.destination_id}')">Abandon backlog</button><button onclick="backfill('${d.destination_id}')">Backfill range</button></td></tr>`}).join('')+'</tbody></table>':'No destination configured. Add the one destination PTK should use.';
const j=s.local_journal;document.getElementById('journal').textContent=`Journal ${fmt(j.bytes)} / ${fmt(j.capacity_bytes)} bytes in ${fmt(j.segments)} segments; evidence ${fmt(j.evidence_bytes)} / ${fmt(j.evidence_capacity_bytes)} bytes in ${fmt(j.evidence_files)} files.`}
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
async function op(id,action){const r=await api(`/api/destinations/${id}/${action}`,{method:'POST',body:'{}'});const x=await r.json();if(!r.ok)alert(x.error);await refresh()}
async function abandon(id){const actor=prompt('Operator name recording this custody decision:');if(!actor)return;const reason=prompt('Reason for abandoning undelivered obligations:');if(!reason)return;if(!confirm('This releases local retention for undelivered sensitive evidence. Continue?'))return;const r=await api(`/api/destinations/${id}/abandon`,{method:'POST',body:JSON.stringify({actor,reason,remove:false})});const x=await r.json();if(!r.ok)alert(x.error);await refresh()}
async function backfill(id){const actor=prompt('Operator starting this bounded backfill:');if(!actor)return;const from_utc=prompt('Start UTC (inclusive), for example 2026-08-01T00:00:00Z:');if(!from_utc)return;const to_utc=prompt('End UTC (exclusive):');if(!to_utc)return;if(!confirm('Send all PTK evidence in this explicit range to this destination?'))return;const r=await api(`/api/destinations/${id}/backfill`,{method:'POST',body:JSON.stringify({actor,from_utc,to_utc,confirm_backfill:true})});const x=await r.json();if(!r.ok)alert(x.error);await refresh()}
document.getElementById('add').addEventListener('submit',async e=>{e.preventDefault();const f=new FormData(e.target);const body={operator_label:f.get('label'),kind:f.get('kind'),endpoint:f.get('endpoint'),credential:f.get('credential'),server_certificate_sha256:f.get('server_certificate_sha256'),confirm_sensitive_duplication:f.get('duplicate')==='on'};const r=await api('/api/destinations',{method:'POST',body:JSON.stringify(body)});const x=await r.json();document.getElementById('result').textContent=r.ok?'Saved':x.error;if(r.ok){e.target.reset();await refresh()}});refresh();setInterval(refresh,5000);
</script></body></html>
""";

    private const string IndexHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>PTK Audit</title>
<style>
body{font-family:ui-monospace,Menlo,Consolas,monospace;margin:1.5rem;background:#111;color:#ddd}
h1{font-size:1.2rem} h2{font-size:1rem;margin-top:1.5rem;color:#9cf}
pre{background:#1a1a1a;padding:.75rem;overflow:auto;border-radius:6px}
table{border-collapse:collapse;width:100%} td,th{border-bottom:1px solid #333;padding:.25rem .5rem;text-align:left;font-size:.85rem}
input,select{background:#222;color:#ddd;border:1px solid #444;padding:.3rem;border-radius:4px}
button{background:#265;color:#fff;border:0;padding:.4rem .8rem;border-radius:4px;cursor:pointer}
.warn{color:#fa5}.ok{color:#5c5}#msg{margin-left:.75rem}
</style>
</head>
<body>
<h1>PTK Audit — local journal</h1>
<p>Everything on this page is read from the local audit journal; it does not
depend on any SIEM being reachable. Another supervisor's in-progress segment
appears here after it rotates.</p>
<h2>Health</h2><pre id="health">loading…</pre>
<h2>Recent records (<span id="count">…</span>)</h2>
<div id="partial" class="warn"></div>
<table id="records"><thead><tr><th>time</th><th>type</th><th>session</th><th>outcome</th></tr></thead><tbody></tbody></table>
<h2>Quarantine</h2><pre id="quarantine">loading…</pre>
<h2>SIEM connection</h2>
<form id="settings" onsubmit="return saveSettings(event)">
<label>Kind <select id="kind">
<option value="none">none (local only)</option>
<option value="otlp_http">OTLP / PTK receiver</option>
<option value="splunk_hec">Splunk HEC</option>
</select></label>
<label>Endpoint <input id="endpoint" size="40" placeholder="https://host:4318/"></label>
<label>Token <input id="credential" type="password" size="24" placeholder="(unchanged)"></label>
<button>Save</button><span id="msg"></span>
</form>
<p>Settings apply when PTK next starts; the export configuration is
startup-frozen by design.</p>
<script>
const token=new URLSearchParams(location.search).get('token')||'';
const api=(p)=>fetch(p,{headers:{Authorization:'Bearer '+token}});
const put=(p,b)=>fetch(p,{method:'PUT',headers:{Authorization:'Bearer '+token,'Content-Type':'application/json'},body:JSON.stringify(b)});
async function refresh(){
 const h=await (await api('/api/health')).json();
 document.getElementById('health').textContent=JSON.stringify(h,null,1);
 const r=await (await api('/api/records?tail=100')).json();
 const body=document.querySelector('#records tbody');body.innerHTML='';
 document.getElementById('count').textContent=r.records.length;
 document.getElementById('partial').textContent=r.partial?'WARNING: partial read — some journal evidence could not be read ('+(r.unreadable_count||0)+' unreadable segment(s)'+(r.live_tail_error?', live tail: '+r.live_tail_error:'')+(r.read_error?', spool: '+r.read_error:'')+')':'';
 for(const line of r.records.slice().reverse()){
  let rec;try{rec=JSON.parse(line)}catch{rec=null}
  const tr=document.createElement('tr');
  const cells=rec?[rec.observed_utc,rec.event_type,(rec.session&&rec.session.name)||'',(rec.outcome&&rec.outcome.state)||'']:[ '','unparseable','',''];
  for(const c of cells){const td=document.createElement('td');td.textContent=c||'';tr.appendChild(td)}
  tr.title=line;body.appendChild(tr);
 }
 const q=await (await api('/api/quarantine')).json();
 document.getElementById('quarantine').textContent=q.items.length?JSON.stringify(q.items,null,1):'none';
 const s=await (await api('/api/settings')).json();
 document.getElementById('kind').value=s.kind||'none';
 document.getElementById('endpoint').value=s.endpoint||'';
}
async function saveSettings(e){
 e.preventDefault();
 const body={kind:document.getElementById('kind').value,endpoint:document.getElementById('endpoint').value};
 const cred=document.getElementById('credential').value;if(cred)body.credential=cred;
 const res=await put('/api/settings',body);
 document.getElementById('msg').textContent=res.ok?'saved — applies at next PTK start':'save failed';
 document.getElementById('msg').className=res.ok?'ok':'warn';
 return false;
}
refresh();setInterval(refresh,5000);
</script>
</body>
</html>
""";
}
