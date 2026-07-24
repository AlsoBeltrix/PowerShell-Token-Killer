using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Ownership;
using PtkMcpServer.Audit;
using PtkSharedContracts;

namespace PtkMcpGuardian.Standalone;

/// <summary>
/// Guardian-lifetime declared state for R4's single existing default session.
/// It is the sole source for both private-host recovery manifests and
/// guardian-local public session projections.
/// </summary>
internal sealed class FrozenDefaultSessionState :
    IGuardianHostRecoveryManifestSource,
    IGuardianHostSupervisorSessionSource,
    IGuardianWorkerCreateCapabilityAuthority
{
    private static ReadOnlySpan<byte> ConfigurationDigestDomain =>
        "ptk.guardian-configuration/1\0"u8;

    private readonly GuardianBootId _guardianBootId;
    private readonly FrozenSessionCatalog _catalog;
    private readonly object _sync = new();
    private GuardianHostWorkerIdentity _workerIdentity;
    private GuardianAuditSession _auditSession;
    private readonly IWorkerGenerationAllocator _workerGenerations;
    private readonly Func<CapabilityToken> _createCapabilityToken;
    private WorkerGenerationHighWatermarkEntry _workerHighWatermark;
    private WorkerGeneration? _pendingWorkerGeneration;
    private PublicSessionState _state = PublicSessionState.Ready;
    private bool _readyForEffects = true;
    private bool _warmStateLost;
    private BootstrapState _bootstrapState = BootstrapState.Restored;

    internal FrozenDefaultSessionState(
        GuardianBootId guardianBootId,
        WorkerBootId workerBootId,
        FrozenSessionCatalog catalog,
        bool allowColdBackground,
        Func<CapabilityToken>? createCapabilityToken = null)
    {
        _guardianBootId = guardianBootId ??
            throw new ArgumentNullException(nameof(guardianBootId));
        ArgumentNullException.ThrowIfNull(workerBootId);
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = new FrozenSessionCatalog(catalog.Snapshot());

        var alias = new CanonicalAlias("default");
        var transition = new SessionTransitionVersion(1);
        var workerGeneration = new WorkerGeneration(1);
        _workerIdentity = new GuardianHostWorkerIdentity(
            workerBootId,
            workerGeneration);
        Binding = new RecoveryBinding(
            alias,
            RecoveryBindingKind.Default,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            allowColdBackground,
            DesiredSessionState.Ready,
            transition,
            ComputeBindingDigest(allowColdBackground, transition));
        CatalogDigest = _catalog.CatalogDigest;
        ConfigurationDigest = ComputeConfigurationDigest(
            CatalogDigest,
            Binding.BindingDigest);
        _workerHighWatermark = new WorkerGenerationHighWatermarkEntry(
            alias,
            new WorkerGenerationHighWatermark(workerGeneration.Value));
        _workerGenerations = new PerAliasWorkerGenerationAllocator(
            [_workerHighWatermark]);
        _createCapabilityToken = createCapabilityToken ?? NewCapabilityToken;
        _auditSession = new GuardianAuditSession(Binding, workerGeneration);
    }

    internal RecoveryBinding Binding { get; }

    internal Sha256Digest CatalogDigest { get; }

    internal Sha256Digest ConfigurationDigest { get; }

    public RecoveryManifest Create(GuardianHostIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.GuardianBootId != _guardianBootId)
            throw new InvalidOperationException("The host identity belongs to another guardian boot.");

        lock (_sync)
        {
            return new RecoveryManifest(
                _guardianBootId,
                identity.HostGeneration,
                CatalogDigest,
                ConfigurationDigest,
                _catalog.Snapshot(),
                [Binding],
                [_workerHighWatermark],
                identity.HostGeneration);
        }
    }

    public GuardianWorkerCreateCapability GrantWorkerCreateCapability(
        WorkerCreateCapabilityRequestedEvent request,
        long nowUnixTimeMilliseconds,
        long maximumDeadlineUnixTimeMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (maximumDeadlineUnixTimeMilliseconds <= nowUnixTimeMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDeadlineUnixTimeMilliseconds));
        }
        if (request.GuardianBootId != _guardianBootId ||
            request.SessionAlias != Binding.Alias ||
            request.SessionTransitionVersion != Binding.TransitionVersion ||
            request.BindingDigest != Binding.BindingDigest ||
            request.StartupDeadlineUnixTimeMilliseconds <= nowUnixTimeMilliseconds ||
            request.StartupDeadlineUnixTimeMilliseconds >
                maximumDeadlineUnixTimeMilliseconds)
        {
            throw new InvalidOperationException(
                "The worker create request does not match the frozen session binding.");
        }

        var token = _createCapabilityToken() ??
            throw new InvalidOperationException(
                "The worker create capability token source returned null.");
        lock (_sync)
        {
            var generation = _workerGenerations.Allocate(Binding.Alias);
            _workerHighWatermark = new WorkerGenerationHighWatermarkEntry(
                Binding.Alias,
                new WorkerGenerationHighWatermark(generation.Value));
            _pendingWorkerGeneration = generation;
            _state = PublicSessionState.Starting;
            _readyForEffects = false;
            _bootstrapState = BootstrapState.Pending;
            return new GuardianWorkerCreateCapability(generation, token);
        }
    }

    public IReadOnlyList<PublicSessionStateSnapshot> SnapshotSessions()
    {
        lock (_sync)
        {
            return
            [
                new PublicSessionStateSnapshot(
                    Binding.Alias,
                    Binding.DesiredState,
                    _state,
                    _workerIdentity.BootId,
                    _workerIdentity.Generation,
                    Binding.TransitionVersion,
                    recoveryPhase: null,
                    recoveryAttempt: 0,
                    retryAfterMilliseconds: null,
                    readyForEffects: _readyForEffects,
                    lastFailureCode: null,
                    warmStateLost: _warmStateLost,
                    bootstrapState: _bootstrapState),
            ];
        }
    }

    public void ObserveHostReady(GuardianHostIdentity identity, bool recovered)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.GuardianBootId != _guardianBootId)
            throw new InvalidOperationException("The ready host belongs to another guardian boot.");
        if (recovered)
        {
            lock (_sync) _warmStateLost = true;
        }
    }

    public void ObserveSessionLifecycle(
        SessionLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        if (lifecycleEvent.GuardianBootId != _guardianBootId ||
            lifecycleEvent.SessionAlias != Binding.Alias ||
            lifecycleEvent.SessionTransitionVersion !=
                Binding.TransitionVersion)
        {
            throw new InvalidOperationException(
                "The session lifecycle event does not match the frozen binding.");
        }

        lock (_sync)
        {
            if (lifecycleEvent.State == PublicSessionState.Ready)
            {
                var worker = lifecycleEvent.WorkerIdentity ??
                    throw new InvalidOperationException(
                        "A ready session lifecycle event requires a worker.");
                if (_pendingWorkerGeneration is null ||
                    worker.Generation != _pendingWorkerGeneration ||
                    !lifecycleEvent.ReadyForEffects ||
                    lifecycleEvent.BootstrapState != BootstrapState.Restored)
                {
                    throw new InvalidOperationException(
                        "The ready lifecycle event does not match the pending worker grant.");
                }
                _workerIdentity = worker;
                _auditSession = new GuardianAuditSession(
                    Binding,
                    worker.Generation);
                _pendingWorkerGeneration = null;
            }
            else if (lifecycleEvent.State == PublicSessionState.Cold)
            {
                if (lifecycleEvent.WorkerIdentity is not null ||
                    lifecycleEvent.ReadyForEffects ||
                    lifecycleEvent.BootstrapState !=
                        BootstrapState.NotApplicable)
                {
                    throw new InvalidOperationException(
                        "The cold lifecycle event carries live worker state.");
                }
                _pendingWorkerGeneration = null;
            }
            else
            {
                throw new InvalidOperationException(
                    "The frozen session received a nonterminal lifecycle event.");
            }

            _state = lifecycleEvent.State;
            _readyForEffects = lifecycleEvent.ReadyForEffects;
            _warmStateLost |= lifecycleEvent.WarmStateLost;
            _bootstrapState = lifecycleEvent.BootstrapState;
        }
    }

    public void ObserveSessionRecoveryUnknown(CanonicalAlias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        if (alias != Binding.Alias)
        {
            throw new InvalidOperationException(
                "The ambiguous lifecycle result belongs to another session.");
        }

        lock (_sync)
        {
            _state = PublicSessionState.RecoveryUnknown;
            _readyForEffects = false;
            _warmStateLost = true;
            _bootstrapState = BootstrapState.Unknown;
        }
    }

    public void ObserveSessionOperationResult(
        GuardianHostSessionOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Alias != Binding.Alias ||
            result.TransitionVersion != Binding.TransitionVersion ||
            result.WorkerIdentity is { } worker &&
                (worker.BootId != _workerIdentity.BootId ||
                 worker.Generation != _workerIdentity.Generation))
        {
            throw new InvalidOperationException(
                "The session lifecycle result does not match the frozen default binding.");
        }

        lock (_sync)
        {
            _state = result.State;
            _readyForEffects = result.ReadyForEffects;
            _warmStateLost |= result.WarmStateLost;
            _bootstrapState = result.BootstrapState;
        }
    }

    public bool TryGetJobListTarget(
        CanonicalAlias alias,
        [NotNullWhen(true)] out GuardianHostJobListTarget? target)
    {
        ArgumentNullException.ThrowIfNull(alias);
        lock (_sync)
        {
            target = alias == Binding.Alias
                ? new GuardianHostJobListTarget(
                    Binding.Alias,
                    Binding.TransitionVersion,
                    _workerIdentity,
                    _auditSession,
                    _readyForEffects)
                : null;
        }
        return target is not null;
    }

    public bool TryGetJobListTargetInvalidation(
        GuardianHostJobListTarget target,
        [NotNullWhen(true)] out GuardianHostJobListTargetInvalidation? invalidation)
    {
        ArgumentNullException.ThrowIfNull(target);
        invalidation = null;
        return false;
    }

    private static Sha256Digest ComputeBindingDigest(
        bool allowColdBackground,
        SessionTransitionVersion transition)
    {
        var enabled = allowColdBackground ? "true" : "false";
        var canonical = Encoding.UTF8.GetBytes(
            $"ptk.session-binding/1\0default\0default\0{enabled}\0ready\0{transition.Value}");
        return Sha256Digest.Compute(canonical);
    }

    private static Sha256Digest ComputeConfigurationDigest(
        Sha256Digest catalogDigest,
        Sha256Digest bindingDigest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(ConfigurationDigestDomain);
        hash.AppendData(Convert.FromHexString(catalogDigest.Value));
        hash.AppendData(Convert.FromHexString(bindingDigest.Value));
        return new Sha256Digest(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static CapabilityToken NewCapabilityToken()
    {
        Span<byte> bytes = stackalloc byte[ContractLimits.CapabilityTokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return new CapabilityToken(Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));
    }
}
