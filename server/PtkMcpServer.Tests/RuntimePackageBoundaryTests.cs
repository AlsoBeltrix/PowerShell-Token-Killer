namespace PtkMcpServer.Tests;

public sealed class RuntimePackageBoundaryTests
{
    [Fact]
    public void Dev_installer_packages_only_the_current_supervisor_runtime()
    {
        var installer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "install.ps1"));

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
    public void Ptk_installer_never_installs_or_selects_the_separate_mini_siem()
    {
        var installer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "install.ps1"));

        Assert.DoesNotContain("PtkSiemReceiver", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("PTK_SIEM_CONFIG", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("destinations.json", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("ptk-siem-receiver-", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("New-Service", installer, StringComparison.Ordinal);
        Assert.Contains("ptk-audit-destination.ps1", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Dev_installer_blocks_every_packaged_runtime_process()
    {
        var installer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "install.ps1"));

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
            "install.ps1"));

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
    public void Host_identity_reads_use_the_non_mutating_protection_boundary()
    {
        // cr2-1: a RETAINED host identity must be validated, never repaired.
        // On Windows, VerifyProtectedFile re-applies the owner/DACL, which
        // silently adopted a foreign or over-permissive host.id instead of
        // quarantining it. The factory must use only the non-mutating
        // external boundary; protection application is reserved for files
        // this process creates.
        var factory = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "server",
            "PtkMcpServer",
            "Audit",
            "AuditJournalFactory.cs"));

        Assert.DoesNotContain(
            "SecureAuditStorage.VerifyProtectedFile(",
            factory,
            StringComparison.Ordinal);
        Assert.Contains(
            "SecureAuditStorage.VerifyExternalProtectedFile(",
            factory,
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

        // Exactly five hosted services: the audit runtime gate (registered
        // first — audit startup is durable before session infrastructure;
        // audit-restoration R2), the audit export coordinator (R3, additive and
        // non-gating), the loopback audit web UI and the alert webhook (R4,
        // both incapable of gating execution), and the supervisor lifecycle.
        // Idle lifecycle machinery stays banned below.
        Assert.Equal(
            5,
            program.Split(
                "AddSingleton<IHostedService>",
                StringSplitOptions.None).Length - 1);
        // Export must never sit between audit admission and the supervisor
        // as a startup dependency: it is registered after the gate and owns
        // no admission coupling.
        Assert.Contains(
            "new AuditExportCoordinator(",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "sp => sp.GetRequiredService<AuditRuntimeGate>()",
            program,
            StringComparison.Ordinal);
        Assert.True(
            program.IndexOf(
                "sp => sp.GetRequiredService<AuditRuntimeGate>()",
                StringComparison.Ordinal) <
            program.IndexOf(
                "sp => sp.GetRequiredService<SupervisorLifecycle>()",
                StringComparison.Ordinal));
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
