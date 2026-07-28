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

    private static TimeSpan ReadPositiveSeconds(string variable, double fallbackSeconds) =>
        TimeSpan.FromSeconds(
            double.TryParse(Environment.GetEnvironmentVariable(variable), out var seconds) &&
            seconds > 0
                ? seconds
                : fallbackSeconds);
}
