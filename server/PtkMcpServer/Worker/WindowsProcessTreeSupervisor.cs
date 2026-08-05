namespace PtkMcpServer.Worker;

internal enum WindowsProcessCreationMode
{
    Runnable,
    SuspendedForContainmentProof,
}

internal interface IWindowsWorkerNative
{
    IWindowsJobHandle CreateUnnamedJob();
    void SetJobLimitFlags(IWindowsJobHandle job, uint limitFlags);
    uint QueryJobLimitFlags(IWindowsJobHandle job);
    uint QueryJobActiveProcessCount(IWindowsJobHandle job);
    void TerminateJob(IWindowsJobHandle job);
    IWindowsJobEmptyObserver CreateJobEmptyObserver(IWindowsJobHandle job);
    IWindowsWorkerPipeSet CreateWorkerPipeSet();
    IWindowsProcessHandle CreateProcessInJob(
        WorkerLaunchCommand command,
        IWindowsJobHandle job,
        IWindowsWorkerPipeSet pipes,
        WindowsProcessCreationMode mode);
    bool IsProcessInJob(IWindowsProcessHandle process, IWindowsJobHandle job);
    void ResumePrimaryThreadForContainmentProof(IWindowsProcessHandle process);
}

internal interface IWindowsJobHandle : IDisposable;

internal interface IWindowsJobEmptyObserver : IDisposable
{
    Task WaitForEmptyAsync(CancellationToken cancellationToken = default);
}

internal interface IWindowsProcessHandle : IDisposable
{
    int ProcessId { get; }

    /// <summary>
    /// The exit code once the process has exited, or <see langword="null"/>
    /// while it runs and whenever the query fails. Never throws.
    /// </summary>
    int? ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}

internal interface IWindowsWorkerPipeSet : IDisposable
{
    int ChildHandleCount { get; }
    Stream RequestWriter { get; }
    Stream EventReader { get; }
    Stream StandardOutputReader { get; }
    Stream StandardErrorReader { get; }
    void CloseChildEnds();
}

internal sealed class WindowsProcessTreeSupervisor
{
    internal const uint KillOnJobClose = 0x00002000;

    private const int RequiredChildHandleCount = 5;
    private readonly IWindowsWorkerNative _native;
    private readonly Func<bool> _isWindows;

    internal WindowsProcessTreeSupervisor()
        : this(new WindowsWorkerNative())
    {
    }

    internal WindowsProcessTreeSupervisor(
        IWindowsWorkerNative native,
        Func<bool>? isWindows = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
    }

    internal ContainedWindowsWorker Launch(
        WorkerLaunchCommand command,
        CancellationToken cancellationToken = default) =>
        Launch(command, WindowsProcessCreationMode.Runnable, cancellationToken);

    internal ContainedWindowsWorker Launch(
        WorkerLaunchCommand command,
        WindowsProcessCreationMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (mode is not WindowsProcessCreationMode.Runnable and
            not WindowsProcessCreationMode.SuspendedForContainmentProof)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (!_isWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows worker containment requires Windows 10 or Windows Server 2016 or newer.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        IWindowsJobHandle? job = null;
        IWindowsJobEmptyObserver? emptyObserver = null;
        IWindowsWorkerPipeSet? pipes = null;
        IWindowsProcessHandle? process = null;
        try
        {
            job = InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.CreateJob,
                () => _native.CreateUnnamedJob() ??
                    throw new InvalidOperationException("Native job creation returned no job handle."));
            InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.ConfigureJob,
                () => _native.SetJobLimitFlags(job, KillOnJobClose));
            var configuredFlags = InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.QueryJob,
                () => _native.QueryJobLimitFlags(job));
            if (configuredFlags != KillOnJobClose)
            {
                throw new WorkerLaunchException(
                    "containment_setup_failed",
                    WorkerLaunchStage.QueryJob);
            }

            cancellationToken.ThrowIfCancellationRequested();
            emptyObserver = InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.QueryJob,
                () => _native.CreateJobEmptyObserver(job) ??
                    throw new InvalidOperationException(
                        "Native job observation returned no owner."));

            cancellationToken.ThrowIfCancellationRequested();
            pipes = InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.CreatePipe,
                () => _native.CreateWorkerPipeSet() ??
                    throw new InvalidOperationException("Native pipe creation returned no pipe set."));
            var childHandleCount = InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.CreatePipe,
                () => pipes.ChildHandleCount);
            if (childHandleCount != RequiredChildHandleCount)
            {
                throw new WorkerLaunchException(
                    "containment_setup_failed",
                    WorkerLaunchStage.CreatePipe);
            }

            cancellationToken.ThrowIfCancellationRequested();
            process = InvokeStage(
                "worker_create_failed",
                WorkerLaunchStage.CreateProcess,
                () => _native.CreateProcessInJob(command, job, pipes, mode) ??
                    throw new InvalidOperationException("Native process creation returned no process handle."));
            InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.CloseChildHandles,
                pipes.CloseChildEnds);

            var containedAtCreation = InvokeStage(
                "containment_verification_failed",
                WorkerLaunchStage.VerifyContainment,
                () => _native.IsProcessInJob(process, job));
            if (!containedAtCreation)
            {
                throw new WorkerLaunchException(
                    "containment_verification_failed",
                    WorkerLaunchStage.VerifyContainment);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var contained = new ContainedWindowsWorker(
                _native,
                job,
                emptyObserver,
                process,
                pipes,
                mode);
            job = null;
            emptyObserver = null;
            process = null;
            pipes = null;
            return contained;
        }
        catch (Exception exception)
        {
            // Closing the sole job handle is the first rollback action after a
            // process exists. That contains the entire tree before ordinary
            // process/stream handle cleanup, including when cleanup itself fails.
            Task? containmentEmpty = null;
            try
            {
                if (job is not null && emptyObserver is not null &&
                    JobMayContainProcesses(job))
                {
                    containmentEmpty = ObserveAfterJobTermination(
                        job,
                        emptyObserver);
                    job = null;
                    emptyObserver = null;
                }
                else
                {
                    DisposeIgnoringFailure(job);
                    DisposeIgnoringFailure(emptyObserver);
                }
            }
            finally
            {
                DisposeIgnoringFailure(process);
                DisposeIgnoringFailure(pipes);
            }

            if (containmentEmpty is null ||
                containmentEmpty.IsCompletedSuccessfully)
                throw;
            throw AttachContainment(exception, containmentEmpty);
        }
    }

    private bool JobMayContainProcesses(IWindowsJobHandle job)
    {
        try
        {
            return _native.QueryJobActiveProcessCount(job) != 0;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return true;
        }
    }

    private Task ObserveAfterJobTermination(
        IWindowsJobHandle job,
        IWindowsJobEmptyObserver observer)
    {
        Task empty;
        try
        {
            empty = observer.WaitForEmptyAsync(CancellationToken.None);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            empty = Task.FromException(exception);
        }
        catch
        {
            DisposeIgnoringFailure(observer);
            throw;
        }

        try
        {
            _native.TerminateJob(job);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            DisposeIgnoringFailure(job);
            DisposeIgnoringFailure(observer);
            return Task.FromException(exception);
        }
        catch
        {
            DisposeIgnoringFailure(job);
            DisposeIgnoringFailure(observer);
            throw;
        }

        return CompleteObservationAsync(empty, job, observer);
    }

    private static async Task CompleteObservationAsync(
        Task empty,
        IWindowsJobHandle job,
        IWindowsJobEmptyObserver observer)
    {
        try
        {
            await empty.ConfigureAwait(false);
        }
        finally
        {
            DisposeIgnoringFailure(job);
            DisposeIgnoringFailure(observer);
        }
    }

    private static Exception AttachContainment(
        Exception exception,
        Task containmentEmpty) =>
        exception is WorkerLaunchException launch
            ? new WorkerLaunchException(
                launch.DetailCode,
                launch.Stage,
                launch.NativeErrorCode,
                launch,
                containmentEmpty)
            : new WorkerProcessException(
                "worker_launch_containment_unconfirmed",
                exception,
                containmentEmpty);

    private static void DisposeIgnoringFailure(IDisposable? value)
    {
        try
        {
            value?.Dispose();
        }
        catch
        {
            // Preserve the launch-stage failure after attempting every cleanup.
        }
    }

    private static T InvokeStage<T>(
        string detailCode,
        WorkerLaunchStage stage,
        Func<T> action)
    {
        try
        {
            return action();
        }
        catch (WorkerLaunchException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new WorkerLaunchException(detailCode, stage, innerException: exception);
        }
    }

    private static void InvokeStage(
        string detailCode,
        WorkerLaunchStage stage,
        Action action) =>
        InvokeStage(
            detailCode,
            stage,
            () =>
            {
                action();
                return true;
            });

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}

internal sealed class ContainedWindowsWorker : IWorkerContainedProcess
{
    private static readonly TimeSpan ContainmentConfirmationGrace =
        TimeSpan.FromSeconds(10);

    private readonly object _containmentGate = new();
    private readonly TaskCompletionSource _containmentEmpty = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Ownership? _ownership;
    private Task<WorkerContainmentResult>? _containment;

    internal ContainedWindowsWorker(
        IWindowsWorkerNative native,
        IWindowsJobHandle job,
        IWindowsJobEmptyObserver emptyObserver,
        IWindowsProcessHandle process,
        IWindowsWorkerPipeSet pipes,
        WindowsProcessCreationMode mode)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(emptyObserver);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(pipes);
        _ownership = new Ownership(
            native,
            job,
            emptyObserver,
            process,
            pipes,
            mode);
    }

    public int ProcessId => Current.Process.ProcessId;
    public int ContainmentProcessId => ProcessId;
    public Stream RequestWriter => Current.Pipes.RequestWriter;
    public Stream EventReader => Current.Pipes.EventReader;
    public Stream StandardOutputReader => Current.Pipes.StandardOutputReader;
    public Stream StandardErrorReader => Current.Pipes.StandardErrorReader;

    // Null once ownership is released: the handle is gone and there is
    // nothing left to ask. Reported only to explain a death, so an
    // unanswerable query is absent rather than an error.
    public int? ExitCode => _ownership?.Process.ExitCode;

    public Task ContainmentEmpty => _containmentEmpty.Task;

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        Current.Process.WaitForExitAsync(cancellationToken);

    public Task<WorkerContainmentResult> ContainAsync(
        WorkerContainmentReason reason)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
        lock (_containmentGate)
            return _containment ??= ContainCoreAsync();
    }

    private async Task<WorkerContainmentResult> ContainCoreAsync()
    {
        var ownership = Interlocked.Exchange(ref _ownership, null);
        if (ownership is null)
        {
            return WorkerContainmentResult.Unknown(
                "windows_worker_containment_unconfirmed");
        }

        Task observation;
        try
        {
            observation = ownership.EmptyObserver.WaitForEmptyAsync(
                CancellationToken.None);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            DisposeOwnership(ownership, includeObserver: true);
            return WorkerContainmentResult.Unknown(
                "windows_worker_containment_unconfirmed");
        }
        catch
        {
            DisposeOwnership(ownership, includeObserver: true);
            throw;
        }

        try
        {
            ownership.Native.TerminateJob(ownership.Job);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            DisposeOwnership(ownership, includeObserver: true);
            return WorkerContainmentResult.Unknown(
                "windows_worker_containment_unconfirmed");
        }
        catch
        {
            DisposeOwnership(ownership, includeObserver: true);
            throw;
        }

        DisposeIgnoringFailure(ownership.Process);
        DisposeIgnoringFailure(ownership.Pipes);

        var bounded = await Task.WhenAny(
            observation,
            Task.Delay(ContainmentConfirmationGrace)).ConfigureAwait(false);
        if (bounded == observation)
        {
            try
            {
                await observation.ConfigureAwait(false);
                _containmentEmpty.TrySetResult();
                DisposeIgnoringFailure(ownership.Job);
                DisposeIgnoringFailure(ownership.EmptyObserver);
                return WorkerContainmentResult.Confirmed();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                DisposeIgnoringFailure(ownership.Job);
                DisposeIgnoringFailure(ownership.EmptyObserver);
                return WorkerContainmentResult.Unknown(
                    "windows_worker_containment_unconfirmed");
            }
        }

        _ = CompleteLaterAsync(
            observation,
            ownership.Job,
            ownership.EmptyObserver);
        return WorkerContainmentResult.Unknown("descendants_unknown");
    }

    private async Task CompleteLaterAsync(
        Task observation,
        IWindowsJobHandle job,
        IWindowsJobEmptyObserver observer)
    {
        try
        {
            await observation.ConfigureAwait(false);
            _containmentEmpty.TrySetResult();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
        finally
        {
            DisposeIgnoringFailure(job);
            DisposeIgnoringFailure(observer);
        }
    }

    internal void ResumeForContainmentProof()
    {
        var ownership = Current;
        if (ownership.Mode != WindowsProcessCreationMode.SuspendedForContainmentProof)
        {
            throw new InvalidOperationException(
                "Only a containment-proof worker may be resumed explicitly.");
        }

        if (Interlocked.CompareExchange(ref ownership.ResumeAttempted, 1, 0) != 0)
            throw new InvalidOperationException("The containment-proof worker resume is one-shot.");

        try
        {
            ownership.Native.ResumePrimaryThreadForContainmentProof(ownership.Process);
        }
        catch (WorkerLaunchException)
        {
            // Resume is intentionally not retryable. Kill the still-contained
            // process tree before releasing its remaining handles.
            DisposeIgnoringFailure();
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var failure = new WorkerLaunchException(
                "containment_resume_failed",
                WorkerLaunchStage.ResumePrimaryThread,
                innerException: exception);
            DisposeIgnoringFailure();
            throw failure;
        }
    }

    public void Dispose()
    {
        var ownership = Interlocked.Exchange(ref _ownership, null);
        if (ownership is null)
            return;

        List<Exception>? failures = null;
        DisposeAndCapture(ownership.Job, ref failures);
        DisposeAndCapture(ownership.EmptyObserver, ref failures);
        DisposeAndCapture(ownership.Process, ref failures);
        DisposeAndCapture(ownership.Pipes, ref failures);

        if (failures is { Count: 1 })
            throw failures[0];
        if (failures is { Count: > 1 })
            throw new AggregateException("Contained worker cleanup failed.", failures);
    }

    private void DisposeIgnoringFailure()
    {
        try
        {
            Dispose();
        }
        catch
        {
            // Preserve the resume-stage failure after attempting every cleanup.
        }
    }

    private Ownership Current => Volatile.Read(ref _ownership) ??
        throw new ObjectDisposedException(nameof(ContainedWindowsWorker));

    private static void DisposeAndCapture(IDisposable value, ref List<Exception>? failures)
    {
        try
        {
            value.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
    }

    private static void DisposeOwnership(
        Ownership ownership,
        bool includeObserver)
    {
        DisposeIgnoringFailure(ownership.Job);
        if (includeObserver)
            DisposeIgnoringFailure(ownership.EmptyObserver);
        DisposeIgnoringFailure(ownership.Process);
        DisposeIgnoringFailure(ownership.Pipes);
    }

    private static void DisposeIgnoringFailure(IDisposable value)
    {
        try
        {
            value.Dispose();
        }
        catch
        {
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class Ownership(
        IWindowsWorkerNative native,
        IWindowsJobHandle job,
        IWindowsJobEmptyObserver emptyObserver,
        IWindowsProcessHandle process,
        IWindowsWorkerPipeSet pipes,
        WindowsProcessCreationMode mode)
    {
        internal IWindowsWorkerNative Native { get; } = native;
        internal IWindowsJobHandle Job { get; } = job;
        internal IWindowsJobEmptyObserver EmptyObserver { get; } =
            emptyObserver;
        internal IWindowsProcessHandle Process { get; } = process;
        internal IWindowsWorkerPipeSet Pipes { get; } = pipes;
        internal WindowsProcessCreationMode Mode { get; } = mode;
        internal int ResumeAttempted;
    }
}
