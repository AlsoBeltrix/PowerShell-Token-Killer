using System.Diagnostics;

namespace PtkRtkTestFixture;

/// <summary>Assembly marker used by the test suite to locate the fixture's
/// native apphost and its managed runtime files.</summary>
public static class FixtureMarker
{
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The RTK native fixture is Windows-only.");
            return 126;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            Console.Error.WriteLine("The RTK native fixture could not resolve its apphost path.");
            return 126;
        }

        var sidecar = Path.ChangeExtension(executable, ".cmd");
        var commandProcessor = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        if (!File.Exists(sidecar) || !File.Exists(commandProcessor))
        {
            Console.Error.WriteLine("The RTK native fixture sidecar is unavailable.");
            return 126;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = commandProcessor,
            Arguments = "/d /s /c " + BuildCommand(sidecar, args),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return 126;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            Console.Error.WriteLine("The RTK native fixture sidecar failed to run.");
            return 126;
        }
    }

    private static string BuildCommand(string sidecar, IReadOnlyList<string> args)
    {
        var tokens = new string[args.Count + 1];
        tokens[0] = QuoteForCommandProcessor(sidecar);
        for (var index = 0; index < args.Count; index++)
            tokens[index + 1] = QuoteForCommandProcessor(args[index]);

        // /s /c strips the outer pair while retaining the quotes around a
        // sidecar path that may contain spaces.
        return "\"" + string.Join(' ', tokens) + "\"";
    }

    private static string QuoteForCommandProcessor(string value)
    {
        if (value.Length == 0) return "\"\"";
        var escaped = value
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("\"", "\"\"", StringComparison.Ordinal);
        return escaped.Any(character => char.IsWhiteSpace(character) ||
                                        character is '&' or '|' or '<' or '>' or '^')
            ? "\"" + escaped + "\""
            : escaped;
    }
}
