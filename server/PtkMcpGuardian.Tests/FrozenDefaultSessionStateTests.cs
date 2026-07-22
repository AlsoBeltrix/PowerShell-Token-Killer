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

    private static GuardianHostIdentity Identity(long generation) => new(
        Guardian,
        new HostBootId(Guid.Parse("44444444-4444-4444-8444-444444444444")),
        new HostGeneration(generation));
}
