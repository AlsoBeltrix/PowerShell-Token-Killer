using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PtkMcpServer.Worker;

internal interface IUnixWorkerBootstrapNative
{
    void SetCloseOnExec(int descriptor);
    int DuplicateCloseOnExec(int descriptor);
    FileAccess GetAccess(int descriptor);
    void Close(int descriptor);
    Stream CreateStream(int descriptor, FileAccess access);
}

internal static class WorkerBootstrap
{
    internal static IWorkerBootstrapStreams Open(WorkerBootstrapValues values)
    {
        if (OperatingSystem.IsWindows())
            return WindowsWorkerBootstrap.Open(values);
        return UnixWorkerBootstrap.Open(values);
    }
}

/// <summary>
/// First-action Unix worker bootstrap. The native broker maps the two protocol
/// pipes to fixed descriptors 3 and 4. This boundary marks both close-on-exec,
/// duplicates them into managed ownership, closes the inherited originals,
/// verifies direction, and exposes no descriptor to user descendants.
/// </summary>
internal static class UnixWorkerBootstrap
{
    internal const int RequestDescriptor = 3;
    internal const int EventDescriptor = 4;

    internal static IWorkerBootstrapStreams Open(
        WorkerBootstrapValues values,
        IUnixWorkerBootstrapNative? native = null,
        Func<bool>? isUnix = null)
    {
        if (!(isUnix ?? (() =>
                OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))())
        {
            throw new WorkerBootstrapException("platform_unsupported");
        }
        if (values.RequestHandle is null || values.EventHandle is null)
            throw new WorkerBootstrapException("handle_missing");
        if (!string.Equals(
                values.RequestHandle,
                RequestDescriptor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            !string.Equals(
                values.EventHandle,
                EventDescriptor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new WorkerBootstrapException("handle_invalid");
        }

        native ??= new UnixWorkerBootstrapNative();
        var request = -1;
        var events = -1;
        Stream? requestStream = null;
        Stream? eventStream = null;
        try
        {
            Invoke("handle_invalid", () =>
            {
                native.SetCloseOnExec(RequestDescriptor);
                native.SetCloseOnExec(EventDescriptor);
            });
            request = Invoke(
                "handle_duplication_failed",
                () => native.DuplicateCloseOnExec(RequestDescriptor));
            events = Invoke(
                "handle_duplication_failed",
                () => native.DuplicateCloseOnExec(EventDescriptor));
            Invoke("bootstrap_failure", () =>
            {
                native.Close(RequestDescriptor);
                native.Close(EventDescriptor);
            });

            RequireAccess(native, request, FileAccess.Read);
            RequireAccess(native, events, FileAccess.Write);
            requestStream = Invoke(
                "stream_creation_failed",
                () => native.CreateStream(request, FileAccess.Read));
            request = -1;
            eventStream = Invoke(
                "stream_creation_failed",
                () => native.CreateStream(events, FileAccess.Write));
            events = -1;

            var owner = new UnixBootstrapStreams(requestStream, eventStream);
            requestStream = null;
            eventStream = null;
            return owner;
        }
        finally
        {
            DisposeIgnoringFailure(requestStream);
            DisposeIgnoringFailure(eventStream);
            CloseIgnoringFailure(native, request);
            CloseIgnoringFailure(native, events);
        }
    }

    private static void RequireAccess(
        IUnixWorkerBootstrapNative native,
        int descriptor,
        FileAccess expected)
    {
        var actual = Invoke(
            "handle_invalid",
            () => native.GetAccess(descriptor));
        if (actual != expected)
            throw new WorkerBootstrapException("handle_direction_invalid");
    }

    private static T Invoke<T>(string detailCode, Func<T> action)
    {
        try
        {
            return action() ?? throw new InvalidOperationException(
                "Unix worker bootstrap boundary returned null.");
        }
        catch (WorkerBootstrapException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new WorkerBootstrapException(detailCode, exception);
        }
    }

    private static void Invoke(string detailCode, Action action) =>
        Invoke(
            detailCode,
            () =>
            {
                action();
                return true;
            });

    private static void CloseIgnoringFailure(
        IUnixWorkerBootstrapNative native,
        int descriptor)
    {
        if (descriptor < 0)
            return;
        try
        {
            native.Close(descriptor);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private static void DisposeIgnoringFailure(IDisposable? value)
    {
        try
        {
            value?.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class UnixBootstrapStreams(
        Stream requestStream,
        Stream eventStream) : IWorkerBootstrapStreams
    {
        private Stream? _requestStream = requestStream;
        private Stream? _eventStream = eventStream;

        public Stream RequestStream => Volatile.Read(ref _requestStream) ??
            throw new ObjectDisposedException(nameof(UnixBootstrapStreams));

        public Stream EventStream => Volatile.Read(ref _eventStream) ??
            throw new ObjectDisposedException(nameof(UnixBootstrapStreams));

        public void Dispose()
        {
            var request = Interlocked.Exchange(ref _requestStream, null);
            var events = Interlocked.Exchange(ref _eventStream, null);
            Exception? failure = null;
            try
            {
                request?.Dispose();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                failure = exception;
            }
            try
            {
                events?.Dispose();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                failure ??= exception;
            }
            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

internal sealed class UnixWorkerBootstrapNative : IUnixWorkerBootstrapNative
{
    private const int GetDescriptorFlags = 1;
    private const int SetDescriptorFlags = 2;
    private const int GetStatusFlags = 3;
    private const int CloseOnExec = 1;
    private const int AccessMode = 3;
    private const int ReadOnly = 0;
    private const int WriteOnly = 1;

    public void SetCloseOnExec(int descriptor)
    {
        var flags = Fcntl(descriptor, GetDescriptorFlags, 0);
        if (flags < 0 ||
            Fcntl(descriptor, SetDescriptorFlags, flags | CloseOnExec) < 0)
        {
            throw NativeFailure("fcntl(FD_CLOEXEC)");
        }
    }

    public int DuplicateCloseOnExec(int descriptor)
    {
        var duplicate = Dup(descriptor);
        if (duplicate < 0)
            throw NativeFailure("dup");
        try
        {
            SetCloseOnExec(duplicate);
            return duplicate;
        }
        catch
        {
            _ = CloseNative(duplicate);
            throw;
        }
    }

    public FileAccess GetAccess(int descriptor)
    {
        var flags = Fcntl(descriptor, GetStatusFlags, 0);
        if (flags < 0)
            throw NativeFailure("fcntl(F_GETFL)");
        return (flags & AccessMode) switch
        {
            ReadOnly => FileAccess.Read,
            WriteOnly => FileAccess.Write,
            _ => FileAccess.ReadWrite,
        };
    }

    public void Close(int descriptor)
    {
        if (CloseNative(descriptor) != 0)
            throw NativeFailure("close");
    }

    public Stream CreateStream(int descriptor, FileAccess access)
    {
        var handle = new SafePipeHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            var direction = access switch
            {
                FileAccess.Read => PipeDirection.In,
                FileAccess.Write => PipeDirection.Out,
                _ => throw new ArgumentOutOfRangeException(nameof(access)),
            };
            var stream = new AnonymousPipeClientStream(direction, handle);
            handle = null!;
            return stream;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static Win32Exception NativeFailure(string operation) =>
        new(Marshal.GetLastPInvokeError(), $"Unix worker {operation} failed.");

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int descriptor, int command, int argument);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseNative(int descriptor);
}
