using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;

namespace PtkMcpServer;

/// <summary>User-invoked package setup, outside MCP/audit/worker startup.</summary>
internal static class OperatorCli
{
    private const string Usage = """
        ptk version                   Print the exact running build identity
        ptk doctor [--home PATH]       Check the package and RTK dependency
        ptk init --agent NAMES         Register selected agent harnesses
        ptk init --all-agents          Register every detected harness
        ptk uninstall --agent NAMES    Remove selected harness integrations
        ptk uninstall --all-agents     Remove all harness integrations
        ptk serve                     Run the MCP server on stdio (also the default)

        Setup options: --dry-run, --home PATH
        Agent names: claude,codex,grok,agy,kimi (comma separated)
        Package managers own payload installation/removal. Run setup as yourself.
        """;

    // Null preserves the original server argument path. Worker classification
    // must happen before calling this method, including malformed worker args.
    internal static int? Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] == "serve") return null;
        if (args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return 0;
        }
        if (args[0] is "version" or "--version")
        {
            if (args.Length != 1) return Refuse("version takes no options.", error);
            output.WriteLine(PtkVersion.Value);
            return 0;
        }
        if (args[0] is not ("init" or "uninstall" or "doctor")) return null;

        try
        {
            string? home = null, agents = null;
            var allAgents = false;
            var dryRun = false;
            for (var index = 1; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--home" when index + 1 < args.Length && home is null:
                        home = args[++index];
                        break;
                    case "--agent" when index + 1 < args.Length && agents is null:
                        agents = args[++index];
                        break;
                    case "--all-agents" when !allAgents:
                        allAgents = true;
                        break;
                    case "--dry-run" when !dryRun:
                        dryRun = true;
                        break;
                    default:
                        return Refuse($"Unknown, repeated, or incomplete option: {args[index]}", error);
                }
            }

            var doctor = args[0] == "doctor";
            if (doctor && (agents is not null || allAgents || dryRun))
                return Refuse("doctor accepts only --home.", error);
            if (!doctor && ((agents is null) == !allAgents || agents?.Trim().Length == 0))
                return Refuse("Choose --agent <names> or --all-agents explicitly.", error);

            var root = ResolvePackageHome(home, AppContext.BaseDirectory);
            var script = Path.Combine(root, "scripts", "ptk_init.ps1");
            var binary = Path.Combine(root, "bin", OperatingSystem.IsWindows() ? "PtkMcpServer.exe" : "PtkMcpServer");
            var hook = Path.Combine(root, "bin", OperatingSystem.IsWindows() ? "ptk-hook.exe" : "ptk-hook");
            if (!File.Exists(script) || !File.Exists(binary) || !File.Exists(hook))
                throw new IOException($"Incomplete PTK package at {root}; reinstall it with your package manager or installer.");

            var rtk = RtkDependency.ResolveExecutablePath();
            if (doctor)
            {
                output.WriteLine($"PTK: {PtkVersion.Value}");
                output.WriteLine($"Package: {root}");
                output.WriteLine($"Native hook: {hook}");
                output.WriteLine($"RTK: {rtk ?? "missing"}");
            }
            // Removal must remain possible if a dependency has gone missing.
            if (rtk is null && args[0] != "uninstall")
            {
                error.WriteLine(RtkDependency.UnavailableMessage());
                return 78;
            }
            if (doctor) return 0;

            var parameters = new Dictionary<string, object> { ["PtkHome"] = root };
            if (agents is not null) parameters["Agent"] = agents;
            if (allAgents) parameters["AllAgents"] = true;
            if (dryRun) parameters["DryRun"] = true;
            if (args[0] == "uninstall") parameters["Uninstall"] = true;
            return RunRegistration(script, parameters, output, error);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or RuntimeException)
        {
            error.WriteLine($"ptk: {exception.Message}");
            return 1;
        }
    }

    internal static string ResolvePackageHome(string? requested, string executableDirectory)
    {
        if (requested is not null) return Path.GetFullPath(requested);
        var root = Path.GetFullPath(Path.Combine(executableDirectory, ".."));
        var marker = Path.Combine(root, "PACKAGE-HOME");
        if (!File.Exists(marker)) return root;
        // Written by the package manager inside its owned payload. Never read
        // from cwd or the user's config; registrations need a stable opt/current
        // path even when the executing image lives in a versioned directory.
        var stable = File.ReadAllText(marker).Trim();
        if (!Path.IsPathFullyQualified(stable))
            throw new IOException($"PACKAGE-HOME must name an absolute package root: {marker}");
        return Path.GetFullPath(stable);
    }

    internal static int RunRegistration(string script, Dictionary<string, object> parameters,
        TextWriter output, TextWriter error)
    {
        var host = new SetupHost();
        var state = InitialSessionState.CreateDefault();
        if (OperatingSystem.IsWindows()) state.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(host, state);
        if (OperatingSystem.IsWindows()) runspace.ApartmentState = ApartmentState.STA;
        runspace.Open();
        using var powershell = PowerShell.Create();
        powershell.Runspace = runspace;
        powershell.AddCommand(script);
        foreach (var (name, value) in parameters) powershell.AddParameter(name, value);
        powershell.Streams.Information.DataAdded += (_, item) =>
            output.WriteLine(powershell.Streams.Information[item.Index].MessageData);
        powershell.Streams.Warning.DataAdded += (_, item) =>
            error.WriteLine(powershell.Streams.Warning[item.Index]);
        powershell.Streams.Error.DataAdded += (_, item) =>
            error.WriteLine(powershell.Streams.Error[item.Index]);
        foreach (var item in powershell.Invoke()) output.WriteLine(item);
        if (host.ExitCode != 0) return host.ExitCode;
        // A file invoked as a command handles `exit N` at the script boundary:
        // it marks the pipeline failed and sets LASTEXITCODE without calling
        // PSHost.SetShouldExit or adding an ErrorRecord.
        if (powershell.HadErrors && powershell.Streams.Error.Count == 0 &&
            runspace.SessionStateProxy.GetVariable("LASTEXITCODE") is int scriptExit && scriptExit != 0)
            return scriptExit;
        return powershell.HadErrors ? 1 : 0;
    }

    private static int Refuse(string reason, TextWriter error)
    {
        error.WriteLine($"ptk: {reason}");
        error.WriteLine(Usage);
        return 64;
    }

    private sealed class SetupHost : PSHost
    {
        public int ExitCode { get; private set; }
        public override Guid InstanceId { get; } = Guid.NewGuid();
        public override string Name => "ptk setup";
        public override Version Version => new(1, 0);
        public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
        public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;
        // Setup selection is explicit; Write-Host arrives on the information
        // stream. No console host, profiles, or external pwsh process is needed.
        public override PSHostUserInterface UI => null!;
        public override void SetShouldExit(int exitCode) => ExitCode = exitCode;
        public override void EnterNestedPrompt() => throw new NotSupportedException("Setup does not accept interactive prompts.");
        public override void ExitNestedPrompt() => throw new NotSupportedException();
        public override void NotifyBeginApplication() { }
        public override void NotifyEndApplication() { }
    }
}
