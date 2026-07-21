using PtkMcpGuardian.Ownership;

namespace PtkMcpServer.Sessions;

/// <summary>
/// One construction path for the existing default runtime. Transitional public
/// mode and the private host freeze their inputs at process startup; worker mode
/// invokes the same path only from WorkerServer's validated initialize factory.
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
        JobPwshExecutable jobPwshExecutable,
        CancellationToken cancellationToken = default)
        => Create(
            callTimeout,
            maxCallTimeout,
            jobPwshExecutable,
            new MonotonicPublicJobIdAllocator(),
            cancellationToken,
            allowColdBackground: true);

    internal static SessionRuntime Create(
        TimeSpan callTimeout,
        TimeSpan maxCallTimeout,
        JobPwshExecutable jobPwshExecutable,
        IPublicJobIdAllocator publicJobIdAllocator,
        CancellationToken cancellationToken = default,
        bool allowColdBackground = true)
    {
        ArgumentNullException.ThrowIfNull(publicJobIdAllocator);
        cancellationToken.ThrowIfCancellationRequested();
        RunspaceHost? host = null;
        JobManager? jobs = null;
        try
        {
            host = new RunspaceHost(callTimeout, maxCallTimeout: maxCallTimeout);
            cancellationToken.ThrowIfCancellationRequested();
            jobs = new JobManager(
                publicJobIdAllocator,
                jobPwshExecutable,
                allowColdBackground: allowColdBackground);
            cancellationToken.ThrowIfCancellationRequested();

            var runtime = new SessionRuntime(host, jobs, new RawUsageCounter());
            host = null;
            jobs = null;
            return runtime;
        }
        finally
        {
            jobs?.Dispose();
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
