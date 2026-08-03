using System.Diagnostics;
using System.Text;

namespace PtkMcpServer;

/// <summary>
/// Asks RTK whether a submitted script can be rewritten to route through its
/// per-command filters. RTK owns the decision: PTK submits the exact text and
/// either runs what comes back or runs the original unchanged.
///
/// The upstream contract is <c>rtk hook check --agent ptk &lt;command&gt;</c>
/// (rtk's <c>rewrite_command</c>): exit 0 with the rewritten command on stdout,
/// or a non-zero exit with an explanatory line on stderr. RTK decomposes
/// <c>&amp;&amp;</c>, <c>||</c>, and <c>;</c> and rewrites each segment it
/// recognizes while preserving the others, so the result stays a shell command
/// line that PowerShell 7 runs natively.
///
/// stderr is advisory only (rtk may print a hook-not-installed notice) and is
/// never read for the routing decision.
/// </summary>
internal static class RtkCommandRewriter
{
    /// <summary>
    /// Rewriting is a planning step, not user work: it must not consume a
    /// meaningful share of the call budget. RTK parses text and returns
    /// promptly; anything slower is treated as a decline.
    /// </summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Bounds the accepted rewrite. RTK returns one command line; a payload
    /// beyond this is not a shape PTK will execute.
    /// </summary>
    internal const int MaximumRewriteCharacters = 8 * 1024;

    private static readonly UTF8Encoding Utf8 = new(false, false);

    /// <summary>
    /// Returns the rewritten command when RTK accepted the script, else null.
    /// Never throws for an ordinary decline, a missing binary, a timeout, or a
    /// malformed answer: every one of those means "run the original".
    /// </summary>
    internal static string? TryRewrite(
        RtkExecutableIdentity rtk,
        string script,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rtk);
        ArgumentNullException.ThrowIfNull(script);

        if (!IsRewritableShape(script))
            return null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = rtk.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
            };
            startInfo.ArgumentList.Add("hook");
            startInfo.ArgumentList.Add("check");
            startInfo.ArgumentList.Add("--agent");
            startInfo.ArgumentList.Add("ptk");
            startInfo.ArgumentList.Add(script);
            if (!string.IsNullOrWhiteSpace(workingDirectory) &&
                Path.IsPathFullyQualified(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            // Read stdout before waiting so a chatty child cannot deadlock on a
            // full pipe buffer. stderr is drained and discarded for the same
            // reason, never inspected.
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            if (!process.WaitForExit((int)Budget.TotalMilliseconds))
            {
                TryKill(process);
                return null;
            }

            // WaitForExit(int) can return before the async readers complete.
            if (!Task.WhenAll(stdout, stderr).Wait(Budget))
                return null;
            if (process.ExitCode != 0)
                return null;

            return AcceptRewrite(script, stdout.Result);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            // A rewrite is an optimization. Any failure to obtain one leaves
            // the original script, which is always executable.
            return null;
        }
    }

    /// <summary>
    /// Cheap pre-filter for shapes RTK will not rewrite anyway, so ordinary
    /// PowerShell never pays to start a process. RTK itself declines multi-line
    /// input, heredocs, and arithmetic expansion; matching that here keeps the
    /// common case (a cmdlet pipeline) free of a child process entirely.
    /// </summary>
    private static bool IsRewritableShape(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;
        if (script.Length > MaximumRewriteCharacters)
            return false;
        return !script.Contains('\n') && !script.Contains('\r');
    }

    /// <summary>
    /// Applies the acceptance rules. A rewrite is used only when RTK produced a
    /// single non-empty line that differs from what was submitted.
    /// </summary>
    private static string? AcceptRewrite(string script, string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        var rewritten = stdout.Trim('\r', '\n', ' ', '\t');
        if (rewritten.Length == 0 || rewritten.Length > MaximumRewriteCharacters)
            return null;

        // The submitted text is one line, so a rewrite that introduces one is
        // a different execution shape than the caller asked for.
        if (rewritten.Contains('\n') || rewritten.Contains('\r'))
            return null;

        // An identity rewrite buys nothing and would only add a process hop.
        if (string.Equals(rewritten, script.Trim(), StringComparison.Ordinal))
            return null;

        return rewritten;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The child already exited, or cannot be signalled. Either way the
            // caller falls back to the original script.
        }
    }
}
