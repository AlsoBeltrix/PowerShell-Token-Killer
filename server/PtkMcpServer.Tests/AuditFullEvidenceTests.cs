using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Tests;

public sealed class AuditFullEvidenceTests
{
    [Fact]
    public async Task Terminal_v5_exports_exact_command_response_and_output_artifact()
    {
        var parent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "test-full-evidence-" + Guid.NewGuid().ToString("N"));
        var auditRoot = Path.Combine(parent, "audit");
        var outputRoot = Path.Combine(parent, "output");
        Directory.CreateDirectory(parent);
        try
        {
            var options = AuditOptions.Create(
                auditRoot,
                maxRecordBytes: AuditOptions.DefaultMaxRecordBytes,
                segmentBytes: 4 * 1024 * 1024,
                aggregateBytes: 16 * 1024 * 1024,
                emergencyReserveBytes: 1024 * 1024,
                maxEvidenceBytes: AuditOptions.DefaultMaxEvidenceBytes,
                evidenceAggregateBytes: 16 * 1024 * 1024);
            var health = new AuditHealth(options);
            var sink = new InMemoryAuditJournalSink(
                options.SegmentBytes,
                options.AggregateBytes,
                options.ProtectionMode,
                options.RetentionAge);
            using var journal = new AuditJournal(
                options,
                health,
                sink,
                "evidence-test",
                binaryDigest: null,
                hostId: Guid.Parse("12345678-1234-4abc-8def-0123456789ab"),
                supervisorBootId: Guid.Parse("22345678-1234-4abc-8def-0123456789ab"));
            var store = new ScriptEvidenceStore(options);
            var provider = new ScriptEvidenceStoreProvider(store);
            using var outputStore = new OutputStore(new OutputStoreOptions(
                outputRoot,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 8 * 1024 * 1024,
                MaximumSessionBytes: 16 * 1024 * 1024,
                MaximumAggregateBytes: 32 * 1024 * 1024));

            using var capture = new ForegroundOutputCapture(outputStore);
            await capture.PrepareAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            var artifact = new OutputArtifactContent(
                "unshaped output",
                ["native stderr"],
                ["powershell error"],
                ["warning"],
                7,
                OutputProvenance.PowerShellObjects);
            var recovery = await capture.SealAsync(artifact, TimeSpan.FromSeconds(5));
            Assert.NotNull(recovery.Handle);

            var call = new CallToolRequestParams
            {
                Name = "ptk_invoke",
                Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["script"] = JsonSerializer.SerializeToElement("'exact command'"),
                },
            };
            Assert.True(AuditCallMetadataCapture.TryCapture(
                call,
                new AuditClientContext("test", "1", "session"),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
                DateTimeOffset.UtcNow,
                out var metadata,
                out var exactScript,
                out _));
            var context = new AuditCallContext(journal, provider, outputStore);
            Assert.True(context.TryBegin(metadata!, exactScript, out _));
            context.RecordInvokeResult(
                new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: [],
                    Warnings: [],
                    TimedOut: false,
                    Disposition: InvokeDisposition.NotStarted,
                    UserExecutionStarted: false)
                {
                    OutputRecovery = recovery,
                },
                "caller-visible response");

            var terminal = sink.Lines
                .Select(line => Encoding.UTF8.GetString(line).TrimEnd('\n'))
                .Single(line => line.Contains("\"event_type\":\"call.not_started\"", StringComparison.Ordinal));
            using (var terminalDocument = JsonDocument.Parse(terminal))
            {
                var root = terminalDocument.RootElement;
                Assert.Equal("ptk.audit/5", root.GetProperty("schema_version").GetString());
                Assert.Equal(3, root.GetProperty("evidence_manifest").GetArrayLength());
            }

            Assert.True(AuditEvidenceEnvelope.TryExpand(
                [terminal],
                provider,
                out var expanded,
                out var failure), failure);
            Assert.Equal(4, expanded.Count);
            var evidenceByKind = expanded.Skip(1)
                .Select(record => JsonDocument.Parse(record))
                .ToDictionary(
                    document => document.RootElement.GetProperty("evidence_kind").GetString()!,
                    document => document);
            try
            {
                Assert.Equal(
                    "'exact command'",
                    Payload(evidenceByKind["submitted_command"]));
                Assert.Equal(
                    "caller-visible response",
                    Payload(evidenceByKind["caller_response"]));
                Assert.Equal(
                    string.Join(
                        Environment.NewLine,
                        "unshaped output",
                        "[exit] 7",
                        "[stderr]",
                        "native stderr",
                        "[errors]",
                        "powershell error",
                        "[warnings]",
                        "warning"),
                    Payload(evidenceByKind["captured_output"]));
            }
            finally
            {
                foreach (var document in evidenceByKind.Values)
                    document.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
    }

    private static string Payload(JsonDocument document) =>
        Encoding.UTF8.GetString(
            document.RootElement.GetProperty("payload_base64").GetBytesFromBase64());
}
