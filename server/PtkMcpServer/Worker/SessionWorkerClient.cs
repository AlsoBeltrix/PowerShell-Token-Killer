using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PtkMcpServer.Worker;

internal sealed record SessionWorkerInvocation(
    WorkerResult Result,
    Guid? ArtifactId,
    OutputArtifactContent? ArtifactContent,
    OutputRecoverySummary? OutputRecovery = null,
    Task<OutputRecoverySummary>? OutputRecoveryCompletion = null);

internal enum WorkerInvocationDisposition
{
    NotStarted,
    OutcomeUnknown,
}

/// <summary>
/// A worker process exited when nobody asked it to, carrying whatever it said
/// on the way out. Before this existed the same event surfaced as a bare
/// end-of-stream, indistinguishable from a pipe failure with a live worker —
/// so a worker defect, a transport fault, and a caller's own command killing
/// the process all read identically (GitHub #13).
/// </summary>
internal sealed class WorkerExitException(
    string? diagnostic,
    int? exitCode)
    : EndOfStreamException(Describe(diagnostic, exitCode))
{
    /// <summary>
    /// The worker's final standard-error line, or <see langword="null"/> when
    /// it wrote nothing recognizable.
    /// </summary>
    internal string? Diagnostic { get; } = diagnostic;

    /// <summary>
    /// The process exit code, or <see langword="null"/> when the platform
    /// could not answer.
    /// </summary>
    internal int? ExitCode { get; } = exitCode;

    /// <summary>
    /// Whether PTK observed enough to say anything beyond "the worker died".
    /// </summary>
    /// <remarks>
    /// There is deliberately no <c>Kind</c> here. Nothing this class carries is
    /// forgery-proof: the worker runs the caller's script in-process, so the
    /// caller both shares the worker's standard error (can write a convincing
    /// <c>ptk_worker_exit kind=...</c> line) and chooses the exit code
    /// (<c>[Environment]::Exit(84)</c>). Deriving a cause from either would let
    /// a caller put words in PTK's mouth. The first fix for i13-1 only moved
    /// the forgery from the text to the code; the reviewer caught that.
    ///
    /// What PTK genuinely observed is that the process exited when nobody asked
    /// it to. That is the only claim made. The exit code and the retained line
    /// are still reported — they are usually the real cause and are exactly the
    /// evidence GitHub #13 needs — but as labelled untrusted evidence, never as
    /// PTK's own classification.
    /// </remarks>
    internal bool HasEvidence => ExitCode is not null || Diagnostic is not null;

    private static string Describe(string? diagnostic, int? exitCode)
    {
        var sb = new StringBuilder("Worker process exited unexpectedly");
        if (exitCode is { } code)
        {
            sb.Append(" (exit code ")
                .Append(code.ToString(CultureInfo.InvariantCulture))
                .Append(')');
        }
        sb.Append('.');
        if (diagnostic is not null)
            sb.Append(' ').Append(diagnostic);
        return sb.ToString();
    }
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

    /// <summary>
    /// What the worker said on its way out, when this failure was a worker
    /// death rather than a transport fault. Null on every other cause.
    /// </summary>
    internal WorkerExitException? WorkerExit =>
        InnerException as WorkerExitException;
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
        IWorkerArtifactCapture? artifactCapture,
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
    private static readonly TimeSpan DiagnosticDrainGrace =
        TimeSpan.FromSeconds(2);

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
    // Standard error is retained rather than discarded: the worker's own exit
    // diagnostic is the only evidence of why it died, and dropping it left
    // every death reading as an indistinguishable transport failure
    // (GitHub #13). Standard output keeps discarding — the worker is not
    // supposed to write there, so retaining it would only buy an unbounded
    // channel for executed-command output.
    private readonly WorkerDiagnosticTail _standardErrorTail = new();
    private readonly Task _exitObserved;
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
        _standardErrorDrain =
            _standardErrorTail.DrainAsync(process.StandardErrorReader);
        _ = IgnoreFailureAsync(_fatal.Task);
        _exitObserved = ObserveExitAsync();
    }

    public int ProcessId => _process.ProcessId;

    /// <summary>
    /// The worker's last bounded standard-error line, or <see langword="null"/>
    /// when it wrote nothing recognizable. Reads whatever has arrived so far;
    /// callers on the death path await <see cref="_standardErrorDrain"/> first
    /// so the dying worker's final write is included.
    /// </summary>
    internal string? LastStandardError => _standardErrorTail.Text;

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
        IWorkerArtifactCapture? artifactCapture,
        CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        var writeStarted = false;
        try
        {
            var requestId = NextRequestId();
            artifactCapture?.BindRequest(requestId);
            var request = WorkerOperationProtocol.CreateInvokeEnvelope(
                SessionId,
                Incarnation,
                requestId,
                script,
                raw,
                route,
                timeoutSeconds,
                artifactCapture?.Request,
                _limits);

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
                    if (artifactCapture is null)
                        throw UnsolicitedArtifact();
                    artifactCapture.Accept(chunk);
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
                    if (artifactCapture is null)
                        throw UnsolicitedArtifact();
                    artifactCapture.Accept(seal);
                    artifactStarted = true;
                    continue;
                }

                var result = WorkerOperationProtocol.ParseResult(
                    envelope,
                    SessionId,
                    Incarnation);
                RequireRequest(requestId, result.RequestId);
                if (artifactStarted && artifactCapture?.IsSealed != true)
                {
                    throw new WorkerProtocolException(
                        "artifact_seal_missing",
                        "Worker terminal arrived before its artifact seal.");
                }
                var outputRecoveryCompletion =
                    artifactCapture?.CompleteAtResultAsync();
                return new SessionWorkerInvocation(
                    result,
                    ArtifactId: null,
                    ArtifactContent: null,
                    OutputRecoveryCompletion:
                        outputRecoveryCompletion is null
                            ? null
                            : ObserveArtifactCompletionAsync(
                                outputRecoveryCompletion));
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

            // A dying worker closes its event pipe before the exit observer
            // can name the cause, so the in-flight read raises a bare
            // end-of-stream first. Resolve the real cause BEFORE poisoning:
            // _fatal is TrySetException, so whoever poisons first decides what
            // every later reader sees, and poisoning with the bare
            // end-of-stream here would discard the worker's own account of its
            // death — the exact evidence #13 needs.
            var cause = await WithWorkerExitAsync(exception).ConfigureAwait(false);
            if (writeStarted)
                Poison(cause);
            throw new WorkerInvocationException(
                writeStarted
                    ? WorkerInvocationDisposition.OutcomeUnknown
                    : WorkerInvocationDisposition.NotStarted,
                InvocationFailureCode(cause, writeStarted),
                cause);
        }
        finally
        {
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
            // Set from the writer's own callback, invoked immediately before
            // the underlying stream write — not eagerly before the call. The
            // writer has two cancellation points before it offers any byte to
            // the pipe (its write-gate wait, and an explicit token check
            // before encoding). An eager flag treated a cancel at either point
            // as an ambiguous transport, so a canceled read-only ptk_state
            // poisoned the worker and the supervisor replaced a perfectly
            // healthy session, losing its variables, modules, and
            // connections. This is the boundary the invoke path already uses.
            await WriteRequiredAsync(
                    request,
                    cancellationToken,
                    () => writeAttempted = true)
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
            // Proved pre-write: the worker never saw this request, so the
            // transport is still sound and there is nothing to cancel. At or
            // after the first write attempt the stream and the request outcome
            // are both ambiguous, and fail-closed poisoning still applies.
            if (writeAttempted)
            {
                Poison(exception);
                if (exception is OperationCanceledException &&
                    cancellationToken.IsCancellationRequested)
                {
                    await CancelBestEffortAsync().ConfigureAwait(false);
                }
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
                // Cancellation, a missing acknowledgement, or an invalid one:
                // fall through to containment, which is the final descendant
                // sweep either way.
            }
            finally
            {
                // Completed only after the handshake attempt. Completing it
                // before, as this used to, made NextRequestId(allowStopping)
                // throw on the very next line — that check also rejects a
                // completed _fatal — so the shutdown frame was never written,
                // `stopped` was never read, and every close, reset, replace,
                // and dispose skipped worker-side session teardown and went
                // straight to forced containment.
                _fatal.TrySetResult();
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
        try
        {
            return await ReadRequiredAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Worker initialization deadline expired.",
                exception);
        }
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
            // One code for every unrequested death, because that is the only
            // thing PTK observed rather than was told. The cause travels as
            // labelled untrusted evidence beside it — a caller controls both
            // the worker's exit code and its standard error, so any finer
            // classification here would be PTK repeating the caller's claim as
            // its own (i13-1, reopened).
            WorkerExitException { HasEvidence: true } =>
                "worker_exited_unexpectedly",
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

    private async Task<OutputRecoverySummary> ObserveArtifactCompletionAsync(
        Task<OutputRecoverySummary> completion)
    {
        try
        {
            return await completion.ConfigureAwait(false);
        }
        catch (WorkerProtocolException exception)
        {
            Poison(exception);
            throw new WorkerInvocationException(
                WorkerInvocationDisposition.OutcomeUnknown,
                exception.DetailCode,
                exception);
        }
    }

    /// <summary>
    /// Upgrades a bare transport failure to the worker's own exit account when
    /// the process is in fact dying. Returns <paramref name="exception"/>
    /// unchanged when the worker is alive — a genuine pipe fault on a live
    /// worker must keep reading exactly as it did before.
    /// </summary>
    private async Task<Exception> WithWorkerExitAsync(Exception exception)
    {
        // A protocol or process failure already names something specific
        // ("truncated_frame"); a recorded death cannot improve on it, and
        // substituting one would trade a precise cause for a vaguer one.
        if (exception is WorkerProtocolException or WorkerProcessException)
            return exception;

        // Otherwise a recorded death outranks whatever this caller tripped
        // over — but only when it actually carries facts. Gating the lookup on
        // the incoming exception's type meant the call arriving just AFTER a
        // death got the pre-#13 answer: NextRequestId raises a fresh
        // IOException once _fatal is faulted, and that type never reached the
        // lookup, so facts already in hand were discarded (finding i13-3).
        if (InformativeWorkerExit() is { } recorded)
            return recorded;

        if (exception is not EndOfStreamException and not ObjectDisposedException)
            return exception;
        // Nothing recorded yet and the transport just ended: the worker may be
        // dying right now, with the observer moments behind. Wait briefly for
        // its account rather than reporting a bare transport close.
        await IgnoreFailureAsync(_exitObserved.WaitAsync(DiagnosticDrainGrace))
            .ConfigureAwait(false);
        return InformativeWorkerExit() ?? exception;
    }

    /// <summary>
    /// The recorded worker death, but only when it says something the caller's
    /// own exception does not. A death carrying no kind, no exit code, and no
    /// diagnostic adds nothing, and preferring it would overwrite a specific
    /// detail code — "worker_transport_unavailable" for a call that never
    /// reached the pipe — with the blanket "worker_transport_closed".
    /// </summary>
    private WorkerExitException? InformativeWorkerExit() =>
        RecordedWorkerExit() is { HasEvidence: true } exit ? exit : null;

    private WorkerExitException? RecordedWorkerExit() =>
        _fatal.Task.IsFaulted
            ? _fatal.Task.Exception?.InnerExceptions
                .OfType<WorkerExitException>()
                .FirstOrDefault()
            : null;

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
            // Let the retainer finish before reading it: the diagnostic is the
            // worker's last write, so the bytes can still be in flight when the
            // wait completes. Bounded so a stuck stream cannot hold the
            // poisoning of a session that is already gone.
            await IgnoreFailureAsync(
                _standardErrorDrain.WaitAsync(DiagnosticDrainGrace))
                .ConfigureAwait(false);
            lock (_gate)
            {
                // `_stopping` as well as `_stopped`: a worker that acknowledges
                // shutdown may exit before StopAsync assigns `_stopped`, and
                // that exit is expected, not a fault. Checking only `_stopped`
                // let the race poison a session we asked to leave — masked
                // while the handshake was unreachable, live now that it runs.
                if (_stopping || _stopped || Volatile.Read(ref _disposed) != 0)
                    return;
            }
            Poison(new WorkerExitException(
                _standardErrorTail.Text,
                _process.ExitCode));
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            Poison(exception);
        }
    }

    private void Poison(Exception exception) =>
        // WorkerExitException is an EndOfStreamException, hence an IOException,
        // so it survives unwrapped and reaches the classifier with the worker's
        // diagnostic and exit code intact.
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
