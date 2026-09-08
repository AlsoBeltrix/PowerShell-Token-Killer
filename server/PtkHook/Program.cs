using System.Diagnostics;
using PtkHook;

// A hook is a decision, never an execution route. No child process is needed.
try
{
    var response = RedirectHook.Decide(Console.In.ReadToEnd(), ServerIsRunning);
    if (response is not null)
        Console.WriteLine(response);
}
catch
{
    // Preserve the original hook's fail-open behavior on its own input/IO errors.
}

static bool ServerIsRunning()
{
    var forced = Environment.GetEnvironmentVariable("PTK_HOOK_LIVENESS");
    if (string.Equals(forced, "up", StringComparison.OrdinalIgnoreCase)) return true;
    if (string.Equals(forced, "down", StringComparison.OrdinalIgnoreCase)) return false;
    try
    {
        var processes = Process.GetProcessesByName("PtkMcpServer");
        foreach (var process in processes) process.Dispose();
        return processes.Length != 0;
    }
    catch { return false; }
}
