using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PtkMcpServer.Tests;

public sealed class AuditInteropContractTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex LowerSha256 =
        new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    [Fact]
    public void Splunk_fixture_disables_transport_compression_for_exact_wire_vector()
    {
        var fixture = File.ReadAllText(PathOf("splunk-hec-fixture.yaml"), StrictUtf8);
        var setting = Assert.Single(
            fixture.Split('\n'),
            line => line.TrimStart().StartsWith(
                "disable_compression:",
                StringComparison.Ordinal));
        Assert.Equal("    disable_compression: true", setting);
    }

    [Fact]
    public void Audit_v3_vectors_have_exact_shape_hash_and_host_semantics()
    {
        var nullVector = ReadAuditVector("audit-v3-null.jsonl");
        var liveVector = ReadAuditVector("audit-v3-host.jsonl");
        using (nullVector.Document)
        using (liveVector.Document)
        {
            AssertAuditEnvelope(nullVector);
            AssertAuditEnvelope(liveVector);
            Assert.Equal(
                "host.recovery_failed",
                nullVector.Document.RootElement.GetProperty("event_type").GetString());
            Assert.Equal(
                "host.ready",
                liveVector.Document.RootElement.GetProperty("event_type").GetString());

            var absentHost = nullVector.Document.RootElement.GetProperty("host");
            Assert.Equal(JsonValueKind.Null, absentHost.GetProperty("boot_id").ValueKind);
            Assert.Equal(JsonValueKind.Null, absentHost.GetProperty("generation").ValueKind);
            Assert.Equal("stopped", absentHost.GetProperty("state").GetString());
            Assert.Equal(0, absentHost.GetProperty("recovery_attempt").GetInt64());

            var liveHost = liveVector.Document.RootElement.GetProperty("host");
            Assert.Equal(
                Guid.Parse("33333333-3333-4333-8333-333333333333"),
                liveHost.GetProperty("boot_id").GetGuid());
            Assert.Equal(long.MaxValue, liveHost.GetProperty("generation").GetInt64());
            Assert.Equal(long.MaxValue, liveHost.GetProperty("recovery_attempt").GetInt64());
            Assert.Equal(
                "λ",
                liveVector.Document.RootElement.GetProperty("actor")
                    .GetProperty("client_name").GetString()![^1..]);
        }
    }

    [Fact]
    public void Sentinel_static_vector_derives_every_column_from_raw_event()
    {
        using var validation = ReadStrictJson("adapter-live-validation.json");
        var projection =
            validation.RootElement.GetProperty("sentinel_static_projection_validation");
        Assert.Equal("passed", projection.GetProperty("status").GetString());
        Assert.Equal(
            "static_semantic_projection",
            projection.GetProperty("evidence_kind").GetString());
        Assert.Equal(
            "resolve_each_frozen_column_source_from_the_exact_RawEvent_and_compare_types_and_values",
            projection.GetProperty("method").GetString());
        Assert.Equal(
            "all_19_DCR_columns_equal_the_frozen_source_mapping",
            projection.GetProperty("result").GetString());

        using var dcr = ReadStrictJson("sentinel-dcr.json");
        using var table = ReadStrictJson("sentinel-table.json");
        var dcrColumns = dcr.RootElement.GetProperty("properties")
            .GetProperty("streamDeclarations").GetProperty("Custom-PtkAudit")
            .GetProperty("columns").EnumerateArray().ToArray();
        var tableColumns = table.RootElement.GetProperty("properties")
            .GetProperty("schema").GetProperty("columns").EnumerateArray().ToArray();
        Assert.Equal(19, dcrColumns.Length);
        var columnNames = dcrColumns
            .Select(column => column.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(
            columnNames,
            tableColumns.Select(column => column.GetProperty("name").GetString()));
        Assert.Equal(
            [
                "datetime", "datetime", "string", "string", "string", "string",
                "string", "string", "string", "string", "long", "string", "long",
                "string", "long", "string", "string", "string", "long",
            ],
            dcrColumns.Select(column => column.GetProperty("type").GetString()));
        Assert.Equal(
            [
                "dateTime", "dateTime", "string", "string", "string", "string",
                "string", "string", "string", "string", "long", "string", "long",
                "string", "long", "string", "string", "string", "long",
            ],
            tableColumns.Select(column => column.GetProperty("type").GetString()));

        var columnSources = projection.GetProperty("column_sources");
        AssertPropertyOrder(columnSources, columnNames);
        using var sentinel = ReadStrictJson("sentinel-event.json");
        var mapped = Assert.Single(sentinel.RootElement.EnumerateArray());
        AssertPropertyOrder(mapped, columnNames);

        var exactAuditBytes = ReadWithoutFinalLf("audit-v3-host.jsonl");
        var mappedRawEvent = mapped.GetProperty("RawEvent");
        Assert.Equal(JsonValueKind.String, mappedRawEvent.ValueKind);
        var mappedRawBytes = StrictUtf8.GetBytes(mappedRawEvent.GetString()!);
        Assert.Equal(exactAuditBytes, mappedRawBytes);
        using var rawEvent = JsonDocument.Parse(
            mappedRawBytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        AssertNoDuplicateProperties(rawEvent.RootElement);
        Assert.Equal(
            "contract-vector-λ",
            rawEvent.RootElement.GetProperty("actor")
                .GetProperty("client_name").GetString());

        foreach (var column in dcrColumns)
        {
            var name = column.GetProperty("name").GetString()!;
            var type = column.GetProperty("type").GetString()!;
            var destination = mapped.GetProperty(name);
            var sourcePointer = columnSources.GetProperty(name).GetString()!;
            if (sourcePointer == "$exact_audit_body_utf8_without_final_lf")
            {
                Assert.Equal("RawEvent", name);
                Assert.Equal(JsonValueKind.String, destination.ValueKind);
                continue;
            }

            var source = ResolveJsonPointer(rawEvent.RootElement, sourcePointer);
            if (type is "string" or "datetime")
            {
                Assert.Equal(JsonValueKind.String, source.ValueKind);
                Assert.Equal(JsonValueKind.String, destination.ValueKind);
                Assert.Equal(source.GetString(), destination.GetString());
                if (type == "datetime")
                {
                    Assert.True(destination.TryGetDateTimeOffset(out _));
                }
                continue;
            }

            Assert.Equal("long", type);
            Assert.Equal(JsonValueKind.Number, source.ValueKind);
            Assert.Equal(JsonValueKind.Number, destination.ValueKind);
            Assert.True(source.TryGetInt64(out var sourceValue));
            Assert.True(destination.TryGetInt64(out var destinationValue));
            Assert.Equal(sourceValue, destinationValue);
            Assert.Equal(source.GetRawText(), destination.GetRawText());
        }

        var live = validation.RootElement.GetProperty("sentinel_live_validation");
        Assert.True(live.GetProperty("required_for_release").GetBoolean());
        Assert.False(live.GetProperty("required_in_ordinary_offline_ci").GetBoolean());
        Assert.Equal(
            "not_run_no_azure_validation_tenant",
            live.GetProperty("last_status").GetString());
        using var pins = ReadStrictJson("adapter-pins.json");
        var sentinelPins = pins.RootElement.GetProperty("sentinel");
        Assert.Contains(
            $"api-version={sentinelPins.GetProperty("table_management_api_version").GetString()}",
            live.GetProperty("table_put").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            $"api-version={sentinelPins.GetProperty("dcr_management_api_version").GetString()}",
            live.GetProperty("dcr_put").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            $"api-version={sentinelPins.GetProperty("logs_ingestion_api_version").GetString()}",
            live.GetProperty("ingestion_post").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            sentinelPins.GetProperty("stream").GetString()!,
            live.GetProperty("ingestion_post").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_pins_and_splunk_wire_vector_are_closed()
    {
        using var pins = ReadStrictJson("adapter-pins.json");
        var collector = pins.RootElement.GetProperty("otelcol_contrib");
        Assert.Equal("v0.156.0", collector.GetProperty("version").GetString());
        Assert.Equal(
            "aa158b23c8f89d795b21a05a49b3978565dfebd4",
            collector.GetProperty("release_commit").GetString());
        Assert.Equal(
            "41e24cd516dd69a5b4277465cdb2ff4ef0676f49",
            collector.GetProperty("contrib_source_commit").GetString());
        var archives = collector.GetProperty("archives");
        Assert.Equal(6, archives.EnumerateObject().Count());
        Assert.All(archives.EnumerateObject(), property =>
        {
            AssertPropertyOrder(property.Value, ["url", "sha256"]);
            Assert.StartsWith(
                "https://github.com/open-telemetry/opentelemetry-collector-releases/releases/download/v0.156.0/",
                property.Value.GetProperty("url").GetString(),
                StringComparison.Ordinal);
            Assert.Matches(
                LowerSha256,
                property.Value.GetProperty("sha256").GetString()!);
        });
        Assert.All(
            pins.RootElement.GetProperty("splunk").EnumerateObject()
                .Where(property =>
                    property.Name.EndsWith("source", StringComparison.Ordinal)
                    || property.Name.EndsWith(
                        "documentation",
                        StringComparison.Ordinal)),
            property => Assert.StartsWith(
                "https://",
                property.Value.GetString(),
                StringComparison.Ordinal));

        var splunk = File.ReadAllText(PathOf("splunk-hec-fixture.yaml"), StrictUtf8);
        Assert.Contains(
            "endpoint: https://splunk.example.invalid:8088/services/collector/event",
            splunk,
            StringComparison.Ordinal);
        Assert.Contains("host: host.id", splunk, StringComparison.Ordinal);
        Assert.Contains("not PTK's anchor", splunk, StringComparison.Ordinal);

        var splunkVector = ReadJsonLine("splunk-hec-event.jsonl");
        using (splunkVector.Document)
        {
            var hec = splunkVector.Document.RootElement;
            AssertPropertyOrder(
                hec,
                ["event", "fields", "host", "source", "sourcetype", "index", "time"]);
            Assert.Equal(
                StrictUtf8.GetString(ReadWithoutFinalLf("audit-v3-host.jsonl")),
                hec.GetProperty("event").GetString());
            Assert.Equal(
                "11111111-1111-4111-8111-111111111111",
                hec.GetProperty("host").GetString());
            Assert.Equal("ptk", hec.GetProperty("source").GetString());
            Assert.Equal("ptk:audit", hec.GetProperty("sourcetype").GetString());
            Assert.Equal("ptk", hec.GetProperty("index").GetString());
            Assert.Equal("1784118897.1234567", hec.GetProperty("time").GetRawText());
            var fields = hec.GetProperty("fields");
            Assert.Equal(
                "ptk.audit.host.ready",
                fields.GetProperty("otel.log.name").GetString());
            Assert.Equal(
                "ptk.audit/3",
                fields.GetProperty("ptk.audit.schema_version").GetString());
            Assert.Equal(
                long.MaxValue,
                fields.GetProperty("ptk.host.generation").GetInt64());
            Assert.Equal(
                long.MaxValue,
                fields.GetProperty("ptk.host.recovery_attempt").GetInt64());
            Assert.Equal(
                "33333333-3333-4333-8333-333333333333",
                fields.GetProperty("ptk.host.boot_id").GetString());
            Assert.Equal(
                "ready",
                fields.GetProperty("ptk.host.state").GetString());
            Assert.Equal(
                "completed",
                fields.GetProperty("ptk.outcome.state").GetString());
            Assert.Equal(
                "confirmed",
                fields.GetProperty("ptk.termination.certainty").GetString());
            Assert.Equal(
                "0.2.0-contract-vector",
                fields.GetProperty("service.version").GetString());
        }

        using var validation = ReadStrictJson("adapter-live-validation.json");
        var splunkValidation =
            validation.RootElement.GetProperty("splunk_translator_validation");
        Assert.Equal("passed", splunkValidation.GetProperty("status").GetString());
        Assert.Equal(
            "v0.156.0",
            splunkValidation.GetProperty("collector_version").GetString());
        Assert.Equal(
            "exact_expected_body_match_including_final_lf",
            splunkValidation.GetProperty("result").GetString());
        Assert.True(validation.RootElement.GetProperty("pinned_collector_live_validation")
            .GetProperty("required_for_release").GetBoolean());

        var sentinelPins = pins.RootElement.GetProperty("sentinel");
        Assert.Equal(
            "2025-07-01",
            sentinelPins.GetProperty("table_management_api_version").GetString());
        Assert.Equal("Direct", sentinelPins.GetProperty("dcr_kind").GetString());
        Assert.False(
            sentinelPins.GetProperty("external_data_collection_endpoint_required")
                .GetBoolean());
    }

    private static void AssertAuditEnvelope(AuditVector vector)
    {
        Assert.InRange(vector.ExactLine.Length, 3, 65_536);
        Assert.Equal((byte)'\n', vector.ExactLine[^1]);
        Assert.False(
            vector.ExactLine.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        var root = vector.Document.RootElement;
        AssertPropertyOrder(
            root,
            [
                "schema_version", "event_id", "event_type", "occurred_utc",
                "observed_utc", "producer", "host", "sequence",
                "previous_event_hash", "session", "actor", "correlation", "request",
                "operator_disposition", "routing", "outcome", "coverage", "audit",
                "event_hash",
            ]);
        AssertPropertyOrder(
            root.GetProperty("host"),
            ["boot_id", "generation", "state", "recovery_attempt"]);
        Assert.Equal("ptk.audit/3", root.GetProperty("schema_version").GetString());

        var body = vector.ExactLine.AsSpan(0, vector.ExactLine.Length - 1);
        var eventHash = root.GetProperty("event_hash").GetString()!;
        Assert.Matches(LowerSha256, eventHash);
        var suffix = StrictUtf8.GetBytes($",\"event_hash\":\"{eventHash}\"}}");
        Assert.True(body.EndsWith(suffix));
        var preHash = new byte[body.Length - suffix.Length + 1];
        body[..^suffix.Length].CopyTo(preHash);
        preHash[^1] = (byte)'}';
        Assert.Equal(
            eventHash,
            Convert.ToHexString(SHA256.HashData(preHash)).ToLowerInvariant());
    }

    private static AuditVector ReadAuditVector(string fileName)
    {
        var vector = ReadJsonLine(fileName);
        Assert.InRange(vector.ExactLine.Length, 3, 65_536);
        return vector;
    }

    private static AuditVector ReadJsonLine(string fileName)
    {
        var bytes = File.ReadAllBytes(PathOf(fileName));
        _ = StrictUtf8.GetString(bytes);
        Assert.True(bytes.Length >= 2);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);
        var document = JsonDocument.Parse(
            bytes.AsMemory(0, bytes.Length - 1),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        AssertNoDuplicateProperties(document.RootElement);
        return new AuditVector(bytes, document);
    }

    private static JsonDocument ReadStrictJson(string fileName)
    {
        var bytes = File.ReadAllBytes(PathOf(fileName));
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        _ = StrictUtf8.GetString(bytes);
        var document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        AssertNoDuplicateProperties(document.RootElement);
        return document;
    }

    private static byte[] ReadWithoutFinalLf(string fileName)
    {
        var bytes = File.ReadAllBytes(PathOf(fileName));
        Assert.True(bytes.Length >= 2);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);
        return bytes[..^1];
    }

    private static void AssertNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Assert.True(
                    names.Add(property.Name),
                    $"Duplicate JSON property '{property.Name}'.");
                AssertNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AssertNoDuplicateProperties(item);
            }
        }
    }

    private static void AssertPropertyOrder(
        JsonElement element,
        IEnumerable<string> expected) =>
        Assert.Equal(
            expected,
            element.EnumerateObject().Select(property => property.Name));

    private static JsonElement ResolveJsonPointer(JsonElement root, string pointer)
    {
        Assert.StartsWith("/", pointer, StringComparison.Ordinal);
        var current = root;
        foreach (var token in pointer.Split('/').Skip(1))
        {
            var property = token.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            current = current.GetProperty(property);
        }
        return current;
    }

    private static string PathOf(
        string fileName,
        [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(sourcePath)!,
                "..",
                "Contracts",
                "ResilienceR0",
                fileName));

    private sealed record AuditVector(byte[] ExactLine, JsonDocument Document);
}
