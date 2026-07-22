using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Ownership;

internal sealed record GuardianJobRegistration(
    PublicJobId PublicJobId,
    long RegistrationId);

internal sealed record GuardianJobCapability(
    PublicJobId PublicJobId,
    CapabilityToken JobCapability,
    long RegistrationId,
    GuardianBootId GuardianBootId,
    HostBootId HostBootId,
    HostGeneration HostGeneration,
    CanonicalAlias SessionAlias,
    SessionTransitionVersion SessionTransitionVersion,
    GuardianHostWorkerIdentity WorkerIdentity)
{
    internal bool MatchesOwner(
        GuardianHostIdentity hostIdentity,
        CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion,
        GuardianHostWorkerIdentity workerIdentity)
    {
        ArgumentNullException.ThrowIfNull(hostIdentity);
        ArgumentNullException.ThrowIfNull(sessionAlias);
        ArgumentNullException.ThrowIfNull(sessionTransitionVersion);
        ArgumentNullException.ThrowIfNull(workerIdentity);
        return GuardianBootId == hostIdentity.GuardianBootId &&
            HostBootId == hostIdentity.HostBootId &&
            HostGeneration == hostIdentity.HostGeneration &&
            SessionAlias == sessionAlias &&
            SessionTransitionVersion == sessionTransitionVersion &&
            SameWorker(WorkerIdentity, workerIdentity);
    }

    private static bool SameWorker(
        GuardianHostWorkerIdentity left,
        GuardianHostWorkerIdentity right) =>
        left.BootId == right.BootId && left.Generation == right.Generation;
}

internal enum GuardianJobContainmentStatus
{
    Pending,
    Confirmed,
    Unconfirmed,
}

internal sealed record GuardianJobTombstone(
    PublicJobId PublicJobId,
    CanonicalAlias SessionAlias,
    string LossReason,
    GuardianJobContainmentStatus ContainmentStatus,
    GuardianHostJobState? TerminalState = null,
    int? ExitCode = null);

/// <summary>
/// Guardian-owned bounded map from irreversible public job identifiers to the
/// private capabilities returned by one exact host/session/worker generation.
/// A reservation happens before private dispatch; activation happens only
/// after the matching successful background-start response. Canceled or
/// removed identifiers are never returned to the allocator. An active job
/// whose host generation is lost remains addressable as a session-scoped
/// tombstone, but can no longer authorize private dispatch.
/// </summary>
internal sealed class GuardianJobCapabilityRegistry : IDisposable
{
    internal const int DefaultMaximumTrackedJobs = 4096;

    private readonly object _gate = new();
    private readonly IPublicJobIdAllocator _publicJobIds;
    private readonly int _maximumTrackedJobs;
    private readonly Dictionary<long, Entry> _entries = [];

    private long _nextRegistrationId;
    private bool _disposed;

    internal GuardianJobCapabilityRegistry(
        IPublicJobIdAllocator publicJobIds,
        int maximumTrackedJobs = DefaultMaximumTrackedJobs)
    {
        _publicJobIds = publicJobIds ??
            throw new ArgumentNullException(nameof(publicJobIds));
        if (maximumTrackedJobs is < 1 or > DefaultMaximumTrackedJobs)
            throw new ArgumentOutOfRangeException(nameof(maximumTrackedJobs));
        _maximumTrackedJobs = maximumTrackedJobs;
    }

    internal int TrackedCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    internal int PendingCount
    {
        get { lock (_gate) return _entries.Values.Count(entry => entry.IsPending); }
    }

    internal int ActiveCount
    {
        get { lock (_gate) return _entries.Values.Count(entry => entry.IsActive); }
    }

    internal int TombstoneCount
    {
        get
        {
            lock (_gate)
                return _entries.Values.Count(entry => entry.Tombstone is not null);
        }
    }

    internal bool TryReserve(
        GuardianHostIdentity hostIdentity,
        CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion,
        GuardianHostWorkerIdentity workerIdentity,
        out GuardianJobRegistration? registration,
        out string? failure) =>
        TryReserve(
            hostIdentity,
            sessionAlias,
            sessionTransitionVersion,
            workerIdentity,
            operationIdentity: null,
            out registration,
            out failure);

    internal bool TryReserve(
        GuardianHostIdentity hostIdentity,
        CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion,
        GuardianHostWorkerIdentity workerIdentity,
        GuardianHostOperationIdentity? operationIdentity,
        out GuardianJobRegistration? registration,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(hostIdentity);
        ArgumentNullException.ThrowIfNull(sessionAlias);
        ArgumentNullException.ThrowIfNull(sessionTransitionVersion);
        ArgumentNullException.ThrowIfNull(workerIdentity);
        registration = null;
        failure = null;

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_entries.Count >= _maximumTrackedJobs)
            {
                failure = "public_job_capacity";
                return false;
            }

            var publicJobId = _publicJobIds.Allocate();
            if (_entries.ContainsKey(publicJobId.Value))
            {
                failure = "public_job_id_duplicate";
                return false;
            }

            var registrationId = checked(++_nextRegistrationId);
            registration = new GuardianJobRegistration(
                publicJobId,
                registrationId);
            _entries.Add(
                publicJobId.Value,
                new Entry(
                    registration,
                    hostIdentity,
                    sessionAlias,
                    sessionTransitionVersion,
                    workerIdentity,
                    operationIdentity));
            return true;
        }
    }

    internal bool TryActivate(
        GuardianJobRegistration registration,
        InvokeBackgroundResult result,
        out GuardianJobCapability? capability,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(result);
        capability = null;
        failure = null;

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (result.PublicJobId != registration.PublicJobId)
            {
                failure = "public_job_response_mismatch";
                return false;
            }
            if (!_entries.TryGetValue(registration.PublicJobId.Value, out var entry) ||
                !entry.Matches(registration))
            {
                failure = "public_job_registration_unknown";
                return false;
            }
            if (!entry.IsPending)
            {
                failure = "public_job_already_active";
                return false;
            }

            capability = entry.Activate(result.JobCapability);
            return true;
        }
    }

    internal bool TryGetActive(
        PublicJobId publicJobId,
        out GuardianJobCapability? capability)
    {
        ArgumentNullException.ThrowIfNull(publicJobId);
        lock (_gate)
        {
            capability = null;
            return !_disposed &&
                _entries.TryGetValue(publicJobId.Value, out var entry) &&
                entry.IsActive &&
                (capability = entry.Capability) is not null;
        }
    }

    internal bool TryObserveLifecycleEvent(
        JobLifecycleEvent lifecycleEvent,
        out bool terminalAvailable)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        lock (_gate)
        {
            terminalAvailable = false;
            return !_disposed &&
                _entries.TryGetValue(
                    lifecycleEvent.PublicJobId.Value,
                    out var entry) &&
                entry.TryObserveLifecycleEvent(
                    lifecycleEvent,
                    out terminalAvailable);
        }
    }

    internal bool TryTakeLifecycleTerminal(
        PublicJobId publicJobId,
        out JobLifecycleEvent? lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(publicJobId);
        lock (_gate)
        {
            lifecycleEvent = null;
            if (_disposed ||
                !_entries.TryGetValue(publicJobId.Value, out var entry) ||
                entry.LifecycleTerminal is not { } found)
            {
                return false;
            }

            lifecycleEvent = found;
            entry.TakeLifecycleTerminal();
            return true;
        }
    }

    internal int MarkGenerationLost(
        GuardianHostIdentity hostIdentity,
        string reason) =>
        MarkGenerationLost(hostIdentity, reason, out _);

    internal int MarkGenerationLost(
        GuardianHostIdentity hostIdentity,
        string reason,
        out PublicJobId[] lostPublicJobIds)
    {
        ArgumentNullException.ThrowIfNull(hostIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var marked = 0;
            var lost = new List<PublicJobId>();
            foreach (var entry in _entries.Values)
            {
                if (entry.MarkLost(hostIdentity, reason))
                {
                    marked++;
                    lost.Add(entry.Registration.PublicJobId);
                }
            }
            lostPublicJobIds = [.. lost];
            return marked;
        }
    }

    internal int MarkGenerationContainmentUnconfirmed(
        GuardianHostIdentity hostIdentity)
    {
        ArgumentNullException.ThrowIfNull(hostIdentity);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var marked = 0;
            foreach (var entry in _entries.Values)
            {
                if (entry.UpdateContainment(
                        hostIdentity,
                        GuardianJobContainmentStatus.Unconfirmed))
                {
                    marked++;
                }
            }
            return marked;
        }
    }

    internal int ConfirmGenerationContainment(
        GuardianHostIdentity hostIdentity)
    {
        ArgumentNullException.ThrowIfNull(hostIdentity);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var marked = 0;
            foreach (var entry in _entries.Values)
            {
                if (entry.UpdateContainment(
                        hostIdentity,
                        GuardianJobContainmentStatus.Confirmed))
                {
                    marked++;
                }
            }
            return marked;
        }
    }

    internal bool TryGetTombstone(
        PublicJobId publicJobId,
        CanonicalAlias sessionAlias,
        out GuardianJobTombstone? tombstone)
    {
        ArgumentNullException.ThrowIfNull(publicJobId);
        ArgumentNullException.ThrowIfNull(sessionAlias);
        lock (_gate)
        {
            tombstone = null;
            if (_disposed ||
                !_entries.TryGetValue(publicJobId.Value, out var entry) ||
                entry.Tombstone is not { } found ||
                found.SessionAlias != sessionAlias)
            {
                return false;
            }

            tombstone = found;
            return true;
        }
    }

    internal GuardianJobTombstone[] SnapshotTombstones(CanonicalAlias sessionAlias)
    {
        ArgumentNullException.ThrowIfNull(sessionAlias);
        lock (_gate)
        {
            if (_disposed) return [];
            return _entries.Values
                .Select(entry => entry.Tombstone)
                .Where(tombstone => tombstone?.SessionAlias == sessionAlias)
                .Select(tombstone => tombstone!)
                .OrderBy(tombstone => tombstone.PublicJobId.Value)
                .ToArray();
        }
    }

    internal bool HasActiveOwner(
        GuardianHostIdentity hostIdentity,
        CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion,
        GuardianHostWorkerIdentity workerIdentity)
    {
        ArgumentNullException.ThrowIfNull(hostIdentity);
        ArgumentNullException.ThrowIfNull(sessionAlias);
        ArgumentNullException.ThrowIfNull(sessionTransitionVersion);
        ArgumentNullException.ThrowIfNull(workerIdentity);
        lock (_gate)
        {
            return !_disposed && _entries.Values.Any(entry =>
                entry.IsActive &&
                entry.Capability!.MatchesOwner(
                    hostIdentity,
                    sessionAlias,
                    sessionTransitionVersion,
                    workerIdentity));
        }
    }

    internal bool TryCancel(GuardianJobRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            if (_disposed ||
                !_entries.TryGetValue(registration.PublicJobId.Value, out var entry) ||
                !entry.Matches(registration) ||
                !entry.IsPending ||
                entry.HasObservedLifecycleTerminal)
            {
                return false;
            }

            return _entries.Remove(registration.PublicJobId.Value);
        }
    }

    internal bool TryRemove(GuardianJobCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        lock (_gate)
        {
            if (_disposed ||
                !_entries.TryGetValue(capability.PublicJobId.Value, out var entry) ||
                !entry.Matches(capability))
            {
                return false;
            }

            return _entries.Remove(capability.PublicJobId.Value);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _entries.Clear();
        }
    }

    private void ThrowIfDisposedLocked()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GuardianJobCapabilityRegistry));
    }

    private sealed class Entry
    {
        private readonly GuardianHostIdentity _hostIdentity;
        private readonly CanonicalAlias _sessionAlias;
        private readonly SessionTransitionVersion _sessionTransitionVersion;
        private readonly GuardianHostWorkerIdentity _workerIdentity;
        private readonly GuardianHostOperationIdentity? _operationIdentity;

        internal Entry(
            GuardianJobRegistration registration,
            GuardianHostIdentity hostIdentity,
            CanonicalAlias sessionAlias,
            SessionTransitionVersion sessionTransitionVersion,
            GuardianHostWorkerIdentity workerIdentity,
            GuardianHostOperationIdentity? operationIdentity)
        {
            Registration = registration;
            _hostIdentity = hostIdentity;
            _sessionAlias = sessionAlias;
            _sessionTransitionVersion = sessionTransitionVersion;
            _workerIdentity = workerIdentity;
            _operationIdentity = operationIdentity;
        }

        internal GuardianJobRegistration Registration { get; }

        internal GuardianJobCapability? Capability { get; private set; }

        internal GuardianJobTombstone? Tombstone { get; private set; }

        internal JobLifecycleEvent? LifecycleTerminal { get; private set; }

        internal JobLifecycleEvent? ObservedLifecycleTerminal { get; private set; }

        private bool LifecycleTerminalObserved { get; set; }

        internal bool HasObservedLifecycleTerminal => LifecycleTerminalObserved;

        internal bool IsPending => Capability is null && Tombstone is null;

        internal bool IsActive => Capability is not null && Tombstone is null;

        internal bool Matches(GuardianJobRegistration registration) =>
            Registration.PublicJobId == registration.PublicJobId &&
            Registration.RegistrationId == registration.RegistrationId;

        internal bool Matches(GuardianJobCapability capability) =>
            Capability is { } active &&
            active.PublicJobId == capability.PublicJobId &&
            active.JobCapability == capability.JobCapability &&
            active.RegistrationId == capability.RegistrationId &&
            active.GuardianBootId == capability.GuardianBootId &&
            active.HostBootId == capability.HostBootId &&
            active.HostGeneration == capability.HostGeneration &&
            active.SessionAlias == capability.SessionAlias &&
            active.SessionTransitionVersion == capability.SessionTransitionVersion &&
            SameWorker(active.WorkerIdentity, capability.WorkerIdentity);

        internal GuardianJobCapability Activate(CapabilityToken jobCapability)
        {
            Capability = new GuardianJobCapability(
                Registration.PublicJobId,
                jobCapability,
                Registration.RegistrationId,
                _hostIdentity.GuardianBootId,
                _hostIdentity.HostBootId,
                _hostIdentity.HostGeneration,
                _sessionAlias,
                _sessionTransitionVersion,
                _workerIdentity);
            return Capability;
        }

        internal bool TryObserveLifecycleEvent(
            JobLifecycleEvent lifecycleEvent,
            out bool terminalAvailable)
        {
            terminalAvailable = false;
            if (!MatchesLifecycleEvent(lifecycleEvent) || LifecycleTerminalObserved)
                return false;

            if (lifecycleEvent.State is
                GuardianHostJobState.Queued or GuardianHostJobState.Running)
            {
                return lifecycleEvent.ExitCode is null;
            }
            if (lifecycleEvent.OutputState == GuardianHostOutputState.Streaming ||
                (lifecycleEvent.State is
                    GuardianHostJobState.Completed or GuardianHostJobState.Failed) &&
                    lifecycleEvent.ExitCode is null)
            {
                return false;
            }

            LifecycleTerminalObserved = true;
            LifecycleTerminal = lifecycleEvent;
            ObservedLifecycleTerminal = lifecycleEvent;
            terminalAvailable = true;
            return true;
        }

        internal void TakeLifecycleTerminal()
        {
            if (LifecycleTerminal is null)
            {
                throw new InvalidOperationException(
                    "The guardian job has no pending lifecycle terminal.");
            }
            LifecycleTerminal = null;
        }

        internal bool MarkLost(
            GuardianHostIdentity hostIdentity,
            string reason)
        {
            if (Capability is null || Tombstone is not null ||
                !MatchesHost(hostIdentity))
            {
                return false;
            }

            Tombstone = new GuardianJobTombstone(
                Registration.PublicJobId,
                _sessionAlias,
                reason,
                GuardianJobContainmentStatus.Pending,
                ObservedLifecycleTerminal?.State,
                ObservedLifecycleTerminal?.ExitCode);
            Capability = null;
            return true;
        }

        internal bool UpdateContainment(
            GuardianHostIdentity hostIdentity,
            GuardianJobContainmentStatus containmentStatus)
        {
            if (Tombstone is not { } tombstone ||
                !MatchesHost(hostIdentity) ||
                tombstone.ContainmentStatus == containmentStatus ||
                tombstone.ContainmentStatus == GuardianJobContainmentStatus.Confirmed)
            {
                return false;
            }

            Tombstone = tombstone with { ContainmentStatus = containmentStatus };
            return true;
        }

        private bool MatchesHost(GuardianHostIdentity hostIdentity) =>
            _hostIdentity.GuardianBootId == hostIdentity.GuardianBootId &&
            _hostIdentity.HostBootId == hostIdentity.HostBootId &&
            _hostIdentity.HostGeneration == hostIdentity.HostGeneration;

        private bool MatchesLifecycleEvent(JobLifecycleEvent lifecycleEvent) =>
            lifecycleEvent.RequestId is null &&
            lifecycleEvent.GuardianBootId == _hostIdentity.GuardianBootId &&
            lifecycleEvent.HostBootId == _hostIdentity.HostBootId &&
            lifecycleEvent.HostGeneration == _hostIdentity.HostGeneration &&
            lifecycleEvent.PublicJobId == Registration.PublicJobId &&
            lifecycleEvent.SessionAlias == _sessionAlias &&
            lifecycleEvent.SessionTransitionVersion == _sessionTransitionVersion &&
            SameWorker(lifecycleEvent.WorkerIdentity!, _workerIdentity) &&
            SameOperation(lifecycleEvent.OperationIdentity, _operationIdentity);

        private static bool SameOperation(
            GuardianHostOperationIdentity? left,
            GuardianHostOperationIdentity? right) =>
            left is not null && right is not null &&
            left.PlanId == right.PlanId &&
            left.OperationId == right.OperationId;

        private static bool SameWorker(
            GuardianHostWorkerIdentity left,
            GuardianHostWorkerIdentity right) =>
            left.BootId == right.BootId && left.Generation == right.Generation;
    }
}
