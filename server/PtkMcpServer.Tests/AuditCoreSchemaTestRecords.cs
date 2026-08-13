using System.Security.Cryptography;
using System.Text;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

internal static class AuditCoreSchemaTestRecords
{
    internal static readonly Guid EventId =
        Guid.Parse("01890f3e-1234-7abc-8def-0123456789ab");
    internal static readonly Guid ParentEventId =
        Guid.Parse("01890f3e-9abc-7abc-8def-0123456789ab");
    internal static readonly Guid SupervisorBootId =
        Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
    internal static readonly DateTimeOffset Occurred =
        new(2026, 7, 11, 12, 34, 56, 123, TimeSpan.Zero);
    internal const string HashA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly Guid CallId =
        Guid.Parse("01890f3e-5678-7abc-8def-0123456789ab");
    private static readonly Guid HostId =
        Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid WorkerBootId =
        Guid.Parse("32345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid PlanId =
        Guid.Parse("42345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid EvidenceId =
        Guid.Parse("52345678-1234-4abc-8def-0123456789ab");
    private static readonly DateTimeOffset Observed = Occurred.AddTicks(4567);
    private const string TraceId = "0123456789abcdef0123456789abcdef";
    private const string HashB =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string V1 = "\"schema_version\":\"ptk.audit/1\"";
    private const string V2 = "\"schema_version\":\"ptk.audit/2\"";
    private const string PreviousSupervisorBootId =
        ",\"previous_supervisor_boot_id\":null";
    private const string DestinationFields =
        ",\"destination_kind\":null,\"destination_path\":null";
    private const string RetentionFields =
        ",\"evidence_subject_id\":null,\"evidence_subject_digest\":null," +
        "\"evidence_subject_bytes\":null,\"evidence_subject_state\":null," +
        "\"retention_reason\":null";
    private const string NullDisposition = ",\"operator_disposition\":null";
    private const string EventHashMarker = ",\"event_hash\":\"";

    internal static SerializedAuditEvent Create(
        bool includeOptionalQueryValues = true,
        long sequence = 1,
        string? previousEventHash = null,
        string? outcomeState = null,
        AuditOperatorDispositionFacts? operatorDisposition = null,
        Guid? eventId = null) =>
        AuditEventSerializer.Serialize(
            sequence,
            previousEventHash,
            new AuditProducerContext(
                HostId,
                SupervisorBootId,
                includeOptionalQueryValues ? WorkerBootId : null,
                4321,
                "1.2.3-test",
                HashA),
            CompleteInput(includeOptionalQueryValues, outcomeState, operatorDisposition),
            eventId ?? EventId,
            Occurred,
            Observed);

    /// <summary>A second distinct record identity for multi-record corpora
    /// (R5 conformance): the receiver's events are keyed by event id, so a
    /// chain of distinct records must never reuse <see cref="EventId"/>.</summary>
    internal static readonly Guid SecondEventId =
        Guid.Parse("01890f3e-2222-7abc-8def-0123456789ab");

    internal static readonly Guid UnicodeEventId =
        Guid.Parse("01890f3e-4444-7abc-8def-0123456789ab");

    /// <summary>
    /// A canonical record whose free-text fields carry non-ASCII content
    /// (CJK, Cyrillic, Greek, an astral-plane symbol) — the SIEM conformance
    /// corpus's Unicode-fidelity leg (audit-restoration R5 / mini-SIEM S4).
    /// Additive: no existing record's bytes change.
    /// </summary>
    internal static SerializedAuditEvent CreateUnicode(
        long sequence,
        string? previousEventHash)
    {
        var input = CompleteInput(
            includeOptionalQueryValues: true,
            outcomeState: null,
            operatorDisposition: null);
        input = input with
        {
            Session = input.Session! with
            {
                DeclaredPurpose = "Unicode 検証 — тест ✓ δοκιμή 𝄞",
            },
            Request = input.Request! with
            {
                Cwd = "/tmp/工作/área",
            },
        };
        return AuditEventSerializer.Serialize(
            sequence,
            previousEventHash,
            new AuditProducerContext(
                HostId,
                SupervisorBootId,
                WorkerBootId,
                4321,
                "1.2.3-test",
                HashA),
            input,
            UnicodeEventId,
            Occurred,
            Observed);
    }

    internal static byte[] ToLegacyV1(ReadOnlyMemory<byte> v2Line)
    {
        var preHash = PreHashText(v2Line);
        preHash = ReplaceOnce(preHash, V2, V1);
        preHash = ReplaceOnce(preHash, PreviousSupervisorBootId, string.Empty);
        preHash = ReplaceOnce(preHash, DestinationFields, string.Empty);
        preHash = ReplaceOnce(preHash, RetentionFields, string.Empty);
        preHash = ReplaceOnce(preHash, NullDisposition, string.Empty);
        return WithRecomputedHash(preHash);
    }

    internal static byte[] RelabelV2AsV1WithoutShrinking(ReadOnlyMemory<byte> v2Line) =>
        WithRecomputedHash(ReplaceOnce(PreHashText(v2Line), V2, V1));

    internal static byte[] RelabelV1AsV2WithoutExpanding(ReadOnlyMemory<byte> v1Line) =>
        WithRecomputedHash(ReplaceOnce(PreHashText(v1Line), V1, V2));

    private static AuditEventInput CompleteInput(
        bool includeOptionalQueryValues,
        string? outcomeState,
        AuditOperatorDispositionFacts? operatorDisposition) => new()
        {
            EventType = operatorDisposition is null
            ? "execution.planned"
            : "export.disposition_authorized",
            Session = new AuditSession
            {
                Name = includeOptionalQueryValues ? "default" : null,
                Generation = includeOptionalQueryValues ? 0 : null,
                BindingKind = includeOptionalQueryValues ? "default" : null,
                DeclaredPurpose = "test purpose",
                DeclaredTarget = "localhost",
                DeclaredIdentity = "test-user",
                EffectiveIdentity = "test-user",
                AllowColdBackground = false,
            },
            Actor = new AuditActor
            {
                Transport = "mcp_stdio",
                ClientName = "test-client",
                ClientVersion = "1.0",
                ClientSessionId = "session-1",
                AttributionStrength = "client_asserted",
            },
            Correlation = new AuditCorrelation
            {
                CallId = includeOptionalQueryValues ? CallId : null,
                JobId = includeOptionalQueryValues ? 7 : null,
                ParentEventId = ParentEventId,
                TraceId = includeOptionalQueryValues ? TraceId : null,
                PlanId = PlanId,
            },
            Request = new AuditRequest
            {
                Tool = "ptk_invoke",
                Action = "invoke",
                ProvidedFields = ["action", "raw"],
                SessionRequested = "default",
                Cwd = "/tmp/work",
                TimeoutMs = 30_000,
                DeadlineUtc = Observed.AddMinutes(1),
                Route = "auto",
                Background = false,
                Raw = false,
                ExpectedGeneration = 0,
                Force = false,
                AllowColdBackground = false,
                MaxBytes = 65_536,
                PatternFingerprint = HashA,
                OutputHandleDigest = HashB,
                OriginalScriptDigest = HashA,
                ScriptEvidenceId = EvidenceId,
            },
            OperatorDisposition = operatorDisposition,
            Routing = new AuditRouting
            {
                Domain = "powershell",
                RequestedRoute = "auto",
                EffectiveRoute = "powershell_direct",
                PermittedFallbacks = ["powershell_direct", "native_direct"],
                Provenance = "powershell_objects",
            },
            Outcome = new AuditOutcome
            {
                State = outcomeState,
                QueueMs = 0,
                WarmStateLost = false,
                WorkerReplaced = false,
                TerminationCertainty = includeOptionalQueryValues
                ? "not_applicable"
                : null,
            },
            Coverage = new AuditCoverage
            {
                PtkRequest = true,
                RootProcessObserved = "not_applicable",
                DescendantsObserved = "not_applicable",
                RemoteEffectObserved = "not_applicable",
            },
            Audit = new AuditEventHealth
            {
                ProtectionMode = "local-only",
                HealthState = "healthy",
            },
        };

    private static string PreHashText(ReadOnlyMemory<byte> line)
    {
        if (line.Length < 2 || line.Span[^1] != (byte)'\n')
            throw new ArgumentException("The test audit record is not JSONL.", nameof(line));
        var body = Encoding.UTF8.GetString(line.Span[..^1]);
        var marker = body.LastIndexOf(EventHashMarker, StringComparison.Ordinal);
        if (marker < 1 || body[^1] != '}')
            throw new ArgumentException("The test audit record has no final event hash.", nameof(line));
        return body[..marker] + '}';
    }

    private static byte[] WithRecomputedHash(string preHash)
    {
        if (preHash.Length < 2 || preHash[^1] != '}')
            throw new ArgumentException("The test audit pre-hash body is invalid.", nameof(preHash));
        var preHashBytes = Encoding.UTF8.GetBytes(preHash);
        var hash = Convert.ToHexString(SHA256.HashData(preHashBytes)).ToLowerInvariant();
        return Encoding.UTF8.GetBytes(
            preHash[..^1] + EventHashMarker + hash + "\"}\n");
    }

    private static string ReplaceOnce(string value, string oldValue, string newValue)
    {
        var first = value.IndexOf(oldValue, StringComparison.Ordinal);
        if (first < 0 ||
            value.IndexOf(oldValue, first + oldValue.Length, StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException("The test audit record does not have the expected v2 shape.");
        }
        return value[..first] + newValue + value[(first + oldValue.Length)..];
    }
}
