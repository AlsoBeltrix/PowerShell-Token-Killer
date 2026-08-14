using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Storage;

namespace PtkSiemReceiver.Web;

/// <summary>
/// Marker set on connections accepted by the operator listener, so request
/// handlers can tell the two surfaces apart without port bookkeeping: ingest
/// must never serve on the operator port and the operator API must never
/// serve on ingest, whatever ports the deployment chose.
/// </summary>
internal interface IOperatorSurfaceFeature;

internal sealed class OperatorSurfaceFeature : IOperatorSurfaceFeature
{
    internal static readonly OperatorSurfaceFeature Instance = new();
}

/// <summary>Container-owned holder so the operator HTTPS certificate is
/// disposed with the application (mirrors the ingest certificate's
/// factory-singleton ownership).</summary>
internal sealed class OperatorTlsMaterial(
    System.Security.Cryptography.X509Certificates.X509Certificate2? certificate) : IDisposable
{
    internal System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate { get; }
        = certificate;

    public void Dispose() => Certificate?.Dispose();
}

/// <summary>
/// The read-only operator query API + dashboard (mini-SIEM S5, executed as
/// audit-restoration R5b): events by time/type/session/boot filters, event
/// detail with chain context, chain status, and the quarantine evidence
/// list, rendered by one embedded static page. Separate listener from
/// ingest; bearer-token auth from the protected config; loopback-bound by
/// default, and the config loader refuses a non-loopback bind without an
/// operator HTTPS certificate, so the token never travels plaintext
/// off-host. Everything here reads the store through short-lived read-only
/// connections — this surface can inspect evidence, never create or change
/// it (the alert-lifecycle writer arrives with S6).
/// </summary>
internal static class OperatorEndpoints
{
    private const int DefaultEventLimit = 100;
    private const int MaximumEventLimit = 500;
    internal const int MaximumChainLimit = 200;

    internal static void Map(WebApplication application)
    {
        application.MapGet("/", HandleDashboardAsync);
        application.MapGet("/api/events", HandleEventsAsync);
        application.MapGet("/api/events/{eventId}", HandleEventDetailAsync);
        application.MapGet("/api/evidence/{artifactId}", HandleEvidenceAsync);
        application.MapGet("/api/chains", HandleChainsAsync);
        application.MapGet("/api/quarantine", HandleQuarantineAsync);
        application.MapGet("/api/gaps", HandleGapsAsync);
        application.MapPost("/api/gaps/{gapId:long}/disposition", HandleGapDispositionAsync);
        application.MapGet("/api/alerts", HandleAlertsAsync);
        application.MapPost("/api/alerts/{alertId:long}/transition", HandleAlertTransitionAsync);
        application.MapGet("/api/custody/health", HandleCustodyHealthAsync);
        if (application.Services.GetService<CustodyWitness>() is not null)
            application.MapPost("/api/custody/restore", HandleCustodyRestoreAsync);
    }

    private static async Task HandleCustodyHealthAsync(
        HttpContext context,
        SiemReceiverOptions options,
        CustodyHealthState health)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;
        var snapshot = health.Snapshot;
        await WriteJsonAsync(context, 200, new
        {
            healthy = snapshot.Healthy,
            failure_code = snapshot.FailureCode,
            checked_utc = snapshot.CheckedUtc,
            custody_sequence = snapshot.CustodySequence,
            custody_hash = snapshot.CustodyHash,
            witness_sequence = snapshot.WitnessSequence,
            witness_hash = snapshot.WitnessHash,
            restore_pending = snapshot.RestorePending,
            anchor_configured = snapshot.AnchorConfigured,
        }).ConfigureAwait(false);
    }

    private static async Task HandleCustodyRestoreAsync(
        HttpContext context,
        SiemReceiverOptions options,
        CustodyWitness witness)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;
        try
        {
            var outcome = await witness.AuthorizeRestoreAsync(
                OperatorReceipt(context, options),
                context.RequestAborted).ConfigureAwait(false);
            await WriteJsonAsync(context, 200, new
            {
                restore_recorded = outcome.Created,
                alert_id = outcome.AlertId,
                custody_sequence = outcome.Head.Sequence,
                custody_hash = outcome.Head.Hash,
            }).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await WriteJsonAsync(
                context, 409, new { error = "custody_restore_not_pending" })
                .ConfigureAwait(false);
        }
    }

    // ---- Admission ----

    private static async Task<bool> AdmitAsync(HttpContext context, SiemReceiverOptions options)
    {
        if (!await AdmitSurfaceAsync(context, options).ConfigureAwait(false)) return false;

        if (!HasValidOperatorToken(context.Request, options.OperatorToken))
        {
            await WriteJsonAsync(
                context, 401, new { error = "unauthorized" }).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    /// <summary>Surface + Host admission without the credential: the static
    /// dashboard page carries zero evidence, so it serves token-free and the
    /// operator pastes the token into the page instead of the URL — a URL
    /// travels through request logs and browser history, a header does
    /// not.</summary>
    private static async Task<bool> AdmitSurfaceAsync(
        HttpContext context, SiemReceiverOptions options)
    {
        if (context.Features.Get<IOperatorSurfaceFeature>() is null)
        {
            await WriteJsonAsync(
                context, 404, new { error = "not_found" }).ConfigureAwait(false);
            return false;
        }

        // Plain-HTTP serving is loopback-only by configuration; pin the Host
        // header too so a DNS-rebound page cannot script the API (the
        // producer UI's rule). An HTTPS operator endpoint authenticates the
        // server by certificate instead.
        if (options.OperatorHttpsCertificatePath is null &&
            !IsLoopbackHost(context.Request.Host.Host))
        {
            await WriteJsonAsync(
                context, 403, new { error = "forbidden" }).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "localhost" or "::1";

    // Header-only on purpose: a query-string credential lands in request
    // logs and browser history (cr7-1).
    private static bool HasValidOperatorToken(HttpRequest request, string operatorToken)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var presented = header["Bearer ".Length..];
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(operatorToken));
    }

    // ---- Read-only queries ----

    internal static async Task HandleEventsAsync(
        HttpContext context,
        SiemReceiverOptions options)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;

        var limit = ParseLimit(context.Request.Query["limit"].ToString());

        // Time bounds are parsed, not compared as caller text: the store's
        // column is the fixed seven-digit format below, and lexicographic
        // comparison against a coarser caller string silently drops
        // same-second evidence (cr7-4).
        if (!TryCanonicalizeTimeFilter(
                context.Request.Query["from"].ToString(),
                roundUpWholeSecond: false,
                out var from) ||
            !TryCanonicalizeTimeFilter(
                context.Request.Query["to"].ToString(),
                roundUpWholeSecond: true,
                out var to))
        {
            await WriteJsonAsync(
                context, 400, new { error = "time_filter" }).ConfigureAwait(false);
            return;
        }

        var filters = new List<string>();
        using var connection = OpenReadOnly(options.SqlitePath);
        using var command = connection.CreateCommand();
        AddOptionalFilter(command, filters, "occurred_utc >= $from", "$from", from);
        AddOptionalFilter(command, filters, "occurred_utc <= $to", "$to", to);
        AddOptionalFilter(command, filters, "event_type = $type",
            "$type", context.Request.Query["type"].ToString());
        AddOptionalFilter(command, filters, "session_name = $session",
            "$session", context.Request.Query["session"].ToString());
        AddOptionalFilter(command, filters, "supervisor_boot_id = $boot",
            "$boot", context.Request.Query["boot"].ToString());
        AddOptionalFilter(command, filters, "call_id = $call",
            "$call", context.Request.Query["call"].ToString());
        AddOptionalFilter(command, filters, "source_event_id = $source",
            "$source", context.Request.Query["source"].ToString());
        AddOptionalFilter(command, filters, "artifact_id = $artifact",
            "$artifact", context.Request.Query["artifact"].ToString());
        command.CommandText =
            "SELECT event_id, supervisor_boot_id, sequence, schema_version, event_type, " +
            "occurred_utc, observed_utc, session_name, session_generation, outcome_state, " +
            "received_utc, post_gap, call_id, source_event_id, evidence_kind, artifact_id, " +
            "chunk_index, chunk_count, capture_state, " +
            "(SELECT state FROM evidence_delivery_status d " +
            " WHERE d.source_event_id = events.event_id) FROM events" +
            (filters.Count > 0 ? " WHERE " + string.Join(" AND ", filters) : string.Empty) +
            " ORDER BY occurred_utc DESC, sequence DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<object>();
        using (var reader = await command.ExecuteReaderAsync(context.RequestAborted)
                   .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                events.Add(new
                {
                    event_id = reader.GetString(0),
                    supervisor_boot_id = reader.GetString(1),
                    sequence = reader.GetInt64(2),
                    schema_version = reader.GetString(3),
                    event_type = reader.GetString(4),
                    occurred_utc = reader.GetString(5),
                    observed_utc = reader.GetString(6),
                    session_name = reader.IsDBNull(7) ? null : reader.GetString(7),
                    session_generation = reader.IsDBNull(8) ? (long?)null : reader.GetInt64(8),
                    outcome_state = reader.IsDBNull(9) ? null : reader.GetString(9),
                    received_utc = reader.GetString(10),
                    post_gap = reader.GetInt64(11) != 0,
                    call_id = reader.IsDBNull(12) ? null : reader.GetString(12),
                    source_event_id = reader.IsDBNull(13) ? null : reader.GetString(13),
                    evidence_kind = reader.IsDBNull(14) ? null : reader.GetString(14),
                    artifact_id = reader.IsDBNull(15) ? null : reader.GetString(15),
                    chunk_index = reader.IsDBNull(16) ? (long?)null : reader.GetInt64(16),
                    chunk_count = reader.IsDBNull(17) ? (long?)null : reader.GetInt64(17),
                    capture_state = reader.IsDBNull(18) ? null : reader.GetString(18),
                    evidence_delivery_state = reader.IsDBNull(19) ? null : reader.GetString(19),
                });
            }
        }

        await WriteJsonAsync(context, 200, new { events, limit }).ConfigureAwait(false);
    }

    internal static async Task HandleEventDetailAsync(
        HttpContext context,
        SiemReceiverOptions options,
        string eventId)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;
        if (!Guid.TryParseExact(eventId, "D", out var parsedEventId))
        {
            await WriteJsonAsync(
                context, 400, new { error = "event_id" }).ConfigureAwait(false);
            return;
        }

        // Bind the parsed GUID's canonical lowercase form, not the route
        // text: the store holds lowercase and TryParseExact accepts
        // uppercase (cr7-5).
        var canonicalEventId = parsedEventId.ToString("D");

        using var connection = OpenReadOnly(options.SqlitePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT event_id, supervisor_boot_id, sequence, schema_version, event_type, " +
            "occurred_utc, observed_utc, previous_event_hash, event_hash, exact_json_body, " +
            "received_utc FROM events WHERE event_id = $id;";
        command.Parameters.AddWithValue("$id", canonicalEventId);
        string bootId;
        long sequence;
        object detail;
        using (var reader = await command.ExecuteReaderAsync(context.RequestAborted)
                   .ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                await WriteJsonAsync(
                    context, 404, new { error = "unknown_event" }).ConfigureAwait(false);
                return;
            }

            bootId = reader.GetString(1);
            sequence = reader.GetInt64(2);
            detail = new
            {
                event_id = reader.GetString(0),
                supervisor_boot_id = bootId,
                sequence,
                schema_version = reader.GetString(3),
                event_type = reader.GetString(4),
                occurred_utc = reader.GetString(5),
                observed_utc = reader.GetString(6),
                previous_event_hash = reader.IsDBNull(7) ? null : reader.GetString(7),
                event_hash = reader.GetString(8),
                body = Encoding.UTF8.GetString((byte[])reader.GetValue(9)),
                received_utc = reader.GetString(10),
            };
        }

        var neighbors = new List<object>();
        using (var neighborCommand = connection.CreateCommand())
        {
            neighborCommand.CommandText =
                "SELECT event_id, sequence, event_type FROM events " +
                "WHERE supervisor_boot_id = $boot AND sequence IN ($prev, $next);";
            neighborCommand.Parameters.AddWithValue("$boot", bootId);
            neighborCommand.Parameters.AddWithValue("$prev", sequence - 1);
            neighborCommand.Parameters.AddWithValue("$next", sequence + 1);
            using var reader = await neighborCommand.ExecuteReaderAsync(context.RequestAborted)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                neighbors.Add(new
                {
                    event_id = reader.GetString(0),
                    sequence = reader.GetInt64(1),
                    event_type = reader.GetString(2),
                });
            }
        }

        object? chain = null;
        using (var chainCommand = connection.CreateCommand())
        {
            chainCommand.CommandText =
                "SELECT head_sequence, head_event_id, head_event_hash FROM chains " +
                "WHERE supervisor_boot_id = $boot;";
            chainCommand.Parameters.AddWithValue("$boot", bootId);
            using var reader = await chainCommand.ExecuteReaderAsync(context.RequestAborted)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                chain = new
                {
                    head_sequence = reader.GetInt64(0),
                    head_event_id = reader.GetString(1),
                    head_event_hash = reader.GetString(2),
                };
            }
        }

        object? evidenceDelivery = null;
        using (var deliveryCommand = connection.CreateCommand())
        {
            deliveryCommand.CommandText = """
                SELECT expected_chunks, received_chunks, state
                FROM evidence_delivery_status
                WHERE source_event_id = $event_id;
            """;
            deliveryCommand.Parameters.AddWithValue("$event_id", canonicalEventId);
            long expectedChunks = 0;
            long receivedChunks = 0;
            string? deliveryState = null;
            await using (var reader = await deliveryCommand
                             .ExecuteReaderAsync(context.RequestAborted)
                             .ConfigureAwait(false))
            {
                if (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
                {
                    expectedChunks = reader.GetInt64(0);
                    receivedChunks = reader.GetInt64(1);
                    deliveryState = reader.GetString(2);
                }
            }
            if (deliveryState is not null)
            {
                var missing = new List<string>();
                using (var missingCommand = connection.CreateCommand())
                {
                    missingCommand.CommandText = """
                        SELECT m.envelope_event_id
                        FROM evidence_manifest_items m
                        LEFT JOIN events e
                          ON e.event_id = m.envelope_event_id
                         AND e.source_event_id = m.source_event_id
                         AND e.evidence_id = m.evidence_id
                         AND e.evidence_kind = m.evidence_kind
                         AND e.evidence_digest = m.digest
                         AND e.evidence_byte_count = m.byte_count
                         AND e.evidence_encoding = m.encoding
                         AND e.artifact_id = m.artifact_id
                         AND e.artifact_digest = m.artifact_digest
                         AND e.artifact_byte_count = m.artifact_byte_count
                         AND e.chunk_index = m.chunk_index
                         AND e.chunk_count = m.chunk_count
                         AND e.chunk_offset = m.chunk_offset
                         AND e.retention_class = m.retention_class
                         AND e.capture_state = m.capture_state
                        WHERE m.source_event_id = $event_id
                          AND e.event_id IS NULL
                        ORDER BY m.artifact_id, m.chunk_index;
                        """;
                    missingCommand.Parameters.AddWithValue("$event_id", canonicalEventId);
                    using var missingReader = await missingCommand
                        .ExecuteReaderAsync(context.RequestAborted)
                        .ConfigureAwait(false);
                    while (await missingReader.ReadAsync(context.RequestAborted)
                               .ConfigureAwait(false))
                    {
                        missing.Add(missingReader.GetString(0));
                    }
                }
                evidenceDelivery = new
                {
                    expected_chunks = expectedChunks,
                    received_chunks = receivedChunks,
                    state = deliveryState,
                    missing_event_ids = missing,
                };
            }
        }

        await WriteJsonAsync(
            context, 200, new
            {
                @event = detail,
                neighbors,
                chain,
                evidence_delivery = evidenceDelivery,
            }).ConfigureAwait(false);
    }

    internal static async Task HandleEvidenceAsync(
        HttpContext context,
        SiemReceiverOptions options,
        string artifactId)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;
        if (!Guid.TryParseExact(artifactId, "D", out var parsedArtifactId))
        {
            await WriteJsonAsync(
                context, 400, new { error = "artifact_id" }).ConfigureAwait(false);
            return;
        }

        var canonicalArtifactId = parsedArtifactId.ToString("D");
        var chunks = new List<EvidenceChunkRow>();
        using (var connection = OpenReadOnly(options.SqlitePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT event_id, source_event_id, call_id, evidence_id, evidence_kind,
                       evidence_digest, evidence_byte_count, evidence_encoding,
                       artifact_digest, artifact_byte_count, chunk_index, chunk_count,
                       chunk_offset, retention_class, capture_state, exact_json_body
                FROM events
                WHERE artifact_id = $artifact
                ORDER BY chunk_index;
                """;
            command.Parameters.AddWithValue("$artifact", canonicalArtifactId);
            using var reader = await command.ExecuteReaderAsync(context.RequestAborted)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                chunks.Add(new EvidenceChunkRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetInt64(11),
                    reader.GetInt64(12),
                    reader.GetString(13),
                    reader.GetString(14),
                    (byte[])reader.GetValue(15)));
            }
        }

        if (chunks.Count == 0)
        {
            await WriteJsonAsync(
                context, 404, new { error = "not_found" }).ConfigureAwait(false);
            return;
        }

        byte[]? payload = null;
        try
        {
            payload = ReassembleEvidence(canonicalArtifactId, chunks);
            var first = chunks[0];
            var text = new UTF8Encoding(false, true).GetString(payload);
            await WriteJsonAsync(context, 200, new
            {
                evidence = new
                {
                    artifact_id = canonicalArtifactId,
                    source_event_id = first.SourceEventId,
                    call_id = first.CallId,
                    evidence_kind = first.EvidenceKind,
                    encoding = first.Encoding,
                    retention_class = first.RetentionClass,
                    capture_state = first.CaptureState,
                    artifact_digest = first.ArtifactDigest,
                    artifact_byte_count = first.ArtifactByteCount,
                    chunk_count = first.ChunkCount,
                    event_ids = chunks.Select(chunk => chunk.EventId).ToArray(),
                    evidence_ids = chunks.Select(chunk => chunk.EvidenceId).ToArray(),
                    payload_base64 = Convert.ToBase64String(payload),
                    text,
                },
            }).ConfigureAwait(false);
        }
        catch (EvidenceIncompleteException)
        {
            await WriteJsonAsync(
                context, 409, new { error = "evidence_incomplete" }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            InvalidDataException or OverflowException or DecoderFallbackException or JsonException)
        {
            await WriteJsonAsync(
                context, 500, new { error = "evidence_integrity" }).ConfigureAwait(false);
        }
        finally
        {
            if (payload is not null)
                CryptographicOperations.ZeroMemory(payload);
            foreach (var chunk in chunks)
                CryptographicOperations.ZeroMemory(chunk.ExactJsonBody);
        }
    }

    private static byte[] ReassembleEvidence(
        string artifactId,
        IReadOnlyList<EvidenceChunkRow> chunks)
    {
        var first = chunks[0];
        if (first.ChunkCount != chunks.Count ||
            first.ArtifactByteCount < 0 ||
            first.ArtifactByteCount > 128L * 131_072L)
        {
            throw new EvidenceIncompleteException();
        }

        using var buffer = new MemoryStream(checked((int)first.ArtifactByteCount));
        long expectedOffset = 0;
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            if (chunk.ChunkIndex != index ||
                chunk.ChunkCount != first.ChunkCount ||
                chunk.ChunkOffset != expectedOffset ||
                chunk.ArtifactByteCount != first.ArtifactByteCount ||
                !string.Equals(chunk.SourceEventId, first.SourceEventId, StringComparison.Ordinal) ||
                !string.Equals(chunk.CallId, first.CallId, StringComparison.Ordinal) ||
                !string.Equals(chunk.EvidenceKind, first.EvidenceKind, StringComparison.Ordinal) ||
                !string.Equals(chunk.Encoding, first.Encoding, StringComparison.Ordinal) ||
                !string.Equals(chunk.ArtifactDigest, first.ArtifactDigest, StringComparison.Ordinal) ||
                !string.Equals(chunk.RetentionClass, first.RetentionClass, StringComparison.Ordinal) ||
                !string.Equals(chunk.CaptureState, first.CaptureState, StringComparison.Ordinal))
            {
                throw new EvidenceIncompleteException();
            }

            using var document = JsonDocument.Parse(chunk.ExactJsonBody);
            var root = document.RootElement;
            if (!string.Equals(
                    root.GetProperty("artifact_id").GetString(),
                    artifactId,
                    StringComparison.Ordinal) ||
                root.GetProperty("chunk_index").GetInt64() != index ||
                root.GetProperty("chunk_offset").GetInt64() != expectedOffset)
            {
                throw new InvalidDataException("Evidence envelope does not match its index.");
            }

            var bytes = root.GetProperty("payload_base64").GetBytesFromBase64();
            try
            {
                if (bytes.LongLength != chunk.ByteCount ||
                    !string.Equals(
                        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                        chunk.Digest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Evidence chunk digest is invalid.");
                }
                buffer.Write(bytes);
                expectedOffset = checked(expectedOffset + bytes.LongLength);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        var payload = buffer.ToArray();
        if (payload.LongLength != first.ArtifactByteCount ||
            !string.Equals(
                Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                first.ArtifactDigest,
                StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new InvalidDataException("Evidence artifact digest is invalid.");
        }
        return payload;
    }

    private sealed record EvidenceChunkRow(
        string EventId,
        string SourceEventId,
        string? CallId,
        string EvidenceId,
        string EvidenceKind,
        string Digest,
        long ByteCount,
        string Encoding,
        string ArtifactDigest,
        long ArtifactByteCount,
        long ChunkIndex,
        long ChunkCount,
        long ChunkOffset,
        string RetentionClass,
        string CaptureState,
        byte[] ExactJsonBody);

    private sealed class EvidenceIncompleteException : Exception
    {
    }

    internal static async Task HandleChainsAsync(
        HttpContext context,
        SiemReceiverOptions options)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;

        using var connection = OpenReadOnly(options.SqlitePath);
        using var command = connection.CreateCommand();
        // Bounded like every other list (cr7-3): the newest boots window
        // serves triage; growth in retained history must not grow this
        // response. One extra row detects truncation.
        command.CommandText =
            "SELECT c.supervisor_boot_id, c.head_sequence, c.head_event_id, " +
            "c.head_event_hash, COUNT(e.event_id), MAX(e.received_utc) " +
            "FROM chains c LEFT JOIN events e ON e.supervisor_boot_id = c.supervisor_boot_id " +
            "GROUP BY c.supervisor_boot_id " +
            "ORDER BY MAX(e.received_utc) DESC, c.supervisor_boot_id DESC " +
            "LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", MaximumChainLimit + 1);
        var chains = new List<object>();
        using (var reader = await command.ExecuteReaderAsync(context.RequestAborted)
                   .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                chains.Add(new
                {
                    supervisor_boot_id = reader.GetString(0),
                    head_sequence = reader.GetInt64(1),
                    head_event_id = reader.GetString(2),
                    head_event_hash = reader.GetString(3),
                    stored_events = reader.GetInt64(4),
                    last_received_utc = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
            }
        }

        var truncated = chains.Count > MaximumChainLimit;
        if (truncated) chains.RemoveAt(MaximumChainLimit);

        await WriteJsonAsync(context, 200, new { chains, truncated }).ConfigureAwait(false);
    }

    internal static async Task HandleQuarantineAsync(
        HttpContext context,
        SiemReceiverOptions options)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;

        using var connection = OpenReadOnly(options.SqlitePath);
        using var command = connection.CreateCommand();
        // Bounded and blob-free: the list is triage; the raw evidence stays
        // in the store.
        command.CommandText =
            "SELECT attempt_id, failure_code, claimed_event_id, " +
            "claimed_supervisor_boot_id, claimed_sequence, received_utc " +
            "FROM quarantine ORDER BY attempt_id DESC LIMIT 100;";
        var items = new List<object>();
        using (var reader = await command.ExecuteReaderAsync(context.RequestAborted)
                   .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                items.Add(new
                {
                    attempt_id = reader.GetInt64(0),
                    failure_code = reader.GetString(1),
                    claimed_event_id = reader.IsDBNull(2) ? null : reader.GetString(2),
                    claimed_supervisor_boot_id =
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                    claimed_sequence = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4),
                    received_utc = reader.GetString(5),
                });
            }
        }

        await WriteJsonAsync(context, 200, new { items }).ConfigureAwait(false);
    }

    internal static async Task HandleGapsAsync(
        HttpContext context,
        SiemReceiverOptions options)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;

        using var connection = OpenReadOnly(options.SqlitePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT gap_id, supervisor_boot_id, observed_head_sequence, claimed_sequence, " +
            "opened_utc, state, disposition, disposition_actor, disposition_utc, " +
            "resumed_utc, resume_event_id FROM gaps " +
            "ORDER BY gap_id DESC LIMIT 200;";
        var gaps = new List<object>();
        using (var reader = await command.ExecuteReaderAsync(context.RequestAborted)
                   .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                gaps.Add(new
                {
                    gap_id = reader.GetInt64(0),
                    supervisor_boot_id = reader.GetString(1),
                    observed_head_sequence =
                        reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2),
                    claimed_sequence = reader.GetInt64(3),
                    opened_utc = reader.GetString(4),
                    state = reader.GetString(5),
                    disposition = reader.IsDBNull(6) ? null : reader.GetString(6),
                    disposition_actor = reader.IsDBNull(7) ? null : reader.GetString(7),
                    disposition_utc = reader.IsDBNull(8) ? null : reader.GetString(8),
                    resumed_utc = reader.IsDBNull(9) ? null : reader.GetString(9),
                    resume_event_id = reader.IsDBNull(10) ? null : reader.GetString(10),
                });
            }
        }

        await WriteJsonAsync(context, 200, new { gaps }).ConfigureAwait(false);
    }

    /// <summary>The one write this surface owns in S6's first half: the
    /// operator's gap disposition, which is the sole resumption authority.
    /// The transition commits through the store's serialized writer with a
    /// custody entry naming the credential (the operator token's SHA-256,
    /// never the token) and the caller's endpoint.</summary>
    internal static async Task HandleGapDispositionAsync(
        HttpContext context,
        SiemReceiverOptions options,
        long gapId)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;

        var store = context.RequestServices
            .GetService<Storage.SqliteIngestStore>();
        if (store is null)
        {
            await WriteJsonAsync(
                context, 503, new { error = "store_unavailable" }).ConfigureAwait(false);
            return;
        }

        string? disposition = null;
        try
        {
            using var body = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            if (body.RootElement.ValueKind == JsonValueKind.Object &&
                body.RootElement.TryGetProperty("disposition", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                disposition = value.GetString();
            }
        }
        catch (JsonException)
        {
        }

        if (disposition is not ("resolved" or "accepted-loss"))
        {
            await WriteJsonAsync(
                context, 400, new { error = "disposition" }).ConfigureAwait(false);
            return;
        }

        var outcome = await store.DispositionGapAsync(
            gapId,
            disposition,
            OperatorReceipt(context, options),
            context.RequestAborted).ConfigureAwait(false);
        var (status, payload) = outcome switch
        {
            Storage.GapDispositionOutcome.NotFound =>
                (404, (object)new { error = "unknown_gap" }),
            Storage.GapDispositionOutcome.IllegalState =>
                (409, new { error = "illegal_transition" }),
            Storage.GapDispositionOutcome.Resumed =>
                (200, new { gap_id = gapId, state = "resumed", disposition }),
            _ => (200, new { gap_id = gapId, state = "dispositioned", disposition }),
        };
        await WriteJsonAsync(context, status, payload).ConfigureAwait(false);
    }

    internal static async Task HandleAlertsAsync(
        HttpContext context,
        SiemReceiverOptions options)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;

        var stateFilter = context.Request.Query["state"].ToString();
        if (stateFilter is not ("" or "open" or "acknowledged" or "closed"))
        {
            await WriteJsonAsync(
                context, 400, new { error = "state_filter" }).ConfigureAwait(false);
            return;
        }

        using var connection = OpenReadOnly(options.SqlitePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT alert_id, rule_name, subject_kind, subject_id, created_utc, state, " +
            "enqueue_config_hash, evaluation_config_hash, detail, updated_utc, updated_by " +
            "FROM alerts" +
            (stateFilter.Length > 0 ? " WHERE state = $state" : string.Empty) +
            " ORDER BY alert_id DESC LIMIT 200;";
        if (stateFilter.Length > 0)
            command.Parameters.AddWithValue("$state", stateFilter);
        var alerts = new List<object>();
        using (var reader = await command.ExecuteReaderAsync(context.RequestAborted)
                   .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                alerts.Add(new
                {
                    alert_id = reader.GetInt64(0),
                    rule = reader.GetString(1),
                    subject_kind = reader.GetString(2),
                    subject_id = reader.GetString(3),
                    created_utc = reader.GetString(4),
                    state = reader.GetString(5),
                    enqueue_config_hash = reader.GetString(6),
                    evaluation_config_hash = reader.GetString(7),
                    detail = reader.GetString(8),
                    updated_utc = reader.GetString(9),
                    updated_by = reader.IsDBNull(10) ? null : reader.GetString(10),
                });
            }
        }

        await WriteJsonAsync(context, 200, new { alerts }).ConfigureAwait(false);
    }

    /// <summary>The alert-lifecycle API — the sole writer of alert custody
    /// transitions: open → acknowledged → closed, nothing else, rows never
    /// deleted here.</summary>
    internal static async Task HandleAlertTransitionAsync(
        HttpContext context,
        SiemReceiverOptions options,
        long alertId)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;

        var store = context.RequestServices
            .GetService<Storage.SqliteIngestStore>();
        if (store is null)
        {
            await WriteJsonAsync(
                context, 503, new { error = "store_unavailable" }).ConfigureAwait(false);
            return;
        }

        string? targetState = null;
        try
        {
            using var body = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            if (body.RootElement.ValueKind == JsonValueKind.Object &&
                body.RootElement.TryGetProperty("state", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                targetState = value.GetString();
            }
        }
        catch (JsonException)
        {
        }

        if (targetState is not ("acknowledged" or "closed"))
        {
            await WriteJsonAsync(
                context, 400, new { error = "state" }).ConfigureAwait(false);
            return;
        }

        var outcome = await store.TransitionAlertAsync(
            alertId,
            targetState,
            OperatorReceipt(context, options),
            context.RequestAborted).ConfigureAwait(false);
        var (status, payload) = outcome switch
        {
            Storage.AlertTransitionOutcome.NotFound =>
                (404, (object)new { error = "unknown_alert" }),
            Storage.AlertTransitionOutcome.IllegalTransition =>
                (409, new { error = "illegal_transition" }),
            _ => (200, new { alert_id = alertId, state = targetState }),
        };
        await WriteJsonAsync(context, status, payload).ConfigureAwait(false);
    }

    internal static async Task HandleDashboardAsync(
        HttpContext context,
        SiemReceiverOptions options)
    {
        if (!await AdmitSurfaceAsync(context, options).ConfigureAwait(false)) return;
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(DashboardHtml);
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted)
            .ConfigureAwait(false);
    }

    // ---- Plumbing ----

    /// <summary>The custody actor identity for operator writes: the operator
    /// token's SHA-256 (never the token) plus the caller's endpoint.</summary>
    private static Ingest.IngestReceiptContext OperatorReceipt(
        HttpContext context,
        SiemReceiverOptions options)
    {
        var timeProvider = context.RequestServices.GetRequiredService<TimeProvider>();
        var address = context.Connection.RemoteIpAddress;
        var endpoint = address is null
            ? "unknown"
            : $"{address}:{context.Connection.RemotePort}";
        return new Ingest.IngestReceiptContext(
            timeProvider.GetUtcNow().ToUniversalTime(),
            Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(options.OperatorToken)))
                .ToLowerInvariant(),
            endpoint);
    }

    private static SqliteConnection OpenReadOnly(string sqlitePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = sqlitePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();
        return connection;
    }

    private static void AddOptionalFilter(
        SqliteCommand command,
        List<string> filters,
        string clause,
        string parameterName,
        string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        filters.Add(clause);
        command.Parameters.AddWithValue(parameterName, value);
    }

    /// <summary>Empty stays empty (no filter); anything else must parse as a
    /// timestamp or the request is a 400, and binds in the store's own
    /// canonical UTC format. A whole-second upper bound rounds up to the last
    /// tick of its second so it includes the second it names.</summary>
    private static bool TryCanonicalizeTimeFilter(
        string text, bool roundUpWholeSecond, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrEmpty(text)) return true;
        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        if (roundUpWholeSecond && parsed.UtcTicks % TimeSpan.TicksPerSecond == 0)
            parsed = parsed.AddTicks(TimeSpan.TicksPerSecond - 1);
        canonical = parsed.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        return true;
    }

    private static int ParseLimit(string text) =>
        int.TryParse(text, out var limit) && limit is >= 1 and <= MaximumEventLimit
            ? limit
            : DefaultEventLimit;

    private static async Task WriteJsonAsync(HttpContext context, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted)
            .ConfigureAwait(false);
    }

    // No third-party script: the plan sketched htmx, but a static page with
    // inline fetch calls serves the same read-only views without vendoring a
    // dependency into the evidence surface (simplicity rule; same posture as
    // the producer's audit UI).
    private const string DashboardHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>PTK SIEM Receiver</title>
<style>
body{font-family:ui-monospace,Menlo,Consolas,monospace;margin:1.5rem;background:#111;color:#ddd}
h1{font-size:1.2rem} h2{font-size:1rem;margin-top:1.5rem;color:#9cf}
pre{background:#1a1a1a;padding:.75rem;overflow:auto;border-radius:6px}
table{border-collapse:collapse;width:100%} td,th{border-bottom:1px solid #333;padding:.25rem .5rem;text-align:left;font-size:.85rem}
input,select{background:#222;color:#ddd;border:1px solid #444;padding:.3rem;border-radius:4px}
button{background:#265;color:#fff;border:0;padding:.4rem .8rem;border-radius:4px;cursor:pointer}
.warn{color:#fa5}
</style>
</head>
<body>
<h1>PTK SIEM Receiver — stored audit evidence</h1>
<form id="auth" onsubmit="return saveToken(event)" style="display:none">
<input id="tok" type="password" placeholder="operator token" size="40"> <button>Unlock</button>
<span class="warn">token required</span>
</form>
<h2>Chains</h2><pre id="chains">loading…</pre>
<h2>Events</h2>
<form onsubmit="return refreshEvents(event)">
<input id="type" placeholder="event_type"> <input id="session" placeholder="session">
<input id="boot" placeholder="boot id" size="38"> <button>Filter</button>
</form>
<table id="events"><thead><tr><th>occurred</th><th>type</th><th>boot</th><th>seq</th><th>session</th><th>outcome</th></tr></thead><tbody></tbody></table>
<h2>Alerts</h2><pre id="alerts">loading…</pre>
<h2>Gaps</h2><pre id="gaps">loading…</pre>
<h2>Quarantine</h2><pre id="quarantine">loading…</pre>
<script>
let token=sessionStorage.getItem('ptk_operator_token')||'';
const api=(p)=>fetch(p,{headers:{Authorization:'Bearer '+token}});
function saveToken(e){
 e.preventDefault();
 token=document.getElementById('tok').value.trim();
 sessionStorage.setItem('ptk_operator_token',token);
 refresh();
 return false;
}
async function refreshEvents(e){
 if(e)e.preventDefault();
 const q=new URLSearchParams();
 for(const k of ['type','session','boot']){const v=document.getElementById(k).value;if(v)q.set(k,v);}
 const r=await (await api('/api/events?'+q)).json();
 const body=document.querySelector('#events tbody');body.innerHTML='';
 for(const ev of r.events){
  const tr=document.createElement('tr');
  for(const c of [ev.occurred_utc,ev.event_type,ev.supervisor_boot_id,ev.sequence,ev.session_name||'',ev.outcome_state||'']){
   const td=document.createElement('td');td.textContent=c;tr.appendChild(td);}
  tr.title=ev.event_id;body.appendChild(tr);
 }
 return false;
}
async function refresh(){
 const r=await api('/api/chains');
 if(r.status===401){document.getElementById('auth').style.display='';return;}
 document.getElementById('auth').style.display='none';
 const c=await r.json();
 document.getElementById('chains').textContent=JSON.stringify(c.chains,null,1);
 await refreshEvents();
 const a=await (await api('/api/alerts')).json();
 document.getElementById('alerts').textContent=a.alerts.length?JSON.stringify(a.alerts,null,1):'none';
 const g=await (await api('/api/gaps')).json();
 document.getElementById('gaps').textContent=g.gaps.length?JSON.stringify(g.gaps,null,1):'none';
 const q=await (await api('/api/quarantine')).json();
 document.getElementById('quarantine').textContent=q.items.length?JSON.stringify(q.items,null,1):'none';
}
async function loop(){try{await refresh();}finally{setTimeout(loop,10000);}}
loop();
</script>
</body>
</html>
""";
}
