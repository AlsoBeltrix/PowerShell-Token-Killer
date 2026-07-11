using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditCallContextTests : IDisposable
{
    private readonly List<string> _roots = [];

    public static TheoryData<InvokeDisposition, bool, bool, string, string, string, string> InvokeTerminals =>
        new()
        {
            { InvokeDisposition.Completed, true, false, "execution.completed", "completed", "confirmed", "complete" },
            { InvokeDisposition.Failed, false, false, "execution.failed", "failed", "confirmed", "complete" },
            { InvokeDisposition.Canceled, false, false, "execution.canceled", "canceled", "confirmed", "complete" },
            { InvokeDisposition.OutcomeUnknown, false, true, "execution.timed_out", "outcome_unknown", "unknown", "unknown" },
            { InvokeDisposition.OutcomeUnknown, false, false, "execution.outcome_unknown", "outcome_unknown", "unknown", "unknown" },
        };

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Preserve the assertion failure that prevented cleanup. */ }
        }
    }

    [Theory]
    [MemberData(nameof(InvokeTerminals))]
    public async Task Invoke_terminal_dispositions_have_exact_event_and_certainty(
        InvokeDisposition disposition,
        bool success,
        bool timedOut,
        string terminalEvent,
        string terminalState,
        string certainty,
        string rootCoverage)
    {
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", "'terminal-matrix'")),
            exactScript: "'terminal-matrix'");
        Assert.True(fixture.Context.BeginValidation());
        Assert.True(await fixture.Context.AuthorizeInvocationAsync(
            new InvocationPreparation(
                EffectiveRoute: "powershell_direct",
                RequestedRoute: "auto",
                PermittedFallbacks: ["powershell_direct"],
                FallbackReason: null),
            CancellationToken.None));

        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: success,
                Output: success ? "ok" : string.Empty,
                Errors: success ? [] : ["failed"],
                Warnings: [],
                TimedOut: timedOut,
                Disposition: disposition,
                UserExecutionStarted: true),
            success ? "ok" : "failed");

        var events = fixture.Events();
        Assert.Equal(
            [
                "call.accepted",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "execution.dispatched",
                terminalEvent,
                success ? "call.completed" : "call.failed",
            ],
            events.Select(EventType));
        AssertOutcome(events[^2], terminalState, certainty, rootCoverage);
        AssertOutcome(
            events[^1],
            success ? "completed" : "failed",
            disposition == InvokeDisposition.OutcomeUnknown ? "unknown" : "not_applicable",
            "not_applicable");
    }

    [Fact]
    public void Preflight_refusal_has_no_execution_event_and_is_terminally_not_started()
    {
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", "'never-ran'")),
            exactScript: "'never-ran'");
        Assert.True(fixture.Context.BeginValidation());

        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: false,
                Output: string.Empty,
                Errors: ["refused"],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.NotStarted,
                UserExecutionStarted: false),
            "refused");

        var events = fixture.Events();
        Assert.Equal(
            [
                "call.accepted",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "call.not_started",
            ],
            events.Select(EventType));
        AssertOutcome(events[^2], "not_started", "not_applicable", "none");
        AssertOutcome(events[^1], "not_started", "not_applicable", "not_applicable");
    }

    [Theory]
    [InlineData("ptk_reset", "reset.requested", "runspace.recycled")]
    [InlineData("ptk_state", "state.probe_requested", "state.probe_completed")]
    public void Control_calls_have_exact_requested_outcome_and_call_terminal_sequence(
        string tool,
        string requestedEvent,
        string completedEvent)
    {
        using var fixture = CreateFixture(Call(tool), exactScript: null);
        Assert.True(fixture.Context.AuthorizeControl(requestedEvent));
        fixture.Context.RecordControlOutcome(
            completedEvent,
            "completed",
            warmStateLost: tool == "ptk_reset");
        fixture.Context.CompleteCall("completed", "ok");

        var events = fixture.Events();
        Assert.Equal(
            ["call.accepted", requestedEvent, completedEvent, "call.completed"],
            events.Select(EventType));
        AssertOutcome(events[1], "requested", "not_applicable", "not_applicable");
        AssertOutcome(events[2], "completed", "confirmed", "not_applicable");
        AssertOutcome(events[3], "completed", "not_applicable", "not_applicable");
    }

    [Fact]
    public void Background_refusal_records_start_intent_before_validation_and_no_terminal_job_event()
    {
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", "'never-ran'"), ("background", true)),
            exactScript: "'never-ran'");

        Assert.True(fixture.Context.BeginJobStartRequest(17));
        fixture.Context.RecordJobNotStarted("dialect_refused", "refused", 17);

        Assert.Equal(
            [
                "call.accepted",
                "job.start_requested",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "job.not_started",
                "call.not_started",
            ],
            fixture.Events().Select(EventType));
    }

    [Fact]
    public void Background_refusal_persistence_failure_is_marked_for_generic_fail_closed_response()
    {
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", "'never-ran'"), ("background", true)),
            exactScript: "'never-ran'",
            journalFault: (point, append) => point == AuditSinkFaultPoint.Flush && append == 5);

        Assert.True(fixture.Context.BeginJobStartRequest(17));
        fixture.Context.RecordValidationNoStart("dialect_refused");
        Assert.True(fixture.Context.AuthorizationPersistenceFailed);

        fixture.Context.RecordJobNotStarted("dialect_refused", "must be replaced", 17);
        Assert.DoesNotContain("job.not_started", fixture.Events().Select(EventType));
    }

    [Fact]
    public void Read_outcome_persists_exact_byte_count_and_next_offset_before_release()
    {
        using var fixture = CreateFixture(
            Call("ptk_job", ("action", "output"), ("id", 17L), ("offset", 4L)),
            exactScript: null);
        Assert.True(fixture.Context.AuthorizeControl("job.output_requested", 17));

        fixture.Context.CommitReadOutcome(
            "job.output_accessed",
            "completed",
            "ignored for byte override",
            jobId: 17,
            nextOffset: 11,
            bytesReturnedOverride: 7);
        fixture.Context.CompleteCall("completed", "response");

        var access = fixture.Events().Single(value => EventType(value) == "job.output_accessed");
        var outcome = access.GetProperty("outcome");
        Assert.Equal(7, outcome.GetProperty("bytes_returned").GetInt64());
        Assert.Equal(11, outcome.GetProperty("next_offset").GetInt64());
        Assert.Equal(17, access.GetProperty("correlation").GetProperty("job_id").GetInt64());
    }

    [Fact]
    public void Read_outcome_persistence_failure_is_not_swallowed()
    {
        using var fixture = CreateFixture(
            Call("ptk_job", ("action", "status"), ("id", 17L)),
            exactScript: null,
            journalFault: (point, append) => point == AuditSinkFaultPoint.Flush && append == 2);

        Assert.Throws<AuditUnavailableException>(() =>
            fixture.Context.CommitReadOutcome(
                "job.status_accessed",
                "completed",
                "must not be released",
                jobId: 17));
    }

    private ContextFixture CreateFixture(
        CallToolRequestParams call,
        string? exactScript,
        Func<AuditSinkFaultPoint, int, bool>? journalFault = null)
    {
        const int maxRecordBytes = 4096;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "test-audit-context-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var options = AuditOptions.Create(
            root,
            maxRecordBytes: maxRecordBytes,
            segmentBytes: maxRecordBytes * 64L,
            aggregateBytes: maxRecordBytes * 64L,
            emergencyReserveBytes: maxRecordBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: maxRecordBytes,
            evidenceAggregateBytes: maxRecordBytes * 8L,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
        var health = new AuditHealth(options);
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge,
            journalFault);
        var journal = new AuditJournal(
            options,
            health,
            sink,
            "context-test",
            binaryDigest: null,
            hostId: Guid.Parse("12345678-1234-4abc-8def-0123456789ab"),
            supervisorBootId: Guid.Parse("22345678-1234-4abc-8def-0123456789ab"));
        var evidence = new ScriptEvidenceStore(options.EvidenceDirectory);
        Assert.True(AuditCallMetadataCapture.TryCapture(
            call,
            new AuditClientContext("context-test", "1", "session"),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            DateTimeOffset.UtcNow,
            out var metadata,
            out var capturedScript,
            out var failure),
            failure);
        Assert.Equal(exactScript, capturedScript);
        var context = new AuditCallContext(journal, evidence);
        Assert.True(context.TryBegin(metadata!, capturedScript, out failure), failure);
        return new ContextFixture(sink, journal, context);
    }

    private static CallToolRequestParams Call(
        string name,
        params (string Name, object? Value)[] arguments)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (argumentName, value) in arguments)
            values.Add(argumentName, JsonSerializer.SerializeToElement(value));
        return new CallToolRequestParams { Name = name, Arguments = values };
    }

    private static string EventType(JsonElement value) =>
        value.GetProperty("event_type").GetString()!;

    private static void AssertOutcome(
        JsonElement value,
        string state,
        string certainty,
        string rootCoverage)
    {
        var outcome = value.GetProperty("outcome");
        Assert.Equal(state, outcome.GetProperty("state").GetString());
        Assert.Equal(certainty, outcome.GetProperty("termination_certainty").GetString());
        Assert.Equal(
            rootCoverage,
            value.GetProperty("coverage").GetProperty("root_process_observed").GetString());
    }

    private sealed record ContextFixture(
        InMemoryAuditJournalSink Sink,
        AuditJournal Journal,
        AuditCallContext Context) : IDisposable
    {
        internal List<JsonElement> Events() => Sink.Lines.Select(line =>
        {
            using var document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
            return document.RootElement.Clone();
        }).ToList();

        public void Dispose() => Journal.Dispose();
    }
}
