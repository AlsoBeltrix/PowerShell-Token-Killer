using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PtkSiemReceiver.Ingest;

namespace PtkSiemReceiver.Storage;

internal sealed record CustodyHead(long Sequence, string? Hash);

internal sealed record CustodyRestoreRequest(
    string RestoreId,
    string DetectedUtc,
    long PriorCustodySequence,
    string? PriorCustodyHash,
    long RestoredCustodySequence,
    string? RestoredCustodyHash);

internal sealed record CustodyRestoreApplyResult(
    long AlertId,
    CustodyHead Head,
    bool Created);

internal sealed partial class SqliteIngestStore
{
    internal async Task<CustodyHead> ReadCustodyHeadAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return ReadCustodyHead(_writer, transaction: null);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    internal async Task<string?> ReadCustodyHashAsync(
        long sequence,
        CancellationToken cancellationToken)
    {
        if (sequence == 0) return null;
        ThrowIfDisposed();
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var command = CreateCommand(
                _writer,
                null,
                "SELECT receipt_hash FROM custody WHERE receipt_sequence = $sequence;");
            command.Parameters.AddWithValue("$sequence", sequence);
            return command.ExecuteScalar() as string;
        }
        finally
        {
            _writerGate.Release();
        }
    }

    internal async Task<bool> HasCustodyRestoreAsync(
        string restoreId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var command = CreateCommand(
                _writer,
                null,
                "SELECT COUNT(*) FROM custody_restore_events WHERE restore_id = $id;");
            command.Parameters.AddWithValue("$id", restoreId);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        finally
        {
            _writerGate.Release();
        }
    }

    internal async Task<CustodyRestoreApplyResult> ApplyCustodyRestoreAsync(
        CustodyRestoreRequest request,
        IngestReceiptContext operatorReceipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateReceipt(operatorReceipt);
        ThrowIfDisposed();
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _writer.BeginTransaction(deferred: false);
            using (var existing = CreateCommand(_writer, transaction, """
                SELECT alert_id FROM custody_restore_events WHERE restore_id = $id;
                """))
            {
                existing.Parameters.AddWithValue("$id", request.RestoreId);
                var alert = existing.ExecuteScalar();
                if (alert is not null)
                {
                    var existingHead = ReadCustodyHead(_writer, transaction);
                    transaction.Commit();
                    return new CustodyRestoreApplyResult(
                        Convert.ToInt64(alert, CultureInfo.InvariantCulture),
                        existingHead,
                        Created: false);
                }
            }

            var currentHead = ReadCustodyHead(_writer, transaction);
            if (currentHead.Sequence != request.RestoredCustodySequence ||
                !string.Equals(
                    currentHead.Hash,
                    request.RestoredCustodyHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The restored custody head changed before reconciliation.");
            }

            var restoreEvidence = CustodySnapshotBytes(new
            {
                v = 1,
                kind = "restore",
                restore_id = request.RestoreId,
                detected_utc = request.DetectedUtc,
                authorized_utc = FormatUtc(operatorReceipt.ReceivedUtc),
                operator_actor = operatorReceipt.ClientCertificateThumbprint,
                operator_endpoint = operatorReceipt.RemoteEndpoint,
                prior_custody_sequence = request.PriorCustodySequence,
                prior_custody_hash = request.PriorCustodyHash,
                restored_custody_sequence = request.RestoredCustodySequence,
                restored_custody_hash = request.RestoredCustodyHash,
            });
            var restoreReceipt = AppendCustody(
                restoreEvidence,
                operatorReceipt,
                "restore:authorized",
                "restore",
                request.RestoreId,
                transaction);

            const string ruleName = "custody_restore_data_loss";
            const string subjectKind = "custody_restore";
            const string zeroHash =
                "0000000000000000000000000000000000000000000000000000000000000000";
            var createdUtc = FormatUtc(operatorReceipt.ReceivedUtc);
            var detail = JsonSerializer.Serialize(new
            {
                restore_id = request.RestoreId,
                prior_custody_sequence = request.PriorCustodySequence,
                prior_custody_hash = request.PriorCustodyHash,
                restored_custody_sequence = request.RestoredCustodySequence,
                restored_custody_hash = request.RestoredCustodyHash,
                disposition_required = true,
            });
            var workItemId = -restoreReceipt.Sequence;
            using (var insertAlert = CreateCommand(_writer, transaction, """
                INSERT INTO alerts(
                    rule_name, work_item_id, subject_kind, subject_id,
                    created_utc, state, enqueue_config_hash,
                    evaluation_config_hash, detail, updated_utc)
                VALUES(
                    $rule, $work_item_id, $subject_kind, $subject_id,
                    $created_utc, 'open', $config_hash,
                    $config_hash, $detail, $created_utc);
                """))
            {
                insertAlert.Parameters.AddWithValue("$rule", ruleName);
                insertAlert.Parameters.AddWithValue("$work_item_id", workItemId);
                insertAlert.Parameters.AddWithValue("$subject_kind", subjectKind);
                insertAlert.Parameters.AddWithValue("$subject_id", request.RestoreId);
                insertAlert.Parameters.AddWithValue("$created_utc", createdUtc);
                insertAlert.Parameters.AddWithValue("$config_hash", zeroHash);
                insertAlert.Parameters.AddWithValue("$detail", detail);
                if (insertAlert.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Custody restore alert was not inserted.");
            }
            var alertId = Convert.ToInt64(
                ExecuteScalar(_writer, transaction, "SELECT last_insert_rowid();"),
                CultureInfo.InvariantCulture);
            var alertReceipt = AppendCustody(
                CustodySnapshotBytes(new
                {
                    v = 1,
                    kind = "alert",
                    transition = "created",
                    alert_id = alertId,
                    rule = ruleName,
                    work_item_id = workItemId,
                    subject_kind = subjectKind,
                    subject_id = request.RestoreId,
                    created_utc = createdUtc,
                    enqueue_config_hash = zeroHash,
                    evaluation_config_hash = zeroHash,
                    detail,
                }),
                operatorReceipt,
                "alert:created",
                "alert",
                alertId.ToString(CultureInfo.InvariantCulture),
                transaction);

            using (var insertRestore = CreateCommand(_writer, transaction, """
                INSERT INTO custody_restore_events(
                    restore_id, detected_utc, authorized_utc,
                    operator_actor, operator_endpoint,
                    prior_custody_sequence, prior_custody_hash,
                    restored_custody_sequence, restored_custody_hash,
                    custody_sequence, alert_id)
                VALUES(
                    $restore_id, $detected_utc, $authorized_utc,
                    $operator_actor, $operator_endpoint,
                    $prior_sequence, $prior_hash,
                    $restored_sequence, $restored_hash,
                    $custody_sequence, $alert_id);
                """))
            {
                insertRestore.Parameters.AddWithValue("$restore_id", request.RestoreId);
                insertRestore.Parameters.AddWithValue("$detected_utc", request.DetectedUtc);
                insertRestore.Parameters.AddWithValue("$authorized_utc", createdUtc);
                insertRestore.Parameters.AddWithValue(
                    "$operator_actor", operatorReceipt.ClientCertificateThumbprint);
                insertRestore.Parameters.AddWithValue(
                    "$operator_endpoint", operatorReceipt.RemoteEndpoint);
                insertRestore.Parameters.AddWithValue(
                    "$prior_sequence", request.PriorCustodySequence);
                insertRestore.Parameters.AddWithValue(
                    "$prior_hash", (object?)request.PriorCustodyHash ?? DBNull.Value);
                insertRestore.Parameters.AddWithValue(
                    "$restored_sequence", request.RestoredCustodySequence);
                insertRestore.Parameters.AddWithValue(
                    "$restored_hash", (object?)request.RestoredCustodyHash ?? DBNull.Value);
                insertRestore.Parameters.AddWithValue(
                    "$custody_sequence", restoreReceipt.Sequence);
                insertRestore.Parameters.AddWithValue("$alert_id", alertId);
                if (insertRestore.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Custody restore event was not inserted.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new CustodyRestoreApplyResult(
                alertId,
                new CustodyHead(alertReceipt.Sequence, alertReceipt.ReceiptHash),
                Created: true);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    private static CustodyHead ReadCustodyHead(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT receipt_sequence, receipt_hash
            FROM custody ORDER BY receipt_sequence DESC LIMIT 1;
            """);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new CustodyHead(reader.GetInt64(0), reader.GetString(1))
            : new CustodyHead(0, null);
    }
}
