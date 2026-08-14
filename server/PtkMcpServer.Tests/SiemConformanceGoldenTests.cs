using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Tests;

/// <summary>
/// audit-restoration R5 / mini-SIEM S4 fixture gate: the producer-owned
/// golden request corpora. Each golden file holds the EXACT bytes
/// <see cref="HttpAuditDestination"/> sends for a canonical v1/v2 record
/// chain — captured through the real delivery path, never hand-written —
/// and this suite byte-compares the current encoder against those committed
/// files, so wire drift fails HERE, on the producer, naming the file. The
/// receiver's conformance suite consumes the same files as its only source
/// of truth and fails closed when they are absent; receiver-authored copies
/// are never truth. Regenerate deliberately with PTK_WRITE_GOLDEN=1 and
/// review the diff — regeneration is a wire-contract change.
/// </summary>
public sealed class SiemConformanceGoldenTests
{
    internal const string FixtureDirectoryName = "SiemConformance";
    private const string RegenerateVariable = "PTK_WRITE_GOLDEN";

    public static TheoryData<string> GoldenNames => new(
        "otlp-http-v1.golden.json",
        "otlp-http-v2.golden.json",
        "otlp-http-v4.golden.json",
        "otlp-http-evidence-v1.golden.json",
        "splunk-hec-v1.golden.json",
        "splunk-hec-v2.golden.json",
        "splunk-hec-v4.golden.json",
        "splunk-hec-evidence-v1.golden.json");

    [Theory]
    [MemberData(nameof(GoldenNames))]
    public async Task The_current_encoder_matches_the_committed_golden_bytes(string name)
    {
        var actual = await BuildRequestBodyAsync(name);
        var path = Path.Combine(FixtureDirectory(), name);
        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            Directory.CreateDirectory(FixtureDirectory());
            await File.WriteAllBytesAsync(path, actual);
        }
        Assert.True(
            File.Exists(path),
            $"Producer golden fixture missing: {path}. Generate it deliberately " +
            $"({RegenerateVariable}=1) and review the bytes — the corpus is the wire contract.");
        Assert.Equal(await File.ReadAllBytesAsync(path), actual);
    }

    [Fact]
    public void The_corpora_are_chains_with_the_unicode_leg_intact()
    {
        // Three records per corpus, one boot, sequences 1..3, each linking
        // its predecessor's recomputed hash — the receiver's strict chain
        // validation is the consumer that proves the links. Unicode fidelity
        // is a VALUE property: the serializer legally \u-escapes non-ASCII
        // on the wire, so the pin decodes the record and compares the value.
        Assert.Equal(3, V1CorpusRecords().Count);
        Assert.Equal(3, V2CorpusRecords().Count);
        Assert.Equal(3, V4CorpusRecords().Count);
        Assert.Equal(UnicodePurpose, DeclaredPurposeOf(V2CorpusRecords()[2]));
        Assert.Equal(UnicodePurpose, DeclaredPurposeOf(V1CorpusRecords()[2]));
    }

    internal const string UnicodePurpose = "Unicode 検証 — тест ✓ δοκιμή 𝄞";

    private static string? DeclaredPurposeOf(string record)
    {
        using var document = System.Text.Json.JsonDocument.Parse(record);
        return document.RootElement
            .GetProperty("session")
            .GetProperty("declared_purpose")
            .GetString();
    }

    // ---- Corpus builders (shared with nothing; the goldens are the API) ----

    internal static IReadOnlyList<string> V2CorpusRecords()
    {
        var first = AuditCoreSchemaTestRecords.Create();
        var second = AuditCoreSchemaTestRecords.Create(
            includeOptionalQueryValues: false,
            sequence: 2,
            previousEventHash: first.EventHash,
            eventId: AuditCoreSchemaTestRecords.SecondEventId);
        var third = AuditCoreSchemaTestRecords.CreateUnicode(
            sequence: 3,
            previousEventHash: second.EventHash);
        return [Line(first.Utf8Line), Line(second.Utf8Line), Line(third.Utf8Line)];
    }

    internal static IReadOnlyList<string> V1CorpusRecords()
    {
        // A v1 chain links v1 hashes: each successor's v2 form is built with
        // the DOWNGRADED predecessor's recomputed hash before its own
        // downgrade, so the delivered chain verifies end-to-end as v1.
        var first = AuditCoreSchemaTestRecords.ToLegacyV1(
            AuditCoreSchemaTestRecords.Create().Utf8Line);
        var second = AuditCoreSchemaTestRecords.ToLegacyV1(
            AuditCoreSchemaTestRecords.Create(
                includeOptionalQueryValues: false,
                sequence: 2,
                previousEventHash: EmbeddedEventHash(first),
                eventId: AuditCoreSchemaTestRecords.SecondEventId).Utf8Line);
        var third = AuditCoreSchemaTestRecords.ToLegacyV1(
            AuditCoreSchemaTestRecords.CreateUnicode(
                sequence: 3,
                previousEventHash: EmbeddedEventHash(second)).Utf8Line);
        return [Line(first), Line(second), Line(third)];
    }

    internal static IReadOnlyList<string> V4CorpusRecords()
    {
        var first = AuditCoreSchemaTestRecords.CreateV4();
        var second = AuditCoreSchemaTestRecords.CreateV4(
            sequence: 2,
            previousEventHash: first.EventHash,
            eventId: AuditCoreSchemaTestRecords.SecondEventId);
        var third = AuditCoreSchemaTestRecords.CreateV4(
            sequence: 3,
            previousEventHash: second.EventHash,
            eventId: AuditCoreSchemaTestRecords.UnicodeEventId,
            declaredPurpose: UnicodePurpose);
        return [Line(first.Utf8Line), Line(second.Utf8Line), Line(third.Utf8Line)];
    }

    internal static IReadOnlyList<string> EvidenceV1CorpusRecords()
    {
        var chunks = new[]
        {
            Encoding.UTF8.GetBytes("submitted command: Get-Secret Ω\n"),
            Encoding.UTF8.GetBytes("complete output/error: denied ✓\n"),
        };
        var artifactBytes = chunks.SelectMany(bytes => bytes).ToArray();
        var artifactDigest = Digest(artifactBytes);
        var artifactId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var entries = new[]
        {
            Entry(
                "11111111-1111-4111-8111-111111111111",
                "019f5ee1-2384-7eac-8f88-2eb4e7ec5ea1",
                "submitted_command",
                chunks[0],
                artifactId,
                artifactDigest,
                artifactBytes.LongLength,
                0,
                chunks.Length,
                0),
            Entry(
                "22222222-2222-4222-8222-222222222222",
                "019f5ee1-2384-7eac-8f88-2eb4e7ec5ea2",
                "submitted_command",
                chunks[1],
                artifactId,
                artifactDigest,
                artifactBytes.LongLength,
                1,
                chunks.Length,
                chunks[0].LongLength),
        };
        var records = new List<string>(entries.Length);
        string? previousHash = null;
        for (var index = 0; index < entries.Length; index++)
        {
            records.Add(AuditEvidenceEnvelope.CreateRecord(
                entries[index],
                chunks[index],
                Guid.Parse("92874c03-05a7-4aa6-8094-b2e87cad5696"),
                Guid.Parse("7b297f65-646d-4f5d-9a40-f6c7c6ec45b1"),
                "1.2.3",
                Guid.Parse("019f5ee1-2384-7eac-8f88-2eb4e7ec5eaf"),
                Guid.Parse("019f5ee1-2384-7eac-8f88-2eb4e7ec5eb0"),
                DateTimeOffset.Parse(
                    "2026-07-11T12:34:56.1234567Z",
                    System.Globalization.CultureInfo.InvariantCulture),
                previousHash,
                out var eventHash));
            previousHash = eventHash;
        }
        return records;
    }

    private static async Task<byte[]> BuildRequestBodyAsync(string goldenName)
    {
        var records = goldenName.Contains("-evidence-", StringComparison.Ordinal)
            ? EvidenceV1CorpusRecords()
            : goldenName.Contains("-v1.", StringComparison.Ordinal)
            ? V1CorpusRecords()
            : goldenName.Contains("-v4.", StringComparison.Ordinal)
                ? V4CorpusRecords()
                : V2CorpusRecords();
        var kind = goldenName.StartsWith("splunk-hec-", StringComparison.Ordinal)
            ? AuditDestinationKind.SplunkHec
            : AuditDestinationKind.OtlpHttp;
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        using var destination = new HttpAuditDestination(
            new AuditExportSettings(
                kind,
                new Uri("https://conformance.example/"),
                "golden-credential"),
            client);
        var result = await destination.DeliverAsync(records, CancellationToken.None);
        Assert.Equal(AuditDeliveryDisposition.Delivered, result.Disposition);
        Assert.NotNull(handler.Body);
        return handler.Body!;
    }

    private static AuditEvidenceManifestEntry Entry(
        string evidenceId,
        string envelopeEventId,
        string evidenceKind,
        byte[] bytes,
        Guid artifactId,
        string artifactDigest,
        long artifactByteCount,
        int chunkIndex,
        int chunkCount,
        long chunkOffset) => new()
    {
        EvidenceId = Guid.Parse(evidenceId),
        EnvelopeEventId = Guid.Parse(envelopeEventId),
        EvidenceKind = evidenceKind,
        Digest = Digest(bytes),
        ByteCount = bytes.Length,
        Encoding = "utf-8",
        ArtifactId = artifactId,
        ArtifactDigest = artifactDigest,
        ArtifactByteCount = artifactByteCount,
        ChunkIndex = chunkIndex,
        ChunkCount = chunkCount,
        ChunkOffset = chunkOffset,
        RetentionClass = "forensic",
        CaptureState = "complete",
    };

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Line(ReadOnlyMemory<byte> line) =>
        Encoding.UTF8.GetString(line.Span).TrimEnd('\n');

    private static string Line(byte[] line) =>
        Encoding.UTF8.GetString(line).TrimEnd('\n');

    private static string EmbeddedEventHash(byte[] line)
    {
        const string marker = "\"event_hash\":\"";
        var text = Encoding.UTF8.GetString(line);
        var index = text.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(index > 0, "The corpus record carries no event hash.");
        return text.Substring(index + marker.Length, 64);
    }

    private static string FixtureDirectory([CallerFilePath] string sourcePath = "") =>
        Path.Combine(Path.GetDirectoryName(sourcePath)!, FixtureDirectoryName);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
