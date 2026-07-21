using PtkMcpGuardian.Ownership;
using PtkMcpServer.Sessions;
using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

internal delegate IPrivateSessionOperations PrivateSessionRuntimeFactory(
    TimeSpan callTimeout,
    TimeSpan maxCallTimeout,
    JobPwshExecutable jobPwshExecutable,
    IPublicJobIdAllocator publicJobIdAllocator,
    CancellationToken cancellationToken,
    bool allowColdBackground);

/// <summary>
/// Freezes process-local runtime inputs once for the private host and creates
/// only the exact ready default binding authenticated by PrivateHostServer.
/// Public job identifiers remain guardian-reserved; the compatibility
/// allocator supplied to JobManager always fails if an unreserved path leaks
/// into this process.
/// </summary>
internal sealed class DefaultPrivateHostSessionFactory : IPrivateHostSessionFactory
{
    private readonly TimeSpan _callTimeout;
    private readonly TimeSpan _maxCallTimeout;
    private readonly JobPwshExecutable _jobPwshExecutable;
    private readonly PrivateSessionRuntimeFactory _runtimeFactory;

    internal DefaultPrivateHostSessionFactory()
        : this(
            DefaultSessionRuntimeFactory.ReadCallTimeout(),
            DefaultSessionRuntimeFactory.ReadMaxCallTimeout(),
            JobPwshExecutable.ResolveFromPath())
    {
    }

    internal DefaultPrivateHostSessionFactory(
        TimeSpan callTimeout,
        TimeSpan maxCallTimeout,
        JobPwshExecutable jobPwshExecutable,
        PrivateSessionRuntimeFactory? runtimeFactory = null)
    {
        if (callTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(callTimeout));
        if (maxCallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxCallTimeout));

        _callTimeout = callTimeout;
        _maxCallTimeout = maxCallTimeout;
        _jobPwshExecutable = jobPwshExecutable;
        _runtimeFactory = runtimeFactory ?? DefaultSessionRuntimeFactory.Create;
    }

    public ValueTask<IPrivateSessionOperations> CreateAsync(
        PrivateHostInitialization initialization,
        RecoveryBinding defaultBinding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialization);
        ArgumentNullException.ThrowIfNull(defaultBinding);
        cancellationToken.ThrowIfCancellationRequested();

        var manifest = initialization.Manifest ??
            throw new InvalidDataException("Private host initialization has no recovery manifest.");
        if (manifest.Bindings.Count != 1 ||
            !ReferenceEquals(manifest.Bindings[0], defaultBinding) ||
            defaultBinding.Alias.Value != "default" ||
            defaultBinding.BindingKind != RecoveryBindingKind.Default ||
            defaultBinding.DesiredState != DesiredSessionState.Ready ||
            defaultBinding.TransitionVersion.Value <= 0)
        {
            throw new InvalidDataException(
                "The private default-session factory requires the exact ready manifest binding.");
        }

        var runtime = _runtimeFactory(
            _callTimeout,
            _maxCallTimeout,
            _jobPwshExecutable,
            RejectingPublicJobIdAllocator.Instance,
            cancellationToken,
            defaultBinding.AllowColdBackground) ??
            throw new InvalidOperationException(
                "Private default-session runtime factory returned no session.");
        return ValueTask.FromResult(runtime);
    }

    private sealed class RejectingPublicJobIdAllocator : IPublicJobIdAllocator
    {
        internal static RejectingPublicJobIdAllocator Instance { get; } = new();

        private RejectingPublicJobIdAllocator()
        {
        }

        public PublicJobId Allocate() =>
            throw new InvalidOperationException(
                "Private hosts cannot allocate public job identifiers; the guardian must reserve them.");
    }
}
