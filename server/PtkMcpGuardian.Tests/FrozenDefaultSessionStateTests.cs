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
