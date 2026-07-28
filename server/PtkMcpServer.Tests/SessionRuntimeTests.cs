using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tests;

public sealed class SessionRuntimeTests
{
    [Fact]
    public async Task Dispose_terminates_owned_runspace()
    {
        var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60));
        var runtime = new SessionRuntime(host, new RawUsageCounter());
        try
        {
            runtime.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => host.InvokeAsync("'must not run'"));
        }
        finally
        {
            host.Dispose();
        }
    }
}
