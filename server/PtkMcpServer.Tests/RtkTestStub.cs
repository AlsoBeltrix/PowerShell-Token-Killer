using System.Security.Cryptography;
using PtkRtkTestFixture;

namespace PtkMcpServer.Tests;

internal static class RtkTestStub
{
    internal static (DirectoryInfo Directory, string Path) Create(
        string body,
        string? parentDirectory = null,
        string? fileName = null,
        bool answerRewrite = true)
    {
        var directory = parentDirectory is null
            ? Directory.CreateTempSubdirectory("ptk-rtk-route-")
            : Directory.CreateDirectory(Path.Combine(
                parentDirectory,
                "ptk-rtk-route-" + Guid.NewGuid().ToString("N")));
        var requestedName = fileName ??
            (OperatingSystem.IsWindows() ? "rtk-stub.exe" : "rtk-stub.sh");
        var path = Path.Combine(
            directory.FullName,
            OperatingSystem.IsWindows()
                ? Path.ChangeExtension(requestedName, ".exe")
                : requestedName);
        Write(path, body, answerRewrite);
        return (directory, path);
    }

    internal static (DirectoryInfo Directory, string Path) CreatePassthrough(
        string? parentDirectory = null)
    {
        var fixture = Create(
            OperatingSystem.IsWindows() ? "rem passthrough" : "exec \"$@\"",
            parentDirectory);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(
                Path.ChangeExtension(fixture.Path, ".cmd"),
                FixtureMarker.PassthroughSidecarMarker + "\r\n");
        }
        return fixture;
    }

    // answerRewrite: false leaves the `rewrite` question to the body, for
    // tests of the rewrite contract itself.
    internal static void Write(string path, string body, bool answerRewrite = true)
    {
        if (OperatingSystem.IsWindows())
        {
            InstallOrMutateWindowsFixture(path, body);
            File.WriteAllText(
                Path.ChangeExtension(path, ".cmd"),
                "@echo off\r\n" + (answerRewrite ? WindowsRewritePreamble : "") +
                body.Replace("\n", "\r\n") + "\r\n");
            return;
        }

        File.WriteAllText(
            path,
            "#!/bin/sh\n" + (answerRewrite ? UnixRewritePreamble : "") +
            body.Replace("%*", "\"$@\"").Replace("exit /b ", "exit ") + "\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // Real rtk answers `rewrite <command>` on stdout with the rewritten
    // command, prefixing `rtk ` onto each segment it recognizes, and exits 1
    // when it has no rewrite. Production asks that question before every
    // routed call, so every stub must answer it the same way — otherwise the
    // stub's own argument echo would masquerade as a rewrite. The stub
    // recognizes exactly the single-command shape its callers route, which
    // keeps the answer verifiable against the real binary's behavior. It
    // answers with exit 0; rtk 1.x often answers 3 (rewrite, host asks), which
    // RtkCommandRewriterTests covers directly.
    //
    // The body below the preamble still handles the routed execution itself
    // (`rtk <command> ...`), which is what each test asserts against.
    // `%~2` strips the surrounding quotes cmd keeps on the passed argument;
    // real rtk returns a bare command line, and production rejects a rewrite
    // that does not reduce to the submitted text once `rtk ` prefixes are
    // removed.
    //
    // cmd cannot echo a script containing its own quote characters without
    // truncating it, and a truncated answer is exactly what production must
    // reject. Real rtk likewise declines shapes it cannot rewrite, so the stub
    // declines (exit 1, no stdout) whenever the submitted script contains a
    // quote — matching upstream behavior instead of emitting a malformed
    // rewrite the guard would refuse anyway.
    private const string WindowsRewritePreamble =
        "if /I \"%1\"==\"rewrite\" (\r\n" +
        "  echo.%~2| findstr /C:\"\\\"\" >nul && exit /b 1\r\n" +
        "  echo rtk %~2\r\n" +
        "  exit /b 0\r\n" +
        ")\r\n";

    private const string UnixRewritePreamble =
        "if [ \"$1\" = \"rewrite\" ]; then\n" +
        "  case \"$2\" in *\\\"*) exit 1;; esac\n" +
        "  echo \"rtk $2\"\n" +
        "  exit 0\n" +
        "fi\n";

    private static void InstallOrMutateWindowsFixture(string path, string body)
    {
        if (File.Exists(path))
        {
            // PE loaders permit an overlay after the image. Appending a body
            // digest leaves the native fixture runnable while making a same-path
            // replacement visible to the production identity hash.
            using var executable = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            executable.Write(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));
            return;
        }

        var fixtureAssembly = typeof(FixtureMarker).Assembly.Location;
        var fixtureDirectory = Path.GetDirectoryName(fixtureAssembly)
            ?? throw new InvalidOperationException(
                "RTK fixture assembly directory is unavailable.");
        var fixtureBaseName = Path.GetFileNameWithoutExtension(fixtureAssembly);
        var fixtureAppHost = Path.Combine(fixtureDirectory, fixtureBaseName + ".exe");
        if (!File.Exists(fixtureAppHost))
            throw new FileNotFoundException("RTK fixture apphost is unavailable.", fixtureAppHost);

        File.Copy(fixtureAppHost, path);
        foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var source = Path.Combine(fixtureDirectory, fixtureBaseName + extension);
            if (!File.Exists(source))
                throw new FileNotFoundException("RTK fixture runtime file is unavailable.", source);
            File.Copy(
                source,
                Path.Combine(Path.GetDirectoryName(path)!, fixtureBaseName + extension));
        }
    }
}
