namespace PtkMcpServer.Worker;

internal enum WorkerContainmentReason
{
    Close,
    Reset,
    Timeout,
    SupervisorShutdown,
    LaunchFailure,
}

internal enum WorkerContainmentOutcome
{
    ConfirmedEmpty,
    DescendantsUnknown,
}

internal readonly record struct WorkerContainmentResult(
    WorkerContainmentOutcome Outcome,
    string DetailCode)
{
    internal static WorkerContainmentResult Confirmed() =>
        new(WorkerContainmentOutcome.ConfirmedEmpty, "containment_confirmed");

    internal static WorkerContainmentResult Unknown(string detailCode) =>
        new(WorkerContainmentOutcome.DescendantsUnknown, detailCode);
}

internal interface IWorkerContainedProcess : IDisposable
{
    int ProcessId { get; }
    int ContainmentProcessId { get; }
    Stream RequestWriter { get; }
    Stream EventReader { get; }
    Stream StandardOutputReader { get; }
    Stream StandardErrorReader { get; }
    Task ContainmentEmpty { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
    Task<WorkerContainmentResult> ContainAsync(WorkerContainmentReason reason);
}

internal interface IWorkerProcessLauncher
{
    Task<IWorkerContainedProcess> LaunchAsync(
        WorkerLaunchCommand command,
        CancellationToken cancellationToken = default);
}

internal sealed class WorkerProcessException : Exception
{
    internal WorkerProcessException(
        string detailCode,
        Exception? innerException = null,
        Task? containmentEmpty = null)
        : base($"Worker process authority failed ({detailCode}).", innerException)
    {
        DetailCode = detailCode;
        ContainmentEmpty = containmentEmpty;
    }

    internal string DetailCode { get; }
    internal Task? ContainmentEmpty { get; }
}

/// <summary>
/// One instance belongs to one future session slot. A launch failure or
/// containment attempt that cannot prove the old domain empty keeps this
/// launcher closed until its exact observer later completes.
/// </summary>
internal sealed class SingleDomainWorkerProcessLauncher(
    IWorkerProcessLauncher inner) : IWorkerProcessLauncher
{
    private readonly Lock _gate = new();
    private Task? _activeContainment;
    private bool _launching;

    public async Task<IWorkerContainedProcess> LaunchAsync(
        WorkerLaunchCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (_launching ||
                _activeContainment is { Status: not TaskStatus.RanToCompletion })
            {
                throw new WorkerProcessException(
                    "previous_containment_unconfirmed",
                    containmentEmpty: _activeContainment);
            }

            _activeContainment = null;
            _launching = true;
        }

        try
        {
            var process = await inner.LaunchAsync(command, cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
                _activeContainment = process.ContainmentEmpty;
            return process;
        }
        catch (WorkerProcessException exception)
            when (exception.ContainmentEmpty is not null)
        {
            lock (_gate)
                _activeContainment = exception.ContainmentEmpty;
            throw;
        }
        catch (WorkerLaunchException exception)
            when (exception.ContainmentEmpty is not null)
        {
            lock (_gate)
                _activeContainment = exception.ContainmentEmpty;
            throw;
        }
        finally
        {
            lock (_gate)
                _launching = false;
        }
    }
}

internal sealed class WindowsWorkerProcessLauncher : IWorkerProcessLauncher
{
    private readonly WindowsProcessTreeSupervisor _supervisor;

    internal WindowsWorkerProcessLauncher()
        : this(new WindowsProcessTreeSupervisor())
    {
    }

    internal WindowsWorkerProcessLauncher(WindowsProcessTreeSupervisor supervisor)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
    }

    public Task<IWorkerContainedProcess> LaunchAsync(
        WorkerLaunchCommand command,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IWorkerContainedProcess>(
            _supervisor.Launch(command, cancellationToken));
}

internal static class WorkerProcessLauncher
{
    internal static IWorkerProcessLauncher Create(string? unixBrokerPath = null)
    {
        IWorkerProcessLauncher platform = OperatingSystem.IsWindows()
            ? new WindowsWorkerProcessLauncher()
            : new UnixWorkerProcessLauncher(
                RequireUnixBrokerPath(unixBrokerPath),
                new UnixWorkerContainmentRegistry());
        return new SingleDomainWorkerProcessLauncher(platform);
    }

    private static string RequireUnixBrokerPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A Unix worker broker path is required.",
                nameof(path));
        }

        return path;
    }
}
