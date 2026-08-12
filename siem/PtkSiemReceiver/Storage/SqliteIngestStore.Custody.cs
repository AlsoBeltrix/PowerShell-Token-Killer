using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PtkSiemReceiver.Ingest;

namespace PtkSiemReceiver.Storage;

internal sealed record CustodyVerificationResult(
    bool Healthy,
    string FailureCode,
    long HeadSequence,
    string? HeadHash,
    long LegacyUnverifiedReceipts)
{
    internal static CustodyVerificationResult Empty { get; } =
        new(true, "healthy", 0, null, 0);
}

internal sealed partial class SqliteIngestStore
{
    private sealed record RetentionEvidenceEntry(
        string SubjectKind,
        string SubjectId,
        long CustodySequence,
        string EvidenceHash,
        string? SupervisorBootId,
        long? ProducerSequence,
        string? PreviousEventHash,
        string? EventHash);

    private sealed record AlertSubjectState(
        string RuleName,
        long WorkItemId,
        string SubjectKind,
        string SubjectId,
        string CreatedUtc,
        string State,
        string EnqueueConfigHash,
        string EvaluationConfigHash,
        string Detail,
        string UpdatedUtc,
        string? UpdatedBy);

    private sealed record GapSubjectState(
        string SupervisorBootId,
        long ClaimedSequence,
        string OpenedUtc,
        string State,
        long? OpeningAttemptId,
        string? Disposition,
        string? DispositionActor,
        string? DispositionEndpoint,
        string? DispositionUtc,
        string? ResumedUtc,
        string? ResumeEventId);

    private sealed record CustodySubjectSnapshot(
        IReadOnlyDictionary<string, string> EventEvidenceHashes,
        IReadOnlyDictionary<string, string> QuarantineEvidenceHashes,
        IReadOnlyDictionary<(string Kind, string Id), long> LatestReceiptSequences,
        IReadOnlyDictionary<string, AlertSubjectState> Alerts,
        IReadOnlyDictionary<string, GapSubjectState> Gaps);

    internal async Task<CustodyVerificationResult> VerifyCustodyAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return VerifyCustodyCore(_writer, _faultInjector);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    /// <summary>
    /// Schema v7 receipts predate retained evidence. Recover the exact bytes
    /// where the immutable source row still exists. Gap/alert lifecycle
    /// snapshots cannot always be reconstructed byte-for-byte after later
    /// transitions; those receipts remain an explicitly counted legacy
    /// prefix until a post-upgrade checkpoint witnesses the chain head.
    /// </summary>
    private static void BackfillLegacyCustodyEvidence(SqliteConnection connection)
    {
        var recovered = new List<(long Sequence, byte[] Evidence)>();
        using (var command = CreateCommand(connection, null, """
            SELECT c.receipt_sequence, e.raw_request
            FROM custody c
            JOIN events e ON c.subject_kind = 'event' AND c.subject_id = e.event_id
            WHERE c.ledger_version = 1 AND c.evidence IS NULL
            UNION ALL
            SELECT c.receipt_sequence, q.raw_request
            FROM custody c
            JOIN quarantine q
              ON c.subject_kind = 'quarantine'
             AND c.subject_id = CAST(q.attempt_id AS TEXT)
            WHERE c.ledger_version = 1 AND c.evidence IS NULL;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                recovered.Add((reader.GetInt64(0), reader.GetFieldValue<byte[]>(1)));
        }

        if (recovered.Count == 0) return;
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var item in recovered)
        {
            using var update = CreateCommand(connection, transaction, """
                UPDATE custody SET evidence_hash = $hash, evidence = $evidence
                WHERE receipt_sequence = $sequence
                  AND ledger_version = 1
                  AND evidence IS NULL;
                """);
            update.Parameters.AddWithValue("$hash", CustodyEvidenceHash.Compute(item.Evidence));
            update.Parameters.AddWithValue("$evidence", item.Evidence);
            update.Parameters.AddWithValue("$sequence", item.Sequence);
            if (update.ExecuteNonQuery() != 1)
                throw new SiemReceiverStartupException("custody_migration");
        }
        transaction.Commit();
    }

    private static CustodyVerificationResult VerifyCustodyCore(
        SqliteConnection connection,
        ISqliteIngestFaultInjector? faultInjector)
    {
        try
        {
            var compacted = ReadCompactedEvidence(connection);
            var subjects = ReadCustodySubjectSnapshot(connection, faultInjector);
            long expectedSequence = 1;
            long legacyUnverified = 0;
            string? previousHash = null;

            using var command = CreateCommand(connection, null, """
                SELECT receipt_sequence, ledger_version, previous_receipt_hash,
                       receipt_hash, received_utc, client_certificate_thumbprint,
                       remote_endpoint, disposition, subject_kind, subject_id,
                       evidence_hash, evidence
                FROM custody
                ORDER BY receipt_sequence ASC;
                """);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var sequence = reader.GetInt64(0);
                var ledgerVersion = reader.GetInt32(1);
                var storedPrevious = reader.IsDBNull(2) ? null : reader.GetString(2);
                var storedHash = reader.GetString(3);
                var receivedUtc = reader.GetString(4);
                var credential = reader.GetString(5);
                var endpoint = reader.GetString(6);
                var disposition = reader.GetString(7);
                var subjectKind = reader.GetString(8);
                var subjectId = reader.GetString(9);
                var evidenceHash = reader.IsDBNull(10) ? null : reader.GetString(10);
                var evidence = reader.IsDBNull(11)
                    ? null
                    : reader.GetFieldValue<byte[]>(11);

                if (sequence != expectedSequence)
                    return Failure("custody_integrity_sequence", sequence, previousHash, legacyUnverified);
                if (!string.Equals(storedPrevious, previousHash, StringComparison.Ordinal))
                    return Failure("custody_integrity_link", sequence, previousHash, legacyUnverified);
                if (!IsLowerHex(storedHash))
                    return Failure("custody_integrity_hash", sequence, previousHash, legacyUnverified);

                string computedHash;
                if (ledgerVersion == 1)
                {
                    if (evidence is null)
                    {
                        legacyUnverified++;
                        computedHash = storedHash;
                    }
                    else
                    {
                        if (evidenceHash is not null &&
                            !string.Equals(
                                evidenceHash,
                                CustodyEvidenceHash.Compute(evidence),
                                StringComparison.Ordinal))
                        {
                            return Failure("custody_integrity_evidence", sequence, previousHash, legacyUnverified);
                        }
                        computedHash = CustodyHash.ComputeV1(
                            sequence,
                            storedPrevious,
                            evidence,
                            receivedUtc,
                            credential,
                            endpoint,
                            disposition,
                            subjectKind,
                            subjectId);
                    }
                }
                else if (ledgerVersion == 2)
                {
                    if (!IsLowerHex(evidenceHash))
                        return Failure("custody_integrity_evidence", sequence, previousHash, legacyUnverified);
                    if (evidence is not null &&
                        !string.Equals(
                            evidenceHash,
                            CustodyEvidenceHash.Compute(evidence),
                            StringComparison.Ordinal))
                    {
                        return Failure("custody_integrity_evidence", sequence, previousHash, legacyUnverified);
                    }
                    if (evidence is null &&
                        (!compacted.TryGetValue(sequence, out var compactedHash) ||
                         !string.Equals(compactedHash, evidenceHash, StringComparison.Ordinal)))
                    {
                        return Failure("custody_integrity_evidence_missing", sequence, previousHash, legacyUnverified);
                    }

                    computedHash = CustodyHash.ComputeV2(
                        sequence,
                        storedPrevious,
                        evidenceHash!,
                        receivedUtc,
                        credential,
                        endpoint,
                        disposition,
                        subjectKind,
                        subjectId);
                }
                else
                {
                    return Failure("custody_integrity_version", sequence, previousHash, legacyUnverified);
                }

                if (!string.Equals(computedHash, storedHash, StringComparison.Ordinal))
                    return Failure("custody_integrity_hash", sequence, previousHash, legacyUnverified);

                if (!VerifySubjectEvidence(
                        subjects,
                        sequence,
                        ledgerVersion,
                        subjectKind,
                        subjectId,
                        evidenceHash,
                        evidence,
                        compacted))
                {
                    return Failure("custody_integrity_subject", sequence, previousHash, legacyUnverified);
                }

                previousHash = storedHash;
                expectedSequence++;
            }

            reader.Close();
            if (!VerifyRetentionTombstones(connection))
                return Failure(
                    "custody_integrity_tombstone",
                    expectedSequence,
                    previousHash,
                    legacyUnverified);

            return new CustodyVerificationResult(
                true,
                legacyUnverified == 0 ? "healthy" : "custody_legacy_unverified",
                expectedSequence - 1,
                previousHash,
                legacyUnverified);
        }
        catch (SiemReceiverStartupException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return new CustodyVerificationResult(false, "custody_integrity_read", 0, null, 0);
        }
    }

    private static Dictionary<long, string> ReadCompactedEvidence(SqliteConnection connection)
    {
        var entries = new Dictionary<long, string>();
        using var command = CreateCommand(connection, null, """
            SELECT custody_sequence, evidence_hash
            FROM retention_tombstone_entries;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!entries.TryAdd(reader.GetInt64(0), reader.GetString(1)))
                throw new SiemReceiverStartupException("custody_integrity_tombstone");
        }
        return entries;
    }

    private static bool VerifyRetentionTombstones(SqliteConnection connection)
    {
        var tombstones = new List<(long Id, string Kind, string? Boot, long? First,
            long? Last, string? PreviousBoundary, string? EventBoundary, long Count,
            string Commitment, long FirstCustody, long LastCustody,
            string? Predecessor, string Successor, string CreatedUtc)>();
        using (var command = CreateCommand(connection, null, """
            SELECT tombstone_id, subject_kind, supervisor_boot_id,
                   first_sequence, last_sequence, boundary_previous_event_hash,
                   boundary_event_hash, purged_count, deleted_commitment,
                   first_custody_sequence, last_custody_sequence,
                   custody_predecessor_hash, custody_successor_hash, created_utc
            FROM retention_tombstones ORDER BY tombstone_id;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                tombstones.Add((
                    reader.GetInt64(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt64(7), reader.GetString(8), reader.GetInt64(9),
                    reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetString(12), reader.GetString(13)));
            }
        }

        foreach (var tombstone in tombstones)
        {
            var entries = new List<RetentionEvidenceEntry>();
            using (var command = CreateCommand(connection, null, """
                SELECT subject_kind, subject_id, custody_sequence, evidence_hash,
                       producer_sequence, previous_event_hash, event_hash
                FROM retention_tombstone_entries
                WHERE tombstone_id = $id ORDER BY custody_sequence;
                """))
            {
                command.Parameters.AddWithValue("$id", tombstone.Id);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    entries.Add(new RetentionEvidenceEntry(
                        reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                        reader.GetString(3), tombstone.Boot,
                        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6)));
                }
            }

            if (entries.Count == 0 || entries.Count != tombstone.Count ||
                entries[0].CustodySequence != tombstone.FirstCustody ||
                entries[^1].CustodySequence != tombstone.LastCustody ||
                entries.Any(entry => entry.SubjectKind != tombstone.Kind) ||
                !string.Equals(
                    CustodyEvidenceHash.Compute(JsonSerializer.SerializeToUtf8Bytes(entries)),
                    tombstone.Commitment,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (tombstone.Kind == "event")
            {
                var bySequence = entries.OrderBy(entry => entry.ProducerSequence).ToArray();
                if (bySequence[0].ProducerSequence != tombstone.First ||
                    bySequence[^1].ProducerSequence != tombstone.Last ||
                    !string.Equals(bySequence[0].PreviousEventHash, tombstone.PreviousBoundary, StringComparison.Ordinal) ||
                    !string.Equals(bySequence[^1].EventHash, tombstone.EventBoundary, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            using var receipt = CreateCommand(connection, null, """
                SELECT receipt_hash, previous_receipt_hash, received_utc, evidence
                FROM custody
                WHERE subject_kind = 'retention_tombstone' AND subject_id = $id;
                """);
            receipt.Parameters.AddWithValue("$id", tombstone.Id.ToString(CultureInfo.InvariantCulture));
            using var receiptReader = receipt.ExecuteReader();
            if (!receiptReader.Read() ||
                !string.Equals(receiptReader.GetString(0), tombstone.Successor, StringComparison.Ordinal) ||
                !string.Equals(
                    receiptReader.IsDBNull(1) ? null : receiptReader.GetString(1),
                    tombstone.Predecessor,
                    StringComparison.Ordinal) ||
                !string.Equals(receiptReader.GetString(2), tombstone.CreatedUtc, StringComparison.Ordinal))
            {
                return false;
            }

            var expectedEvidence = JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = 1,
                kind = "retention_tombstone",
                tombstone_id = tombstone.Id,
                subject_kind = tombstone.Kind,
                supervisor_boot_id = tombstone.Boot,
                purged_count = tombstone.Count,
                deleted_commitment = tombstone.Commitment,
                first_custody_sequence = tombstone.FirstCustody,
                last_custody_sequence = tombstone.LastCustody,
                custody_predecessor_hash = tombstone.Predecessor,
                entries = entries.ToArray(),
                created_utc = tombstone.CreatedUtc,
            });
            if (receiptReader.IsDBNull(3) ||
                !receiptReader.GetFieldValue<byte[]>(3).AsSpan().SequenceEqual(expectedEvidence))
            {
                return false;
            }
        }
        return true;
    }

    private static CustodySubjectSnapshot ReadCustodySubjectSnapshot(
        SqliteConnection connection,
        ISqliteIngestFaultInjector? faultInjector)
    {
        var events = new Dictionary<string, string>(StringComparer.Ordinal);
        faultInjector?.CustodySnapshotQueryForTests("events");
        using (var command = CreateCommand(
                   connection, null, "SELECT event_id, raw_request FROM events;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                events.Add(
                    reader.GetString(0),
                    CustodyEvidenceHash.Compute(reader.GetFieldValue<byte[]>(1)));
            }
        }

        var quarantine = new Dictionary<string, string>(StringComparer.Ordinal);
        faultInjector?.CustodySnapshotQueryForTests("quarantine");
        using (var command = CreateCommand(
                   connection, null, "SELECT attempt_id, raw_request FROM quarantine;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                quarantine.Add(
                    reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                    CustodyEvidenceHash.Compute(reader.GetFieldValue<byte[]>(1)));
            }
        }

        var latest = new Dictionary<(string Kind, string Id), long>();
        faultInjector?.CustodySnapshotQueryForTests("latest_lifecycle");
        using (var command = CreateCommand(connection, null, """
            SELECT subject_kind, subject_id, MAX(receipt_sequence)
            FROM custody
            WHERE subject_kind IN ('alert', 'gap')
            GROUP BY subject_kind, subject_id;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                latest.Add((reader.GetString(0), reader.GetString(1)), reader.GetInt64(2));
        }

        var alerts = new Dictionary<string, AlertSubjectState>(StringComparer.Ordinal);
        faultInjector?.CustodySnapshotQueryForTests("alerts");
        using (var command = CreateCommand(connection, null, """
            SELECT alert_id, rule_name, work_item_id, subject_kind, subject_id,
                   created_utc, state, enqueue_config_hash,
                   evaluation_config_hash, detail, updated_utc, updated_by
            FROM alerts;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                alerts.Add(
                    reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                    new AlertSubjectState(
                        reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                        reader.GetString(4), reader.GetString(5), reader.GetString(6),
                        reader.GetString(7), reader.GetString(8), reader.GetString(9),
                        reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
            }
        }

        var gaps = new Dictionary<string, GapSubjectState>(StringComparer.Ordinal);
        faultInjector?.CustodySnapshotQueryForTests("gaps");
        using (var command = CreateCommand(connection, null, """
            SELECT gap_id, supervisor_boot_id, claimed_sequence, opened_utc, state,
                   opening_attempt_id, disposition, disposition_actor,
                   disposition_endpoint, disposition_utc, resumed_utc,
                   resume_event_id
            FROM gaps;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                gaps.Add(
                    reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                    new GapSubjectState(
                        reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                        reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        reader.IsDBNull(9) ? null : reader.GetString(9),
                        reader.IsDBNull(10) ? null : reader.GetString(10),
                        reader.IsDBNull(11) ? null : reader.GetString(11)));
            }
        }

        return new CustodySubjectSnapshot(events, quarantine, latest, alerts, gaps);
    }

    private static bool VerifySubjectEvidence(
        CustodySubjectSnapshot subjects,
        long custodySequence,
        int ledgerVersion,
        string subjectKind,
        string subjectId,
        string? evidenceHash,
        byte[]? evidence,
        IReadOnlyDictionary<long, string> compacted)
    {
        // Exact evidence was not retained in schema v7. The migration
        // backfills event/quarantine bytes while their source still exists;
        // gap/alert lifecycle snapshots and already-retained sources cannot
        // be reconstructed. Those rows remain explicitly counted as legacy
        // unverified instead of turning a successful schema upgrade into a
        // permanent startup refusal. Every v2 receipt and every recovered v1
        // receipt still takes the full subject check below.
        if (ledgerVersion == 1 && evidence is null) return true;
        if (evidence is null &&
            compacted.TryGetValue(custodySequence, out var compactedHash) &&
            string.Equals(compactedHash, evidenceHash, StringComparison.Ordinal))
        {
            return true;
        }
        if (subjectKind is "alert")
            return VerifyLatestAlertState(
                subjects, custodySequence, subjectId, evidence);
        if (subjectKind is "gap")
            return VerifyLatestGapState(
                subjects, custodySequence, subjectId, evidence);
        if (subjectKind is not ("event" or "quarantine")) return true;
        var source = subjectKind == "event"
            ? subjects.EventEvidenceHashes
            : subjects.QuarantineEvidenceHashes;
        return source.TryGetValue(subjectId, out var rawHash) &&
               string.Equals(rawHash, evidenceHash, StringComparison.Ordinal);
    }

    private static bool VerifyLatestAlertState(
        CustodySubjectSnapshot subjects,
        long custodySequence,
        string subjectId,
        byte[]? evidence)
    {
        if (!IsLatestSubjectReceipt(subjects, custodySequence, "alert", subjectId))
            return true;
        if (evidence is null) return false;
        if (!subjects.Alerts.TryGetValue(subjectId, out var alert)) return false;
        using var document = JsonDocument.Parse(evidence);
        var root = document.RootElement;
        var transition = RequiredJsonString(root, "transition");
        var expectedState = transition switch
        {
            "created" => "open",
            "open->acknowledged" => "acknowledged",
            "acknowledged->closed" => "closed",
            _ => null,
        };
        if (expectedState is null || !string.Equals(alert.State, expectedState, StringComparison.Ordinal))
            return false;
        if (root.GetProperty("alert_id").GetInt64().ToString(CultureInfo.InvariantCulture) != subjectId ||
            !string.Equals(RequiredJsonString(root, "rule"), alert.RuleName, StringComparison.Ordinal) ||
            root.GetProperty("work_item_id").GetInt64() != alert.WorkItemId)
        {
            return false;
        }
        if (transition == "created")
        {
            return string.Equals(RequiredJsonString(root, "subject_kind"), alert.SubjectKind, StringComparison.Ordinal) &&
                   string.Equals(RequiredJsonString(root, "subject_id"), alert.SubjectId, StringComparison.Ordinal) &&
                   string.Equals(RequiredJsonString(root, "created_utc"), alert.CreatedUtc, StringComparison.Ordinal) &&
                   string.Equals(RequiredJsonString(root, "enqueue_config_hash"), alert.EnqueueConfigHash, StringComparison.Ordinal) &&
                   string.Equals(RequiredJsonString(root, "evaluation_config_hash"), alert.EvaluationConfigHash, StringComparison.Ordinal) &&
                   string.Equals(RequiredJsonString(root, "detail"), alert.Detail, StringComparison.Ordinal);
        }
        return string.Equals(RequiredJsonString(root, "utc"), alert.UpdatedUtc, StringComparison.Ordinal) &&
               alert.UpdatedBy is not null &&
               string.Equals(RequiredJsonString(root, "actor"), alert.UpdatedBy, StringComparison.Ordinal);
    }

    private static bool VerifyLatestGapState(
        CustodySubjectSnapshot subjects,
        long custodySequence,
        string subjectId,
        byte[]? evidence)
    {
        if (!IsLatestSubjectReceipt(subjects, custodySequence, "gap", subjectId))
            return true;
        if (evidence is null) return false;
        if (!subjects.Gaps.TryGetValue(subjectId, out var gap)) return false;
        using var document = JsonDocument.Parse(evidence);
        var root = document.RootElement;
        var transition = RequiredJsonString(root, "transition");
        var expectedState = transition switch
        {
            "opened" => "open",
            "resumed" => "resumed",
            "healed" => "resumed",
            _ when transition.StartsWith("dispositioned:", StringComparison.Ordinal) => "dispositioned",
            _ => null,
        };
        if (expectedState is null || !string.Equals(gap.State, expectedState, StringComparison.Ordinal) ||
            root.GetProperty("gap_id").GetInt64().ToString(CultureInfo.InvariantCulture) != subjectId ||
            !string.Equals(RequiredJsonString(root, "supervisor_boot_id"), gap.SupervisorBootId, StringComparison.Ordinal) ||
            root.GetProperty("claimed_sequence").GetInt64() != gap.ClaimedSequence)
        {
            return false;
        }
        return transition switch
        {
            "opened" =>
                string.Equals(RequiredJsonString(root, "opened_utc"), gap.OpenedUtc, StringComparison.Ordinal) &&
                JsonNullableInt64(root, "opening_attempt_id") ==
                    gap.OpeningAttemptId,
            "resumed" or "healed" =>
                gap.ResumedUtc is not null &&
                string.Equals(RequiredJsonString(root, "resumed_utc"), gap.ResumedUtc, StringComparison.Ordinal) &&
                gap.ResumeEventId is not null &&
                string.Equals(RequiredJsonString(root, "resume_event_id"), gap.ResumeEventId, StringComparison.Ordinal),
            _ =>
                gap.Disposition is not null &&
                string.Equals(RequiredJsonString(root, "disposition"), gap.Disposition, StringComparison.Ordinal) &&
                gap.DispositionActor is not null &&
                string.Equals(RequiredJsonString(root, "actor"), gap.DispositionActor, StringComparison.Ordinal) &&
                gap.DispositionEndpoint is not null &&
                string.Equals(RequiredJsonString(root, "endpoint"), gap.DispositionEndpoint, StringComparison.Ordinal) &&
                gap.DispositionUtc is not null &&
                string.Equals(RequiredJsonString(root, "utc"), gap.DispositionUtc, StringComparison.Ordinal),
        };
    }

    private static bool IsLatestSubjectReceipt(
        CustodySubjectSnapshot subjects,
        long custodySequence,
        string subjectKind,
        string subjectId)
    {
        return subjects.LatestReceiptSequences.TryGetValue(
                   (subjectKind, subjectId), out var latest) &&
               latest == custodySequence;
    }

    private static string RequiredJsonString(JsonElement root, string property) =>
        root.GetProperty(property).GetString() ?? throw new JsonException();

    private static long? JsonNullableInt64(JsonElement root, string property)
    {
        var value = root.GetProperty(property);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
    }

    private static CustodyVerificationResult Failure(
        string failureCode,
        long sequence,
        string? previousHash,
        long legacyUnverified) =>
        new(false, failureCode, sequence - 1, previousHash, legacyUnverified);

    private static bool IsLowerHex(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    private static IReadOnlyList<string> ReadStringColumn(SqliteCommand command)
    {
        var values = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private long PurgeSelectedEvents(
        IReadOnlyList<string> eventIds,
        DateTimeOffset utcNow,
        SqliteTransaction transaction)
    {
        if (eventIds.Count == 0) return 0;
        var entries = eventIds
            .Select(id => ReadRetentionEntry("event", id, transaction))
            .ToArray();
        foreach (var group in entries.GroupBy(entry => entry.SupervisorBootId))
            CreateRetentionTombstone("event", group.ToArray(), utcNow, transaction);
        return DeleteSubjects("events", "event_id", eventIds, transaction);
    }

    private long PurgeSelectedQuarantine(
        IReadOnlyList<string> attemptIds,
        DateTimeOffset utcNow,
        SqliteTransaction transaction)
    {
        if (attemptIds.Count == 0) return 0;
        var entries = attemptIds
            .Select(id => ReadRetentionEntry("quarantine", id, transaction))
            .ToArray();
        CreateRetentionTombstone("quarantine", entries, utcNow, transaction);
        return DeleteSubjects("quarantine", "attempt_id", attemptIds, transaction);
    }

    private long PurgeSelectedAlerts(
        IReadOnlyList<string> alertIds,
        DateTimeOffset utcNow,
        SqliteTransaction transaction)
    {
        if (alertIds.Count == 0) return 0;
        var entries = new List<RetentionEvidenceEntry>();
        foreach (var alertId in alertIds)
        {
            using var command = CreateCommand(_writer, transaction, """
                SELECT receipt_sequence, evidence_hash
                FROM custody
                WHERE subject_kind = 'alert' AND subject_id = $id
                ORDER BY receipt_sequence ASC;
                """);
            command.Parameters.AddWithValue("$id", alertId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1))
                    throw new InvalidOperationException("Alert retention lacks verifiable custody evidence.");
                entries.Add(new RetentionEvidenceEntry(
                    "alert", alertId, reader.GetInt64(0), reader.GetString(1),
                    null, null, null, null));
            }
        }
        if (entries.Count == 0)
            throw new InvalidOperationException("Alert retention found no custody lifecycle.");
        CreateRetentionTombstone("alert", entries, utcNow, transaction);
        return DeleteSubjects("alerts", "alert_id", alertIds, transaction);
    }

    private RetentionEvidenceEntry ReadRetentionEntry(
        string subjectKind,
        string subjectId,
        SqliteTransaction transaction)
    {
        var sql = subjectKind == "event"
            ? """
              SELECT c.receipt_sequence, c.evidence_hash,
                     s.supervisor_boot_id, s.sequence,
                     s.previous_event_hash, s.event_hash
              FROM custody c
              JOIN events s ON s.event_id = c.subject_id
              WHERE c.subject_kind = 'event' AND c.subject_id = $id
              ORDER BY c.receipt_sequence DESC LIMIT 1;
              """
            : """
              SELECT c.receipt_sequence, c.evidence_hash,
                     NULL, NULL, NULL, NULL
              FROM custody c
              JOIN quarantine s ON s.attempt_id = $numeric_id
              WHERE c.subject_kind = 'quarantine' AND c.subject_id = $id
              ORDER BY c.receipt_sequence DESC LIMIT 1;
              """;
        using var command = CreateCommand(_writer, transaction, sql);
        command.Parameters.AddWithValue("$id", subjectId);
        if (subjectKind == "quarantine")
            command.Parameters.AddWithValue(
                "$numeric_id", long.Parse(subjectId, CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(1))
            throw new InvalidOperationException("Retention source lacks verifiable custody evidence.");
        return new RetentionEvidenceEntry(
            subjectKind,
            subjectId,
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private void CreateRetentionTombstone(
        string subjectKind,
        IReadOnlyList<RetentionEvidenceEntry> entries,
        DateTimeOffset utcNow,
        SqliteTransaction transaction)
    {
        if (entries.Count == 0) return;
        var ordered = entries.OrderBy(entry => entry.CustodySequence).ToArray();
        var predecessorHash = Convert.ToString(
            ExecuteScalar(
                _writer,
                transaction,
                "SELECT receipt_hash FROM custody ORDER BY receipt_sequence DESC LIMIT 1;"),
            CultureInfo.InvariantCulture);
        var deletedCommitment = CustodyEvidenceHash.Compute(
            JsonSerializer.SerializeToUtf8Bytes(ordered));
        var createdUtc = FormatUtc(utcNow);

        long tombstoneId;
        using (var insert = CreateCommand(_writer, transaction, """
            INSERT INTO retention_tombstones(
                subject_kind, supervisor_boot_id, first_sequence, last_sequence,
                boundary_previous_event_hash, boundary_event_hash, purged_count,
                deleted_commitment, first_custody_sequence, last_custody_sequence,
                custody_predecessor_hash, custody_successor_hash, created_utc)
            VALUES(
                $kind, $boot, $first_sequence, $last_sequence,
                $boundary_previous, $boundary_event, $count,
                $commitment, $first_custody, $last_custody,
                $predecessor, '', $utc);
            """))
        {
            insert.Parameters.AddWithValue("$kind", subjectKind);
            AddNullable(insert, "$boot", ordered[0].SupervisorBootId);
            AddNullable(insert, "$first_sequence", ordered.Min(item => item.ProducerSequence));
            AddNullable(insert, "$last_sequence", ordered.Max(item => item.ProducerSequence));
            AddNullable(insert, "$boundary_previous", ordered
                .OrderBy(item => item.ProducerSequence)
                .First().PreviousEventHash);
            AddNullable(insert, "$boundary_event", ordered
                .OrderBy(item => item.ProducerSequence)
                .Last().EventHash);
            insert.Parameters.AddWithValue("$count", ordered.LongLength);
            insert.Parameters.AddWithValue("$commitment", deletedCommitment);
            insert.Parameters.AddWithValue("$first_custody", ordered[0].CustodySequence);
            insert.Parameters.AddWithValue("$last_custody", ordered[^1].CustodySequence);
            AddNullable(insert, "$predecessor", predecessorHash);
            insert.Parameters.AddWithValue("$utc", createdUtc);
            if (insert.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The retention tombstone was not inserted.");
            tombstoneId = Convert.ToInt64(
                ExecuteScalar(_writer, transaction, "SELECT last_insert_rowid();"),
                CultureInfo.InvariantCulture);
        }

        foreach (var entry in ordered)
        {
            using var insertEntry = CreateCommand(_writer, transaction, """
                INSERT INTO retention_tombstone_entries(
                    tombstone_id, subject_kind, subject_id, custody_sequence,
                    evidence_hash, producer_sequence, previous_event_hash, event_hash)
                VALUES($tombstone, $kind, $id, $custody, $evidence,
                       $producer_sequence, $previous_event, $event_hash);
                """);
            insertEntry.Parameters.AddWithValue("$tombstone", tombstoneId);
            insertEntry.Parameters.AddWithValue("$kind", entry.SubjectKind);
            insertEntry.Parameters.AddWithValue("$id", entry.SubjectId);
            insertEntry.Parameters.AddWithValue("$custody", entry.CustodySequence);
            insertEntry.Parameters.AddWithValue("$evidence", entry.EvidenceHash);
            AddNullable(insertEntry, "$producer_sequence", entry.ProducerSequence);
            AddNullable(insertEntry, "$previous_event", entry.PreviousEventHash);
            AddNullable(insertEntry, "$event_hash", entry.EventHash);
            if (insertEntry.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The retention tombstone entry was not inserted.");
        }

        var receipt = AppendCustody(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = 1,
                kind = "retention_tombstone",
                tombstone_id = tombstoneId,
                subject_kind = subjectKind,
                supervisor_boot_id = ordered[0].SupervisorBootId,
                purged_count = ordered.LongLength,
                deleted_commitment = deletedCommitment,
                first_custody_sequence = ordered[0].CustodySequence,
                last_custody_sequence = ordered[^1].CustodySequence,
                custody_predecessor_hash = predecessorHash,
                entries = ordered,
                created_utc = createdUtc,
            }),
            new IngestReceiptContext(
                utcNow.ToUniversalTime(), new string('0', 64), "receiver"),
            "retention:tombstone",
            "retention_tombstone",
            tombstoneId.ToString(CultureInfo.InvariantCulture),
            transaction);

        using (var update = CreateCommand(_writer, transaction, """
            UPDATE retention_tombstones SET custody_successor_hash = $hash
            WHERE tombstone_id = $id;
            """))
        {
            update.Parameters.AddWithValue("$hash", receipt.ReceiptHash);
            update.Parameters.AddWithValue("$id", tombstoneId);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The retention tombstone successor was not linked.");
        }

        foreach (var entry in ordered)
        {
            using var compact = CreateCommand(_writer, transaction, """
                UPDATE custody SET evidence = NULL
                WHERE receipt_sequence = $sequence AND evidence_hash = $hash;
                """);
            compact.Parameters.AddWithValue("$sequence", entry.CustodySequence);
            compact.Parameters.AddWithValue("$hash", entry.EvidenceHash);
            if (compact.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Custody evidence compaction lost its source receipt.");
        }
    }

    private long DeleteSubjects(
        string table,
        string idColumn,
        IReadOnlyList<string> subjectIds,
        SqliteTransaction transaction)
    {
        if (subjectIds.Count == 0) return 0;
        var parameters = subjectIds
            .Select((_, index) => $"$id{index}")
            .ToArray();
        using var command = CreateCommand(
            _writer,
            transaction,
            $"DELETE FROM {table} WHERE {idColumn} IN ({string.Join(",", parameters)});");
        for (var index = 0; index < subjectIds.Count; index++)
        {
            object value = table == "events"
                ? subjectIds[index]
                : long.Parse(subjectIds[index], CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(parameters[index], value);
        }
        _faultInjector?.RetentionDeleteStatementForTests(table, subjectIds.Count);
        var deleted = command.ExecuteNonQuery();
        if (deleted != subjectIds.Count)
            throw new InvalidOperationException("Retention selection changed inside its transaction.");
        return deleted;
    }
}
