namespace PtkMcpServer.Tests;

public class IdleWatchdogTests
{
    /// <summary>
    /// The backstop is opt-in. A 4h default once killed servers whose sessions
    /// were merely idle overnight, which removed the tool from a live session
    /// and made every later reference fail the request outright. Only an
    /// explicit positive value may arm it; nothing else may reintroduce a
    /// fallback.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("nonsense")]
    [InlineData("14400s")]
    public void Idle_backstop_is_disabled_unless_explicitly_configured(string? configured) =>
        Assert.Null(IdleWatchdog.ReadConfiguredTimeout(configured));

    [Theory]
    [InlineData("60", 60d)]
    [InlineData("0.5", 0.5d)]
    [InlineData("14400", 14400d)]
    public void Explicit_positive_seconds_arm_the_backstop(string configured, double expected) =>
        Assert.Equal(
            TimeSpan.FromSeconds(expected),
            IdleWatchdog.ReadConfiguredTimeout(configured));

    [Fact]
    public async Task Fires_once_the_idle_timeout_elapses()
    {
        var fired = new TaskCompletionSource();
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        using var watchdog = new IdleWatchdog(
            idleTimeout: TimeSpan.FromMilliseconds(100),
            lastActivityUtc: () => stale,
            onIdle: () => fired.TrySetResult());

        await watchdog.StartAsync(CancellationToken.None);

        var completed = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(fired.Task, completed);
    }

    [Fact]
    public async Task Does_not_fire_while_activity_keeps_arriving()
    {
        var fired = false;
        using var watchdog = new IdleWatchdog(
            idleTimeout: TimeSpan.FromMilliseconds(100),
            lastActivityUtc: () => DateTimeOffset.UtcNow,
            onIdle: () => fired = true);

        await watchdog.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        await watchdog.StopAsync(CancellationToken.None);

        Assert.False(fired);
    }

    [Fact]
    public async Task RunspaceHost_invocations_refresh_last_activity()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60));
        var before = host.LastActivityUtc;

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await host.InvokeAsync("'touch'");

        Assert.True(host.LastActivityUtc > before);
    }
}
