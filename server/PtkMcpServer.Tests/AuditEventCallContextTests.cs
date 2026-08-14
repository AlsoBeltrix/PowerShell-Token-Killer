using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditEventCallContextTests
{
    [Fact]
    public void V4_serializes_attribution_client_and_execution_context_separately()
    {
        var serialized = AuditCoreSchemaTestRecords.CreateV4();
        using var document = JsonDocument.Parse(serialized.Utf8Line);
        var root = document.RootElement;

        Assert.Equal("ptk.audit/4", root.GetProperty("schema_version").GetString());
        Assert.Equal(
            [
                "schema_version", "event_id", "event_type", "occurred_utc", "observed_utc",
                "producer", "sequence", "previous_event_hash", "session", "actor",
                "call_attribution", "client_context", "execution_context", "correlation",
                "request", "operator_disposition", "routing", "outcome", "coverage",
                "audit", "event_hash",
            ],
            root.EnumerateObject().Select(property => property.Name).ToArray());

        var actor = root.GetProperty("actor");
        Assert.Equal("test-client", actor.GetProperty("client_name").GetString());
        Assert.False(actor.TryGetProperty("agent_name", out _));
        Assert.False(actor.TryGetProperty("model_name", out _));

        var attribution = root.GetProperty("call_attribution");
        Assert.Equal("codex", attribution.GetProperty("agent_name").GetString());
        Assert.Equal("openai", attribution.GetProperty("model_provider").GetString());
        Assert.Equal("gpt-5.6-sol", attribution.GetProperty("model_name").GetString());
        Assert.Equal("client", attribution.GetProperty("source").GetString());
        Assert.Equal("client_asserted", attribution.GetProperty("strength").GetString());

        var context = root.GetProperty("client_context");
        Assert.Equal("task-17", context.GetProperty("task_id").GetString());
        Assert.Equal("run-29", context.GetProperty("run_id").GetString());
        Assert.Equal(120_000, context.GetProperty("mcp_task_ttl_ms").GetInt64());

        var execution = root.GetProperty("execution_context");
        Assert.Equal("/tmp/work", execution.GetProperty("requested_cwd").GetString());
        Assert.Equal("/tmp/work", execution.GetProperty("effective_cwd").GetString());
        Assert.Equal("/tmp", execution.GetProperty("repository_root").GetString());
        Assert.Equal("work", execution.GetProperty("repository_relative_path").GetString());

        Assert.Equal(serialized.EventHash, EmbeddedHash(serialized.Utf8Line));
        Assert.Equal(serialized.EventHash, RecomputedHash(serialized.Utf8Line));
    }

    [Fact]
    public void Client_assertions_cannot_be_serialized_as_authenticated()
    {
        var exception = Assert.Throws<AuditEventValidationException>(
            () => AuditCoreSchemaTestRecords.CreateV4(
                attributionStrength: "authenticated"));

        Assert.Contains("call_attribution.strength", exception.Message, StringComparison.Ordinal);
    }

    private static string EmbeddedHash(ReadOnlyMemory<byte> line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("event_hash").GetString()!;
    }

    private static string RecomputedHash(ReadOnlyMemory<byte> line)
    {
        var text = Encoding.UTF8.GetString(line.Span).TrimEnd('\n');
        const string marker = ",\"event_hash\":\"";
        var markerIndex = text.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex > 0);
        var preHash = text[..markerIndex] + "}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preHash)))
            .ToLowerInvariant();
    }
}
