using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class AuditWorkerCompletionTests
{
    [Fact]
    public async Task Supervisor_completion_preserves_available_output_for_terminal_evidence()
    {
        var parent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "test-worker-audit-" + Guid.NewGuid().ToString("N"));
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
                "worker-evidence-test",
                binaryDigest: null,
                hostId: Guid.NewGuid(),
                supervisorBootId: Guid.NewGuid());
            var evidenceStore = new ScriptEvidenceStore(options);
            var evidenceProvider = new ScriptEvidenceStoreProvider(evidenceStore);
            using var outputStore = new OutputStore(new OutputStoreOptions(
                outputRoot,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 8 * 1024 * 1024,
                MaximumSessionBytes: 16 * 1024 * 1024,
                MaximumAggregateBytes: 32 * 1024 * 1024));

            using var capture = new ForegroundOutputCapture(outputStore);
            await capture.PrepareAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var recovery = await capture.SealAsync(
            new OutputArtifactContent(
                "worker exact output",
                    [],
                    [],
                    [],
                    null,
                    OutputProvenance.PowerShellObjects),
            TimeSpan.FromSeconds(5));
        Assert.NotNull(recovery.Handle);
        var retainedOutput = outputStore.ReadExactForAudit(
            recovery.Handle,
            recovery.Bytes);

            var call = new CallToolRequestParams
            {
                Name = "ptk_invoke",
                Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["script"] = JsonSerializer.SerializeToElement("'worker command'"),
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
            var context = new AuditCallContext(journal, evidenceProvider, outputStore);
            Assert.True(context.TryBegin(metadata!, exactScript, out _));
            var repositoryRoot = FindRepositoryRoot();
            var effectiveWorkingDirectory = Path.Combine(
                repositoryRoot,
                "server",
                "PtkMcpServer");

            using var supervisor = new WorkerSupervisor(
                new NamedSessionSupervisor(
                    () => new SuccessfulWorkerFactory(
                        recovery,
                        effectiveWorkingDirectory),
                    startupTimeout: TimeSpan.FromSeconds(5),
                    containmentGrace: TimeSpan.FromSeconds(1)));
            _ = await supervisor.NamedSessions.OpenAsync("audit-worker");

            var outcome = await InvokeWithCurrentAuditContractAsync(
                supervisor,
                outputStore,
                context);
            if (!context.TerminalWritten)
            {
                context.CompleteFromFilter(
                    "completed",
                    Encoding.UTF8.GetByteCount(outcome.Text),
                    outcome.Text);
            }

            var terminal = sink.Lines
                .Select(line => Encoding.UTF8.GetString(line).TrimEnd('\n'))
                .Single(line => line.Contains(
                    "\"event_type\":\"call.completed\"",
                    StringComparison.Ordinal));
            using var terminalDocument = JsonDocument.Parse(terminal);
            var capturedOutput = terminalDocument.RootElement
                .GetProperty("evidence_manifest")
                .EnumerateArray()
                .Single(entry => string.Equals(
                    entry.GetProperty("evidence_kind").GetString(),
                    "captured_output",
                    StringComparison.Ordinal));
            Assert.Equal("complete", capturedOutput
                .GetProperty("capture_state")
                .GetString());
            byte[]? publishedOutput = null;
            _ = evidenceStore.ReadExact(
                capturedOutput.GetProperty("evidence_id").GetString()!,
                bytes => publishedOutput = bytes.ToArray());
            Assert.Equal(retainedOutput, publishedOutput);
            var executionContext = terminalDocument.RootElement.GetProperty(
                "execution_context");
            Assert.Equal(
                effectiveWorkingDirectory,
                executionContext.GetProperty("effective_cwd").GetString());
            Assert.Equal(
                repositoryRoot,
                executionContext.GetProperty("repository_root").GetString());
            Assert.Equal(
                Path.Combine("server", "PtkMcpServer"),
                executionContext.GetProperty("repository_relative_path").GetString());
        }
        finally
        {
            if (Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            var marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
                return current.FullName;
        }
        throw new InvalidOperationException("Test repository root was not found.");
    }

    private static async Task<ToolOutcome> InvokeWithCurrentAuditContractAsync(
        WorkerSupervisor supervisor,
        OutputStore outputStore,
        AuditCallContext context)
    {
        var accessor = new AuditCallContextAccessor
        {
            Current = context,
        };
        return await supervisor.InvokeAsync(
            "'worker command'",
            CancellationToken.None,
            raw: false,
            route: "pwsh",
            timeoutSeconds: 30,
            session: "audit-worker",
            outputStore,
            accessor);
    }

    private sealed class SuccessfulWorkerFactory(
        OutputRecoverySummary recovery,
        string effectiveWorkingDirectory)
        : ISessionWorkerFactory
    {
        public Task<ISessionWorker> StartAsync(
            Guid sessionId,
            long incarnation,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<ISessionWorker>(
                new SuccessfulWorker(
                    sessionId,
                    incarnation,
                    recovery,
                    effectiveWorkingDirectory));
    }

    private sealed class SuccessfulWorker(
        Guid sessionId,
        long incarnation,
        OutputRecoverySummary recovery,
        string effectiveWorkingDirectory) : ISessionWorker
    {
        private readonly TaskCompletionSource _fatal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 47002;
        public Guid SessionId => sessionId;
        public long Incarnation => incarnation;
        public bool IsTransportUsable => true;
        public Task Fatal => _fatal.Task;
        public Task ContainmentEmpty => Task.CompletedTask;

        public Task<SessionWorkerInvocation> InvokeAsync(
            string script,
            bool raw,
            WorkerInvokeRoute route,
            int timeoutSeconds,
            IWorkerArtifactCapture? artifactCapture,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionWorkerInvocation(
                new WorkerResult(
                    RequestId: 1,
                    WorkerResultStatus.Completed,
                    "caller-visible worker response",
                    DetailCode: null,
                    effectiveWorkingDirectory,
                    UserExecutionStarted: true),
                ArtifactId: null,
                ArtifactContent: null,
                OutputRecovery: recovery));

        public Task<WorkerStateSnapshot> StateAsync(
            bool listAvailable,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerStateSnapshot(
                RequestId: 1,
                Available: false,
                Text: string.Empty,
                DetailCode: "state_unavailable"));

        public Task<WorkerContainmentResult> StopAsync(
            WorkerContainmentReason reason,
            CancellationToken cancellationToken) =>
            Task.FromResult(WorkerContainmentResult.Confirmed());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
