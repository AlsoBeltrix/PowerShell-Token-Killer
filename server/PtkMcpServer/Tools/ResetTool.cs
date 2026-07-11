using System.ComponentModel;
using ModelContextProtocol.Server;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class ResetTool
{
    [McpServerTool(Name = "ptk_reset")]
    [Description(
        "Recycle the warm runspace to factory state: discards all variables, loaded " +
        "modules, connections, and the current directory, restores environment " +
        "variables to their server-start values (a PATH polluted by test shims comes " +
        "back clean), and kills running background jobs. Use when leaked state is " +
        "corrupting results; ptk_state shows what has drifted.")]
    public static async Task<string> Reset(
        RunspaceHost host,
        JobManager jobs,
        CancellationToken cancellationToken = default,
        AuditCallContextAccessor? auditContext = null)
    {
        var audit = auditContext?.Current;
        if (audit is not null && !audit.AuthorizeControl("reset.requested"))
            return AuditCallContext.NotStartedMessage;

        JobManager.JobResetLease jobReset;
        try
        {
            jobReset = jobs.BeginReset();
        }
        catch
        {
            audit?.RecordControlOutcome(
                "reset.not_started",
                "not_started",
                detailCode: "job_reset_admission_failed",
                terminationCertainty: "not_applicable");
            throw;
        }

        using (jobReset)
        try
        {
            await host.ResetAsync(cancellationToken);
            audit?.RecordControlOutcome(
                jobReset.FailedCount == 0 ? "runspace.recycled" : "reset.partial_effect",
                jobReset.FailedCount == 0 ? "completed" : "partial",
                detailCode: jobReset.FailedCount == 0 ? null : "runspace_recycled_job_kill_failed",
                warmStateLost: true);
        }
        catch
        {
            audit?.RecordControlOutcome(
                jobReset.TerminationRequestedCount > 0 ? "reset.partial_effect" : "reset.outcome_unknown",
                "outcome_unknown",
                detailCode: jobReset.TerminationRequestedCount > 0
                    ? "jobs_killed_runspace_outcome_unknown"
                    : "runspace_outcome_unknown",
                terminationCertainty: "unknown");
            throw;
        }
        if (jobReset.FailedCount > 0)
        {
            return $"Runspace recycled; all warm state cleared and environment restored; " +
                   $"{jobReset.TerminationRequestedCount} background job(s) received a kill request and " +
                   $"{jobReset.FailedCount} kill request(s) failed.";
        }
        return jobReset.TerminationRequestedCount > 0
            ? $"Runspace recycled; all warm state cleared, environment restored, {jobReset.TerminationRequestedCount} background job(s) killed."
            : "Runspace recycled; all warm state cleared and environment restored.";
    }
}
