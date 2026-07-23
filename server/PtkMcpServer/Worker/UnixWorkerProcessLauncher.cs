using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace PtkMcpServer.Worker;

internal readonly record struct UnixProcessIdentity(ulong High, ulong Low)
{
    internal bool IsValid => High != 0 || Low != 0;
}

internal sealed record UnixWorkerContainmentIdentity(
    int BrokerProcessId,
    UnixProcessIdentity BrokerIdentity,
    int WorkerProcessId,
    UnixProcessIdentity WorkerIdentity,
    int WorkerProcessGroup);

internal interface IUnixWorkerContainmentRegistry
{
    ValueTask RegisterPendingAsync(
        UnixWorkerContainmentIdentity identity,
        CancellationToken cancellationToken);

    ValueTask RegisterArmedAsync(
        UnixWorkerContainmentIdentity identity,
        CancellationToken cancellationToken);

    ValueTask RemoveAsync(
        UnixWorkerContainmentIdentity identity,
        CancellationToken cancellationToken);
}

internal interface IUnixWorkerNative
{
    int Spawn(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<(int Source, int Target)> descriptorMappings,
        out int processId);

    UnixProcessIdentity QueryIdentity(int processId);
    int GetProcessGroup(int processId);
    Task<int> WaitForExitCodeAsync(int processId);
}

/// <summary>
/// Unix worker launch authority. Managed code starts only the pinned native
/// broker. The broker forks a gated worker inside the host group; this owner
/// obtains outer pending and armed acknowledgements before allowing the group
/// move and worker exec.
/// </summary>
internal sealed class UnixWorkerProcessLauncher : IWorkerProcessLauncher
{
    private static readonly TimeSpan BrokerHandshakeTimeout =
        TimeSpan.FromSeconds(5);

    private readonly string _brokerPath;
    private readonly IUnixWorkerContainmentRegistry _registry;
    private readonly IUnixWorkerNative _native;

    internal UnixWorkerProcessLauncher(
        string brokerPath,
        IUnixWorkerContainmentRegistry registry,
        IUnixWorkerNative? native = null)
    {
        if (string.IsNullOrWhiteSpace(brokerPath) ||
            brokerPath.Contains('\0') ||
            !Path.IsPathFullyQualified(brokerPath))
        {
            throw new ArgumentException(
                "The Unix worker broker path is invalid.",
                nameof(brokerPath));
        }
        _brokerPath = Path.GetFullPath(brokerPath);
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _native = native ?? new UnixWorkerNative();
    }

    public async Task<IWorkerContainedProcess> LaunchAsync(
        WorkerLaunchCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Unix worker containment requires Linux or macOS.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        UnixWorkerPipeSet? pipes = null;
        ContainedUnixWorker? authority = null;
        try
        {
            pipes = new UnixWorkerPipeSet();
            var arguments = new List<string>
            {
                _brokerPath,
                "--broker-v2",
                command.WorkingDirectory,
                command.ExecutablePath,
            };
            arguments.AddRange(command.Arguments);
            var environment = new Dictionary<string, string>(
                command.Environment,
                StringComparer.OrdinalIgnoreCase)
            {
                [WorkerBootstrapEnvironment.RequestHandle] =
                    UnixWorkerBootstrap.RequestDescriptor.ToString(
                        CultureInfo.InvariantCulture),
                [WorkerBootstrapEnvironment.EventHandle] =
                    UnixWorkerBootstrap.EventDescriptor.ToString(
                        CultureInfo.InvariantCulture),
            };
            var spawnError = _native.Spawn(
                _brokerPath,
                arguments,
                environment,
                pipes.DescriptorMappings,
                out var brokerProcessId);
            pipes.DisposeLocalClientCopies();
            if (spawnError != 0 || brokerProcessId <= 0)
            {
                throw new WorkerProcessException(
                    "unix_worker_broker_spawn_failed",
                    new Win32Exception(spawnError));
            }

            authority = new ContainedUnixWorker(
                brokerProcessId,
                pipes,
                _registry,
                _native);
            pipes = null;
            await authority.CompleteHandshakeAsync(
                BrokerHandshakeTimeout,
                cancellationToken).ConfigureAwait(false);
            var result = authority;
            authority = null;
            return result;
        }
        catch
        {
            if (authority is not null)
            {
                try
                {
                    await authority.ContainAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                }
                authority.Dispose();
            }
            throw;
        }
        finally
        {
            pipes?.Dispose();
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class ContainedUnixWorker : IWorkerContainedProcess
    {
        private readonly object _gate = new();
        private readonly int _brokerProcessId;
        private readonly UnixWorkerPipeSet _pipes;
        private readonly IUnixWorkerContainmentRegistry _registry;
        private readonly IUnixWorkerNative _native;
        private readonly Task<int> _brokerExit;

        private UnixWorkerContainmentIdentity? _identity;
        private Task _workerOrBrokerExit = Task.CompletedTask;
        private Task? _containment;
        private bool _ready;
        private int _disposed;

        internal ContainedUnixWorker(
            int brokerProcessId,
            UnixWorkerPipeSet pipes,
            IUnixWorkerContainmentRegistry registry,
            IUnixWorkerNative native)
        {
            if (brokerProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(brokerProcessId));
            _brokerProcessId = brokerProcessId;
            _pipes = pipes ?? throw new ArgumentNullException(nameof(pipes));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _native = native ?? throw new ArgumentNullException(nameof(native));
            _brokerExit = native.WaitForExitCodeAsync(brokerProcessId);
        }

        public int ProcessId => _identity?.WorkerProcessId ??
            throw new InvalidOperationException("Unix worker is not released.");

        public Stream RequestWriter => _pipes.WorkerRequests;
        public Stream EventReader => _pipes.WorkerEvents;
        public Stream StandardOutputReader => _pipes.StandardOutput;
        public Stream StandardErrorReader => _pipes.StandardError;

        internal async Task CompleteHandshakeAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
            var token = linked.Token;

            var hello = await UnixWorkerBrokerProtocolReader.ReadEventAsync(
                _pipes.BrokerEvents,
                token)
                .ConfigureAwait(false);
            RequireKind(hello, UnixWorkerBrokerEventKind.Hello);
            if (hello.BrokerProcessId != _brokerProcessId ||
                hello.WorkerProcessId != 0 ||
                hello.ProcessGroup != 0 ||
                !_native.QueryIdentity(_brokerProcessId)
                    .Equals(hello.BrokerIdentity))
            {
                throw InvalidBrokerEvent();
            }

            await SendCommandAsync(
                UnixWorkerBrokerCommand.Start,
                token).ConfigureAwait(false);
            var gated = await UnixWorkerBrokerProtocolReader.ReadEventAsync(
                _pipes.BrokerEvents,
                token)
                .ConfigureAwait(false);
            RequireKind(gated, UnixWorkerBrokerEventKind.ChildGated);
            var hostGroup = _native.GetProcessGroup(0);
            if (!SameBroker(hello, gated) ||
                gated.WorkerProcessId <= 0 ||
                !gated.WorkerIdentity.IsValid ||
                gated.ProcessGroup != 0 ||
                _native.QueryIdentity(gated.WorkerProcessId) !=
                    gated.WorkerIdentity ||
                _native.GetProcessGroup(gated.WorkerProcessId) != hostGroup)
            {
                throw InvalidBrokerEvent();
            }

            var identity = new UnixWorkerContainmentIdentity(
                gated.BrokerProcessId,
                gated.BrokerIdentity,
                gated.WorkerProcessId,
                gated.WorkerIdentity,
                gated.WorkerProcessId);
            _identity = identity;
            await _registry.RegisterPendingAsync(identity, token)
                .ConfigureAwait(false);
            await SendCommandAsync(
                UnixWorkerBrokerCommand.ArmGroup,
                token).ConfigureAwait(false);

            var armed = await UnixWorkerBrokerProtocolReader.ReadEventAsync(
                _pipes.BrokerEvents,
                token)
                .ConfigureAwait(false);
            RequireKind(armed, UnixWorkerBrokerEventKind.Armed);
            if (!SameIdentity(gated, armed) ||
                armed.ProcessGroup != armed.WorkerProcessId ||
                _native.QueryIdentity(armed.WorkerProcessId) !=
                    armed.WorkerIdentity ||
                _native.GetProcessGroup(armed.WorkerProcessId) !=
                    armed.WorkerProcessId)
            {
                throw InvalidBrokerEvent();
            }
            await _registry.RegisterArmedAsync(identity, token)
                .ConfigureAwait(false);
            await SendCommandAsync(
                UnixWorkerBrokerCommand.Release,
                token).ConfigureAwait(false);

            var released = await UnixWorkerBrokerProtocolReader.ReadEventAsync(
                _pipes.BrokerEvents,
                token)
                .ConfigureAwait(false);
            RequireKind(released, UnixWorkerBrokerEventKind.Released);
            if (released.BrokerProcessId != 0 ||
                released.WorkerProcessId != 0 ||
                released.BrokerIdentity.IsValid ||
                released.WorkerIdentity.IsValid ||
                released.ProcessGroup != 0)
            {
                throw InvalidBrokerEvent();
            }

            lock (_gate)
            {
                _ready = true;
                _workerOrBrokerExit = Task.WhenAny(
                    ObserveWorkerExitAsync(identity),
                    _brokerExit);
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            Task exit;
            lock (_gate)
            {
                if (!_ready)
                    throw new InvalidOperationException("Unix worker is not released.");
                exit = _workerOrBrokerExit;
            }
            return exit.WaitAsync(cancellationToken);
        }

        public Task ContainAsync()
        {
            lock (_gate)
                return _containment ??= ContainCoreAsync();
        }

        private async Task ContainCoreAsync()
        {
            try
            {
                await SendCommandAsync(
                    UnixWorkerBrokerCommand.Shutdown,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
            _pipes.Commands.Dispose();
            _pipes.Liveness.Dispose();

            var exitCode = await _brokerExit.ConfigureAwait(false);
            if (exitCode is not 0 and not 73)
            {
                throw new WorkerProcessException(
                    exitCode == 74
                        ? "unix_worker_containment_unconfirmed"
                        : "unix_worker_broker_failed");
            }

            if (_identity is { } identity)
            {
                await ObserveWorkerExitAsync(identity).ConfigureAwait(false);
                await _registry.RemoveAsync(
                    identity,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task ObserveWorkerExitAsync(
            UnixWorkerContainmentIdentity identity)
        {
            while (true)
            {
                try
                {
                    if (_native.QueryIdentity(identity.WorkerProcessId) !=
                        identity.WorkerIdentity)
                    {
                        return;
                    }
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    return;
                }
                await Task.Delay(25).ConfigureAwait(false);
            }
        }

        private async ValueTask SendCommandAsync(
            UnixWorkerBrokerCommand command,
            CancellationToken cancellationToken)
        {
            var frame = new byte[UnixWorkerBrokerProtocol.HeaderBytes];
            UnixWorkerBrokerProtocol.WriteHeader(
                frame,
                (byte)command,
                payloadLength: 0);
            await _pipes.Commands.WriteAsync(frame, cancellationToken)
                .ConfigureAwait(false);
            await _pipes.Commands.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _pipes.Dispose();
        }

        private static bool SameBroker(
            UnixWorkerBrokerEvent left,
            UnixWorkerBrokerEvent right) =>
            left.BrokerProcessId == right.BrokerProcessId &&
            left.BrokerIdentity == right.BrokerIdentity;

        private static bool SameIdentity(
            UnixWorkerBrokerEvent left,
            UnixWorkerBrokerEvent right) =>
            SameBroker(left, right) &&
            left.WorkerProcessId == right.WorkerProcessId &&
            left.WorkerIdentity == right.WorkerIdentity;

        private static void RequireKind(
            UnixWorkerBrokerEvent value,
            UnixWorkerBrokerEventKind expected)
        {
            if (value.Kind != expected)
                throw InvalidBrokerEvent();
        }

        private static WorkerProcessException InvalidBrokerEvent() =>
            new("unix_worker_broker_protocol_invalid");
    }

    private sealed class UnixWorkerPipeSet : IDisposable
    {
        private readonly AnonymousPipeServerStream[] _streams;
        private int _disposed;

        internal UnixWorkerPipeSet()
        {
            Liveness = Outbound();
            Commands = Outbound();
            BrokerEvents = Inbound();
            WorkerRequests = Outbound();
            WorkerEvents = Inbound();
            StandardOutput = Inbound();
            StandardError = Inbound();
            _streams =
            [
                Liveness,
                Commands,
                BrokerEvents,
                WorkerRequests,
                WorkerEvents,
                StandardOutput,
                StandardError,
            ];
            DescriptorMappings =
            [
                (Descriptor(Liveness), 3),
                (Descriptor(Commands), 4),
                (Descriptor(BrokerEvents), 5),
                (Descriptor(WorkerRequests), 6),
                (Descriptor(WorkerEvents), 7),
                (Descriptor(StandardOutput), 8),
                (Descriptor(StandardError), 9),
            ];
        }

        internal AnonymousPipeServerStream Liveness { get; }
        internal AnonymousPipeServerStream Commands { get; }
        internal AnonymousPipeServerStream BrokerEvents { get; }
        internal AnonymousPipeServerStream WorkerRequests { get; }
        internal AnonymousPipeServerStream WorkerEvents { get; }
        internal AnonymousPipeServerStream StandardOutput { get; }
        internal AnonymousPipeServerStream StandardError { get; }
        internal IReadOnlyList<(int Source, int Target)> DescriptorMappings { get; }

        internal void DisposeLocalClientCopies()
        {
            foreach (var stream in _streams)
            {
                try
                {
                    stream.DisposeLocalCopyOfClientHandle();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            List<Exception>? failures = null;
            foreach (var stream in _streams)
            {
                try
                {
                    stream.Dispose();
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    (failures ??= []).Add(exception);
                }
            }
            if (failures is { Count: 1 })
                throw failures[0];
            if (failures is { Count: > 1 })
                throw new AggregateException("Unix worker pipe cleanup failed.", failures);
        }

        private static AnonymousPipeServerStream Outbound() =>
            new(PipeDirection.Out, HandleInheritability.Inheritable);

        private static AnonymousPipeServerStream Inbound() =>
            new(PipeDirection.In, HandleInheritability.Inheritable);

        private static int Descriptor(AnonymousPipeServerStream stream)
        {
            var value = checked((ulong)(nuint)stream.ClientSafePipeHandle
                .DangerousGetHandle());
            return value is > 0 and <= int.MaxValue
                ? checked((int)value)
                : throw new InvalidOperationException(
                    "Unix worker pipe descriptor is invalid.");
        }
    }
}

internal enum UnixWorkerBrokerCommand : byte
{
    Start = 1,
    ArmGroup = 2,
    Release = 3,
    Shutdown = 4,
}

internal enum UnixWorkerBrokerEventKind : byte
{
    Hello = 1,
    ChildGated = 2,
    Armed = 3,
    Released = 4,
    StartFailed = 5,
}

internal readonly record struct UnixWorkerBrokerEvent(
    UnixWorkerBrokerEventKind Kind,
    int BrokerProcessId,
    UnixProcessIdentity BrokerIdentity,
    int WorkerProcessId,
    UnixProcessIdentity WorkerIdentity,
    int ProcessGroup,
    byte FailureStage,
    int FailureError);

internal static class UnixWorkerBrokerProtocol
{
    internal const int HeaderBytes = 8;
    internal const int MaximumPayloadBytes = 64;
    private const byte Version = 2;

    internal static void WriteHeader(
        Span<byte> destination,
        byte messageType,
        ushort payloadLength)
    {
        if (destination.Length < HeaderBytes)
            throw new ArgumentException("Broker header destination is too short.", nameof(destination));
        destination[0] = (byte)'P';
        destination[1] = (byte)'T';
        destination[2] = (byte)'K';
        destination[3] = (byte)'B';
        destination[4] = Version;
        destination[5] = messageType;
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], payloadLength);
    }

    internal static UnixWorkerBrokerEvent ParseEvent(
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> payload)
    {
        if (header.Length != HeaderBytes ||
            header[0] != (byte)'P' ||
            header[1] != (byte)'T' ||
            header[2] != (byte)'K' ||
            header[3] != (byte)'B' ||
            header[4] != Version ||
            BinaryPrimitives.ReadUInt16BigEndian(header[6..]) != payload.Length)
        {
            throw Invalid();
        }
        var kind = (UnixWorkerBrokerEventKind)header[5];
        if (!Enum.IsDefined(kind))
            throw Invalid();
        var expectedLength = kind switch
        {
            UnixWorkerBrokerEventKind.Hello => 20,
            UnixWorkerBrokerEventKind.ChildGated => 40,
            UnixWorkerBrokerEventKind.Armed => 44,
            UnixWorkerBrokerEventKind.Released => 0,
            UnixWorkerBrokerEventKind.StartFailed => 5,
            _ => -1,
        };
        if (payload.Length != expectedLength)
            throw Invalid();
        if (kind == UnixWorkerBrokerEventKind.Released)
            return new(kind, 0, default, 0, default, 0, 0, 0);
        if (kind == UnixWorkerBrokerEventKind.StartFailed)
        {
            return new(
                kind,
                0,
                default,
                0,
                default,
                0,
                payload[0],
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload[1..])));
        }

        var brokerProcessId = PositiveInt32(payload);
        var brokerIdentity = Identity(payload[4..]);
        if (kind == UnixWorkerBrokerEventKind.Hello)
        {
            return new(
                kind,
                brokerProcessId,
                brokerIdentity,
                0,
                default,
                0,
                0,
                0);
        }
        var workerProcessId = PositiveInt32(payload[20..]);
        var workerIdentity = Identity(payload[24..]);
        var processGroup = kind == UnixWorkerBrokerEventKind.Armed
            ? PositiveInt32(payload[40..])
            : 0;
        return new(
            kind,
            brokerProcessId,
            brokerIdentity,
            workerProcessId,
            workerIdentity,
            processGroup,
            0,
            0);
    }

    private static int PositiveInt32(ReadOnlySpan<byte> source)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(source);
        return value is > 0 and <= int.MaxValue
            ? checked((int)value)
            : throw Invalid();
    }

    private static UnixProcessIdentity Identity(ReadOnlySpan<byte> source)
    {
        var identity = new UnixProcessIdentity(
            BinaryPrimitives.ReadUInt64BigEndian(source),
            BinaryPrimitives.ReadUInt64BigEndian(source[8..]));
        return identity.IsValid ? identity : throw Invalid();
    }

    private static WorkerProcessException Invalid() =>
        new("unix_worker_broker_protocol_invalid");
}

internal sealed class UnixWorkerNative : IUnixWorkerNative
{
    private const int DarwinProcessBsdInfo = 3;
    private const int DarwinProcessBsdInfoBytes = 136;
    private const int DarwinStartSecondsOffset = 120;
    private const int DarwinStartMicrosecondsOffset = 128;
    private const int FileActionsBytes = 512;
    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int DuplicateMinimum = 16;
    private const int GetDescriptorFlags = 1;
    private const int SetDescriptorFlags = 2;
    private const int CloseOnExec = 1;
    private const int Interrupted = 4;

    public int Spawn(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<(int Source, int Target)> descriptorMappings,
        out int processId)
    {
        processId = 0;
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(descriptorMappings);

        var duplicated = new List<(int Source, int Target)>();
        try
        {
            foreach (var mapping in descriptorMappings)
            {
                var descriptor = DuplicateAboveMinimum(mapping.Source);
                if (descriptor < 0)
                    return Marshal.GetLastPInvokeError();
                var flags = Fcntl(descriptor, GetDescriptorFlags, 0);
                if (flags < 0 ||
                    Fcntl(
                        descriptor,
                        SetDescriptorFlags,
                        flags | CloseOnExec) < 0)
                {
                    var failure = Marshal.GetLastPInvokeError();
                    _ = CloseNative(descriptor);
                    return failure;
                }
                duplicated.Add((descriptor, mapping.Target));
            }

            using var argumentArray = new Utf8StringArray(arguments);
            using var environmentArray = new Utf8StringArray(
                environment.Select(pair => $"{pair.Key}={pair.Value}").ToArray());
            var actions = Marshal.AllocHGlobal(FileActionsBytes);
            try
            {
                Marshal.Copy(new byte[FileActionsBytes], 0, actions, FileActionsBytes);
                var result = PosixSpawnFileActionsInit(actions);
                if (result != 0)
                    return result;
                try
                {
                    result = PosixSpawnFileActionsAddOpen(
                        actions,
                        0,
                        "/dev/null",
                        OpenReadOnly,
                        0);
                    if (result != 0)
                        return result;
                    result = PosixSpawnFileActionsAddOpen(
                        actions,
                        1,
                        "/dev/null",
                        OpenWriteOnly,
                        0);
                    if (result != 0)
                        return result;
                    result = PosixSpawnFileActionsAddOpen(
                        actions,
                        2,
                        "/dev/null",
                        OpenWriteOnly,
                        0);
                    if (result != 0)
                        return result;
                    foreach (var mapping in duplicated)
                    {
                        result = PosixSpawnFileActionsAddDup2(
                            actions,
                            mapping.Source,
                            mapping.Target);
                        if (result != 0)
                            return result;
                        result = PosixSpawnFileActionsAddClose(
                            actions,
                            mapping.Source);
                        if (result != 0)
                            return result;
                    }
                    return PosixSpawn(
                        out processId,
                        executable,
                        actions,
                        IntPtr.Zero,
                        argumentArray.Pointer,
                        environmentArray.Pointer);
                }
                finally
                {
                    _ = PosixSpawnFileActionsDestroy(actions);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(actions);
            }
        }
        finally
        {
            foreach (var mapping in duplicated)
                _ = CloseNative(mapping.Source);
        }
    }

    public UnixProcessIdentity QueryIdentity(int processId)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));
        if (OperatingSystem.IsLinux())
            return QueryLinuxIdentity(processId);
        return QueryDarwinIdentity(processId);
    }

    public int GetProcessGroup(int processId)
    {
        var result = GetProcessGroupNative(processId);
        return result >= 0
            ? result
            : throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Unix getpgid failed.");
    }

    public Task<int> WaitForExitCodeAsync(int processId) =>
        Task.Run(() =>
        {
            for (;;)
            {
                var result = WaitPid(processId, out var status, 0);
                if (result == processId)
                {
                    if ((status & 0x7f) == 0)
                        return (status >> 8) & 0xff;
                    return 128 + (status & 0x7f);
                }
                if (result < 0 && Marshal.GetLastPInvokeError() != Interrupted)
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "Unix worker broker waitpid failed.");
                }
            }
        });

    private static UnixProcessIdentity QueryLinuxIdentity(int processId)
    {
        var value = File.ReadAllText(
            $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat");
        var endName = value.LastIndexOf(')');
        if (endName < 0 || endName + 2 >= value.Length)
            throw new InvalidDataException("Linux process identity is invalid.");
        var fields = value[(endName + 2)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 ||
            !ulong.TryParse(
                fields[19],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var startTicks) ||
            startTicks == 0)
        {
            throw new InvalidDataException("Linux process identity is invalid.");
        }
        return new UnixProcessIdentity(0, startTicks);
    }

    private static UnixProcessIdentity QueryDarwinIdentity(int processId)
    {
        var buffer = Marshal.AllocHGlobal(DarwinProcessBsdInfoBytes);
        try
        {
            var received = ProcessPidInfo(
                processId,
                DarwinProcessBsdInfo,
                0,
                buffer,
                DarwinProcessBsdInfoBytes);
            if (received != DarwinProcessBsdInfoBytes)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Darwin process identity query failed.");
            }
            var seconds = unchecked((ulong)Marshal.ReadInt64(
                buffer,
                DarwinStartSecondsOffset));
            var microseconds = unchecked((ulong)Marshal.ReadInt64(
                buffer,
                DarwinStartMicrosecondsOffset));
            var identity = new UnixProcessIdentity(seconds, microseconds);
            return identity.IsValid
                ? identity
                : throw new InvalidDataException(
                    "Darwin process identity is invalid.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int DuplicateAboveMinimum(int source)
    {
        var temporary = new List<int>();
        try
        {
            for (;;)
            {
                var duplicate = Dup(source);
                if (duplicate < 0)
                    return -1;
                if (duplicate >= DuplicateMinimum)
                    return duplicate;
                temporary.Add(duplicate);
            }
        }
        finally
        {
            foreach (var descriptor in temporary)
                _ = CloseNative(descriptor);
        }
    }

    [DllImport("libc", EntryPoint = "posix_spawn", SetLastError = true)]
    private static extern int PosixSpawn(
        out int processId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr fileActions,
        IntPtr attributes,
        IntPtr arguments,
        IntPtr environment);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    private static extern int PosixSpawnFileActionsInit(IntPtr actions);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addopen")]
    private static extern int PosixSpawnFileActionsAddOpen(
        IntPtr actions,
        int descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        int mode);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    private static extern int PosixSpawnFileActionsAddDup2(
        IntPtr actions,
        int source,
        int destination);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addclose")]
    private static extern int PosixSpawnFileActionsAddClose(
        IntPtr actions,
        int descriptor);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    private static extern int PosixSpawnFileActionsDestroy(IntPtr actions);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int descriptor, int command, int argument);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseNative(int descriptor);

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetProcessGroupNative(int processId);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int processId, out int status, int options);

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidinfo", SetLastError = true)]
    private static extern int ProcessPidInfo(
        int processId,
        int flavor,
        ulong argument,
        IntPtr buffer,
        int bufferSize);

    private sealed class Utf8StringArray : IDisposable
    {
        private readonly IntPtr[] _strings;

        internal Utf8StringArray(IReadOnlyList<string> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            _strings = new IntPtr[values.Count];
            Pointer = Marshal.AllocHGlobal((values.Count + 1) * IntPtr.Size);
            try
            {
                for (var index = 0; index < values.Count; index++)
                {
                    var value = values[index];
                    if (value is null || value.Contains('\0'))
                    {
                        throw new ArgumentException(
                            "The native launch vector is invalid.",
                            nameof(values));
                    }
                    _strings[index] = Marshal.StringToCoTaskMemUTF8(value);
                    Marshal.WriteIntPtr(
                        Pointer,
                        index * IntPtr.Size,
                        _strings[index]);
                }
                Marshal.WriteIntPtr(
                    Pointer,
                    values.Count * IntPtr.Size,
                    IntPtr.Zero);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal IntPtr Pointer { get; private set; }

        public void Dispose()
        {
            foreach (var value in _strings)
            {
                if (value != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(value);
            }
            if (Pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
            }
        }
    }
}

internal static class UnixWorkerBrokerProtocolReader
{
    internal static async ValueTask<UnixWorkerBrokerEvent> ReadEventAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[UnixWorkerBrokerProtocol.HeaderBytes];
        await stream.ReadExactlyAsync(header, cancellationToken)
            .ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6));
        if (payloadLength > UnixWorkerBrokerProtocol.MaximumPayloadBytes)
        {
            throw new WorkerProcessException(
                "unix_worker_broker_protocol_invalid");
        }
        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken)
            .ConfigureAwait(false);
        return UnixWorkerBrokerProtocol.ParseEvent(header, payload);
    }
}
