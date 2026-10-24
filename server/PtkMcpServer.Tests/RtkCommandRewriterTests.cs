namespace PtkMcpServer.Tests;

/// <summary>
/// The rewrite question PTK asks RTK before every routed call. rtk 1.0 began
/// rejecting unknown agent names on <c>hook check --agent ptk</c> with exit 2,
/// which declined every rewrite and silently stopped routing; the documented
/// integration entry point is <c>rtk rewrite &lt;command&gt;</c>.
/// </summary>
public sealed class RtkCommandRewriterTests
{
    /// <summary>
    /// The stub answers only the exact argument shape <c>rewrite "git status"</c>
    /// (anything else exits 9), so a pass also proves the argument contract.
    /// Exit 0 and 3 both carry the rewrite; rtk 1.x answers 3 for an ordinary
    /// rewrite no allow rule covers. Exit 1 (no rewrite) and 2 (deny rule)
    /// decline to the original.
    /// </summary>
    [Theory]
    [InlineData(0, "rtk git status")]
    [InlineData(3, "rtk git status")]
    [InlineData(1, null)]
    [InlineData(2, null)]
    public void Rewrite_exit_code_decides_whether_the_rewrite_is_used(
        int exitCode,
        string? expected)
    {
        var printsRewrite = exitCode is 0 or 3;
        var body = OperatingSystem.IsWindows()
            ? "if /I not \"%~1\"==\"rewrite\" exit /b 9\n" +
              "if not \"%~2\"==\"git status\" exit /b 9\n" +
              "if not \"%~3\"==\"\" exit /b 9\n" +
              (printsRewrite ? "echo rtk git status\n" : "") +
              $"exit /b {exitCode}"
            : "[ \"$#\" -eq 2 ] || exit 9\n" +
              "[ \"$1\" = \"rewrite\" ] || exit 9\n" +
              "[ \"$2\" = \"git status\" ] || exit 9\n" +
              (printsRewrite ? "echo 'rtk git status'\n" : "") +
              $"exit {exitCode}";
        var (directory, path) = RtkTestStub.Create(body, answerRewrite: false);
        try
        {
            var rewritten = RtkCommandRewriter.TryRewrite(
                new RtkExecutableIdentity(path),
                "git status",
                workingDirectory: null,
                CancellationToken.None);

            Assert.Equal(expected, rewritten);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
