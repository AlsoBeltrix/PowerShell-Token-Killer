using PtkMcpServer;

namespace PtkMcpServer.Worker;

internal readonly record struct UnixProcessIdentity(ulong High, ulong Low)
{
    internal bool IsValid => High != 0 || Low != 0;
}

internal sealed record UnixWorkerContainmentIdentity(
    int BrokerProcessId,
    UnixProcessIdentity BrokerIdentity,
    int WorkerProcessId,
    UnixProcessIdentity WorkerIdentity,
    int WorkerProcessGroup);

internal interface IUnixWorkerNative
{
    int Spawn(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<(int Source, int Target)> descriptorMappings,
        out int processId);

    UnixProcessIdentity QueryIdentity(int processId);
    int GetProcessGroup(int processId);
    bool ProcessGroupExists(int processGroup);
    List<ProcessTableRow>? TryTakeProcessTable();
    Task<int> WaitForExitCodeAsync(int processId);
}

internal interface IUnixWorkerContainmentRegistry
{
    ValueTask RegisterPendingAsync(
        UnixWorkerContainmentIdentity identity,
        CancellationToken cancellationToken);

    ValueTask RegisterArmedAsync(
        UnixWorkerContainmentIdentity identity,
        CancellationToken cancellationToken);

    ValueTask<WorkerContainmentResult> CompleteAsync(
        UnixWorkerContainmentIdentity identity,
        bool brokerConfirmed,
        CancellationToken cancellationToken);

    Task WaitForEmptyAsync(UnixWorkerContainmentIdentity identity);
}

/// <summary>
/// Supervisor-owned state for one Unix session slot. The native broker owns
/// the worker process group; this registry binds the exact broker/worker
/// incarnations, observes descendants while the worker is live, and refuses a
/// new generation until the old group and every observed escape are gone.
/// </summary>
internal sealed class UnixWorkerContainmentRegistry : IUnixWorkerContainmentRegistry, IDisposable
{
    private static readonly TimeSpan ObservationInterval =
        TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly IUnixWorkerNative _native;
    private readonly CancellationTokenSource _lifetime = new();
    private Registration? _active;
    private int _disposed;

    internal UnixWorkerContainmentRegistry(IUnixWorkerNative? native = null)
    {
        _native = native ?? new UnixWorkerNative();
    }

    internal bool EscapeObserved
    {
        get
        {
            lock (_gate)
                return _active?.EscapeObserved ?? false;
        }
    }

    internal int HealthyObservationCount
    {
        get
        {
            lock (_gate)
                return _active?.HealthyObservationCount ?? 0;
        }
    }

    public ValueTask RegisterPendingAsync(
        UnixWorkerContainmentIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ValidateIdentity(identity);

        lock (_gate)
        {
            if (_active is not null)
            {
                throw new WorkerProcessException(
                    "previous_containment_unconfirmed",
                    containmentEmpty: _active.Empty.Task);
            }

            RequireLivePending(identity);
            _active = new Registration(identity);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RegisterArmedAsync(
        UnixWorkerContainmentIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        Registration registration;
        lock (_gate)
        {
            registration = RequireActive(identity);
            if (registration.Armed)
                throw new WorkerProcessException("unix_worker_registry_invalid");
            RequireLiveArmed(identity);
            registration.Armed = true;
            registration.Observer = ObserveAsync(registration, _lifetime.Token);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<WorkerContainmentResult> CompleteAsync(
        UnixWorkerContainmentIdentity identity,
        bool brokerConfirmed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var registration = GetActive(identity);
        ObserveOnce(registration);

        var confirmed = false;
        var escaped = false;
        lock (_gate)
        {
            registration.BrokerConfirmed |= brokerConfirmed;
            escaped = registration.EscapeObserved;
            confirmed = CanConfirmEmpty(registration);
            if (confirmed && !escaped)
                CompleteRegistration(registration);
            else
                registration.Confirmation ??= ConfirmEventuallyAsync(
                    registration,
                    _lifetime.Token);
        }

        await Task.Yield();
        return confirmed && !escaped
            ? WorkerContainmentResult.Confirmed()
            : WorkerContainmentResult.Unknown(
                escaped
                    ? "descendants_unknown"
                    : "unix_worker_containment_unconfirmed");
    }

    public Task WaitForEmptyAsync(UnixWorkerContainmentIdentity identity)
    {
        lock (_gate)
            return RequireActive(identity).Empty.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task ObserveAsync(
        Registration registration,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_active, registration))
                    return;
            }

            ObserveOnce(registration);
            try
            {
                await Task.Delay(ObservationInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConfirmEventuallyAsync(
        Registration registration,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ObserveOnce(registration);
            lock (_gate)
            {
                if (!ReferenceEquals(_active, registration))
                    return;
                if (CanConfirmEmpty(registration))
                {
                    CompleteRegistration(registration);
                    return;
                }
            }

            try
            {
                await Task.Delay(ObservationInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ObserveOnce(Registration registration)
    {
        List<ProcessTableRow>? snapshot;
        try
        {
            snapshot = _native.TryTakeProcessTable();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return;
        }

        if (snapshot is null)
            return;

        HashSet<int> closure = DescendantClosure(
            snapshot,
            registration.Identity.WorkerProcessId);
        Dictionary<int, ObservedProcess> observations = [];
        HashSet<int> previouslyTracked;
        lock (_gate)
        {
            if (!ReferenceEquals(_active, registration))
                return;
            previouslyTracked = registration.Descendants.Keys.ToHashSet();
        }

        foreach (var processId in closure.Concat(previouslyTracked).Distinct())
        {
            if (processId == registration.Identity.WorkerProcessId)
                continue;
            try
            {
                var identity = _native.QueryIdentity(processId);
                var processGroup = _native.GetProcessGroup(processId);
                if (identity.IsValid)
                {
                    observations[processId] = new ObservedProcess(
                        identity,
                        processGroup);
                }
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_active, registration))
                return;

            registration.HealthyObservationCount++;
            foreach (var processId in closure)
            {
                if (processId == registration.Identity.WorkerProcessId ||
                    !observations.TryGetValue(processId, out var observed))
                {
                    continue;
                }

                registration.Descendants[processId] = observed.Identity;
                if (observed.ProcessGroup !=
                    registration.Identity.WorkerProcessGroup)
                {
                    registration.EscapeObserved = true;
                    registration.EscapedDescendants[processId] =
                        observed.Identity;
                }
            }

            foreach (var (processId, expected) in
                     registration.Descendants.ToArray())
            {
                if (!observations.TryGetValue(processId, out var observed) ||
                    observed.Identity != expected)
                {
                    registration.Descendants.Remove(processId);
                    continue;
                }

                if (observed.ProcessGroup !=
                    registration.Identity.WorkerProcessGroup)
                {
                    registration.EscapeObserved = true;
                    registration.EscapedDescendants[processId] = expected;
                }
            }
        }
    }

    private bool CanConfirmEmpty(Registration registration)
    {
        if (!registration.BrokerConfirmed ||
            (registration.Armed &&
                registration.HealthyObservationCount == 0) ||
            IsIdentityLive(
                registration.Identity.BrokerProcessId,
                registration.Identity.BrokerIdentity) ||
            IsIdentityLive(
                registration.Identity.WorkerProcessId,
                registration.Identity.WorkerIdentity))
        {
            return false;
        }

        try
        {
            if (_native.ProcessGroupExists(
                    registration.Identity.WorkerProcessGroup))
            {
                return false;
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return false;
        }

        foreach (var (processId, identity) in
                 registration.EscapedDescendants)
        {
            if (IsIdentityLive(processId, identity))
                return false;
        }

        return true;
    }

    private void CompleteRegistration(Registration registration)
    {
        if (!ReferenceEquals(_active, registration))
            return;
        _active = null;
        registration.Empty.TrySetResult();
    }

    private bool IsIdentityLive(int processId, UnixProcessIdentity expected)
    {
        try
        {
            return _native.QueryIdentity(processId) == expected;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return false;
        }
    }

    private void RequireLivePending(UnixWorkerContainmentIdentity identity)
    {
        if (_native.QueryIdentity(identity.BrokerProcessId) !=
                identity.BrokerIdentity ||
            _native.QueryIdentity(identity.WorkerProcessId) !=
                identity.WorkerIdentity)
        {
            throw new WorkerProcessException("unix_worker_registry_invalid");
        }

        var supervisorGroup = _native.GetProcessGroup(0);
        if (_native.GetProcessGroup(identity.BrokerProcessId) !=
                supervisorGroup ||
            _native.GetProcessGroup(identity.WorkerProcessId) !=
                supervisorGroup ||
            _native.ProcessGroupExists(identity.WorkerProcessGroup))
        {
            throw new WorkerProcessException("unix_worker_registry_invalid");
        }
    }

    private void RequireLiveArmed(UnixWorkerContainmentIdentity identity)
    {
        if (_native.QueryIdentity(identity.BrokerProcessId) !=
                identity.BrokerIdentity ||
            _native.QueryIdentity(identity.WorkerProcessId) !=
                identity.WorkerIdentity ||
            _native.GetProcessGroup(identity.BrokerProcessId) !=
                _native.GetProcessGroup(0) ||
            _native.GetProcessGroup(identity.WorkerProcessId) !=
                identity.WorkerProcessGroup ||
            !_native.ProcessGroupExists(identity.WorkerProcessGroup))
        {
            throw new WorkerProcessException("unix_worker_registry_invalid");
        }
    }

    private Registration GetActive(UnixWorkerContainmentIdentity identity)
    {
        lock (_gate)
            return RequireActive(identity);
    }

    private Registration RequireActive(UnixWorkerContainmentIdentity identity)
    {
        if (_active is null || _active.Identity != identity)
            throw new WorkerProcessException("unix_worker_registry_invalid");
        return _active;
    }

    private static void ValidateIdentity(UnixWorkerContainmentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.BrokerProcessId <= 0 ||
            identity.WorkerProcessId <= 0 ||
            identity.BrokerProcessId == identity.WorkerProcessId ||
            identity.WorkerProcessGroup != identity.WorkerProcessId ||
            !identity.BrokerIdentity.IsValid ||
            !identity.WorkerIdentity.IsValid)
        {
            throw new WorkerProcessException("unix_worker_registry_invalid");
        }
    }

    private static HashSet<int> DescendantClosure(
        List<ProcessTableRow> snapshot,
        int rootProcessId)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var row in snapshot)
        {
            if (!children.TryGetValue(row.Ppid, out var values))
            {
                values = [];
                children[row.Ppid] = values;
            }

            values.Add(row.Pid);
        }

        var result = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(rootProcessId);
        while (pending.TryDequeue(out var parent))
        {
            if (!children.TryGetValue(parent, out var values))
                continue;
            foreach (var child in values)
            {
                if (result.Add(child))
                    pending.Enqueue(child);
            }
        }

        return result;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private readonly record struct ObservedProcess(
        UnixProcessIdentity Identity,
        int ProcessGroup);

    private sealed class Registration(UnixWorkerContainmentIdentity identity)
    {
        internal UnixWorkerContainmentIdentity Identity { get; } = identity;
        internal Dictionary<int, UnixProcessIdentity> Descendants { get; } = [];
        internal Dictionary<int, UnixProcessIdentity> EscapedDescendants { get; } = [];
        internal TaskCompletionSource Empty { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Armed;
        internal bool BrokerConfirmed;
        internal bool EscapeObserved;
        internal int HealthyObservationCount;
        internal Task? Observer;
        internal Task? Confirmation;
    }
}
