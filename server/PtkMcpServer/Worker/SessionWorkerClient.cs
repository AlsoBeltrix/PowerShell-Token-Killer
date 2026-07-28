using System.Collections;
using System.Runtime.InteropServices;

namespace PtkMcpServer.Worker;

internal sealed record SessionWorkerInvocation(
    WorkerResult Result,
    Guid? ArtifactId,
    OutputArtifactContent? ArtifactContent);

internal enum WorkerInvocationDisposition
{
    NotStarted,
    OutcomeUnknown,
}

internal sealed class WorkerInvocationException : IOException
{
    internal WorkerInvocationException(
        WorkerInvocationDisposition disposition,
        string causeDetailCode,
        Exception innerException)
        : base(
            disposition == WorkerInvocationDisposition.NotStarted
                ? "Worker invocation was not started."
                : "Worker invocation outcome is unknown.",
            innerException)
    {
        Disposition = disposition;
        CauseDetailCode = causeDetailCode;
    }

    internal WorkerInvocationDisposition Disposition { get; }
    internal string CauseDetailCode { get; }
}

internal interface ISessionWorker : IAsyncDisposable
{
    int ProcessId { get; }
    Guid SessionId { get; }
    long Incarnation { get; }
    bool IsTransportUsable { get; }
    Task Fatal { get; }
    Task ContainmentEmpty { get; }

    Task<SessionWorkerInvocation> InvokeAsync(
        string script,
        bool raw,
        WorkerInvokeRoute route,
        int timeoutSeconds,
        WorkerArtifactRequest? artifact,
        CancellationToken cancellationToken);

    Task<WorkerStateSnapshot> StateAsync(
        bool listAvailable,
        CancellationToken cancellationToken);

    Task<WorkerContainmentResult> StopAsync(
        WorkerContainmentReason reason,
        CancellationToken cancellationToken);
}

internal interface ISessionWorkerFactory
{
    Task<ISessionWorker> StartAsync(
        Guid sessionId,
        long incarnation,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken);
}

internal sealed class SessionWorkerStartException : Exception
{
    internal SessionWorkerStartException(
        string detailCode,
        bool processLaunched,
        WorkerContainmentResult? containment,
        Task? containmentEmpty,
        Exception? innerException = null)
        : base($"Session worker startup failed ({detailCode}).", innerException)
    {
        DetailCode = detailCode;
        ProcessLaunched = processLaunched;
        Containment = containment;
        ContainmentEmpty = containmentEmpty;
    }

    internal string DetailCode { get; }
    internal bool ProcessLaunched { get; }
    internal WorkerContainmentResult? Containment { get; }
    internal Task? ContainmentEmpty { get; }
}

/// <summary>
/// One slot-local factory. Its launcher survives worker replacement so a new
/// incarnation cannot overlap an unconfirmed old containment domain.
/// </summary>
internal sealed class ProcessSessionWorkerFactory : ISessionWorkerFactory
{
    private readonly IWorkerProcessLauncher _launcher;
    private readonly WorkerLaunchCommand _command;
    private readonly WorkerProtocolLimits _limits;

    internal ProcessSessionWorkerFactory(
        IWorkerProcessLauncher launcher,
        WorkerLaunchCommand command,
        WorkerProtocolLimits limits)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    internal static ProcessSessionWorkerFactory CreateDefault(
        WorkerProtocolLimits limits)
    {
        var command = SessionWorkerLaunchCommand.Create();
        var brokerPath = OperatingSystem.IsWindows()
            ? null
            : Path.Combine(
                SessionWorkerLaunchCommand.ApplicationDirectory(),
                SessionWorkerLaunchCommand.UnixBrokerFileName);
        return new ProcessSessionWorkerFactory(
            WorkerProcessLauncher.Create(brokerPath),
            command,
            limits);
    }

    public async Task<ISessionWorker> StartAsync(
        Guid sessionId,
        long incarnation,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        IWorkerContainedProcess? process = null;
        ProcessSessionWorker? client = null;
        try
        {
            process = await _launcher.LaunchAsync(_command, cancellationToken)
                .ConfigureAwait(false);
            client = new ProcessSessionWorker(
                process,
                sessionId,
                incarnation,
                _limits);
            await client.InitializeAsync(deadlineUtc, cancellationToken)
                .ConfigureAwait(false);
            process = null;
            var ready = client;
            client = null;
            return ready;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            if (client is not null)
            {
                WorkerContainmentResult clientContainment;
                try
                {
                    clientContainment = await client
                        .StopAsync(
                            WorkerContainmentReason.LaunchFailure,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception containmentFailure)
                    when (!IsFatal(containmentFailure))
                {
                    clientContainment = WorkerContainmentResult.Unknown(
                        "worker_launch_containment_unconfirmed");
                    exception = new AggregateException(
                        exception,
                        containmentFailure);
                }
                var clientContainmentEmpty = client.ContainmentEmpty;
                await client.DisposeAsync().ConfigureAwait(false);
                client = null;
                process = null;
                throw new SessionWorkerStartException(
                    InitializationFailureCode(exception, deadlineUtc),
                    processLaunched: true,
                    clientContainment,
                    clientContainmentEmpty,
                    exception);
            }

            if (process is null)
            {
                var launchContainmentEmpty = ContainmentTask(exception);
                throw new SessionWorkerStartException(
                    LaunchFailureCode(exception, deadlineUtc),
                    processLaunched: launchContainmentEmpty is not null,
                    containment: launchContainmentEmpty is null
                        ? null
                        : WorkerContainmentResult.Unknown(
                            "worker_launch_containment_unconfirmed"),
                    launchContainmentEmpty,
                    exception);
            }

            WorkerContainmentResult containment;
            try
            {
                containment = await process
                    .ContainAsync(WorkerContainmentReason.LaunchFailure)
                    .ConfigureAwait(false);
            }
            catch (Exception containmentFailure) when (!IsFatal(containmentFailure))
            {
                containment = WorkerContainmentResult.Unknown(
                    "worker_launch_containment_unconfirmed");
                exception = new AggregateException(exception, containmentFailure);
            }
            var containmentEmpty = process.ContainmentEmpty;
            process.Dispose();
            process = null;
            throw new SessionWorkerStartException(
                "worker_initialize_failed",
                processLaunched: true,
                containment,
                containmentEmpty,
                exception);
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            process?.Dispose();
        }
    }

    private static string LaunchFailureCode(
        Exception exception,
        DateTimeOffset deadlineUtc) => exception switch
        {
            WorkerLaunchException launch => launch.DetailCode,
            WorkerProcessException process => process.DetailCode,
            TimeoutException => "worker_start_timed_out",
            OperationCanceledException
                when DateTimeOffset.UtcNow >= deadlineUtc =>
                    "worker_start_timed_out",
            OperationCanceledException => "worker_start_canceled",
            _ => "worker_launch_failed",
        };

    private static string InitializationFailureCode(
        Exception exception,
        DateTimeOffset deadlineUtc) =>
        exception is OperationCanceledException or TimeoutException
            ? LaunchFailureCode(exception, deadlineUtc)
            : "worker_initialize_failed";

    private static Task? ContainmentTask(Exception exception) => exception switch
    {
        WorkerLaunchException launch => launch.ContainmentEmpty,
        WorkerProcessException process => process.ContainmentEmpty,
        _ => null,
    };

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}

/// <summary>
/// Small supervisor-side client for one worker incarnation. The session
/// registry already serializes foreground work, so this client deliberately
/// owns one request/response exchange at a time instead of a multiplexing
/// request table.
/// </summary>
internal sealed class ProcessSessionWorker : ISessionWorker
{
    private static readonly TimeSpan CancelWriteGrace =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly IWorkerContainedProcess _process;
    private readonly WorkerProtocolLimits _limits;
    private readonly WorkerProtocolReader _reader;
    private readonly WorkerProtocolWriter _writer;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly TaskCompletionSource _fatal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _standardOutputDrain;
    private readonly Task _standardErrorDrain;
    private long _requestId;
    private bool _initialized;
    private bool _stopping;
    private bool _stopped;
    private int _disposed;

    internal ProcessSessionWorker(
        IWorkerContainedProcess process,
        Guid sessionId,
        long incarnation,
        WorkerProtocolLimits limits)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
        if (incarnation <= 0)
            throw new ArgumentOutOfRangeException(nameof(incarnation));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        SessionId = sessionId;
        Incarnation = incarnation;
        _reader = new WorkerProtocolReader(process.EventReader);
        _writer = new WorkerProtocolWriter(process.RequestWriter);
        _standardOutputDrain = DrainAsync(process.StandardOutputReader);
        _standardErrorDrain = DrainAsync(process.StandardErrorReader);
        _ = IgnoreFailureAsync(_fatal.Task);
        _ = ObserveExitAsync();
    }

    public int ProcessId => _process.ProcessId;
    public Guid SessionId { get; }
    public long Incarnation { get; }
    public bool IsTransportUsable
    {
        get
        {
            lock (_gate)
            {
                return !_stopping &&
                    !_stopped &&
                    !_fatal.Task.IsCompleted;
            }
        }
    }
    public Task Fatal => _fatal.Task;
    public Task ContainmentEmpty => _process.ContainmentEmpty;

    internal async Task InitializeAsync(
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_initialized || _requestId != 0)
                throw new InvalidOperationException("Worker initialization runs once.");
            _requestId = 1;
        }

        await WriteRequiredAsync(
            WorkerOperationProtocol.CreateInitializeEnvelope(
                SessionId,
                Incarnation,
                requestId: 1,
                deadlineUtc,
                _limits),
            cancellationToken).ConfigureAwait(false);

        var ready = await ReadBeforeDeadlineAsync(
            deadlineUtc,
            cancellationToken).ConfigureAwait(false);
        var accepted = WorkerOperationProtocol.ParseReady(
            ready,
            SessionId,
            Incarnation,
            expectedRequestId: 1);
        if (accepted != _limits)
        {
            throw new WorkerProtocolException(
                "protocol_limits_mismatch",
                "Worker readiness changed the initialized limits.");
        }
        lock (_gate) _initialized = true;
    }

    public async Task<SessionWorkerInvocation> InvokeAsync(
        string script,
        bool raw,
        WorkerInvokeRoute route,
        int timeoutSeconds,
        WorkerArtifactRequest? artifact,
        CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        var writeStarted = false;
        WorkerArtifactReceiver? receiver = null;
        using var artifactBytes = new MemoryStream();
        try
        {
            var requestId = NextRequestId();
            var request = WorkerOperationProtocol.CreateInvokeEnvelope(
                SessionId,
                Incarnation,
                requestId,
                script,
                raw,
                route,
                timeoutSeconds,
                artifact,
                _limits);
            if (artifact is not null)
                receiver = new WorkerArtifactReceiver(requestId, artifact);

            await WriteRequiredAsync(
                    request,
                    cancellationToken,
                    () => writeStarted = true)
                .ConfigureAwait(false);
            var artifactStarted = false;
            while (true)
            {
                var envelope = await ReadRequiredAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (envelope.Kind == WorkerMessageKind.ArtifactChunk)
                {
                    var chunk = WorkerOperationProtocol.ParseArtifactChunk(
                        envelope,
                        SessionId,
                        Incarnation,
                        _limits);
                    RequireRequest(requestId, chunk.RequestId);
                    receiver?.Accept(chunk);
                    if (receiver is null)
                        throw UnsolicitedArtifact();
                    artifactBytes.Write(chunk.Bytes);
                    artifactStarted = true;
                    continue;
                }
                if (envelope.Kind == WorkerMessageKind.ArtifactSeal)
                {
                    var seal = WorkerOperationProtocol.ParseArtifactSeal(
                        envelope,
                        SessionId,
                        Incarnation);
                    RequireRequest(requestId, seal.RequestId);
                    receiver?.Accept(seal);
                    if (receiver is null)
                        throw UnsolicitedArtifact();
                    artifactStarted = true;
                    continue;
                }

                var result = WorkerOperationProtocol.ParseResult(
                    envelope,
                    SessionId,
                    Incarnation);
                RequireRequest(requestId, result.RequestId);
                if (artifactStarted && receiver?.IsSealed != true)
                {
                    throw new WorkerProtocolException(
                        "artifact_seal_missing",
                        "Worker terminal arrived before its artifact seal.");
                }
                OutputArtifactContent? artifactContent = null;
                if (receiver?.IsSealed == true)
                {
                    artifactContent = WorkerOutputArtifactCodec.Decode(
                        artifactBytes.ToArray(),
                        artifact!.MaximumBytes);
                }
                return new SessionWorkerInvocation(
                    result,
                    artifactContent is null ? null : artifact?.ArtifactId,
                    artifactContent);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                if (writeStarted)
                {
                    Poison(exception);
                    await CancelBestEffortAsync().ConfigureAwait(false);
                }
                throw;
            }

            if (writeStarted)
                Poison(exception);
            throw new WorkerInvocationException(
                writeStarted
                    ? WorkerInvocationDisposition.OutcomeUnknown
                    : WorkerInvocationDisposition.NotStarted,
                InvocationFailureCode(exception, writeStarted),
                exception);
        }
        finally
        {
            receiver?.Dispose();
            _operation.Release();
        }
    }

    public async Task<WorkerStateSnapshot> StateAsync(
        bool listAvailable,
        CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        var writeAttempted = false;
        try
        {
            var requestId = NextRequestId();
            var request = WorkerOperationProtocol.CreateStateQueryEnvelope(
                SessionId,
                Incarnation,
                requestId,
                listAvailable);
            writeAttempted = true;
            await WriteRequiredAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var snapshot = WorkerOperationProtocol.ParseStateSnapshot(
                await ReadRequiredAsync(cancellationToken).ConfigureAwait(false),
                SessionId,
                Incarnation);
            RequireRequest(requestId, snapshot.RequestId);
            return snapshot;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            if (writeAttempted)
                Poison(exception);
            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                await CancelBestEffortAsync().ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task<WorkerContainmentResult> StopAsync(
        WorkerContainmentReason reason,
        CancellationToken cancellationToken)
    {
        var shouldShutdown = false;
        lock (_gate)
        {
            if (!_stopping && !_stopped && _initialized)
            {
                _stopping = true;
                shouldShutdown = true;
            }
        }
        if (shouldShutdown)
            _fatal.TrySetResult();

        if (shouldShutdown)
        {
            try
            {
                await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var requestId = NextRequestId(allowStopping: true);
                    await WriteRequiredAsync(
                        WorkerOperationProtocol.CreateEmptyEnvelope(
                            WorkerMessageKind.Shutdown,
                            SessionId,
                            Incarnation,
                            requestId),
                        cancellationToken).ConfigureAwait(false);
                    var stopped = await ReadRequiredAsync(cancellationToken)
                        .ConfigureAwait(false);
                    WorkerOperationProtocol.ParseEmpty(
                        stopped,
                        WorkerMessageKind.Stopped,
                        SessionId,
                        Incarnation);
                    RequireRequest(requestId, stopped.RequestId!.Value);
                    lock (_gate) _stopped = true;
                }
                finally
                {
                    _operation.Release();
                }
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
        }

        var containment = await _process.ContainAsync(reason)
            .ConfigureAwait(false);
        try
        {
            await _process.WaitForExitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            if (containment.Outcome == WorkerContainmentOutcome.ConfirmedEmpty)
            {
                containment = WorkerContainmentResult.Unknown(
                    "worker_exit_unconfirmed");
            }
        }
        return containment;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            using var cancellation = new CancellationTokenSource(DisposeGrace);
            _ = await StopAsync(
                WorkerContainmentReason.SupervisorShutdown,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
        _fatal.TrySetResult();
        try
        {
            _process.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception)) { }
        try
        {
            await Task.WhenAll(
                    _standardOutputDrain,
                    _standardErrorDrain)
                .WaitAsync(DisposeGrace)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception)) { }
    }

    private long NextRequestId(bool allowStopping = false)
    {
        lock (_gate)
        {
            if (!_initialized)
                throw new InvalidOperationException("Worker is not initialized.");
            if (_stopped || _stopping && !allowStopping)
                throw new InvalidOperationException("Worker is stopping.");
            if (_fatal.Task.IsCompleted)
                throw new IOException("Worker transport is unavailable.");
            if (_requestId == long.MaxValue)
            {
                throw new WorkerProtocolException(
                    "worker_request_id_exhausted",
                    "Worker client request ID space is exhausted.");
            }
            return ++_requestId;
        }
    }

    private async Task<WorkerEnvelope> ReadBeforeDeadlineAsync(
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        var remaining = deadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("Worker initialization deadline expired.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(remaining);
        return await ReadRequiredAsync(deadline.Token).ConfigureAwait(false);
    }

    private async Task<WorkerEnvelope> ReadRequiredAsync(
        CancellationToken cancellationToken)
    {
        var read = _reader.ReadAsync(cancellationToken).AsTask();
        _ = IgnoreFailureAsync(read);
        return await read
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false) ??
            throw new EndOfStreamException(
                "Worker event stream ended unexpectedly.");
    }

    private async Task WriteRequiredAsync(
        WorkerEnvelope envelope,
        CancellationToken cancellationToken,
        Action? onWriteAttempt = null)
    {
        var write = _writer
            .WriteAsync(envelope, cancellationToken, onWriteAttempt)
            .AsTask();
        _ = IgnoreFailureAsync(write);
        await write
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string InvocationFailureCode(
        Exception exception,
        bool writeStarted) =>
        exception switch
        {
            WorkerProtocolException protocol => protocol.DetailCode,
            WorkerProcessException process => process.DetailCode,
            EndOfStreamException => "worker_transport_closed",
            ObjectDisposedException => "worker_transport_closed",
            IOException => writeStarted
                ? "worker_transport_failure"
                : "worker_transport_unavailable",
            InvalidOperationException => "worker_transport_unavailable",
            _ => "worker_invoke_failed",
        };

    private async Task CancelBestEffortAsync()
    {
        long requestId;
        lock (_gate) requestId = _requestId;
        try
        {
            var write = _writer.WriteAsync(
                    WorkerOperationProtocol.CreateCancelEnvelope(
                        SessionId,
                        Incarnation,
                        requestId),
                    CancellationToken.None)
                .AsTask();
            _ = IgnoreFailureAsync(write);
            await write
                .WaitAsync(CancelWriteGrace)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (_stopped || Volatile.Read(ref _disposed) != 0)
                    return;
            }
            Poison(new EndOfStreamException("Worker process exited unexpectedly."));
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            Poison(exception);
        }
    }

    private void Poison(Exception exception) =>
        _fatal.TrySetException(
            exception is IOException
                ? exception
                : new IOException("Worker transport is unusable.", exception));

    private static void RequireRequest(long expected, long actual)
    {
        if (expected != actual)
        {
            throw new WorkerProtocolException(
                "request_id_mismatch",
                "Worker response targets another request.");
        }
    }

    private static WorkerProtocolException UnsolicitedArtifact() =>
        new(
            "unsolicited_artifact",
            "Worker emitted an artifact that was not requested.");

    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[16 * 1024];
        while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0)
        {
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}

internal static class SessionWorkerLaunchCommand
{
    internal const string UnixBrokerFileName = "PtkWorkerBroker";

    internal static WorkerLaunchCommand Create()
    {
        var serverDirectory = ApplicationDirectory();
        var serverAssembly = typeof(WorkerProcessEntry).Assembly.Location;
        var appHost = Path.Combine(
            serverDirectory,
            OperatingSystem.IsWindows()
                ? "PtkMcpServer.exe"
                : "PtkMcpServer");
        return File.Exists(appHost)
            ? new WorkerLaunchCommand(
                appHost,
                ["--worker"],
                Environment.CurrentDirectory,
                CaptureEnvironment())
            : new WorkerLaunchCommand(
                ResolveDotnetHost(),
                ["exec", serverAssembly, "--worker"],
                Environment.CurrentDirectory,
                CaptureEnvironment());
    }

    internal static string ApplicationDirectory()
    {
        var serverAssembly = typeof(WorkerProcessEntry).Assembly.Location;
        return Path.GetDirectoryName(serverAssembly) ??
            throw new InvalidOperationException(
                "The server assembly directory is unavailable.");
    }

    private static string ResolveDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) &&
            Path.IsPathFullyQualified(configured) &&
            File.Exists(configured))
        {
            return configured;
        }

        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var root = runtime.Parent?.Parent?.Parent ??
            throw new InvalidOperationException(
                "The dotnet host directory is unavailable.");
        var path = Path.Combine(
            root.FullName,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "The dotnet host is unavailable.",
                path);
    }

    private static IEnumerable<KeyValuePair<string, string>> CaptureEnvironment()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key ||
                entry.Value is not string value ||
                key.Contains('=') ||
                WorkerBootstrapEnvironment.ReservedHandleVariables.Contains(key))
            {
                continue;
            }
            yield return new KeyValuePair<string, string>(key, value);
        }
    }
}
