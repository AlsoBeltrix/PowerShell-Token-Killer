namespace PtkMcpServer.Tests;

public sealed class OperatorCliTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ptk-cli-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData("init")]
    [InlineData("uninstall")]
    public void Setup_requires_selection_before_loading_or_mutating_a_package(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(64, OperatorCli.Run([command, "--home", root], output, error));
        Assert.Contains("Choose --agent", error.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void Version_works_without_a_package_or_starting_the_MCP_host()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, OperatorCli.Run(["--version"], output, error));
        Assert.Equal(PtkVersion.Value, output.ToString().Trim());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void Bare_and_serve_invocations_continue_to_the_MCP_host()
    {
        Assert.Null(OperatorCli.Run([], TextWriter.Null, TextWriter.Null));
        Assert.Null(OperatorCli.Run(["serve"], TextWriter.Null, TextWriter.Null));
    }

    [Fact]
    public void Setup_runs_in_this_process_passes_values_without_evaluation_and_propagates_exit()
    {
        var script = Path.Combine(root, "setup.ps1");
        File.WriteAllText(script, "param([string]$Agent)\nWrite-Host $PID\nWrite-Output $Agent\nexit 7\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        const string literal = "codex; throw 'must not execute'";
        var exitCode = OperatorCli.RunRegistration(script, new() { ["Agent"] = literal }, output, error);
        Assert.True(exitCode == 7, $"Exit: {exitCode}; stdout: {output}; stderr: {error}");
        Assert.Contains(Environment.ProcessId.ToString(), output.ToString());
        Assert.Contains(literal, output.ToString());
    }

    [Fact]
    public void Setup_reports_nonterminating_errors_as_failure()
    {
        var script = Path.Combine(root, "setup.ps1");
        File.WriteAllText(script, "Write-Error 'setup failed'\n");
        using var error = new StringWriter();
        Assert.Equal(1, OperatorCli.RunRegistration(script, new(), TextWriter.Null, error));
        Assert.Contains("setup failed", error.ToString());
    }

    [Fact]
    public void Package_manager_stable_home_survives_a_versioned_execution_path()
    {
        var current = Path.Combine(root, "current");
        var versioned = Directory.CreateDirectory(Path.Combine(root, "0.3.0")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(versioned, "bin")).FullName;
        File.WriteAllText(Path.Combine(versioned, "PACKAGE-HOME"), current + "\n");
        Assert.Equal(current, OperatorCli.ResolvePackageHome(null, bin));
        Assert.Equal(root, OperatorCli.ResolvePackageHome(root, bin));
        File.WriteAllText(Path.Combine(versioned, "PACKAGE-HOME"), "../current");
        Assert.Throws<IOException>(() => OperatorCli.ResolvePackageHome(null, bin));
    }
}
