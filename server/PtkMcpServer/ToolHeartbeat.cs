using ModelContextProtocol;

namespace PtkMcpServer;

/// <summary>
/// Keeps a long tool call visibly alive on the MCP channel (GitHub #44).
///
/// A client that supplies a progressToken watches for progress
/// notifications and reaps a call that stays silent past its own idle
/// timeout — observed live: a 40-minute <c>ptk_invoke</c> with a valid
/// 3600-second budget was aborted by the client at its 1800-second idle
/// default, while a 17-minute call of the same shape survived. That
/// silently caps the timeoutSeconds contract at whatever idle window the
/// client ships, and the abandoned call then keeps the worker busy,
/// wedging the session queue. A periodic heartbeat holds the channel open
/// for exactly as long as the call's own budget allows. When the client
/// sent no progressToken the SDK injects a no-op progress and the beats
/// cost nothing.
///
/// The beat reports a monotonically increasing count with no total — a
/// percentage would be a fabricated claim, and the message says only that
/// execution continues.
/// </summary>
internal static class ToolHeartbeat
{
    /// <summary>
    /// Fallback for direct (non-SDK) callers such as tests; the SDK itself
    /// injects its own no-op when the client sent no progressToken.
    /// </summary>
    internal sealed class NoProgress : IProgress<ProgressNotificationValue>
    {
        internal static readonly NoProgress Instance = new();
        public void Report(ProgressNotificationValue value)
        {
        }
    }

    /// <summary>
    /// Mutable only so tests can shrink the interval; production never
    /// writes it. 30 seconds sits far under every idle timeout observed in
    /// the wild while adding negligible traffic to a multi-minute call.
    /// </summary>
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    internal static async Task<T> KeepAliveAsync<T>(
        Task<T> work,
        IProgress<ProgressNotificationValue> progress)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(progress);

        using var stopBeating = new CancellationTokenSource();
        var beats = 0f;
        while (true)
        {
            var delay = Task.Delay(Interval, stopBeating.Token);
            var finished = await Task.WhenAny(work, delay).ConfigureAwait(false);
            if (finished == work)
            {
                stopBeating.Cancel();
                return await work.ConfigureAwait(false);
            }

            beats += 1f;
            progress.Report(new ProgressNotificationValue
            {
                Progress = beats,
                Message = "executing",
            });
        }
    }
}
