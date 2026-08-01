using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditCallMetadataTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 34, 56, TimeSpan.Zero);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromHours(1);

    [Fact]
    public void Invoke_capture_preserves_field_presence_defaults_and_exact_script_separately()
    {
        var call = Call(
            "ptk_invoke",
            ("raw", false),
            ("script", "Get-Process"),
            ("session", "exchange-online"));
        var client = new AuditClientContext("Claude Code", "2.1.207", "stdio-7");

        Assert.True(Capture(call, client, out var metadata, out var script, out var failure));

        Assert.Null(failure);
        Assert.Equal("Get-Process", script);
        Assert.Equal("mcp_stdio", metadata!.Actor.Transport);
        Assert.Equal("Claude Code", metadata.Actor.ClientName);
        Assert.Equal("2.1.207", metadata.Actor.ClientVersion);
        Assert.Equal("stdio-7", metadata.Actor.ClientSessionId);
        Assert.Equal("client_asserted", metadata.Actor.AttributionStrength);
        Assert.Equal("ptk_invoke", metadata.Request.Tool);
        Assert.Equal("invoke", metadata.Request.Action);
        Assert.Equal(["raw", "script", "session"], metadata.Request.ProvidedFields);
        Assert.Equal("exchange-online", metadata.Request.SessionRequested);
        Assert.Equal("auto", metadata.Request.Route);
        Assert.Null(metadata.Request.Background);
        Assert.False(metadata.Request.Raw);
        Assert.Equal(300_000, metadata.Request.TimeoutMs);
        Assert.Equal(Now.AddMinutes(5), metadata.Request.DeadlineUtc);
        Assert.DoesNotContain("Get-Process", metadata.ToString(), StringComparison.Ordinal);
        Assert.True(metadata.OperationProfile.RequiresScriptEvidence);
        Assert.True(metadata.OperationProfile.MayHaveSideEffects);
        Assert.Equal(11, metadata.OperationProfile.MaximumRecordSlots);
    }

    [Fact]
    public void Invoke_route_and_timeout_normalization_match_current_tool_behavior()
    {
        var forced = Call(
            "ptk_invoke",
            ("script", "'ok'"),
            ("route", "PWSH"),
            ("timeoutSeconds", 7_200));
        Assert.True(Capture(forced, new(), out var metadata, out _, out _));
        Assert.Equal("pwsh", metadata!.Request.Route);
        Assert.Equal(3_600_000, metadata.Request.TimeoutMs);
        Assert.Equal(Now.AddHours(1), metadata.Request.DeadlineUtc);

        var unknownRoute = Call("ptk_invoke", ("script", "'ok'"), ("route", "future-route"));
        Assert.True(Capture(unknownRoute, new(), out metadata, out _, out _));
        Assert.Equal("auto", metadata!.Request.Route);

        var negativeOverride = Call("ptk_invoke", ("script", "'ok'"), ("timeoutSeconds", -10));
        Assert.True(Capture(negativeOverride, new(), out metadata, out _, out _));
        Assert.Equal(300_000, metadata!.Request.TimeoutMs);
    }

    [Fact]
    public void Invoke_profile_reserves_only_bounded_call_records()
    {
        var call = Call("ptk_invoke", ("script", "dotnet test"));

        Assert.True(Capture(call, new(), out var metadata, out _, out _));

        var profile = metadata!.OperationProfile;
        Assert.Equal(11, profile.MaximumRecordSlots);
        Assert.Equal(11L * 65_536, profile.MaximumReservationBytes(65_536));
    }

    [Fact]
    public void Session_capture_records_explicit_lifecycle_target_without_job_fields()
    {
        Assert.True(Capture(
            Call("ptk_session", ("action", "open"), ("name", "exchange-online")),
            new(),
            out var opened,
            out var script,
            out _));

        Assert.Null(script);
        Assert.Equal("open", opened!.Request.Action);
        Assert.Equal("exchange-online", opened.Request.SessionRequested);
        Assert.Equal(["action", "name"], opened.Request.ProvidedFields);
        Assert.Null(opened.Request.JobId);
        Assert.True(opened.OperationProfile.MayHaveSideEffects);
        Assert.Equal(4, opened.OperationProfile.MaximumRecordSlots);

        Assert.True(Capture(
            Call("ptk_session", ("action", "list")),
            new(),
            out var listed,
            out _,
            out _));
        Assert.Null(listed!.Request.SessionRequested);
        Assert.False(listed.OperationProfile.MayHaveSideEffects);
        Assert.Equal(2, listed.OperationProfile.MaximumRecordSlots);
    }

    [Fact]
    public void Session_action_and_name_requirements_fail_closed()
    {
        AssertRejected(
            Call("ptk_session", ("action", "open")),
            "name is required for this action");
        AssertRejected(
            Call("ptk_session", ("action", "list"), ("name", "exchange-online")),
            "list does not accept name");
        AssertRejected(
            Call("ptk_session", ("action", "future")),
            "action is unsupported");
    }
    [Fact]
    public void State_and_reset_capture_effective_current_defaults()
    {
        Assert.True(Capture(Call("ptk_state"), new(), out var state, out _, out _));
        Assert.Equal("state", state!.Request.Action);
        Assert.False(state.Request.ListAvailable);
        Assert.Empty(state.Request.ProvidedFields);
        Assert.Equal("default", state.Request.SessionRequested);
        Assert.Equal("transport_only", state.Actor.AttributionStrength);
        Assert.True(state.OperationProfile.MayHaveSideEffects);
        Assert.Equal(5, state.OperationProfile.MaximumRecordSlots);

        Assert.True(Capture(
            Call("ptk_reset", ("session", "exchange-onprem")),
            new(),
            out var reset,
            out _,
            out _));
        Assert.Equal("reset", reset!.Request.Action);
        Assert.Equal("exchange-onprem", reset.Request.SessionRequested);
        Assert.True(reset.OperationProfile.MayHaveSideEffects);
        Assert.Equal(4, reset.OperationProfile.MaximumRecordSlots);
    }

    [Fact]
    public void Output_capture_validates_shape_and_records_only_protected_fields()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        using var protector = new AuditOutputRequestProtector(key);
        const string handle = "ptko_super-secret-capability";
        const string pattern = "token=secret";
        var call = Call(
            "ptk_output",
            ("pattern", pattern),
            ("handle", handle),
            ("maxBytes", 512),
            ("action", "SEARCH"),
            ("offset", 7L));

        Assert.True(Capture(
            call,
            new(),
            out var metadata,
            out var script,
            out var failure,
            protector));

        Assert.Null(script);
        Assert.Null(failure);
        Assert.Equal("ptk_output", metadata!.Request.Tool);
        Assert.Equal("search", metadata.Request.Action);
        Assert.Equal(7, metadata.Request.Offset);
        Assert.Equal(512, metadata.Request.MaxBytes);
        Assert.Equal(["action", "handle", "maxBytes", "offset", "pattern"], metadata.Request.ProvidedFields);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(handle))).ToLowerInvariant(),
            metadata.Request.OutputHandleDigest);
        var hmacInput = Encoding.UTF8.GetBytes("ptk.output-pattern/1\0" + pattern);
        Assert.Equal(
            Convert.ToHexString(HMACSHA256.HashData(key, hmacInput)).ToLowerInvariant(),
            metadata.Request.PatternFingerprint);
        Assert.Equal(3, metadata.OperationProfile.MaximumRecordSlots);
        Assert.False(metadata.OperationProfile.MayHaveSideEffects);
        Assert.False(metadata.OperationProfile.RequiresScriptEvidence);
        Assert.DoesNotContain(handle, metadata.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(pattern, metadata.ToString(), StringComparison.Ordinal);

        Assert.True(Capture(
            Call("ptk_output", ("handle", handle)),
            new(),
            out var read,
            out _,
            out _,
            protector));
        Assert.Equal("read", read!.Request.Action);
        Assert.Equal(0, read.Request.Offset);
        Assert.Equal(OutputStore.DefaultReadBytes, read.Request.MaxBytes);
        Assert.Null(read.Request.PatternFingerprint);
    }

    [Fact]
    public void Output_list_capture_needs_no_handle_protector_and_records_session_filter()
    {
        Assert.True(Capture(
            Call(
                "ptk_output",
                ("session", "paid-review"),
                ("action", "LIST")),
            new(),
            out var metadata,
            out var script,
            out var failure));

        Assert.Null(script);
        Assert.Null(failure);
        Assert.Equal("ptk_output", metadata!.Request.Tool);
        Assert.Equal("list", metadata.Request.Action);
        Assert.Equal("paid-review", metadata.Request.SessionRequested);
        Assert.Equal(["action", "session"], metadata.Request.ProvidedFields);
        Assert.Null(metadata.Request.Offset);
        Assert.Null(metadata.Request.MaxBytes);
        Assert.Null(metadata.Request.PatternFingerprint);
        Assert.Null(metadata.Request.OutputHandleDigest);
        Assert.Equal(3, metadata.OperationProfile.MaximumRecordSlots);
        Assert.False(metadata.OperationProfile.MayHaveSideEffects);
        Assert.False(metadata.OperationProfile.RequiresScriptEvidence);
    }

    [Fact]
    public void Output_capture_rejects_inapplicable_or_unbounded_fields_without_echoing_them()
    {
        using var protector = new AuditOutputRequestProtector(new byte[32]);
        AssertOutputRejected(Call("ptk_output"), "handle is missing", protector);
        AssertOutputRejected(
            Call("ptk_output", ("handle", "secret-handle"), ("action", "future")),
            "action is unsupported",
            protector,
            "secret-handle");
        AssertOutputRejected(
            Call("ptk_output", ("handle", "h"), ("offset", -1L)),
            "nonnegative int64",
            protector);
        AssertOutputRejected(
            Call("ptk_output", ("handle", "h"), ("maxBytes", OutputStore.MaximumReadBytes + 1)),
            "maxBytes",
            protector);
        AssertOutputRejected(
            Call("ptk_output", ("handle", "h"), ("action", "search")),
            "requires a bounded pattern",
            protector);
        AssertOutputRejected(
            Call(
                "ptk_output",
                ("handle", "h"),
                ("action", "search"),
                ("maxBytes", 3),
                ("pattern", "needle")),
            "maxBytes cannot contain its pattern",
            protector,
            "needle");
        AssertOutputRejected(
            Call("ptk_output", ("handle", "h"), ("pattern", "secret-pattern")),
            "inapplicable argument",
            protector,
            "secret-pattern");
        AssertOutputRejected(
            Call("ptk_output", ("handle", "h"), ("action", "status"), ("offset", 0L)),
            "inapplicable argument",
            protector);
        AssertOutputRejected(
            Call("ptk_output", ("action", "list"), ("handle", "secret-handle")),
            "inapplicable argument",
            protector,
            "secret-handle");
        AssertOutputRejected(
            Call("ptk_output", ("action", "list"), ("offset", 0L)),
            "inapplicable argument",
            protector);
        AssertOutputRejected(
            Call("ptk_output", ("action", "list"), ("maxBytes", 1)),
            "inapplicable argument",
            protector);
        AssertOutputRejected(
            Call("ptk_output", ("action", "list"), ("pattern", "secret-pattern")),
            "inapplicable argument",
            protector,
            "secret-pattern");
        AssertOutputRejected(
            Call("ptk_output", ("action", "list"), ("session", "Invalid Session")),
            "session is invalid",
            protector);
        AssertOutputRejected(
            Call("ptk_output", ("handle", "h"), ("session", "default")),
            "inapplicable session",
            protector);
        AssertOutputRejected(
            Call("ptk_output", ("handle", "h"), ("script", "Remove-Item secret")),
            "unknown argument field",
            protector,
            "Remove-Item secret");
    }

    [Fact]
    public void Unknown_tools_fields_and_actions_fail_closed_without_partial_metadata()
    {
        AssertRejected(Call("ptk_future"), "unknown tool");
        AssertRejected(Call("ptk_job", ("action", "list")), "unknown tool");
        AssertRejected(Call("ptk_state", ("futureField", "secret-value")), "unknown argument field", "secret-value");
        AssertRejected(Call("ptk_reset", ("force", true)), "unknown argument field");
        AssertRejected(Call("ptk_session", ("action", "future-action")), "action is unsupported");
    }

    [Fact]
    public void Wrong_json_kinds_ranges_and_missing_required_values_fail_before_acceptance()
    {
        AssertRejected(Call("ptk_invoke", ("script", 42)), "wrong JSON kind");
        AssertRejected(Call("ptk_invoke", ("script", "'ok'"), ("raw", "true")), "wrong JSON kind");
        AssertRejected(Call("ptk_invoke"), "required argument script is missing");
        AssertRejected(Call("ptk_invoke", ("script", "'ok'"), ("session", null)), "wrong JSON kind");
        AssertRejected(Call("ptk_state", ("listAvailable", 1)), "wrong JSON kind");
        AssertRejected(Call("ptk_state", ("session", "Not-Canonical")), "valid session name");
        AssertRejected(Call("ptk_session", ("action", "open"), ("name", 42)), "wrong JSON kind");
        AssertRejected(Call("ptk_session", ("action", null)), "wrong JSON kind");

        Assert.True(Capture(
            Call("ptk_invoke", ("script", "'ok'"), ("route", null)),
            new(),
            out var nullRoute,
            out _,
            out _));
        Assert.Equal("auto", nullRoute!.Request.Route);
        Assert.Contains("route", nullRoute.Request.ProvidedFields);

        AssertRejected(
            Call("ptk_session", ("action", "open"), ("name", null)),
            "name is required for this action");
    }

    [Fact]
    public void Oversize_script_is_never_truncated_or_copied_into_failure()
    {
        var secret = new string('x', 131_073);
        var call = Call("ptk_invoke", ("script", secret));

        Assert.False(Capture(call, new(), out var metadata, out var script, out var failure));

        Assert.Null(metadata);
        Assert.Null(script);
        Assert.Contains("script is not representable", failure);
        Assert.DoesNotContain(secret, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_assertions_are_bounded_and_never_mislabeled_authenticated()
    {
        Assert.True(Capture(
            Call("ptk_state"),
            new AuditClientContext(ClientName: "codex"),
            out var metadata,
            out _,
            out _));
        Assert.Equal("client_asserted", metadata!.Actor.AttributionStrength);
        Assert.NotEqual("authenticated", metadata.Actor.AttributionStrength);

        var secret = "token=" + new string('s', 300);
        Assert.False(Capture(
            Call("ptk_state"),
            new AuditClientContext(ClientName: secret),
            out metadata,
            out _,
            out var failure));
        Assert.Null(metadata);
        Assert.DoesNotContain(secret, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Nonintegral_or_non_utc_boundary_time_is_rejected_not_rounded()
    {
        Assert.False(AuditCallMetadataCapture.TryCapture(
            Call("ptk_state"),
            new(),
            TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond + 1),
            MaximumTimeout,
            Now,
            out _,
            out _,
            out var fractionalFailure));
        Assert.Contains("default timeout is not representable", fractionalFailure);

        Assert.False(AuditCallMetadataCapture.TryCapture(
            Call("ptk_state"),
            new(),
            DefaultTimeout,
            MaximumTimeout,
            Now.ToOffset(TimeSpan.FromHours(1)),
            out _,
            out _,
            out var clockFailure));
        Assert.Contains("clock is not UTC", clockFailure);
    }

    private static bool Capture(
        CallToolRequestParams call,
        AuditClientContext client,
        out AuditCallMetadata? metadata,
        out string? script,
        out string? failure,
        AuditOutputRequestProtector? outputProtector = null) =>
        AuditCallMetadataCapture.TryCapture(
            call,
            client,
            DefaultTimeout,
            MaximumTimeout,
            Now,
            out metadata,
            out script,
            out failure,
            outputProtector);

    private static void AssertOutputRejected(
        CallToolRequestParams call,
        string expectedFailure,
        AuditOutputRequestProtector protector,
        string? forbiddenValue = null)
    {
        Assert.False(Capture(
            call,
            new(),
            out var metadata,
            out var script,
            out var failure,
            protector));
        Assert.Null(metadata);
        Assert.Null(script);
        Assert.Contains(expectedFailure, failure);
        if (forbiddenValue is not null)
            Assert.DoesNotContain(forbiddenValue, failure, StringComparison.Ordinal);
    }

    private static void AssertRejected(
        CallToolRequestParams call,
        string expectedFailure,
        string? forbiddenValue = null)
    {
        Assert.False(Capture(call, new(), out var metadata, out var script, out var failure));
        Assert.Null(metadata);
        Assert.Null(script);
        Assert.Contains(expectedFailure, failure);
        if (forbiddenValue is not null)
            Assert.DoesNotContain(forbiddenValue, failure, StringComparison.Ordinal);
    }

    private static CallToolRequestParams Call(string name, params (string Name, object? Value)[] arguments)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (argumentName, value) in arguments)
            values.Add(argumentName, JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object)));

        return new CallToolRequestParams
        {
            Name = name,
            Arguments = values,
        };
    }
}
