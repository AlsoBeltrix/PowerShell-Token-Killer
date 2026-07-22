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
    IGuardianHostSupervisorSessionSource
{
    private static ReadOnlySpan<byte> ConfigurationDigestDomain =>
        "ptk.guardian-configuration/1\0"u8;

    private readonly GuardianBootId _guardianBootId;
    private readonly FrozenSessionCatalog _catalog;
    private readonly GuardianHostWorkerIdentity _workerIdentity;
    private readonly GuardianHostJobListTarget _target;
    private readonly WorkerGenerationHighWatermarkEntry _workerHighWatermark;
    private int _warmStateLost;

    internal FrozenDefaultSessionState(
        GuardianBootId guardianBootId,
        WorkerBootId workerBootId,
        FrozenSessionCatalog catalog,
        bool allowColdBackground)
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
        _target = new GuardianHostJobListTarget(
            alias,
            transition,
            _workerIdentity,
            new GuardianAuditSession(Binding, workerGeneration),
            readyForEffects: true);
    }

    internal RecoveryBinding Binding { get; }

    internal Sha256Digest CatalogDigest { get; }

    internal Sha256Digest ConfigurationDigest { get; }

    public RecoveryManifest Create(GuardianHostIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.GuardianBootId != _guardianBootId)
            throw new InvalidOperationException("The host identity belongs to another guardian boot.");

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

    public IReadOnlyList<PublicSessionStateSnapshot> SnapshotSessions() =>
    [
        new PublicSessionStateSnapshot(
            Binding.Alias,
            Binding.DesiredState,
            PublicSessionState.Ready,
            _workerIdentity.BootId,
            _workerIdentity.Generation,
            Binding.TransitionVersion,
            recoveryPhase: null,
            recoveryAttempt: 0,
            retryAfterMilliseconds: null,
            readyForEffects: true,
            lastFailureCode: null,
            warmStateLost: Volatile.Read(ref _warmStateLost) != 0,
            bootstrapState: BootstrapState.Restored),
    ];

    public void ObserveHostReady(GuardianHostIdentity identity, bool recovered)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.GuardianBootId != _guardianBootId)
            throw new InvalidOperationException("The ready host belongs to another guardian boot.");
        if (recovered)
            Interlocked.Exchange(ref _warmStateLost, 1);
    }

    public bool TryGetJobListTarget(
        CanonicalAlias alias,
        [NotNullWhen(true)] out GuardianHostJobListTarget? target)
    {
        ArgumentNullException.ThrowIfNull(alias);
        target = alias == Binding.Alias ? _target : null;
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
}
