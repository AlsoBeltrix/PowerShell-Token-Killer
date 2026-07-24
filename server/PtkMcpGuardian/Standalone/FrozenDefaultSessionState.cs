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
/// guardian-local public session projections. State is held per alias behind
/// one lock so later dynamic/template bindings cannot reopen single-alias
/// assumptions; every current observer still rejects any alias it does not
/// declare.
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
    private readonly Dictionary<CanonicalAlias, AliasState> _aliases = [];
    private readonly IWorkerGenerationAllocator _workerGenerations;
    private readonly Func<CapabilityToken> _createCapabilityToken;

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
        var binding = new RecoveryBinding(
            alias,
            RecoveryBindingKind.Default,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            allowColdBackground,
            DesiredSessionState.Ready,
            transition,
            ComputeBindingDigest(
                alias,
                RecoveryBindingKind.Default,
                allowColdBackground,
                DesiredSessionState.Ready,
                transition));
        CatalogDigest = _catalog.CatalogDigest;
        ConfigurationDigest = ComputeConfigurationDigest(
            CatalogDigest,
            binding.BindingDigest);
        var highWatermark = new WorkerGenerationHighWatermarkEntry(
            alias,
            new WorkerGenerationHighWatermark(workerGeneration.Value));
        _workerGenerations = new PerAliasWorkerGenerationAllocator([highWatermark]);
        _createCapabilityToken = createCapabilityToken ?? NewCapabilityToken;
        _aliases.Add(
            alias,
            new AliasState(
                binding,
                new GuardianHostWorkerIdentity(workerBootId, workerGeneration),
                new GuardianAuditSession(binding, workerGeneration),
                highWatermark)
            {
                State = PublicSessionState.Ready,
                ReadyForEffects = true,
                BootstrapState = BootstrapState.Restored,
            });
        Binding = binding;
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
            var ordered = _aliases.Values
                .OrderBy(state => state.Binding.Alias.Value, StringComparer.Ordinal)
                .ToArray();
            return new RecoveryManifest(
                _guardianBootId,
                identity.HostGeneration,
                CatalogDigest,
                ConfigurationDigest,
                _catalog.Snapshot(),
                ordered.Select(state => state.Binding).ToArray(),
                ordered.Select(state => state.HighWatermark).ToArray(),
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
        if (request.GuardianBootId != _guardianBootId)
        {
            throw new InvalidOperationException(
                "The worker create request does not match the frozen session binding.");
        }

        lock (_sync)
        {
            if (!_aliases.TryGetValue(request.SessionAlias, out var state) ||
                request.SessionTransitionVersion != state.Binding.TransitionVersion ||
                request.BindingDigest != state.Binding.BindingDigest ||
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
            var generation = _workerGenerations.Allocate(request.SessionAlias);
            state.HighWatermark = new WorkerGenerationHighWatermarkEntry(
                request.SessionAlias,
                new WorkerGenerationHighWatermark(generation.Value));
            state.PendingWorkerGeneration = generation;
            state.State = PublicSessionState.Starting;
            state.ReadyForEffects = false;
            state.BootstrapState = BootstrapState.Pending;
            return new GuardianWorkerCreateCapability(generation, token);
        }
    }

    public IReadOnlyList<PublicSessionStateSnapshot> SnapshotSessions()
    {
        lock (_sync)
        {
            return _aliases.Values
                .OrderBy(state => state.Binding.Alias.Value, StringComparer.Ordinal)
                .Select(state => new PublicSessionStateSnapshot(
                    state.Binding.Alias,
                    state.Binding.DesiredState,
                    state.State,
                    state.WorkerIdentity.BootId,
                    state.WorkerIdentity.Generation,
                    state.Binding.TransitionVersion,
                    recoveryPhase: null,
                    recoveryAttempt: 0,
                    retryAfterMilliseconds: null,
                    readyForEffects: state.ReadyForEffects,
                    lastFailureCode: null,
                    warmStateLost: state.WarmStateLost,
                    bootstrapState: state.BootstrapState))
                .ToArray();
        }
    }

    public void ObserveHostReady(GuardianHostIdentity identity, bool recovered)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.GuardianBootId != _guardianBootId)
            throw new InvalidOperationException("The ready host belongs to another guardian boot.");
        if (recovered)
        {
            lock (_sync)
            {
                foreach (var state in _aliases.Values)
                {
                    state.WarmStateLost = true;
                }
            }
        }
    }

    public void ObserveSessionLifecycle(
        SessionLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        if (lifecycleEvent.GuardianBootId != _guardianBootId)
        {
            throw new InvalidOperationException(
                "The session lifecycle event does not match the frozen binding.");
        }

        lock (_sync)
        {
            if (!_aliases.TryGetValue(lifecycleEvent.SessionAlias, out var state) ||
                lifecycleEvent.SessionTransitionVersion !=
                    state.Binding.TransitionVersion)
            {
                throw new InvalidOperationException(
                    "The session lifecycle event does not match the frozen binding.");
            }

            if (lifecycleEvent.State == PublicSessionState.Ready)
            {
                var worker = lifecycleEvent.WorkerIdentity ??
                    throw new InvalidOperationException(
                        "A ready session lifecycle event requires a worker.");
                if (state.PendingWorkerGeneration is null ||
                    worker.Generation != state.PendingWorkerGeneration ||
                    !lifecycleEvent.ReadyForEffects ||
                    lifecycleEvent.BootstrapState != BootstrapState.Restored)
                {
                    throw new InvalidOperationException(
                        "The ready lifecycle event does not match the pending worker grant.");
                }
                state.WorkerIdentity = worker;
                state.AuditSession = new GuardianAuditSession(
                    state.Binding,
                    worker.Generation);
                state.PendingWorkerGeneration = null;
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
                state.PendingWorkerGeneration = null;
            }
            else
            {
                throw new InvalidOperationException(
                    "The frozen session received a nonterminal lifecycle event.");
            }

            state.State = lifecycleEvent.State;
            state.ReadyForEffects = lifecycleEvent.ReadyForEffects;
            state.WarmStateLost |= lifecycleEvent.WarmStateLost;
            state.BootstrapState = lifecycleEvent.BootstrapState;
        }
    }

    public void ObserveSessionRecoveryUnknown(CanonicalAlias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        lock (_sync)
        {
            if (!_aliases.TryGetValue(alias, out var state))
            {
                throw new InvalidOperationException(
                    "The ambiguous lifecycle result belongs to another session.");
            }

            state.State = PublicSessionState.RecoveryUnknown;
            state.ReadyForEffects = false;
            state.WarmStateLost = true;
            state.BootstrapState = BootstrapState.Unknown;
        }
    }

    public void ObserveSessionOperationResult(
        GuardianHostSessionOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_sync)
        {
            if (!_aliases.TryGetValue(result.Alias, out var state) ||
                result.TransitionVersion != state.Binding.TransitionVersion ||
                result.WorkerIdentity is { } worker &&
                (worker.BootId != state.WorkerIdentity.BootId ||
                 worker.Generation != state.WorkerIdentity.Generation))
            {
                throw new InvalidOperationException(
                    "The session lifecycle result does not match the frozen default binding.");
            }

            state.State = result.State;
            state.ReadyForEffects = result.ReadyForEffects;
            state.WarmStateLost |= result.WarmStateLost;
            state.BootstrapState = result.BootstrapState;
        }
    }

    public bool TryGetJobListTarget(
        CanonicalAlias alias,
        [NotNullWhen(true)] out GuardianHostJobListTarget? target)
    {
        ArgumentNullException.ThrowIfNull(alias);
        lock (_sync)
        {
            target = _aliases.TryGetValue(alias, out var state)
                ? new GuardianHostJobListTarget(
                    state.Binding.Alias,
                    state.Binding.TransitionVersion,
                    state.WorkerIdentity,
                    state.AuditSession,
                    state.ReadyForEffects)
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
        CanonicalAlias alias,
        RecoveryBindingKind kind,
        bool allowColdBackground,
        DesiredSessionState desiredState,
        SessionTransitionVersion transition)
    {
        var kindText = kind switch
        {
            RecoveryBindingKind.Default => "default",
            RecoveryBindingKind.Dynamic => "dynamic",
            RecoveryBindingKind.Template => "template",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var desiredText = desiredState switch
        {
            DesiredSessionState.Ready => "ready",
            DesiredSessionState.Cold => "cold",
            _ => throw new ArgumentOutOfRangeException(nameof(desiredState)),
        };
        var enabled = allowColdBackground ? "true" : "false";
        var canonical = Encoding.UTF8.GetBytes(
            $"ptk.session-binding/1\0{alias.Value}\0{kindText}\0{enabled}\0{desiredText}\0{transition.Value}");
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

    private sealed class AliasState(
        RecoveryBinding binding,
        GuardianHostWorkerIdentity workerIdentity,
        GuardianAuditSession auditSession,
        WorkerGenerationHighWatermarkEntry highWatermark)
    {
        internal RecoveryBinding Binding { get; } = binding;
        internal GuardianHostWorkerIdentity WorkerIdentity { get; set; } = workerIdentity;
        internal GuardianAuditSession AuditSession { get; set; } = auditSession;
        internal WorkerGenerationHighWatermarkEntry HighWatermark { get; set; } = highWatermark;
        internal WorkerGeneration? PendingWorkerGeneration { get; set; }
        internal PublicSessionState State { get; set; }
        internal bool ReadyForEffects { get; set; }
        internal bool WarmStateLost { get; set; }
        internal BootstrapState BootstrapState { get; set; }
    }
}
