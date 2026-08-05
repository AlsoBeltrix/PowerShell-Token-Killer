using System.Globalization;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Sessions;

/// <summary>
/// One construction path for the existing default runtime. Supervisor mode
/// freezes its inputs at supervisor startup; worker mode invokes the same path
/// only from WorkerServer's validated initialize factory.
/// </summary>
internal static class DefaultSessionRuntimeFactory
{
    internal static TimeSpan ReadCallTimeout() =>
        ReadPositiveSeconds("PTK_CALL_TIMEOUT_SECONDS", 300);

    internal static TimeSpan ReadMaxCallTimeout() =>
        ReadPositiveSeconds("PTK_MAX_CALL_TIMEOUT_SECONDS", 3600);

    internal static SessionRuntime Create(
        TimeSpan callTimeout,
        TimeSpan maxCallTimeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunspaceHost? host = null;
        try
        {
            host = new RunspaceHost(callTimeout, maxCallTimeout: maxCallTimeout);
            cancellationToken.ThrowIfCancellationRequested();

            var runtime = new SessionRuntime(host, new RawUsageCounter());
            host = null;
            return runtime;
        }
        finally
        {
            host?.Dispose();
        }
    }

    /// <summary>
    /// Reads a configured timeout, falling back unless the value is one the
    /// whole pipeline will actually accept.
    /// </summary>
    /// <remarks>
    /// Finding opr-10. The predicate was <c>seconds &gt; 0</c>, which is not
    /// the real contract and let three classes of value through to throw
    /// later: an overflowing exponent parses to positive infinity and throws
    /// in <see cref="TimeSpan.FromSeconds"/>; a fractional or sub-second
    /// value converts fine and then throws in
    /// <c>WorkerOperationProtocol.CreateLimits</c>, which requires whole
    /// seconds; and a value past
    /// <c>WorkerOperationProtocol.MaximumTimeoutSeconds</c> throws there too.
    /// Supervisor mode reads both variables before constructing the MCP
    /// server, so an operator typo produced an unhandled startup exception
    /// and no handshake rather than the documented fallback. Validate against
    /// the downstream contract here, at the one place the value enters.
    /// </remarks>
    private static TimeSpan ReadPositiveSeconds(string variable, double fallbackSeconds) =>
        TimeSpan.FromSeconds(
            double.TryParse(
                Environment.GetEnvironmentVariable(variable),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var seconds) &&
            double.IsFinite(seconds) &&
            Math.Floor(seconds) == seconds &&
            seconds >= 1 &&
            seconds <= WorkerOperationProtocol.MaximumTimeoutSeconds
                ? seconds
                : fallbackSeconds);
}
