using System.Globalization;
using Microsoft.Extensions.Hosting;

namespace PtkMcpServer;

/// <summary>
/// Optional lifetime backstop: ends the process when no runspace activity has
/// happened for the idle timeout.
/// </summary>
/// <remarks>
/// Registered only when <c>PTK_IDLE_EXIT_SECONDS</c> is set to a positive value,
/// and deliberately off by default. It was once a 4h default meant to reap
/// orphans that the harness failed to kill, but "no tool call for N hours" does
/// not distinguish an orphan from a session sitting open overnight with the
/// client still attached. Killing the latter removes the tool from a live
/// session, and every later reference to it fails the request outright - a worse
/// outcome than the stray process this guards against. The stdio contract
/// already ends the process on client disconnect (stdin EOF), which is the
/// signal that actually means "nobody is there".
/// </remarks>
public sealed class IdleWatchdog(
    TimeSpan idleTimeout,
    Func<DateTimeOffset> lastActivityUtc,
    Action onIdle) : BackgroundService
{
    private static readonly TimeSpan MinPoll = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The configured idle backstop, or <see langword="null"/> when it is
    /// disabled — which is the default. Only a positive, invariantly parsable
    /// value enables it, so unset, empty, zero, negative and unparsable all mean
    /// "no timer" rather than "fall back to some default".
    /// </summary>
    public static TimeSpan? ReadConfiguredTimeout(string? configuredSeconds) =>
        double.TryParse(
            configuredSeconds,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var idleDeadline = lastActivityUtc() + idleTimeout;
            var wait = idleDeadline - DateTimeOffset.UtcNow;
            if (wait <= TimeSpan.Zero)
            {
                onIdle();
                return;
            }
            await Task.Delay(wait < MinPoll ? MinPoll : wait, stoppingToken);
        }
    }
}
