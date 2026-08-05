using PtkMcpServer.Sessions;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class SessionRuntimeTests
{
    /// <summary>
    /// Finding opr-10: the timeout predicate accepted any parsed double
    /// greater than zero, so values that cannot become a legal timeout
    /// survived parsing and threw later — at
    /// <see cref="TimeSpan.FromSeconds"/> for infinity, or at
    /// WorkerOperationProtocol.CreateLimits for a fractional or
    /// out-of-range value. Supervisor mode reads these before the MCP
    /// handshake, so an operator typo produced an unhandled startup failure
    /// and no server instead of the documented fallback.
    /// </summary>
    [Theory]
    // Rejected: must fall back rather than throw anywhere downstream.
    [InlineData("1e400", 300)]   // parses to +infinity
    [InlineData("1.5", 300)]     // not whole seconds
    [InlineData("0.5", 300)]     // rounds below the one-second floor
    [InlineData("0.0001", 300)]  // sub-millisecond
    [InlineData("86401", 300)]   // past WorkerOperationProtocol.MaximumTimeoutSeconds
    [InlineData("0", 300)]
    [InlineData("-5", 300)]
    [InlineData("abc", 300)]
    [InlineData("", 300)]
    [InlineData("NaN", 300)]
    // Accepted, including both boundaries.
    [InlineData("1", 1)]
    [InlineData("300", 300)]
    [InlineData("86400", 86400)]
    public void Call_timeout_falls_back_unless_the_value_is_a_legal_timeout(
        string configured,
        int expectedSeconds)
    {
        var previous = Environment.GetEnvironmentVariable("PTK_CALL_TIMEOUT_SECONDS");
        var previousMax = Environment.GetEnvironmentVariable("PTK_MAX_CALL_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("PTK_CALL_TIMEOUT_SECONDS", configured);
            // Hold the maximum at the ceiling so this theory judges the call
            // value on its own merits; the pair rule has its own theory
            // below, and without this the 86400 case falls back because it
            // exceeds the default 3600 maximum.
            Environment.SetEnvironmentVariable("PTK_MAX_CALL_TIMEOUT_SECONDS", "86400");

            // Must not throw for any input, and must land on a value the
            // worker protocol will actually accept.
            var timeout = DefaultSessionRuntimeFactory.ReadCallTimeout();

            Assert.Equal(expectedSeconds, timeout.TotalSeconds);
            _ = WorkerOperationProtocol.CreateLimits(
                timeout,
                TimeSpan.FromSeconds(WorkerOperationProtocol.MaximumTimeoutSeconds));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_CALL_TIMEOUT_SECONDS", previous);
            Environment.SetEnvironmentVariable("PTK_MAX_CALL_TIMEOUT_SECONDS", previousMax);
        }
    }

    /// <summary>
    /// opr-10, the pair half: each value can be individually legal and the
    /// PAIR still illegal, because CreateLimits rejects a default greater
    /// than the maximum. Validating the two variables independently does not
    /// cover it, and the throw lands in the same place — before the MCP
    /// handshake in supervisor mode, before initialize in worker mode.
    /// </summary>
    [Theory]
    [InlineData("3000", "100")]    // both legal alone, inverted together
    [InlineData("86400", "1")]     // both boundaries, inverted together
    // Finding o10-1: the simplest trigger of all -- lower the maximum alone
    // and leave the call timeout unset, so the 300 default exceeds it.
    [InlineData(null, "100")]
    [InlineData("300", "3600")]    // the shipped defaults
    [InlineData("1", "86400")]
    [InlineData("abc", "xyz")]     // both fall back
    public void Configured_timeout_pair_is_always_one_the_protocol_accepts(
        string? call,
        string maximum)
    {
        var previousCall = Environment.GetEnvironmentVariable("PTK_CALL_TIMEOUT_SECONDS");
        var previousMax = Environment.GetEnvironmentVariable("PTK_MAX_CALL_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("PTK_CALL_TIMEOUT_SECONDS", call);
            Environment.SetEnvironmentVariable("PTK_MAX_CALL_TIMEOUT_SECONDS", maximum);

            var callTimeout = DefaultSessionRuntimeFactory.ReadCallTimeout();
            var maxTimeout = DefaultSessionRuntimeFactory.ReadMaxCallTimeout();

            // Exactly what Program.cs and WorkerProcessEntry do next. It must
            // not throw for any configured pair.
            var limits = WorkerOperationProtocol.CreateLimits(callTimeout, maxTimeout);

            Assert.True(limits.DefaultTimeoutSeconds <= limits.MaximumTimeoutSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_CALL_TIMEOUT_SECONDS", previousCall);
            Environment.SetEnvironmentVariable("PTK_MAX_CALL_TIMEOUT_SECONDS", previousMax);
        }
    }

    [Fact]
    public async Task Dispose_terminates_owned_runspace()
    {
        var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60));
        var runtime = new SessionRuntime(host, new RawUsageCounter());
        try
        {
            runtime.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => host.InvokeAsync("'must not run'"));
        }
        finally
        {
            host.Dispose();
        }
    }
}
