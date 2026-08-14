using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        Assert.True(await AuthorizePlanAndDispatchAsync(
            fixture.Context,
            ExecutionPlanner.CreateDirect(
                "'terminal-matrix'",
                "auto",
                compressAvailable: true,
                ResolutionContext.Warm),
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
    public async Task Invocation_audit_uses_plan_domain_not_effective_route()
    {
        const string script = "git diff | Set-Content patch.txt";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var plan = new ExecutionPlan(
            script,
            script,
            ExecutionDomain.MixedDataflow,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.PowerShellObjects,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            rtkExecutableIdentity: null);

        Assert.True(await AuthorizePlanAndDispatchAsync(
            fixture.Context,
            plan,
            CancellationToken.None));
        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: true,
                Output: "ok",
                Errors: [],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.Completed,
                UserExecutionStarted: true),
            "ok");

        var dispatched = fixture.Events()
            .Single(value => EventType(value) == "execution.dispatched");
        var routing = dispatched.GetProperty("routing");
        Assert.Equal("mixed_dataflow", routing.GetProperty("domain").GetString());
        Assert.Equal("auto", routing.GetProperty("requested_route").GetString());
        Assert.Equal("powershell_direct", routing.GetProperty("effective_route").GetString());
        Assert.Equal("powershell_objects", routing.GetProperty("provenance").GetString());
        Assert.Empty(routing.GetProperty("permitted_fallbacks").EnumerateArray());
    }

    [Fact]
    public async Task Rtk_fallback_audits_the_planned_route_then_the_actual_dispatch_and_terminals()
    {
        const string script = "git status";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var rtkPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "audit-rtk"));
        var plan = new ExecutionPlan(
            script,
            executionScript: "rtk " + script,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            ImmutableArray.Create(ExecutionPath.PowerShellDirect),
            fallbackReason: null,
            new RtkExecutableIdentity(rtkPath),
            workingDirectory: Path.GetFullPath(Path.GetTempPath()),
            directFallbackProvenance: OutputProvenance.PowerShellObjects);

        Assert.True(await fixture.Context.AuthorizePlanAsync(
            plan,
            CancellationToken.None));
        var dispatch = ExecutionDispatch.RtkUnavailableFallback(plan);
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(
            dispatch,
            CancellationToken.None));
        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: true,
                Output: "ok",
                Errors: [],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.Completed,
                UserExecutionStarted: true),
            "ok");

        var events = fixture.Events();
        Assert.Equal(
            [
                "call.accepted",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "execution.dispatched",
                "execution.completed",
                "call.completed",
            ],
            events.Select(EventType));

        var planned = events.Single(value => EventType(value) == "execution.planned");
        var planId = planned.GetProperty("correlation").GetProperty("plan_id").GetGuid();
        AssertRouting(
            planned,
            requestedRoute: "auto",
            effectiveRoute: "rtk",
            provenance: "rtk_unknown",
            fallbackReason: null);

        foreach (var actual in events.Where(value => EventType(value) is
                     "execution.dispatched" or "execution.completed" or "call.completed"))
        {
            Assert.Equal(
                planId,
                actual.GetProperty("correlation").GetProperty("plan_id").GetGuid());
            AssertRouting(
                actual,
                requestedRoute: "auto",
                effectiveRoute: "powershell_direct",
                provenance: "powershell_objects",
                fallbackReason: "rtk_executable_became_unavailable");
        }
    }

    [Fact]
    public async Task Rtk_dispatch_may_be_superseded_once_by_its_audited_exact_fallback()
    {
        const string script = "git status";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var plan = new ExecutionPlan(
            script,
            executionScript: "rtk " + script,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            ImmutableArray.Create(ExecutionPath.PowerShellDirect),
            fallbackReason: null,
            new RtkExecutableIdentity(
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), "audit-rtk"))),
            workingDirectory: Path.GetFullPath(Path.GetTempPath()),
            directFallbackProvenance: OutputProvenance.PowerShellObjects);

        Assert.True(await fixture.Context.AuthorizePlanAsync(plan, CancellationToken.None));
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(
            ExecutionDispatch.FromPlan(plan),
            CancellationToken.None));
        var fallbackDispatch = ExecutionDispatch.RtkUnavailableFallback(plan);
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(
            fallbackDispatch,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Context.AuthorizeDispatchAsync(
                fallbackDispatch,
                CancellationToken.None));
        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: true,
                Output: "ok",
                Errors: [],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.Completed,
                UserExecutionStarted: true),
            "ok");

        var events = fixture.Events();
        Assert.Equal(9, events.Count);
        var dispatched = events
            .Where(value => EventType(value) == "execution.dispatched")
            .ToArray();
        Assert.Equal(2, dispatched.Length);
        AssertRouting(
            dispatched[0],
            requestedRoute: "auto",
            effectiveRoute: "rtk",
            provenance: "rtk_unknown",
            fallbackReason: null);
        AssertRouting(
            dispatched[1],
            requestedRoute: "auto",
            effectiveRoute: "powershell_direct",
            provenance: "powershell_objects",
            fallbackReason: "rtk_executable_became_unavailable");
        Assert.Equal(
            "rtk_executable_became_unavailable",
            dispatched[1].GetProperty("outcome").GetProperty("detail_code").GetString());
        var planIds = events
            .Where(value => value.GetProperty("correlation").GetProperty("plan_id").ValueKind != JsonValueKind.Null)
            .Select(value => value.GetProperty("correlation").GetProperty("plan_id").GetGuid())
            .Distinct()
            .ToArray();
        Assert.Single(planIds);
    }

    [Fact]
    public async Task Failed_fallback_dispatch_append_restores_the_initial_rtk_routing()
    {
        const string script = "git status";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script,
            journalFault: (point, append) =>
                point == AuditSinkFaultPoint.BeforeAppend && append == 7);
        Assert.True(fixture.Context.BeginValidation());
        var plan = new ExecutionPlan(
            script,
            executionScript: "rtk " + script,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            ImmutableArray.Create(ExecutionPath.PowerShellDirect),
            fallbackReason: null,
            new RtkExecutableIdentity(
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), "audit-rtk"))),
            workingDirectory: Path.GetFullPath(Path.GetTempPath()),
            directFallbackProvenance: OutputProvenance.PowerShellObjects);

        Assert.True(await fixture.Context.AuthorizePlanAsync(plan, CancellationToken.None));
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(
            ExecutionDispatch.FromPlan(plan),
            CancellationToken.None));
        Assert.False(await fixture.Context.AuthorizeDispatchAsync(
            ExecutionDispatch.RtkUnavailableFallback(plan),
            CancellationToken.None));
    }




    [Fact]
    public async Task Planned_but_undispatched_execution_records_a_no_start_terminal()
    {
        const string script = "git status";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var rtkPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "audit-rtk"));
        var plan = new ExecutionPlan(
            script,
            executionScript: "rtk " + script,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            ImmutableArray.Create(ExecutionPath.PowerShellDirect),
            fallbackReason: null,
            new RtkExecutableIdentity(rtkPath),
            workingDirectory: Path.GetFullPath(Path.GetTempPath()),
            directFallbackProvenance: OutputProvenance.PowerShellObjects);

        Assert.True(await fixture.Context.AuthorizePlanAsync(
            plan,
            CancellationToken.None));
        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: false,
                Output: string.Empty,
                Errors: ["dispatch refused"],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.NotStarted,
                UserExecutionStarted: false),
            "dispatch refused");

        var events = fixture.Events();
        Assert.Equal(
            [
                "call.accepted",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "execution.not_started",
                "call.not_started",
            ],
            events.Select(EventType));
        Assert.DoesNotContain("execution.dispatched", events.Select(EventType));
        var notStarted = events.Single(value => EventType(value) == "execution.not_started");
        Assert.Equal(
            "dispatch_not_authorized",
            notStarted.GetProperty("outcome").GetProperty("detail_code").GetString());
        AssertOutcome(notStarted, "not_started", "not_applicable", "none");
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

    [Fact]
    public async Task Dispatch_records_effective_working_directory_and_repository_context()
    {
        const string script = "git status";
        var call = Call("ptk_invoke", ("script", script));
        call.Meta = new JsonObject
        {
            [AuditCallMetadataCapture.CallContextMetadataKey] = new JsonObject
            {
                ["agent_name"] = "codex",
                ["model_provider"] = "openai",
                ["model_name"] = "gpt-5.6-sol",
                ["requested_cwd"] = "/client/requested",
            },
        };
        var repository = Directory.CreateTempSubdirectory("ptk-context-repo-").FullName;
        _roots.Add(repository);
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var workingDirectory = Directory.CreateDirectory(
            Path.Combine(repository, "src", "module")).FullName;

        using var fixture = CreateFixture(call, exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var plan = new ExecutionPlan(
            script,
            executionScript: "rtk " + script,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            ImmutableArray.Create(ExecutionPath.PowerShellDirect),
            fallbackReason: null,
            new RtkExecutableIdentity(Path.Combine(repository, "rtk")),
            workingDirectory,
            directFallbackProvenance: OutputProvenance.PowerShellObjects);

        Assert.True(await fixture.Context.AuthorizePlanAsync(plan, CancellationToken.None));
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(
            ExecutionDispatch.FromPlan(plan),
            CancellationToken.None));
        fixture.Context.CompleteFromFilter("completed", 0);

        var events = fixture.Events();
        var accepted = events.Single(value => EventType(value) == "call.accepted");
        Assert.Equal("ptk.audit/4", accepted.GetProperty("schema_version").GetString());
        Assert.Equal(
            "not_dispatched",
            accepted.GetProperty("execution_context")
                .GetProperty("effective_cwd_unavailable_reason").GetString());

        var dispatched = events.Single(value => EventType(value) == "execution.dispatched");
        var execution = dispatched.GetProperty("execution_context");
        Assert.Equal("/client/requested", execution.GetProperty("requested_cwd").GetString());
        Assert.Equal(workingDirectory, execution.GetProperty("effective_cwd").GetString());
        Assert.Equal(repository, execution.GetProperty("repository_root").GetString());
        Assert.Equal(
            Path.Combine("src", "module"),
            execution.GetProperty("repository_relative_path").GetString());
        Assert.Equal(
            "client_asserted",
            dispatched.GetProperty("call_attribution").GetProperty("strength").GetString());
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

    private static async ValueTask<bool> AuthorizePlanAndDispatchAsync(
        AuditCallContext context,
        ExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        if (!await context.AuthorizePlanAsync(plan, cancellationToken)) return false;
        return await context.AuthorizeDispatchAsync(
            ExecutionDispatch.FromPlan(plan),
            cancellationToken);
    }

    private static void AssertRouting(
        JsonElement value,
        string requestedRoute,
        string effectiveRoute,
        string provenance,
        string? fallbackReason)
    {
        var routing = value.GetProperty("routing");
        Assert.Equal("native_terminal", routing.GetProperty("domain").GetString());
        Assert.Equal(requestedRoute, routing.GetProperty("requested_route").GetString());
        Assert.Equal(effectiveRoute, routing.GetProperty("effective_route").GetString());
        Assert.Equal(
            ["powershell_direct"],
            routing.GetProperty("permitted_fallbacks")
                .EnumerateArray()
                .Select(item => item.GetString()));
        Assert.Equal(provenance, routing.GetProperty("provenance").GetString());
        if (fallbackReason is null)
            Assert.Equal(JsonValueKind.Null, routing.GetProperty("fallback_reason").ValueKind);
        else
            Assert.Equal(fallbackReason, routing.GetProperty("fallback_reason").GetString());
    }

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
