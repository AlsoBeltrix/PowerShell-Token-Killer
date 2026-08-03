using System.Management.Automation;

namespace PtkMcpServer.Tests;

public sealed class TrustedPreflightClassifierTests
{
    [Fact]
    public void Snapshot_is_case_insensitive_type_exact_and_cloneable()
    {
        var original = new TrustedCommandSnapshot();
        original.Set("GiT", CommandTypes.All,
            new ResolvedCommand(CommandTypes.Application, "/usr/bin/git"));
        original.Set("missing", CommandTypes.All, null);

        var clone = original.Clone();
        clone.Set("git", CommandTypes.All,
            new ResolvedCommand(CommandTypes.Function, Definition: "shadow"));

        Assert.Equal(CommandTypes.Application, original.Resolve("git", CommandTypes.All)!.CommandType);
        Assert.Equal(CommandTypes.Function, clone.Resolve("GIT", CommandTypes.All)!.CommandType);
        Assert.Null(original.Resolve("git", CommandTypes.Application));
        Assert.Null(original.Resolve("MISSING", CommandTypes.All));
        Assert.Null(original.Resolve("uncaptured", CommandTypes.All));
    }

    [Fact]
    public void Required_names_include_nested_and_error_recovered_commands_once()
    {
        var names = TrustedPreflightClassifier.GetRequiredCommandNames(
            "function f { export X=1 }; EXPORT Y=2; if $true; then");

        Assert.Contains("export", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("then", names, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, names.Count(name => name.Equals("export", StringComparison.OrdinalIgnoreCase)));
    }

    public static TheoryData<string, string> BashDetections => new()
    {
        { "cat <<EOF\nhello\nEOF", "heredoc" },
        { "cat <<'EOF'\nhello\nEOF", "heredoc" },
        { "if [ -f x.txt ]; then echo hi; fi", "if/then" },
        { "[ -f x.txt ]", "test expression" },
        { "[[ -f x.txt ]]", "test expression" },
        { "for i in 1 2 3; do echo $i; done", "do/done" },
        { "greet() { echo hi; }", "function definition" },
        { "diff <(sort a.txt) <(sort b.txt)", "process substitution" },
        { "export FOO=1", "export" },
        { "FOO=bar echo hi", "environment-variable prefix" },
        { "local x=1", "local" },
        { "source ./env.sh", "source" },
        { "set -e", "shell options" },
        { "set -euo pipefail", "shell options" },
        { "echo `date`", "backticks" },
        { "echo `date +%s`", "backticks" },
    };



    public static TheoryData<string> FalsePositiveScripts => new()
    {
        "echo hi && echo there",
        "Get-Date | Out-String",
        "node --version 2>/dev/null",
        "echo $(1+1)",
        "echo 'literal $x'",
        "bash -lc 'echo hi'",
        "bash -lc 'local x=1; export FOO=1'",
        "git commit -m 'set -e belongs in the message'",
        "Set-Variable -Name x -Value 1",
        "set",
        "dotnet test --filter Name=Foo",
        "source $path",
        "Write-Host `n",
        "Write-Host `n `t",
        "echo 'a `date` b'",
        "Write-Output `tColumn` Name",
        "Get-ChildItem C:\\Temp\\",
        "echo a \\\necho b",
    };








    public static TheoryData<string> BlankedOrUnrelatedParseFatalEvidence => new()
    {
        "Write-Output > # cat <<EOF",
        "Write-Output x\"<<EOF\" >",
        "if x\"foo`\"then\"",
        "Write-Output >; Write-Output x\"foo`\n<<EOF\"",
        "Write-Output >; $x = @'\ncat <<EOF\n'@",
        "Write-Output then; if $true",
        "if $true; Write-Output then",
        "function then { 'ok' }; if $true; Get-Date; then",
        "Set-Alias then Write-Output; if $true; Get-Date; then",
        "foo 'bar'() { echo hi; }",
    };



    private static TrustedCommandSnapshot StockCommands()
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set("set", CommandTypes.All,
            new ResolvedCommand(CommandTypes.Alias, Definition: "Set-Variable"));
        return commands;
    }
}
