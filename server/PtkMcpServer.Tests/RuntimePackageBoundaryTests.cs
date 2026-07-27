namespace PtkMcpServer.Tests;

public sealed class RuntimePackageBoundaryTests
{
    [Fact]
    public void Dev_installer_excludes_legacy_audit_admin_from_runtime_payload()
    {
        var installer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "dev-install.ps1"));

        Assert.DoesNotContain("PtkAuditAdmin", installer, StringComparison.Ordinal);
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
