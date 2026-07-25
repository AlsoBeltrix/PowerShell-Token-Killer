using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Ownership;
using PtkMcpGuardian.Standalone;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class FrozenDefaultSessionStateTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly WorkerBootId Worker = new(
        Guid.Parse("22222222-2222-4222-8222-222222222222"));

    [Fact]
    public void Strict_default_has_canonical_binding_configuration_and_ready_snapshot()
    {
        var state = new FrozenDefaultSessionState(
            Guardian,
            Worker,
            new FrozenSessionCatalog([]),
            allowColdBackground: true);

        Assert.Equal(
            "d472a0bb358ea4ab25df3549583885ad5dd0f7009923475814c727b7b870072f",
            state.Binding.BindingDigest.Value);
        Assert.Equal(
            "c2d5c00a1d175536658b9ed55cb34dde2740423732f22f7e2ba664afe2d252b9",
            state.CatalogDigest.Value);
        Assert.Equal(
            "6f527c387b7ebc61c0bcdd86ff67c5873e3e24e1aa0cb1bb6b3fd95a026cc663",
            state.ConfigurationDigest.Value);
        Assert.Equal("default", state.Binding.Alias.Value);
        Assert.Equal(RecoveryBindingKind.Default, state.Binding.BindingKind);
        Assert.True(state.Binding.AllowColdBackground);
        Assert.Equal(DesiredSessionState.Ready, state.Binding.DesiredState);
        Assert.Equal(1, state.Binding.TransitionVersion.Value);

        var snapshot = Assert.Single(state.SnapshotSessions());
        Assert.Equal("default", snapshot.Alias.Value);
        Assert.Equal(PublicSessionState.Ready, snapshot.State);
        Assert.Equal(DesiredSessionState.Ready, snapshot.DesiredState);
        Assert.Equal(Worker, snapshot.WorkerBootId);
        Assert.Equal(1, snapshot.Generation?.Value);
        Assert.Equal(1, snapshot.TransitionVersion.Value);
        Assert.True(snapshot.ReadyForEffects);
        Assert.False(snapshot.WarmStateLost);
        Assert.Equal(BootstrapState.Restored, snapshot.BootstrapState);
        Assert.Null(snapshot.RecoveryPhase);
        Assert.Equal(0, snapshot.RecoveryAttempt);
    }

    [Fact]
    public void Recovered_ready_host_persists_warm_state_loss_for_the_guardian_lifetime()
    {
        var state = State();

        state.ObserveHostReady(Identity(generation: 1), recovered: false);
        Assert.False(Assert.Single(state.SnapshotSessions()).WarmStateLost);

        state.ObserveHostReady(Identity(generation: 2), recovered: true);
        Assert.True(Assert.Single(state.SnapshotSessions()).WarmStateLost);

        state.ObserveHostReady(Identity(generation: 3), recovered: true);
        Assert.True(Assert.Single(state.SnapshotSessions()).WarmStateLost);
    }

    [Fact]
    public void Ambiguous_lifecycle_stays_blocked_until_an_authoritative_repair()
    {
        var state = State();
        var alias = new CanonicalAlias("default");

        state.ObserveSessionRecoveryUnknown(alias);
        var ambiguous = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.RecoveryUnknown, ambiguous.State);
        Assert.False(ambiguous.ReadyForEffects);
        Assert.True(ambiguous.WarmStateLost);
        Assert.Equal(BootstrapState.Unknown, ambiguous.BootstrapState);
        Assert.True(state.TryGetJobListTarget(alias, out var blockedTarget));
        Assert.False(blockedTarget.ReadyForEffects);

        state.ObserveHostReady(Identity(generation: 2), recovered: true);
        var recoveredHost = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.RecoveryUnknown, recoveredHost.State);
        Assert.False(recoveredHost.ReadyForEffects);

        state.ObserveSessionOperationResult(new ResetResult(
            alias,
            PublicSessionState.Ready,
            new GuardianHostWorkerIdentity(Worker, new WorkerGeneration(1)),
            new SessionTransitionVersion(1),
            readyForEffects: true,
            warmStateLost: true,
            BootstrapState.Restored));

        var repaired = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.Ready, repaired.State);
        Assert.True(repaired.ReadyForEffects);
        Assert.True(repaired.WarmStateLost);
        Assert.Equal(BootstrapState.Restored, repaired.BootstrapState);
        Assert.True(state.TryGetJobListTarget(alias, out var repairedTarget));
        Assert.True(repairedTarget.ReadyForEffects);
    }

    [Fact]
    public void Ready_lifecycle_cannot_repair_an_ambiguous_alias()
    {
        // The sibling test above covers ObserveHostReady. This covers the
        // channel that actually carries a replacement host's restored session —
        // a Ready SessionLifecycleEvent — which is how the ambiguous outcome was
        // being erased in production (r6x-2). A host restoring its declared
        // session is not an authoritative repair.
        var state = State();
        var alias = state.Binding.Alias;
        state.ObserveSessionRecoveryUnknown(alias);

        var grant = state.GrantWorkerCreateCapability(
            CreateRequest(state, deadlineUnixTimeMilliseconds: 500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        var boot = new WorkerBootId(
            Guid.Parse("66666666-6666-4666-8666-666666666666"));
        state.ObserveSessionLifecycle(new SessionLifecycleEvent(
            Guardian,
            Identity(2).HostBootId,
            new HostGeneration(2),
            new HostEventSequence(2),
            requestId: null,
            alias,
            state.Binding.TransitionVersion,
            new GuardianHostWorkerIdentity(boot, grant.WorkerGeneration),
            PublicSessionState.Starting,
            PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.AutomaticRecovery,
            readyForEffects: true,
            warmStateLost: true,
            BootstrapState.Restored));

        var stillAmbiguous = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.RecoveryUnknown, stillAmbiguous.State);
        Assert.False(stillAmbiguous.ReadyForEffects);
        Assert.True(stillAmbiguous.WarmStateLost);
        Assert.Equal(BootstrapState.Unknown, stillAmbiguous.BootstrapState);
        Assert.True(state.TryGetJobListTarget(alias, out var blockedTarget));
        Assert.False(blockedTarget.ReadyForEffects);

        // The new worker identity is still absorbed, so the authoritative repair
        // that follows addresses the live worker rather than a dead generation.
        Assert.Equal(boot, blockedTarget.WorkerIdentity.BootId);
        Assert.Equal(grant.WorkerGeneration, blockedTarget.WorkerIdentity.Generation);

        state.ObserveSessionOperationResult(new ResetResult(
            alias,
            PublicSessionState.Ready,
            new GuardianHostWorkerIdentity(boot, grant.WorkerGeneration),
            new SessionTransitionVersion(1),
            readyForEffects: true,
            warmStateLost: true,
            BootstrapState.Restored));

        var repaired = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.Ready, repaired.State);
        Assert.True(repaired.ReadyForEffects);
        Assert.Equal(BootstrapState.Restored, repaired.BootstrapState);
    }

    [Fact]
    public void Recovering_lifecycle_projects_the_hosts_exact_recovery_facts()
    {
        // Before this, the guardian hardcoded recoveryPhase/attempt/retryAfter to
        // null/0/null, so an alias whose worker died under a healthy host still
        // projected its last ready lifecycle. The facts now cross the wire and
        // are reported verbatim — never reconstructed from a later transition.
        var state = State();
        var alias = state.Binding.Alias;
        var dying = new GuardianHostWorkerIdentity(Worker, new WorkerGeneration(1));

        state.ObserveSessionLifecycle(RecoveringLifecycle(
            state, dying, PublicSessionState.Ready, PublicSessionState.Recovering,
            GuardianHostSessionLifecycleReason.WorkerExit,
            RecoveryPhase.Containment, attempt: 1, retryAfter: 250, sequence: 2));

        var contained = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.Recovering, contained.State);
        Assert.False(contained.ReadyForEffects);
        Assert.True(contained.WarmStateLost);
        Assert.Equal(RecoveryPhase.Containment, contained.RecoveryPhase);
        Assert.Equal(1, contained.RecoveryAttempt);
        Assert.Equal(250, contained.RetryAfterMilliseconds);
        Assert.Equal(BootstrapState.Pending, contained.BootstrapState);
        // The alias reports the worker being contained, not a live one.
        Assert.Equal(Worker, contained.WorkerBootId);
        // Dispatch stays blocked for the whole recovery.
        Assert.True(state.TryGetJobListTarget(alias, out var recoveringTarget));
        Assert.False(recoveringTarget.ReadyForEffects);

        var grant = state.GrantWorkerCreateCapability(
            CreateRequest(state, deadlineUnixTimeMilliseconds: 500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        var replacement = new WorkerBootId(
            Guid.Parse("77777777-7777-4777-8777-777777777777"));
        var replacementIdentity = new GuardianHostWorkerIdentity(
            replacement, grant.WorkerGeneration);

        state.ObserveSessionLifecycle(RecoveringLifecycle(
            state, replacementIdentity, PublicSessionState.Recovering,
            PublicSessionState.Bootstrapping,
            GuardianHostSessionLifecycleReason.AutomaticRecovery,
            RecoveryPhase.Bootstrap, attempt: 2, retryAfter: 1_000, sequence: 3));

        var bootstrapping = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.Bootstrapping, bootstrapping.State);
        Assert.Equal(RecoveryPhase.Bootstrap, bootstrapping.RecoveryPhase);
        Assert.Equal(2, bootstrapping.RecoveryAttempt);
        Assert.Equal(1_000, bootstrapping.RetryAfterMilliseconds);
        // Bootstrapping names the replacement being started, not the dead
        // generation the alias is still bound to.
        Assert.Equal(replacement, bootstrapping.WorkerBootId);
        Assert.Equal(grant.WorkerGeneration, bootstrapping.Generation);

        state.ObserveSessionLifecycle(new SessionLifecycleEvent(
            Guardian,
            Identity(2).HostBootId,
            new HostGeneration(2),
            new HostEventSequence(4),
            requestId: null,
            alias,
            state.Binding.TransitionVersion,
            replacementIdentity,
            PublicSessionState.Bootstrapping,
            PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.AutomaticRecovery,
            readyForEffects: true,
            warmStateLost: true,
            BootstrapState.Restored));

        // Reaching ready clears every recovery fact; a settled state that still
        // advertised a phase is one the public snapshot would reject outright.
        var recovered = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.Ready, recovered.State);
        Assert.True(recovered.ReadyForEffects);
        Assert.Null(recovered.RecoveryPhase);
        Assert.Equal(0, recovered.RecoveryAttempt);
        Assert.Null(recovered.RetryAfterMilliseconds);
        Assert.Equal(replacement, recovered.WorkerBootId);
    }

    [Fact]
    public void Recovering_lifecycle_cannot_downgrade_an_ambiguous_alias()
    {
        // recovery_unknown is nonretryable and demands an explicit repair;
        // session_recovering is retryable. Letting an automatic recovery
        // overwrite the ambiguity would invite the model to resubmit work whose
        // first outcome nobody knows — the same no-replay boundary the Ready
        // interception holds (r6x-2 #1), reached from the other direction.
        var state = State();
        var alias = state.Binding.Alias;
        state.ObserveSessionRecoveryUnknown(alias);

        state.ObserveSessionLifecycle(RecoveringLifecycle(
            state,
            new GuardianHostWorkerIdentity(Worker, new WorkerGeneration(1)),
            PublicSessionState.Ready,
            PublicSessionState.Recovering,
            GuardianHostSessionLifecycleReason.WorkerExit,
            RecoveryPhase.Containment, attempt: 1, retryAfter: 250, sequence: 2));

        var stillAmbiguous = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.RecoveryUnknown, stillAmbiguous.State);
        Assert.False(stillAmbiguous.ReadyForEffects);
        Assert.Equal(BootstrapState.Unknown, stillAmbiguous.BootstrapState);
        // No recovery metadata leaks onto a state that cannot carry it.
        Assert.Null(stillAmbiguous.RecoveryPhase);
        Assert.Equal(0, stillAmbiguous.RecoveryAttempt);
        Assert.Null(stillAmbiguous.RetryAfterMilliseconds);
    }

    [Fact]
    public void Recovering_lifecycle_naming_an_unknown_worker_is_refused()
    {
        var state = State();
        var stranger = new GuardianHostWorkerIdentity(
            new WorkerBootId(Guid.Parse("88888888-8888-4888-8888-888888888888")),
            new WorkerGeneration(9));

        Assert.Throws<InvalidOperationException>(() =>
            state.ObserveSessionLifecycle(RecoveringLifecycle(
                state, stranger, PublicSessionState.Ready,
                PublicSessionState.Recovering,
                GuardianHostSessionLifecycleReason.WorkerExit,
                RecoveryPhase.Containment, attempt: 1, retryAfter: 250, sequence: 2)));

        // The refusal leaves the alias exactly as it was.
        var unchanged = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.Ready, unchanged.State);
        Assert.Null(unchanged.RecoveryPhase);
    }

    [Fact]
    public void Invalidated_dispatch_target_carries_its_own_transitions_recovery_evidence()
    {
        // Without this the supervisor could only answer a stale dispatch with a
        // blanket nonretryable session_recovery_unknown, because
        // TryGetJobListTargetInvalidation returned false unconditionally. The
        // honest answer for a worker that died before any write is a retryable
        // backend_lost_before_dispatch carrying the live recovery metadata.
        var state = State();
        var alias = state.Binding.Alias;
        Assert.True(state.TryGetJobListTarget(alias, out var readyTarget));
        Assert.True(readyTarget.ReadyForEffects);
        Assert.False(state.TryGetJobListTargetInvalidation(readyTarget, out _));

        state.ObserveSessionLifecycle(RecoveringLifecycle(
            state,
            new GuardianHostWorkerIdentity(Worker, new WorkerGeneration(1)),
            PublicSessionState.Ready,
            PublicSessionState.Recovering,
            GuardianHostSessionLifecycleReason.WorkerExit,
            RecoveryPhase.Containment, attempt: 1, retryAfter: 250, sequence: 2));

        Assert.True(state.TryGetJobListTargetInvalidation(readyTarget, out var invalidation));
        Assert.True(invalidation.AppliesTo(readyTarget));
        var evidence = invalidation.RecoverySnapshot;
        Assert.Equal(alias, evidence.Alias);
        Assert.False(evidence.ReadyForEffects);
        Assert.Equal(RecoveryPhase.Containment, evidence.RecoveryPhase);
        Assert.Equal(1, evidence.RecoveryAttempt);
        Assert.Equal(250, evidence.RetryAfterMilliseconds);

        // The now-current, non-ready target is a different dispatch identity and
        // must not be served the invalidated target's evidence.
        Assert.True(state.TryGetJobListTarget(alias, out var recoveringTarget));
        Assert.False(state.TryGetJobListTargetInvalidation(recoveringTarget, out _));
    }

    [Fact]
    public void Ambiguous_alias_yields_no_invalidation_evidence()
    {
        // An ambiguous outcome is not a clean pre-write loss: the request may
        // already have taken effect. Serving retryable evidence here would tell
        // the model to resubmit exactly the work the no-replay boundary
        // protects, so the supervisor must fall through to recovery_unknown.
        var state = State();
        var alias = state.Binding.Alias;
        Assert.True(state.TryGetJobListTarget(alias, out var readyTarget));
        state.ObserveSessionRecoveryUnknown(alias);

        state.ObserveSessionLifecycle(RecoveringLifecycle(
            state,
            new GuardianHostWorkerIdentity(Worker, new WorkerGeneration(1)),
            PublicSessionState.Ready,
            PublicSessionState.Recovering,
            GuardianHostSessionLifecycleReason.WorkerExit,
            RecoveryPhase.Containment, attempt: 1, retryAfter: 250, sequence: 2));

        Assert.False(state.TryGetJobListTargetInvalidation(readyTarget, out _));
    }

    [Fact]
    public void Worker_create_during_recovery_clears_the_recovery_facts_it_cannot_carry()
    {
        // A replacement worker is created while the alias is still projecting
        // Recovering. That moves it to Starting, which the public contract
        // forbids from carrying a recovery phase, so leaving the previous
        // recovering event's facts attached made every later snapshot throw -
        // and ptk_state/ptk_session list are pure snapshot reads, so they
        // failed outright for the whole recovery window (r6acc-1).
        var state = State();
        var scratch = new CanonicalAlias("scratch");
        var scratchBinding = state.DeclareDynamicAlias(
            scratch,
            allowColdBackground: true);
        var grant = state.GrantWorkerCreateCapability(
            new WorkerCreateCapabilityRequestedEvent(
                Guardian,
                Identity(1).HostBootId,
                new HostGeneration(1),
                new HostEventSequence(1),
                scratch,
                scratchBinding.TransitionVersion,
                scratchBinding.BindingDigest,
                500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        var boot = new WorkerBootId(
            Guid.Parse("99999999-9999-4999-8999-999999999999"));
        var worker = new GuardianHostWorkerIdentity(boot, grant.WorkerGeneration);
        state.ObserveSessionLifecycle(new SessionLifecycleEvent(
            Guardian,
            Identity(2).HostBootId,
            new HostGeneration(2),
            new HostEventSequence(2),
            requestId: null,
            scratch,
            state.GetDeclaredBinding(scratch)!.TransitionVersion,
            worker,
            PublicSessionState.Starting,
            PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.AutomaticRecovery,
            readyForEffects: true,
            warmStateLost: false,
            BootstrapState.Restored));
        state.ObserveSessionLifecycle(new SessionLifecycleEvent(
            Guardian,
            Identity(2).HostBootId,
            new HostGeneration(2),
            new HostEventSequence(3),
            requestId: null,
            scratch,
            state.GetDeclaredBinding(scratch)!.TransitionVersion,
            worker,
            PublicSessionState.Ready,
            PublicSessionState.Recovering,
            GuardianHostSessionLifecycleReason.ExecutionTimeout,
            readyForEffects: false,
            warmStateLost: true,
            BootstrapState.Pending,
            RecoveryPhase.Containment,
            1,
            250));

        var binding = state.GetDeclaredBinding(scratch)!;
        _ = state.GrantWorkerCreateCapability(
            new WorkerCreateCapabilityRequestedEvent(
                Guardian,
                Identity(2).HostBootId,
                new HostGeneration(2),
                new HostEventSequence(4),
                scratch,
                binding.TransitionVersion,
                binding.BindingDigest,
                500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);

        var starting = Assert.Single(state.SnapshotSessions()
            .Where(value => value.Alias.Value == "scratch"));
        Assert.Equal(PublicSessionState.Starting, starting.State);
        Assert.Null(starting.RecoveryPhase);
        Assert.Equal(0, starting.RecoveryAttempt);
        Assert.Null(starting.RetryAfterMilliseconds);
    }

    [Fact]
    public void Recovering_dynamic_alias_projects_and_encodes_through_the_public_contract()
    {
        // The real apphost recovers a dynamic alias, not the default one, and
        // the projection has to survive the public state codec - a snapshot the
        // guardian can build but not encode would fail ptk_state outright.
        var state = State();
        var scratch = new CanonicalAlias("scratch");
        var scratchBinding = state.DeclareDynamicAlias(scratch, allowColdBackground: true);
        var grant = state.GrantWorkerCreateCapability(
            new WorkerCreateCapabilityRequestedEvent(
                Guardian,
                Identity(1).HostBootId,
                new HostGeneration(1),
                new HostEventSequence(1),
                scratch,
                scratchBinding.TransitionVersion,
                scratchBinding.BindingDigest,
                500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        var boot = new WorkerBootId(
            Guid.Parse("99999999-9999-4999-8999-999999999999"));
        var worker = new GuardianHostWorkerIdentity(boot, grant.WorkerGeneration);
        state.ObserveSessionLifecycle(new SessionLifecycleEvent(
            Guardian,
            Identity(2).HostBootId,
            new HostGeneration(2),
            new HostEventSequence(2),
            requestId: null,
            scratch,
            state.GetDeclaredBinding(scratch)!.TransitionVersion,
            worker,
            PublicSessionState.Starting,
            PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.AutomaticRecovery,
            readyForEffects: true,
            warmStateLost: false,
            BootstrapState.Restored));

        state.ObserveSessionLifecycle(new SessionLifecycleEvent(
            Guardian,
            Identity(2).HostBootId,
            new HostGeneration(2),
            new HostEventSequence(3),
            requestId: null,
            scratch,
            state.GetDeclaredBinding(scratch)!.TransitionVersion,
            worker,
            PublicSessionState.Ready,
            PublicSessionState.Recovering,
            GuardianHostSessionLifecycleReason.ExecutionTimeout,
            readyForEffects: false,
            warmStateLost: true,
            BootstrapState.Pending,
            RecoveryPhase.Containment,
            1,
            250));

        var sessions = state.SnapshotSessions();
        var recovering = sessions.Single(session => session.Alias == scratch);
        Assert.Equal(PublicSessionState.Recovering, recovering.State);
        Assert.Equal(RecoveryPhase.Containment, recovering.RecoveryPhase);

        // The whole snapshot must survive the public codec, exactly as ptk_state
        // encodes it.
        var encoded = PublicStateCodec.Encode(new PublicStateSnapshot(
            Guardian,
            new PublicHostStateSnapshot(
                new HostBootId(Identity(2).HostBootId.Value),
                new HostGeneration(2),
                PublicHostState.Ready,
                recoveryPhase: null,
                recoveryAttempt: 0,
                retryAfterMilliseconds: null,
                readyForEffects: true,
                lastFailureCode: null),
            sessions));
        var decoded = PublicStateCodec.Decode(encoded);
        var roundTripped = decoded.Sessions.Single(
            session => session.Alias == scratch);
        Assert.Equal(PublicSessionState.Recovering, roundTripped.State);
        Assert.Equal(RecoveryPhase.Containment, roundTripped.RecoveryPhase);
        Assert.Equal(1, roundTripped.RecoveryAttempt);
        Assert.Equal(250, roundTripped.RetryAfterMilliseconds);
    }

    private SessionLifecycleEvent RecoveringLifecycle(
        FrozenDefaultSessionState state,
        GuardianHostWorkerIdentity worker,
        PublicSessionState previousState,
        PublicSessionState nextState,
        GuardianHostSessionLifecycleReason reason,
        RecoveryPhase phase,
        long attempt,
        int retryAfter,
        long sequence) =>
        new(
            Guardian,
            Identity(2).HostBootId,
            new HostGeneration(2),
            new HostEventSequence(sequence),
            requestId: null,
            state.Binding.Alias,
            state.Binding.TransitionVersion,
            worker,
            previousState,
            nextState,
            reason,
            readyForEffects: false,
            warmStateLost: true,
            BootstrapState.Pending,
            phase,
            attempt,
            retryAfter);

    [Fact]
    public void Repaired_alias_accepts_an_ordinary_ready_lifecycle_again()
    {
        // Stickiness must end at the repair, not outlive it: once an
        // authoritative result has repaired the alias, an ordinary Ready
        // lifecycle has to commit normally or automatic recovery could never
        // bring the alias back.
        var state = State();
        var alias = state.Binding.Alias;
        state.ObserveSessionRecoveryUnknown(alias);
        state.ObserveSessionOperationResult(new ResetResult(
            alias,
            PublicSessionState.Ready,
            new GuardianHostWorkerIdentity(Worker, new WorkerGeneration(1)),
            new SessionTransitionVersion(1),
            readyForEffects: true,
            warmStateLost: true,
            BootstrapState.Restored));

        var grant = state.GrantWorkerCreateCapability(
            CreateRequest(state, deadlineUnixTimeMilliseconds: 500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        var boot = new WorkerBootId(
            Guid.Parse("77777777-7777-4777-8777-777777777777"));
        state.ObserveSessionLifecycle(new SessionLifecycleEvent(
            Guardian,
            Identity(2).HostBootId,
            new HostGeneration(2),
            new HostEventSequence(3),
            requestId: null,
            alias,
            state.Binding.TransitionVersion,
            new GuardianHostWorkerIdentity(boot, grant.WorkerGeneration),
            PublicSessionState.Starting,
            PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.AutomaticRecovery,
            readyForEffects: true,
            warmStateLost: true,
            BootstrapState.Restored));

        var ready = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.Ready, ready.State);
        Assert.True(ready.ReadyForEffects);
        Assert.Equal(BootstrapState.Restored, ready.BootstrapState);
        Assert.Equal(boot, ready.WorkerBootId);
    }

    [Fact]
    public void Manifest_changes_only_generation_envelope_across_host_attempts()
    {
        var state = State();

        var first = state.Create(Identity(generation: 1));
        var second = state.Create(Identity(generation: 2));

        Assert.Equal(Guardian, first.GuardianBootId);
        Assert.Equal(1, first.HostGeneration.Value);
        Assert.Equal(2, second.HostGeneration.Value);
        Assert.Equal(first.CatalogDigest, second.CatalogDigest);
        Assert.Equal(first.ConfigurationDigest, second.ConfigurationDigest);
        Assert.Same(first.Bindings[0], second.Bindings[0]);
        Assert.Equal(state.Binding, first.Bindings[0]);
        Assert.Empty(first.Templates);
        var watermark = Assert.Single(first.WorkerGenerationHighWatermarks);
        Assert.Equal("default", watermark.Alias.Value);
        Assert.Equal(1, watermark.Generation.Value);
        Assert.Equal(first.HostGeneration, first.HostGenerationHighWatermark);
        Assert.Equal(second.HostGeneration, second.HostGenerationHighWatermark);
    }

    [Fact]
    public void Exact_worker_create_grants_irreversibly_advance_the_manifest_high_water()
    {
        var token = Token(7);
        var state = new FrozenDefaultSessionState(
            Guardian,
            Worker,
            new FrozenSessionCatalog([]),
            allowColdBackground: true,
            createCapabilityToken: () => token);

        var first = state.GrantWorkerCreateCapability(
            CreateRequest(state, deadlineUnixTimeMilliseconds: 500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);

        Assert.Equal(2, first.WorkerGeneration.Value);
        Assert.Same(token, first.Token);
        Assert.Equal(
            2,
            Assert.Single(state.Create(Identity(1)).WorkerGenerationHighWatermarks)
                .Generation.Value);

        var second = state.GrantWorkerCreateCapability(
            CreateRequest(state, deadlineUnixTimeMilliseconds: 550),
            nowUnixTimeMilliseconds: 200,
            maximumDeadlineUnixTimeMilliseconds: 600);
        Assert.Equal(3, second.WorkerGeneration.Value);
        Assert.Equal(
            3,
            Assert.Single(state.Create(Identity(2)).WorkerGenerationHighWatermarks)
                .Generation.Value);
    }

    [Fact]
    public void Ready_lifecycle_binds_the_host_selected_boot_to_the_latest_grant()
    {
        var state = State();
        var grant = state.GrantWorkerCreateCapability(
            CreateRequest(state, deadlineUnixTimeMilliseconds: 500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        var boot = new WorkerBootId(
            Guid.Parse("55555555-5555-4555-8555-555555555555"));
        var lifecycle = new SessionLifecycleEvent(
            Guardian,
            Identity(1).HostBootId,
            new HostGeneration(1),
            new HostEventSequence(2),
            requestId: null,
            state.Binding.Alias,
            state.Binding.TransitionVersion,
            new GuardianHostWorkerIdentity(
                boot,
                grant.WorkerGeneration),
            PublicSessionState.Starting,
            PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.AutomaticRecovery,
            readyForEffects: true,
            warmStateLost: false,
            BootstrapState.Restored);

        state.ObserveSessionLifecycle(lifecycle);

        var snapshot = Assert.Single(state.SnapshotSessions());
        Assert.Equal(boot, snapshot.WorkerBootId);
        Assert.Equal(grant.WorkerGeneration, snapshot.Generation);
        Assert.True(snapshot.ReadyForEffects);
        Assert.True(state.TryGetJobListTarget(
            state.Binding.Alias,
            out var target));
        Assert.Equal(boot, target.WorkerIdentity.BootId);
        Assert.Equal(grant.WorkerGeneration, target.WorkerIdentity.Generation);
        Assert.Equal(
            grant.WorkerGeneration.Value,
            target.AuditSession.Session.Generation);
    }

    [Fact]
    public void Stale_ready_lifecycle_cannot_replace_the_latest_grant()
    {
        var state = State();
        var first = state.GrantWorkerCreateCapability(
            CreateRequest(state, deadlineUnixTimeMilliseconds: 500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        var second = state.GrantWorkerCreateCapability(
            CreateRequest(state, deadlineUnixTimeMilliseconds: 550),
            nowUnixTimeMilliseconds: 200,
            maximumDeadlineUnixTimeMilliseconds: 600);
        var stale = new SessionLifecycleEvent(
            Guardian,
            Identity(1).HostBootId,
            new HostGeneration(1),
            new HostEventSequence(2),
            requestId: null,
            state.Binding.Alias,
            state.Binding.TransitionVersion,
            new GuardianHostWorkerIdentity(
                new WorkerBootId(
                    Guid.Parse("55555555-5555-4555-8555-555555555555")),
                first.WorkerGeneration),
            PublicSessionState.Starting,
            PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.AutomaticRecovery,
            readyForEffects: true,
            warmStateLost: false,
            BootstrapState.Restored);

        Assert.Throws<InvalidOperationException>(
            () => state.ObserveSessionLifecycle(stale));

        var snapshot = Assert.Single(state.SnapshotSessions());
        Assert.False(snapshot.ReadyForEffects);
        Assert.Equal(PublicSessionState.Starting, snapshot.State);

        state.ObserveSessionLifecycle(new SessionLifecycleEvent(
            Guardian,
            Identity(1).HostBootId,
            new HostGeneration(1),
            new HostEventSequence(3),
            requestId: null,
            state.Binding.Alias,
            state.Binding.TransitionVersion,
            new GuardianHostWorkerIdentity(
                new WorkerBootId(
                    Guid.Parse("66666666-6666-4666-8666-666666666666")),
                second.WorkerGeneration),
            PublicSessionState.Starting,
            PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.AutomaticRecovery,
            readyForEffects: true,
            warmStateLost: false,
            BootstrapState.Restored));
        snapshot = Assert.Single(state.SnapshotSessions());
        Assert.Equal(second.WorkerGeneration, snapshot.Generation);
        Assert.True(snapshot.ReadyForEffects);
    }

    [Fact]
    public void Refused_worker_create_request_consumes_no_generation_or_token()
    {
        var tokenCalls = 0;
        var state = new FrozenDefaultSessionState(
            Guardian,
            Worker,
            new FrozenSessionCatalog([]),
            allowColdBackground: true,
            createCapabilityToken: () =>
            {
                tokenCalls++;
                return Token(8);
            });
        var exact = CreateRequest(state, deadlineUnixTimeMilliseconds: 500);
        var wrongDigest = new WorkerCreateCapabilityRequestedEvent(
            exact.GuardianBootId,
            exact.HostBootId,
            exact.HostGeneration,
            exact.EventSequence,
            exact.SessionAlias,
            exact.SessionTransitionVersion,
            new Sha256Digest(new string('f', 64)),
            exact.StartupDeadlineUnixTimeMilliseconds);
        var wrongGuardian = new WorkerCreateCapabilityRequestedEvent(
            new GuardianBootId(
                Guid.Parse("99999999-9999-4999-8999-999999999999")),
            exact.HostBootId,
            exact.HostGeneration,
            exact.EventSequence,
            exact.SessionAlias,
            exact.SessionTransitionVersion,
            exact.BindingDigest,
            exact.StartupDeadlineUnixTimeMilliseconds);

        Assert.Throws<InvalidOperationException>(() =>
            state.GrantWorkerCreateCapability(
                wrongDigest,
                nowUnixTimeMilliseconds: 100,
                maximumDeadlineUnixTimeMilliseconds: 600));
        Assert.Throws<InvalidOperationException>(() =>
            state.GrantWorkerCreateCapability(
                wrongGuardian,
                nowUnixTimeMilliseconds: 100,
                maximumDeadlineUnixTimeMilliseconds: 600));
        Assert.Throws<InvalidOperationException>(() =>
            state.GrantWorkerCreateCapability(
                exact,
                nowUnixTimeMilliseconds: 500,
                maximumDeadlineUnixTimeMilliseconds: 600));
        Assert.Throws<InvalidOperationException>(() =>
            state.GrantWorkerCreateCapability(
                exact,
                nowUnixTimeMilliseconds: 100,
                maximumDeadlineUnixTimeMilliseconds: 499));

        Assert.Equal(0, tokenCalls);
        Assert.Equal(
            1,
            Assert.Single(state.Create(Identity(1)).WorkerGenerationHighWatermarks)
                .Generation.Value);

        var granted = state.GrantWorkerCreateCapability(
            exact,
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        Assert.Equal(2, granted.WorkerGeneration.Value);
        Assert.Equal(1, tokenCalls);
    }

    [Fact]
    public void Declared_dynamic_alias_projects_cold_until_its_first_grant_binds_a_worker()
    {
        var state = new FrozenDefaultSessionState(
            Guardian,
            Worker,
            new FrozenSessionCatalog([]),
            allowColdBackground: true,
            workerBootIdSource: static () => new WorkerBootId(
                Guid.Parse("55555555-5555-4555-8555-555555555555")));
        var alias = new CanonicalAlias("scratch");

        var binding = state.DeclareDynamicAlias(alias, allowColdBackground: true);

        Assert.Equal(RecoveryBindingKind.Dynamic, binding.BindingKind);
        Assert.Equal(DesiredSessionState.Ready, binding.DesiredState);
        Assert.Equal(1, binding.TransitionVersion.Value);
        Assert.Equal(
            RecoveryBinding.ComputeBindingDigest(
                alias,
                RecoveryBindingKind.Dynamic,
                allowColdBackground: true,
                DesiredSessionState.Ready,
                binding.TransitionVersion),
            binding.BindingDigest);

        var snapshots = state.SnapshotSessions();
        Assert.Equal(2, snapshots.Count);
        Assert.Equal("default", snapshots[0].Alias.Value);
        var declared = snapshots[1];
        Assert.Equal("scratch", declared.Alias.Value);
        Assert.Equal(PublicSessionState.Cold, declared.State);
        Assert.Equal(DesiredSessionState.Ready, declared.DesiredState);
        Assert.Null(declared.WorkerBootId);
        Assert.Null(declared.Generation);
        Assert.False(declared.ReadyForEffects);
        Assert.True(declared.WarmStateLost);
        Assert.Equal(BootstrapState.Unknown, declared.BootstrapState);

        var manifest = state.Create(Identity(2));
        Assert.Equal(
            ["default", "scratch"],
            manifest.Bindings.Select(value => value.Alias.Value));
        Assert.Equal(
            ["default", "scratch"],
            manifest.WorkerGenerationHighWatermarks.Select(value => value.Alias.Value));
        Assert.All(
            manifest.WorkerGenerationHighWatermarks,
            entry => Assert.Equal(1, entry.Generation.Value));

        Assert.True(state.TryGetJobListTarget(alias, out var target));
        Assert.Equal(1, target.WorkerIdentity.Generation.Value);
        Assert.False(target.ReadyForEffects);

        var granted = state.GrantWorkerCreateCapability(
            new WorkerCreateCapabilityRequestedEvent(
                Guardian,
                Identity(2).HostBootId,
                new HostGeneration(2),
                new HostEventSequence(1),
                alias,
                binding.TransitionVersion,
                binding.BindingDigest,
                500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        Assert.Equal(2, granted.WorkerGeneration.Value);
        var starting = state.SnapshotSessions()[1];
        Assert.Equal(PublicSessionState.Starting, starting.State);
        Assert.Equal(BootstrapState.Pending, starting.BootstrapState);

        var realWorker = new GuardianHostWorkerIdentity(
            new WorkerBootId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
            granted.WorkerGeneration);
        state.ObserveSessionLifecycle(new SessionLifecycleEvent(
            Guardian,
            Identity(2).HostBootId,
            new HostGeneration(2),
            new HostEventSequence(2),
            requestId: null,
            alias,
            binding.TransitionVersion,
            realWorker,
            PublicSessionState.Starting,
            PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.RequestedOpen,
            readyForEffects: true,
            warmStateLost: false,
            BootstrapState.Restored));

        var ready = state.SnapshotSessions()[1];
        Assert.Equal(PublicSessionState.Ready, ready.State);
        Assert.Equal(realWorker.BootId, ready.WorkerBootId);
        Assert.Equal(2, ready.Generation?.Value);
        Assert.True(ready.ReadyForEffects);
        Assert.True(state.TryGetJobListTarget(alias, out var readyTarget));
        Assert.True(readyTarget.ReadyForEffects);
        Assert.Equal(realWorker.BootId, readyTarget.WorkerIdentity.BootId);
        Assert.Equal(2, readyTarget.WorkerIdentity.Generation.Value);
    }

    [Fact]
    public void A_failed_open_marks_only_the_dynamic_desired_state_cold()
    {
        var state = new FrozenDefaultSessionState(
            Guardian,
            Worker,
            new FrozenSessionCatalog([]),
            allowColdBackground: true,
            workerBootIdSource: static () => new WorkerBootId(
                Guid.Parse("55555555-5555-4555-8555-555555555555")));
        var alias = new CanonicalAlias("scratch");
        _ = state.DeclareDynamicAlias(alias, allowColdBackground: true);

        state.MarkDynamicAliasOpenFailed(alias);
        var manifest = state.Create(Identity(2));
        Assert.Equal(2, manifest.Bindings.Count);
        Assert.Equal(DesiredSessionState.Cold, manifest.Bindings[1].DesiredState);
        Assert.Equal(DesiredSessionState.Ready, manifest.Bindings[0].DesiredState);
        Assert.Equal(
            manifest.Bindings[1].BindingDigest,
            state.GetDeclaredBinding(alias)?.BindingDigest);

        state.MarkDynamicAliasOpenFailed(new CanonicalAlias("missing"));
        Assert.Equal(2, state.SnapshotSessions().Count);
        state.MarkDynamicAliasOpenFailed(new CanonicalAlias("default"));
        Assert.Equal(
            DesiredSessionState.Ready,
            state.Create(Identity(2)).Bindings[0].DesiredState);
    }

    [Fact]
    public void A_default_close_result_never_flips_the_default_desired_state()
    {        var state = State();
        var alias = new CanonicalAlias("default");

        state.ObserveSessionOperationResult(new SessionCloseResult(
            alias,
            PublicSessionState.Cold,
            workerIdentity: null,
            new SessionTransitionVersion(1),
            readyForEffects: false,
            warmStateLost: true,
            BootstrapState.NotApplicable));

        var manifest = state.Create(Identity(2));
        Assert.Equal(
            DesiredSessionState.Ready,
            Assert.Single(manifest.Bindings).DesiredState);
    }

    [Fact]
    public void Dynamic_alias_redeclaration_and_default_redeclaration_are_refused()
    {
        var state = State();
        var alias = new CanonicalAlias("scratch");
        _ = state.DeclareDynamicAlias(alias, allowColdBackground: true);

        Assert.Throws<InvalidOperationException>(() =>
            state.DeclareDynamicAlias(alias, allowColdBackground: true));
        Assert.Throws<ArgumentException>(() =>
            state.DeclareDynamicAlias(
                new CanonicalAlias("default"),
                allowColdBackground: true));
    }

    [Fact]
    public void Closed_dynamic_alias_is_declared_cold_in_the_next_manifest_and_reopen_flips_it_back()
    {
        var state = new FrozenDefaultSessionState(
            Guardian,
            Worker,
            new FrozenSessionCatalog([]),
            allowColdBackground: true,
            workerBootIdSource: static () => new WorkerBootId(
                Guid.Parse("55555555-5555-4555-8555-555555555555")));
        var alias = new CanonicalAlias("scratch");
        var binding = state.DeclareDynamicAlias(alias, allowColdBackground: true);
        var worker2 = new GuardianHostWorkerIdentity(
            new WorkerBootId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
            new WorkerGeneration(2));
        SessionLifecycleEvent ReadyLifecycle(
            GuardianHostWorkerIdentity worker,
            long sequence) => new(
            Guardian,
            Identity(2).HostBootId,
            new HostGeneration(2),
            new HostEventSequence(sequence),
            requestId: null,
            alias,
            binding.TransitionVersion,
            worker,
            PublicSessionState.Starting,
            PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.RequestedOpen,
            readyForEffects: true,
            warmStateLost: false,
            BootstrapState.Restored);
        WorkerCreateCapabilityRequestedEvent CreateRequest() => new(
            Guardian,
            Identity(2).HostBootId,
            new HostGeneration(2),
            new HostEventSequence(1),
            alias,
            binding.TransitionVersion,
            binding.BindingDigest,
            500);

        var granted = state.GrantWorkerCreateCapability(
            CreateRequest(),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        state.ObserveSessionLifecycle(ReadyLifecycle(worker2, 2));
        state.ObserveSessionOperationResult(new SessionOpenResult(
            alias,
            PublicSessionState.Ready,
            worker2,
            binding.TransitionVersion,
            readyForEffects: true,
            warmStateLost: false,
            BootstrapState.Restored));
        Assert.Equal(2, granted.WorkerGeneration.Value);

        var opened = state.Create(Identity(2));
        Assert.Equal(DesiredSessionState.Ready, opened.Bindings[1].DesiredState);
        Assert.Equal(binding.BindingDigest, opened.Bindings[1].BindingDigest);

        state.ObserveSessionOperationResult(new SessionCloseResult(
            alias,
            PublicSessionState.Cold,
            workerIdentity: null,
            binding.TransitionVersion,
            readyForEffects: false,
            warmStateLost: true,
            BootstrapState.NotApplicable));
        var closed = state.Create(Identity(2));
        Assert.Equal(DesiredSessionState.Cold, closed.Bindings[1].DesiredState);
        Assert.Equal(binding.BindingDigest, closed.Bindings[1].BindingDigest);
        Assert.Equal(DesiredSessionState.Ready, closed.Bindings[0].DesiredState);
        Assert.Equal(
            DesiredSessionState.Cold,
            state.SnapshotSessions()[1].DesiredState);

        var grantedReopen = state.GrantWorkerCreateCapability(
            CreateRequest(),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        Assert.Equal(3, grantedReopen.WorkerGeneration.Value);
        var worker3 = new GuardianHostWorkerIdentity(
            worker2.BootId,
            new WorkerGeneration(3));
        state.ObserveSessionLifecycle(ReadyLifecycle(worker3, 3));
        state.ObserveSessionOperationResult(new SessionOpenResult(
            alias,
            PublicSessionState.Ready,
            worker3,
            binding.TransitionVersion,
            readyForEffects: true,
            warmStateLost: false,
            BootstrapState.Restored));
        var reopened = state.Create(Identity(2));
        Assert.Equal(DesiredSessionState.Ready, reopened.Bindings[1].DesiredState);
        Assert.Equal(binding.BindingDigest, reopened.Bindings[1].BindingDigest);
    }

    [Fact]
    public void A_pending_grant_is_superseded_by_the_next_grant_across_host_loss()
    {
        var state = State();
        var request = CreateRequest(state, deadlineUnixTimeMilliseconds: 500);

        var first = state.GrantWorkerCreateCapability(
            request,
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        Assert.Equal(2, first.WorkerGeneration.Value);

        state.ObserveHostReady(Identity(2), recovered: true);

        var second = state.GrantWorkerCreateCapability(
            request,
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        Assert.Equal(3, second.WorkerGeneration.Value);
        var snapshot = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.Starting, snapshot.State);
        Assert.True(snapshot.WarmStateLost);

        // Only the latest pending grant can bind; the superseded one is dead.
        SessionLifecycleEvent Lifecycle(long sequence, long generation) => new(
            Guardian,
            Identity(2).HostBootId,
            new HostGeneration(2),
            new HostEventSequence(sequence),
            requestId: null,
            new CanonicalAlias("default"),
            new SessionTransitionVersion(1),
            new GuardianHostWorkerIdentity(Worker, new WorkerGeneration(generation)),
            PublicSessionState.Starting,
            PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.AutomaticRecovery,
            readyForEffects: true,
            warmStateLost: false,
            BootstrapState.Restored);
        Assert.Throws<InvalidOperationException>(() =>
            state.ObserveSessionLifecycle(Lifecycle(2, first.WorkerGeneration.Value)));
        state.ObserveSessionLifecycle(Lifecycle(3, second.WorkerGeneration.Value));
        Assert.Equal(
            PublicSessionState.Ready,
            Assert.Single(state.SnapshotSessions()).State);
    }

    [Fact]
    public void Faulted_lifecycle_clears_the_pending_grant_and_projects_the_fault()
    {
        var state = State();
        var granted = state.GrantWorkerCreateCapability(
            CreateRequest(state, deadlineUnixTimeMilliseconds: 500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        Assert.Equal(2, granted.WorkerGeneration.Value);

        state.ObserveSessionLifecycle(new SessionLifecycleEvent(
            Guardian,
            Identity(1).HostBootId,
            new HostGeneration(1),
            new HostEventSequence(2),
            requestId: null,
            new CanonicalAlias("default"),
            new SessionTransitionVersion(1),
            new GuardianHostWorkerIdentity(Worker, new WorkerGeneration(1)),
            PublicSessionState.Resetting,
            PublicSessionState.Faulted,
            GuardianHostSessionLifecycleReason.ContainmentUnconfirmed,
            readyForEffects: false,
            warmStateLost: true,
            BootstrapState.Failed));

        var snapshot = Assert.Single(state.SnapshotSessions());
        Assert.Equal(PublicSessionState.Faulted, snapshot.State);
        Assert.False(snapshot.ReadyForEffects);
        Assert.True(snapshot.WarmStateLost);
        Assert.Equal(BootstrapState.Failed, snapshot.BootstrapState);

        var second = state.GrantWorkerCreateCapability(
            CreateRequest(state, deadlineUnixTimeMilliseconds: 500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        Assert.Equal(3, second.WorkerGeneration.Value);

        var wrongReason = new SessionLifecycleEvent(
            Guardian,
            Identity(1).HostBootId,
            new HostGeneration(1),
            new HostEventSequence(3),
            requestId: null,
            new CanonicalAlias("default"),
            new SessionTransitionVersion(1),
            new GuardianHostWorkerIdentity(Worker, new WorkerGeneration(1)),
            PublicSessionState.Resetting,
            PublicSessionState.Faulted,
            GuardianHostSessionLifecycleReason.RequestedReset,
            readyForEffects: false,
            warmStateLost: true,
            BootstrapState.Failed);
        Assert.Throws<InvalidOperationException>(() =>
            state.ObserveSessionLifecycle(wrongReason));
    }

    [Fact]
    public void Second_ready_lifecycle_for_one_grant_is_rejected()
    {        var state = State();
        var granted = state.GrantWorkerCreateCapability(
            CreateRequest(state, deadlineUnixTimeMilliseconds: 500),
            nowUnixTimeMilliseconds: 100,
            maximumDeadlineUnixTimeMilliseconds: 600);
        SessionLifecycleEvent Lifecycle(long sequence) => new(
            Guardian,
            Identity(1).HostBootId,
            new HostGeneration(1),
            new HostEventSequence(sequence),
            requestId: null,
            new CanonicalAlias("default"),
            new SessionTransitionVersion(1),
            new GuardianHostWorkerIdentity(Worker, granted.WorkerGeneration),
            PublicSessionState.Starting,
            PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.AutomaticRecovery,
            readyForEffects: true,
            warmStateLost: false,
            BootstrapState.Restored);

        state.ObserveSessionLifecycle(Lifecycle(2));
        Assert.Equal(
            PublicSessionState.Ready,
            Assert.Single(state.SnapshotSessions()).State);
        Assert.Throws<InvalidOperationException>(() =>
            state.ObserveSessionLifecycle(Lifecycle(3)));
    }

    [Fact]
    public void Dispatch_target_is_exact_and_foreign_guardian_manifest_is_rejected()
    {
        var state = State();
        var alias = new CanonicalAlias("default");

        Assert.True(state.TryGetJobListTarget(alias, out var target));
        Assert.NotNull(target);
        Assert.Equal(Worker, target.WorkerIdentity.BootId);
        Assert.Equal(1, target.WorkerIdentity.Generation.Value);
        Assert.True(target.ReadyForEffects);
        Assert.False(state.TryGetJobListTarget(
            new CanonicalAlias("missing"),
            out _));
        Assert.False(state.TryGetJobListTargetInvalidation(target, out _));

        var foreign = new GuardianHostIdentity(
            new GuardianBootId(Guid.Parse("33333333-3333-4333-8333-333333333333")),
            Identity(1).HostBootId,
            new HostGeneration(1));
        Assert.Throws<InvalidOperationException>(() => state.Create(foreign));
    }

    [Fact]
    public void Cold_background_choice_changes_both_binding_and_configuration_digests()
    {
        var enabled = State(allowColdBackground: true);
        var disabled = State(allowColdBackground: false);

        Assert.NotEqual(enabled.Binding.BindingDigest, disabled.Binding.BindingDigest);
        Assert.NotEqual(enabled.ConfigurationDigest, disabled.ConfigurationDigest);
        Assert.Equal(enabled.CatalogDigest, disabled.CatalogDigest);
    }

    private static FrozenDefaultSessionState State(bool allowColdBackground = true) => new(
        Guardian,
        Worker,
        new FrozenSessionCatalog([]),
        allowColdBackground);

    private static WorkerCreateCapabilityRequestedEvent CreateRequest(
        FrozenDefaultSessionState state,
        long deadlineUnixTimeMilliseconds) => new(
            Guardian,
            Identity(1).HostBootId,
            new HostGeneration(1),
            new HostEventSequence(1),
            state.Binding.Alias,
            state.Binding.TransitionVersion,
            state.Binding.BindingDigest,
            deadlineUnixTimeMilliseconds);

    private static CapabilityToken Token(byte marker)
    {
        Span<byte> bytes = stackalloc byte[ContractLimits.CapabilityTokenBytes];
        bytes.Fill(marker);
        return new CapabilityToken(Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));
    }

    private static GuardianHostIdentity Identity(long generation) => new(
        Guardian,
        new HostBootId(Guid.Parse("44444444-4444-4444-8444-444444444444")),
        new HostGeneration(generation));
}
