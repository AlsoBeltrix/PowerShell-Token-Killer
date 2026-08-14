using System.Security.Cryptography;

namespace PtkMcpServer.Audit;

internal sealed record AuditEvidencePayload(
    string EvidenceKind,
    string Encoding,
    string RetentionClass,
    string CaptureState,
    byte[] Bytes);

internal sealed record AuditEvidenceManifestEntry
{
    public required Guid EvidenceId { get; init; }
    public required Guid EnvelopeEventId { get; init; }
    public required string EvidenceKind { get; init; }
    public required string Digest { get; init; }
    public required int ByteCount { get; init; }
    public required string Encoding { get; init; }
    public required Guid ArtifactId { get; init; }
    public required string ArtifactDigest { get; init; }
    public required long ArtifactByteCount { get; init; }
    public required int ChunkIndex { get; init; }
    public required int ChunkCount { get; init; }
    public required long ChunkOffset { get; init; }
    public required string RetentionClass { get; init; }
    public required string CaptureState { get; init; }
}

internal sealed record AuditEvidenceBatchPlan(
    IReadOnlyList<ReadOnlyMemory<byte>> Payloads,
    IReadOnlyList<AuditEvidenceChunkPlan> Chunks);

internal sealed record AuditEvidenceChunkPlan
{
    public required Guid EnvelopeEventId { get; init; }
    public required string EvidenceKind { get; init; }
    public required string Encoding { get; init; }
    public required Guid ArtifactId { get; init; }
    public required string ArtifactDigest { get; init; }
    public required long ArtifactByteCount { get; init; }
    public required int ChunkIndex { get; init; }
    public required int ChunkCount { get; init; }
    public required long ChunkOffset { get; init; }
    public required string RetentionClass { get; init; }
    public required string CaptureState { get; init; }
}

internal static class AuditEvidenceManifest
{
    internal const int MaximumEntries = 128;
    internal const int PreferredChunkBytes = 96 * 1024;

    internal static AuditEvidenceManifestEntry ExistingScript(
        ScriptEvidenceReference reference)
    {
        var evidenceId = Guid.Parse(reference.EvidenceId);
        return new AuditEvidenceManifestEntry
        {
            EvidenceId = evidenceId,
            EnvelopeEventId = Guid.CreateVersion7(),
            EvidenceKind = "submitted_command",
            Digest = reference.ScriptDigest,
            ByteCount = reference.ByteLength,
            Encoding = "utf-8",
            ArtifactId = evidenceId,
            ArtifactDigest = reference.ScriptDigest,
            ArtifactByteCount = reference.ByteLength,
            ChunkIndex = 0,
            ChunkCount = 1,
            ChunkOffset = 0,
            RetentionClass = "forensic",
            CaptureState = "complete",
        };
    }

    internal static AuditEvidenceBatchPlan Plan(
        IReadOnlyList<AuditEvidencePayload> artifacts,
        int maximumEvidenceBytes)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (maximumEvidenceBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumEvidenceBytes));

        var chunkBytes = Math.Min(maximumEvidenceBytes, PreferredChunkBytes);
        var payloads = new List<ReadOnlyMemory<byte>>();
        var chunks = new List<AuditEvidenceChunkPlan>();
        foreach (var artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            var artifactId = Guid.NewGuid();
            var artifactDigest = Convert.ToHexString(
                SHA256.HashData(artifact.Bytes)).ToLowerInvariant();
            var count = Math.Max(1, checked((artifact.Bytes.Length + chunkBytes - 1) / chunkBytes));
            if (checked(chunks.Count + count) > MaximumEntries - 1)
                throw new ArgumentOutOfRangeException(
                    nameof(artifacts),
                    "Captured evidence requires too many bounded chunks.");

            for (var index = 0; index < count; index++)
            {
                var offset = checked(index * chunkBytes);
                var length = Math.Min(chunkBytes, artifact.Bytes.Length - offset);
                var payload = artifact.Bytes.AsMemory(offset, Math.Max(0, length));
                payloads.Add(payload);
                chunks.Add(new AuditEvidenceChunkPlan
                {
                    EnvelopeEventId = Guid.CreateVersion7(),
                    EvidenceKind = artifact.EvidenceKind,
                    Encoding = artifact.Encoding,
                    ArtifactId = artifactId,
                    ArtifactDigest = artifactDigest,
                    ArtifactByteCount = artifact.Bytes.LongLength,
                    ChunkIndex = index,
                    ChunkCount = count,
                    ChunkOffset = offset,
                    RetentionClass = artifact.RetentionClass,
                    CaptureState = artifact.CaptureState,
                });
            }
        }

        return new AuditEvidenceBatchPlan(payloads, chunks);
    }

    internal static IReadOnlyList<AuditEvidenceManifestEntry> Bind(
        AuditEvidenceBatchPlan plan,
        IReadOnlyList<ScriptEvidenceReference> references)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(references);
        if (plan.Chunks.Count != references.Count)
            throw new InvalidOperationException("Evidence publication changed batch cardinality.");

        return plan.Chunks.Select((chunk, index) =>
        {
            var reference = references[index];
            return new AuditEvidenceManifestEntry
            {
                EvidenceId = Guid.Parse(reference.EvidenceId),
                EnvelopeEventId = chunk.EnvelopeEventId,
                EvidenceKind = chunk.EvidenceKind,
                Digest = reference.ScriptDigest,
                ByteCount = reference.ByteLength,
                Encoding = chunk.Encoding,
                ArtifactId = chunk.ArtifactId,
                ArtifactDigest = chunk.ArtifactDigest,
                ArtifactByteCount = chunk.ArtifactByteCount,
                ChunkIndex = chunk.ChunkIndex,
                ChunkCount = chunk.ChunkCount,
                ChunkOffset = chunk.ChunkOffset,
                RetentionClass = chunk.RetentionClass,
                CaptureState = chunk.CaptureState,
            };
        }).ToArray();
    }
}
