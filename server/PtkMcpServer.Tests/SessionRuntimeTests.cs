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
        try
        {
            Environment.SetEnvironmentVariable("PTK_CALL_TIMEOUT_SECONDS", configured);

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
