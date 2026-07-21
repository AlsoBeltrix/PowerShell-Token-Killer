using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpGuardian.Tests;

public sealed class AuditCallLifecycleTests
{
    [Fact]
    public void Guardian_safe_lifecycle_owns_admission_chain_and_terminal_reservation()
    {
        var options = AuditOptions.Create(
            Path.Combine(Path.GetTempPath(), "ptk-guardian-audit-" + Guid.NewGuid().ToString("N")),
            maxRecordBytes: AuditEventSerializer.MaximumLineBytes,
            segmentBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            aggregateBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            emergencyReserveBytes: AuditEventSerializer.MaximumLineBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: ScriptEvidenceStore.MaximumScriptBytes,
            evidenceAggregateBytes: ScriptEvidenceStore.MaximumScriptBytes * 2L,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
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
            "guardian-audit-test",
            binaryDigest: null,
            hostId: Guid.Parse("12345678-1234-4abc-8def-0123456789ab"),
            supervisorBootId: Guid.Parse("22345678-1234-4abc-8def-0123456789ab"));
        var call = new AuditCallLifecycle(
            journal,
            new ScriptEvidenceStore(options.EvidenceDirectory));
        var metadata = Metadata();

        Assert.True(call.TryBegin(metadata, exactSubmittedScript: null, out var failure), failure);
        Assert.True(call.Accepted);
        Assert.Equal(4L * options.MaxRecordBytes, journal.ReservedBytes);

        ((IAuditBoundaryCall)call).CompleteFromFilter("completed", bytesReturned: 17);

        Assert.True(call.TerminalWritten);
        Assert.Equal(0, journal.ReservedBytes);
        var events = sink.Lines.Select(Parse).ToArray();
        Assert.Equal(["call.accepted", "call.completed"], events.Select(EventType));
        var callId = events[0].GetProperty("correlation").GetProperty("call_id").GetGuid();
        Assert.Equal(callId, events[1].GetProperty("correlation").GetProperty("call_id").GetGuid());
        Assert.Equal(
            events[0].GetProperty("event_id").GetGuid(),
            events[1].GetProperty("correlation").GetProperty("parent_event_id").GetGuid());
        Assert.Equal(
            17,
            events[1].GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
    }

    [Fact]
    public void Guardian_gate_admits_the_exact_lifecycle_from_its_factory()
    {
        var options = AuditOptions.Create(
            Path.Combine(Path.GetTempPath(), "ptk-guardian-gate-" + Guid.NewGuid().ToString("N")),
            maxRecordBytes: AuditEventSerializer.MaximumLineBytes,
            segmentBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            aggregateBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            emergencyReserveBytes: AuditEventSerializer.MaximumLineBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: ScriptEvidenceStore.MaximumScriptBytes,
            evidenceAggregateBytes: ScriptEvidenceStore.MaximumScriptBytes * 2L,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
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
            "guardian-gate-test",
            binaryDigest: null,
            hostId: Guid.Parse("32345678-1234-4abc-8def-0123456789ab"),
            supervisorBootId: Guid.Parse("42345678-1234-4abc-8def-0123456789ab"));
        var evidence = new ScriptEvidenceStore(options.EvidenceDirectory);
        var factory = new RecordingCallFactory();
        using var gate = AuditRuntimeGate.CreateOperationalForTests(
            options,
            health,
            journal,
            evidence,
            factory);

        Assert.True(gate.TryBeginCall(
            Metadata(),
            exactSubmittedScript: null,
            out var call,
            out var lease,
            out var failure), failure);

        call!.CompleteCall("completed", "ok");
        lease!.Dispose();
        Assert.Equal(1, factory.CreateCount);
        Assert.Same(factory.Created, call);
        Assert.Equal(["call.accepted", "call.completed"], sink.Lines.Select(Parse).Select(EventType));
    }

    private static AuditCallMetadata Metadata() => new(
            new AuditActor
            {
                Transport = "mcp_stdio",
                ClientName = "guardian-test",
                ClientVersion = "1",
                ClientSessionId = "session",
                AttributionStrength = "client_asserted",
            },
            new AuditRequest
            {
                Tool = "ptk_state",
                Action = "state",
                SessionRequested = "default",
                ProvidedFields = [],
                ListAvailable = false,
            },
            new AuditOperationProfile(
                MaximumCallRecordSlots: 5,
                PersistentJobTerminalSlots: 0,
                RequiresScriptEvidence: false,
                MayHaveSideEffects: true));

    private static JsonElement Parse(byte[] line)
    {
        using var document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
        return document.RootElement.Clone();
    }

    private static string EventType(JsonElement value) =>
        value.GetProperty("event_type").GetString()!;

    private sealed class RecordingCallFactory : IAuditCallFactory
    {
        internal int CreateCount { get; private set; }

        internal AuditCallLifecycle? Created { get; private set; }

        public AuditCallLifecycle Create(
            AuditJournal journal,
            ScriptEvidenceStoreProvider evidence)
        {
            CreateCount++;
            return Created = new AuditCallLifecycle(journal, evidence);
        }
    }
}
