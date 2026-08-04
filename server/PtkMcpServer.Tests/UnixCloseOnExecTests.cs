using System.Runtime.InteropServices;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

/// <summary>
/// opr-14: FD_CLOEXEC was set through a fixed <c>Fcntl(int, int, int)</c>
/// P/Invoke to libc's variadic <c>fcntl</c>. On Apple arm64 the variadic
/// callee reads the third argument from a stack slot the fixed calling
/// convention never writes, so the flag could silently fail to be set and a
/// user's command child could inherit the worker's protocol descriptors.
///
/// These assert the observable outcome — the flag is actually set on the
/// descriptor — rather than which syscall achieved it. They are the guard
/// that matters on macOS arm64 CI (macos-latest is Apple silicon); on Linux
/// they prove the repair did not regress the platform that already worked.
/// </summary>
public sealed class UnixCloseOnExecTests
{
    private const int GetDescriptorFlags = 1; // F_GETFD
    private const int CloseOnExec = 1;        // FD_CLOEXEC

    [Fact]
    public void Close_on_exec_is_actually_set_on_the_descriptor()
    {
        if (OperatingSystem.IsWindows())
            return;

        var descriptor = OpenNullDescriptor();
        try
        {
            var before = Fcntl2(descriptor, GetDescriptorFlags);
            Assert.True(before >= 0, "F_GETFD failed before the call");
            Assert.Equal(0, before & CloseOnExec);

            Assert.True(
                UnixCloseOnExec.TrySet(descriptor),
                $"TrySet failed with errno {Marshal.GetLastPInvokeError()}");

            var after = Fcntl2(descriptor, GetDescriptorFlags);
            Assert.True(after >= 0, "F_GETFD failed after the call");
            Assert.Equal(
                CloseOnExec,
                after & CloseOnExec);
        }
        finally
        {
            _ = CloseNative(descriptor);
        }
    }

    [Fact]
    public void Setting_close_on_exec_on_a_closed_descriptor_reports_failure()
    {
        if (OperatingSystem.IsWindows())
            return;

        var descriptor = OpenNullDescriptor();
        _ = CloseNative(descriptor);

        Assert.False(UnixCloseOnExec.TrySet(descriptor));
    }

    private static int OpenNullDescriptor()
    {
        var descriptor = OpenNative("/dev/null", 0);
        Assert.True(
            descriptor >= 0,
            $"open(/dev/null) failed with errno {Marshal.GetLastPInvokeError()}");
        return descriptor;
    }

    // Two-argument form: F_GETFD takes no variadic argument, so this
    // declaration is ABI-correct everywhere.
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl2(int descriptor, int command);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative(string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseNative(int descriptor);
}
