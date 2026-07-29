namespace PtkMcpServer.Tests;

[Collection("ProcessEnvironment")]
public sealed class ExternalPowerShellModulePathTests
{
    [Fact]
    public async Task Warm_runspace_discovers_modules_beside_path_pwsh()
    {
        var root = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            $"ptk-external-pwsh-{Guid.NewGuid():N}")).FullName;
        var shellHome = Directory.CreateDirectory(
            Path.Combine(root, "pwsh-home")).FullName;
        var moduleRoot = Directory.CreateDirectory(Path.Combine(
            shellHome,
            "Modules",
            "PtkExternalProbe",
            "1.0.0")).FullName;
        var userModules = Directory.CreateDirectory(
            Path.Combine(root, "user-modules")).FullName;
        File.WriteAllText(
            Path.Combine(
                shellHome,
                OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh"),
            string.Empty);
        File.WriteAllText(
            Path.Combine(moduleRoot, "PtkExternalProbe.psd1"),
            """
            @{
                RootModule = 'PtkExternalProbe.psm1'
                ModuleVersion = '1.0.0'
                FunctionsToExport = @('Get-PtkExternalProbe')
            }
            """);
        File.WriteAllText(
            Path.Combine(moduleRoot, "PtkExternalProbe.psm1"),
            """
            function Get-PtkExternalProbe { 'external-module-ok' }
            Export-ModuleMember -Function Get-PtkExternalProbe
            """);

        var savedPath = Environment.GetEnvironmentVariable("PATH");
        var savedModulePath = Environment.GetEnvironmentVariable("PSModulePath");
        try
        {
            Environment.SetEnvironmentVariable("PATH", shellHome);
            Environment.SetEnvironmentVariable("PSModulePath", userModules);
            using var host = new RunspaceHost(
                callTimeout: TimeSpan.FromSeconds(30));

            var result = await host.InvokeAsync(
                "Import-Module PtkExternalProbe -Force; Get-PtkExternalProbe");
            var modulePath = await host.InvokeAsync("$env:PSModulePath");

            Assert.True(result.Success, result.Output);
            Assert.Contains("external-module-ok", result.Output);
            Assert.True(modulePath.Success, modulePath.Output);
            Assert.Contains(
                Path.Combine(shellHome, "Modules"),
                modulePath.Output,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable("PSModulePath", savedModulePath);
            Directory.Delete(root, recursive: true);
        }
    }
}
