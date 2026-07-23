using System.Collections;
using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

internal interface IPrivateHostWorkerLaunchAuthority
{
    Task<IWorkerProcessClient> LaunchAsync(
        RecoveryBinding binding,
        GuardianHostWorkerIdentity workerIdentity,
        DateTimeOffset deadlineUtc,
        Func<WorkerEnvelope, CancellationToken, ValueTask>? onEvent,
        CancellationToken cancellationToken);
}

internal sealed class PrivateHostWorkerSlot : IAsyncDisposable
{
    private IWorkerProcessClient? _process;

    internal PrivateHostWorkerSlot(
        RecoveryBinding binding,
        GuardianHostWorkerIdentity identity,
        IWorkerProcessClient process)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        if (process.WorkerBootId != identity.BootId.Value ||
            process.Generation != identity.Generation.Value)
        {
            throw new ArgumentException(
                "Worker process identity does not match its slot.",
                nameof(process));
        }
    }

    internal RecoveryBinding Binding { get; }

    internal GuardianHostWorkerIdentity Identity { get; }

    internal IWorkerProcessClient Process =>
        Volatile.Read(ref _process) ??
        throw new ObjectDisposedException(nameof(PrivateHostWorkerSlot));

    public async ValueTask DisposeAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is not null)
            await process.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Converts one exact guardian grant into one contained worker slot. The grant
/// is burned immediately before entering the OS launch authority; any launch
/// or identity-validation failure leaves its generation permanently consumed.
/// </summary>
internal sealed class PrivateHostWorkerSlotFactory
{
    private static readonly TimeSpan DefaultStartupTimeout =
        TimeSpan.FromSeconds(30);

    private readonly IPrivateHostWorkerCreateCapabilitySource _capabilities;
    private readonly IPrivateHostWorkerLaunchAuthority _launch;
    private readonly Func<WorkerBootId> _workerBootId;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _startupTimeout;

    internal PrivateHostWorkerSlotFactory(
        IPrivateHostWorkerCreateCapabilitySource capabilities,
        IPrivateHostWorkerLaunchAuthority launch,
        Func<WorkerBootId>? workerBootId = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? startupTimeout = null)
    {
        _capabilities = capabilities ??
            throw new ArgumentNullException(nameof(capabilities));
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _workerBootId = workerBootId ??
            (() => new WorkerBootId(Guid.NewGuid()));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _startupTimeout = startupTimeout ?? DefaultStartupTimeout;
        if (_startupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
    }

    internal async ValueTask<PrivateHostWorkerSlot> CreateAsync(
        RecoveryBinding binding,
        WorkerGenerationHighWatermark generationHighWatermark,
        Func<WorkerEnvelope, CancellationToken, ValueTask>? onEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(generationHighWatermark);
        cancellationToken.ThrowIfCancellationRequested();

        var deadline = _utcNow().Add(_startupTimeout);
        var deadlineMilliseconds = deadline.ToUnixTimeMilliseconds();
        var capability = await _capabilities.RequestAsync(
                binding,
                generationHighWatermark,
                deadlineMilliseconds,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var bootId = _workerBootId() ??
            throw new InvalidOperationException(
                "Worker boot ID source returned null.");
        var consumed = capability.Consume();
        var identity = new GuardianHostWorkerIdentity(
            bootId,
            consumed.WorkerGeneration);

        IWorkerProcessClient? process = null;
        try
        {
            process = await _launch.LaunchAsync(
                    binding,
                    identity,
                    deadline,
                    onEvent,
                    cancellationToken)
                .ConfigureAwait(false) ??
                throw new InvalidOperationException(
                    "Worker launch authority returned no process client.");
            if (process.WorkerBootId != identity.BootId.Value ||
                process.Generation != identity.Generation.Value)
            {
                throw new InvalidDataException(
                    "Worker launch authority returned a mismatched identity.");
            }

            var slot = new PrivateHostWorkerSlot(binding, identity, process);
            process = null;
            return slot;
        }
        finally
        {
            if (process is not null)
                await process.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Production OS launch composition. The already-verified private apphost is
/// reused as the worker entry; Unix uses its matched sibling containment
/// broker and Windows uses creation-time Job Object assignment.
/// </summary>
internal sealed class ProductionPrivateHostWorkerLaunchAuthority :
    IPrivateHostWorkerLaunchAuthority
{
    private readonly PrivateHostServerIdentity _host;
    private readonly IPrivateHostControlEventSink _control;
    private readonly IWorkerProcessClientFactory _clients;
    private readonly string _executablePath;
    private readonly string? _unixBrokerPath;
    private readonly string _workingDirectory;
    private readonly KeyValuePair<string, string>[] _environment;

    internal ProductionPrivateHostWorkerLaunchAuthority(
        PrivateHostServerIdentity host,
        IPrivateHostControlEventSink control,
        IWorkerProcessClientFactory? clients = null,
        string? executablePath = null,
        string? unixBrokerPath = null,
        string? workingDirectory = null,
        IEnumerable<KeyValuePair<string, string>>? environment = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _clients = clients ?? WorkerProcessClientFactory.Instance;
        _executablePath = ResolveExecutablePath(executablePath);
        _workingDirectory = Path.GetFullPath(
            workingDirectory ?? Environment.CurrentDirectory);
        _environment = FreezeEnvironment(
            environment ?? CaptureEnvironment());
        _unixBrokerPath = OperatingSystem.IsWindows()
            ? null
            : Path.GetFullPath(unixBrokerPath ?? Path.Combine(
                Path.GetDirectoryName(_executablePath) ??
                    throw new InvalidOperationException(
                        "Private host executable has no parent directory."),
                "PtkContainmentBroker"));
    }

    public Task<IWorkerProcessClient> LaunchAsync(
        RecoveryBinding binding,
        GuardianHostWorkerIdentity workerIdentity,
        DateTimeOffset deadlineUtc,
        Func<WorkerEnvelope, CancellationToken, ValueTask>? onEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(workerIdentity);
        var command = new WorkerLaunchCommand(
            _executablePath,
            ["--worker"],
            _workingDirectory,
            _environment,
            workerIdentity.BootId.Value);
        IWorkerProcessLauncher launcher = OperatingSystem.IsWindows()
            ? new WindowsWorkerProcessLauncher()
            : new UnixWorkerProcessLauncher(
                _unixBrokerPath!,
                new PrivateHostUnixWorkerContainmentRegistry(
                    _host,
                    binding.Alias,
                    binding.TransitionVersion,
                    workerIdentity,
                    _control));
        return _clients.LaunchAsync(
            launcher,
            command,
            workerIdentity.Generation.Value,
            deadlineUtc,
            onEvent,
            cancellationToken);
    }

    private static string ResolveExecutablePath(string? executablePath)
    {
        var selected = executablePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(selected))
        {
            throw new InvalidOperationException(
                "Private host executable path is unavailable.");
        }
        return Path.GetFullPath(selected);
    }

    private static IEnumerable<KeyValuePair<string, string>> CaptureEnvironment()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || entry.Value is not string value)
            {
                throw new InvalidOperationException(
                    "Private host environment is not textual.");
            }
            if (WorkerBootstrapEnvironment.ReservedVariables.Contains(key) ||
                PrivateHostBootstrapEnvironment.IsReserved(key))
            {
                continue;
            }
            yield return new KeyValuePair<string, string>(key, value);
        }
    }

    private static KeyValuePair<string, string>[] FreezeEnvironment(
        IEnumerable<KeyValuePair<string, string>> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var frozen = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in environment)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Value is null ||
                WorkerBootstrapEnvironment.ReservedVariables.Contains(pair.Key) ||
                PrivateHostBootstrapEnvironment.IsReserved(pair.Key) ||
                !frozen.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    "Worker parent environment is invalid or reserved.",
                    nameof(environment));
            }
        }
        return frozen.ToArray();
    }
}
