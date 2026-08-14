using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

#pragma warning disable MCPEXP001

public sealed class AuditCallAttributionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 13, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Namespaced_call_context_is_captured_as_client_asserted()
    {
        var call = InvokeCall();
        call.Meta = new JsonObject
        {
            [AuditCallMetadataCapture.CallContextMetadataKey] = new JsonObject
            {
                ["agent_name"] = "codex",
                ["model_provider"] = "openai",
                ["model_name"] = "gpt-5.6-sol",
                ["task_id"] = "task-17",
                ["task_name"] = "SIEM attribution",
                ["run_id"] = "run-29",
            },
        };
        call.Task = new McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(2) };

        Assert.True(Capture(call, out var metadata, out var failure));
        Assert.Null(failure);
        Assert.Equal("codex", metadata!.Attribution.AgentName);
        Assert.Null(metadata.Attribution.AgentUnavailableReason);
        Assert.Equal("openai", metadata.Attribution.ModelProvider);
        Assert.Equal("gpt-5.6-sol", metadata.Attribution.ModelName);
        Assert.Null(metadata.Attribution.ModelUnavailableReason);
        Assert.Equal("client", metadata.Attribution.Source);
        Assert.Equal("client_asserted", metadata.Attribution.Strength);
        Assert.Equal("task-17", metadata.ClientContext.TaskId);
        Assert.Equal("SIEM attribution", metadata.ClientContext.TaskName);
        Assert.Equal("run-29", metadata.ClientContext.RunId);
        Assert.Equal(120_000, metadata.ClientContext.McpTaskTtlMs);

        Assert.Equal("test-client", metadata.Actor.ClientName);
        Assert.Equal("1.2.3", metadata.Actor.ClientVersion);
        Assert.Equal("initialize-session", metadata.Actor.ClientSessionId);
        Assert.NotEqual(metadata.Actor.ClientName, metadata.Attribution.AgentName);
        Assert.NotEqual("authenticated", metadata.Attribution.Strength);
    }

    [Fact]
    public void Transport_deserialized_meta_uses_the_same_contract()
    {
        var call = JsonSerializer.Deserialize<CallToolRequestParams>(
            """
            {
              "name": "ptk_invoke",
              "arguments": { "script": "Get-Date" },
              "_meta": {
                "io.github.also-beltrix.ptk/call-context/v1": {
                  "agent_name": "claude-code",
                  "model_provider": "anthropic",
                  "model_name": "claude-fable-5",
                  "run_id": "run-serialized"
                }
              }
            }
            """);

        Assert.NotNull(call);
        Assert.True(Capture(call, out var metadata, out var failure));
        Assert.Null(failure);
        Assert.Equal("claude-code", metadata!.Attribution.AgentName);
        Assert.Equal("anthropic", metadata.Attribution.ModelProvider);
        Assert.Equal("claude-fable-5", metadata.Attribution.ModelName);
        Assert.Equal("run-serialized", metadata.ClientContext.RunId);
    }

    [Fact]
    public void Omitted_call_context_has_explicit_identity_absence_reasons()
    {
        Assert.True(Capture(InvokeCall(), out var metadata, out var failure));

        Assert.Null(failure);
        Assert.Null(metadata!.Attribution.AgentName);
        Assert.Equal("not_supplied_by_client", metadata.Attribution.AgentUnavailableReason);
        Assert.Null(metadata.Attribution.ModelProvider);
        Assert.Null(metadata.Attribution.ModelName);
        Assert.Equal("not_supplied_by_client", metadata.Attribution.ModelUnavailableReason);
        Assert.Null(metadata.Attribution.Source);
        Assert.Equal("transport_only", metadata.Attribution.Strength);
        Assert.Null(metadata.ClientContext.TaskId);
        Assert.Null(metadata.ClientContext.TaskName);
        Assert.Null(metadata.ClientContext.RunId);
        Assert.Null(metadata.ClientContext.McpTaskTtlMs);
    }

    [Fact]
    public void Partial_identity_labels_each_missing_identity_without_guessing()
    {
        var call = InvokeCall();
        call.Meta = new JsonObject
        {
            [AuditCallMetadataCapture.CallContextMetadataKey] = new JsonObject
            {
                ["model_provider"] = "anthropic",
            },
        };

        Assert.True(Capture(call, out var metadata, out var failure));

        Assert.Null(failure);
        Assert.Null(metadata!.Attribution.AgentName);
        Assert.Equal("not_supplied_by_client", metadata.Attribution.AgentUnavailableReason);
        Assert.Equal("anthropic", metadata.Attribution.ModelProvider);
        Assert.Null(metadata.Attribution.ModelName);
        Assert.Equal("not_supplied_by_client", metadata.Attribution.ModelUnavailableReason);
        Assert.Equal("client", metadata.Attribution.Source);
        Assert.Equal("client_asserted", metadata.Attribution.Strength);
    }

    [Fact]
    public void Malformed_or_unbounded_namespaced_context_fails_without_echoing_values()
    {
        const string secret = "sensitive-model-label";
        var wrongKind = InvokeCall();
        wrongKind.Meta = new JsonObject
        {
            [AuditCallMetadataCapture.CallContextMetadataKey] = secret,
        };
        AssertRejected(wrongKind, "must be an object", secret);

        var unknownField = InvokeCall();
        unknownField.Meta = new JsonObject
        {
            [AuditCallMetadataCapture.CallContextMetadataKey] = new JsonObject
            {
                ["agent_name"] = "codex",
                ["future_secret"] = secret,
            },
        };
        AssertRejected(unknownField, "unknown field", secret);

        var tooLong = InvokeCall();
        tooLong.Meta = new JsonObject
        {
            [AuditCallMetadataCapture.CallContextMetadataKey] = new JsonObject
            {
                ["model_name"] = new string('x', 257) + secret,
            },
        };
        AssertRejected(tooLong, "not representable", secret);
    }

    [Fact]
    public void Execution_context_uses_effective_path_and_bounded_repository_identity()
    {
        using var root = new TemporaryDirectory("ptk-audit-context-");
        Directory.CreateDirectory(Path.Combine(root.Path, ".git"));
        var workingDirectory = Directory.CreateDirectory(
            Path.Combine(root.Path, "src", "module")).FullName;

        var context = AuditExecutionContextCapture.Capture(
            requestedCwd: "/client/requested/path",
            effectiveCwd: workingDirectory);

        Assert.Equal("/client/requested/path", context.RequestedCwd);
        Assert.Equal(workingDirectory, context.EffectiveCwd);
        Assert.Equal(root.Path, context.RepositoryRoot);
        Assert.Equal(Path.Combine("src", "module"), context.RepositoryRelativePath);
        Assert.Null(context.RepositoryUnavailableReason);
    }

    [Fact]
    public void Execution_context_labels_repository_absence()
    {
        using var root = new TemporaryDirectory("ptk-audit-no-repo-");

        var context = AuditExecutionContextCapture.Capture(null, root.Path);

        Assert.Equal(root.Path, context.EffectiveCwd);
        Assert.Null(context.RepositoryRoot);
        Assert.Null(context.RepositoryRelativePath);
        Assert.Equal("repository_not_detected", context.RepositoryUnavailableReason);
    }

    private static CallToolRequestParams InvokeCall() => new()
    {
        Name = "ptk_invoke",
        Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["script"] = JsonSerializer.SerializeToElement("Get-Date"),
        },
    };

    private static bool Capture(
        CallToolRequestParams call,
        out AuditCallMetadata? metadata,
        out string? failure) =>
        AuditCallMetadataCapture.TryCapture(
            call,
            new AuditClientContext("test-client", "1.2.3", "initialize-session"),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            Now,
            out metadata,
            out _,
            out failure);

    private static void AssertRejected(
        CallToolRequestParams call,
        string expectedFailure,
        string submittedValue)
    {
        Assert.False(Capture(call, out var metadata, out var failure));
        Assert.Null(metadata);
        Assert.Contains(expectedFailure, failure, StringComparison.Ordinal);
        Assert.DoesNotContain(submittedValue, failure, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory(string prefix)
        {
            Path = Directory.CreateTempSubdirectory(prefix).FullName;
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

#pragma warning restore MCPEXP001
