namespace PtkMcpServer.Audit;

/// <summary>
/// Guardian-safe terminal capability returned by one admitted audit call.
/// The server may inspect outcome ownership and complete the fallback terminal,
/// but it cannot reach the concrete gate through this boundary.
/// </summary>
internal interface IAuditBoundaryCall
{
    bool AuthorizationPersistenceFailed { get; }

    bool UserExecutionStarted { get; }

    bool TerminalWritten { get; }

    void CompleteFromFilter(string state, long bytesReturned);
}

/// <summary>
/// Guardian-owned construction seam for the one call lifecycle admitted by
/// the runtime gate. The transitional server supplies its typed authorizer;
/// the guardian uses the execution-agnostic lifecycle directly.
/// </summary>
internal interface IAuditCallFactory
{
    AuditCallLifecycle Create(
        AuditJournal journal,
        ScriptEvidenceStoreProvider evidence);
}

internal sealed class GuardianAuditCallFactory : IAuditCallFactory
{
    internal static GuardianAuditCallFactory Instance { get; } = new();

    private GuardianAuditCallFactory()
    {
    }

    public AuditCallLifecycle Create(
        AuditJournal journal,
        ScriptEvidenceStoreProvider evidence) => new(journal, evidence);
}

/// <summary>
/// Guardian-owned audit admission surface used by the outer MCP filter.
/// Concrete server call state remains an implementation detail validated only
/// after admission and before tool dispatch.
/// </summary>
internal interface IAuditAdmissionOwner
{
    AuditHealth Health { get; }

    void Touch();

    bool TryBeginCall(
        AuditCallMetadata metadata,
        string? exactSubmittedScript,
        out IAuditBoundaryCall? call,
        out IDisposable? callLease,
        out string? failureClass);
}
