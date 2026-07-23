using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Host;

/// <summary>
/// Unix private-host launcher. The managed guardian uses posix_spawn only for
/// the single-threaded native broker; that broker gates fork/exec until it has
/// established PGID == host PID and retains direct-child reap authority.
/// </summary>
internal sealed class UnixPrivateHostProcessLauncher : IPrivateHostProcessLauncher
{
    private static readonly TimeSpan BrokerHandshakeTimeout = TimeSpan.FromSeconds(5);
    private readonly string _brokerPath;

    internal UnixPrivateHostProcessLauncher(string brokerPath)
    {
        if (string.IsNullOrWhiteSpace(brokerPath) ||
            brokerPath.Contains('\0') ||
            !Path.IsPathFullyQualified(brokerPath))
        {
            throw new ArgumentException("The Unix guardian broker path is invalid.", nameof(brokerPath));
        }
        _brokerPath = Path.GetFullPath(brokerPath);
    }

    public PrivateHostProcessLaunchResult Launch(PrivateHostLaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Unix private-host containment requires Linux or macOS.");
        }

        AnonymousPipeServerStream? liveness = null;
        AnonymousPipeServerStream? brokerCommands = null;
        AnonymousPipeServerStream? brokerEvents = null;
        UnixPrivateHostAuthority? authority = null;
        var spawned = false;
        try
        {
            liveness = new AnonymousPipeServerStream(
                PipeDirection.Out,
                HandleInheritability.Inheritable);
            brokerCommands = new AnonymousPipeServerStream(
                PipeDirection.Out,
                HandleInheritability.Inheritable);
            brokerEvents = new AnonymousPipeServerStream(
                PipeDirection.In,
                HandleInheritability.Inheritable);

            var arguments = new[]
            {
                _brokerPath,
                "--broker-v2",
                Descriptor(liveness.ClientSafePipeHandle).ToString(CultureInfo.InvariantCulture),
                Descriptor(brokerCommands.ClientSafePipeHandle).ToString(CultureInfo.InvariantCulture),
                Descriptor(brokerEvents.ClientSafePipeHandle).ToString(CultureInfo.InvariantCulture),
                Descriptor(command.InheritedHandles[0]).ToString(CultureInfo.InvariantCulture),
                Descriptor(command.InheritedHandles[1]).ToString(CultureInfo.InvariantCulture),
                command.WorkingDirectory,
                command.ExecutablePath,
            };
            var spawnError = UnixNative.Spawn(
                _brokerPath,
                arguments,
                command.Environment,
                out var brokerProcessId);
            DisposeClientCopies(liveness, brokerCommands, brokerEvents);
            if (spawnError != 0 || brokerProcessId <= 0)
                return NoChild();
            spawned = true;

            authority = new UnixPrivateHostAuthority(
                brokerProcessId,
                liveness,
                brokerCommands,
                brokerEvents);
            liveness = null;
            brokerCommands = null;
            brokerEvents = null;
            authority.CompleteLaunchHandshake(BrokerHandshakeTimeout);
            var result = Started(authority);
            authority = null;
            return result;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            if (authority is not null)
            {
                authority.AbortBeforeRelease();
                var result = Started(authority);
                authority = null;
                return result;
            }
            if (!spawned)
                return NoChild();
            throw new InvalidOperationException(
                "The Unix private-host launch lost post-spawn ownership.",
                exception);
        }
        finally
        {
            authority?.Dispose();
            DisposeClientCopies(liveness, brokerCommands, brokerEvents);
            liveness?.Dispose();
            brokerCommands?.Dispose();
            brokerEvents?.Dispose();
        }
    }

    private static int Descriptor(Microsoft.Win32.SafeHandles.SafePipeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return Descriptor(unchecked((nuint)handle.DangerousGetHandle()));
    }

    private static int Descriptor(nuint handle)
    {
        var value = checked((ulong)handle);
        if (value is 0 or > int.MaxValue)
            throw new InvalidOperationException("The Unix inherited descriptor is invalid.");
        return checked((int)value);
    }

    private static void DisposeClientCopies(params AnonymousPipeServerStream?[] streams)
    {
        foreach (var stream in streams)
        {
            if (stream is null) continue;
            try
            {
                stream.DisposeLocalCopyOfClientHandle();
            }
            catch (InvalidOperationException)
            {
                // Already transferred or disposed.
            }
        }
    }

    private static PrivateHostProcessLaunchResult Started(IPrivateHostLaunchedProcess process) =>
        new(GuardianHostLaunchOutcome.Started, process);

    private static PrivateHostProcessLaunchResult NoChild() =>
        new(GuardianHostLaunchOutcome.ProvedNoChild, process: null);

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed class UnixPrivateHostAuthority :
        IPrivateHostLaunchedProcess,
        IUnixWorkerContainmentAuthority
    {
        private readonly object _sync = new();
        private readonly int _brokerProcessId;
        private readonly AnonymousPipeServerStream _liveness;
        private readonly AnonymousPipeServerStream _commands;
        private readonly AnonymousPipeServerStream _events;
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _containment = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _brokerExit;
        private readonly Dictionary<ulong, PendingRegistryCommand> _registryCommands = [];

        private int _hostProcessId;
        private ulong _nextRegistryRequestId;
        private bool _eventPumpStarted;
        private bool _containmentStarted;
        private bool _disposed;

        internal UnixPrivateHostAuthority(
            int brokerProcessId,
            AnonymousPipeServerStream liveness,
            AnonymousPipeServerStream commands,
            AnonymousPipeServerStream events)
        {
            if (brokerProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(brokerProcessId));
            _brokerProcessId = brokerProcessId;
            _hostProcessId = brokerProcessId;
            _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
            _commands = commands ?? throw new ArgumentNullException(nameof(commands));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _brokerExit = WaitForBrokerAsync(brokerProcessId);
            _ = ObserveBrokerExitAsync();
        }

        public int ProcessId => Volatile.Read(ref _hostProcessId);

        public Task Exited => _exited.Task;

        public Task ContainmentConfirmed => _containment.Task;

        internal int BrokerProcessId => _brokerProcessId;

        internal void CompleteLaunchHandshake(TimeSpan timeout)
        {
            BrokerEvent ready;
            using (var cancellation = new CancellationTokenSource(timeout))
            {
                ready = ReadEventAsync(_events, cancellation.Token)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            if (ready.Kind != BrokerEventKind.Ready ||
                ready.HostProcessId <= 0 ||
                ready.BrokerProcessId != _brokerProcessId ||
                ready.Value != ready.HostProcessId ||
                ready.RequestId != 0 ||
                ready.HostProcessId == _brokerProcessId)
            {
                throw new InvalidDataException("The Unix guardian broker ready frame is invalid.");
            }

            Volatile.Write(ref _hostProcessId, ready.HostProcessId);
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(UnixPrivateHostAuthority));
                _eventPumpStarted = true;
            }
            _ = PumpEventsAsync();
            if (!SendCommand(BrokerCommandKind.Start))
                AbortBeforeRelease();
        }

        internal void AbortBeforeRelease() => StartContainment();

        public void BeginContainment(GuardianHostContainmentDeadline deadline)
        {
            ArgumentNullException.ThrowIfNull(deadline);
            StartContainment();
        }

        public Task RegisterPendingAsync(
            GuardianHostContainmentIdentity identity,
            CancellationToken cancellationToken) =>
            SendRegistryCommandAsync(
                BrokerCommandKind.RegisterPending,
                BrokerEventKind.RegistryPending,
                identity,
                cancellationToken);

        public Task RegisterArmedAsync(
            GuardianHostContainmentIdentity identity,
            CancellationToken cancellationToken) =>
            SendRegistryCommandAsync(
                BrokerCommandKind.RegisterArmed,
                BrokerEventKind.RegistryArmed,
                identity,
                cancellationToken);

        public Task RemoveAsync(
            GuardianHostContainmentIdentity identity,
            CancellationToken cancellationToken) =>
            SendRegistryCommandAsync(
                BrokerCommandKind.Remove,
                BrokerEventKind.RegistryRemoved,
                identity,
                cancellationToken);

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            StartContainment();
        }

        private void StartContainment()
        {
            lock (_sync)
            {
                if (_containmentStarted) return;
                _containmentStarted = true;
            }
            _ = SendCommand(BrokerCommandKind.Stop);
            _commands.Dispose();
            _liveness.Dispose();
        }

        private bool SendCommand(BrokerCommandKind kind)
        {
            var frame = new byte[BrokerProtocol.CommandBytes];
            WriteCommandHeader(frame, kind, requestId: 0);
            try
            {
                lock (_sync)
                {
                    if (_commands.SafePipeHandle.IsClosed)
                        return false;
                    _commands.Write(frame);
                    _commands.Flush();
                }
                return true;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                return false;
            }
        }

        private async Task SendRegistryCommandAsync(
            BrokerCommandKind commandKind,
            BrokerEventKind expectedEvent,
            GuardianHostContainmentIdentity identity,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(identity);
            cancellationToken.ThrowIfCancellationRequested();
            if (identity.BrokerPid > int.MaxValue ||
                identity.WorkerPid > int.MaxValue ||
                identity.BrokerPid == identity.WorkerPid ||
                identity.BrokerStartIdentityHigh == 0 &&
                    identity.BrokerStartIdentityLow == 0 ||
                identity.WorkerStartIdentityHigh == 0 &&
                    identity.WorkerStartIdentityLow == 0)
            {
                throw new ArgumentException(
                    "The Unix worker containment identity is invalid.",
                    nameof(identity));
            }

            Task completion;
            lock (_sync)
            {
                if (_disposed || _containmentStarted ||
                    _commands.SafePipeHandle.IsClosed)
                {
                    throw new InvalidOperationException(
                        "The Unix guardian broker registry is unavailable.");
                }
                var requestId = checked(++_nextRegistryRequestId);
                var pending = new PendingRegistryCommand(
                    expectedEvent,
                    new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                if (!_registryCommands.TryAdd(requestId, pending))
                {
                    throw new InvalidOperationException(
                        "The Unix guardian broker request ID was reused.");
                }

                var frame = new byte[BrokerProtocol.CommandBytes];
                WriteCommandHeader(frame, commandKind, requestId);
                BinaryPrimitives.WriteUInt32BigEndian(
                    frame.AsSpan(24),
                    identity.BrokerPid);
                BinaryPrimitives.WriteUInt32BigEndian(
                    frame.AsSpan(28),
                    identity.WorkerPid);
                BinaryPrimitives.WriteUInt32BigEndian(
                    frame.AsSpan(32),
                    identity.ProcessGroupId);
                BinaryPrimitives.WriteUInt64BigEndian(
                    frame.AsSpan(40),
                    identity.BrokerStartIdentityHigh);
                BinaryPrimitives.WriteUInt64BigEndian(
                    frame.AsSpan(48),
                    identity.BrokerStartIdentityLow);
                BinaryPrimitives.WriteUInt64BigEndian(
                    frame.AsSpan(56),
                    identity.WorkerStartIdentityHigh);
                BinaryPrimitives.WriteUInt64BigEndian(
                    frame.AsSpan(64),
                    identity.WorkerStartIdentityLow);
                try
                {
                    _commands.Write(frame);
                    _commands.Flush();
                }
                catch (Exception exception) when (exception is
                    IOException or ObjectDisposedException)
                {
                    _registryCommands.Remove(requestId);
                    throw new IOException(
                        "The Unix guardian broker registry write failed.",
                        exception);
                }
                completion = pending.Completion.Task;
            }

            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static void WriteCommandHeader(
            Span<byte> frame,
            BrokerCommandKind kind,
            ulong requestId)
        {
            BinaryPrimitives.WriteUInt32BigEndian(frame, BrokerProtocol.Magic);
            BinaryPrimitives.WriteUInt32BigEndian(
                frame[4..],
                BrokerProtocol.Version);
            BinaryPrimitives.WriteUInt32BigEndian(frame[8..], (uint)kind);
            BinaryPrimitives.WriteUInt64BigEndian(frame[16..], requestId);
        }

        private async Task PumpEventsAsync()
        {
            try
            {
                for (;;)
                {
                    var message = await ReadEventAsync(_events, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (message.BrokerProcessId != _brokerProcessId ||
                        message.HostProcessId != ProcessId)
                    {
                        StartContainment();
                        return;
                    }
                    switch (message.Kind)
                    {
                        case BrokerEventKind.HostExited:
                            if (message.Value != 0 || message.RequestId != 0)
                            {
                                StartContainment();
                                return;
                            }
                            _exited.TrySetResult();
                            break;
                        case BrokerEventKind.ContainmentConfirmed:
                            if (message.Value != 0 || message.RequestId != 0)
                            {
                                StartContainment();
                                return;
                            }
                            _exited.TrySetResult();
                            _containment.TrySetResult();
                            return;
                        case BrokerEventKind.ContainmentFailed:
                            if (message.Value != 0 || message.RequestId != 0)
                            {
                                StartContainment();
                                return;
                            }
                            _exited.TrySetResult();
                            return;
                        case BrokerEventKind.RegistryPending:
                        case BrokerEventKind.RegistryArmed:
                        case BrokerEventKind.RegistryRemoved:
                            if (message.Value != 0 ||
                                !CompleteRegistryCommand(message))
                            {
                                StartContainment();
                                return;
                            }
                            break;
                        case BrokerEventKind.RegistryRejected:
                            if (message.Value == 0 ||
                                !RejectRegistryCommand(message))
                            {
                                StartContainment();
                                return;
                            }
                            StartContainment();
                            break;
                        default:
                            StartContainment();
                            return;
                    }
                }
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                StartContainment();
                _exited.TrySetResult();
            }
            finally
            {
                FailRegistryCommands();
                _events.Dispose();
            }
        }

        private bool CompleteRegistryCommand(BrokerEvent message)
        {
            PendingRegistryCommand pending;
            lock (_sync)
            {
                if (message.RequestId == 0 ||
                    !_registryCommands.Remove(message.RequestId, out pending!))
                {
                    return false;
                }
            }
            if (pending.ExpectedEvent != message.Kind)
            {
                pending.Completion.TrySetException(new InvalidDataException(
                    "The Unix guardian broker registry acknowledgment is invalid."));
                return false;
            }
            pending.Completion.TrySetResult();
            return true;
        }

        private bool RejectRegistryCommand(BrokerEvent message)
        {
            PendingRegistryCommand pending;
            lock (_sync)
            {
                if (message.RequestId == 0 ||
                    !_registryCommands.Remove(message.RequestId, out pending!))
                {
                    return false;
                }
            }
            pending.Completion.TrySetException(new InvalidOperationException(
                "The Unix guardian broker rejected a containment registry transition."));
            return true;
        }

        private void FailRegistryCommands()
        {
            PendingRegistryCommand[] pending;
            lock (_sync)
            {
                pending = _registryCommands.Values.ToArray();
                _registryCommands.Clear();
            }
            foreach (var command in pending)
            {
                command.Completion.TrySetException(new IOException(
                    "The Unix guardian broker registry stopped."));
            }
        }

        private async Task ObserveBrokerExitAsync()
        {
            await _brokerExit.ConfigureAwait(false);
            _exited.TrySetResult();
            bool eventPumpStarted;
            lock (_sync)
                eventPumpStarted = _eventPumpStarted;
            if (!eventPumpStarted)
            {
                try
                {
                    _events.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private static async Task WaitForBrokerAsync(int brokerProcessId)
        {
            await Task.Run(() =>
            {
                for (;;)
                {
                    var result = UnixNative.WaitPid(brokerProcessId, out _, 0);
                    if (result == brokerProcessId || result < 0 &&
                        Marshal.GetLastPInvokeError() != UnixNative.Interrupted)
                    {
                        return;
                    }
                }
            }).ConfigureAwait(false);
        }
    }

    private enum BrokerCommandKind : uint
    {
        Start = 1,
        Stop = 2,
        RegisterPending = 3,
        RegisterArmed = 4,
        Remove = 5,
    }

    private enum BrokerEventKind : uint
    {
        Ready = 1,
        HostExited = 2,
        ContainmentConfirmed = 3,
        ContainmentFailed = 4,
        RegistryPending = 5,
        RegistryArmed = 6,
        RegistryRemoved = 7,
        RegistryRejected = 8,
    }

    private readonly record struct BrokerEvent(
        BrokerEventKind Kind,
        int HostProcessId,
        int BrokerProcessId,
        int Value,
        ulong RequestId);

    private sealed record PendingRegistryCommand(
        BrokerEventKind ExpectedEvent,
        TaskCompletionSource Completion);

    private static class BrokerProtocol
    {
        internal const uint Magic = 0x50544b42;
        internal const uint Version = 2;
        internal const int EventBytes = 48;
        internal const int CommandBytes = 80;
    }

    private static async ValueTask<BrokerEvent> ReadEventAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var frame = new byte[BrokerProtocol.EventBytes];
        await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(frame) != BrokerProtocol.Magic ||
            BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4)) != BrokerProtocol.Version ||
            BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(12)) != 0 ||
            BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(28)) != 0 ||
            BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(40)) != 0)
        {
            throw new InvalidDataException("The Unix guardian broker event frame is invalid.");
        }
        var kind = (BrokerEventKind)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8));
        if (!Enum.IsDefined(kind))
            throw new InvalidDataException("The Unix guardian broker event kind is invalid.");
        return new BrokerEvent(
            kind,
            checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(16))),
            checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(20))),
            checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(24))),
            BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(32)));
    }

    private static class UnixNative
    {
        internal const int Interrupted = 4;
        private const int FileActionsBytes = 512;
        private const int OpenReadOnly = 0;
        private const int OpenWriteOnly = 1;

        internal static int Spawn(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment,
            out int processId)
        {
            processId = 0;
            using var argumentArray = new Utf8StringArray(arguments);
            using var environmentArray = new Utf8StringArray(
                environment.Select(pair => $"{pair.Key}={pair.Value}").ToArray());
            var actions = Marshal.AllocHGlobal(FileActionsBytes);
            try
            {
                Marshal.Copy(new byte[FileActionsBytes], 0, actions, FileActionsBytes);
                var result = PosixSpawnFileActionsInit(actions);
                if (result != 0) return result;
                try
                {
                    result = PosixSpawnFileActionsAddOpen(
                        actions,
                        0,
                        "/dev/null",
                        OpenReadOnly,
                        0);
                    if (result != 0) return result;
                    result = PosixSpawnFileActionsAddOpen(
                        actions,
                        1,
                        "/dev/null",
                        OpenWriteOnly,
                        0);
                    if (result != 0) return result;
                    result = PosixSpawnFileActionsAddOpen(
                        actions,
                        2,
                        "/dev/null",
                        OpenWriteOnly,
                        0);
                    if (result != 0) return result;
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

        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
        private static extern int PosixSpawnFileActionsDestroy(IntPtr actions);

        [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
        internal static extern int WaitPid(int processId, out int status, int options);
    }

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
                        throw new ArgumentException("The native launch vector is invalid.", nameof(values));
                    _strings[index] = Marshal.StringToCoTaskMemUTF8(value);
                    Marshal.WriteIntPtr(Pointer, index * IntPtr.Size, _strings[index]);
                }
                Marshal.WriteIntPtr(Pointer, values.Count * IntPtr.Size, IntPtr.Zero);
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
