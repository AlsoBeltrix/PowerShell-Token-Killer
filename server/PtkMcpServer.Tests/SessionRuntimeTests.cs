using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tests;

public sealed class SessionRuntimeTests
{
    [Fact]
    public async Task Dispose_terminates_owned_jobs_and_runspace()
    {
        var jobsRoot = Path.Combine(
            Path.GetTempPath(),
            "ptk-session-runtime-jobs-" + Guid.NewGuid().ToString("N"));
        var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60));
        var jobs = new JobManager(jobsRoot);
        var runtime = new SessionRuntime(host, jobs, new RawUsageCounter());
        try
        {
            var job = jobs.Start("Start-Sleep -Seconds 300");
            Assert.True(jobs.Snapshot(job.Id)!.Running);

            runtime.Dispose();

            Assert.False(jobs.Snapshot(job.Id)!.Running);
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => host.InvokeAsync("'must not run'"));
        }
        finally
        {
            try { jobs.Dispose(); } catch { }
            host.Dispose();
            try { Directory.Delete(jobsRoot, recursive: true); } catch { }
        }
    }
}
