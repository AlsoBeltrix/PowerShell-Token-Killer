using System.Runtime.InteropServices;

namespace PtkMcpServer.Worker;

/// <summary>
/// Sets FD_CLOEXEC without calling a variadic function through a fixed
/// P/Invoke signature (opr-14).
///
/// libc declares <c>fcntl</c> as <c>int fcntl(int, int, ...)</c>. Declaring it
/// as the fixed <c>Fcntl(int, int, int)</c> happens to work on x86-64 System V,
/// where the first integer arguments land in the same registers either way. It
/// does not work on Apple arm64: Apple's ABI passes variadic arguments on the
/// stack while a fixed third argument goes in a register, so the callee reads
/// a stack slot PTK never wrote. The flags value is then whatever was there,
/// and FD_CLOEXEC may never be set — leaving the worker's duplicated protocol
/// descriptors inheritable by any command child the user runs.
///
/// <c>ioctl(fd, FIOCLEX)</c> takes no variadic argument at all on the path that
/// matters, so there is no ABI mismatch to get wrong. It is a single atomic
/// call rather than the get/modify/set sequence, which also removes the race
/// where a concurrent spawn inherits a descriptor between the two fcntl calls.
/// Both Linux and macOS implement FIOCLEX.
/// </summary>
internal static class UnixCloseOnExec
{
    // FIOCLEX is NOT the same number on both platforms. Darwin encodes
    // direction and size into the request (_IO('f', 1) == 0x20006601); Linux
    // uses a plain magic number (0x5451, include/uapi/asm-generic/ioctls.h).
    // Verified against the xnu and Linux headers, 2026-08-04.
    private const uint DarwinSetCloseOnExec = 0x20006601;
    private const uint LinuxSetCloseOnExec = 0x5451;

    /// <summary>
    /// Marks the descriptor close-on-exec. Returns false with the P/Invoke
    /// error set, so callers keep their existing failure reporting.
    /// </summary>
    internal static bool TrySet(int descriptor)
    {
        if (OperatingSystem.IsMacOS())
            return Ioctl(descriptor, DarwinSetCloseOnExec) == 0;
        if (OperatingSystem.IsLinux())
            return Ioctl(descriptor, LinuxSetCloseOnExec) == 0;
        return false;
    }

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int descriptor, uint request);
}
