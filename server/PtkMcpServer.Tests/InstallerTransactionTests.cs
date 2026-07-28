using System.Diagnostics;

namespace PtkMcpServer.Tests;

public sealed class InstallerTransactionTests
{
    [Fact]
    public async Task Activation_and_registration_faults_restore_exact_prior_state()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = Path.Combine(
            repositoryRoot,
            "server",
            "test-install-transaction.ps1");
        var start = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);

        using var process = Process.Start(start) ??
            throw new InvalidOperationException(
                "The installer transaction test process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync()
            .WaitAsync(TimeSpan.FromSeconds(45));
        var output = await standardOutput;
        var error = await standardError;

        Assert.True(
            process.ExitCode == 0,
            $"exit={process.ExitCode}{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{output}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{error}");
        Assert.Contains(
            "INSTALL TRANSACTION TEST PASSED",
            output,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            "Repository root not found upward from the test base directory.");
    }
}
