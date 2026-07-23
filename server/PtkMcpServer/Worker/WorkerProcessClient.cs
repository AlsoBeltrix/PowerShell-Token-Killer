using System.Buffers;
using System.Security.Cryptography;

namespace PtkMcpServer.Worker;

internal interface IWorkerContainedProcess : IDisposable
{
    int ProcessId { get; }
    Stream RequestWriter { get; }
    Stream EventReader { get; }
    Stream StandardOutputReader { get; }
    Stream StandardErrorReader { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}

internal interface IWorkerProcessLauncher
{
    IWorkerContainedProcess Launch(
        WorkerLaunchCommand command,
        CancellationToken cancellationToken = default);
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

    public IWorkerContainedProcess Launch(
        WorkerLaunchCommand command,
        CancellationToken cancellationToken = default) =>
        _supervisor.Launch(command, cancellationToken);
}

internal sealed record WorkerDiagnosticSummary(
    long TotalBytes,
    int DigestedBytes,
    bool Truncated,
    string PrefixSha256);

internal sealed record WorkerDiagnosticReport(
    WorkerDiagnosticSummary StandardOutput,
    WorkerDiagnosticSummary StandardError);

internal sealed class WorkerProcessException : Exception
{
    internal WorkerProcessException(string detailCode, Exception? innerException = null)
        : base($"Worker process authority failed ({detailCode}).", innerException)
    {
        DetailCode = detailCode;
    }

    internal string DetailCode { get; }
}

/// <summary>
/// Owns one initialized worker client together with the creation-time
/// containment that makes its process tree disposable as one unit. Process
/// exit and protocol failure converge on the same exactly-once containment
/// path. Diagnostic streams are continuously drained into bounded, content-free
/// summaries so user output can never become a protocol frame or host log.
/// </summary>
internal sealed class WorkerProcessClient : IAsyncDisposable
{
    internal const int MaximumDiagnosticBytesPerStream = 64 * 1024;

    private readonly object _gate = new();
    private readonly IWorkerContainedProcess _process;
    private readonly Task _processExit;
    private readonly Task<WorkerDiagnosticSummary> _standardOutput;
    private readonly Task<WorkerDiagnosticSummary> _standardError;
    private readonly TaskCompletionSource _fatal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _containment;
    private Task _monitor = Task.CompletedTask;
    private bool _monitorStarted;
    private int _shutdownOrDispose;

    private WorkerProcessClient(
        IWorkerContainedProcess process,
        Func<WorkerEnvelope, CancellationToken, ValueTask>? onEvent)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        if (process.ProcessId <= 0)
            throw new ArgumentException("Contained worker process ID must be positive.", nameof(process));

        _processExit = process.WaitForExitAsync(CancellationToken.None);
        _standardOutput = DrainDiagnosticAsync(process.StandardOutputReader);
        _standardError = DrainDiagnosticAsync(process.StandardErrorReader);
        Client = new WorkerClient(
            process.RequestWriter,
            process.EventReader,
            expectedWorkerBootId: null,
            onEvent);
    }

    internal WorkerClient Client { get; }
    internal int ProcessId => _process.ProcessId;
    internal Guid WorkerBootId => Client.WorkerBootId;
    internal long Generation => Client.Generation;
    internal Task Fatal => _fatal.Task;

    internal Task<WorkerDiagnosticReport> Diagnostics => ReadDiagnosticsAsync();

    internal static async Task<WorkerProcessClient> LaunchAsync(
        IWorkerProcessLauncher launcher,
        WorkerLaunchCommand command,
        long generation,
        DateTimeOffset deadlineUtc,
        Func<WorkerEnvelope, CancellationToken, ValueTask>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(command);
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        cancellationToken.ThrowIfCancellationRequested();
        if (deadlineUtc <= DateTimeOffset.UtcNow)
            throw new TimeoutException("Worker launch deadline has expired.");

        var process = launcher.Launch(command, cancellationToken) ??
            throw new WorkerProcessException("worker_launch_returned_null");
        WorkerProcessClient? authority = null;
        try
        {
            authority = new WorkerProcessClient(process, onEvent);
            process = null!;
            await authority.InitializeAsync(
                generation,
                deadlineUtc,
                cancellationToken).ConfigureAwait(false);
            authority.StartMonitoring();
            var result = authority;
            authority = null;
            return result;
        }
        catch
        {
            if (authority is not null)
            {
                try
                {
                    await authority.EnsureContainmentAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                }
            }
            else
            {
                try
                {
                    process?.Dispose();
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                }
            }
            throw;
        }
    }

    internal async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _shutdownOrDispose, 1, 0) != 0)
            throw new InvalidOperationException("Worker process shutdown is one-shot.");

        try
        {
            await Client.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            await _processExit.WaitAsync(cancellationToken).ConfigureAwait(false);
            await EnsureContainmentAsync().ConfigureAwait(false);
            await _monitor.ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await EnsureContainmentAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
            throw;
        }
    }

    private async Task InitializeAsync(
        long generation,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var remaining = deadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("Worker initialization deadline has expired.");
        deadline.CancelAfter(remaining);

        var initialization = Client.InitializeAsync(
            generation,
            deadlineUtc,
            deadline.Token);
        var winner = await Task.WhenAny(initialization, _processExit)
            .ConfigureAwait(false);
        if (winner == _processExit)
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            try
            {
                await initialization.ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
            throw new WorkerProcessException("worker_exited_before_ready");
        }

        try
        {
            await initialization.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested &&
            DateTimeOffset.UtcNow >= deadlineUtc)
        {
            throw new WorkerProcessException(
                "worker_initialize_timed_out",
                exception);
        }
    }

    private async Task MonitorAsync()
    {
        var winner = await Task.WhenAny(_processExit, Client.Fatal)
            .ConfigureAwait(false);
        if (Volatile.Read(ref _shutdownOrDispose) != 0)
            return;

        Exception failure;
        if (winner == Client.Fatal)
        {
            try
            {
                await Client.Fatal.ConfigureAwait(false);
                failure = new WorkerProcessException("worker_protocol_stopped");
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                failure = new WorkerProcessException(
                    "worker_protocol_failed",
                    exception);
            }
        }
        else
        {
            try
            {
                await _processExit.ConfigureAwait(false);
                failure = new WorkerProcessException("worker_exited");
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                failure = new WorkerProcessException(
                    "worker_exit_observation_failed",
                    exception);
            }
        }

        _fatal.TrySetException(failure);
        try
        {
            await EnsureContainmentAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private void StartMonitoring()
    {
        lock (_gate)
        {
            if (_monitorStarted)
                throw new InvalidOperationException("Worker process monitor is already running.");
            _monitorStarted = true;
            _monitor = MonitorAsync();
        }
    }

    private Task EnsureContainmentAsync()
    {
        lock (_gate)
            return _containment ??= ContainCoreAsync();
    }

    private async Task ContainCoreAsync()
    {
        List<Exception>? failures = null;
        try
        {
            await Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            _process.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await _processExit.ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            _ = await ReadDiagnosticsAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            (failures ??= []).Add(exception);
        }

        if (failures is { Count: 1 })
            throw failures[0];
        if (failures is { Count: > 1 })
            throw new AggregateException("Worker containment cleanup failed.", failures);
    }

    private async Task<WorkerDiagnosticReport> ReadDiagnosticsAsync() =>
        new(
            await _standardOutput.ConfigureAwait(false),
            await _standardError.ConfigureAwait(false));

    private static async Task<WorkerDiagnosticSummary> DrainDiagnosticAsync(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytes = 0;
        var digestedBytes = 0;
        try
        {
            for (;;)
            {
                var received = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (received == 0)
                    break;

                totalBytes = totalBytes > long.MaxValue - received
                    ? long.MaxValue
                    : totalBytes + received;
                var remaining = MaximumDiagnosticBytesPerStream - digestedBytes;
                var admitted = Math.Min(remaining, received);
                if (admitted > 0)
                {
                    digest.AppendData(buffer, 0, admitted);
                    digestedBytes += admitted;
                }
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, received));
            }

            var hash = digest.GetHashAndReset();
            try
            {
                return new WorkerDiagnosticSummary(
                    totalBytes,
                    digestedBytes,
                    totalBytes > digestedBytes,
                    Convert.ToHexStringLower(hash));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _shutdownOrDispose, 1);
        await EnsureContainmentAsync().ConfigureAwait(false);
        await _monitor.ConfigureAwait(false);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}
