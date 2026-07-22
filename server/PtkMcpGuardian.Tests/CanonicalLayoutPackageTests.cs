using System.Diagnostics;
using System.Runtime.InteropServices;
using PtkMcpGuardian.Package;

namespace PtkMcpGuardian.Tests;

public sealed class CanonicalLayoutPackageTests
{
    [Fact]
    public async Task Windows_layout_is_matched_and_the_packaged_guardian_accepts_public_eof()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var repositoryRoot = FindRepositoryRoot();
        var runtimeIdentifier = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException(
                "The Windows package fixture requires x64 or arm64."),
        };
        var layoutRoot = Path.Combine(
            Path.GetTempPath(),
            $"ptk-canonical-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(layoutRoot);
        try
        {
            var generator = Path.Combine(repositoryRoot, "scripts", "dev-install.ps1");
            var generated = await RunAsync(
                "pwsh",
                repositoryRoot,
                [
                    "-NoProfile",
                    "-File",
                    generator,
                    "-LayoutOnly",
                    "-OutputDir",
                    layoutRoot,
                    "-Rid",
                    runtimeIdentifier,
                    "-Version",
                    "0.2.0-layout-test",
                ],
                closeStandardInput: false,
                TimeSpan.FromMinutes(3));
            Assert.True(
                generated.ExitCode == 0,
                $"Canonical layout generation failed: {Bounded(generated.StandardError)}" +
                Bounded(generated.StandardOutput));

            var package = MatchedPackageLoader.Load(layoutRoot, runtimeIdentifier);
            Assert.Equal(
                Path.Combine(layoutRoot, "bin", "PtkMcpServer.exe"),
                package.HostAppHostPath);
            Assert.Equal(
                [
                    MatchedPackageRole.Version,
                    MatchedPackageRole.AuditAdmin,
                    MatchedPackageRole.GuardianManaged,
                    MatchedPackageRole.GuardianAppHost,
                    MatchedPackageRole.HostManaged,
                    MatchedPackageRole.HostAppHost,
                    MatchedPackageRole.HostRuntime,
                    MatchedPackageRole.SharedContract,
                    MatchedPackageRole.Script,
                    MatchedPackageRole.Module,
                ],
                package.RequiredArtifactPaths.Select(artifact => artifact.Role));

            var guardian = await RunAsync(
                Path.Combine(layoutRoot, "bin", "PtkMcpGuardian.exe"),
                layoutRoot,
                [],
                closeStandardInput: true,
                TimeSpan.FromSeconds(30));
            Assert.Equal(0, guardian.ExitCode);
            Assert.Empty(guardian.StandardOutput);
            Assert.Empty(guardian.StandardError);
        }
        finally
        {
            if (Directory.Exists(layoutRoot))
                Directory.Delete(layoutRoot, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        string workingDirectory,
        IEnumerable<string> arguments,
        bool closeStandardInput,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
        if (closeStandardInput)
            process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"'{fileName}' did not exit within {timeout}.");
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static string Bounded(string value) => value.Length <= 4096
        ? value
        : value[..4096];

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
