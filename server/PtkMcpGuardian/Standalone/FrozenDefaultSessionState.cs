using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
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
    private readonly Func<WorkerBootId> _workerBootIdSource;

    internal FrozenDefaultSessionState(
        GuardianBootId guardianBootId,
        WorkerBootId workerBootId,
        FrozenSessionCatalog catalog,
        bool allowColdBackground,
        Func<CapabilityToken>? createCapabilityToken = null,
        Func<WorkerBootId>? workerBootIdSource = null)
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
            RecoveryBinding.ComputeBindingDigest(
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
        _workerBootIdSource = workerBootIdSource ??
            (static () => new WorkerBootId(Guid.NewGuid()));
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
                ordered.Select(state => state.DesiredState ==
                        state.Binding.DesiredState
                    ? state.Binding
                    : new RecoveryBinding(
                        state.Binding.Alias,
                        state.Binding.BindingKind,
                        state.Binding.TemplateName,
                        state.Binding.TemplateDigest,
                        state.Binding.BootstrapDigest,
                        state.Binding.AllowColdBackground,
                        state.DesiredState,
                        state.Binding.TransitionVersion,
                        state.Binding.BindingDigest)).ToArray(),
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

    public RecoveryBinding? GetDeclaredBinding(CanonicalAlias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        lock (_sync)
        {
            return _aliases.TryGetValue(alias, out var state)
                ? state.Binding
                : null;
        }
    }

    public void MarkDynamicAliasOpenFailed(CanonicalAlias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        lock (_sync)
        {
            if (!_aliases.TryGetValue(alias, out var state) ||
                state.Binding.BindingKind != RecoveryBindingKind.Dynamic)
            {
                return;
            }
            state.DesiredState = DesiredSessionState.Cold;
        }
    }

    public IReadOnlyList<PublicSessionStateSnapshot> SnapshotSessions()
    {
        lock (_sync)
        {
            return _aliases.Values
                .OrderBy(state => state.Binding.Alias.Value, StringComparer.Ordinal)
                .Select(state => ProjectSession(state))
                .ToArray();
        }
    }

    private static PublicSessionStateSnapshot ProjectSession(AliasState state)
    {
        // While recovery is in flight the alias's bound identity is the dead
        // generation, so report exactly the worker the host named for this
        // recovery and nothing else. A cold alias has no worker at all.
        var projectedWorker = state.State == PublicSessionState.Cold
            ? null
            : SessionLifecycleEvent.CarriesRecoveryMetadata(state.State)
                ? state.RecoveringWorkerIdentity
                : state.WorkerIdentity;
        return new PublicSessionStateSnapshot(
                    state.Binding.Alias,
                    state.DesiredState,
                    state.State,
                    projectedWorker?.BootId,
                    projectedWorker?.Generation,
                    state.Binding.TransitionVersion,
                    state.RecoveryPhase,
                    state.RecoveryAttempt,
                    state.RetryAfterMilliseconds,
                    readyForEffects: state.ReadyForEffects,
                    lastFailureCode: null,
                    warmStateLost: state.WarmStateLost,
                    bootstrapState: state.BootstrapState);
    }

    /// <summary>
    /// Declares one dynamic alias at open-dispatch time. The declared dispatch
    /// identity consumes generation 1 exactly like the default alias's, so the
    /// first capability grant allocates generation 2. The public projection is
    /// cold with no worker until the first grant and ready lifecycle land.
    /// </summary>
    public RecoveryBinding DeclareDynamicAlias(
        CanonicalAlias alias,
        bool allowColdBackground)
    {
        ArgumentNullException.ThrowIfNull(alias);
        if (alias.Value == "default")
            throw new ArgumentException(
                "The default alias cannot be redeclared.", nameof(alias));

        var transition = new SessionTransitionVersion(1);
        var workerGeneration = new WorkerGeneration(1);
        var binding = new RecoveryBinding(
            alias,
            RecoveryBindingKind.Dynamic,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            allowColdBackground,
            DesiredSessionState.Ready,
            transition,
            RecoveryBinding.ComputeBindingDigest(
                alias,
                RecoveryBindingKind.Dynamic,
                allowColdBackground,
                DesiredSessionState.Ready,
                transition));
        var bootId = _workerBootIdSource() ??
            throw new InvalidOperationException(
                "The worker boot ID source returned null.");
        var highWatermark = new WorkerGenerationHighWatermarkEntry(
            alias,
            new WorkerGenerationHighWatermark(workerGeneration.Value));
        lock (_sync)
        {
            if (_aliases.Count >= ContractLimits.MaximumAliases ||
                !_aliases.TryAdd(
                    alias,
                    new AliasState(
                        binding,
                        new GuardianHostWorkerIdentity(bootId, workerGeneration),
                        new GuardianAuditSession(binding, workerGeneration),
                        highWatermark)
                    {
                        State = PublicSessionState.Cold,
                        ReadyForEffects = false,
                        WarmStateLost = true,
                        BootstrapState = BootstrapState.Unknown,
                    }))
            {
                throw new InvalidOperationException(
                    "The dynamic session alias is already declared or exhausted.");
            }
            if (_workerGenerations.Allocate(alias) != workerGeneration)
            {
                throw new InvalidOperationException(
                    "The declared dynamic alias lost its generation watermark.");
            }
        }
        return binding;
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
            else if (lifecycleEvent.State == PublicSessionState.Faulted)
            {
                var faultWorker = lifecycleEvent.WorkerIdentity ??
                    throw new InvalidOperationException(
                        "A faulted session lifecycle event requires a worker.");
                if (faultWorker.BootId != state.WorkerIdentity.BootId ||
                    faultWorker.Generation != state.WorkerIdentity.Generation ||
                    lifecycleEvent.ReadyForEffects ||
                    lifecycleEvent.BootstrapState != BootstrapState.Failed ||
                    lifecycleEvent.Reason is not (
                        GuardianHostSessionLifecycleReason.ContainmentUnconfirmed or
                        GuardianHostSessionLifecycleReason.BootstrapFailed or
                        GuardianHostSessionLifecycleReason.CircuitTransition))
                {
                    throw new InvalidOperationException(
                        "The faulted lifecycle event does not match the current worker or reason.");
                }
                state.PendingWorkerGeneration = null;
            }
            else if (SessionLifecycleEvent.CarriesRecoveryMetadata(lifecycleEvent.State))
            {
                // Automatic recovery in flight for this alias. The event names a
                // worker only where the wire contract requires one, so validate
                // the identity when present rather than demanding it: it is
                // either the worker whose loss started recovery, or the pending
                // grant being bootstrapped. The identity is deliberately NOT
                // absorbed here — recovery has produced no bound worker yet, and
                // only the Ready branch's grant match may rebind the alias.
                if (lifecycleEvent.WorkerIdentity is { } recoveringWorker)
                {
                    var namesCurrentWorker =
                        recoveringWorker.BootId == state.WorkerIdentity.BootId &&
                        recoveringWorker.Generation == state.WorkerIdentity.Generation;
                    var namesPendingGrant =
                        state.PendingWorkerGeneration is { } pendingGeneration &&
                        recoveringWorker.Generation == pendingGeneration;
                    if (!namesCurrentWorker && !namesPendingGrant)
                    {
                        throw new InvalidOperationException(
                            "The recovering lifecycle event names an unknown worker.");
                    }
                }
                if (lifecycleEvent.ReadyForEffects)
                {
                    throw new InvalidOperationException(
                        "A recovering session cannot be ready for effects.");
                }
            }
            else
            {
                throw new InvalidOperationException(
                    "The frozen session received a nonterminal lifecycle event.");
            }

            if (state.AmbiguousUntilRepaired &&
                (lifecycleEvent.State == PublicSessionState.Ready ||
                    SessionLifecycleEvent.CarriesRecoveryMetadata(lifecycleEvent.State)))
            {
                // Neither a host restoring its declared session nor an automatic
                // recovery is the explicit repair this alias is waiting for. The
                // worker identity above is absorbed deliberately, so a later
                // authoritative repair targets the live worker; the projection
                // stays unusable. Committing the Ready event here would silently
                // erase the ambiguous outcome of the earlier session-changing
                // request and let ordinary work be dispatched into a session
                // whose outcome is unknown, which is exactly the no-replay
                // boundary this state exists to hold. A recovering event is
                // refused for the same reason in the other direction: it would
                // downgrade a nonretryable recovery_unknown to a retryable
                // session_recovering and invite the model to resubmit work whose
                // first outcome nobody knows.
                state.State = PublicSessionState.RecoveryUnknown;
                state.ReadyForEffects = false;
                state.WarmStateLost = true;
                state.BootstrapState = BootstrapState.Unknown;
                ClearRecoveryMetadata(state);
                return;
            }

            // Capture the exact target this transition invalidates, atomically
            // with the transition itself. The interface forbids rebuilding these
            // facts from a later target, so it is captured here or never.
            var invalidatedTarget =
                state.ReadyForEffects &&
                SessionLifecycleEvent.CarriesRecoveryMetadata(lifecycleEvent.State)
                    ? new GuardianHostJobListTarget(
                        state.Binding.Alias,
                        state.Binding.TransitionVersion,
                        state.WorkerIdentity,
                        state.AuditSession,
                        state.ReadyForEffects)
                    : null;

            state.State = lifecycleEvent.State;
            state.ReadyForEffects = lifecycleEvent.ReadyForEffects;
            state.WarmStateLost |= lifecycleEvent.WarmStateLost;
            state.BootstrapState = lifecycleEvent.BootstrapState;
            // Recovery facts are carried verbatim, and every state that cannot
            // hold them clears them: the wire contract already guarantees the
            // event's metadata is null for exactly those states.
            state.RecoveryPhase = lifecycleEvent.RecoveryPhase;
            state.RecoveryAttempt = lifecycleEvent.RecoveryAttempt ?? 0;
            state.RetryAfterMilliseconds = lifecycleEvent.RetryAfterMilliseconds;
            state.RecoveringWorkerIdentity =
                SessionLifecycleEvent.CarriesRecoveryMetadata(lifecycleEvent.State)
                    ? lifecycleEvent.WorkerIdentity
                    : null;

            if (invalidatedTarget is not null)
            {
                // The projection is taken after the commit, so the evidence is
                // this transition's own recovery snapshot rather than anything
                // observed later. Its constructor re-checks completeness.
                state.Invalidation = new GuardianHostJobListTargetInvalidation(
                    invalidatedTarget,
                    ProjectSession(state));
            }
        }
    }

    private static void ClearRecoveryMetadata(AliasState state)
    {
        state.RecoveryPhase = null;
        state.RecoveryAttempt = 0;
        state.RetryAfterMilliseconds = null;
        state.RecoveringWorkerIdentity = null;
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
            state.AmbiguousUntilRepaired = true;
            ClearRecoveryMetadata(state);
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
            if (state.Binding.BindingKind != RecoveryBindingKind.Default)
            {
                state.DesiredState = result.State == PublicSessionState.Cold
                    ? DesiredSessionState.Cold
                    : DesiredSessionState.Ready;
            }
            state.ReadyForEffects = result.ReadyForEffects;
            state.WarmStateLost |= result.WarmStateLost;
            state.BootstrapState = result.BootstrapState;
            // An authoritative result names a settled state, which is never one
            // that carries recovery metadata; keeping stale facts here would
            // advertise a phase the public snapshot rejects.
            ClearRecoveryMetadata(state);
            // This is the interface's authoritative session-changing result, and
            // therefore the only channel that repairs an ambiguous alias.
            state.AmbiguousUntilRepaired = false;
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
        lock (_sync)
        {
            // Only the evidence captured for this exact dispatch identity is
            // served. Anything else would be recovery metadata synthesized from
            // a later target, which the interface prohibits outright.
            invalidation =
                _aliases.TryGetValue(target.Alias, out var state) &&
                state.Invalidation is { } captured &&
                captured.AppliesTo(target)
                    ? captured
                    : null;
        }
        return invalidation is not null;
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
        internal DesiredSessionState DesiredState { get; set; } =
            DesiredSessionState.Ready;
        internal bool ReadyForEffects { get; set; }
        internal bool WarmStateLost { get; set; }
        internal BootstrapState BootstrapState { get; set; }

        /// <summary>
        /// The alias's last automatic-recovery facts, exactly as its owning host
        /// reported them. They are only ever set from a recovering lifecycle
        /// event and are cleared by every state that cannot carry them, so the
        /// projection never advertises a phase the public snapshot would reject
        /// or an attempt reconstructed from a later transition.
        /// </summary>
        internal RecoveryPhase? RecoveryPhase { get; set; }
        internal long RecoveryAttempt { get; set; }
        internal int? RetryAfterMilliseconds { get; set; }

        /// <summary>
        /// The worker the owning host named in the current recovering lifecycle:
        /// the worker being contained, or the replacement being bootstrapped.
        /// The projection reports this instead of <see cref="WorkerIdentity"/>
        /// while recovery is in flight, because the bound identity is the dead
        /// generation until a ready grant rebinds the alias.
        /// </summary>
        internal GuardianHostWorkerIdentity? RecoveringWorkerIdentity { get; set; }

        /// <summary>
        /// Evidence captured in the exact transition that invalidated a ready
        /// dispatch target, never reconstructed afterwards. It is what lets a
        /// stale dispatch be refused as retryable backend_lost_before_dispatch
        /// carrying real recovery metadata, instead of the blanket nonretryable
        /// recovery_unknown the supervisor falls back to without it.
        /// </summary>
        internal GuardianHostJobListTargetInvalidation? Invalidation { get; set; }

        /// <summary>
        /// True while a session-changing request's outcome is unknown and no
        /// authoritative result has repaired the alias. It is sticky on purpose:
        /// a host restoring its declared session must not clear it, or the
        /// ambiguity is erased and ordinary work can be dispatched into a
        /// session whose outcome nobody knows. Only
        /// <see cref="ObserveSessionOperationResult"/> clears it.
        /// </summary>
        internal bool AmbiguousUntilRepaired { get; set; }
    }
}
