using System.Runtime.InteropServices;

namespace PtkMcpServer;

/// <summary>
/// Points the process's standard-input HANDLE at the null device after the MCP
/// transport has captured the real stdin stream. Without this, native commands
/// run in the warm runspace inherit the live-but-idle JSON-RPC stdin pipe and
/// any child that reads or waits on stdin (git's MSYS runtime, sort, ssh)
/// blocks until the whole session ends. With it, children read instant EOF.
/// The transport is unaffected: its stream wraps the original handle, captured
/// before the swap.
/// </summary>
internal static class ChildStdinGuard
{
    private const int StdInputHandle = -10;

    // Holds the NUL handle for the process lifetime. A local would be
    // finalized by the GC, closing the handle installed via SetStdHandle and
    // leaving later children a stale stdin handle.
    private static Microsoft.Win32.SafeHandles.SafeFileHandle? _nulHandle;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int nStdHandle, nint handle);

    private const uint HandleFlagInherit = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint hObject, uint dwMask, uint dwFlags);

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    public static void DetachChildStdin()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _nulHandle = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // The handle must be INHERITABLE: SetStdHandle stores its VALUE,
                // and children receive that value - without the inherit flag the
                // handle does not exist in the child's table, so anything that
                // touches stdin (rustup shims duplicate it at startup) fails with
                // "The handle is invalid (os error 6)" instead of reading EOF
                // (v2-feedback plan, slice 0 probe). File.OpenHandle has no
                // inheritable option, so set the flag explicitly.
                SetHandleInformation(_nulHandle.DangerousGetHandle(), HandleFlagInherit, HandleFlagInherit);
                SetStdHandle(StdInputHandle, _nulHandle.DangerousGetHandle());
            }
            else
            {
                var devNull = open("/dev/null", 0 /* O_RDONLY */);
                if (devNull >= 0) dup2(devNull, 0);
            }
        }
        catch
        {
            // Best effort: without the guard the server still works for
            // everything except natives that read stdin.
        }
    }
}
