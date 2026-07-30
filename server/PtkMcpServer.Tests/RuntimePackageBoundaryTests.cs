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
    public void Dev_installer_blocks_every_packaged_runtime_process()
    {
        var installer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "dev-install.ps1"));

        Assert.Contains(
            "$ptkRuntimeProcessNames = @('PtkMcpServer', 'PtkWorkerBroker')",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-Process -Name $ptkRuntimeProcessNames",
            installer,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            installer.Split(
                "Assert-PtkRuntimeNotRunning",
                StringSplitOptions.None).Length - 1);
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

    [Fact]
    public void Production_composition_registers_no_idle_lifecycle_service()
    {
        var program = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "server",
            "PtkMcpServer",
            "Program.cs"));

        Assert.Equal(
            1,
            program.Split(
                "AddSingleton<IHostedService>",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "sp => sp.GetRequiredService<SupervisorLifecycle>()",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddHostedService",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IdleWatchdog",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Production_acceptance_kills_only_the_public_supervisor()
    {
        var acceptance = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "server",
            "test-production-acceptance.ps1"));

        Assert.Contains(
            "$hardKillServer.Process.Kill()",
            acceptance,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$hardKillServer.Process.Kill($true)",
            acceptance,
            StringComparison.Ordinal);
        Assert.Contains(
            "Wait-ForProcessExit ($hardKillKnownIds",
            acceptance,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Production_acceptance_excludes_upstream_PowerShell_telemetry()
    {
        var acceptance = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "server",
            "test-production-acceptance.ps1"));

        Assert.Contains(
            "$start.Environment['POWERSHELL_TELEMETRY_OPTOUT'] = '1'",
            acceptance,
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
