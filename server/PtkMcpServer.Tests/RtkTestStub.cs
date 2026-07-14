using System.Security.Cryptography;
using PtkRtkTestFixture;

namespace PtkMcpServer.Tests;

internal static class RtkTestStub
{
    internal static (DirectoryInfo Directory, string Path) Create(
        string body,
        string? parentDirectory = null,
        string? fileName = null)
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
        Write(path, body);
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

    internal static void Write(string path, string body)
    {
        if (OperatingSystem.IsWindows())
        {
            InstallOrMutateWindowsFixture(path, body);
            File.WriteAllText(
                Path.ChangeExtension(path, ".cmd"),
                "@echo off\r\n" + body.Replace("\n", "\r\n") + "\r\n");
            return;
        }

        File.WriteAllText(
            path,
            "#!/bin/sh\n" + body.Replace("%*", "\"$@\"").Replace("exit /b ", "exit ") + "\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

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
