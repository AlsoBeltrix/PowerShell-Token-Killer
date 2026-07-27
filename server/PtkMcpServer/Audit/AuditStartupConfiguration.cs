namespace PtkMcpServer.Audit;

/// <summary>
/// Resolves the legacy audit root for the separate administration executable.
/// The production MCP server does not load audit configuration.
/// </summary>
internal sealed class AuditStartupConfiguration : IDisposable
{
    internal const string AuditRootEnvironmentVariable = "PTK_AUDIT_ROOT";

    private int _disposed;

    private AuditStartupConfiguration(AuditOptions auditOptions)
    {
        AuditOptions = auditOptions;
    }

    internal AuditOptions AuditOptions { get; }

    internal static AuditStartupConfiguration LoadFromEnvironment() =>
        Load(Environment.GetEnvironmentVariable(AuditRootEnvironmentVariable));

    internal static AuditStartupConfiguration Load(string? configuredAuditRoot)
    {
        var localOptions = string.IsNullOrWhiteSpace(configuredAuditRoot)
            ? AuditOptions.CreateDefault()
            : AuditOptions.Create(Path.GetFullPath(configuredAuditRoot));
        return new AuditStartupConfiguration(localOptions);
    }

    internal static AuditOptions ResolvePermanentBlockOptions(
        AuditOptions localOptions,
        Guid supervisorBootId,
        Guid blockedEventId)
    {
        ArgumentNullException.ThrowIfNull(localOptions);
        var probeOptions = AsAnchored(
            localOptions,
            new string('0', 64));
        var checkpoint = AuditExportCheckpointStore.ReadSnapshot(
            probeOptions,
            supervisorBootId);
        var blocked = checkpoint.BlockedRecord;
        if (blocked is null || blocked.EventId != blockedEventId)
        {
            throw new IOException(
                "The requested legacy audit export block is not present.");
        }
        return AsAnchored(
            localOptions,
            blocked.ExportConfigurationIdentity);
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private static AuditOptions AsAnchored(
        AuditOptions options,
        string exportConfigurationIdentity) =>
        AuditOptions.Create(
            options.RootDirectory,
            AuditProtectionMode.Anchored,
            exportConfigurationIdentity,
            options.MaxRecordBytes,
            options.SegmentBytes,
            options.AggregateBytes,
            options.EmergencyReserveBytes,
            options.RetentionAge,
            options.MaxEvidenceBytes,
            options.EvidenceAggregateBytes,
            options.EvidenceRetentionAge);
}
