using System.Diagnostics.CodeAnalysis;
using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Standalone.Fake;

/// <summary>
/// The deliberately nonproduction R3 composition root. It exists only to
/// prove that one public MCP transport can outlive replaceable host
/// generations before the real private-host launcher is wired in R4.
/// </summary>
internal sealed class R3FakeGuardianComposition : IAsyncDisposable
{
    internal static readonly TimeSpan HostStartupTimeout = TimeSpan.FromSeconds(30);

    private readonly GuardianHostSupervisor _supervisor;
    private int _runClaimed;
    private int _disposeClaimed;

    private R3FakeGuardianComposition(
        GuardianBootId guardianBootId,
        GuardianHostSupervisorPins pins,
        R3FakeHostControl control,
        R3FakeHostAttemptFactory factory,
        GuardianHostSupervisor supervisor)
    {
        GuardianBootId = guardianBootId;
        Pins = pins;
        Control = control;
        Factory = factory;
        _supervisor = supervisor;
    }

    internal GuardianBootId GuardianBootId { get; }

    internal GuardianHostSupervisorPins Pins { get; }

    internal R3FakeHostControl Control { get; }

    internal R3FakeHostAttemptFactory Factory { get; }

    internal GuardianHostSupervisor Supervisor => _supervisor;

    internal static R3FakeGuardianComposition Create(
        R3FakeHostControl? control = null,
        R3FakeHostProfile? profile = null,
        TimeProvider? timeProvider = null,
        IGuardianHostSupervisorScheduler? scheduler = null)
    {
        var selectedControl = control ?? new R3FakeHostControl();
        var selectedProfile = profile ?? R3FakeHostProfile.StrictDefault;
        var selectedTimeProvider = timeProvider ?? TimeProvider.System;
        var selectedScheduler = scheduler ??
            new R3SystemGuardianHostSupervisorScheduler(selectedTimeProvider);
        var guardianBootId = new GuardianBootId(Guid.NewGuid());
        var pins = CreatePins();
        var factory = new R3FakeHostAttemptFactory(
            pins,
            selectedControl,
            profile: selectedProfile);
        var lifecycle = new GuardianHostLifecycleController(
            guardianBootId,
            new MonotonicHostGenerationAllocator(),
            new R3RandomHostBootIdSource(),
            new R3GuardianHostStartupDeadlineSource(
                selectedTimeProvider,
                HostStartupTimeout),
            factory,
            selectedTimeProvider);
        var supervisor = new GuardianHostSupervisor(
            guardianBootId,
            lifecycle,
            new R3FakeRecoveryManifestSource(
                guardianBootId,
                pins,
                selectedProfile),
            new MonotonicPrivateRequestIdAllocator(),
            selectedTimeProvider,
            selectedScheduler,
            new R3FakeSessionSource(selectedProfile),
            R3NoOpDispatchObserver.Instance,
            pins);
        return new R3FakeGuardianComposition(
            guardianBootId,
            pins,
            selectedControl,
            factory,
            supervisor);
    }

    internal async Task RunAsync(
        Stream publicInput,
        Stream publicOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publicInput);
        ArgumentNullException.ThrowIfNull(publicOutput);
        if (Interlocked.CompareExchange(ref _runClaimed, 1, 0) != 0)
            throw new InvalidOperationException("The R3 guardian composition can run only once.");

        try
        {
            await _supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
            await new GuardianMcpApplication(_supervisor)
                .RunAsync(publicInput, publicOutput, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await _supervisor.ShutdownAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeClaimed, 1) != 0)
            return;
        await _supervisor.DisposeAsync().ConfigureAwait(false);
    }

    private static GuardianHostSupervisorPins CreatePins() => new(
        Sha256Digest.Compute("ptk.r3.fake-host/executable"u8),
        Sha256Digest.Compute("ptk.r3.fake-host/build"u8),
        PublicToolContractResource.ComputeDigest(),
        Sha256Digest.Compute("ptk.r3.fake-host/configuration"u8),
        Sha256Digest.Compute("ptk.r3.fake-host/catalog"u8),
        Sha256Digest.Compute("ptk.r3.fake-host/package"u8));
}

internal sealed class R3RandomHostBootIdSource : IGuardianHostBootIdSource
{
    public HostBootId Next() => new(Guid.NewGuid());
}

internal sealed class R3GuardianHostStartupDeadlineSource :
    IGuardianHostStartupDeadlineSource
{
    private readonly TimeProvider _timeProvider;
    private readonly long _timeoutTimestampTicks;

    internal R3GuardianHostStartupDeadlineSource(
        TimeProvider timeProvider,
        TimeSpan timeout)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeoutTimestampTicks = checked((long)Math.Ceiling(
            timeout.TotalSeconds * timeProvider.TimestampFrequency));
        if (_timeoutTimestampTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public GuardianHostStartupDeadline Next() => new(
        checked(_timeProvider.GetTimestamp() + _timeoutTimestampTicks));
}

internal sealed class R3SystemGuardianHostSupervisorScheduler(TimeProvider timeProvider) :
    IGuardianHostSupervisorScheduler
{
    private readonly TimeProvider _timeProvider = timeProvider ??
        throw new ArgumentNullException(nameof(timeProvider));

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));
        return new ValueTask(Task.Delay(delay, _timeProvider, cancellationToken));
    }
}

internal sealed class R3FakeRecoveryManifestSource :
    IGuardianHostRecoveryManifestSource
{
    private readonly GuardianBootId _guardianBootId;
    private readonly GuardianHostSupervisorPins _pins;
    private readonly R3FakeHostProfile _profile;

    internal R3FakeRecoveryManifestSource(
        GuardianBootId guardianBootId,
        GuardianHostSupervisorPins pins,
        R3FakeHostProfile profile)
    {
        _guardianBootId = guardianBootId ??
            throw new ArgumentNullException(nameof(guardianBootId));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public RecoveryManifest Create(GuardianHostIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.GuardianBootId != _guardianBootId)
            throw new InvalidOperationException("The fake manifest identity is foreign.");

        return new RecoveryManifest(
            _guardianBootId,
            identity.HostGeneration,
            _pins.CatalogDigest,
            _pins.ConfigurationDigest,
            [],
            [
                new RecoveryBinding(
                    _profile.JobListTarget.Alias,
                    RecoveryBindingKind.Default,
                    templateName: null,
                    templateDigest: null,
                    bootstrapDigest: null,
                    _profile.AllowColdBackground,
                    _profile.DesiredState,
                    _profile.JobListTarget.TransitionVersion,
                    _profile.BindingDigest),
            ],
            [
                new WorkerGenerationHighWatermarkEntry(
                    _profile.JobListTarget.Alias,
                    _profile.WorkerGenerationHighWatermark),
            ],
            identity.HostGeneration);
    }
}

internal sealed class R3FakeSessionSource : IGuardianHostSupervisorSessionSource
{
    private readonly R3FakeHostProfile _profile;

    internal R3FakeSessionSource(R3FakeHostProfile profile) =>
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

    public IReadOnlyList<PublicSessionStateSnapshot> SnapshotSessions()
    {
        var target = _profile.JobListTarget;
        return
        [
            new PublicSessionStateSnapshot(
                target.Alias,
                _profile.DesiredState,
                PublicSessionState.Ready,
                target.WorkerIdentity.BootId,
                target.WorkerIdentity.Generation,
                target.TransitionVersion,
                recoveryPhase: null,
                recoveryAttempt: 0,
                retryAfterMilliseconds: null,
                readyForEffects: true,
                lastFailureCode: null,
                warmStateLost: false,
                bootstrapState: BootstrapState.Restored),
        ];
    }

    public bool TryGetJobListTarget(
        [NotNullWhen(true)] out GuardianHostJobListTarget? target)
    {
        target = _profile.JobListTarget;
        return true;
    }

    public bool TryGetJobListTargetInvalidation(
        GuardianHostJobListTarget target,
        [NotNullWhen(true)] out GuardianHostJobListTargetInvalidation? invalidation)
    {
        ArgumentNullException.ThrowIfNull(target);
        invalidation = null;
        return false;
    }
}

internal sealed class R3NoOpDispatchObserver : IGuardianHostSupervisorDispatchObserver
{
    internal static R3NoOpDispatchObserver Instance { get; } = new();

    private R3NoOpDispatchObserver() { }

    public ValueTask BeforeWriteAuthorizationAsync(
        GuardianHostDispatchObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public void OnWriteStarting(GuardianHostDispatchObservation observation) =>
        ArgumentNullException.ThrowIfNull(observation);

    public void OnTerminalDecoded(GuardianHostDispatchObservation observation) =>
        ArgumentNullException.ThrowIfNull(observation);
}
