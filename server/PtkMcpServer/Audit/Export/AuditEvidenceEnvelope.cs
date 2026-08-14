using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PtkMcpServer.Audit.Export;

internal static class AuditEvidenceEnvelope
{
    internal const string SchemaVersion = "ptk.evidence/1";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryExpand(
        IReadOnlyList<string> auditRecords,
        ScriptEvidenceStoreProvider evidence,
        out IReadOnlyList<string> deliveryRecords,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(auditRecords);
        ArgumentNullException.ThrowIfNull(evidence);
        var expanded = new List<string>(auditRecords.Count);
        failure = null;
        try
        {
            foreach (var auditRecord in auditRecords)
            {
                expanded.Add(auditRecord);
                using var document = JsonDocument.Parse(auditRecord);
                var root = document.RootElement;
                if (!string.Equals(
                        root.GetProperty("schema_version").GetString(),
                        AuditEventSerializer.FullEvidenceSchemaVersion,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                AppendEvidence(root, evidence, expanded);
            }
            deliveryRecords = expanded;
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            deliveryRecords = [];
            failure = exception is ScriptEvidenceStorageException
                ? "export.evidence_unavailable"
                : "export.evidence_invalid";
            return false;
        }
    }

    internal static bool IsEvidenceRecord(string record)
    {
        try
        {
            using var document = JsonDocument.Parse(record);
            return string.Equals(
                document.RootElement.GetProperty("schema_version").GetString(),
                SchemaVersion,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return false;
        }
    }

    internal static string CreateRecord(
        AuditEvidenceManifestEntry entry,
        ReadOnlySpan<byte> payload,
        Guid hostId,
        Guid sourceBootId,
        string producerVersion,
        Guid sourceEventId,
        Guid? callId,
        DateTimeOffset observedUtc,
        string? previousHash,
        out string eventHash)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        var item = new EvidenceManifestItem(
            entry.EvidenceId.ToString("D"),
            entry.EnvelopeEventId.ToString("D"),
            entry.EvidenceKind,
            entry.Digest,
            entry.ByteCount,
            entry.Encoding,
            entry.ArtifactId.ToString("D"),
            entry.ArtifactDigest,
            entry.ArtifactByteCount,
            entry.ChunkIndex,
            entry.ChunkCount,
            entry.ChunkOffset,
            entry.RetentionClass,
            entry.CaptureState);
        var bytes = payload.ToArray();
        try
        {
            var envelope = Serialize(
                item,
                bytes,
                hostId.ToString("D"),
                sourceBootId.ToString("D"),
                producerVersion,
                sourceEventId.ToString("D"),
                callId?.ToString("D"),
                observedUtc.ToUniversalTime().ToString(
                    TimestampFormat, CultureInfo.InvariantCulture),
                previousHash);
            eventHash = envelope.Hash;
            return envelope.Record;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void AppendEvidence(
        JsonElement root,
        ScriptEvidenceStoreProvider evidence,
        ICollection<string> expanded)
    {
        var producer = root.GetProperty("producer");
        var hostId = RequireString(producer, "host_id");
        var sourceBootId = RequireString(producer, "supervisor_boot_id");
        var producerVersion = RequireString(producer, "version");
        var sourceEventId = RequireString(root, "event_id");
        var observedUtc = RequireString(root, "observed_utc");
        var callId = root.GetProperty("correlation").GetProperty("call_id").GetString();
        var manifest = root.GetProperty("evidence_manifest")
            .EnumerateArray()
            .Select(ParseManifest)
            .ToArray();

        foreach (var artifact in manifest.GroupBy(item => item.ArtifactId))
        {
            var ordered = artifact.OrderBy(item => item.ChunkIndex).ToArray();
            var payloads = new List<byte[]>(ordered.Length);
            try
            {
                foreach (var item in ordered)
                {
                    byte[]? payload = null;
                    var reference = evidence.ReadExactForExport(
                        item.EvidenceId,
                        bytes => payload = bytes.ToArray());
                    if (payload is null ||
                        reference.ByteLength != item.ByteCount ||
                        !string.Equals(reference.ScriptDigest, item.Digest, StringComparison.Ordinal))
                    {
                        throw new IOException("Stored evidence does not match its manifest.");
                    }
                    payloads.Add(payload);
                }

                var combinedLength = payloads.Sum(payload => checked((long)payload.Length));
                if (combinedLength != ordered[0].ArtifactByteCount)
                    throw new IOException("Evidence chunks do not reassemble to the manifest byte count.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var payload in payloads)
                    hash.AppendData(payload);
                var artifactDigest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(
                        artifactDigest,
                        ordered[0].ArtifactDigest,
                        StringComparison.Ordinal))
                {
                    throw new IOException("Evidence chunks do not reassemble to the manifest digest.");
                }

                string? previousHash = null;
                for (var index = 0; index < ordered.Length; index++)
                {
                    var envelope = Serialize(
                        ordered[index],
                        payloads[index],
                        hostId,
                        sourceBootId,
                        producerVersion,
                        sourceEventId,
                        callId,
                        observedUtc,
                        previousHash);
                    expanded.Add(envelope.Record);
                    previousHash = envelope.Hash;
                }
            }
            finally
            {
                foreach (var payload in payloads)
                    CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    private static EvidenceManifestItem ParseManifest(JsonElement item) => new(
        RequireString(item, "evidence_id"),
        RequireString(item, "envelope_event_id"),
        RequireString(item, "evidence_kind"),
        RequireString(item, "digest"),
        item.GetProperty("byte_count").GetInt32(),
        RequireString(item, "encoding"),
        RequireString(item, "artifact_id"),
        RequireString(item, "artifact_digest"),
        item.GetProperty("artifact_byte_count").GetInt64(),
        item.GetProperty("chunk_index").GetInt32(),
        item.GetProperty("chunk_count").GetInt32(),
        item.GetProperty("chunk_offset").GetInt64(),
        RequireString(item, "retention_class"),
        RequireString(item, "capture_state"));

    private static SerializedEnvelope Serialize(
        EvidenceManifestItem item,
        byte[] payload,
        string hostId,
        string sourceBootId,
        string producerVersion,
        string sourceEventId,
        string? callId,
        string observedUtc,
        string? previousHash)
    {
        var hashInput = SerializeCore(
            item,
            payload,
            hostId,
            sourceBootId,
            producerVersion,
            sourceEventId,
            callId,
            observedUtc,
            previousHash,
            eventHash: null);
        var hash = Convert.ToHexString(SHA256.HashData(hashInput)).ToLowerInvariant();
        var record = SerializeCore(
            item,
            payload,
            hostId,
            sourceBootId,
            producerVersion,
            sourceEventId,
            callId,
            observedUtc,
            previousHash,
            hash);
        return new SerializedEnvelope(StrictUtf8.GetString(record), hash);
    }

    private static byte[] SerializeCore(
        EvidenceManifestItem item,
        byte[] payload,
        string hostId,
        string sourceBootId,
        string producerVersion,
        string sourceEventId,
        string? callId,
        string observedUtc,
        string? previousHash,
        string? eventHash)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SchemaVersion);
            writer.WriteString("event_id", item.EnvelopeEventId);
            writer.WriteString("event_type", $"evidence.{item.EvidenceKind}");
            writer.WriteString("occurred_utc", observedUtc);
            writer.WriteString("observed_utc", observedUtc);
            writer.WriteStartObject("producer");
            writer.WriteString("host_id", hostId);
            writer.WriteString("supervisor_boot_id", sourceBootId);
            writer.WriteString("version", producerVersion);
            writer.WriteEndObject();
            writer.WriteStartObject("stream");
            writer.WriteString("stream_id", item.ArtifactId);
            writer.WriteNumber("sequence", item.ChunkIndex + 1L);
            WriteString(writer, "previous_event_hash", previousHash);
            writer.WriteEndObject();
            writer.WriteString("source_event_id", sourceEventId);
            WriteString(writer, "call_id", callId);
            writer.WriteString("evidence_id", item.EvidenceId);
            writer.WriteString("evidence_kind", item.EvidenceKind);
            writer.WriteString("digest", item.Digest);
            writer.WriteNumber("byte_count", item.ByteCount);
            writer.WriteString("encoding", item.Encoding);
            writer.WriteString("artifact_id", item.ArtifactId);
            writer.WriteString("artifact_digest", item.ArtifactDigest);
            writer.WriteNumber("artifact_byte_count", item.ArtifactByteCount);
            writer.WriteNumber("chunk_index", item.ChunkIndex);
            writer.WriteNumber("chunk_count", item.ChunkCount);
            writer.WriteNumber("chunk_offset", item.ChunkOffset);
            writer.WriteString("retention_class", item.RetentionClass);
            writer.WriteString("capture_state", item.CaptureState);
            writer.WriteBase64String("payload_base64", payload);
            if (eventHash is not null)
                writer.WriteString("event_hash", eventHash);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static string RequireString(JsonElement parent, string property) =>
        parent.GetProperty(property).GetString() ??
        throw new IOException($"Evidence manifest property '{property}' is missing.");

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record EvidenceManifestItem(
        string EvidenceId,
        string EnvelopeEventId,
        string EvidenceKind,
        string Digest,
        int ByteCount,
        string Encoding,
        string ArtifactId,
        string ArtifactDigest,
        long ArtifactByteCount,
        int ChunkIndex,
        int ChunkCount,
        long ChunkOffset,
        string RetentionClass,
        string CaptureState);

    private readonly record struct SerializedEnvelope(string Record, string Hash);
}
