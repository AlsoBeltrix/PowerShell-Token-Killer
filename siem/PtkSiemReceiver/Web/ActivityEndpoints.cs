using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Storage;

namespace PtkSiemReceiver.Web;

/// <summary>
/// Operator-facing activity projection. The immutable event/evidence ledger remains
/// authoritative; this surface correlates it into one row per PTK call and never
/// rewrites, guesses, or caches forensic facts.
/// </summary>
internal static class ActivityEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaximumLimit = 200;
    private const int MaximumFilterLength = 256;

    private static readonly HashSet<string> TerminalEventTypes =
    [
        "call.completed",
        "call.failed",
        "call.not_started",
    ];

    private static readonly HashSet<string> ActivityStates =
    [
        "accepted",
        "completed",
        "failed",
        "canceled",
        "timed_out",
        "outcome_unknown",
        "not_started",
    ];

    internal static void Map(WebApplication application)
    {
        application.MapGet("/api/activities", HandleActivitiesAsync);
        application.MapGet("/api/activities/{activityId}", HandleActivityDetailAsync);
        application.MapGet("/api/health", HandleHealthAsync);
        application.MapGet("/api/quarantine/{attemptId:long}", HandleQuarantineDetailAsync);
    }

    private static async Task HandleActivitiesAsync(
        HttpContext context,
        SiemReceiverOptions options)
    {
        if (!await OperatorEndpoints.AdmitAsync(context, options).ConfigureAwait(false))
            return;

        if (!TryParseListRequest(context.Request, out var request, out var error))
        {
            await OperatorEndpoints.WriteJsonAsync(
                context,
                400,
                new { error }).ConfigureAwait(false);
            return;
        }

        using var connection = OperatorEndpoints.OpenReadOnly(options.SqlitePath);
        var index = await QueryActivityIndexAsync(
            connection,
            request!,
            context.RequestAborted).ConfigureAwait(false);

        var hasMore = index.Count > request!.Limit;
        if (hasMore)
            index.RemoveAt(index.Count - 1);

        var activities = new List<ActivityProjection>(index.Count);
        foreach (var row in index)
        {
            var projection = await LoadProjectionAsync(
                connection,
                row.ActivityId,
                includeRawEvents: false,
                context.RequestAborted).ConfigureAwait(false);
            if (projection is not null)
                activities.Add(projection);
        }

        var nextCursor = hasMore && index.Count > 0
            ? EncodeCursor(index[^1])
            : null;

        await OperatorEndpoints.WriteJsonAsync(context, 200, new
        {
            activities,
            limit = request.Limit,
            next_cursor = nextCursor,
            attribution_notice =
                "Agent and model values are shown only when supplied, with their recorded trust strength; PTK does not infer them.",
        }).ConfigureAwait(false);
    }

    private static async Task HandleActivityDetailAsync(
        HttpContext context,
        SiemReceiverOptions options,
        string activityId)
    {
        if (!await OperatorEndpoints.AdmitAsync(context, options).ConfigureAwait(false))
            return;

        if (!Guid.TryParseExact(activityId, "D", out var parsed))
        {
            await OperatorEndpoints.WriteJsonAsync(
                context,
                400,
                new { error = "activity_id" }).ConfigureAwait(false);
            return;
        }

        using var connection = OperatorEndpoints.OpenReadOnly(options.SqlitePath);
        var projection = await LoadProjectionAsync(
            connection,
            parsed.ToString("D"),
            includeRawEvents: true,
            context.RequestAborted).ConfigureAwait(false);
        if (projection is null)
        {
            await OperatorEndpoints.WriteJsonAsync(
                context,
                404,
                new { error = "unknown_activity" }).ConfigureAwait(false);
            return;
        }

        await OperatorEndpoints.WriteJsonAsync(context, 200, new
        {
            activity = projection,
            evidence_access = new
            {
                authorization = "operator bearer token required",
                content = "Follow each evidence href to retrieve and verify exact retained bytes.",
            },
            system_views = new
            {
                alerts = "/api/alerts",
                gaps = "/api/gaps",
                quarantine = "/api/quarantine",
                raw_events = "/api/events",
            },
        }).ConfigureAwait(false);
    }

    private static async Task HandleHealthAsync(
        HttpContext context,
        SiemReceiverOptions options,
        CustodyHealthState custodyHealth)
    {
        if (!await OperatorEndpoints.AdmitAsync(context, options).ConfigureAwait(false))
            return;

        using var connection = OperatorEndpoints.OpenReadOnly(options.SqlitePath);
        var storedEvents = ScalarLong(connection, "SELECT COUNT(*) FROM events;");
        var storedActivities = ScalarLong(
            connection,
            "SELECT COUNT(DISTINCT call_id) FROM events WHERE call_id IS NOT NULL AND evidence_id IS NULL;");
        var incompleteEvidence = ScalarLong(
            connection,
            "SELECT COUNT(*) FROM evidence_delivery_status WHERE state != 'complete';");
        var evidenceArtifacts = ScalarLong(
            connection,
            "SELECT COUNT(DISTINCT artifact_id) FROM events WHERE artifact_id IS NOT NULL;");
        var openGaps = ScalarLong(
            connection,
            "SELECT COUNT(*) FROM gaps WHERE state != 'resumed';");
        var quarantined = ScalarLong(connection, "SELECT COUNT(*) FROM quarantine;");
        var openAlerts = ScalarLong(
            connection,
            "SELECT COUNT(*) FROM alerts WHERE state != 'closed';");
        var tombstones = ScalarLong(connection, "SELECT COUNT(*) FROM retention_tombstones;");
        var purgedRecords = ScalarLong(
            connection,
            "SELECT COALESCE(SUM(purged_count), 0) FROM retention_tombstones;");
        var latestReceived = ScalarString(
            connection,
            "SELECT MAX(received_utc) FROM events;");

        var custody = custodyHealth.Snapshot;
        var healthy = custody.Healthy && incompleteEvidence == 0 && openGaps == 0 && quarantined == 0;

        await OperatorEndpoints.WriteJsonAsync(context, 200, new
        {
            status = healthy ? "healthy" : "attention_required",
            summary = healthy
                ? "Ingest, evidence delivery, chain integrity, and custody are healthy."
                : "One or more evidence, integrity, quarantine, or custody conditions need review.",
            ingest = new
            {
                status = latestReceived is null ? "waiting_for_first_event" : "records_stored",
                stored_events = storedEvents,
                stored_activities = storedActivities,
                latest_received_utc = latestReceived,
            },
            evidence = new
            {
                status = incompleteEvidence == 0 ? "complete" : "incomplete",
                retained_artifacts = evidenceArtifacts,
                incomplete_manifests = incompleteEvidence,
                explanation = incompleteEvidence == 0
                    ? "Every retained evidence manifest has all expected chunks."
                    : "At least one activity is missing expected evidence chunks; inspect its activity detail.",
            },
            integrity = new
            {
                status = openGaps == 0 && quarantined == 0 ? "intact" : "attention_required",
                open_gaps = openGaps,
                quarantined_attempts = quarantined,
                open_alerts = openAlerts,
            },
            retention = new
            {
                status = options.RetentionMaxAgeDays is null && options.RetentionMaxTotalBytes is null
                    ? "unbounded"
                    : "enforced",
                maximum_age_days = options.RetentionMaxAgeDays,
                maximum_total_bytes = options.RetentionMaxTotalBytes,
                tombstones,
                purged_records = purgedRecords,
                explanation = options.RetentionMaxAgeDays is null && options.RetentionMaxTotalBytes is null
                    ? "No age or total-byte retention bound is configured."
                    : "Configured retention is active; purges remain represented by custody-chained tombstones.",
            },
            custody = new
            {
                status = custody.Healthy ? "healthy" : "attention_required",
                failure_code = custody.FailureCode,
                checked_utc = custody.CheckedUtc,
                custody_sequence = custody.CustodySequence,
                witness_sequence = custody.WitnessSequence,
                restore_pending = custody.RestorePending,
                anchor_configured = custody.AnchorConfigured,
            },
        }).ConfigureAwait(false);
    }

    private static bool TryParseListRequest(
        HttpRequest request,
        out ActivityListRequest? parsed,
        out string error)
    {
        parsed = null;
        error = "activity_filter";

        var limitText = request.Query["limit"].ToString();
        var limit = string.IsNullOrEmpty(limitText)
            ? DefaultLimit
            : int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                && value is >= 1 and <= MaximumLimit
                ? value
                : -1;
        if (limit < 1)
        {
            error = "activity_limit";
            return false;
        }

        if (!TryReadFilter(request, "agent", out var agent) ||
            !TryReadFilter(request, "model", out var model) ||
            !TryReadFilter(request, "client", out var client) ||
            !TryReadFilter(request, "session", out var session) ||
            !TryReadFilter(request, "tool", out var tool) ||
            !TryReadFilter(request, "query", out var query) ||
            !TryReadFilter(request, "state", out var state))
            return false;

        if (state is not null && !ActivityStates.Contains(state))
        {
            error = "activity_state";
            return false;
        }

        if (!TryCanonicalizeTime(request.Query["from"].ToString(), false, out var from) ||
            !TryCanonicalizeTime(request.Query["to"].ToString(), true, out var to))
        {
            error = "activity_time";
            return false;
        }

        ActivityIndexRow? cursor = null;
        var cursorText = request.Query["cursor"].ToString();
        if (!string.IsNullOrEmpty(cursorText) && !TryDecodeCursor(cursorText, out cursor))
        {
            error = "activity_cursor";
            return false;
        }

        parsed = new ActivityListRequest(
            limit, from, to, state, agent, model, client, session, tool, query, cursor);
        return true;
    }

    private static async Task HandleQuarantineDetailAsync(
        HttpContext context,
        SiemReceiverOptions options,
        long attemptId)
    {
        if (!await OperatorEndpoints.AdmitAsync(context, options).ConfigureAwait(false))
            return;
        if (attemptId < 1)
        {
            await OperatorEndpoints.WriteJsonAsync(
                context,
                400,
                new { error = "attempt_id" }).ConfigureAwait(false);
            return;
        }

        using var connection = OperatorEndpoints.OpenReadOnly(options.SqlitePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT failure_code, claimed_event_id, claimed_event_hash,
                   claimed_previous_event_hash, claimed_supervisor_boot_id,
                   claimed_sequence, observed_head_sequence, observed_head_event_hash,
                   raw_request, exact_json_body, received_utc
            FROM quarantine
            WHERE attempt_id = $attempt;
            """;
        command.Parameters.AddWithValue("$attempt", attemptId);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
        {
            await OperatorEndpoints.WriteJsonAsync(
                context,
                404,
                new { error = "unknown_quarantine_attempt" }).ConfigureAwait(false);
            return;
        }

        var rawRequest = (byte[])reader.GetValue(8);
        var exactBody = reader.IsDBNull(9) ? null : (byte[])reader.GetValue(9);
        try
        {
            var exactText = TryDecodeUtf8(exactBody);
            var relatedActivityId = FindRelatedActivityId(
                options.SqlitePath,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                exactText);
            await OperatorEndpoints.WriteJsonAsync(context, 200, new
            {
                attempt = new
                {
                    attempt_id = attemptId,
                    failure_code = reader.GetString(0),
                    claimed_event_id = reader.IsDBNull(1) ? null : reader.GetString(1),
                    claimed_event_hash = reader.IsDBNull(2) ? null : reader.GetString(2),
                    claimed_previous_event_hash = reader.IsDBNull(3) ? null : reader.GetString(3),
                    claimed_supervisor_boot_id = reader.IsDBNull(4) ? null : reader.GetString(4),
                    claimed_sequence = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5),
                    observed_head_sequence = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6),
                    observed_head_event_hash = reader.IsDBNull(7) ? null : reader.GetString(7),
                    received_utc = reader.GetString(10),
                    related_activity_id = relatedActivityId,
                    related_activity_href = relatedActivityId is null
                        ? null
                        : $"/api/activities/{relatedActivityId}",
                },
                evidence = new
                {
                    raw_request_base64 = Convert.ToBase64String(rawRequest),
                    exact_json_body_base64 = exactBody is null
                        ? null
                        : Convert.ToBase64String(exactBody),
                    exact_json_text = exactText,
                    explanation = exactBody is null
                        ? "The receiver could not isolate a record body; raw_request_base64 is the complete rejected input."
                        : "Both the complete rejected input and its isolated record body are retained here.",
                },
            }).ConfigureAwait(false);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(rawRequest);
            if (exactBody is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(exactBody);
        }
    }

    private static string? TryDecodeUtf8(byte[]? bytes)
    {
        if (bytes is null)
            return null;
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? FindRelatedActivityId(
        string sqlitePath,
        string? claimedEventId,
        string? exactJsonText)
    {
        if (claimedEventId is not null)
        {
            using var connection = OperatorEndpoints.OpenReadOnly(sqlitePath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT call_id FROM events WHERE event_id = $event;";
            command.Parameters.AddWithValue("$event", claimedEventId);
            if (command.ExecuteScalar() is string storedCallId)
                return storedCallId;
        }
        if (exactJsonText is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(exactJsonText);
            if (document.RootElement.TryGetProperty("correlation", out var correlation) &&
                correlation.ValueKind == JsonValueKind.Object &&
                correlation.TryGetProperty("call_id", out var callId) &&
                callId.ValueKind == JsonValueKind.String &&
                Guid.TryParseExact(callId.GetString(), "D", out var parsed))
                return parsed.ToString("D");
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static bool TryReadFilter(HttpRequest request, string name, out string? value)
    {
        var candidate = request.Query[name].ToString();
        if (candidate.Length > MaximumFilterLength || candidate.Contains('\0'))
        {
            value = null;
            return false;
        }

        value = string.IsNullOrWhiteSpace(candidate) ? null : candidate.Trim();
        return true;
    }

    private static bool TryCanonicalizeTime(string text, bool roundUpWholeSecond, out string? canonical)
    {
        canonical = null;
        if (string.IsNullOrEmpty(text))
            return true;
        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            return false;
        if (roundUpWholeSecond && parsed.UtcTicks % TimeSpan.TicksPerSecond == 0)
            parsed = parsed.AddTicks(TimeSpan.TicksPerSecond - 1);
        canonical = parsed.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        return true;
    }

    private static async Task<List<ActivityIndexRow>> QueryActivityIndexAsync(
        SqliteConnection connection,
        ActivityListRequest request,
        CancellationToken cancellationToken)
    {
        connection.CreateFunction<string?, string?>(
            "ptk_evidence_text",
            DecodeEvidenceChunkForSearch,
            isDeterministic: true);
        using var command = connection.CreateCommand();
        var filters = new List<string> { "1 = 1" };
        AddFilter(command, filters, "started_utc >= $from", "$from", request.From);
        AddFilter(command, filters, "started_utc <= $to", "$to", request.To);
        AddFilter(command, filters, "state = $state", "$state", request.State);
        AddFilter(command, filters, "agent_name = $agent COLLATE NOCASE", "$agent", request.Agent);
        AddFilter(command, filters,
            "(model_provider = $model COLLATE NOCASE OR model_name = $model COLLATE NOCASE)",
            "$model", request.Model);
        AddFilter(command, filters, "client_name = $client COLLATE NOCASE", "$client", request.Client);
        AddFilter(command, filters, "session_name = $session COLLATE NOCASE", "$session", request.Session);
        AddFilter(command, filters, "tool = $tool COLLATE NOCASE", "$tool", request.Tool);

        if (request.Query is not null)
        {
            filters.Add("instr(lower(search_text), lower($query)) > 0");
            command.Parameters.AddWithValue("$query", request.Query);
        }

        if (request.Cursor is not null)
        {
            filters.Add(
                "(started_utc < $cursor_time OR " +
                "(started_utc = $cursor_time AND activity_id < $cursor_id))");
            command.Parameters.AddWithValue("$cursor_time", request.Cursor.StartedUtc);
            command.Parameters.AddWithValue("$cursor_id", request.Cursor.ActivityId);
        }

        command.CommandText = $$"""
            WITH activity_index AS (
                SELECT
                    call_id AS activity_id,
                    COALESCE(
                        MIN(CASE WHEN event_type = 'call.accepted' THEN occurred_utc END),
                        MIN(occurred_utc)) AS started_utc,
                    COALESCE(
                        MAX(CASE
                            WHEN event_type = 'call.not_started' THEN 'not_started'
                            WHEN event_type IN ('call.completed', 'call.failed') THEN outcome_state
                        END),
                        'accepted') AS state,
                    MAX(json_extract(CAST(exact_json_body AS TEXT), '$.call_attribution.agent_name')) AS agent_name,
                    MAX(json_extract(CAST(exact_json_body AS TEXT), '$.call_attribution.model_provider')) AS model_provider,
                    MAX(json_extract(CAST(exact_json_body AS TEXT), '$.call_attribution.model_name')) AS model_name,
                    MAX(json_extract(CAST(exact_json_body AS TEXT), '$.actor.client_name')) AS client_name,
                    MAX(session_name) AS session_name,
                    MAX(json_extract(CAST(exact_json_body AS TEXT), '$.request.tool')) AS tool,
                    lower(
                        COALESCE(MAX(json_extract(CAST(exact_json_body AS TEXT), '$.call_attribution.agent_name')), '') || ' ' ||
                        COALESCE(MAX(json_extract(CAST(exact_json_body AS TEXT), '$.call_attribution.model_provider')), '') || ' ' ||
                        COALESCE(MAX(json_extract(CAST(exact_json_body AS TEXT), '$.call_attribution.model_name')), '') || ' ' ||
                        COALESCE(MAX(json_extract(CAST(exact_json_body AS TEXT), '$.actor.client_name')), '') || ' ' ||
                        COALESCE(MAX(session_name), '') || ' ' ||
                        COALESCE(MAX(json_extract(CAST(exact_json_body AS TEXT), '$.request.tool')), '') || ' ' ||
                        COALESCE(MAX(json_extract(CAST(exact_json_body AS TEXT), '$.request.action')), '') || ' ' ||
                        COALESCE(MAX(outcome_state), 'accepted') || ' ' ||
                        COALESCE((
                            SELECT ptk_evidence_text(CAST(command_event.exact_json_body AS TEXT))
                            FROM events command_event
                            WHERE command_event.call_id = core.call_id
                              AND command_event.evidence_kind = 'submitted_command'
                            ORDER BY command_event.chunk_index
                            LIMIT 1
                        ), '')) AS search_text
                FROM events core
                WHERE call_id IS NOT NULL AND evidence_id IS NULL
                GROUP BY core.call_id
            )
            SELECT activity_id, started_utc
            FROM activity_index
            WHERE {{string.Join(" AND ", filters)}}
            ORDER BY started_utc DESC, activity_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", request.Limit + 1);

        var rows = new List<ActivityIndexRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new ActivityIndexRow(reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private static string? DecodeEvidenceChunkForSearch(string? body)
    {
        if (body is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("payload_base64", out var payload) ||
                payload.ValueKind != JsonValueKind.String)
                return null;
            var bytes = payload.GetBytesFromBase64();
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }
    }

    private static void AddFilter(
        SqliteCommand command,
        List<string> filters,
        string sql,
        string parameter,
        string? value)
    {
        if (value is null)
            return;
        filters.Add(sql);
        command.Parameters.AddWithValue(parameter, value);
    }

    private static async Task<ActivityProjection?> LoadProjectionAsync(
        SqliteConnection connection,
        string activityId,
        bool includeRawEvents,
        CancellationToken cancellationToken)
    {
        var events = new List<StoredActivityEvent>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT event_id, supervisor_boot_id, sequence, schema_version, event_type,
                       occurred_utc, observed_utc, outcome_state, post_gap, evidence_id,
                       exact_json_body
                FROM events
                WHERE call_id = $call
                ORDER BY occurred_utc, supervisor_boot_id, sequence, event_id;
                """;
            command.Parameters.AddWithValue("$call", activityId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var bodyBytes = (byte[])reader.GetValue(10);
                using var document = JsonDocument.Parse(bodyBytes);
                events.Add(new StoredActivityEvent(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8) != 0,
                    !reader.IsDBNull(9),
                    document.RootElement.Clone()));
            }
        }

        var core = events.Where(item => !item.IsEvidence).ToList();
        if (core.Count == 0)
            return null;

        var evidence = await LoadEvidenceAsync(connection, activityId, cancellationToken)
            .ConfigureAwait(false);
        var accepted = core.FirstOrDefault(item => item.EventType == "call.accepted");
        var terminal = core.LastOrDefault(item => TerminalEventTypes.Contains(item.EventType));
        var facts = FactOrder(core, accepted, terminal).ToList();
        var first = accepted ?? core[0];
        var last = terminal ?? core[^1];

        var state = terminal is null
            ? "accepted"
            : FirstString([terminal], "outcome", "state") ?? terminal.EventType switch
            {
                "call.completed" => "completed",
                "call.failed" => "failed",
                "call.not_started" => "not_started",
                _ => "outcome_unknown",
            };

        var commandEvidence = EvidenceFactFor(evidence, "submitted_command");
        var response = EvidenceFactFor(evidence, "caller_response");
        var output = EvidenceFactFor(evidence, "captured_output");
        var rawEvents = includeRawEvents
            ? events.Select(item => new RawActivityEvent(
                item.EventId,
                item.EventType,
                item.SchemaVersion,
                item.Body)).ToArray()
            : null;

        var bootIds = core.Select(item => item.SupervisorBootId).Distinct(StringComparer.Ordinal).ToArray();
        var chainStatus = bootIds.Length != 1
            ? "multiple_chains"
            : core.Any(item => item.PostGap) ? "post_gap" : "intact";
        var agentName = FirstString(facts, "call_attribution", "agent_name");
        var agentUnavailableReason = FirstString(
            facts,
            "call_attribution",
            "agent_unavailable_reason") ??
            (agentName is null ? "not_recorded_in_event_schema" : null);
        var modelProvider = FirstString(facts, "call_attribution", "model_provider");
        var modelName = FirstString(facts, "call_attribution", "model_name");
        var modelUnavailableReason = FirstString(
            facts,
            "call_attribution",
            "model_unavailable_reason") ??
            (modelName is null ? "not_recorded_in_event_schema" : null);

        return new ActivityProjection(
            activityId,
            accepted?.EventId,
            terminal?.EventId,
            first.OccurredUtc,
            terminal?.OccurredUtc,
            state,
            new ClientFact(
                FirstString(facts, "actor", "client_name"),
                FirstString(facts, "actor", "client_version"),
                FirstString(facts, "actor", "client_session_id"),
                FirstString(facts, "actor", "attribution_strength") ?? "transport_only"),
            new AgentFact(
                agentName,
                agentUnavailableReason),
            new ModelFact(
                modelProvider,
                modelName,
                modelUnavailableReason),
            new AttributionFact(
                FirstString(facts, "call_attribution", "source"),
                FirstString(facts, "call_attribution", "strength") ?? "transport_only"),
            new ClientContextFact(
                FirstString(facts, "client_context", "task_id"),
                FirstString(facts, "client_context", "task_name"),
                FirstLong(facts, "client_context", "mcp_task_ttl_ms"),
                FirstString(facts, "client_context", "task_unavailable_reason"),
                FirstString(facts, "client_context", "run_id"),
                FirstString(facts, "client_context", "run_unavailable_reason"),
                FirstString(facts, "client_context", "source"),
                FirstString(facts, "client_context", "strength") ?? "transport_only"),
            new SessionFact(
                FirstString(facts, "session", "name"),
                FirstLong(facts, "session", "generation")),
            ExecutionContextFactFor(facts),
            new RequestFact(
                FirstString(facts, "request", "tool"),
                FirstString(facts, "request", "action"),
                FirstString(facts, "request", "route") ??
                    FirstString(facts, "routing", "requested_route"),
                FirstLong(facts, "request", "timeout_ms")),
            commandEvidence,
            response,
            output,
            new OutcomeFact(
                FirstLong(facts, "outcome", "exit_code"),
                FirstLong(facts, "outcome", "duration_ms"),
                FirstLong(facts, "outcome", "bytes_returned"),
                FirstString(facts, "outcome", "detail_code")),
            new ChainFact(
                bootIds.Length == 1 ? bootIds[0] : null,
                core.Min(item => item.Sequence),
                core.Max(item => item.Sequence),
                chainStatus),
            evidence,
            rawEvents);
    }

    private static ExecutionContextFact ExecutionContextFactFor(
        IReadOnlyList<StoredActivityEvent> facts)
    {
        var requestedCwd = FirstString(facts, "execution_context", "requested_cwd");
        var effectiveCwd = FirstString(facts, "execution_context", "effective_cwd");
        var repositoryRoot = FirstString(facts, "execution_context", "repository_root");
        return new ExecutionContextFact(
            requestedCwd,
            requestedCwd is null
                ? FirstString(facts, "execution_context", "requested_cwd_unavailable_reason")
                : null,
            effectiveCwd,
            effectiveCwd is null
                ? FirstString(facts, "execution_context", "effective_cwd_unavailable_reason")
                : null,
            repositoryRoot,
            repositoryRoot is null
                ? null
                : FirstString(facts, "execution_context", "repository_relative_path"),
            repositoryRoot is null
                ? FirstString(facts, "execution_context", "repository_unavailable_reason")
                : null);
    }

    private static IEnumerable<StoredActivityEvent> FactOrder(
        IReadOnlyList<StoredActivityEvent> core,
        StoredActivityEvent? accepted,
        StoredActivityEvent? terminal)
    {
        if (accepted is not null)
            yield return accepted;
        if (terminal is not null && terminal != accepted)
            yield return terminal;
        for (var index = core.Count - 1; index >= 0; index--)
        {
            var candidate = core[index];
            if (candidate != accepted && candidate != terminal)
                yield return candidate;
        }
    }

    private static string? FirstString(
        IEnumerable<StoredActivityEvent> events,
        params string[] path)
    {
        foreach (var item in events)
        {
            if (TryGet(item.Body, path, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (text is not null)
                    return text;
            }
        }
        return null;
    }

    private static long? FirstLong(
        IEnumerable<StoredActivityEvent> events,
        params string[] path)
    {
        foreach (var item in events)
        {
            if (TryGet(item.Body, path, out var value) && value.TryGetInt64(out var number))
                return number;
        }
        return null;
    }

    private static bool TryGet(JsonElement root, IReadOnlyList<string> path, out JsonElement value)
    {
        value = root;
        foreach (var name in path)
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(name, out value))
                return false;
        }
        return value.ValueKind != JsonValueKind.Null;
    }

    private static async Task<IReadOnlyList<EvidenceFact>> LoadEvidenceAsync(
        SqliteConnection connection,
        string activityId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH manifest AS (
                SELECT DISTINCT
                    m.envelope_event_id, m.evidence_id, m.evidence_kind,
                    m.encoding, m.artifact_id, m.artifact_digest,
                    m.artifact_byte_count, m.retention_class, m.capture_state
                FROM evidence_manifest_items m
                JOIN events source ON source.event_id = m.source_event_id
                WHERE source.call_id = $call
            )
            SELECT
                m.artifact_id,
                m.evidence_kind,
                m.artifact_digest,
                m.artifact_byte_count,
                m.encoding,
                m.retention_class,
                MIN(m.capture_state),
                MIN(m.evidence_id),
                COUNT(*),
                SUM(CASE WHEN e.event_id IS NULL THEN 0 ELSE 1 END),
                SUM(CASE WHEN e.event_id IS NULL AND t.subject_id IS NOT NULL THEN 1 ELSE 0 END)
            FROM manifest m
            LEFT JOIN events e ON e.event_id = m.envelope_event_id
            LEFT JOIN retention_tombstone_entries t
                ON t.subject_kind = 'event' AND t.subject_id = m.envelope_event_id
            GROUP BY
                m.artifact_id, m.evidence_kind, m.artifact_digest,
                m.artifact_byte_count, m.encoding, m.retention_class
            ORDER BY
                CASE m.evidence_kind
                    WHEN 'submitted_command' THEN 1
                    WHEN 'caller_response' THEN 2
                    WHEN 'captured_output' THEN 3
                    ELSE 4
                END,
                m.artifact_id;
            """;
        command.Parameters.AddWithValue("$call", activityId);
        var evidence = new List<EvidenceFact>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var expected = reader.GetInt64(8);
                var received = reader.GetInt64(9);
                var purged = reader.GetInt64(10);
                var captureState = reader.GetString(6);
                string availability;
                string? reason;
                if (!string.Equals(captureState, "complete", StringComparison.Ordinal))
                {
                    availability = "not_observed";
                    reason = "PTK recorded that capture was incomplete.";
                }
                else if (received == expected)
                {
                    availability = "destination";
                    reason = null;
                }
                else if (received + purged == expected && purged > 0)
                {
                    availability = "retained_then_purged";
                    reason = "The receiver retained this evidence, then removed it under recorded retention policy.";
                }
                else
                {
                    availability = "delivery_incomplete";
                    reason = $"The receiver has {received} of {expected} expected evidence chunks.";
                }

                var artifactId = reader.GetString(0);
                evidence.Add(new EvidenceFact(
                    reader.GetString(7),
                    reader.GetString(1),
                    artifactId,
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    availability,
                    reason,
                    availability == "destination" ? $"/api/evidence/{artifactId}" : null,
                    null));
            }
        }

        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            if (item.Kind == "submitted_command" && item.Availability == "destination")
            {
                evidence[index] = item with
                {
                    Preview = await LoadEvidencePreviewAsync(
                        connection,
                        item.ArtifactId!,
                        cancellationToken).ConfigureAwait(false),
                };
            }
        }
        return evidence;
    }

    private static async Task<string?> LoadEvidencePreviewAsync(
        SqliteConnection connection,
        string artifactId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT exact_json_body
            FROM events
            WHERE artifact_id = $artifact
            ORDER BY chunk_index
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$artifact", artifactId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not byte[] body)
            return null;
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("payload_base64", out var payload) ||
            payload.ValueKind != JsonValueKind.String)
            return null;
        byte[] bytes;
        try
        {
            bytes = payload.GetBytesFromBase64();
        }
        catch (FormatException)
        {
            return null;
        }
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            return text.Length <= 160 ? text : text[..160] + "…";
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static EvidenceFact EvidenceFactFor(
        IReadOnlyList<EvidenceFact> evidence,
        string kind) =>
        evidence.FirstOrDefault(item => item.Kind == kind) ??
        new EvidenceFact(
            null,
            kind,
            null,
            null,
            null,
            null,
            null,
            "not_observed",
            "PTK did not record retained evidence of this kind for the activity.",
            null,
            null);

    private static string EncodeCursor(ActivityIndexRow row)
    {
        var bytes = Encoding.UTF8.GetBytes(row.StartedUtc + "\n" + row.ActivityId);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeCursor(string text, out ActivityIndexRow? row)
    {
        row = null;
        if (text.Length > 256 || text.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return false;
        try
        {
            var normalized = text.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            var separator = decoded.IndexOf('\n', StringComparison.Ordinal);
            if (separator <= 0 || separator == decoded.Length - 1)
                return false;
            var started = decoded[..separator];
            var activity = decoded[(separator + 1)..];
            if (!DateTimeOffset.TryParseExact(
                    started,
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out _) ||
                !Guid.TryParseExact(activity, "D", out _))
                return false;
            row = new ActivityIndexRow(activity, started);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private sealed record ActivityListRequest(
        int Limit,
        string? From,
        string? To,
        string? State,
        string? Agent,
        string? Model,
        string? Client,
        string? Session,
        string? Tool,
        string? Query,
        ActivityIndexRow? Cursor);

    private sealed record ActivityIndexRow(string ActivityId, string StartedUtc);

    private sealed record StoredActivityEvent(
        string EventId,
        string SupervisorBootId,
        long Sequence,
        string SchemaVersion,
        string EventType,
        string OccurredUtc,
        string ObservedUtc,
        string? OutcomeState,
        bool PostGap,
        bool IsEvidence,
        JsonElement Body);
}

internal sealed record ActivityProjection(
    [property: JsonPropertyName("activity_id")] string ActivityId,
    [property: JsonPropertyName("admitted_event_id")] string? AdmittedEventId,
    [property: JsonPropertyName("terminal_event_id")] string? TerminalEventId,
    [property: JsonPropertyName("started_utc")] string StartedUtc,
    [property: JsonPropertyName("finished_utc")] string? FinishedUtc,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("client")] ClientFact Client,
    [property: JsonPropertyName("agent")] AgentFact Agent,
    [property: JsonPropertyName("model")] ModelFact Model,
    [property: JsonPropertyName("attribution")] AttributionFact Attribution,
    [property: JsonPropertyName("client_context")] ClientContextFact ClientContext,
    [property: JsonPropertyName("session")] SessionFact Session,
    [property: JsonPropertyName("context")] ExecutionContextFact Context,
    [property: JsonPropertyName("request")] RequestFact Request,
    [property: JsonPropertyName("command")] EvidenceFact Command,
    [property: JsonPropertyName("response")] EvidenceFact Response,
    [property: JsonPropertyName("output")] EvidenceFact Output,
    [property: JsonPropertyName("outcome")] OutcomeFact Outcome,
    [property: JsonPropertyName("chain")] ChainFact Chain,
    [property: JsonPropertyName("evidence")] IReadOnlyList<EvidenceFact> Evidence,
    [property: JsonPropertyName("raw_events")] IReadOnlyList<RawActivityEvent>? RawEvents);

internal sealed record ClientFact(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("attribution_strength")] string AttributionStrength);

internal sealed record AgentFact(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("unavailable_reason")] string? UnavailableReason);

internal sealed record ModelFact(
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("unavailable_reason")] string? UnavailableReason);

internal sealed record AttributionFact(
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("strength")] string Strength);

internal sealed record ClientContextFact(
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("task_name")] string? TaskName,
    [property: JsonPropertyName("mcp_task_ttl_ms")] long? McpTaskTtlMs,
    [property: JsonPropertyName("task_unavailable_reason")] string? TaskUnavailableReason,
    [property: JsonPropertyName("run_id")] string? RunId,
    [property: JsonPropertyName("run_unavailable_reason")] string? RunUnavailableReason,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("strength")] string Strength);

internal sealed record SessionFact(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("generation")] long? Generation);

internal sealed record ExecutionContextFact(
    [property: JsonPropertyName("requested_cwd")] string? RequestedCwd,
    [property: JsonPropertyName("requested_cwd_unavailable_reason")] string? RequestedCwdUnavailableReason,
    [property: JsonPropertyName("effective_cwd")] string? EffectiveCwd,
    [property: JsonPropertyName("effective_cwd_unavailable_reason")] string? EffectiveCwdUnavailableReason,
    [property: JsonPropertyName("repository")] string? Repository,
    [property: JsonPropertyName("repository_relative_path")] string? RepositoryRelativePath,
    [property: JsonPropertyName("repository_unavailable_reason")] string? RepositoryUnavailableReason);

internal sealed record RequestFact(
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("route")] string? Route,
    [property: JsonPropertyName("timeout_ms")] long? TimeoutMs);

internal sealed record EvidenceFact(
    [property: JsonPropertyName("evidence_id")] string? EvidenceId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("artifact_id")] string? ArtifactId,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("byte_count")] long? ByteCount,
    [property: JsonPropertyName("encoding")] string? Encoding,
    [property: JsonPropertyName("retention_class")] string? RetentionClass,
    [property: JsonPropertyName("availability")] string Availability,
    [property: JsonPropertyName("unavailable_reason")] string? UnavailableReason,
    [property: JsonPropertyName("href")] string? Href,
    [property: JsonPropertyName("preview")] string? Preview);

internal sealed record OutcomeFact(
    [property: JsonPropertyName("exit_code")] long? ExitCode,
    [property: JsonPropertyName("duration_ms")] long? DurationMs,
    [property: JsonPropertyName("bytes_returned")] long? BytesReturned,
    [property: JsonPropertyName("detail_code")] string? DetailCode);

internal sealed record ChainFact(
    [property: JsonPropertyName("boot_id")] string? BootId,
    [property: JsonPropertyName("first_sequence")] long FirstSequence,
    [property: JsonPropertyName("last_sequence")] long LastSequence,
    [property: JsonPropertyName("status")] string Status);

internal sealed record RawActivityEvent(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("body")] JsonElement Body);
