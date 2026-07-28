namespace PtkMcpServer.Tests;

public sealed class RuntimePackageBoundaryTests
{
    [Fact]
    public void Dev_installer_packages_only_the_current_supervisor_runtime()
    {
        var installer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "dev-install.ps1"));

        Assert.Contains("PtkWorkerBroker", installer, StringComparison.Ordinal);
        Assert.Contains(
            "ptk_install_transaction.psm1",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PtkAuditAdmin", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("guardian", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private host", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dev_installer_contains_registration_failures_inside_the_transaction()
    {
        var installer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "dev-install.ps1"));

        Assert.Contains(
            "Invoke-PtkHarnessInitialization",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Environment]::ProcessPath",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Per-harness initialization failed with exit code",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "& (Join-Path $ptkHome 'scripts' 'ptk_init.ps1')",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "& $init -Uninstall",
            installer,
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
